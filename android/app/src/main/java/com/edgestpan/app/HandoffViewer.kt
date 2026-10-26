package com.edgestpan.app

import android.content.Context
import android.graphics.Color
import android.graphics.PixelFormat
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import com.edgestpan.protocol.ClientEngine
import com.edgestpan.protocol.ClientEvent
import com.edgestpan.protocol.HandoffPayloads
import com.edgestpan.protocol.MediaFrames
import com.edgestpan.protocol.ScreenEdge
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ConcurrentLinkedQueue
import kotlin.math.min

/**
 * One PC window on the tablet, over USB. Video is TCP 27184. Touches outside
 * the window are left for the shared pointer. Moves go out as unreliable
 * handoff input so a frame cannot stall a click.
 */
class HandoffViewer(
    private val context: Context,
    private val engine: ClientEngine,
    private val method: String,
    private val strings: Map<String, String>,
    private val flush: () -> Unit,
    private val onFail: (String) -> Unit = {}
) {
    private val main = Handler(Looper.getMainLooper())
    private val actions = ConcurrentLinkedQueue<() -> Unit>()
    private val gate = Object()
    private var liveId = 0L
    private var edge = ScreenEdge.Right
    private var follow = false
    private var finger = false
    private var grabbed = false
    private var grabX = 0
    private var grabY = 0
    private var pointerX = 0
    private var pointerY = 0
    private var pointerOn = false
    private var videoX = 0
    private var videoY = 0
    private var contentW = 2
    private var contentH = 2
    private var captionPx = 48
    private var downRawX = 0f
    private var downRawY = 0f
    private var downVideoX = 0
    private var downVideoY = 0
    private var downW = 0
    private var downH = 0
    private var moveX = 0
    private var moveY = 0
    private var movePending = false
    private var lastGeom = 0L
    private var host: Context? = null
    private var root: View? = null
    private var surfaceView: SurfaceView? = null
    private var imageView: ImageView? = null
    private var statusView: TextView? = null
    private var params: WindowManager.LayoutParams? = null
    @Volatile private var surface: Surface? = null
    @Volatile private var running = false
    private var socket: Socket? = null
    private var reader: Thread? = null

    fun offer(event: ClientEvent.HandoffOffer) {
        synchronized(gate) {
            if (liveId != 0L) return
            liveId = event.id
        }
        edge = event.edge
        follow = true
        finger = false
        grabbed = false
        DisplaySize.ensure(context)
        captionPx = (36f * DisplaySize.density).toInt().coerceIn(36, 72)
        val fitted = fit(event.physW, event.physH)
        contentW = fitted.first
        contentH = fitted.second
        videoX = when (event.edge) {
            ScreenEdge.Left -> (DisplaySize.width - contentW).coerceAtLeast(0)
            else -> 0
        }
        videoY = when (event.edge) {
            ScreenEdge.Top -> (DisplaySize.height - contentH).coerceAtLeast(0)
            else -> 0
        }
        val id = event.id
        val title = event.title.ifBlank { strings["handoffWaiting"] ?: "Waiting for the window…" }
        Thread({
            if (method != "usb") {
                fail(id, "handoffNoLink")
                return@Thread
            }
            val key = engine.mediaReceiveKey()
            if (key == null) {
                fail(id, "handoffNoLink")
                return@Thread
            }
            val sock = Socket()
            try {
                sock.connect(InetSocketAddress("127.0.0.1", 27184), 2000)
                sock.tcpNoDelay = true
                sock.sendBufferSize = 64 * 1024
                sock.receiveBufferSize = 256 * 1024
                sock.soTimeout = 400
            } catch (_: Exception) {
                runCatching { sock.close() }
                fail(id, "handoffNoLink")
                return@Thread
            }
            synchronized(gate) {
                if (liveId != id) {
                    runCatching { sock.close() }
                    return@Thread
                }
                socket = sock
                running = true
            }
            actions.add { engine.handoffAccept(id, contentW, contentH) }
            main.post { if (liveId == id) attach(id, title) }
            read(sock, key, id, event.physW, event.physH)
        }, "EdgeSpan.HandoffView").apply { isDaemon = true; reader = this }.start()
    }

    fun geometry(event: ClientEvent.HandoffGeometry) {
        if (event.id != liveId || finger) return
        videoX = event.x
        videoY = event.y
        if (pointerOn) {
            grabX = pointerX - event.x
            grabY = pointerY - event.y
            grabbed = true
        }
        main.post { if (event.id == liveId) layout() }
    }

    fun state(event: ClientEvent.HandoffState) {
        if (event.id != liveId) return
        if (event.value == HandoffPayloads.StateRestored) {
            follow = false
            grabbed = false
            sendGeometry(event.id, force = true)
        } else if (event.value == HandoffPayloads.StateClosed) {
            drop(event.id)
        }
    }

    fun returned(event: ClientEvent.HandoffReturned) {
        if (event.id == liveId) drop(event.id)
    }

    fun tick(x: Int, y: Int, control: Boolean) {
        pointerX = x
        pointerY = y
        pointerOn = control
        if (follow && control && !finger && liveId != 0L && !grabbed) {
            grabX = x - videoX
            grabY = y - videoY
            grabbed = true
        }
        if (!follow || finger || !control || !grabbed || liveId == 0L) return
        videoX = x - grabX
        videoY = y - grabY
        main.post { if (liveId != 0L) layout() }
    }

    fun drain(): Boolean {
        var sent = false
        while (true) {
            val action = actions.poll() ?: break
            action()
            sent = true
        }
        if (movePending && liveId != 0L) {
            movePending = false
            engine.handoffMove(liveId, moveX, moveY)
            sent = true
        }
        if (sent) flush()
        return sent
    }

    fun close() {
        val id = liveId
        if (id != 0L) actions.add { engine.handoffReturn(id) }
        drop(id)
        drain()
    }

    private fun fail(id: Long, key: String) {
        onFail(key)
        main.post {
            if (liveId != id && liveId != 0L) return@post
            showNote(strings[key] ?: key)
        }
        actions.add {
            if (engine.canHandoff()) engine.handoffReturn(id)
        }
        drop(id)
    }

    private fun fit(physW: Int, physH: Int): Pair<Int, Int> {
        val screenW = DisplaySize.width.coerceAtLeast(physW.coerceAtLeast(2))
        val screenH = DisplaySize.height.coerceAtLeast(physH.coerceAtLeast(2))
        val maxW = (screenW - 8).coerceAtLeast(80)
        val maxH = (screenH - captionPx - 8).coerceAtLeast(80)
        var w = physW.coerceAtLeast(2)
        var h = physH.coerceAtLeast(2)
        if (w > maxW || h > maxH) {
            val scale = min(maxW.toFloat() / w, maxH.toFloat() / h)
            w = (w * scale).toInt().coerceAtLeast(2)
            h = (h * scale).toInt().coerceAtLeast(2)
        }
        return (w and -2) to (h and -2)
    }

    private fun attach(id: Long, title: String) {
        val ctx = owner() ?: run {
            fail(id, "handoffNoLink")
            return
        }
        host = ctx
        val caption = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Color.parseColor("#12141C"))
            setPadding(16, 0, 8, 0)
            gravity = Gravity.CENTER_VERTICAL
        }
        val label = TextView(ctx).apply {
            text = title
            setTextColor(Color.parseColor("#F4F7FF"))
            textSize = 14f
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f)
            gravity = Gravity.CENTER_VERTICAL
        }
        val close = TextView(ctx).apply {
            text = "×"
            setTextColor(Color.parseColor("#F4F7FF"))
            textSize = 20f
            setPadding(24, 0, 16, 0)
            gravity = Gravity.CENTER
            setOnClickListener {
                actions.add { engine.handoffState(id, HandoffPayloads.StateClosed) }
                drop(id)
            }
        }
        caption.addView(label)
        caption.addView(close)
        caption.setOnTouchListener { _, event -> onCaption(id, event) }
        val video = SurfaceView(ctx)
        video.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                holder.setFixedSize(contentW.coerceAtLeast(2), contentH.coerceAtLeast(2))
                surface = holder.surface
            }
            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
            override fun surfaceDestroyed(holder: SurfaceHolder) {
                surface = null
            }
        })
        video.setOnTouchListener { _, event -> onVideo(id, event) }
        val still = ImageView(ctx).apply {
            scaleType = ImageView.ScaleType.FIT_XY
            visibility = View.GONE
            setOnTouchListener { _, event -> onVideo(id, event) }
        }
        val status = TextView(ctx).apply {
            text = strings["handoffWaiting"] ?: "Waiting for the window…"
            setTextColor(Color.WHITE)
            setBackgroundColor(Color.parseColor("#CC12141C"))
            setPadding(24, 16, 24, 16)
        }
        val frame = FrameLayout(ctx)
        frame.addView(video, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        frame.addView(still, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        frame.addView(status, FrameLayout.LayoutParams(FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
        val column = object : LinearLayout(ctx) {
            override fun dispatchKeyEvent(event: KeyEvent): Boolean {
                onKey(id, event)
                return true
            }
        }
        column.orientation = LinearLayout.VERTICAL
        column.setBackgroundColor(Color.parseColor("#12141C"))
        column.addView(caption, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, captionPx))
        column.addView(frame, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, contentH))
        column.isFocusable = true
        column.isFocusableInTouchMode = true
        val grip = View(ctx).apply {
            setBackgroundColor(Color.parseColor("#3DDC84"))
            setOnTouchListener { _, event -> onGrip(id, event) }
        }
        frame.addView(grip, FrameLayout.LayoutParams((18 * DisplaySize.density).toInt().coerceIn(18, 36), (18 * DisplaySize.density).toInt().coerceIn(18, 36), Gravity.BOTTOM or Gravity.END))
        val wm = ctx.getSystemService(WindowManager::class.java) ?: return
        val lp = windowParams(contentW, contentH + captionPx)
        lp.x = videoX
        lp.y = videoY - captionPx
        runCatching { wm.addView(column, lp) }.onFailure {
            fail(id, "handoffNoLink")
            return
        }
        root = column
        surfaceView = video
        imageView = still
        statusView = status
        runCatching { Motion.settle(column, edge, Motion.reduce(ctx)) }
        params = lp
        column.requestFocus()
    }

    private fun onCaption(id: Long, event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                finger = true
                downRawX = event.rawX
                downRawY = event.rawY
                downVideoX = videoX
                downVideoY = videoY
                root?.requestFocus()
            }
            MotionEvent.ACTION_MOVE -> {
                val nx = downVideoX + (event.rawX - downRawX).toInt()
                val ny = downVideoY + (event.rawY - downRawY).toInt()
                videoX = nx
                videoY = ny
                layout()
                if (past(nx, ny)) {
                    actions.add { engine.handoffReturn(id) }
                    drop(id)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> finger = false
        }
        return true
    }

    private fun onGrip(id: Long, event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                finger = true
                downRawX = event.rawX
                downRawY = event.rawY
                downW = contentW
                downH = contentH
            }
            MotionEvent.ACTION_MOVE -> {
                val maxW = (DisplaySize.width - 8).coerceAtLeast(80)
                val maxH = (DisplaySize.height - captionPx - 8).coerceAtLeast(80)
                contentW = (downW + (event.rawX - downRawX).toInt()).coerceIn(160, maxW) and -2
                contentH = (downH + (event.rawY - downRawY).toInt()).coerceIn(120, maxH) and -2
                layout()
                surfaceView?.holder?.setFixedSize(contentW, contentH)
                sendGeometry(id, force = false)
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                finger = false
                sendGeometry(id, force = true)
            }
        }
        return true
    }

    private fun onVideo(id: Long, event: MotionEvent): Boolean {
        root?.requestFocus()
        val view = if (imageView?.visibility == View.VISIBLE) imageView else surfaceView
        if (view == null) return true
        val sx = (event.x).toInt().coerceIn(0, (view.width - 1).coerceAtLeast(0))
        val sy = (event.y).toInt().coerceIn(0, (view.height - 1).coerceAtLeast(0))
        val x = sx * contentW / view.width.coerceAtLeast(1)
        val y = sy * contentH / view.height.coerceAtLeast(1)
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                if (event.pointerCount == 1) actions.add { engine.handoffButton(id, x, y, 1, true) }
            }
            MotionEvent.ACTION_MOVE -> {
                if (event.pointerCount >= 2) {
                    actions.add { engine.handoffWheel(id, x, y, -event.y.toInt().coerceIn(-120, 120)) }
                } else {
                    moveX = x
                    moveY = y
                    movePending = true
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> actions.add { engine.handoffButton(id, x, y, 1, false) }
        }
        return true
    }

    private fun onKey(id: Long, event: KeyEvent) {
        if (event.action != KeyEvent.ACTION_DOWN && event.action != KeyEvent.ACTION_UP) return
        val printable = event.action == KeyEvent.ACTION_DOWN && event.unicodeChar > 31 && !event.isCtrlPressed && !event.isAltPressed
        if (printable) {
            actions.add { engine.handoffText(id, event.unicodeChar.toChar().toString()) }
            return
        }
        val vk = vk(event.keyCode)
        if (vk != 0) actions.add { engine.handoffKey(id, vk, event.action == KeyEvent.ACTION_DOWN) }
    }

    private fun past(x: Int, y: Int): Boolean {
        val screenW = DisplaySize.width
        val screenH = DisplaySize.height
        return when (edge) {
            ScreenEdge.Right -> x <= -24 && x <= downVideoX - 24
            ScreenEdge.Left -> x + contentW >= screenW + 24 && x >= downVideoX + 24
            ScreenEdge.Bottom -> y <= -24 && y <= downVideoY - 24
            ScreenEdge.Top -> y + contentH >= screenH + 24 && y >= downVideoY + 24
            else -> false
        }
    }

    private fun sendGeometry(id: Long, force: Boolean) {
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastGeom < 50) return
        lastGeom = now
        val w = contentW
        val h = contentH
        val x = videoX
        val y = videoY
        actions.add { engine.handoffGeometry(id, x, y, w, h) }
    }

    private fun layout() {
        val view = root ?: return
        val lp = params ?: return
        val ctx = host ?: return
        lp.width = contentW
        lp.height = contentH + captionPx
        lp.x = videoX
        lp.y = videoY - captionPx
        val frame = (view as? LinearLayout)?.getChildAt(1)
        frame?.layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, contentH)
        runCatching { ctx.getSystemService(WindowManager::class.java)?.updateViewLayout(view, lp) }
    }

    private fun read(sock: Socket, key: ByteArray, id: Long, videoW: Int, videoH: Int) {
        var codec: MediaCodec? = null
        var seq = 1L
        var pts = 0L
        var asked = 0L
        var held: MediaFrames.Frame? = null
        streamEnded = false
        val input = sock.getInputStream()
        fun pump(decoder: MediaCodec) {
            val info = MediaCodec.BufferInfo()
            while (true) {
                val out = decoder.dequeueOutputBuffer(info, 0)
                if (out == MediaCodec.INFO_TRY_AGAIN_LATER) break
                if (out == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    val format = decoder.outputFormat
                    val w = if (format.containsKey(MediaFormat.KEY_WIDTH)) format.getInteger(MediaFormat.KEY_WIDTH) else contentW
                    val h = if (format.containsKey(MediaFormat.KEY_HEIGHT)) format.getInteger(MediaFormat.KEY_HEIGHT) else contentH
                    main.post { surfaceView?.holder?.setFixedSize(w.coerceAtLeast(2), h.coerceAtLeast(2)) }
                    continue
                }
                if (out >= 0) {
                    decoder.releaseOutputBuffer(out, true)
                    main.post { statusView?.visibility = View.GONE }
                }
            }
        }
        fun feed(decoder: MediaCodec, frame: MediaFrames.Frame) {
            val index = decoder.dequeueInputBuffer(8_000)
            if (index < 0) return
            val buf = decoder.getInputBuffer(index) ?: return
            if (buf.capacity() < frame.annexb.size) return
            buf.clear()
            buf.put(frame.annexb)
            pts += 16_666
            decoder.queueInputBuffer(index, 0, frame.annexb.size, pts, 0)
            pump(decoder)
        }
        try {
            while (running && liveId == id) {
                val lenBuf = readFully(input, 4)
                if (lenBuf == null) {
                    if (streamEnded || !running) break
                    val pending = held
                    val current = surface
                    if (codec == null && pending != null && current != null) {
                        codec = startCodec(current, videoW.coerceAtLeast(2), videoH.coerceAtLeast(2), id)
                        if (codec == null) break
                        feed(codec!!, pending)
                        held = null
                    }
                    continue
                }
                val n = (lenBuf[0].toInt() and 0xff) or ((lenBuf[1].toInt() and 0xff) shl 8) or ((lenBuf[2].toInt() and 0xff) shl 16) or ((lenBuf[3].toInt() and 0xff) shl 24)
                if (n < 16 || n > 8_000_000) break
                val rest = readFully(input, n) ?: break
                val body = ByteArray(4 + n)
                lenBuf.copyInto(body)
                rest.copyInto(body, 4)
                val frame = MediaFrames.open(key, seq, body)
                seq++
                if (frame == null) {
                    val now = SystemClock.elapsedRealtime()
                    if (now - asked > 500) {
                        asked = now
                        actions.add { engine.handoffVideo(id, true) }
                    }
                    continue
                }
                if ((frame.flags and 2) != 0) {
                    val bmp = android.graphics.BitmapFactory.decodeByteArray(frame.annexb, 0, frame.annexb.size)
                    if (bmp != null) {
                        main.post {
                            imageView?.setImageBitmap(bmp)
                            imageView?.visibility = View.VISIBLE
                            surfaceView?.visibility = View.GONE
                            statusView?.visibility = View.GONE
                        }
                    }
                    continue
                }
                if (frame.flags == 1) held = frame
                val current = surface
                if (codec == null && current != null) {
                    codec = startCodec(current, videoW.coerceAtLeast(2), videoH.coerceAtLeast(2), id)
                    if (codec == null) break
                    val start = held
                    if (start != null && start !== frame) feed(codec!!, start)
                    held = null
                }
                val decoder = codec ?: continue
                feed(decoder, frame)
            }
        } catch (_: Exception) {
            if (running && liveId == id) fail(id, "handoffNoLink")
        } finally {
            runCatching { codec?.stop() }
            runCatching { codec?.release() }
            runCatching { sock.close() }
        }
    }

    private fun startCodec(target: Surface, width: Int, height: Int, id: Long): MediaCodec? = try {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
        if (Build.VERSION.SDK_INT >= 30) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4_000_000)
        val codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        codec.configure(format, target, null, 0)
        codec.start()
        codec
    } catch (_: Exception) {
        fail(id, "handoffNoEncoder")
        null
    }

    private var streamEnded = false

    private fun readFully(input: java.io.InputStream, n: Int): ByteArray? {
        val buf = ByteArray(n)
        var off = 0
        while (off < n) {
            if (!running) return null
            val got = try {
                input.read(buf, off, n - off)
            } catch (_: java.net.SocketTimeoutException) {
                if (off == 0) return null
                continue
            }
            if (got < 0) {
                streamEnded = true
                return null
            }
            off += got
        }
        return buf
    }

    private fun drop(id: Long) {
        synchronized(gate) {
            if (id != 0L && liveId != id) return
            liveId = 0L
            running = false
        }
        runCatching { socket?.close() }
        socket = null
        main.post {
            val view = root ?: return@post
            val ctx = host
            root = null
            surfaceView = null
            imageView = null
            statusView = null
            params = null
            surface = null
            if (ctx != null) runCatching { ctx.getSystemService(WindowManager::class.java)?.removeView(view) }
        }
    }

    private fun showNote(text: String) {
        val ctx = owner() ?: return
        val view = TextView(ctx).apply {
            this.text = text
            setTextColor(Color.WHITE)
            setBackgroundColor(Color.parseColor("#CC12141C"))
            setPadding(32, 24, 32, 24)
        }
        val lp = windowParams(WindowManager.LayoutParams.WRAP_CONTENT, WindowManager.LayoutParams.WRAP_CONTENT)
        lp.gravity = Gravity.CENTER
        val wm = ctx.getSystemService(WindowManager::class.java) ?: return
        runCatching { wm.addView(view, lp) }
        main.postDelayed({ runCatching { wm.removeView(view) } }, 2800)
    }

    private fun owner(): Context? {
        if (EdgeAccessibility.instance != null) return EdgeAccessibility.instance
        if (!Settings.canDrawOverlays(context)) return null
        return context
    }

    private fun windowParams(width: Int, height: Int): WindowManager.LayoutParams {
        val type = when {
            EdgeAccessibility.instance != null -> WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY
            Build.VERSION.SDK_INT >= 26 -> WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
            else -> WindowManager.LayoutParams.TYPE_PHONE
        }
        val lp = WindowManager.LayoutParams(
            width,
            height,
            type,
            WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED,
            PixelFormat.TRANSLUCENT
        )
        lp.gravity = Gravity.TOP or Gravity.START
        if (Build.VERSION.SDK_INT >= 28) {
            lp.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        return lp
    }

    private fun vk(code: Int) = when (code) {
        in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z -> 0x41 + (code - KeyEvent.KEYCODE_A)
        in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 -> 0x30 + (code - KeyEvent.KEYCODE_0)
        KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> 0x0D
        KeyEvent.KEYCODE_SPACE -> 0x20
        KeyEvent.KEYCODE_DEL -> 0x08
        KeyEvent.KEYCODE_FORWARD_DEL -> 0x2E
        KeyEvent.KEYCODE_ESCAPE -> 0x1B
        KeyEvent.KEYCODE_TAB -> 0x09
        KeyEvent.KEYCODE_DPAD_LEFT -> 0x25
        KeyEvent.KEYCODE_DPAD_UP -> 0x26
        KeyEvent.KEYCODE_DPAD_RIGHT -> 0x27
        KeyEvent.KEYCODE_DPAD_DOWN -> 0x28
        KeyEvent.KEYCODE_SHIFT_LEFT, KeyEvent.KEYCODE_SHIFT_RIGHT -> 0x10
        KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_CTRL_RIGHT -> 0x11
        KeyEvent.KEYCODE_ALT_LEFT, KeyEvent.KEYCODE_ALT_RIGHT -> 0x12
        else -> 0
    }
}
