package com.edgestpan.app

import android.util.Base64
import com.edgestpan.protocol.RelayFrame
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONObject
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.net.URI
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/** WebSocket signaling plus the UDP ciphertext path to one self-hosted EdgeSpan relay. */
class RelayChannel(httpUrl: String) : AutoCloseable {
    private val wsUrl = toWebSocket(httpUrl)
    private val httpHost: String = URI(wsUrl.replace("ws://", "http://").replace("wss://", "https://")).host
    private val messages = ArrayBlockingQueue<String>(32)
    private val payloads = ArrayBlockingQueue<ByteArray>(64)
    private val http = OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).build()
    private var socket: WebSocket? = null
    private var udp: DatagramSocket? = null
    private var server: InetAddress? = null
    private var serverPort = 0
    private var token = ByteArray(0)
    var peerId: String = ""
        private set

    fun connect(name: String) {
        val request = Request.Builder().url(wsUrl).build()
        val failed = java.util.concurrent.atomic.AtomicReference<String>()
        socket = http.newWebSocket(request, object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                messages.offer(text)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                failed.compareAndSet(null, t.message ?: "The relay did not answer.")
                messages.offer("")
            }
        })
        socket?.send(JSONObject().put("op", "hello").put("name", name.ifBlank { "Android" }).put("role", "client").toString())
        val welcome = messages.poll(8, TimeUnit.SECONDS)
        val reason = failed.get()
        if (welcome.isNullOrBlank()) throw IOException(reason ?: "The relay did not answer.")
        val json = JSONObject(welcome)
        if (json.optString("op") != "welcome") throw IOException(json.optString("message", "The relay rejected this phone."))
        peerId = json.optString("peerId")
        val tokenText = json.optString("token")
        if (peerId.isBlank() || tokenText.isBlank()) throw IOException("The relay rejected this phone.")
        token = Base64.decode(tokenText, Base64.NO_WRAP)
        serverPort = json.optInt("udpPort", 8788)
        server = InetAddress.getByName(httpHost)
        udp = DatagramSocket().also { it.soTimeout = 80 }
        val bind = RelayFrame.bind(token, peerId)
        udp?.send(DatagramPacket(bind, bind.size, server, serverPort))
        Thread({
            val buf = ByteArray(65535)
            while (!Thread.currentThread().isInterrupted) {
                try {
                    val packet = DatagramPacket(buf, buf.size)
                    udp?.receive(packet) ?: break
                    val parsed = RelayFrame.parse(packet.data.copyOf(packet.length)) ?: continue
                    if (parsed.op == RelayFrame.DELIVER) payloads.offer(parsed.payload)
                } catch (_: SocketTimeoutException) {
                } catch (_: IOException) {
                    break
                }
            }
        }, "edgestpan-relay-udp").apply { isDaemon = true }.start()
    }

    fun signal(peer: String, data: String) {
        socket?.send(JSONObject().put("op", "signal").put("to", peer).put("data", data).toString())
    }

    fun awaitPin(peers: List<String>, phoneId: String, timeoutMs: Long): String? {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMs)
        var nextHello = 0L
        while (System.nanoTime() < deadline) {
            val now = System.nanoTime()
            if (now >= nextHello) {
                val hello = JSONObject().put("t", "hello").put("id", phoneId).toString()
                peers.forEach { signal(it, hello) }
                nextHello = now + TimeUnit.SECONDS.toNanos(1)
            }
            val text = messages.poll(250, TimeUnit.MILLISECONDS) ?: continue
            if (text.isBlank()) continue
            val json = try {
                JSONObject(text)
            } catch (_: Exception) {
                continue
            }
            if (json.optString("op") != "signal") continue
            val data = json.optString("data")
            if (data.isBlank()) continue
            val body = try {
                JSONObject(data)
            } catch (_: Exception) {
                continue
            }
            val pin = body.optString("pin")
            if (pin.length in 4..8 && pin.all { it.isDigit() }) return pin
        }
        return null
    }

    fun hosts(): List<String> {
        messages.clear()
        socket?.send(JSONObject().put("op", "list").toString()) ?: return emptyList()
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(4)
        while (System.nanoTime() < deadline) {
            val text = messages.poll(400, TimeUnit.MILLISECONDS) ?: continue
            if (text.isBlank()) continue
            val json = JSONObject(text)
            if (json.optString("op") != "peers") continue
            val list = json.optJSONArray("peers") ?: return emptyList()
            return buildList {
                for (i in 0 until list.length()) {
                    val peer = list.getJSONObject(i)
                    if (peer.optString("role") == "host") add(peer.optString("id"))
                }
            }.filter { it.isNotBlank() }
        }
        return emptyList()
    }

    fun send(peer: String, payload: ByteArray) {
        val bind = RelayFrame.bind(token, peerId)
        udp?.send(DatagramPacket(bind, bind.size, server, serverPort))
        val frame = RelayFrame.data(token, peer, payload)
        udp?.send(DatagramPacket(frame, frame.size, server, serverPort))
    }

    fun poll(timeoutMs: Int): ByteArray? = payloads.poll(timeoutMs.toLong(), TimeUnit.MILLISECONDS)

    fun pollJson(waitMs: Long): JSONObject? {
        val text = messages.poll(waitMs, TimeUnit.MILLISECONDS) ?: return null
        if (text.isBlank()) return null
        return try {
            JSONObject(text)
        } catch (_: Exception) {
            null
        }
    }

    override fun close() {
        socket?.close(1000, "bye")
        udp?.close()
        http.dispatcher.executorService.shutdown()
    }

    private fun toWebSocket(httpUrl: String): String {
        val trimmed = httpUrl.trim().trimEnd('/')
        val withScheme = when {
            trimmed.startsWith("ws://") || trimmed.startsWith("wss://") -> trimmed
            trimmed.startsWith("https://") -> "wss://" + trimmed.removePrefix("https://")
            trimmed.startsWith("http://") -> "ws://" + trimmed.removePrefix("http://")
            else -> "ws://$trimmed"
        }
        return if (withScheme.contains("/v1/signal")) withScheme else "$withScheme/v1/signal"
    }
}
