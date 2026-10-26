package com.edgestpan.protocol

import kotlin.math.abs
import kotlin.math.hypot

/**
 * Turns touches on a streamed PC window into mouse input:
 * tap = left click, drag = left drag, long press = right click,
 * two-finger pan = wheel, pinch = Ctrl+wheel (zoom).
 */
class TouchGesture(private val slop: Float = 14f, private val longPressMs: Long = 480, private val wheelStepPx: Float = 36f) {
    sealed class Out {
        data class Move(val x: Int, val y: Int) : Out()
        data class Button(val button: Int, val down: Boolean, val x: Int, val y: Int) : Out()
        data class Wheel(val x: Int, val y: Int, val delta: Int, val ctrl: Boolean) : Out()
    }

    private enum class Mode { Idle, Pending, Dragging, LongDone, Multi }

    private var mode = Mode.Idle
    private var startX = 0f
    private var startY = 0f
    private var lastX = 0f
    private var lastY = 0f
    private var downAt = 0L
    private var midY = 0f
    private var spread = 0f
    private var panAcc = 0f
    private var zoomAcc = 0f

    fun down(x: Float, y: Float, t: Long): List<Out> {
        mode = Mode.Pending
        startX = x; startY = y; lastX = x; lastY = y; downAt = t
        return listOf(Out.Move(x.toInt(), y.toInt()))
    }

    fun move(x: Float, y: Float, t: Long): List<Out> {
        lastX = x; lastY = y
        return when (mode) {
            Mode.Pending -> if (hypot(x - startX, y - startY) > slop) {
                mode = Mode.Dragging
                listOf(Out.Button(1, true, startX.toInt(), startY.toInt()), Out.Move(x.toInt(), y.toInt()))
            } else tick(t)
            Mode.Dragging -> listOf(Out.Move(x.toInt(), y.toInt()))
            else -> emptyList()
        }
    }

    /** Long press without movement becomes a right click. */
    fun tick(t: Long): List<Out> {
        if (mode != Mode.Pending || t - downAt < longPressMs) return emptyList()
        mode = Mode.LongDone
        val x = startX.toInt(); val y = startY.toInt()
        return listOf(Out.Button(2, true, x, y), Out.Button(2, false, x, y))
    }

    fun up(x: Float, y: Float, t: Long): List<Out> {
        val out = when (mode) {
            Mode.Pending -> {
                val long = tick(t)
                if (long.isNotEmpty()) long
                else listOf(Out.Button(1, true, startX.toInt(), startY.toInt()), Out.Button(1, false, startX.toInt(), startY.toInt()))
            }
            Mode.Dragging -> listOf(Out.Button(1, false, x.toInt(), y.toInt()))
            else -> emptyList()
        }
        mode = Mode.Idle
        return out
    }

    /** Two or more fingers: (x1,y1) and (x2,y2) are the first two pointers. */
    fun multi(x1: Float, y1: Float, x2: Float, y2: Float): List<Out> {
        val out = ArrayList<Out>()
        val my = (y1 + y2) / 2f
        val mx = ((x1 + x2) / 2f).toInt()
        val d = hypot(x2 - x1, y2 - y1).coerceAtLeast(1f)
        if (mode != Mode.Multi) {
            if (mode == Mode.Dragging) out += Out.Button(1, false, lastX.toInt(), lastY.toInt())
            mode = Mode.Multi
            midY = my; spread = d; panAcc = 0f; zoomAcc = 0f
            return out
        }
        panAcc += my - midY
        zoomAcc += (d - spread) / spread
        midY = my; spread = d
        // Whichever gesture dominates wins, so a pinch does not also scroll.
        if (abs(zoomAcc) >= 0.08f && abs(zoomAcc) * 400f > abs(panAcc)) {
            out += Out.Wheel(mx, my.toInt(), if (zoomAcc > 0) 120 else -120, true)
            zoomAcc = 0f; panAcc = 0f
        } else if (abs(panAcc) >= wheelStepPx) {
            val steps = (panAcc / wheelStepPx).toInt()
            out += Out.Wheel(mx, my.toInt(), steps * 120, false)
            panAcc -= steps * wheelStepPx
            zoomAcc = 0f
        }
        return out
    }

    /** All fingers lifted after a multi-touch gesture. */
    fun reset() { mode = Mode.Idle }
}

/**
 * Receiver-side delay meter for adaptive bitrate: delay = (arrival - capture) minus the smallest such
 * value seen recently, so clock offset between PC and phone cancels out and only queueing remains.
 */
class DelayMeter(private val windowMs: Long = 8_000) {
    private var base = Long.MAX_VALUE
    private var baseAt = 0L
    private var worst = 0L
    private var frames = 0
    private var lastReport = 0L
    private var started = false

    /** Returns delay ms for a report when one is due (every second), else null. */
    fun add(nowMs: Long, nowUs: Long, captureUs: Long): Int? {
        val d = nowUs - captureUs
        if (d < base || nowMs - baseAt > windowMs) { base = d; baseAt = nowMs }
        val q = (d - base) / 1000
        if (q > worst) worst = q
        frames++
        if (!started) { started = true; lastReport = nowMs }
        if (nowMs - lastReport < 1000) return null
        val out = worst.coerceIn(0, 0xFFFF).toInt()
        worst = 0; frames = 0; lastReport = nowMs
        return out
    }
}
