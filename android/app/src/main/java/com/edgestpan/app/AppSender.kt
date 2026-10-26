package com.edgestpan.app

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import com.edgestpan.protocol.AppStreamWire
import com.edgestpan.protocol.ClientEngine
import com.edgestpan.protocol.MediaFrames
import org.json.JSONObject
import java.io.DataInputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicBoolean

/** What the running phone session exposes to "Send to PC" (set by PhoneSession while a link is live). */
object AppLink {
    @Volatile var engine: ClientEngine? = null
    @Volatile var method: String = ""
    @Volatile var host: () -> String? = { null }

    fun pcAddress(): String? = when {
        engine == null -> null
        method == "usb" -> "127.0.0.1"
        method == "relay" -> null
        else -> host()
    }
}

/** Where encoded frames come from: the shell server's virtual display, or MediaProjection of the whole screen. */
interface AppFrameSource {
    /** Blocks, calling [sink] for every encoded frame until the source ends or [stop] is set. */
    fun run(stop: AtomicBoolean, sink: (ptsUs: Long, key: Boolean, annexb: ByteArray) -> Unit)
    fun input(event: AppStreamWire.Input)
    fun close()
    val width: Int
    val height: Int
}

/**
 * Android to Windows: streams one app to the PC, where it opens in its own window. Video is sealed with the
 * phone's media send key (the PC decrypts with its receive key); input coming back is sealed with the PC's key.
 */
object AppSender {
    private const val TAG = "EdgeSpan"
    private val running = AtomicBoolean(false)
    @Volatile private var stopFlag: AtomicBoolean? = null
    private val main = Handler(Looper.getMainLooper())

    val active get() = running.get()

    fun stop() {
        stopFlag?.set(true)
    }

    fun strings(context: Context): Map<String, String> = try {
        val lang = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE).getString("lang", "en") ?: "en"
        val root = JSONObject(context.assets.open("strings.json").bufferedReader().readText())
        buildMap {
            val keys = root.keys()
            while (keys.hasNext()) {
                val k = keys.next()
                val item = root.getJSONObject(k)
                put(k, if (item.has(lang)) item.getString(lang) else item.optString("en", k))
            }
        }
    } catch (_: Exception) {
        emptyMap()
    }

    private fun toast(context: Context, key: String, fallback: String) {
        val text = strings(context)[key] ?: fallback
        Log.i(TAG, "appstream $key")
        main.post { Toast.makeText(context.applicationContext, text, Toast.LENGTH_LONG).show() }
    }

    /** Tries the shell server first (true virtual display); returns null when it is not running. */
    fun shellSource(component: String, width: Int, height: Int, dpi: Int): ShellSource? = ShellSource.open(component, width, height, dpi)

    /** Ask the shell server which activity is in front (for "drag across the edge"). */
    fun foregroundComponent(): String? = ShellSource.foreground()

    fun send(context: Context, component: String, title: String, sourceFactory: () -> AppFrameSource?) {
        if (!running.compareAndSet(false, true)) {
            toast(context, "sendPcBusy", "Already sending an app to the PC.")
            return
        }
        val stop = AtomicBoolean(false)
        stopFlag = stop
        Thread({
            try {
                stream(context, component, title, stop, sourceFactory)
            } catch (thrown: Throwable) {
                Log.w(TAG, "appstream failed", thrown)
                toast(context, "sendPcFailed", "Could not send the app to the PC.")
            } finally {
                running.set(false)
                stopFlag = null
            }
        }, "edgestpan-appsend").start()
    }

    private fun stream(context: Context, component: String, title: String, stop: AtomicBoolean, sourceFactory: () -> AppFrameSource?) {
        val engine = AppLink.engine
        val address = AppLink.pcAddress()
        val sendKey = engine?.mediaSendKey()
        val recvKey = engine?.mediaReceiveKey()
        if (engine == null || address == null || sendKey == null || recvKey == null) {
            toast(context, "sendPcNoLink", "Connect to the PC first (USB or Wi-Fi).")
            return
        }
        val source = sourceFactory()
        if (source == null) {
            toast(context, "sendPcNoSource", "Could not capture the app.")
            return
        }
        val pc = Socket()
        try {
            pc.tcpNoDelay = true
            pc.connect(InetSocketAddress(address, AppStreamWire.PORT), 3000)
            val out = pc.getOutputStream()
            val input = DataInputStream(pc.getInputStream())
            var seq = (SecureRandom().nextLong() ushr 16) + 1
            out.write(AppStreamWire.writeHello(engine.id, source.width, source.height, seq, title, component))
            out.flush()
            val n = Integer.reverseBytes(input.readInt())
            if (n !in 13..64) return
            val reply = AppStreamWire.readReply(ByteArray(n).also { input.readFully(it) })
            if (reply == null || !reply.first) {
                toast(context, "sendPcRefused", "The PC refused. Turn on \"Android apps to this PC\" in EdgeSpan on Windows.")
                return
            }
            Log.i(TAG, "appstream connected $address via ${AppLink.method} ${source.width}x${source.height} $component")
            var pcSeq = reply.second
            Thread({
                try {
                    while (!stop.get()) {
                        val len = Integer.reverseBytes(input.readInt())
                        if (len < 16 || len > 1_000_000) break
                        val body = ByteArray(4 + len)
                        ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN).putInt(len)
                        input.readFully(body, 4, len)
                        val frame = MediaFrames.open(recvKey, pcSeq++, body) ?: continue
                        val event = AppStreamWire.readInput(frame.annexb) ?: continue
                        if (event.kind == AppStreamWire.InputClose) {
                            Log.i(TAG, "appstream closed by PC (returned to phone)")
                            stop.set(true)
                            break
                        }
                        source.input(event)
                    }
                } catch (_: Exception) {
                }
                stop.set(true)
                source.close()
            }, "edgestpan-appsend-input").apply { isDaemon = true }.start()
            var frameId = 0L
            source.run(stop) { pts, key, data ->
                val wire = MediaFrames.protect(sendKey, seq++, 1L, ++frameId, pts, if (key) 1 else 0, data)
                synchronized(out) { out.write(wire); out.flush() }
            }
            toast(context, "sendPcReturned", "The app is back on this device.")
        } catch (e: java.io.IOException) {
            Log.w(TAG, "appstream socket $address: $e")
            toast(context, "sendPcNoLink", "Connect to the PC first (USB or Wi-Fi).")
        } finally {
            stop.set(true)
            runCatching { source.close() }
            runCatching { pc.close() }
        }
    }
}

