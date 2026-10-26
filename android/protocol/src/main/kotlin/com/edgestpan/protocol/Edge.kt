package com.edgestpan.protocol

object ScreenEdge {
    const val Left = 0
    const val Right = 1
    const val Top = 2
    const val Bottom = 3
}

object EdgeGuard {
    /**
     * Handoff only after the pointer is on the last pixel of [edge] and the next
     * sample still moves outward. A coordinate that merely lands past the edge
     * from the middle of the screen does not cross.
     */
    fun crosses(edge: Int, x: Int, y: Int, width: Int, height: Int, prevX: Int, prevY: Int): Boolean {
        val lastX = (width - 1).coerceAtLeast(0)
        val lastY = (height - 1).coerceAtLeast(0)
        return when (edge) {
            ScreenEdge.Left -> prevX == 0 && x < 0
            ScreenEdge.Right -> prevX == lastX && x > lastX
            ScreenEdge.Top -> prevY == 0 && y < 0
            ScreenEdge.Bottom -> prevY == lastY && y > lastY
            else -> false
        }
    }
}

object ModifierKeys {
    fun isModifier(vk: Int) = vk == 0x10 || vk == 0x11 || vk == 0x12 ||
        vk == 0x5B || vk == 0x5C || (vk in 0xA0..0xA5)
}

/** Inclusive pixel range of a display: 0 .. size-1 on both axes. */
object PointerBounds {
    fun clamp(x: Int, y: Int, width: Int, height: Int): Pair<Int, Int> {
        val maxX = (width - 1).coerceAtLeast(0)
        val maxY = (height - 1).coerceAtLeast(0)
        return x.coerceIn(0, maxX) to y.coerceIn(0, maxY)
    }
}

data class PairingInvite(val host: String, val port: Int, val pin: String, val name: String, val relayPeer: String = "", val relayUrl: String = "")

object PairingLink {
    fun parse(text: String?): PairingInvite? {
        if (text.isNullOrBlank()) return null
        val trimmed = text.trim()
        val query = when {
            trimmed.startsWith("edgestpan://", ignoreCase = true) -> trimmed.substringAfter('?', "")
            trimmed.contains('=') -> trimmed.trimStart('?')
            else -> return null
        }
        if (query.isEmpty()) return null
        val map = query.split('&').mapNotNull {
            val i = it.indexOf('=')
            if (i <= 0) null else java.net.URLDecoder.decode(it.substring(0, i), Charsets.UTF_8) to java.net.URLDecoder.decode(it.substring(i + 1), Charsets.UTF_8)
        }.toMap()
        val host = map["h"] ?: return null
        val pin = map["pin"]?.trim() ?: return null
        val port = map["p"]?.toIntOrNull() ?: return null
        if (host.isBlank() || pin.length < 4 || port !in 1..65535) return null
        return PairingInvite(host, port, pin, map["n"] ?: "", map["r"] ?: "", map["u"] ?: "")
    }
}

data class DiscoveredHost(val id: String, val name: String, val port: Int, val width: Int, val height: Int, val pin: String = "")

sealed class PairRequest {
    data class Direct(val invite: PairingInvite) : PairRequest()
    data class Pin(val pin: String) : PairRequest()
}

object PairCodes {
    fun parse(text: String?): PairRequest? {
        val trimmed = text?.trim().orEmpty()
        if (trimmed.isEmpty()) return null
        PairingLink.parse(trimmed)?.let { return PairRequest.Direct(it) }
        if (trimmed.length in 4..8 && trimmed.all { it.isDigit() }) return PairRequest.Pin(trimmed)
        return null
    }
}

object RelayFrame {
    const val BIND: Byte = 1
    const val DATA: Byte = 2
    const val DELIVER: Byte = 3

    fun bind(token: ByteArray, peerId: String) = encode(BIND, token, peerId, ByteArray(0))
    fun data(token: ByteArray, destination: String, payload: ByteArray) = encode(DATA, token, destination, payload)

