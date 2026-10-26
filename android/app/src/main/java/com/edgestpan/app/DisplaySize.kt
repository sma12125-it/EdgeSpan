package com.edgestpan.app

import android.app.Activity
import android.content.Context
import android.graphics.Point
import android.hardware.display.DisplayManager
import android.os.Build
import android.view.Display
import android.view.WindowManager
import java.util.concurrent.CopyOnWriteArrayList

/**
 * Physical pixels of the display the pointer travels, in the current rotation.
 * Includes system bars, cutouts, and a DeX desktop. This is not the app window
 * and not the area inside the system insets.
 */
object DisplaySize {
    @Volatile var originX = 0
    @Volatile var originY = 0
    @Volatile var width = 0
    @Volatile var height = 0
    @Volatile var density = 1f
    @Volatile var displayId = Display.DEFAULT_DISPLAY
    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    fun listen(block: () -> Unit) {
        listeners += block
    }

    fun updateFrom(activity: Activity) {
        if (Build.VERSION.SDK_INT >= 30) {
            activity.display?.let { displayId = it.displayId }
        }
        ensure(activity)
    }

    fun ensure(context: Context) {
        val wm = context.getSystemService(WindowManager::class.java) ?: return
        val display = resolve(context, wm) ?: return
        val point = Point()
        @Suppress("DEPRECATION")
        display.getRealSize(point)
        if (point.x < 1 || point.y < 1) return
        val density = context.resources.displayMetrics.density
        apply(0, 0, point.x, point.y, density)
    }

    private fun resolve(context: Context, wm: WindowManager): Display? {
        val manager = context.getSystemService(DisplayManager::class.java)
        manager?.getDisplay(displayId)?.let { return it }
        if (Build.VERSION.SDK_INT >= 30) context.display?.let { return it }
        @Suppress("DEPRECATION")
        return wm.defaultDisplay
    }

    private fun apply(x: Int, y: Int, w: Int, h: Int, d: Float) {
        val nextW = w.coerceAtLeast(1)
        val nextH = h.coerceAtLeast(1)
        val nextD = if (d > 0f) d else 1f
        val changed = x != originX || y != originY || nextW != width || nextH != height || nextD != density
        originX = x
        originY = y
        width = nextW
        height = nextH
        density = nextD
        if (changed) listeners.forEach { runCatching { it() } }
    }
}
