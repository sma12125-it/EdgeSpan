package com.edgestpan.protocol

import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import java.security.interfaces.ECPublicKey
import java.math.BigInteger

class EcKeyPair private constructor(
    private val privateKey: java.security.PrivateKey,
    private val params: java.security.spec.ECParameterSpec,
    val public: ByteArray
) : AutoCloseable {
    fun sharedSecret(peerUncompressed: ByteArray): ByteArray {
        require(peerUncompressed.size == 65 && peerUncompressed[0] == 0x04.toByte())
        val kf = KeyFactory.getInstance("EC")
        val point = ECPoint(BigInteger(1, peerUncompressed.copyOfRange(1, 33)), BigInteger(1, peerUncompressed.copyOfRange(33, 65)))
        val other = kf.generatePublic(ECPublicKeySpec(point, params))
        val ka = KeyAgreement.getInstance("ECDH")
        ka.init(privateKey)
        ka.doPhase(other, true)
        return pad32(ka.generateSecret())
    }

    override fun close() = Unit

    companion object {
        fun generate(): EcKeyPair {
            val gen = KeyPairGenerator.getInstance("EC")
            gen.initialize(ECGenParameterSpec("secp256r1"), SecureRandom())
            val pair = gen.generateKeyPair()
            val params = (pair.private as java.security.interfaces.ECPrivateKey).params
            return EcKeyPair(pair.private, params, export(pair.public as ECPublicKey))
        }

        private fun export(key: ECPublicKey): ByteArray {
            val out = ByteArray(65)
            out[0] = 0x04
            pad32(key.w.affineX.toByteArray()).copyInto(out, 1)
            pad32(key.w.affineY.toByteArray()).copyInto(out, 33)
            return out
        }
    }
}

fun pad32(src: ByteArray): ByteArray {
    if (src.size == 32) return src
    if (src.size == 33 && src[0] == 0.toByte()) return src.copyOfRange(1, 33)
    require(src.size < 32)
    val dst = ByteArray(32)
    src.copyInto(dst, 32 - src.size)
    return dst
}

object SessionCrypto {
    const val PairInfo = "edgestpan-pair-v1"
    const val HostToClient = "edgestpan-h2c-v1"
    const val ClientToHost = "edgestpan-c2h-v1"
    const val NonceInfo = "edgestpan-nonce-v1"
    const val MediaInfo = "edgestpan-media-v1"

    fun hkdf(ikm: ByteArray, salt: ByteArray, info: String, length: Int) =
        hkdf(ikm, salt, info.toByteArray(Charsets.UTF_8), length)

    fun hkdf(ikm: ByteArray, salt: ByteArray, infoBytes: ByteArray, length: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(if (salt.isEmpty()) ByteArray(32) else salt, "HmacSHA256"))
        val prk = mac.doFinal(ikm)
        mac.init(SecretKeySpec(prk, "HmacSHA256"))
        val okm = ByteArray(length)
        var previous = ByteArray(0)
        var offset = 0
        var counter = 1
        while (offset < length) {
            mac.reset()
            mac.update(previous)
            mac.update(infoBytes)
            mac.update(counter.toByte())
            previous = mac.doFinal()
            val n = minOf(previous.size, length - offset)
            previous.copyInto(okm, offset, 0, n)
            offset += n
            counter++
        }
        return okm
    }

    fun pairMacKey(sharedX: ByteArray, pin: String) = hkdf(sharedX, pin.trim().toByteArray(Charsets.UTF_8), PairInfo, 32)

    fun clientProof(macKey: ByteArray, nonce: ByteArray, clientPub: ByteArray, hostPub: ByteArray): ByteArray {
        val data = ByteArray(1 + nonce.size + clientPub.size + hostPub.size)
        data[0] = 0x02
        nonce.copyInto(data, 1)
        clientPub.copyInto(data, 1 + nonce.size)
        hostPub.copyInto(data, 1 + nonce.size + clientPub.size)
        return hmac(macKey, data)
    }

    fun hostProof(macKey: ByteArray, nonce: ByteArray, salt: ByteArray, hostPub: ByteArray, clientPub: ByteArray): ByteArray {
        val data = ByteArray(1 + nonce.size + salt.size + hostPub.size + clientPub.size)
        data[0] = 0x01
        nonce.copyInto(data, 1)
        salt.copyInto(data, 1 + nonce.size)
        hostPub.copyInto(data, 1 + nonce.size + salt.size)
        clientPub.copyInto(data, 1 + nonce.size + salt.size + hostPub.size)
        return hmac(macKey, data)
    }

    fun derive(sharedX: ByteArray, salt: ByteArray, isHost: Boolean): Triple<ByteArray, ByteArray, ByteArray> {
        val h2c = hkdf(sharedX, salt, HostToClient, 32)
        val c2h = hkdf(sharedX, salt, ClientToHost, 32)
        val prefix = hkdf(sharedX, salt, NonceInfo, 4)
        return if (isHost) Triple(h2c, c2h, prefix) else Triple(c2h, h2c, prefix)
    }

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }
}

class SessionCipher(private val sendKey: ByteArray, private val recvKey: ByteArray, prefix: ByteArray, val sessionId: Long, isHost: Boolean) {
    private val prefix = prefix.copyOf(4)
    private val sendDir: Byte = if (isHost) 1 else 2
    private val recvDir: Byte = if (isHost) 2 else 1
    private var seq = 0L
    val lastSequence get() = seq

    fun mediaReceiveKey() = SessionCrypto.hkdf(recvKey, prefix, SessionCrypto.MediaInfo, 32)

    init {
        require(sessionId != 0L)
    }

    fun seal(type: Byte, flags: Byte, payload: ByteArray, ack: Long, timestampUs: Long): ByteArray {
        seq = (seq + 1) and 0xffffffffL
        val marked = (flags.toInt() or PacketFlags.Encrypted.toInt()).toByte()
        val packet = ByteArray(PacketCodec.HeaderSize + payload.size + PacketCodec.TagSize)
        PacketCodec.writeHeader(packet, type, marked, sessionId, seq, ack, timestampUs, payload.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(sendKey, "AES"), GCMParameterSpec(128, nonce(sendDir, seq)))
        cipher.updateAAD(packet, 0, PacketCodec.HeaderSize)
        val out = cipher.doFinal(payload)
        out.copyInto(packet, PacketCodec.HeaderSize)
        return packet
    }

    fun open(datagram: ByteArray): Pair<PacketHeader, ByteArray>? {
        val header = PacketCodec.readHeader(datagram) ?: return null
        if (!header.encrypted || header.sessionId != sessionId) return null
        val total = PacketCodec.HeaderSize + header.payloadLength + PacketCodec.TagSize
        if (datagram.size < total) return null
        return try {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(recvKey, "AES"), GCMParameterSpec(128, nonce(recvDir, header.sequence)))
            cipher.updateAAD(datagram, 0, PacketCodec.HeaderSize)
            val body = datagram.copyOfRange(PacketCodec.HeaderSize, total)
            header to cipher.doFinal(body)
        } catch (_: Exception) {
            null
        }
    }

    private fun nonce(direction: Byte, sequence: Long): ByteArray {
        val nonce = ByteArray(12)
        prefix.copyInto(nonce)
        nonce[4] = direction
        put32(nonce, 5, sequence.toInt())
        return nonce
    }
}

@Suppress("unused")
private val sha256 = MessageDigest.getInstance("SHA-256")