    fun encode(op: Byte, token: ByteArray, id: String, payload: ByteArray): ByteArray {
        require(token.size == 16)
        val idBytes = id.toByteArray(Charsets.UTF_8)
        require(idBytes.size in 1..64)
        val out = ByteArray(21 + idBytes.size + payload.size)
        out[0] = 0xE5.toByte()
        out[1] = 0x52
        out[2] = 1
        out[3] = op
        token.copyInto(out, 4)
        out[20] = idBytes.size.toByte()
        idBytes.copyInto(out, 21)
        payload.copyInto(out, 21 + idBytes.size)
        return out
    }

    data class Parsed(val op: Byte, val id: String, val payload: ByteArray)

    fun parse(buffer: ByteArray): Parsed? {
        if (buffer.size < 22 || buffer[0] != 0xE5.toByte() || buffer[1] != 0x52.toByte() || buffer[2] != 1.toByte()) return null
        val length = buffer[20].toInt() and 0xff
        if (length == 0 || 21 + length > buffer.size) return null
        val id = buffer.copyOfRange(21, 21 + length).toString(Charsets.UTF_8)
        return Parsed(buffer[3], id, buffer.copyOfRange(21 + length, buffer.size))
    }
}

object DiscoveryCodec {
    fun tryDecode(buffer: ByteArray): DiscoveredHost? {
        if (buffer.size < 13 || buffer[0] != 0xE5.toByte() || buffer[1] != 0x44.toByte() || buffer[2] != 1.toByte()) return null
        val port = (buffer[5].toInt() and 0xff) or ((buffer[6].toInt() and 0xff) shl 8)
        val width = (buffer[7].toInt() and 0xff) or ((buffer[8].toInt() and 0xff) shl 8)
        val height = (buffer[9].toInt() and 0xff) or ((buffer[10].toInt() and 0xff) shl 8)
        var offset = 11
        val idLen = buffer.getOrNull(offset)?.toInt()?.and(0xff) ?: return null
        offset++
        if (offset + idLen > buffer.size) return null
        val id = buffer.copyOfRange(offset, offset + idLen).toString(Charsets.UTF_8)
        offset += idLen
        val nameLen = buffer.getOrNull(offset)?.toInt()?.and(0xff) ?: return null
        offset++
        if (offset + nameLen > buffer.size) return null
        val name = buffer.copyOfRange(offset, offset + nameLen).toString(Charsets.UTF_8)
        offset += nameLen
        var pin = ""
        if (offset < buffer.size) {
            val n = buffer[offset].toInt() and 0xff
            if (n in 4..8 && offset + 1 + n <= buffer.size) {
                val text = buffer.copyOfRange(offset + 1, offset + 1 + n).toString(Charsets.US_ASCII)
                if (text.all { it in '0'..'9' }) pin = text
            }
        }
        return DiscoveredHost(id, name, port, width, height, pin)
    }

    fun announce(id: String, name: String, role: Int, caps: Int, port: Int, width: Int, height: Int, pin: String = ""): ByteArray {
        val idb = utf8Trim(id, 32)
        val nameb = utf8Trim(name, 96)
        val pinOk = pin.length in 4..8 && pin.all { it in '0'..'9' }
        val extra = if (pinOk) 1 + pin.length else 0
        val buf = ByteArray(11 + 1 + idb.size + 1 + nameb.size + extra)
        buf[0] = 0xE5.toByte(); buf[1] = 0x44; buf[2] = 1; buf[3] = role.toByte(); buf[4] = caps.toByte()
        put16(buf, 5, port); put16(buf, 7, width); put16(buf, 9, height)
        buf[11] = idb.size.toByte()
        idb.copyInto(buf, 12)
        buf[12 + idb.size] = nameb.size.toByte()
        nameb.copyInto(buf, 13 + idb.size)
        if (pinOk) {
            val at = 13 + idb.size + nameb.size
            buf[at] = pin.length.toByte()
            pin.toByteArray(Charsets.US_ASCII).copyInto(buf, at + 1)
        }
        return buf
    }
}
