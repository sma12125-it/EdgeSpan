package com.edgestpan.protocol

sealed class ClientEvent {
    data class EdgeEntered(val edge: Int, val x: Int, val y: Int) : ClientEvent()
    data class EdgeReturned(val edge: Int) : ClientEvent()
    data class Mouse(val x: Int, val y: Int) : ClientEvent()
    data class Button(val button: Int, val down: Boolean, val x: Int, val y: Int) : ClientEvent()
    data class Scroll(val dx: Int, val dy: Int, val x: Int, val y: Int) : ClientEvent()
    data class Key(val vk: Int, val hid: Int, val mods: Int, val down: Boolean, val codepoint: Long) : ClientEvent()
    data class Clipboard(val text: String) : ClientEvent()
    data class FileOffer(val id: Long, val name: String, val size: Long, val cut: Boolean) : ClientEvent()
    data class FileChunk(val id: Long, val offset: Long, val data: ByteArray) : ClientEvent()
    data class FileAck(val id: Long, val next: Long) : ClientEvent()
    data class FileDone(val id: Long, val ok: Boolean) : ClientEvent()
    data class Locked(val locked: Boolean) : ClientEvent()
    data class HandoffOffer(val id: Long, val dpi: Int, val physW: Int, val physH: Int, val dipW: Int, val dipH: Int, val codec: Int, val edge: Int, val title: String) : ClientEvent()
    data class HandoffGeometry(val id: Long, val x: Int, val y: Int, val physW: Int, val physH: Int) : ClientEvent()
    data class HandoffState(val id: Long, val value: Int) : ClientEvent()
    data class HandoffReturned(val id: Long) : ClientEvent()
    data class HandoffRejected(val id: Long, val reason: String) : ClientEvent()
    data class GlowStyle(val argb: Int, val intensity: Int) : ClientEvent()
    data class HostHello(val caps: Int, val license: Int, val days: Int) : ClientEvent()
}

