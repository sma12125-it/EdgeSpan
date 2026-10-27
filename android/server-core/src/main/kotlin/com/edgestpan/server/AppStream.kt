package com.edgestpan.server

import android.content.AttributionSource
import android.content.Context
import android.content.ContextWrapper
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.Process
import android.view.Surface
import com.edgestpan.protocol.AppStreamWire
import java.io.DataInputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Context that answers as the shell package so DisplayManager accepts the call from app_process. */
private class ShellContext : ContextWrapper(null) {
    override fun getPackageName(): String = "com.android.shell"
    override fun getOpPackageName(): String = "com.android.shell"
    override fun getApplicationContext(): Context = this
    override fun getAttributionSource(): AttributionSource =
        AttributionSource.Builder(2000).setPackageName("com.android.shell").build()
}

/**
 * Local control socket (127.0.0.1:27185) for the app's "Send to PC" feature. The app sends one start
 * request; the server creates a virtual display, launches the activity on it, encodes it with MediaCodec
 * and streams [u32 len][u8 type][...] frames back:
 *  type 1 status: u8 ok, i32 displayId, utf8 message
 *  type 2 video : u64 ptsUs, u8 flags (1 key), annexb
 * The app sends input frames [u32 len][body] in AppStreamWire input format; closing the socket ends the
 * stream and the virtual display is released, which moves the app back to the phone's own screen.
 */
class AppStreamServer(private val port: Int = 27185) {
    fun start() {
        val server = ServerSocket(port, 2, InetAddress.getByName("127.0.0.1"))
        Thread({
            while (true) {
                val client = runCatching { server.accept() }.getOrNull() ?: break
                Thread({ runCatching { serve(client) }.onFailure { log("stream ended $it") } }, "edgestpan-appstream").apply { isDaemon = true }.start()
            }
        }, "edgestpan-appstream-accept").apply { isDaemon = true }.start()
        log("listening $port")
    }

    private fun log(s: String) = println("edgestpan-server appstream $s")

    private fun serve(client: Socket) = client.use { sock ->
        sock.tcpNoDelay = true
        val input = DataInputStream(sock.getInputStream())
        val out = sock.getOutputStream()
        val len = Integer.reverseBytes(input.readInt())
        if (len !in 8..4096) return
        val req = ByteArray(len).also { input.readFully(it) }
        val b = ByteBuffer.wrap(req).order(ByteOrder.LITTLE_ENDIAN)
        val cmd = b.get().toInt()
        if (cmd == 2) {
            // Foreground query: answer with the resumed activity component.
            status(out, true, 0, foreground() ?: "")
            return
        }
        if (cmd != 1) return
        val w = b.short.toInt() and 0xffff
        val h = b.short.toInt() and 0xffff
        val dpi = b.short.toInt() and 0xffff
        val bitrate = b.int
        val nameLen = b.short.toInt() and 0xffff
        val component = String(req, b.position(), nameLen)
        log("start $component ${w}x$h dpi=$dpi bitrate=$bitrate")
        val session = runCatching { Session(component, even(w), even(h), dpi, bitrate) { pts, flags, data -> video(out, pts, flags, data) } }
            .onFailure { status(out, false, -1, "create failed: $it") }
            .getOrNull() ?: return
        if (session.launchResult.startsWith("Error") || session.launchResult.contains("Exception")) {
            // The display exists but the app did not start on it: refuse rather than stream an empty display.
            log("launch failed on display ${session.displayId}: ${session.launchResult}")
            status(out, false, session.displayId, "launch failed: ${session.launchResult}")
            session.close()
            return
        }
        try {
            status(out, true, session.displayId, session.launchResult)
            while (true) {
                val n = Integer.reverseBytes(input.readInt())
                if (n !in 1..65536) break
                val body = ByteArray(n).also { input.readFully(it) }
                val event = AppStreamWire.readInput(body) ?: continue
                if (event.kind == AppStreamWire.InputClose) break
                session.input(event)
            }
        } catch (_: java.io.EOFException) {
        } finally {
            session.close()
            log("stopped $component")
        }
    }

    private fun even(v: Int) = (v.coerceIn(160, 4096) / 2) * 2

