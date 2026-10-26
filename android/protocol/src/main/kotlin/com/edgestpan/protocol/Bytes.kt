package com.edgestpan.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

internal fun ByteArray.u8(i: Int) = this[i].toInt() and 0xff

internal fun le16(buf: ByteArray, o: Int) = (buf.u8(o)) or (buf.u8(o + 1) shl 8)
internal fun le32(buf: ByteArray, o: Int) = le16(buf, o) or (le16(buf, o + 2) shl 16)
internal fun lei32(buf: ByteArray, o: Int) = le32(buf, o)
internal fun leu64(buf: ByteArray, o: Int): Long {
    var v = 0L
    for (i in 0 until 8) v = v or (buf.u8(o + i).toLong() shl (8 * i))
    return v
}

internal fun put16(buf: ByteArray, o: Int, v: Int) {
    buf[o] = v.toByte()
    buf[o + 1] = (v shr 8).toByte()
}

internal fun put32(buf: ByteArray, o: Int, v: Int) {
    put16(buf, o, v)
    put16(buf, o + 2, v ushr 16)
}

internal fun put64(buf: ByteArray, o: Int, v: Long) {
    put32(buf, o, v.toInt())
    put32(buf, o + 4, (v ushr 32).toInt())
}

internal fun utf8Trim(value: String?, max: Int): ByteArray {
    val raw = (value ?: "").toByteArray(Charsets.UTF_8)
    if (raw.size <= max) return raw
    var n = max
    while (n > 0 && raw[n].toInt() and 0xC0 == 0x80) n--
    return raw.copyOf(n)
}

internal fun order(): ByteOrder = ByteOrder.LITTLE_ENDIAN

internal fun buffer(size: Int): ByteBuffer = ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN)
