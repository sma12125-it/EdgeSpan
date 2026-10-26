package com.edgestpan.protocol

object Payloads {
    const val PublicKeyLength = 65

    fun hello(role: Int, caps: Int, width: Int, height: Int, port: Int, id: String, name: String): ByteArray {
        val idb = utf8Trim(id, 32)
        val nameb = utf8Trim(name, 96)
        val buf = ByteArray(10 + idb.size + nameb.size)
        buf[0] = role.toByte(); buf[1] = caps.toByte()
        put16(buf, 2, width); put16(buf, 4, height); put16(buf, 6, port)
        buf[8] = idb.size.toByte()
        idb.copyInto(buf, 9)
        val n = 9 + idb.size
        buf[n] = nameb.size.toByte()
        nameb.copyInto(buf, n + 1)
        return buf
    }

    /** Trailing license code and days, appended by the computer after the name. Absent on older builds. */
    fun hostLicense(body: ByteArray): Pair<Int, Int> {
        if (body.size < 9) return 0 to 0
        val idLen = body.u8(8)
        val nameAt = 9 + idLen
        if (nameAt >= body.size) return 0 to 0
        val nameLen = body.u8(nameAt)
        val after = nameAt + 1 + nameLen
        if (after >= body.size) return 0 to 0
        val days = if (after + 1 < body.size) body.u8(after + 1) else 0
        return body.u8(after) to days
    }

    fun mouseMove(x: Int, y: Int): ByteArray {
        val buf = ByteArray(8)
        put32(buf, 0, x); put32(buf, 4, y)
        return buf
    }

    fun readMouseMove(p: ByteArray): Pair<Int, Int>? {
        if (p.size != 8) return null
        return lei32(p, 0) to lei32(p, 4)
    }

    fun mouseButton(button: Int, down: Boolean, x: Int, y: Int): ByteArray {
        val buf = ByteArray(10)
        buf[0] = button.toByte(); buf[1] = if (down) 1 else 0
        put32(buf, 2, x); put32(buf, 6, y)
        return buf
    }

    fun readMouseButton(p: ByteArray): MouseButtonMsg? {
        if (p.size != 10) return null
        return MouseButtonMsg(p.u8(0), p[1].toInt() != 0, lei32(p, 2), lei32(p, 6))
    }

    fun scroll(dx: Int, dy: Int, x: Int, y: Int): ByteArray {
        val buf = ByteArray(12)
        put16(buf, 0, dx); put16(buf, 2, dy); put32(buf, 4, x); put32(buf, 8, y)
        return buf
    }

    fun readScroll(p: ByteArray): ScrollMsg? {
        if (p.size != 12) return null
        return ScrollMsg(le16(p, 0).toShort().toInt(), le16(p, 2).toShort().toInt(), lei32(p, 4), lei32(p, 8))
    }

    fun key(vk: Int, hid: Int, mods: Int, down: Boolean, codepoint: Long): ByteArray {
        val buf = ByteArray(10)
        put16(buf, 0, vk); put16(buf, 2, hid)
        buf[4] = mods.toByte(); buf[5] = if (down) 1 else 0
        put32(buf, 6, codepoint.toInt())
        return buf
    }

    fun readKey(p: ByteArray): KeyMsg? {
        if (p.size != 10) return null
        return KeyMsg(le16(p, 0), le16(p, 2), p.u8(4), p[5].toInt() != 0, le32(p, 6).toLong() and 0xffffffffL)
    }

    fun edgeEnter(edge: Int, x: Int, y: Int, width: Int, height: Int): ByteArray {
        val buf = ByteArray(13)
        buf[0] = edge.toByte()
        put32(buf, 1, x); put32(buf, 5, y); put16(buf, 9, width); put16(buf, 11, height)
        return buf
    }

    fun readEdgeEnter(p: ByteArray): EdgeEnterMsg? {
        if (p.size != 13) return null
        return EdgeEnterMsg(p.u8(0), lei32(p, 1), lei32(p, 5), le16(p, 9), le16(p, 11))
    }

