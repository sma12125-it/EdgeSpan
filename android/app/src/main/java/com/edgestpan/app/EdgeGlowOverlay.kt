package com.edgestpan.app

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.Shader
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import com.edgestpan.protocol.ElasticLine
import com.edgestpan.protocol.ScreenEdge

/**
 * A thin static green line on the single edge the pointer left.
 * Click-through, so it does not take pointer pixels. A 1px window hears touches outside itself.
 */
class EdgeGlowOverlay(private val app: Context) {
    private val main = Handler(Looper.getMainLooper())
    private var glow: GlowView? = null
    private var watch: WatchView? = null
    private var host: Context? = null
    private var edge = ScreenEdge.Left
    private var running = false
    var onFinger: () -> Unit = {}

    fun show(edge: Int) {
        crossGen++
        this.edge = edge
        running = true
        if (!listening) {
            listening = true
            DisplaySize.listen { if (running) main.post { attach() } }
        }
        main.post { attach() }
    }

    private var listening = false

    fun hide() {
        running = false
        main.post { fadeOut() }
    }

    fun close() {
        running = false
        main.post {
            runCatching { glow?.animate()?.cancel() }
            runCatching { removeGlow() }
            runCatching { removeWatch() }
        }
    }

    private fun context(): Context? {
        val next = EdgeAccessibility.instance ?: app
        if (EdgeAccessibility.instance == null && !Settings.canDrawOverlays(app)) return null
        if (host !== next) {
            removeGlow()
            removeWatch()
            host = next
        }
        return next
    }

    private fun attach() {
        try {
            attachUnsafe()
        } catch (thrown: Throwable) {
            // BadTokenException, SecurityException (overlay permission revoked), a stale accessibility
            // service token, or IllegalStateException from a view already added: drop the glow, keep the session.
            CrashLog.caught(app, "glow attach", thrown)
            runCatching { removeGlow() }
            runCatching { removeWatch() }
            host = null
        }
    }

    private fun attachUnsafe() {
        if (!running) return
        val ctx = context() ?: return
        val wm = ctx.getSystemService(WindowManager::class.java) ?: return
        val view = glow ?: GlowView(ctx).also { glow = it }
        view.edge = edge
        DisplaySize.ensure(ctx)
        val thick = if (Motion.breakthrough(ctx)) (56f * DisplaySize.density).toInt().coerceIn(36, 160) else (4f * DisplaySize.density).toInt().coerceIn(3, 8)
        val screenW = DisplaySize.width
        val screenH = DisplaySize.height
        if (screenW < 1 || screenH < 1) return
        val horizontal = edge == ScreenEdge.Top || edge == ScreenEdge.Bottom
        val params = fullScreenParams(
            if (horizontal) screenW else thick,
            if (horizontal) thick else screenH
        )
        params.gravity = Gravity.TOP or Gravity.START
        params.x = if (edge == ScreenEdge.Right) (screenW - thick).coerceAtLeast(0) else 0
        params.y = if (edge == ScreenEdge.Bottom) (screenH - thick).coerceAtLeast(0) else 0
        if (view.parent == null) {
            view.alpha = 0f
            wm.addView(view, params)
            view.animate().alpha(1f).setDuration(160).start()
        } else {
            view.animate().cancel()
            view.alpha = 1f
            wm.updateViewLayout(view, params)
        }
        ensureWatch(ctx, wm)
        pendingCross?.let { (mode, at) ->
            pendingCross = null
            view.play(mode, at, Motion.reduce(ctx))
        }
        view.invalidate()
    }

    private fun fadeOut() {
        try {
            fadeOutUnsafe()
        } catch (thrown: Throwable) {
            CrashLog.caught(app, "glow fade", thrown)
        }
    }

    private fun fadeOutUnsafe() {
        val view = glow
        if (view == null || view.parent == null) {
            removeGlow()
            return
        }
        view.animate().cancel()
        view.animate().alpha(0f).setDuration(160).withEndAction {
            if (!running) removeGlow()
        }.start()
    }

    private fun ensureWatch(ctx: Context, wm: WindowManager) {
        val view = watch ?: WatchView(ctx) { if (allowFinger()) onFinger() }.also { watch = it }
        if (view.parent != null) return
        val params = WindowManager.LayoutParams(
            1, 1, overlayType(),
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        )
        params.gravity = Gravity.TOP or Gravity.START
        params.x = 0
        params.y = 0
        coverBars(params)
        wm.addView(view, params)
    }

    private fun fullScreenParams(w: Int, h: Int): WindowManager.LayoutParams {
        val params = WindowManager.LayoutParams(
            w, h, overlayType(),
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        )
        coverBars(params)
        return params
    }

    private fun removeGlow() {
        val view = glow ?: return
        val owner = host ?: return
        view.animate().cancel()
        runCatching { owner.getSystemService(WindowManager::class.java)?.removeView(view) }
        glow = null
    }

    private fun removeWatch() {
        val view = watch ?: return
        val owner = host ?: return
        runCatching { owner.getSystemService(WindowManager::class.java)?.removeView(view) }
        watch = null
    }

    private fun overlayType() = when {
        EdgeAccessibility.instance != null -> WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY
        Build.VERSION.SDK_INT >= 26 -> WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        else -> WindowManager.LayoutParams.TYPE_PHONE
    }

    private var pendingCross: Pair<Int, Double>? = null
    private var crossGen = 0

