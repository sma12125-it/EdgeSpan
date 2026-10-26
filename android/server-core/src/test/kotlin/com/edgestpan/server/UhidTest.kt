package com.edgestpan.server

import kotlin.test.Test
import kotlin.test.assertEquals

class UhidTest {
    @Test
    fun create2IsTheShortAndroidWrite() {
        val packet = Uhid.create2("EdgeSpan", HidReport.mouse)
        assertEquals(4376, packet.size)
        assertEquals(11, packet[0].toInt())
        assertEquals(0, packet[1].toInt())
        assertEquals(Uhid.BusVirtual, packet[4 + 258].toInt() and 0xff)
        assertEquals(HidReport.mouse.size, (packet[4 + 256].toInt() and 0xff) or ((packet[4 + 257].toInt() and 0xff) shl 8))
        assertEquals('E'.code, packet[4].toInt())
        assert(HidReport.mouse.contains(0x06.toByte()))
        assert(!HidReport.mouse.contains(0x26.toByte()))
    }

    @Test
    fun relativeReportsCarryButtonsAndSplitLargeMoves() {
        val click = Uhid.relativeMouse(0, 0, 1, 0)
        assertEquals(1, click.size)
        assertEquals(4, click[0].size)
        assertEquals(1, click[0][0].toInt())
        val moved = Uhid.relativeMouse(300, -10, 0, 1)
        val sumX = moved.sumOf { it[1].toInt().toByte().toInt() }
        val sumY = moved.sumOf { it[2].toInt().toByte().toInt() }
        assertEquals(300, sumX)
        assertEquals(-10, sumY)
        assertEquals(1, moved[0][3].toInt())
        val packet = Uhid.input2(click[0])
        assertEquals(10, packet.size)
        assertEquals(12, packet[0].toInt())
        assertEquals(4, packet[4].toInt())
    }

    @Test
    fun helloUpdatesTheScreenAndEmitsAReport() {
        val writes = mutableListOf<ByteArray>()
        val server = InjectionServer({ writes += it }, { })
        val hello = ByteArray(21)
        hello[16] = 0x38; hello[17] = 0x04
        hello[18] = 0x80.toByte(); hello[19] = 0x07
        server.handle(1, hello)
        assertEquals(1080, server.width)
        assertEquals(1920, server.height)
        server.handle(3, byteArrayOf(10, 0, 0, 0, 20, 0, 0, 0))
        assert(writes.isNotEmpty())
        assertEquals(10, writes[0].size)
        server.handle(4, byteArrayOf(0, 1, 10, 0, 0, 0, 20, 0, 0, 0))
        val click = writes.last()
        assertEquals(1, click[6].toInt() and 0x1f)
        server.handle(4, byteArrayOf(1, 1, 10, 0, 0, 0, 20, 0, 0, 0))
        assertEquals(0x03, writes.last()[6].toInt() and 0x1f)
        server.handle(5, byteArrayOf(0, 0, 0x78, 0, 10, 0, 0, 0, 20, 0, 0, 0))
        assertEquals(0x78, writes.last()[9].toInt() and 0xff)
    }

    @Test
    fun pointerStopsOnTheLastPixel() {
        val writes = mutableListOf<ByteArray>()
        val server = InjectionServer({ writes += it }, { })
        val hello = ByteArray(21)
        hello[16] = 100
        hello[18] = 80
        server.handle(1, hello)
        writes.clear()
        server.handle(3, byteArrayOf(0xE8.toByte(), 0x03, 0, 0, 10, 0, 0, 0))
        val dx = writes.sumOf { if (it.size < 8) 0 else it[7].toInt().toByte().toInt() }
        assertEquals(99 - 50, dx)
        writes.clear()
        server.handle(3, byteArrayOf(0xD8.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 10, 0, 0, 0))
        val back = writes.sumOf { if (it.size < 8) 0 else it[7].toInt().toByte().toInt() }
        assertEquals(-99, back)
    }

    @Test
    fun altStaysDownUntilItsOwnKeyUp() {
        val writes = mutableListOf<ByteArray>()
        val server = InjectionServer({ }, { writes += it })
        server.handle(6, key(0xA4, 0, true))
        assertEquals(0x04, writes.last()[6].toInt() and 0xff)
        server.handle(6, key(0x41, 0x04, true))
        assertEquals(0x04, writes.last()[6].toInt() and 0xff)
        assertEquals(0x04, writes.last()[8].toInt() and 0xff)
        server.handle(6, key(0x41, 0x04, false))
        assertEquals(0x04, writes.last()[6].toInt() and 0xff)
        assertEquals(0, writes.last()[8].toInt() and 0xff)
        server.handle(6, key(0, 0, false))
        assertEquals(0, writes.last()[6].toInt() and 0xff)
        assertEquals(0, writes.last()[8].toInt() and 0xff)
    }

    private fun key(vk: Int, hid: Int, down: Boolean): ByteArray {
        val payload = ByteArray(10)
        payload[0] = vk.toByte()
        payload[1] = (vk shr 8).toByte()
        payload[2] = hid.toByte()
        payload[3] = (hid shr 8).toByte()
        payload[5] = if (down) 1 else 0
        return payload
    }
}
