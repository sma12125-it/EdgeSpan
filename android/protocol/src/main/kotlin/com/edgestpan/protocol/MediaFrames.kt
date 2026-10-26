package com.edgestpan.protocol

import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * One encoded frame on the media plane. The nonce is the frame sequence plus
 * "MED1", so a video packet cannot be replayed as a mouse packet.
 */
object MediaFrames {
    data class Frame(val id: Long, val frameId: Long, val captureUs: Long, val flags: Int, val annexb: ByteArray)

    fun protect(key: ByteArray, sequence: Long, id: Long, frameId: Long, captureUs: Long, flags: Int, annexb: ByteArray): ByteArray {
        val plain = ByteArray(17 + annexb.size)
        put32(plain, 0, id.toInt())
        put32(plain, 4, frameId.toInt())
        put64(plain, 8, captureUs)
        plain[16] = flags.toByte()
        annexb.copyInto(plain, 17)
        val sealed = aes(Cipher.ENCRYPT_MODE, key, sequence).doFinal(plain)
        val body = ByteArray(4 + sealed.size)
        put32(body, 0, sealed.size)
        sealed.copyInto(body, 4)
        return body
    }

    fun open(key: ByteArray, sequence: Long, body: ByteArray): Frame? {
        if (body.size < 4 + 16) return null
        val n = le32(body, 0)
        if (n < 16 || 4 + n != body.size) return null
        val plain = try {
            aes(Cipher.DECRYPT_MODE, key, sequence).doFinal(body, 4, n)
        } catch (_: Exception) {
            return null
        }
        if (plain.size < 17) return null
        return Frame(
            le32(plain, 0).toLong() and 0xffffffffL,
            le32(plain, 4).toLong() and 0xffffffffL,
            leu64(plain, 8),
            plain.u8(16),
            plain.copyOfRange(17, plain.size)
        )
    }

    private fun aes(mode: Int, key: ByteArray, sequence: Long): Cipher {
        val nonce = ByteArray(12)
        put64(nonce, 0, sequence)
        nonce[8] = 'M'.code.toByte()
        nonce[9] = 'E'.code.toByte()
        nonce[10] = 'D'.code.toByte()
        nonce[11] = '1'.code.toByte()
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(mode, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        return cipher
    }
}
