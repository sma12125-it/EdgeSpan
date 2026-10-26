package com.edgestpan.protocol

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ProtocolTest {
    @Test
    fun mouseMoveMatchesSharedVector() {
        val packet = PacketCodec.encodeClear(PacketType.MouseMove, 0, 0, 1, 0, 0, Payloads.mouseMove(10, -3))
        assertEquals("E5012000000000000100000000000000000000000000000008000A000000FDFFFFFF", packet.toHex())
    }

    @Test
    fun edgeGuardWaitsUntilThePointerPushesPastTheLastPixel() {
        assertFalse(EdgeGuard.crosses(ScreenEdge.Right, 99, 0, 100, 100, 98, 0))
        assertFalse(EdgeGuard.crosses(ScreenEdge.Right, 99, 0, 100, 100, 99, 0))
        assertTrue(EdgeGuard.crosses(ScreenEdge.Right, 100, 0, 100, 100, 99, 0))
        assertFalse(EdgeGuard.crosses(ScreenEdge.Right, 150, 0, 100, 100, 40, 0))
        assertFalse(EdgeGuard.crosses(ScreenEdge.Left, -1, 10, 100, 100, 4, 10))
        assertTrue(EdgeGuard.crosses(ScreenEdge.Left, -1, 10, 100, 100, 0, 10))
        assertFalse(EdgeGuard.crosses(ScreenEdge.Bottom, 0, 99, 100, 100, 0, 98))
        assertTrue(EdgeGuard.crosses(ScreenEdge.Bottom, 0, 100, 100, 100, 0, 99))
        assertTrue(EdgeGuard.crosses(ScreenEdge.Top, 0, -1, 100, 100, 0, 0))
        assertEquals(0 to 0, PointerBounds.clamp(-4, -8, 100, 80))
        assertEquals(99 to 79, PointerBounds.clamp(140, 90, 100, 80))
    }

    @Test
    fun theClearPairingPacketIsSentAgainUntilItIsAnswered() {
        var now = 0L
        val engine = ClientEngine("id", "phone", 1080, 2400) { now }
        engine.startPairing("123456")
        assertEquals(1, engine.out.size)
        engine.out.clear()
        now = 399
        engine.tick(now)
        assertTrue(engine.out.isEmpty())
        now = 400
        engine.tick(now)
        assertEquals(1, engine.out.size)
        assertEquals(PacketType.PairInit, engine.out[0][2])
    }

    @Test
    fun helloIncludesTheNameAfterAFullLengthId() {
        val id = "a".repeat(32)
        val packet = Payloads.hello(2, 7, 1080, 2400, 0, id, "Android")
        assertEquals(10 + 32 + "Android".length, packet.size)
        assertEquals(32, packet[8].toInt() and 0xff)
        assertEquals("Android".length, packet[9 + 32].toInt() and 0xff)
    }

    @Test
    fun aRepeatedChallengeDoesNotFailPairing() {
        val host = EcKeyPair.generate()
        val client = PairingHandshake.client("123456")
        val challenge = Payloads.pairChallenge(host.public, ByteArray(16) { 7 })
        val first = client.consume(PacketType.PairChallenge, challenge)
        assertEquals(1, first.size)
        assertEquals(PacketType.PairConfirm, first[0].type)
        val second = client.consume(PacketType.PairChallenge, challenge)
        assertTrue(second.isEmpty())
        assertFalse(client.failed)
        assertFalse(client.established)
    }

    @Test
    fun pairCodeAcceptsAPinOrALinkAndNothingElse() {
        assertTrue(PairCodes.parse("482913") is PairRequest.Pin)
        assertTrue(PairCodes.parse("edgestpan://pair?v=1&h=10.0.0.8&p=45455&pin=482913&n=Desk") is PairRequest.Direct)
        assertEquals(null, PairCodes.parse("nope"))
        assertEquals(null, PairCodes.parse("12"))
    }

    @Test
    fun relayBindFrameMatchesTheSpecVector() {
        val token = ByteArray(16) { 0x11 }
        val frame = RelayFrame.bind(token, "ab")
        assertEquals("E5520101" + "11".repeat(16) + "026162", frame.toHex())
        val parsed = RelayFrame.parse(frame)
        requireNotNull(parsed)
        assertEquals(RelayFrame.BIND, parsed.op)
        assertEquals("ab", parsed.id)
    }

    @Test
    fun discoveryRoundTripKeepsTheHostPort() {
        val packet = DiscoveryCodec.announce("host1", "Office", 1, 1, 45455, 1920, 1080)
        val decoded = DiscoveryCodec.tryDecode(packet)
        requireNotNull(decoded)
        assertEquals("host1", decoded.id)
        assertEquals(45455, decoded.port)
        assertEquals(1920, decoded.width)
        assertEquals("", decoded.pin)
        val withPin = DiscoveryCodec.announce("host1", "Office", 1, 1, 45455, 1920, 1080, "482913")
        assertEquals("482913", DiscoveryCodec.tryDecode(withPin)?.pin)
    }

    @Test
    fun datagramFramesSurviveASplitRead() {
        val frame = DatagramFrames.encode(byteArrayOf(1, 2, 3, 4))
        val reader = FrameReader()
        reader.append(frame, 3)
        assertEquals(null, reader.take())
        reader.append(frame.copyOfRange(3, frame.size))
        assertContentEquals(byteArrayOf(1, 2, 3, 4), reader.take())
        assertEquals(null, reader.take())
    }

    @Test
    fun pairingLinkKeepsRelayFields() {
        val invite = PairingLink.parse("edgestpan://pair?v=1&h=10.0.0.8&p=45455&pin=482913&n=%D8%A7%D8%AA%D8%A7%D9%82&r=abc&u=http%3A%2F%2F127.0.0.1%3A8787")
        requireNotNull(invite)
        assertEquals("10.0.0.8", invite.host)
        assertEquals(45455, invite.port)
        assertEquals("abc", invite.relayPeer)
        assertEquals("http://127.0.0.1:8787", invite.relayUrl)
    }

    @Test
    fun bothSidesAgreeOnTheSameDirectionKeys() {
        val host = EcKeyPair.generate()
        val client = EcKeyPair.generate()
        val sharedH = host.sharedSecret(client.public)
        val sharedC = client.sharedSecret(host.public)
        assertContentEquals(sharedH, sharedC)
        val salt = ByteArray(16) { it.toByte() }
        val hostKeys = SessionCrypto.derive(sharedH, salt, true)
        val clientKeys = SessionCrypto.derive(sharedC, salt, false)
        assertContentEquals(hostKeys.first, clientKeys.second)
        assertContentEquals(hostKeys.second, clientKeys.first)
        val cipher = SessionCipher(hostKeys.first, hostKeys.second, hostKeys.third, 7, true)
        val other = SessionCipher(clientKeys.first, clientKeys.second, clientKeys.third, 7, false)
        val sealed = cipher.seal(PacketType.MouseMove, 0, Payloads.mouseMove(4, 5), 0, 0)
        val opened = other.open(sealed)
        requireNotNull(opened)
        assertEquals(PacketType.MouseMove, opened.first.type)
        assertEquals(4 to 5, Payloads.readMouseMove(opened.second))
    }

    @Test
    fun hkdfRfc5869Case1() {
        val ikm = ByteArray(22) { 0x0b }
        val salt = byteArrayOf(0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c)
        val info = ByteArray(10) { (0xf0 + it).toByte() }
        val okm = SessionCrypto.hkdf(ikm, salt, info, 42)
        assertEquals("3CB25F25FAACD57A90434F64D0362F2A2D2D0A90CF1A5A4C5DB02D56ECC4C5BF34007208D5B887185865", okm.toHex())
    }

    @Test
    fun clientReturnsWhenThePointerCrosses() {
        val hostKeys = SessionCrypto.derive(ByteArray(32) { 3 }, ByteArray(16) { 9 }, true)
        val clientKeys = SessionCrypto.derive(ByteArray(32) { 3 }, ByteArray(16) { 9 }, false)
        val host = SessionCipher(hostKeys.first, hostKeys.second, hostKeys.third, 9, true)
        val engine = ClientEngine("phone", "Phone", 200, 400, { 1L })
        engine.establishForTest(clientKeys.first, clientKeys.second, clientKeys.third, 9)
        val entered = mutableListOf<ClientEvent>()
        engine.onEvent = { entered += it }
        engine.ingest(host.seal(PacketType.EdgeEnter, PacketFlags.Reliable, Payloads.edgeEnter(ScreenEdge.Left, 4, 20, 200, 400), 0, 0), 1)
        engine.out.clear()
        engine.ingest(host.seal(PacketType.MouseMove, 0, Payloads.mouseMove(-1, 20), 0, 0), 2)
        assertTrue(engine.hasControl)
        engine.ingest(host.seal(PacketType.MouseMove, 0, Payloads.mouseMove(0, 20), 0, 0), 3)
        assertTrue(engine.hasControl)
        assertEquals(0, engine.x)
        engine.out.clear()
        engine.ingest(host.seal(PacketType.MouseMove, 0, Payloads.mouseMove(-1, 20), 0, 0), 4)
        assertFalse(engine.hasControl)
        assertTrue(engine.out.any { PacketCodec.readHeader(it)?.type == PacketType.EdgeReturn })
        val returned = entered.filterIsInstance<ClientEvent.EdgeReturned>()
        assertEquals(ScreenEdge.Left, returned.last().edge)
    }

    @Test
    fun leavingTheTabletReleasesAHeldAlt() {
        val hostKeys = SessionCrypto.derive(ByteArray(32) { 4 }, ByteArray(16) { 2 }, true)
        val clientKeys = SessionCrypto.derive(ByteArray(32) { 4 }, ByteArray(16) { 2 }, false)
        val host = SessionCipher(hostKeys.first, hostKeys.second, hostKeys.third, 11, true)
        val engine = ClientEngine("phone", "Phone", 200, 400, { 1L })
        engine.establishForTest(clientKeys.first, clientKeys.second, clientKeys.third, 11)
        val keys = mutableListOf<ClientEvent.Key>()
        engine.onEvent = { if (it is ClientEvent.Key) keys += it }
        engine.ingest(host.seal(PacketType.EdgeEnter, PacketFlags.Reliable, Payloads.edgeEnter(ScreenEdge.Left, 4, 20, 200, 400), 0, 0), 1)
        engine.ingest(host.seal(PacketType.KeyEvent, PacketFlags.Reliable, Payloads.key(0xA4, 0, 0, true, 0), 0, 0), 2)
        engine.ingest(host.seal(PacketType.EdgeReturn, PacketFlags.Reliable, Payloads.edgeReturn(10, 10, 0, 20), 0, 0), 3)
        assertFalse(engine.hasControl)
        assertTrue(keys.any { !it.down && it.vk == 0xA4 })
        val before = keys.size
        engine.ingest(host.seal(PacketType.KeyEvent, PacketFlags.Reliable, Payloads.key(0x41, 4, 0, true, 65), 0, 0), 4)
        assertEquals(before, keys.size)
    }

    @Test
    fun glowMarksOnlyTheEdgeThePointerLeft() {
        val entered = GlowPolicy.enter(ScreenEdge.Right)
        assertFalse(entered.show)
        assertEquals(ScreenEdge.Right, entered.edge)
        val left = GlowPolicy.leave(entered.edge, finger = false)
        assertTrue(left.show)
        assertEquals(ScreenEdge.Right, left.edge)
        val back = GlowPolicy.enter(ScreenEdge.Top)
        assertFalse(back.show)
        assertEquals(ScreenEdge.Top, back.edge)
        val finger = GlowPolicy.touch(back.edge)
        assertFalse(finger.show)
        assertTrue(finger.finger)
        val leaked = GlowPolicy.leave(finger.edge, finger.finger)
        assertFalse(leaked.show)
        assertEquals(ScreenEdge.Top, leaked.edge)
    }

    @Test
    fun handoffOfferMatchesTheSharedVectorAndAFrameNeedsTheMediaKey() {
        val offer = HandoffPayloads.Offer(1, HandoffPayloads.PcToPhone, 96, 2, 2, 2, 2, HandoffPayloads.CodecH264, ScreenEdge.Right, "A")
        assertEquals("010000000160000000020002000200020001010141", HandoffPayloads.writeOffer(offer).toHex())
        assertEquals(offer, HandoffPayloads.tryOffer(HandoffPayloads.writeOffer(offer)))
        val hostKeys = SessionCrypto.derive(ByteArray(32) { 9 }, ByteArray(16) { 4 }, true)
        val clientKeys = SessionCrypto.derive(ByteArray(32) { 9 }, ByteArray(16) { 4 }, false)
        val host = SessionCipher(hostKeys.first, hostKeys.second, hostKeys.third, 3, true)
        val client = SessionCipher(clientKeys.first, clientKeys.second, clientKeys.third, 3, false)
        val annex = byteArrayOf(0, 0, 0, 1, 0x65, 1, 2, 3)
        val media = SessionCrypto.hkdf(hostKeys.first, hostKeys.third, SessionCrypto.MediaInfo, 32)
        val wire = MediaFrames.protect(media, 1, 7, 11, 123456, 1, annex)
        val opened = MediaFrames.open(client.mediaReceiveKey(), 1, wire)
        assertEquals(7L, opened?.id)
        assertEquals(11L, opened?.frameId)
        assertTrue(annex.contentEquals(opened?.annexb))
        assertEquals(null, MediaFrames.open(client.mediaReceiveKey(), 2, wire))
    }

    @Test
    fun anOfferIsDeliveredOnlyWhenBothSidesAdvertiseHandoff() {
        val hostKeys = SessionCrypto.derive(ByteArray(32) { 5 }, ByteArray(16) { 6 }, true)
        val clientKeys = SessionCrypto.derive(ByteArray(32) { 5 }, ByteArray(16) { 6 }, false)
        val host = SessionCipher(hostKeys.first, hostKeys.second, hostKeys.third, 8, true)
        val engine = ClientEngine("phone", "Phone", 200, 400, { 1L })
        engine.establishForTest(clientKeys.first, clientKeys.second, clientKeys.third, 8)
        val seen = mutableListOf<ClientEvent>()
        engine.onEvent = { seen += it }
        val offer = HandoffPayloads.writeOffer(HandoffPayloads.Offer(4, HandoffPayloads.PcToPhone, 96, 80, 40, 80, 40, HandoffPayloads.CodecH264, ScreenEdge.Right, "Notes"))
        engine.ingest(host.seal(PacketType.Hello, PacketFlags.Reliable, Payloads.hello(1, 0x09, 1920, 1080, 45455, "pc", "PC"), 0, 0), 1)
        engine.ingest(host.seal(PacketType.HandoffOffer, PacketFlags.Reliable, offer, 0, 0), 2)
        assertTrue(seen.none { it is ClientEvent.HandoffOffer })
        assertEquals("handoffPcOff", seen.filterIsInstance<ClientEvent.HandoffRejected>().single().reason)
        engine.setHandoffPc(true)
        engine.ingest(host.seal(PacketType.HandoffOffer, PacketFlags.Reliable, offer, 0, 0), 3)
        val handed = seen.filterIsInstance<ClientEvent.HandoffOffer>()
        assertEquals(1, handed.size)
        assertEquals(4L, handed[0].id)
        assertEquals(ScreenEdge.Right, handed[0].edge)
        assertEquals("Notes", handed[0].title)
    }

    private fun ByteArray.toHex() = joinToString("") { "%02X".format(it) }
}