    /**
     * One half of the shared elastic boundary at [along] (0..1 along [edge]).
     * ENTER: stretched inward and torn, then hidden. EXIT: bulges outward and snaps back; [stay] keeps the glow shown.
     */
    fun cross(edge: Int, mode: Int, along: Double, stay: Boolean) {
        this.edge = edge
        running = true
        if (!listening) {
            listening = true
            DisplaySize.listen { if (running) main.post { attach() } }
        }
        main.post {
            try {
                pendingCross = Pair(mode, along)
                attach()
            } catch (thrown: Throwable) {
                CrashLog.caught(app, "glow cross", thrown)
            }
            val gen = ++crossGen
            if (!stay) main.postDelayed({ if (running && gen == crossGen) hide() }, ElasticLine.DurationMs.toLong() + 40)
        }
    }

    fun ripple(edge: Int) = cross(edge, ElasticLine.ENTER, 0.5, false)

    private class GlowView(context: Context) : View(context) {
        var edge = ScreenEdge.Left
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeCap = Paint.Cap.ROUND
        }
        private var mode = ElasticLine.ENTER
        private var along = 0.5
        private var startedAt = 0L
        private var reduce = false
        private val frames = android.view.Choreographer.getInstance()
        private val tick = object : android.view.Choreographer.FrameCallback {
            override fun doFrame(time: Long) {
                try {
                    invalidate()
                    if (elapsed() < ElasticLine.DurationMs) frames.postFrameCallback(this)
                } catch (thrown: RuntimeException) {
                    android.util.Log.w("EdgeSpan", "glow", thrown)
                }
            }
        }

        private fun elapsed(): Double = if (startedAt == 0L) Double.MAX_VALUE else (SystemClock.uptimeMillis() - startedAt).toDouble()

        /** Plays the shared elastic line at [at] (0..1 along the edge). */
        fun play(mode: Int, at: Double, reduceMotion: Boolean) {
            try {
                this.mode = mode
                along = at.coerceIn(0.0, 1.0)
                reduce = reduceMotion
                startedAt = if (reduceMotion) 0L else SystemClock.uptimeMillis()
                frames.removeFrameCallback(tick)
                frames.postFrameCallback(tick)
            } catch (thrown: RuntimeException) {
                android.util.Log.w("EdgeSpan", "glow", thrown)
            }
        }

        override fun onDraw(canvas: Canvas) {
            try {
                drawGlow(canvas)
            } catch (thrown: RuntimeException) {
                android.util.Log.e("EdgeSpan", "glow draw", thrown)
            }
        }

        private fun drawGlow(canvas: Canvas) {
            if (width < 1 || height < 1) return
            val ms = elapsed()
            val crossing = ms < ElasticLine.DurationMs
            val horizontal = edge == ScreenEdge.Top || edge == ScreenEdge.Bottom
            val thick = (if (horizontal) height else width).toFloat()
            val density = resources.displayMetrics.density
            // Only the elastic line (no gradient band). Its baseline is centred on the exact edge pixel
            // (offset 0, no inset); the outer half of the stroke is clipped by the screen edge. On exit only the
            // Gaussian segment at the crossing deforms, outward: drawn as the line thinning there (tip clipped).
            val n = 160
            val long = (if (horizontal) width else height).toFloat()
            fun point(i: Int): Pair<Float, Float> {
                val a = i / n.toDouble()
                val d = if (crossing) (ElasticLine.offset(mode, a, along, ms) * thick).toFloat() else 0f
                val p = (a * long).toFloat()
                return when (edge) {
                    ScreenEdge.Right -> Pair(width - d, p)
                    ScreenEdge.Bottom -> Pair(p, height - d)
                    ScreenEdge.Top -> Pair(p, d)
                    else -> Pair(d, p)
                }
            }
            // Colour chosen on the PC (synced via GlowStyle before each crossing); falls back to the teal accent.
            val user = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE).getInt("glowColor", ElasticLine.Accent)
            val light = Color.rgb(Color.red(user) + (255 - Color.red(user)) * 6 / 10, Color.green(user) + (255 - Color.green(user)) * 6 / 10, Color.blue(user) + (255 - Color.blue(user)) * 6 / 10)
            for (pass in 0..1) {
                line.color = if (pass == 0) user else light
                val base = if (pass == 0) 9f * density else 2.4f * density
                val peak = if (pass == 0) 70 else 255
                var prev = point(0)
                for (i in 1..n) {
                    val next = point(i)
                    val mid = (i - 0.5) / n
                    val op = if (crossing) ElasticLine.opacity(mode, mid, along, ms) else 0.85
                    val w = if (crossing) ElasticLine.thickness(mode, mid, along, ms) else 1.0
                    if (op > 0.01) {
                        line.strokeWidth = (base * w).toFloat()
                        line.alpha = (peak * op).toInt().coerceIn(0, 255)
                        canvas.drawLine(prev.first, prev.second, next.first, next.second, line)
                    }
                    prev = next
                }
            }
        }
    }

    private class WatchView(context: Context, private val finger: () -> Unit) : View(context) {
        override fun onTouchEvent(event: MotionEvent): Boolean {
            if (event.action == MotionEvent.ACTION_OUTSIDE && EdgeGlowOverlay.allowFinger()) finger()
            return false
        }
    }

    companion object {
        @Volatile var ignoreUntil = 0L

        fun ignore(ms: Long = 350) {
            ignoreUntil = SystemClock.elapsedRealtime() + ms
        }

        fun allowFinger(): Boolean = SystemClock.elapsedRealtime() >= ignoreUntil

        @Volatile var serverFinger: () -> Unit = {}

        fun fingerFromServer() {
            if (allowFinger()) serverFinger()
        }

        fun coverBars(params: WindowManager.LayoutParams) {
            if (Build.VERSION.SDK_INT >= 28) {
                params.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
            }
            if (Build.VERSION.SDK_INT >= 30) {
                params.fitInsetsTypes = 0
                params.fitInsetsSides = 0
            }
        }
    }
}