class ClientEngine(
    val id: String,
    val name: String,
    var screenW: Int,
    var screenH: Int,
    private val clock: () -> Long
) {
    private var pairing: PairingHandshake? = null
    private var cipher: SessionCipher? = null
    private var clearSeq = 0
    private var clearFlight: ByteArray? = null
    private var clearAt = 0L
    private var inFlight = false
    private var flightSeq = 0L
    private var flightAt = 0L
    private var attempts = 0
    private var flight = ByteArray(0)
    private val wait = ArrayDeque<Triple<Byte, Byte, ByteArray>>()
    private var replayInit = false
    private var replayMax = 0L
    private var replayBits = 0L
    private var seenMouse = false
    private var lastMouse = 0L
    var hasControl = false
        private set
    var locked = false
        private set
    var x = 0
        private set
    var y = 0
        private set
    var returnEdge = ScreenEdge.Left
        private set
    /** Diagnostic sink for edge exits and size changes. */
    var exitTrace: ((String) -> Unit)? = null
    val out = ArrayList<ByteArray>()
    var onEvent: (ClientEvent) -> Unit = {}
    private val heldModifiers = HashSet<Int>()
    private var handoffPc = false
    private var peerCaps = 0
    val ready: Boolean get() = cipher != null
    val rejected: Boolean get() = pairing?.failed == true
    fun canHandoff() = handoffPc && (peerCaps and 0x08) != 0

    fun setHandoffPc(on: Boolean) {
        if (handoffPc == on) return
        handoffPc = on
        if (cipher != null) queueReliable(PacketType.Hello, Payloads.hello(2, helloCaps(), screenW, screenH, 0, id, name), clock())
    }

    fun mediaReceiveKey(): ByteArray? = cipher?.mediaReceiveKey()
    fun mediaSendKey(): ByteArray? = cipher?.mediaSendKey()

    fun startPairing(pin: String) {
        pairing = PairingHandshake.client(pin)
        sendClear(pairing!!.buildInit(id, name))
    }

    fun ingest(datagram: ByteArray, now: Long) {
        val header = PacketCodec.readHeader(datagram) ?: return
        if (header.encrypted) {
            val open = cipher?.open(datagram) ?: return
            if (open.first.ack != 0L && open.first.ack == flightSeq) inFlight = false
            val fresh = accept(open.first.sequence)
            if (open.first.reliable) out += seal(PacketType.Ack, 0, ByteArray(0), open.first.sequence, now)
            if (!fresh || open.first.type == PacketType.Ack) return
            if (open.first.type == PacketType.MouseMove) {
                if (seenMouse && open.first.sequence < lastMouse) return
                seenMouse = true
                lastMouse = open.first.sequence
            }
            dispatch(open.first, open.second, now)
            return
        }
        val clear = PacketCodec.decodeClear(datagram) ?: return
        val replies = pairing?.consume(clear.first.type, clear.second) ?: return
        replies.forEach { sendClear(it) }
        val shake = pairing ?: return
        if (shake.established && cipher == null && shake.sendKey != null && shake.receiveKey != null && shake.noncePrefix != null) {
            cipher = SessionCipher(shake.sendKey!!, shake.receiveKey!!, shake.noncePrefix!!, shake.sessionId, false)
            clearFlight = null
            queueReliable(PacketType.Hello, Payloads.hello(2, helloCaps(), screenW, screenH, 0, id, name), now)
        }
    }

    fun tick(now: Long) {
        if (cipher == null) {
            val flight = clearFlight ?: return
            if (now - clearAt >= 400) {
                out += flight
                clearAt = now
            }
            return
        }
        if (inFlight && attempts < 30 && now - flightAt >= 40) {
            out += flight
            flightAt = now
            attempts++
        }
        if (!inFlight && wait.isNotEmpty()) {
            val next = wait.removeFirst()
            hold(next.first, next.second, next.third, now)
        }
    }

    fun updateScreen(w: Int, h: Int) {
        if (w < 1 || h < 1 || (w == screenW && h == screenH)) return
        exitTrace?.invoke("screen-size ${screenW}x$screenH -> ${w}x$h")
        screenW = w
        screenH = h
        x = x.coerceIn(0, w - 1)
        y = y.coerceIn(0, h - 1)
        if (cipher != null) queueReliable(PacketType.Hello, Payloads.hello(2, helloCaps(), screenW, screenH, 0, id, name), clock())
    }

    fun post(type: Byte, payload: ByteArray) {
        if (cipher == null) return
        queueReliable(type, payload, clock())
    }

    fun postFast(type: Byte, payload: ByteArray) {
        if (cipher == null) return
        out += seal(type, 0, payload, 0, clock())
    }

    fun handoffAccept(id: Long, viewW: Int, viewH: Int) {
        if (!canHandoff()) return
        post(PacketType.HandoffAccept, HandoffPayloads.writeAccept(id, HandoffPayloads.CodecH264, viewW, viewH))
    }

    fun handoffMove(id: Long, x: Int, y: Int) {
        if (!canHandoff()) return
        postFast(PacketType.HandoffInput, HandoffPayloads.writeInput(id, HandoffPayloads.InputMove, x, y, 0, false, 0, 0, 0, ""))
    }

    fun handoffButton(id: Long, x: Int, y: Int, button: Int, down: Boolean) {
        if (!canHandoff()) return
        post(PacketType.HandoffInput, HandoffPayloads.writeInput(id, HandoffPayloads.InputButton, x, y, button, down, 0, 0, 0, ""))
    }

    fun handoffWheel(id: Long, x: Int, y: Int, wheelY: Int, ctrl: Boolean = false) {
        if (!canHandoff()) return
        post(PacketType.HandoffInput, HandoffPayloads.writeInput(id, HandoffPayloads.InputWheel, x, y, if (ctrl) HandoffPayloads.WheelCtrl else 0, false, 0, wheelY, 0, ""))
    }

    /** Adaptive-bitrate report: VideoConfig with fps 0 carries delay (low 16 bits) and drops (high 16). */
    fun handoffStats(id: Long, delayMs: Int, dropped: Int) {
        if (!canHandoff()) return
        val packed = ((dropped.coerceIn(0, 0x7FFF).toLong() shl 16) or delayMs.coerceIn(1, 0xFFFF).toLong())
        postFast(PacketType.HandoffVideoConfig, HandoffPayloads.writeVideo(id, packed, 0, false))
    }

    fun handoffKey(id: Long, vk: Int, down: Boolean) {
        if (!canHandoff()) return
        post(PacketType.HandoffInput, HandoffPayloads.writeInput(id, HandoffPayloads.InputKey, 0, 0, 0, down, 0, 0, vk, ""))
    }

    fun handoffText(id: Long, text: String) {
        if (!canHandoff() || text.isEmpty()) return
        post(PacketType.HandoffInput, HandoffPayloads.writeInput(id, HandoffPayloads.InputText, 0, 0, 0, false, 0, 0, 0, text))
    }

    fun handoffGeometry(id: Long, x: Int, y: Int, physW: Int, physH: Int, density: Float = 1f) {
        if (!canHandoff()) return
        val d = if (density > 0.5f) density else 1f
        post(PacketType.HandoffGeometry, HandoffPayloads.writeGeometry(id, x, y, physW, physH, (physW / d).toInt(), (physH / d).toInt()))
    }

    fun handoffState(id: Long, value: Int) {
        if (!canHandoff()) return
        post(PacketType.HandoffState, HandoffPayloads.writeState(id, value))
    }

    fun handoffReturn(id: Long) {
        if (!canHandoff()) return
        post(PacketType.HandoffReturn, HandoffPayloads.writeReturn(id))
    }

    fun handoffVideo(id: Long, idr: Boolean) {
        if (!canHandoff()) return
        post(PacketType.HandoffVideoConfig, HandoffPayloads.writeVideo(id, 0, 0, idr))
    }

    fun releaseControl() {
        if (!hasControl || cipher == null) return
        releaseModifiers()
        hasControl = false
        queueReliable(PacketType.EdgeReturn, Payloads.edgeReturn(Int.MIN_VALUE, Int.MIN_VALUE, x, y), clock())
        onEvent(ClientEvent.EdgeReturned(returnEdge))
    }

    fun establishForTest(send: ByteArray, recv: ByteArray, prefix: ByteArray, sessionId: Long) {
        cipher = SessionCipher(send, recv, prefix, sessionId, false)
    }

    private fun helloCaps() = if (handoffPc) 0x0F else 0x07

    private fun releaseModifiers() {
        val held = heldModifiers.toList()
        heldModifiers.clear()
        for (vk in held) onEvent(ClientEvent.Key(vk, 0, 0, false, 0))
    }

    private fun dispatch(header: PacketHeader, body: ByteArray, now: Long) {
        when (header.type) {
            PacketType.EdgeEnter -> Payloads.readEdgeEnter(body)?.let {
                hasControl = true
                locked = false
                returnEdge = it.edge
                val placed = PointerBounds.clamp(it.x, it.y, screenW, screenH)
                x = placed.first
                y = placed.second
                onEvent(ClientEvent.EdgeEntered(it.edge, x, y))
            }
            PacketType.EdgeReturn -> {
                releaseModifiers()
                hasControl = false
                onEvent(ClientEvent.EdgeReturned(returnEdge))
            }
            PacketType.MouseMove -> if (hasControl) Payloads.readMouseMove(body)?.let {
                val prevX = x
                val prevY = y
                val rawX = it.first
                val rawY = it.second
                val placed = PointerBounds.clamp(rawX, rawY, screenW, screenH)
                x = placed.first
                y = placed.second
                onEvent(ClientEvent.Mouse(x, y))
                if (!locked && EdgeGuard.crosses(returnEdge, rawX, rawY, screenW, screenH, prevX, prevY)) {
                    exitTrace?.invoke("edge-exit device edge=$returnEdge prev=($prevX,$prevY) raw=($rawX,$rawY) size=${screenW}x$screenH")
                    releaseModifiers()
                    hasControl = false
                    queueReliable(PacketType.EdgeReturn, Payloads.edgeReturn(Int.MIN_VALUE, Int.MIN_VALUE, x, y), now)
                    onEvent(ClientEvent.EdgeReturned(returnEdge))
                }
            }
            PacketType.MouseButton -> if (hasControl) Payloads.readMouseButton(body)?.let {
                val placed = PointerBounds.clamp(it.x, it.y, screenW, screenH)
                onEvent(ClientEvent.Button(it.button, it.down, placed.first, placed.second))
            }
            PacketType.MouseScroll -> if (hasControl) Payloads.readScroll(body)?.let {
                val placed = PointerBounds.clamp(it.x, it.y, screenW, screenH)
                onEvent(ClientEvent.Scroll(it.dx, it.dy, placed.first, placed.second))
            }
            PacketType.KeyEvent -> Payloads.readKey(body)?.let {
                val modifier = ModifierKeys.isModifier(it.vk)
                if (modifier && (hasControl || !it.down)) {
                    if (it.down) heldModifiers += it.vk else heldModifiers -= it.vk
                }
                if (hasControl || (!it.down && modifier))
                    onEvent(ClientEvent.Key(it.vk, it.hid, it.mods, it.down, it.codepoint))
            }
            PacketType.Clipboard -> Payloads.readClipboard(body)?.let { onEvent(ClientEvent.Clipboard(it.second)) }
            PacketType.FileOffer -> Payloads.readFileOffer(body)?.let { onEvent(ClientEvent.FileOffer(it.id, it.name, it.size, it.cut)) }
            PacketType.FileChunk -> Payloads.readFileChunk(body)?.let { onEvent(ClientEvent.FileChunk(it.id, it.offset, it.data)) }
            PacketType.FileAck -> Payloads.readFileAck(body)?.let { onEvent(ClientEvent.FileAck(it.first, it.second)) }
            PacketType.FileDone -> Payloads.readFileDone(body)?.let { onEvent(ClientEvent.FileDone(it.first, it.second)) }
            PacketType.Lock -> { locked = true; onEvent(ClientEvent.Locked(true)) }
            PacketType.Unlock -> { locked = false; onEvent(ClientEvent.Locked(false)) }
            PacketType.GlowStyle -> if (body.size >= 4) {
                val argb = (0xFF shl 24) or ((body[0].toInt() and 0xFF) shl 16) or ((body[1].toInt() and 0xFF) shl 8) or (body[2].toInt() and 0xFF)
                onEvent(ClientEvent.GlowStyle(argb, body[3].toInt() and 0xFF))
            }
            PacketType.Hello -> if (body.size >= 2) {
                peerCaps = body.u8(1)
                val license = Payloads.hostLicense(body)
                onEvent(ClientEvent.HostHello(peerCaps, license.first, license.second))
            }
            PacketType.HandoffOffer -> HandoffPayloads.tryOffer(body)?.let {
                if (canHandoff()) onEvent(ClientEvent.HandoffOffer(it.id, it.dpi.toInt(), it.physW, it.physH, it.dipW, it.dipH, it.codec, it.edge, it.title))
                else onEvent(ClientEvent.HandoffRejected(it.id, if (!handoffPc) "handoffPcOff" else "handoffNeedTablet"))
            }
            PacketType.HandoffGeometry -> HandoffPayloads.tryGeometry(body)?.let {
                onEvent(ClientEvent.HandoffGeometry(it.id, it.x, it.y, it.physW, it.physH))
            }
            PacketType.HandoffState -> HandoffPayloads.tryState(body)?.let {
                onEvent(ClientEvent.HandoffState(it.first, it.second))
            }
            PacketType.HandoffReturn -> HandoffPayloads.tryReturn(body)?.let { onEvent(ClientEvent.HandoffReturned(it)) }
            PacketType.Ping -> Payloads.readToken(body)?.let { out += seal(PacketType.Pong, 0, Payloads.token(it), 0, now) }
        }
    }

    private fun queueReliable(type: Byte, payload: ByteArray, now: Long) {
        if (type == PacketType.Clipboard) {
            val kept = wait.filter { it.first != PacketType.Clipboard }
            wait.clear()
            kept.forEach { wait.addLast(it) }
        }
        if (inFlight) wait.addLast(Triple(type, PacketFlags.Reliable, payload))
        else hold(type, PacketFlags.Reliable, payload, now)
    }

    private fun hold(type: Byte, flags: Byte, payload: ByteArray, now: Long) {
        val bytes = seal(type, flags, payload, 0, now)
        inFlight = true
        flightSeq = cipher!!.lastSequence
        flight = bytes
        flightAt = now
        attempts = 1
        out += bytes
    }

    private fun seal(type: Byte, flags: Byte, payload: ByteArray, ack: Long, now: Long) =
        cipher!!.seal(type, flags, payload, ack, now * 1000)

    private fun sendClear(message: PairMessage) {
        clearSeq++
        val bytes = PacketCodec.encodeClear(message.type, 0, 0, clearSeq.toLong(), 0, clock() * 1000, message.payload)
        clearFlight = bytes
        clearAt = clock()
        out += bytes
    }

    private fun accept(sequence: Long): Boolean {
        if (sequence == 0L) return false
        if (!replayInit) {
            replayInit = true
            replayMax = sequence
            replayBits = 1
            return true
        }
        if (sequence > replayMax) {
            val shift = sequence - replayMax
            replayBits = if (shift >= 64) 1 else (replayBits shl shift.toInt()) or 1
            replayMax = sequence
            return true
        }
        val diff = replayMax - sequence
        if (diff >= 64) return false
        val mask = 1L shl diff.toInt()
        if (replayBits and mask != 0L) return false
        replayBits = replayBits or mask
        return true
    }
}