    fun edgeReturn(hostX: Int, hostY: Int, deviceX: Int, deviceY: Int): ByteArray {
        val buf = ByteArray(16)
        put32(buf, 0, hostX); put32(buf, 4, hostY); put32(buf, 8, deviceX); put32(buf, 12, deviceY)
        return buf
    }

    fun readEdgeReturn(p: ByteArray): EdgeReturnMsg? {
        if (p.size != 16) return null
        return EdgeReturnMsg(lei32(p, 0), lei32(p, 4), lei32(p, 8), lei32(p, 12))
    }

    fun clipboard(id: Long, utf8: ByteArray): ByteArray {
        val buf = ByteArray(8 + utf8.size)
        put32(buf, 0, id.toInt()); put32(buf, 4, utf8.size)
        utf8.copyInto(buf, 8)
        return buf
    }

    fun readClipboard(p: ByteArray): Pair<Long, String>? {
        if (p.size < 8) return null
        val len = le32(p, 4)
        if (len > p.size - 8) return null
        return (le32(p, 0).toLong() and 0xffffffffL) to p.copyOfRange(8, 8 + len).toString(Charsets.UTF_8)
    }

    fun fileOffer(id: Long, cut: Boolean, size: Long, name: String): ByteArray {
        val utf = utf8Trim(name, 240)
        val buf = ByteArray(15 + utf.size)
        put32(buf, 0, id.toInt())
        buf[4] = if (cut) 1 else 0
        put64(buf, 5, size)
        put16(buf, 13, utf.size)
        utf.copyInto(buf, 15)
        return buf
    }

    fun readFileOffer(p: ByteArray): FileOfferMsg? {
        if (p.size < 15) return null
        val size = leu64(p, 5)
        val len = le16(p, 13)
        if (len > p.size - 15 || size < 0) return null
        return FileOfferMsg(le32(p, 0).toLong() and 0xffffffffL, p[4].toInt() != 0, size, p.copyOfRange(15, 15 + len).toString(Charsets.UTF_8))
    }

    fun fileChunk(id: Long, offset: Long, data: ByteArray): ByteArray {
        val buf = ByteArray(8 + data.size)
        put32(buf, 0, id.toInt())
        put32(buf, 4, offset.toInt())
        data.copyInto(buf, 8)
        return buf
    }

    fun readFileChunk(p: ByteArray): FileChunkMsg? {
        if (p.size < 8) return null
        return FileChunkMsg(le32(p, 0).toLong() and 0xffffffffL, le32(p, 4).toLong() and 0xffffffffL, p.copyOfRange(8, p.size))
    }

    fun fileAck(id: Long, next: Long): ByteArray {
        val buf = ByteArray(8)
        put32(buf, 0, id.toInt()); put32(buf, 4, next.toInt())
        return buf
    }

    fun readFileAck(p: ByteArray): Pair<Long, Long>? {
        if (p.size < 8) return null
        return (le32(p, 0).toLong() and 0xffffffffL) to (le32(p, 4).toLong() and 0xffffffffL)
    }

    fun fileDone(id: Long, ok: Boolean): ByteArray {
        val buf = ByteArray(5)
        put32(buf, 0, id.toInt())
        buf[4] = if (ok) 1 else 0
        return buf
    }

    fun readFileDone(p: ByteArray): Pair<Long, Boolean>? {
        if (p.size < 5) return null
        return (le32(p, 0).toLong() and 0xffffffffL) to (p[4].toInt() != 0)
    }

    fun pairInit(publicKey: ByteArray, id: String, name: String): ByteArray {
        require(publicKey.size == PublicKeyLength && publicKey[0] == 0x04.toByte())
        val idb = utf8Trim(id, 32)
        val nameb = utf8Trim(name, 96)
        val buf = ByteArray(PublicKeyLength + 2 + idb.size + nameb.size)
        publicKey.copyInto(buf)
        buf[PublicKeyLength] = idb.size.toByte()
        idb.copyInto(buf, PublicKeyLength + 1)
        val n = PublicKeyLength + 1 + idb.size
        buf[n] = nameb.size.toByte()
        nameb.copyInto(buf, n + 1)
        return buf
    }

