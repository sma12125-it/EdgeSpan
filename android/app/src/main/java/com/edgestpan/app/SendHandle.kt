package com.edgestpan.app

import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.Toast
import com.edgestpan.protocol.ScreenEdge

/**
 * Optional pill on the edge shared with the PC. Drag it across that edge to send the app in front to the PC
 * (needs the shell server to know which app that is); tap it to open the app picker.
 */
object SendHandle {
    private val main = Handler(Looper.getMainLooper())
    private var view: View? = null
    private var edge = -1

    fun refresh(context: Context) = main.post { update(context.applicationContext) }

    private fun update(context: Context) {
        val want = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE).getBoolean("sendHandle", false) &&
            AppLink.engine != null && Settings.canDrawOverlays(context)
        val wm = context.getSystemService(WindowManager::class.java) ?: return
        val e = AppLink.engine?.returnEdge ?: ScreenEdge.Left
        if (!want || e != edge) {
            view?.let { runCatching { wm.removeView(it) } }
            view = null
        }
        if (!want || view != null) return
        edge = e
        val d = context.resources.displayMetrics.density
        val glow = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE).getInt("glowColor", Color.parseColor("#3DDC84"))
        val vertical = e == ScreenEdge.Left || e == ScreenEdge.Right
        val w = if (vertical) (10 * d).toInt() else (72 * d).toInt()
        val h = if (vertical) (72 * d).toInt() else (10 * d).toInt()
        val pill = View(context).apply {
            background = GradientDrawable().apply { cornerRadius = 6 * d; setColor((glow and 0x00FFFFFF) or (0xC0 shl 24)) }
        }
        val type = if (Build.VERSION.SDK_INT >= 26) WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY else @Suppress("DEPRECATION") WindowManager.LayoutParams.TYPE_PHONE
        val lp = WindowManager.LayoutParams(w, h, type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS, PixelFormat.TRANSLUCENT)
        lp.gravity = when (e) {
            ScreenEdge.Left -> Gravity.START or Gravity.CENTER_VERTICAL
            ScreenEdge.Right -> Gravity.END or Gravity.CENTER_VERTICAL
            ScreenEdge.Top -> Gravity.TOP or Gravity.CENTER_HORIZONTAL
            else -> Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
        }
        var startX = 0f
        var startY = 0f
        pill.setOnTouchListener { v, ev ->
            when (ev.actionMasked) {
                MotionEvent.ACTION_DOWN -> { startX = ev.rawX; startY = ev.rawY }
                MotionEvent.ACTION_MOVE -> {
                    v.translationX = if (vertical) (ev.rawX - startX) else 0f
                    v.translationY = if (vertical) 0f else (ev.rawY - startY)
                }
                MotionEvent.ACTION_UP -> {
                    val dx = ev.rawX - startX
                    val dy = ev.rawY - startY
                    v.translationX = 0f; v.translationY = 0f
                    val toward = when (e) {
                        ScreenEdge.Left -> -dx
                        ScreenEdge.Right -> dx
                        ScreenEdge.Top -> -dy
                        else -> dy
                    }
                    val moved = kotlin.math.hypot(dx, dy)
                    if (moved < 12 * d) open(context, null)
                    else if (toward > 0 || moved > 48 * d) sendForeground(context)
                }
            }
            true
        }
        runCatching { wm.addView(pill, lp) }.onSuccess { view = pill }.onFailure { Log.w("EdgeSpan", "send handle: $it") }
    }

    private fun open(context: Context, component: String?) {
        val intent = Intent(context, SendToPcActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (component != null) intent.putExtra("component", component).putExtra("title", component.substringBefore('/'))
        context.startActivity(intent)
    }

    private fun sendForeground(context: Context) {
        Thread({
            val component = AppSender.foregroundComponent()
            Log.i("EdgeSpan", "appstream handle drag foreground=$component")
            main.post {
                if (component == null || component.startsWith(context.packageName)) {
                    Toast.makeText(context, AppSender.strings(context)["sendPcPick"] ?: "Pick the app to send.", Toast.LENGTH_SHORT).show()
                    open(context, null)
                } else open(context, component)
            }
        }, "edgestpan-handle").start()
    }
}
