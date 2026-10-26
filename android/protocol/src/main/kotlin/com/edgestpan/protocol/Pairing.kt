package com.edgestpan.protocol

import java.security.SecureRandom

data class PairMessage(val type: Byte, val payload: ByteArray)

class PairingHandshake private constructor(private val pin: String, private val local: EcKeyPair, private val sessionSeed: Long) {
    private var peerPub: ByteArray? = null
    private var nonce: ByteArray? = null
    private var shared: ByteArray? = null
    private var stage = 0
    var established = false
        private set
    var failed = false
        private set
    var sendKey: ByteArray? = null
        private set
    var receiveKey: ByteArray? = null
        private set
    var noncePrefix: ByteArray? = null
        private set
    var sessionId: Long = 0
        private set
    val publicKey: ByteArray = local.public

    fun buildInit(id: String, name: String) = PairMessage(PacketType.PairInit, Payloads.pairInit(publicKey, id, name))

    fun consume(type: Byte, payload: ByteArray): List<PairMessage> {
        if (stage == 3 || failed) return emptyList()
        return try {
            when (type) {
                PacketType.PairChallenge -> onChallenge(payload)
                PacketType.PairAccept -> onAccept(payload)
                PacketType.PairReject -> { fail(); emptyList() }
                else -> emptyList()
            }
        } catch (_: Exception) {
            fail(); emptyList()
        }
    }

    private fun onChallenge(payload: ByteArray): List<PairMessage> {
        val parsed = Payloads.readPairChallenge(payload) ?: return emptyList()
        if (stage != 0) {
            if (peerPub != null && peerPub.contentEquals(parsed.first)) return emptyList()
            fail()
            return emptyList()
        }
        peerPub = parsed.first
        nonce = parsed.second
        shared = local.sharedSecret(parsed.first)
        val mac = SessionCrypto.pairMacKey(shared!!, pin)
        val proof = SessionCrypto.clientProof(mac, parsed.second, publicKey, parsed.first)
        stage = 1
        return listOf(PairMessage(PacketType.PairConfirm, Payloads.pairConfirm(publicKey, proof)))
    }

    private fun onAccept(payload: ByteArray): List<PairMessage> {
        if (stage != 1 || shared == null || nonce == null || peerPub == null) { fail(); return emptyList() }
        val parsed = Payloads.readPairAccept(payload) ?: return failList()
        val mac = SessionCrypto.pairMacKey(shared!!, pin)
        val expect = SessionCrypto.hostProof(mac, nonce!!, parsed.second, peerPub!!, publicKey)
        if (!expect.contentEquals(parsed.third)) { fail(); return listOf(PairMessage(PacketType.PairReject, byteArrayOf(1))) }
        sessionId = parsed.first
        val derived = SessionCrypto.derive(shared!!, parsed.second, false)
        sendKey = derived.first
        receiveKey = derived.second
        noncePrefix = derived.third
        established = true
        stage = 3
        return emptyList()
    }

    private fun failList(): List<PairMessage> { fail(); return emptyList() }
    private fun fail() { failed = true; established = false; stage = 4 }

    companion object {
        fun client(pin: String): PairingHandshake {
            require(pin.trim().length >= 4)
            return PairingHandshake(pin.trim(), EcKeyPair.generate(), 0)
        }

        fun randomBytes(n: Int): ByteArray = ByteArray(n).also { SecureRandom().nextBytes(it) }
    }
}
