package com.edgestpan.protocol

object HandoffPayloads {
    const val PcToPhone = 1
    const val PhoneToPc = 2
    const val CodecH264 = 1
    const val CodecHevc = 2
    const val InputMove = 1
    const val InputButton = 2
    const val InputWheel = 3
    const val InputKey = 4
    const val InputText = 5
    const val StateFocused = 1
    const val StateMinimized = 2
    const val StateRestored = 3
    const val StateClosed = 4

    data class Offer(
        val id: Long,
        val direction: Int,
        val dpi: Long,
        val physW: Int,
        val physH: Int,
        val dipW: Int,
        val dipH: Int,
        val codec: Int,
        val edge: Int,
        val title: String
    )

    fun writeOffer(offer: Offer): ByteArray {
        val title = utf8Trim(offer.title, 180)
        val buf = ByteArray(20 + title.size)
        put32(buf, 0, offer.id.toInt())
        buf[4] = offer.direction.toByte()
        put32(buf, 5, offer.dpi.toInt())
        put16(buf, 9, offer.physW)
        put16(buf, 11, offer.physH)
        put16(buf, 13, offer.dipW)
        put16(buf, 15, offer.dipH)
        buf[17] = offer.codec.toByte()
        buf[18] = offer.edge.toByte()
        buf[19] = title.size.toByte()
        title.copyInto(buf, 20)
        return buf
    }

    fun tryOffer(payload: ByteArray): Offer? {
        if (payload.size < 20) return null
        val n = payload.u8(19)
        if (20 + n > payload.size) return null
        return Offer(
            le32(payload, 0).toLong() and 0xffffffffL,
            payload.u8(4),
            le32(payload, 5).toLong() and 0xffffffffL,
            le16(payload, 9),
            le16(payload, 11),
            le16(payload, 13),
            le16(payload, 15),
            payload.u8(17),
            payload.u8(18),
            payload.copyOfRange(20, 20 + n).toString(Charsets.UTF_8)
        )
    }

    fun writeAccept(id: Long, codec: Int, viewW: Int, viewH: Int): ByteArray {
        val buf = ByteArray(9)
        put32(buf, 0, id.toInt())
        buf[4] = codec.toByte()
        put16(buf, 5, viewW)
        put16(buf, 7, viewH)
        return buf
    }

    fun writeGeometry(id: Long, x: Int, y: Int, physW: Int, physH: Int, dipW: Int, dipH: Int): ByteArray {
        val buf = ByteArray(20)
        put32(buf, 0, id.toInt())
        put32(buf, 4, x)
        put32(buf, 8, y)
        put16(buf, 12, physW)
        put16(buf, 14, physH)
        put16(buf, 16, dipW)
        put16(buf, 18, dipH)
        return buf
    }

    fun tryGeometry(payload: ByteArray): Geometry? {
        if (payload.size < 20) return null
        return Geometry(
            le32(payload, 0).toLong() and 0xffffffffL,
            lei32(payload, 4),
            lei32(payload, 8),
            le16(payload, 12),
            le16(payload, 14),
            le16(payload, 16),
            le16(payload, 18)
        )
    }

    data class Geometry(val id: Long, val x: Int, val y: Int, val physW: Int, val physH: Int, val dipW: Int, val dipH: Int)

    fun writeInput(id: Long, kind: Int, x: Int, y: Int, button: Int, down: Boolean, wheelX: Int, wheelY: Int, vk: Int, text: String): ByteArray {
        val raw = utf8Trim(text, 400)
        val buf = ByteArray(23 + raw.size)
        put32(buf, 0, id.toInt())
        buf[4] = kind.toByte()
        put32(buf, 5, x)
        put32(buf, 9, y)
        buf[13] = button.toByte()
        buf[14] = if (down) 1 else 0
        put16(buf, 15, wheelX)
        put16(buf, 17, wheelY)
        put16(buf, 19, vk)
        put16(buf, 21, raw.size)
        raw.copyInto(buf, 23)
        return buf
    }

    fun writeState(id: Long, value: Int): ByteArray {
        val buf = ByteArray(5)
        put32(buf, 0, id.toInt())
        buf[4] = value.toByte()
        return buf
    }

    fun tryState(payload: ByteArray): Pair<Long, Int>? {
        if (payload.size < 5) return null
        return (le32(payload, 0).toLong() and 0xffffffffL) to payload.u8(4)
    }

    fun writeReturn(id: Long): ByteArray {
        val buf = ByteArray(4)
        put32(buf, 0, id.toInt())
        return buf
    }

    fun tryReturn(payload: ByteArray): Long? {
        if (payload.size < 4) return null
        return le32(payload, 0).toLong() and 0xffffffffL
    }

    fun writeVideo(id: Long, bitrate: Long, fps: Int, idr: Boolean): ByteArray {
        val buf = ByteArray(10)
        put32(buf, 0, id.toInt())
        put32(buf, 4, bitrate.toInt())
        buf[8] = fps.toByte()
        buf[9] = if (idr) 1 else 0
        return buf
    }
}