    private fun status(out: OutputStream, ok: Boolean, display: Int, message: String) {
        val m = message.toByteArray()
        val b = ByteBuffer.allocate(4 + 1 + 1 + 4 + m.size).order(ByteOrder.LITTLE_ENDIAN)
        b.putInt(1 + 1 + 4 + m.size).put(1).put((if (ok) 1 else 0).toByte()).putInt(display).put(m)
        synchronized(out) { out.write(b.array()); out.flush() }
    }

    private fun video(out: OutputStream, pts: Long, flags: Int, data: ByteArray) {
        val b = ByteBuffer.allocate(4 + 1 + 8 + 1).order(ByteOrder.LITTLE_ENDIAN)
        b.putInt(1 + 8 + 1 + data.size).put(2).putLong(pts).put(flags.toByte())
        synchronized(out) { out.write(b.array()); out.write(data); out.flush() }
    }

    private fun foreground(): String? {
        val text = runCatching { Runtime.getRuntime().exec(arrayOf("dumpsys", "activity", "activities")).inputStream.bufferedReader().readText() }.getOrNull() ?: return null
        return AppStreamText.resumedComponent(text)
    }

    private class Session(component: String, val w: Int, val h: Int, dpi: Int, bitrate: Int, val sink: (Long, Int, ByteArray) -> Unit) {
        private val codec: MediaCodec
        private val surface: Surface
        private val display: VirtualDisplay
        val displayId: Int
        val launchResult: String
        private val input = SystemInput().also { it.display = 0 }
        @Volatile private var running = true
        private val drain: Thread

        init {
            val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            format.setInteger(MediaFormat.KEY_BIT_RATE, bitrate.coerceIn(1_000_000, 40_000_000))
            format.setInteger(MediaFormat.KEY_FRAME_RATE, 60)
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
            // A frame is still produced on a static screen, so a newly attached viewer is never blank.
            format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000)
            if (Build.VERSION.SDK_INT >= 29) format.setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            format.setInteger(MediaFormat.KEY_PRIORITY, 0)
            codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            surface = codec.createInputSurface()
            codec.start()
            display = createDisplay(w, h, dpi, surface)
            displayId = display.display.displayId
            input.display = displayId
            launchResult = launch(component, displayId)
            drain = Thread({ drainLoop() }, "edgestpan-appstream-encode").apply { isDaemon = true; start() }
        }

        private fun createDisplay(w: Int, h: Int, dpi: Int, surface: Surface): VirtualDisplay {
            val ctor = DisplayManager::class.java.getDeclaredConstructor(Context::class.java)
            ctor.isAccessible = true
            val manager = ctor.newInstance(ShellContext())
            val base = 1 or 8 or 64 or 128 // PUBLIC | OWN_CONTENT_ONLY | SUPPORTS_TOUCH | ROTATES_WITH_CONTENT
            val trusted = if (Build.VERSION.SDK_INT >= 33) base or 1024 or 2048 or 4096 or 8192 else base // TRUSTED | OWN_DISPLAY_GROUP | ALWAYS_UNLOCKED | TOUCH_FEEDBACK_DISABLED
            val focus = if (Build.VERSION.SDK_INT >= 34) trusted or 16384 else trusted // OWN_FOCUS
            // app_process has no Looper on this thread; DisplayManager builds a Handler for the callback and throws
            // "Can't create handler inside thread that has not called Looper.prepare()" (seen on Android 12),
            // so give it an explicit Handler on a dedicated looper thread.
            val handler = displayHandler()
            for (flags in listOf(focus, trusted, base)) {
                val result = runCatching { manager.createVirtualDisplay("EdgeSpan", w, h, dpi.coerceIn(120, 640), surface, flags, null, handler) }
                    .recoverCatching { first ->
                        println("edgestpan-server appstream display flags=$flags with handler failed: $first")
                        if (android.os.Looper.myLooper() == null) android.os.Looper.prepare()
                        manager.createVirtualDisplay("EdgeSpan", w, h, dpi.coerceIn(120, 640), surface, flags)
                    }
                result.exceptionOrNull()?.let { println("edgestpan-server appstream display flags=$flags failed: $it") }
                val made = result.getOrNull()
                if (made != null) {
                    println("edgestpan-server appstream display ${made.display.displayId} flags=$flags")
                    return made
                }
            }
            error("virtual display refused")
        }

        private fun displayHandler(): android.os.Handler {
            val thread = android.os.HandlerThread("edgestpan-vd")
            thread.start()
            return android.os.Handler(thread.looper)
        }

