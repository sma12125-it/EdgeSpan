package com.edgestpan.protocol

object DatagramFrames {
    fun encode(payload: ByteArray): ByteArray {
        require(payload.isNotEmpty() && payload.size <= 65535)
        val out = ByteArray(2 + payload.size)
        out[0] = (payload.size and 0xff).toByte()
        out[1] = ((payload.size shr 8) and 0xff).toByte()
        payload.copyInto(out, 2)
        return out
    }
}

class FrameReader {
    private var buf = ByteArray(0)

    fun append(data: ByteArray, length: Int = data.size) {
        if (length <= 0) return
        val next = ByteArray(buf.size + length)
        buf.copyInto(next)
        System.arraycopy(data, 0, next, buf.size, length)
        buf = next
    }

    fun take(): ByteArray? {
        if (buf.size < 2) return null
        val len = (buf[0].toInt() and 0xff) or ((buf[1].toInt() and 0xff) shl 8)
        if (len == 0 || len > 65535) {
            buf = ByteArray(0)
            return null
        }
        if (buf.size < 2 + len) return null
        val payload = buf.copyOfRange(2, 2 + len)
        buf = buf.copyOfRange(2 + len, buf.size)
        return payload
    }
}
