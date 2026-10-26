package com.edgestpan.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Mirror of EdgeSpan.Protocol.AppStreamWire (C#): Android app stream to Windows on TCP 27186. */
object AppStreamWire {
    const val PORT = 27186
    val MAGIC = "ESA1".toByteArray()
    const val InputMove = 1; const val InputButton = 2; const val InputWheel = 3; const val InputKey = 4
    const val InputText = 5; const val InputClose = 6; const val InputBack = 7; const val InputIdr = 8

    data class Input(val kind: Int, val x: Int, val y: Int, val button: Int, val down: Boolean, val wheel: Int, val vk: Int, val hid: Int, val text: String)

    fun writeHello(deviceId: String, width: Int, height: Int, startSeq: Long, title: String, pkg: String): ByteArray {
        val id = deviceId.toByteArray()
        val t = title.toByteArray()
        val p = pkg.toByteArray()
        val len = 4 + 1 + id.size + 2 + 2 + 8 + 2 + t.size + 2 + p.size
        val b = ByteBuffer.allocate(4 + len).order(ByteOrder.LITTLE_ENDIAN)
        b.putInt(len).put(MAGIC).put(id.size.toByte()).put(id)
        b.putShort(width.toShort()).putShort(height.toShort()).putLong(startSeq)
        b.putShort(t.size.toShort()).put(t).putShort(p.size.toShort()).put(p)
        return b.array()
    }

    /** Reply body (after the u32 length): magic, accepted, PC start sequence. Null when malformed. */
    fun readReply(body: ByteArray): Pair<Boolean, Long>? {
        if (body.size < 13 || !body.copyOf(4).contentEquals(MAGIC)) return null
        val b = ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN)
        return (body[4].toInt() != 0) to b.getLong(5)
    }

    fun readInput(body: ByteArray): Input? {
        if (body.size < 19) return null
        val b = ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN)
        val kind = body[0].toInt() and 0xff
        val x = b.getInt(1); val y = b.getInt(5)
        val button = body[9].toInt() and 0xff
        val down = body[10].toInt() != 0
        val wheel = b.getShort(11).toInt()
        val vk = b.getShort(13).toInt() and 0xffff
        val hid = b.getShort(15).toInt() and 0xffff
        val tl = b.getShort(17).toInt() and 0xffff
        if (body.size < 19 + tl) return null
        return Input(kind, x, y, button, down, wheel, vk, hid, String(body, 19, tl))
    }

    fun writeInput(i: Input): ByteArray {
        val t = i.text.toByteArray()
        val b = ByteBuffer.allocate(19 + t.size).order(ByteOrder.LITTLE_ENDIAN)
        b.put(i.kind.toByte()).putInt(i.x).putInt(i.y).put(i.button.toByte()).put((if (i.down) 1 else 0).toByte())
        b.putShort(i.wheel.coerceIn(-32768, 32767).toShort()).putShort(i.vk.toShort()).putShort(i.hid.toShort()).putShort(t.size.toShort()).put(t)
        return b.array()
    }
}
