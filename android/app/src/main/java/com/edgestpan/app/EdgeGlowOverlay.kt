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
            glow?.animate()?.cancel()
            removeGlow()
            removeWatch()
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
        if (!running) return
        val ctx = context() ?: return
        val wm = ctx.getSystemService(WindowManager::class.java) ?: return
        val view = glow ?: GlowView(ctx).also { glow = it }
        view.edge = edge
        DisplaySize.ensure(ctx)
        val thick = if (Motion.breakthrough(ctx)) (28f * DisplaySize.density).toInt().coerceIn(18, 48) else (4f * DisplaySize.density).toInt().coerceIn(3, 8)
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
        if (Motion.breakthrough(ctx)) view.burst(Motion.reduce(ctx))
        view.invalidate()
    }

    private fun fadeOut() {
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

    fun ripple(edge: Int) {
        this.edge = edge
        running = true
        main.post {
            attach()
            glow?.burst(Motion.reduce(app))
            main.postDelayed({ if (running) hide() }, 360)
        }
    }

    private class GlowView(context: Context) : View(context) {
        var edge = ScreenEdge.Left
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeCap = Paint.Cap.ROUND
            color = Color.argb(220, 61, 220, 132)
        }
        private val spring = Spring1D(0.32, 0.72)
        private var last = 0L
        private var reduce = false
        private val frames = android.view.Choreographer.getInstance()
        private val tick = object : android.view.Choreographer.FrameCallback {
            override fun doFrame(time: Long) {
                try {
                    val dt = if (last == 0L) 0.016 else ((time - last) / 1_000_000_000.0).coerceIn(0.001, 0.05)
                    last = time
                    val done = spring.step(dt, reduce)
                    invalidate()
                    if (!done) frames.postFrameCallback(this)
                    else if (spring.target > 0.5) {
                        spring.retarget(0.0)
                        frames.postFrameCallback(this)
                    }
                } catch (thrown: RuntimeException) {
                    android.util.Log.w("EdgeSpan", "glow", thrown)
                }
            }
        }

        fun burst(reduceMotion: Boolean) {
            try {
                reduce = reduceMotion
                spring.nudge(0.0)
                spring.retarget(1.0)
                last = 0
                frames.postFrameCallback(tick)
            } catch (thrown: RuntimeException) {
                android.util.Log.w("EdgeSpan", "glow", thrown)
            }
        }

        override fun onDraw(canvas: Canvas) {
            val push = if (reduce) 0f else spring.value.toFloat().coerceIn(0f, 1f)
            val solid = Color.argb(200, 61, 220, 132)
            val clear = Color.argb(0, 61, 220, 132)
            paint.shader = when (edge) {
                ScreenEdge.Right -> LinearGradient(width.toFloat(), 0f, 0f, 0f, solid, clear, Shader.TileMode.CLAMP)
                ScreenEdge.Bottom -> LinearGradient(0f, height.toFloat(), 0f, 0f, solid, clear, Shader.TileMode.CLAMP)
                ScreenEdge.Top -> LinearGradient(0f, 0f, 0f, height.toFloat(), solid, clear, Shader.TileMode.CLAMP)
                else -> LinearGradient(0f, 0f, width.toFloat(), 0f, solid, clear, Shader.TileMode.CLAMP)
            }
            canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), paint)
            line.strokeWidth = 3f * resources.displayMetrics.density
            val path = android.graphics.Path()
            val n = 28
            val horizontal = edge == ScreenEdge.Top || edge == ScreenEdge.Bottom
            for (i in 0..n) {
                val t = i / n.toFloat()
                val bow = kotlin.math.sin(t * Math.PI).toFloat() * push * (if (horizontal) height else width) * 0.55f
                val x = if (horizontal) t * width else if (edge == ScreenEdge.Right) width - bow else bow
                val y = if (!horizontal) t * height else if (edge == ScreenEdge.Bottom) height - bow else bow
                if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
            }
            canvas.drawPath(path, line)
            if (push > 0.45f) {
                val ripple = (push - 0.45f) / 0.55f
                line.alpha = ((1f - ripple) * 180).toInt()
                val cx = width / 2f
                val cy = height / 2f
                canvas.drawCircle(cx, cy, (if (horizontal) height else width) * 0.35f * ripple, line)
                line.alpha = 255
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
