package com.edgestpan.protocol

object PacketType {
    const val Hello: Byte = 0x01
    const val HelloAck: Byte = 0x02
    const val PairInit: Byte = 0x10
    const val PairChallenge: Byte = 0x11
    const val PairConfirm: Byte = 0x12
    const val PairAccept: Byte = 0x13
    const val PairReject: Byte = 0x14
    const val MouseMove: Byte = 0x20
    const val MouseButton: Byte = 0x21
    const val MouseScroll: Byte = 0x22
    const val KeyEvent: Byte = 0x23
    const val Clipboard: Byte = 0x24
    const val FileOffer: Byte = 0x25
    const val FileChunk: Byte = 0x26
    const val FileAck: Byte = 0x27
    const val FileDone: Byte = 0x28
    const val FileCancel: Byte = 0x29
    const val EdgeEnter: Byte = 0x30
    const val EdgeReturn: Byte = 0x31
    const val Heartbeat: Byte = 0x40
    const val Ping: Byte = 0x41
    const val Pong: Byte = 0x42
    const val Ack: Byte = 0x43
    const val Goodbye: Byte = 0x44
    const val Lock: Byte = 0x50
    const val Unlock: Byte = 0x51
    const val GlowStyle: Byte = 0x52
    const val HandoffOffer: Byte = 0x60
    const val HandoffAccept: Byte = 0x61
    const val HandoffGeometry: Byte = 0x62
    const val HandoffInput: Byte = 0x63
    const val HandoffState: Byte = 0x64
    const val HandoffReturn: Byte = 0x65
    const val HandoffVideoConfig: Byte = 0x66
    const val HandoffClock: Byte = 0x67
}

object PacketFlags {
    const val Encrypted: Byte = 0x01
    const val Reliable: Byte = 0x02
    const val Fragment: Byte = 0x04
}

data class PacketHeader(
    val type: Byte,
    val flags: Byte,
    val sessionId: Long,
    val sequence: Long,
    val ack: Long,
    val timestampUs: Long,
    val payloadLength: Int
) {
    val encrypted get() = flags.toInt() and 0x01 != 0
    val reliable get() = flags.toInt() and 0x02 != 0
    val fragment get() = flags.toInt() and 0x04 != 0
}

object PacketCodec {
    const val Magic: Byte = 0xE5.toByte()
    const val Version: Byte = 1
    const val HeaderSize = 26
    const val TagSize = 16
    const val MaxPayload = 48_000

    private val known = setOf(
        PacketType.Hello, PacketType.HelloAck, PacketType.PairInit, PacketType.PairChallenge,
        PacketType.PairConfirm, PacketType.PairAccept, PacketType.PairReject, PacketType.MouseMove,
        PacketType.MouseButton, PacketType.MouseScroll, PacketType.KeyEvent, PacketType.Clipboard,
        PacketType.FileOffer, PacketType.FileChunk, PacketType.FileAck, PacketType.FileDone, PacketType.FileCancel,
        PacketType.EdgeEnter, PacketType.EdgeReturn, PacketType.Heartbeat, PacketType.Ping,
        PacketType.Pong, PacketType.Ack, PacketType.Goodbye, PacketType.Lock, PacketType.Unlock, PacketType.GlowStyle,
        PacketType.HandoffOffer, PacketType.HandoffAccept, PacketType.HandoffGeometry, PacketType.HandoffInput,
        PacketType.HandoffState, PacketType.HandoffReturn, PacketType.HandoffVideoConfig, PacketType.HandoffClock
    )

    fun encodeClear(type: Byte, flags: Byte, sessionId: Long, sequence: Long, ack: Long, timestampUs: Long, payload: ByteArray): ByteArray {
        val buf = ByteArray(HeaderSize + payload.size)
        writeHeader(buf, type, flags, sessionId, sequence, ack, timestampUs, payload.size)
        payload.copyInto(buf, HeaderSize)
        return buf
    }

    fun writeHeader(dest: ByteArray, type: Byte, flags: Byte, sessionId: Long, sequence: Long, ack: Long, timestampUs: Long, payloadLength: Int) {
        dest[0] = Magic
        dest[1] = Version
        dest[2] = type
        dest[3] = flags
        put32(dest, 4, sessionId.toInt())
        put32(dest, 8, sequence.toInt())
        put32(dest, 12, ack.toInt())
        put64(dest, 16, timestampUs)
        put16(dest, 24, payloadLength)
    }

    fun readHeader(buffer: ByteArray): PacketHeader? {
        if (buffer.size < HeaderSize || buffer[0] != Magic || buffer[1] != Version) return null
        if (buffer[2] !in known) return null
        return PacketHeader(
            buffer[2], buffer[3],
            le32(buffer, 4).toLong() and 0xffffffffL,
            le32(buffer, 8).toLong() and 0xffffffffL,
            le32(buffer, 12).toLong() and 0xffffffffL,
            java.lang.Long.parseUnsignedLong(java.lang.Long.toUnsignedString(readU64(buffer, 16))),
            le16(buffer, 24)
        )
    }

    fun decodeClear(buffer: ByteArray): Pair<PacketHeader, ByteArray>? {
        val header = readHeader(buffer) ?: return null
        if (header.encrypted || buffer.size < HeaderSize + header.payloadLength) return null
        return header to buffer.copyOfRange(HeaderSize, HeaderSize + header.payloadLength)
    }

    private fun readU64(buf: ByteArray, o: Int): Long {
        var v = 0L
        for (i in 0 until 8) v = v or ((buf.u8(o + i).toLong()) shl (8 * i))
        return v
    }
}
