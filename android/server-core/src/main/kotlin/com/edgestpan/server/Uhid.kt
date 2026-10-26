package com.edgestpan.server

/**
 * Linux uhid event layout measured from uhid.h.
 * CREATE2 is written as 4 + 4372 bytes. A longer write fails on 32-bit Android
 * because the kernel rejects an event larger than its userspace struct.
 */
object Uhid {
    const val Destroy = 1
    const val Create2 = 11
    const val Input2 = 12
    const val Create2Body = 4372
    const val BusVirtual = 0x06
    const val HidMax = 4096

    fun create2(name: String, descriptor: ByteArray, vendor: Int = 0x4547, product: Int = 0x0001): ByteArray {
        require(descriptor.size in 1..HidMax)
        val body = ByteArray(Create2Body)
        val rawName = name.toByteArray(Charsets.UTF_8).copyOf(minOf(name.toByteArray().size, 127))
        rawName.copyInto(body)
        put16(body, 256, descriptor.size)
        put16(body, 258, BusVirtual)
        put32(body, 260, vendor)
        put32(body, 264, product)
        put16(body, 268, 1)
        descriptor.copyInto(body, 276)
        val out = ByteArray(4 + Create2Body)
        put32(out, 0, Create2)
        body.copyInto(out, 4)
        return out
    }

    fun input2(report: ByteArray): ByteArray {
        val out = ByteArray(6 + report.size)
        put32(out, 0, Input2)
        put16(out, 4, report.size)
        report.copyInto(out, 6)
        return out
    }

    fun destroy(): ByteArray {
        val out = ByteArray(4)
        put32(out, 0, Destroy)
        return out
    }

    /**
     * Boot-style relative mouse. Android registers REL_X/REL_Y plus BTN_LEFT as
     * SOURCE_MOUSE and draws the system pointer. Absolute axes do not.
     * Large moves are split so each report stays inside -127..127.
     */
    fun relativeMouse(dx: Int, dy: Int, buttons: Int, wheel: Int): List<ByteArray> {
        val out = ArrayList<ByteArray>()
        var x = dx
        var y = dy
        var spin = wheel.coerceIn(-127, 127)
        if (x == 0 && y == 0) {
            out += mousePacket(buttons, 0, 0, spin)
            return out
        }
        while (x != 0 || y != 0) {
            val sx = x.coerceIn(-127, 127)
            val sy = y.coerceIn(-127, 127)
            out += mousePacket(buttons, sx, sy, spin)
            spin = 0
            x -= sx
            y -= sy
        }
        return out
    }

    private fun mousePacket(buttons: Int, dx: Int, dy: Int, wheel: Int) = byteArrayOf(
        (buttons and 0x1f).toByte(),
        dx.toByte(),
        dy.toByte(),
        wheel.toByte()
    )

    fun keyboard(modifiers: Int, usages: List<Int>): ByteArray {
        val report = ByteArray(8)
        report[0] = (modifiers and 0xff).toByte()
        usages.take(6).forEachIndexed { i, usage -> report[2 + i] = usage.toByte() }
        return report
    }

    private fun put16(buf: ByteArray, o: Int, v: Int) {
        buf[o] = v.toByte()
        buf[o + 1] = (v shr 8).toByte()
    }

    private fun put32(buf: ByteArray, o: Int, v: Int) {
        put16(buf, o, v)
        put16(buf, o + 2, v ushr 16)
    }
}

object HidReport {
    /** Relative pointer: 5 buttons, X, Y, wheel. No report id; one collection. */
    val mouse = byteArrayOf(
        0x05, 0x01, 0x09, 0x02, 0xA1.toByte(), 0x01, 0x09, 0x01, 0xA1.toByte(), 0x00,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95.toByte(), 0x05, 0x75, 0x01, 0x81.toByte(), 0x02,
        0x95.toByte(), 0x01, 0x75, 0x03, 0x81.toByte(), 0x01,
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x15, 0x81.toByte(), 0x25, 0x7F, 0x75, 0x08, 0x95.toByte(), 0x02, 0x81.toByte(), 0x06,
        0x09, 0x38, 0x15, 0x81.toByte(), 0x25, 0x7F, 0x75, 0x08, 0x95.toByte(), 0x01, 0x81.toByte(), 0x06,
        0xC0.toByte(), 0xC0.toByte()
    )

    val keyboard = byteArrayOf(
        0x05, 0x01, 0x09, 0x06, 0xA1.toByte(), 0x01,
        0x05, 0x07, 0x19, 0xE0.toByte(), 0x29, 0xE7.toByte(), 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95.toByte(), 0x08, 0x81.toByte(), 0x02,
        0x95.toByte(), 0x01, 0x75, 0x08, 0x81.toByte(), 0x01,
        0x95.toByte(), 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x65, 0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81.toByte(), 0x00,
        0xC0.toByte()
    )
}

fun interface HidSink {
    fun write(packet: ByteArray)
}

/**
 * HID modifier bits stay down until that key's own up. A normal key-up does not
 * clear Alt. A zero report releases every modifier and every usage.
 */
class KeyboardState {
    private var mods = 0
    private val usages = ArrayDeque<Int>()

    fun apply(vk: Int, hid: Int, down: Boolean): ByteArray {
        val bit = modifierBit(vk)
        if (!down && vk == 0 && hid == 0) {
            mods = 0
            usages.clear()
        } else if (bit != 0) {
            mods = if (down) mods or bit else mods and bit.inv()
        } else if (hid != 0) {
            if (down) {
                if (!usages.contains(hid)) usages.addLast(hid)
            } else usages.remove(hid)
        }
        return Uhid.keyboard(mods, usages.toList())
    }

    fun releaseAll(): ByteArray = apply(0, 0, false)

    private fun modifierBit(vk: Int) = when (vk) {
        0xA2, 0x11 -> 0x01
        0xA3 -> 0x10
        0xA0, 0x10 -> 0x02
        0xA1 -> 0x20
        0xA4, 0x12 -> 0x04
        0xA5 -> 0x40
        0x5B -> 0x08
        0x5C -> 0x80
        else -> 0
    }
}
