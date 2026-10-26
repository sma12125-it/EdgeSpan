package com.edgestpan.protocol

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull

/** Same bytes as AppStreamTests.WireBytesMatchAndroid on the Windows side. */
class AppStreamWireTest {
    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

    @Test
    fun helloBytesMatchWindows() {
        assertEquals("1c00000045534131027031380460094d0000000000000001004e0400612f2e4d",
            hex(AppStreamWire.writeHello("p1", 1080, 2400, 77, "N", "a/.M")))
    }

    @Test
    fun inputBytesMatchWindowsAndRoundTrip() {
        val i = AppStreamWire.Input(AppStreamWire.InputButton, 100, 2000, 2, true, -1, 0x41, 4, "x")
        val bytes = AppStreamWire.writeInput(i)
        assertEquals("0264000000d00700000201ffff41000400010078", hex(bytes))
        assertEquals(i, AppStreamWire.readInput(bytes))
    }

    @Test
    fun replyParses() {
        val body = "ESA1".toByteArray() + byteArrayOf(1, 5, 0, 0, 0, 0, 0, 0, 0)
        val r = assertNotNull(AppStreamWire.readReply(body))
        assertEquals(true to 5L, r)
    }
}