/** Client of the shell server's app-stream socket (127.0.0.1:27185). */
class ShellSource private constructor(private val sock: Socket, override val width: Int, override val height: Int) : AppFrameSource {
    private val input = DataInputStream(sock.getInputStream())
    private val out: OutputStream = sock.getOutputStream()

    override fun run(stop: AtomicBoolean, sink: (Long, Boolean, ByteArray) -> Unit) {
        try {
            while (!stop.get()) {
                val n = Integer.reverseBytes(input.readInt())
                if (n < 1 || n > 8_000_000) break
                val body = ByteArray(n).also { input.readFully(it) }
                if (body[0].toInt() != 2 || n < 10) continue
                val b = ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN)
                val pts = b.getLong(1)
                val key = (body[9].toInt() and 1) != 0
                sink(pts, key, body.copyOfRange(10, body.size))
            }
        } catch (_: Exception) {
        }
    }

    override fun input(event: AppStreamWire.Input) {
        val body = AppStreamWire.writeInput(event)
        val head = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(body.size).array()
        runCatching { synchronized(out) { out.write(head); out.write(body); out.flush() } }
    }

    override fun close() {
        input(AppStreamWire.Input(AppStreamWire.InputClose, 0, 0, 0, false, 0, 0, 0, ""))
        runCatching { sock.close() }
    }

    companion object {
        private const val PORT = 27185

        private fun request(body: ByteArray): Socket? {
            val s = Socket()
            return try {
                s.tcpNoDelay = true
                s.connect(InetSocketAddress("127.0.0.1", PORT), 800)
                val head = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(body.size).array()
                s.getOutputStream().write(head + body)
                s.getOutputStream().flush()
                s
            } catch (_: Exception) {
                runCatching { s.close() }
                null
            }
        }

        private fun status(s: Socket): Triple<Boolean, Int, String>? = runCatching {
            s.soTimeout = 8000
            val input = DataInputStream(s.getInputStream())
            val n = Integer.reverseBytes(input.readInt())
            val body = ByteArray(n).also { input.readFully(it) }
            s.soTimeout = 0
            if (body[0].toInt() != 1) return null
            val ok = body[1].toInt() != 0
            val display = ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN).getInt(2)
            Triple(ok, display, String(body, 6, body.size - 6))
        }.getOrNull()

        fun foreground(): String? {
            val s = request(byteArrayOf(2, 0, 0, 0, 0, 0, 0, 0)) ?: return null
            return try { status(s)?.third?.takeIf { it.contains('/') } } finally { runCatching { s.close() } }
        }

        fun open(component: String, width: Int, height: Int, dpi: Int): ShellSource? {
            val name = component.toByteArray()
            val b = ByteBuffer.allocate(1 + 2 + 2 + 2 + 4 + 2 + name.size).order(ByteOrder.LITTLE_ENDIAN)
            val w = (width / 2) * 2
            val h = (height / 2) * 2
            b.put(1).putShort(w.toShort()).putShort(h.toShort()).putShort(dpi.toShort()).putInt(AppStreamBitrate.forSize(w, h))
                .putShort(name.size.toShort()).put(name)
            val s = request(b.array()) ?: return null
            val st = status(s)
            if (st == null || !st.first) {
                Log.w("EdgeSpan", "appstream shell refused: ${st?.third}")
                runCatching { s.close() }
                return null
            }
            Log.i("EdgeSpan", "appstream shell display=${st.second} launch=${st.third}")
            return ShellSource(s, w, h)
        }
    }
}

object AppStreamBitrate {
    /** About 0.12 bits per pixel at 60 fps, between 4 and 20 Mbps. */
    fun forSize(w: Int, h: Int): Int = (w.toLong() * h * 60 * 12 / 100).toInt().coerceIn(4_000_000, 20_000_000)
}
