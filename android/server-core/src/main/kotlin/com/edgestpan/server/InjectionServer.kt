package com.edgestpan.server

import com.edgestpan.protocol.PointerBounds
import java.net.InetAddress
import java.net.ServerSocket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Localhost bridge used by the Android app after the server process is started
 * with app_process. Frames are little-endian: u16 length, u8 type, payload.
 * Type 1 is hello (16-byte token, u16 width, u16 height, u8 mode).
 * Types 3..6 carry mouse, button, scroll, and key reports.
 */
class InjectionServer(
    private val mouse: HidSink,
    private val keyboard: HidSink,
    private val port: Int = 27183,
    private val token: ByteArray? = null,
    /** Server-side InputManager path (scrcpy style); used when UHID is missing or the phone asks for it. */
    private val system: SystemInput? = null,
    uhidOk: Boolean = true
) : AutoCloseable {
    /** 1 = UHID virtual mouse/keyboard, 2 = InputManager.injectInputEvent. Reported to the phone in the hello ack. */
    @Volatile var mode: Int = if (uhidOk || system == null) 1 else 2
        private set
    private val running = AtomicBoolean(false)
    private var socket: ServerSocket? = null
    var width = 1080
        private set
    var height = 1920
        private set
    var helloSeen = false
        private set
    private var buttons = 0
    private var lastX = width / 2
    private var lastY = height / 2
    private val keys = KeyboardState()

    fun start() {
        if (!running.compareAndSet(false, true)) return
        socket = bindLoopback(port, 1)
        Thread({
            while (running.get()) {
                val client = try { socket?.accept() } catch (_: Exception) { null } ?: break
                // The newest phone connection wins: a stale socket from an earlier session must
                // not keep the single reader busy while the new one sits unread in the backlog.
                current?.let { old -> runCatching { old.close() } }
                current = client
                Thread({
                    client.use {
                        val out = it.getOutputStream()
                        clientOut = out
                        log("client connected")
                        try {
                            readLoop(it.getInputStream())
                        } catch (thrown: Throwable) {
                            log("client loop ended: $thrown")
                        }
                        if (clientOut === out) clientOut = null
                        log("client closed")
                    }
                }, "edgestpan-client").apply { isDaemon = true }.start()
            }
        }, "edgestpan-inject").apply { isDaemon = true }.start()
        Thread({ watchTouch() }, "edgestpan-touch").apply { isDaemon = true }.start()
    }

    @Volatile private var clientOut: java.io.OutputStream? = null
    @Volatile private var current: java.net.Socket? = null

    private fun log(line: String) = println("edgestpan-server $line")

    private fun reply(type: Int, vararg payload: Byte) {
        val out = clientOut ?: return
        val len = 1 + payload.size
        runCatching {
            synchronized(out) {
                out.write(byteArrayOf(len.toByte(), (len shr 8).toByte(), type.toByte()) + payload)
                out.flush()
            }
        }
    }

    private fun watchTouch() {
        val root = java.io.File("/dev/input")
        val nodes = root.listFiles { f -> f.name.startsWith("event") } ?: return
        for (node in nodes) {
            val name = runCatching { java.io.File("/sys/class/input/${node.name}/device/name").readText() }.getOrDefault("")
            if (name.contains("EdgeSpan", ignoreCase = true)) continue
            Thread({
                runCatching {
                    java.io.FileInputStream(node).use { input ->
                        val buf = ByteArray(24)
                        while (running.get()) {
                            if (input.read(buf) < 16) break
                            val type = (buf[16].toInt() and 0xff) or ((buf[17].toInt() and 0xff) shl 8)
                            val code = (buf[18].toInt() and 0xff) or ((buf[19].toInt() and 0xff) shl 8)
                            val value = (buf[20].toInt() and 0xff) or ((buf[21].toInt() and 0xff) shl 8)
                            val finger = (type == 3 && code == 0x39 && value != 0xffff) || (type == 1 && code == 0x14a && value == 1)
                            if (finger) {
                                val out = clientOut ?: continue
                                synchronized(out) {
                                    out.write(byteArrayOf(1, 0, 9))
                                    out.flush()
                                }
                            }
                        }
                    }
                }
            }, "edgestpan-touch-${node.name}").apply { isDaemon = true }.start()
        }
    }

    fun acceptOnceForTest(handler: (InjectionServer) -> Unit) = handler(this)

    fun handle(type: Int, payload: ByteArray) {
        when (type) {
            1 -> {
                if (token != null && (payload.size < 16 || !payload.copyOf(16).contentEquals(token))) return
                if (payload.size >= 21) {
                    width = u16(payload, 16).coerceAtLeast(1)
                    height = u16(payload, 18).coerceAtLeast(1)
                    if (!helloSeen) {
                        lastX = width / 2
                        lastY = height / 2
                    } else {
                        val kept = PointerBounds.clamp(lastX, lastY, width, height)
                        lastX = kept.first
                        lastY = kept.second
                    }
                }
                helloSeen = true
                // Ack so the phone knows the server really reads its socket and which path is live.
                reply(2, mode.toByte(), (width and 0xff).toByte(), (width shr 8).toByte(), (height and 0xff).toByte(), (height shr 8).toByte())
                log("hello ${width}x$height mode=$mode")
            }
            3 -> if (payload.size == 8) moveTo(i32(payload, 0), i32(payload, 4), 0, force = false)
            4 -> if (payload.size == 10) {
                val bit = 1 shl (payload[0].toInt() and 7)
                buttons = if (payload[1].toInt() != 0) buttons or bit else buttons and bit.inv()
                moveTo(i32(payload, 2), i32(payload, 6), 0, force = true)
            }
            5 -> if (payload.size == 12) moveTo(i32(payload, 4), i32(payload, 8), i16(payload, 2), force = true)
            6 -> if (payload.size == 10) {
                val down = payload[5].toInt() != 0
                val report = keys.apply(u16(payload, 0), u16(payload, 2), down)
                if (mode == 2 && system != null) system.key(u16(payload, 0), u16(payload, 2), down)
                else keyboard.write(Uhid.input2(report))
            }
            7 -> {
                mouse.write(Uhid.destroy())
                keyboard.write(Uhid.destroy())
            }
            8 -> if (system != null) {
                // Phone saw no UHID mouse device: switch to InputManager injection.
                mode = 2
                log("switched to inputmanager")
                reply(2, 2, (width and 0xff).toByte(), (width shr 8).toByte(), (height and 0xff).toByte(), (height shr 8).toByte())
            }
        }
    }

    private fun readLoop(input: java.io.InputStream) {
        val header = ByteArray(3)
        while (running.get()) {
            if (!readFully(input, header)) return
            val len = u16(header, 0)
            if (len < 1 || len > 4096) return
            val body = ByteArray(len)
            body[0] = header[2]
            if (len > 1 && !readFully(input, body, 1, len - 1)) return
            try {
                handle(body[0].toInt() and 0xff, body.copyOfRange(1, body.size))
            } catch (thrown: Throwable) {
                // Never let one bad frame kill the whole server process.
                log("frame ${body[0].toInt() and 0xff} failed: $thrown")
            }
        }
    }

    private fun moveTo(x: Int, y: Int, wheel: Int, force: Boolean) {
        val placed = PointerBounds.clamp(x, y, width, height)
        val dx = placed.first - lastX
        val dy = placed.second - lastY
        lastX = placed.first
        lastY = placed.second
        if (!force && dx == 0 && dy == 0 && wheel == 0) return
        if (mode == 2 && system != null) {
            system.pointer(lastX, lastY, buttons, wheel)
            return
        }
        for (report in Uhid.relativeMouse(dx, dy, buttons, wheel))
            mouse.write(Uhid.input2(report))
    }

    override fun close() {
        running.set(false)
        try { socket?.close() } catch (_: Exception) { }
    }

    private fun readFully(input: java.io.InputStream, buf: ByteArray, off: Int = 0, len: Int = buf.size): Boolean {
        var n = 0
        while (n < len) {
            val got = input.read(buf, off + n, len - n)
            if (got < 0) return false
            n += got
        }
        return true
    }

    private fun u16(b: ByteArray, o: Int) = (b[o].toInt() and 0xff) or ((b[o + 1].toInt() and 0xff) shl 8)
    private fun i16(b: ByteArray, o: Int) = u16(b, o).toShort().toInt()
    private fun i32(b: ByteArray, o: Int): Int {
        var v = 0
        for (i in 0 until 4) v = v or ((b[o + i].toInt() and 0xff) shl (8 * i))
        return v
    }
}