        private fun launch(component: String, displayId: Int): String {
            val args = arrayListOf("am", "start", "--display", displayId.toString(), "--activity-clear-task", "-f", "0x10000000")
            if (component.contains('/')) args += listOf("-n", component)
            else args += listOf("-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", component)
            val p = Runtime.getRuntime().exec(args.toTypedArray())
            val text = (p.inputStream.bufferedReader().readText() + p.errorStream.bufferedReader().readText()).trim()
            p.waitFor()
            println("edgestpan-server appstream launch $component -> $text")
            val last = text.lineSequence().lastOrNull { it.isNotBlank() } ?: ""
            if ((last.startsWith("Error") || text.contains("Exception")) && component.contains('/')) {
                // Retry without the task flags; some OEM builds reject --activity-clear-task on secondary displays.
                val retry = arrayOf("am", "start", "--display", displayId.toString(), "-n", component)
                val p2 = Runtime.getRuntime().exec(retry)
                val t2 = (p2.inputStream.bufferedReader().readText() + p2.errorStream.bufferedReader().readText()).trim()
                p2.waitFor()
                println("edgestpan-server appstream launch retry $component -> $t2")
                return t2.lineSequence().lastOrNull { it.isNotBlank() } ?: ""
            }
            return last
        }

        private fun drainLoop() {
            val info = MediaCodec.BufferInfo()
            var config: ByteArray? = null
            while (running) {
                val index = try { codec.dequeueOutputBuffer(info, 100_000) } catch (_: IllegalStateException) { break }
                if (index < 0) continue
                val buffer = codec.getOutputBuffer(index)
                if (buffer != null && info.size > 0) {
                    buffer.position(info.offset)
                    buffer.limit(info.offset + info.size)
                    val data = ByteArray(info.size).also { buffer.get(it) }
                    if ((info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) config = data
                    else {
                        val key = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                        // SPS/PPS go in front of every key frame so a viewer can join at any IDR.
                        val payload = if (key && config != null) config!! + data else data
                        runCatching { sink(info.presentationTimeUs, if (key) 1 else 0, payload) }.onFailure { running = false }
                    }
                }
                codec.releaseOutputBuffer(index, false)
            }
        }

        fun input(e: AppStreamWire.Input) {
            when (e.kind) {
                AppStreamWire.InputMove -> input.pointer(e.x.coerceIn(0, w - 1), e.y.coerceIn(0, h - 1), heldBits, 0)
                AppStreamWire.InputButton -> {
                    val bit = when (e.button) { 2 -> 2; 3 -> 4; else -> 1 }
                    heldBits = if (e.down) heldBits or bit else heldBits and bit.inv()
                    input.pointer(e.x.coerceIn(0, w - 1), e.y.coerceIn(0, h - 1), heldBits, 0)
                }
                AppStreamWire.InputWheel -> input.pointer(e.x.coerceIn(0, w - 1), e.y.coerceIn(0, h - 1), heldBits, if (e.wheel > 0) 1 else -1)
                AppStreamWire.InputKey -> input.key(e.vk, e.hid, e.down)
                AppStreamWire.InputBack -> { input.keyCode(4, true); input.keyCode(4, false) }
                AppStreamWire.InputIdr -> runCatching { codec.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }
                AppStreamWire.InputText -> for (ch in e.text) input.char(ch)
            }
        }

        private var heldBits = 0

        fun close() {
            running = false
            runCatching { display.release() }
            runCatching { codec.stop() }
            runCatching { codec.release() }
            runCatching { surface.release() }
        }
    }
}

/** Text helpers kept free of Android classes so they are unit-testable on the JVM. */
object AppStreamText {
    private val patterns = listOf(
        Regex("""topResumedActivity=ActivityRecord\{\S+ \S+ ([\w.]+/[\w.$]+)"""),
        Regex("""mResumedActivity: ActivityRecord\{\S+ \S+ ([\w.]+/[\w.$]+)"""),
        Regex("""ResumedActivity: ActivityRecord\{\S+ \S+ ([\w.]+/[\w.$]+)""")
    )

    fun resumedComponent(dump: String): String? {
        for (p in patterns) p.find(dump)?.let { return it.groupValues[1] }
        return null
    }
}
