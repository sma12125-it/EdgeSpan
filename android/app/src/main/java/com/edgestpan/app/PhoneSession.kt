package com.edgestpan.app

import android.content.Context
import android.content.Intent
import android.hardware.input.InputManager
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import android.view.InputDevice
import com.edgestpan.protocol.ClientEngine
import com.edgestpan.protocol.ClientEvent
import com.edgestpan.protocol.GlowPolicy
import com.edgestpan.protocol.DatagramFrames
import com.edgestpan.protocol.DiscoveryCodec
import com.edgestpan.protocol.FrameReader
import com.edgestpan.protocol.PairCodes
import com.edgestpan.protocol.PairRequest
import org.json.JSONObject
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Pairs with a desktop from a 4–8 digit code or a full link, on the LAN or through the relay.
 * Network failures stay on this thread and become a status the UI can show.
 */
class PhoneSession(
    private val context: Context,
    private val stop: AtomicBoolean,
    private val report: (String, String) -> Unit
) {
    private var failureKey = "pairTimeout"
    private var failureText = "No computer answered. Open pairing on the PC and try again."

    fun run() {
        val prefs = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE)
        val strings = loadStrings(prefs.getString("lang", "en") ?: "en")
        try {
            val method = prefs.getString("method", "") ?: ""
            val name = prefs.getString("name", "Android") ?: "Android"
            val id = prefs.getString("id", null) ?: UUID.randomUUID().toString().replace("-", "").also {
                prefs.edit().putString("id", it).apply()
            }
            DisplaySize.ensure(context)
            val width = DisplaySize.width.coerceAtLeast(1)
            val height = DisplaySize.height.coerceAtLeast(1)
            if (method == "usb") {
                runUsb(prefs, strings, id, name, width, height)
                return
            }
            if (method == "lan") {
                say(strings, "working", "pairWorking", "Looking for the computer…")
                val heard = HostFinder.listen()
                rememberPeers(heard)
                val announced = heard.firstOrNull { it.pin.length in 4..8 }
                if (announced != null) prefs.edit().putString("link", announced.pin).apply()
            }
            val request = PairCodes.parse(prefs.getString("link", ""))
            if (request == null && method != "relay") {
                say(strings, "error", "pairNeedCode", "Enter the code from the computer, or paste the pairing link.")
                return
            }
            val pin = when (request) {
                is PairRequest.Pin -> request.pin
                is PairRequest.Direct -> request.invite.pin
                null -> ""
            }
            val invite = (request as? PairRequest.Direct)?.invite
            val relayUrl = invite?.relayUrl?.ifBlank { null } ?: prefs.getString("relay", "").orEmpty()
            if (method != "lan") say(strings, "working", "pairWorking", "Looking for the computer…")

            var live: Live? = null
            if (method == "relay") {
                if (relayUrl.isBlank()) {
                    say(strings, "error", "pairRelayDown", "The relay did not answer. Check the relay address.")
                    return
                }
                say(strings, "working", "pairWorkingRelay", "Contacting the relay…")
                val typed = prefs.getString("direct", "").orEmpty()
                val typedHost = typed.substringBefore(':').trim()
                val typedPort = typed.substringAfter(':', "").toIntOrNull() ?: 45455
                if (!invite?.host.isNullOrBlank()) {
                    live = direct(invite!!.host, invite.port, pin, id, name, width, height, 4000)
                }
                if (live == null && typedHost.isNotBlank() && typed.contains(':')) {
                    live = direct(typedHost, typedPort, pin, id, name, width, height, 4000)
                }
                if (live == null) {
                    live = relay(relayUrl, invite?.relayPeer.orEmpty(), pin, id, name, width, height, preferFresh = true, stun = prefs.getString("stun", "").orEmpty())
                }
                val kind = if (live?.viaRelay == true) "relay" else "direct"
                prefs.edit().putString("path", kind).apply()
                finishLive(live, strings, kind, width, height)
                return
            }
            if (!invite?.host.isNullOrBlank()) {
                live = direct(invite!!.host, invite.port, pin, id, name, width, height)
                if (live == null && failureKey == "pairBadCode") {
                    say(strings, "error", failureKey, failureText)
                    return
                }
            }
            if (live == null) {
                live = discover(pin, id, name, width, height)
                if (live == null && failureKey == "pairBadCode") {
                    say(strings, "error", failureKey, failureText)
                    return
                }
            }
            if (live == null && method != "lan" && relayUrl.isNotBlank()) {
                say(strings, "working", "pairWorkingRelay", "Contacting the relay…")
                live = relay(relayUrl, invite?.relayPeer.orEmpty(), pin, id, name, width, height)
            }
            if (live == null) {
                say(strings, "error", failureKey, failureText)
                return
            }
            finishLive(live, strings, if (live.viaRelay) "relay" else "lan", width, height)
        } catch (thrown: Throwable) {
            Log.e(TAG, "session", thrown)
            report("error", friendly(thrown, strings))
        }
    }

    private fun runUsb(prefs: android.content.SharedPreferences, strings: Map<String, String>, id: String, name: String, width: Int, height: Int) {
        say(strings, "working", "usbSearching", "Looking for the computer over USB…")
        var live: Live? = null
        val deadline = SystemClock.elapsedRealtime() + 45_000
        while (SystemClock.elapsedRealtime() < deadline && !stop.get()) {
            val pinNow = prefs.getString("link", "")?.trim().orEmpty()
            if (pinNow.isBlank()) {
                fail("pairNeedCode", "Enter the code from the computer, or paste the pairing link.")
                try { Thread.sleep(400) } catch (_: InterruptedException) { break }
                continue
            }
            live = usb(pinNow, id, name, width, height)
            if (live != null) break
            if (failureKey == "usbNoTunnel" || failureKey == "pairBadCode" || failureKey == "pairTimeout") {
                try { Thread.sleep(700) } catch (_: InterruptedException) { break }
                continue
            }
            break
        }
        if (live == null) {
            say(strings, "error", failureKey, strings[failureKey] ?: failureText)
            return
        }
        finishLive(live, strings, "usb", width, height)
    }

    private fun finishLive(live: Live?, strings: Map<String, String>, method: String, width: Int, height: Int) {
        if (live == null) {
            say(strings, "error", failureKey, strings[failureKey] ?: failureText)
            return
        }
        val okKey = when (method) {
            "usb" -> "pairOkUsb"
            "relay" -> "pairOkRelay"
            "direct" -> "pairOkDirect"
            else -> "pairOkLan"
        }
        val okText = when (method) {
            "usb" -> "Connected over USB."
            "relay" -> "Connected through the relay."
            "direct" -> "Connected directly over the internet."
            else -> "Connected on the local network."
        }
        say(strings, "ok", okKey, okText)
        Log.i(TAG, "pairing-ok $method")
        live.pump(stop, width, height, method)
        live.close()
    }

    private fun usb(pin: String, id: String, name: String, width: Int, height: Int): Live? {
        val socket = Socket()
        try {
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress("127.0.0.1", 45455), 2500)
        } catch (_: Exception) {
            runCatching { socket.close() }
            fail("usbNoTunnel", "The USB tunnel is not open. Connect the cable and allow USB debugging.")
            return null
        }
        socket.soTimeout = 80
        val input = socket.getInputStream()
        val output = socket.getOutputStream()
        val reader = FrameReader()
        val scratch = ByteArray(8192)
        val live = Live(false, engine(id, name, width, height), { bytes ->
            val frame = DatagramFrames.encode(bytes)
            output.write(frame)
            output.flush()
        }, { timeout ->
            val buffered = reader.take()
            if (buffered != null) buffered
            else if (readFrame(socket, input, reader, scratch, timeout) == null) null
            else reader.take()
        }, socket::close)
        live.engine.startPairing(pin)
        return finish(live, 12_000)
    }

    private fun direct(host: String, port: Int, pin: String, id: String, name: String, width: Int, height: Int, timeoutMs: Long = 20_000): Live? {
        val address = try {
            InetAddress.getByName(host)
        } catch (_: UnknownHostException) {
            fail("pairUnresolved", "That computer address could not be found.")
            return null
        }
        val socket = DatagramSocket()
        socket.soTimeout = 80
        val live = Live(false, engine(id, name, width, height), { bytes ->
            socket.send(DatagramPacket(bytes, bytes.size, address, port))
        }, { timeout -> receive(socket, timeout) }, socket::close)
        live.engine.startPairing(pin)
        return finish(live, timeoutMs)
    }

    private fun discover(pin: String, id: String, name: String, width: Int, height: Int): Live? {
        val targets = LinkedHashSet<InetSocketAddress>()
        listenForHosts().forEach { targets += it }
        if (isEmulator()) targets += InetSocketAddress("10.0.2.2", 45455)
        val socket = DatagramSocket()
        socket.broadcast = true
        socket.soTimeout = 80
        targets += InetSocketAddress("255.255.255.255", 45455)
        val live = Live(false, engine(id, name, width, height), { bytes ->
            for (target in targets) {
                try {
                    socket.send(DatagramPacket(bytes, bytes.size, target.address, target.port))
                } catch (_: IOException) {
                }
            }
        }, { timeout -> receive(socket, timeout) }, socket::close)
        live.engine.startPairing(pin)
        return finish(live, 20_000)
    }

    private fun readFrame(socket: Socket, input: java.io.InputStream, reader: FrameReader, scratch: ByteArray, timeout: Int): ByteArray? {
        socket.soTimeout = timeout.coerceAtLeast(20)
        return try {
            val n = input.read(scratch)
            if (n <= 0) null
            else {
                reader.append(scratch, n)
                ByteArray(0)
            }
        } catch (_: SocketTimeoutException) {
            null
        } catch (_: IOException) {
            null
        }
    }

    private fun relay(url: String, knownPeer: String, pin: String, id: String, name: String, width: Int, height: Int, preferFresh: Boolean = false, stun: String = ""): Live? {
        val channel = try {
            RelayChannel(url).also { it.connect(name) }
        } catch (thrown: Throwable) {
            Log.e(TAG, "relay", thrown)
            fail("pairRelayDown", thrown.message ?: "The relay did not answer. Check the relay address.")
            return null
        }
        var peers = if (knownPeer.isNotBlank()) listOf(knownPeer) else channel.hosts()
        val peerDeadline = SystemClock.elapsedRealtime() + if (preferFresh) 12_000 else 0
        while (peers.isEmpty() && SystemClock.elapsedRealtime() < peerDeadline && !stop.get()) {
            try { Thread.sleep(400) } catch (_: InterruptedException) { break }
            peers = channel.hosts()
        }
        if (peers.isEmpty()) {
            channel.close()
            fail("pairNoHost", "No computer is connected to this relay. Turn on internet mode on the PC.")
            return null
        }
        var activePin = pin
        if (preferFresh || activePin.isBlank()) {
            val fresh = channel.awaitPin(peers, id, if (activePin.isBlank()) 8_000 else 2_500)
            if (!fresh.isNullOrBlank()) activePin = fresh
        }
        if (activePin.isBlank()) {
            channel.close()
            fail("pairNeedCode", "Enter the code from the computer, or paste the pairing link.")
            return null
        }
        val punched = punchDirect(channel, url, peers, id, activePin, name, width, height, stun)
        if (punched != null) return punched
        var rejected = false
        for (peer in peers) {
            val live = Live(true, engine(id, name, width, height), { bytes -> channel.send(peer, bytes) }, channel::poll) {}
            live.engine.startPairing(activePin)
            val ready = finish(live, 20_000)
            if (ready != null) return Live(true, ready.engine, ready.send, ready.poll, channel::close)
            if (failureKey == "pairBadCode") {
                rejected = true
                break
            }
        }
        channel.close()
        if (rejected) fail("pairBadCode", "That code was not accepted. Start pairing again on the computer.")
        else fail("pairTimeout", "No computer answered. Open pairing on the PC and check the relay address.")
        return null
    }

    private fun punchDirect(channel: RelayChannel, url: String, peers: List<String>, id: String, pin: String, name: String, width: Int, height: Int, stun: String): Live? {
        val punch = DirectPunch()
        try {
            val relayHost = runCatching {
                val text = url.trim()
                java.net.URI(if (text.contains("://")) text else "http://$text").host
            }.getOrNull()
            val (stunHost, stunPort) = splitStun(stun, relayHost)
            val mapped = runCatching { punch.stun(stunHost, stunPort) }.getOrNull()
            val lan = punch.lan()?.let { "$it:${punch.localPort}" }.orEmpty()
            val mappedText = mapped?.address?.hostAddress?.let { "$it:${mapped.port}" }.orEmpty()
            val body = org.json.JSONObject().put("t", "announce").put("id", id).put("lan", lan).put("mapped", mappedText).toString()
            peers.forEach { channel.signal(it, body) }
            val targets = LinkedHashSet<java.net.InetSocketAddress>()
            val deadline = SystemClock.elapsedRealtime() + 2500
            while (SystemClock.elapsedRealtime() < deadline && !stop.get() && punch.probeHits.isEmpty()) {
                val json = channel.pollJson(180)
                val data = json?.optString("data").orEmpty()
                if (json?.optString("op") == "signal" && data.contains("announce")) {
                    val announce = org.json.JSONObject(data)
                    endpoint(announce.optString("mapped"))?.let { targets += it }
                    endpoint(announce.optString("lan"))?.let { targets += it }
                }
                if (targets.isNotEmpty()) punch.probe(targets.toList())
                punch.pollSession()
            }
            if (punch.probeHits.isEmpty() || targets.isEmpty()) {
                punch.close()
                return null
            }
            val list = targets.toList()
            val live = Live(false, engine(id, name, width, height), { bytes ->
                punch.probe(list)
                punch.send(bytes, list)
            }, { punch.pollSession() }, {})
            live.engine.startPairing(pin)
            val ready = finish(live, 4000)
            if (ready != null) {
                return Live(false, ready.engine, { bytes -> punch.send(bytes, list) }, { punch.pollSession() }, {
                    punch.close()
                    channel.close()
                })
            }
            punch.close()
            return null
        } catch (thrown: Throwable) {
            Log.e(TAG, "punch", thrown)
            punch.close()
            return null
        }
    }

    private fun splitStun(stun: String, relayHost: String?): Pair<String, Int> {
        val text = stun.trim()
        if (text.isBlank()) return (relayHost ?: "") to 8788
        val host = text.substringBefore(':')
        val port = text.substringAfter(':', "3478").toIntOrNull() ?: 3478
        return host to port
    }

    private fun endpoint(text: String): java.net.InetSocketAddress? {
        if (!text.contains(':')) return null
        val host = text.substringBeforeLast(':')
        val port = text.substringAfterLast(':').toIntOrNull() ?: return null
        if (host.isBlank() || port !in 1..65535) return null
        return try {
            java.net.InetSocketAddress(java.net.InetAddress.getByName(host), port)
        } catch (_: Exception) {
            null
        }
    }

    private fun finish(live: Live, timeoutMs: Long): Live? {
        val start = SystemClock.elapsedRealtime()
        while (SystemClock.elapsedRealtime() - start < timeoutMs && !stop.get()) {
            val now = SystemClock.elapsedRealtime()
            live.flush()
            live.poll(80)?.let { live.engine.ingest(it, now) }
            live.engine.tick(now)
            if (live.engine.ready) return live
            if (live.engine.rejected) {
                live.close()
                fail("pairBadCode", "That code was not accepted. Start pairing again on the computer.")
                return null
            }
        }
        live.close()
        fail("pairTimeout", "No computer answered. Open pairing on the PC and try again.")
        return null
    }

    private fun listenForHosts(): List<InetSocketAddress> {
        val found = mutableListOf<InetSocketAddress>()
        val socket = try {
            DatagramSocket(null).also {
                it.reuseAddress = true
                it.soTimeout = 400
                it.bind(InetSocketAddress(45454))
            }
        } catch (_: IOException) {
            return found
        }
        val deadline = SystemClock.elapsedRealtime() + 1500
        val buf = ByteArray(512)
        while (SystemClock.elapsedRealtime() < deadline) {
            try {
                val packet = DatagramPacket(buf, buf.size)
                socket.receive(packet)
                val host = DiscoveryCodec.tryDecode(packet.data.copyOf(packet.length)) ?: continue
                val port = if (host.port == 0) packet.port else host.port
                found += InetSocketAddress(packet.address, port)
            } catch (_: SocketTimeoutException) {
            } catch (_: IOException) {
                break
            }
        }
        socket.close()
        return found
    }

    private fun receive(socket: DatagramSocket, timeoutMs: Int): ByteArray? {
        socket.soTimeout = timeoutMs.coerceAtLeast(20)
        val buf = ByteArray(65535)
        val packet = DatagramPacket(buf, buf.size)
        return try {
            socket.receive(packet)
            packet.data.copyOf(packet.length)
        } catch (_: SocketTimeoutException) {
            null
        } catch (_: IOException) {
            null
        }
    }

    private fun engine(id: String, name: String, width: Int, height: Int) =
        ClientEngine(id, name, width, height) { SystemClock.elapsedRealtime() }

    private var hailed = false

    private fun say(strings: Map<String, String>, state: String, key: String, fallback: String) {
        if (state == "ok" && !hailed) {
            hailed = true
            cue()
        }
        report(state, strings[key] ?: fallback)
        Log.i(TAG, "$state $key")
    }

    /** Haptics and the optional tone never decide whether the session stays up. */
    private fun cue() {
        try {
            Haptics.tick(context)
        } catch (thrown: SecurityException) {
            Log.w(TAG, "haptic", thrown)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "haptic", thrown)
        }
        try {
            EdgeTone.play(context)
        } catch (thrown: SecurityException) {
            Log.w(TAG, "tone", thrown)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "tone", thrown)
        }
    }

    private fun fail(key: String, text: String) {
        failureKey = key
        failureText = text
    }

    private fun friendly(thrown: Throwable, strings: Map<String, String>): String {
        val key = when (thrown) {
            is UnknownHostException -> "pairUnresolved"
            is IOException -> "pairRelayDown"
            else -> return thrown.message ?: strings["pairTimeout"] ?: failureText
        }
        return strings[key] ?: thrown.message ?: failureText
    }

    private fun loadStrings(lang: String): Map<String, String> = try {
        val root = JSONObject(context.assets.open("strings.json").bufferedReader().readText())
        buildMap {
            val keys = root.keys()
            while (keys.hasNext()) {
                val key = keys.next()
                val item = root.getJSONObject(key)
                put(key, if (item.has(lang)) item.getString(lang) else item.optString("en", key))
            }
        }
    } catch (_: Exception) {
        emptyMap()
    }

    private fun isEmulator(): Boolean {
        val fingerprint = android.os.Build.FINGERPRINT.lowercase()
        val hardware = android.os.Build.HARDWARE.lowercase()
        return fingerprint.contains("generic") || fingerprint.contains("emulator") || hardware.contains("ranchu") || hardware.contains("goldfish")
    }

    private fun rememberPeers(seen: List<HostFinder.Seen>) {
        val text = seen.joinToString("\n") { "${it.name}|${it.host}|${it.port}|${it.pin}" }
        context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE).edit().putString("peers", text).apply()
    }

    private inner class Live(
        val viaRelay: Boolean,
        val engine: ClientEngine,
        val send: (ByteArray) -> Unit,
        val poll: (Int) -> ByteArray?,
        val closeAction: () -> Unit
    ) {
        fun flush() {
            val copy = engine.out.toList()
            engine.out.clear()
            copy.forEach { runCatching { send(it) } }
        }

        fun pump(stop: AtomicBoolean, width: Int, height: Int, method: String) {
            val prefs = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE)
            val backend = prefs.getString("backend", "")
            val adbAllowed = method == "usb" || backend == "adb"
            val noPath = java.util.concurrent.atomic.AtomicReference<String?>(null)
            val router = InjectorRouter(
                context,
                adbAllowed,
                { DisplaySize.ensure(context); Pair(if (DisplaySize.width > 0) DisplaySize.width else width, if (DisplaySize.height > 0) DisplaySize.height else height) },
                { edgeSpanMouse() },
                { reason -> noPath.compareAndSet(null, reason) }
            )
            val uhid = adbAllowed && router.primeAdb()
            if (uhid) waitForMouse(stop)
            CrashLog.note(context, "session method=$method backend=$backend uhid=$uhid mouse=${edgeSpanMouse()} access=${EdgeAccessibility.instance != null} accessEnabled=${EdgeAccessibility.isEnabled(context)}")
            val injector: Injector = router
            var sentAt = 0L
            var lastLatencyNote = 0L
            var buttons = 0
            var wheels = 0
            val counter = InputCounter(context)
            val trace = stringsTemplate()
            val glowOn = { prefs.getBoolean("edgeGlow", true) }
            val filesOn = { !prefs.contains("files") || prefs.getBoolean("files", true) }
            val clipOn = { !prefs.contains("clipboard") || prefs.getBoolean("clipboard", true) }
            val glow = EdgeGlowOverlay(context)
            var local = false
            var quietUntil = 0L
            var glowState = GlowPolicy.Decision(show = false, edge = 0, finger = false)
            var lastSentClip = ""
            val pipe = FilePipe(engine, ClipHub.receivedDir(context)) { fileName, done, total, phase ->
                Log.i(TAG, "file $phase $fileName $done/$total")
                if (phase == "ready" || phase == "moved" || phase == "failed") {
                    report("ok", prefs.getString("pairDetail", "") ?: fileName)
                }
            }
            fun publishTrace() {
                val line = trace.replace("{0}", buttons.toString()).replace("{1}", wheels.toString())
                prefs.edit().putInt("inputButtons", buttons).putInt("inputWheels", wheels).apply()
                if (prefs.getBoolean("diagnostics", false)) counter.update(line)
                Log.d(TAG, "input-trace buttons=$buttons wheels=$wheels")
            }
            EdgeGlowOverlay.serverFinger = {
                glow.hide()
                glowState = GlowPolicy.touch(glowState.edge)
                if (!local) {
                    local = true
                    quietUntil = SystemClock.elapsedRealtime() + 400
                    injector.hidePointer()
                    engine.releaseControl()
                    flush()
                }
            }
            glow.onFinger = { EdgeGlowOverlay.serverFinger() }
            engine.setHandoffPc(wantsHandoff(prefs))
            val lang = prefs.getString("lang", "en") ?: "en"
            val viewer = HandoffViewer(context, engine, method, loadStrings(lang), { flush() }) { key ->
                handoffFailed(prefs, lang, key)
            }
            engine.onEvent = onEvent@{ event ->
                pipe.onEvent(event)
                val now = SystemClock.elapsedRealtime()
                when (event) {
                    is ClientEvent.EdgeEntered -> {
                        Log.i(TAG, "edge-enter edge=${event.edge} x=${event.x} y=${event.y}")
                        glowState = GlowPolicy.enter(event.edge)
                        local = false
                        try {
                            if (Motion.breakthrough(context)) {
                                glow.ripple(event.edge)
                                cue()
                            } else glow.hide()
                        } catch (thrown: Throwable) {
                            CrashLog.caught(context, "edge-enter effects", thrown)
                        }
                    }
                    is ClientEvent.EdgeReturned -> {
                        Log.i(TAG, "edge-return edge=${event.edge}")
                        injector.releaseModifiers()
                        glowState = GlowPolicy.leave(event.edge, glowState.finger)
                        local = true
                        injector.hidePointer()
                        if (glowState.show && glowOn()) {
                            glow.show(glowState.edge)
                            if (Motion.breakthrough(context)) cue()
                        } else glow.hide()
                    }
                    is ClientEvent.Button -> {
                        buttons++
                        publishTrace()
                        EdgeGlowOverlay.ignore()
                    }
                    is ClientEvent.Scroll -> {
                        wheels++
                        publishTrace()
                        EdgeGlowOverlay.ignore()
                    }
                    is ClientEvent.Clipboard -> if (clipOn()) ClipHub.putText(context, event.text)
                    is ClientEvent.HandoffOffer -> {
                        DiagFile.append(context, "handoff offer ${event.id} ${event.title}")
                        cue()
                        viewer.offer(event)
                    }
                    is ClientEvent.HandoffRejected -> handoffFailed(prefs, lang, event.reason)
                    is ClientEvent.HandoffGeometry -> viewer.geometry(event)
                    is ClientEvent.HandoffState -> viewer.state(event)
                    is ClientEvent.HandoffReturned -> viewer.returned(event)
                    is ClientEvent.HostHello -> {
                        if ((event.caps and 0x08) != 0 && wantsHandoff(prefs)) engine.setHandoffPc(true)
                        prefs.edit().putInt("hostLicense", event.license).putInt("hostLicenseDays", event.days).apply()
                        context.sendBroadcast(Intent(SessionService.ACTION_STATUS).setPackage(context.packageName))
                    }
                    else -> Unit
                }
                if (local && now < quietUntil && event !is ClientEvent.EdgeEntered) return@onEvent
                if (!local || event is ClientEvent.EdgeEntered || event is ClientEvent.Key) {
                    try {
                        injector.onEvent(event)
                    } catch (thrown: Throwable) {
                        if (event is ClientEvent.EdgeEntered) {
                            CrashLog.caught(context, "edge-enter inject", thrown)
                            noPath.compareAndSet(null, "error")
                        } else Log.e(TAG, "inject", thrown)
                    }
                }
            }
            val clips = context.getSystemService(android.content.ClipboardManager::class.java)
            clips?.addPrimaryClipChangedListener { ClipHub.onClipboardChanged() }
            while (!stop.get()) {
                noPath.getAndSet(null)?.let { reason ->
                    // No working injection path: never pretend we have control.
                    InjectAlert.show(context, reason)
                    glow.hide()
                    injector.hidePointer()
                    if (engine.hasControl) {
                        local = true
                        engine.releaseControl()
                        flush()
                    }
                }
                engine.setHandoffPc(wantsHandoff(prefs))
                viewer.drain()
                val now = SystemClock.elapsedRealtime()
                if (clipOn() && ClipHub.pendingRead) ClipHub.readIfAllowed(context)
                if (clipOn()) ClipHub.pollText()?.let { text ->
                    if (text != lastSentClip) {
                        lastSentClip = text
                        engine.post(com.edgestpan.protocol.PacketType.Clipboard, com.edgestpan.protocol.Payloads.clipboard(now, text.toByteArray()))
                    }
                }
                if (filesOn()) ClipHub.pollFile()?.let { pipe.offer(it.name, it.bytes, it.cut, it.delete) }
                if (filesOn()) pipe.pump()
                val pending = engine.out.isNotEmpty()
                flush()
                if (pending) sentAt = now
                val incoming = poll(40)
                if (incoming != null) {
                    val seen = SystemClock.elapsedRealtime()
                    if (sentAt > 0) {
                        val dt = seen - sentAt
                        if (dt in 1..2000) {
                            prefs.edit().putInt("latencyMs", dt.toInt()).apply()
                            if (seen - lastLatencyNote > 1500) {
                                lastLatencyNote = seen
                                report("ok", prefs.getString("pairDetail", "") ?: "")
                            }
                        }
                        sentAt = 0
                    }
                    engine.ingest(incoming, seen)
                }
                engine.tick(now)
                viewer.tick(engine.x, engine.y, engine.hasControl)
                DisplaySize.ensure(context)
                val dw = DisplaySize.width
                val dh = DisplaySize.height
                if (dw > 0 && dh > 0 && (dw != engine.screenW || dh != engine.screenH)) {
                    engine.updateScreen(dw, dh)
                    router.resize(dw, dh)
                }
            }
            injector.releaseModifiers()
            viewer.close()
            glow.close()
            injector.close()
            counter.close()
        }

        private fun stringsTemplate(): String {
            val lang = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE).getString("lang", "en") ?: "en"
            val strings = loadStrings(lang)
            return strings["inputTrace"] ?: "Buttons {0} · Wheel {1}"
        }

        fun close() = runCatching { closeAction() }

        private fun waitForMouse(stop: AtomicBoolean) {
            val deadline = SystemClock.elapsedRealtime() + 1200
            while (!stop.get() && SystemClock.elapsedRealtime() < deadline) {
                if (edgeSpanMouse()) break
                flush()
                poll(80)
                engine.tick(SystemClock.elapsedRealtime())
            }
            val speed = runCatching { Settings.System.getInt(context.contentResolver, "pointer_speed", 0) }.getOrDefault(0)
            Log.i(TAG, "pointer-speed=$speed mouse=${edgeSpanMouse()}")
        }

        private fun handoffFailed(prefs: android.content.SharedPreferences, lang: String, key: String) {
            val text = loadStrings(lang)[key] ?: key
            DiagFile.append(context, "handoff failed $key")
            prefs.edit().putString("handoffError", text).apply()
            context.sendBroadcast(Intent(SessionService.ACTION_STATUS).setPackage(context.packageName))
            android.os.Handler(android.os.Looper.getMainLooper()).post {
                android.widget.Toast.makeText(context.applicationContext, text, android.widget.Toast.LENGTH_LONG).show()
            }
        }

        private fun wantsHandoff(prefs: android.content.SharedPreferences) =
            !prefs.contains("handoffPc") || prefs.getBoolean("handoffPc", true)

        private fun edgeSpanMouse(): Boolean {
            val manager = context.getSystemService(InputManager::class.java) ?: return false
            return manager.inputDeviceIds.any { id ->
                val device = manager.getInputDevice(id) ?: return@any false
                val name = device.name ?: ""
                name.contains("EdgeSpan", ignoreCase = true) &&
                    name.contains("Mouse", ignoreCase = true) &&
                    device.supportsSource(InputDevice.SOURCE_MOUSE)
            }
        }
    }

    companion object {
        const val TAG = "EdgeSpan"
    }
}

object HostFinder {
    data class Seen(val name: String, val host: String, val port: Int, val pin: String)

    fun listen(timeoutMs: Long = 1600): List<Seen> {
        val found = mutableListOf<Seen>()
        val socket = try {
            DatagramSocket(null).also {
                it.reuseAddress = true
                it.soTimeout = 400
                it.bind(InetSocketAddress(45454))
            }
        } catch (_: IOException) {
            return found
        }
        val deadline = SystemClock.elapsedRealtime() + timeoutMs
        val buf = ByteArray(512)
        while (SystemClock.elapsedRealtime() < deadline) {
            try {
                val packet = DatagramPacket(buf, buf.size)
                socket.receive(packet)
                val host = DiscoveryCodec.tryDecode(packet.data.copyOf(packet.length)) ?: continue
                val port = if (host.port == 0) packet.port else host.port
                val address = packet.address.hostAddress ?: continue
                found += Seen(host.name.ifBlank { host.id }, address, port, host.pin)
            } catch (_: SocketTimeoutException) {
            } catch (_: IOException) {
                break
            }
        }
        socket.close()
        return found
    }
}