object ServerMain {
    @JvmStatic
    fun main(args: Array<String>) {
        Thread.setDefaultUncaughtExceptionHandler { t, e ->
            println("edgestpan-server uncaught in ${t.name}: $e")
            e.printStackTrace(System.out)
        }
        val port = args.firstOrNull()?.toIntOrNull() ?: 27183
        var mouse: HidSink = HidSink { }
        var keyboard: HidSink = HidSink { }
        val uhidOk = try {
            val m = FileHid("/dev/uhid")
            val k = FileHid("/dev/uhid")
            m.write(Uhid.create2("EdgeSpan Mouse", HidReport.mouse))
            k.write(Uhid.create2("EdgeSpan Keyboard", HidReport.keyboard, product = 2))
            mouse = m
            keyboard = k
            true
        } catch (thrown: Throwable) {
            println("edgestpan-server uhid-unavailable $thrown")
            false
        }
        val system = runCatching { SystemInput() }.onFailure { println("edgestpan-server inputmanager-unavailable $it") }.getOrNull()
        if (!uhidOk && system == null) {
            println("edgestpan-server uhid-denied no-injection-path")
            return
        }
        println("edgestpan-server mouse-relative $port uhid=$uhidOk inputmanager=${system != null}")
        InjectionServer(mouse, keyboard, port, system = system, uhidOk = uhidOk).start()
        // "Send to PC": virtual display + MediaCodec. Needs the platform classes, so failures stay contained.
        runCatching { AppStreamServer(port + 2).start() }.onFailure { println("edgestpan-server appstream-unavailable $it") }
        Thread.currentThread().join()
    }
}

class FileHid(path: String) : HidSink {
    private val out = java.io.FileOutputStream(path)

    override fun write(packet: ByteArray) {
        synchronized(out) {
            out.write(packet)
            out.flush()
        }
    }
}
