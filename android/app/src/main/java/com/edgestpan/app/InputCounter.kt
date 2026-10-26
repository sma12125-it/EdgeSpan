package com.edgestpan.app

import android.content.Context
import android.graphics.Color
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.WindowManager
import android.widget.TextView

/**
 * Small on-screen count of button and wheel events that actually arrived.
 * Prefers the accessibility overlay, then a draw-over-apps window.
 */
class InputCounter(private val app: Context) {
    private val main = Handler(Looper.getMainLooper())
    private var view: TextView? = null
    private var added = false
    private var host: Context? = null

    fun update(text: String) {
        main.post {
            val next = EdgeAccessibility.instance ?: app
            val overlay = EdgeAccessibility.instance != null || Settings.canDrawOverlays(app)
            if (!overlay) return@post
            if (host !== next) {
                remove()
                host = next
            }
            val wm = next.getSystemService(WindowManager::class.java) ?: return@post
            val label = view ?: TextView(next).also {
                it.setTextColor(Color.WHITE)
                it.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
                val pad = (12 * next.resources.displayMetrics.density).toInt()
                it.setPadding(pad * 2, pad, pad * 2, pad)
                it.setBackgroundColor(0xCC1C1C1C.toInt())
                view = it
            }
            label.text = text
            val type = when {
                EdgeAccessibility.instance != null -> WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY
                Build.VERSION.SDK_INT >= 26 -> WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                else -> WindowManager.LayoutParams.TYPE_PHONE
            }
            val params = WindowManager.LayoutParams(
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.WRAP_CONTENT,
                type,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                    WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                    WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
                PixelFormat.TRANSLUCENT
            )
            params.gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            params.y = (28 * next.resources.displayMetrics.density).toInt()
            if (!added) {
                wm.addView(label, params)
                added = true
            } else {
                wm.updateViewLayout(label, params)
            }
        }
    }

    fun close() {
        main.post { remove() }
    }

    private fun remove() {
        val current = view
        val owner = host
        if (added && current != null && owner != null) {
            val wm = owner.getSystemService(WindowManager::class.java)
            runCatching { wm?.removeView(current) }
        }
        view = null
        added = false
        host = null
    }
}