    fun readPairInit(p: ByteArray): PairInitMsg? {
        if (p.size < PublicKeyLength + 2 || p[0] != 0x04.toByte()) return null
        var o = PublicKeyLength
        val id = readStr(p, o) ?: return null
        o = id.second
        val name = readStr(p, o) ?: return null
        return PairInitMsg(p.copyOfRange(0, PublicKeyLength), id.first, name.first)
    }

    fun pairChallenge(publicKey: ByteArray, nonce: ByteArray): ByteArray {
        val buf = ByteArray(PublicKeyLength + 16)
        publicKey.copyInto(buf)
        nonce.copyInto(buf, PublicKeyLength)
        return buf
    }

    fun readPairChallenge(p: ByteArray): Pair<ByteArray, ByteArray>? {
        if (p.size != PublicKeyLength + 16 || p[0] != 0x04.toByte()) return null
        return p.copyOfRange(0, PublicKeyLength) to p.copyOfRange(PublicKeyLength, p.size)
    }

    fun pairConfirm(publicKey: ByteArray, proof: ByteArray): ByteArray {
        val buf = ByteArray(PublicKeyLength + 32)
        publicKey.copyInto(buf)
        proof.copyInto(buf, PublicKeyLength)
        return buf
    }

    fun readPairConfirm(p: ByteArray): Pair<ByteArray, ByteArray>? {
        if (p.size != PublicKeyLength + 32 || p[0] != 0x04.toByte()) return null
        return p.copyOfRange(0, PublicKeyLength) to p.copyOfRange(PublicKeyLength, p.size)
    }

    fun pairAccept(sessionId: Long, salt: ByteArray, proof: ByteArray): ByteArray {
        val buf = ByteArray(4 + 16 + 32)
        put32(buf, 0, sessionId.toInt())
        salt.copyInto(buf, 4)
        proof.copyInto(buf, 20)
        return buf
    }

    fun readPairAccept(p: ByteArray): Triple<Long, ByteArray, ByteArray>? {
        if (p.size != 52) return null
        return Triple(le32(p, 0).toLong() and 0xffffffffL, p.copyOfRange(4, 20), p.copyOfRange(20, 52))
    }

    fun fragment(msgId: Long, index: Int, count: Int, chunk: ByteArray): ByteArray {
        val buf = ByteArray(8 + chunk.size)
        put32(buf, 0, msgId.toInt()); put16(buf, 4, index); put16(buf, 6, count)
        chunk.copyInto(buf, 8)
        return buf
    }

    fun readFragment(p: ByteArray): FragmentMsg? {
        if (p.size < 8) return null
        return FragmentMsg(le32(p, 0).toLong() and 0xffffffffL, le16(p, 4), le16(p, 6), p.copyOfRange(8, p.size))
    }

    fun token(value: Long): ByteArray {
        val buf = ByteArray(8)
        put64(buf, 0, value)
        return buf
    }

    fun readToken(p: ByteArray): Long? {
        if (p.size != 8) return null
        var v = 0L
        for (i in 0 until 8) v = v or (p.u8(i).toLong() shl (8 * i))
        return v
    }

    private fun readStr(p: ByteArray, o: Int): Pair<String, Int>? {
        if (o >= p.size) return null
        val n = p.u8(o)
        if (o + 1 + n > p.size) return null
        return p.copyOfRange(o + 1, o + 1 + n).toString(Charsets.UTF_8) to (o + 1 + n)
    }
}

data class MouseButtonMsg(val button: Int, val down: Boolean, val x: Int, val y: Int)
data class ScrollMsg(val dx: Int, val dy: Int, val x: Int, val y: Int)
data class KeyMsg(val vk: Int, val hid: Int, val mods: Int, val down: Boolean, val codepoint: Long)
data class EdgeEnterMsg(val edge: Int, val x: Int, val y: Int, val width: Int, val height: Int)
data class EdgeReturnMsg(val hostX: Int, val hostY: Int, val deviceX: Int, val deviceY: Int)
data class PairInitMsg(val publicKey: ByteArray, val id: String, val name: String)
data class FragmentMsg(val msgId: Long, val index: Int, val count: Int, val chunk: ByteArray)
data class FileOfferMsg(val id: Long, val cut: Boolean, val size: Long, val name: String)
data class FileChunkMsg(val id: Long, val offset: Long, val data: ByteArray)
