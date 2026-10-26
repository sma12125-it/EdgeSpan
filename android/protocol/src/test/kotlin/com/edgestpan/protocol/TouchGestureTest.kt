package com.edgestpan.protocol

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class TouchGestureTest {
    private fun buttons(out: List<TouchGesture.Out>) = out.filterIsInstance<TouchGesture.Out.Button>()

    @Test
    fun tapIsLeftClick() {
        val g = TouchGesture()
        g.down(100f, 100f, 0)
        val b = buttons(g.up(101f, 100f, 80))
        assertEquals(listOf(1 to true, 1 to false), b.map { it.button to it.down })
    }

    @Test
    fun longPressIsRightClick() {
        val g = TouchGesture()
        g.down(50f, 50f, 0)
        val b = buttons(g.tick(600))
        assertEquals(listOf(2 to true, 2 to false), b.map { it.button to it.down })
        assertTrue(buttons(g.up(50f, 50f, 700)).isEmpty())
    }

    @Test
    fun dragHoldsLeftButton() {
        val g = TouchGesture()
        g.down(0f, 0f, 0)
        val first = buttons(g.move(40f, 0f, 30))
        assertEquals(listOf(1 to true), first.map { it.button to it.down })
        assertEquals(listOf(1 to false), buttons(g.up(60f, 0f, 90)).map { it.button to it.down })
    }

    @Test
    fun pinchIsCtrlWheelAndPanIsPlainWheel() {
        val g = TouchGesture()
        g.down(100f, 100f, 0)
        g.multi(100f, 100f, 200f, 100f)
        val zoom = g.multi(80f, 100f, 240f, 100f).filterIsInstance<TouchGesture.Out.Wheel>()
        assertTrue(zoom.isNotEmpty() && zoom.all { it.ctrl })
        val g2 = TouchGesture()
        g2.down(100f, 100f, 0)
        g2.multi(100f, 100f, 200f, 100f)
        val pan = g2.multi(100f, 180f, 200f, 180f).filterIsInstance<TouchGesture.Out.Wheel>()
        assertTrue(pan.isNotEmpty() && pan.none { it.ctrl })
    }

    @Test
    fun delayMeterReportsQueueingOncePerSecond() {
        val m = DelayMeter()
        assertNull(m.add(0, 10_000, 0))
        assertNull(m.add(500, 510_000 + 40_000, 500_000))
        val d = m.add(1000, 1_010_000 + 90_000, 1_000_000)
        assertEquals(90, d)
    }
}
