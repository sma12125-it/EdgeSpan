package com.edgestpan.app

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import com.edgestpan.protocol.ClientEvent
import com.edgestpan.protocol.PointerBounds
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.charset.Charset

interface Injector {
    fun onEvent(event: ClientEvent)
    fun close()
    fun hidePointer() {}
    fun releaseModifiers() {}
}

class AdbInjector : Injector {
    private var socket: Socket? = null
    private var out: OutputStream? = null

    fun connect(width: Int, height: Int, port: Int = 27183): Boolean = try {
        val s = Socket()
        s.connect(InetSocketAddress("127.0.0.1", port), 400)
        socket = s
        out = s.getOutputStream()
        val hello = ByteArray(21)
        hello[16] = (width and 0xff).toByte()
        hello[17] = ((width shr 8) and 0xff).toByte()
        hello[18] = (height and 0xff).toByte()
        hello[19] = ((height shr 8) and 0xff).toByte()
        hello[20] = 1
        write(1, hello)
        Thread({
            val input = s.getInputStream()
            val header = ByteArray(3)
            while (!s.isClosed) {
                if (input.read(header) < 3) break
                if ((header[2].toInt() and 0xff) == 9) EdgeGlowOverlay.fingerFromServer()
            }
        }, "edgestpan-finger").apply { isDaemon = true }.start()
        true
    } catch (_: Exception) {
        false
    }

    fun resize(width: Int, height: Int) {
        if (out == null || width < 1 || height < 1) return
        val hello = ByteArray(21)
        hello[16] = (width and 0xff).toByte()
        hello[17] = ((width shr 8) and 0xff).toByte()
        hello[18] = (height and 0xff).toByte()
        hello[19] = ((height shr 8) and 0xff).toByte()
        hello[20] = 1
        write(1, hello)
    }

    override fun onEvent(event: ClientEvent) {
        when (event) {
            is ClientEvent.EdgeEntered -> write(3, i32(event.x) + i32(event.y))
            is ClientEvent.Mouse -> write(3, i32(event.x) + i32(event.y))
            is ClientEvent.Button -> write(4, byteArrayOf(event.button.toByte(), if (event.down) 1 else 0) + i32(event.x) + i32(event.y))
            is ClientEvent.Scroll -> write(5, i16(event.dx) + i16(event.dy) + i32(event.x) + i32(event.y))
            is ClientEvent.Key -> write(6, i16(event.vk) + i16(event.hid) + byteArrayOf(event.mods.toByte(), if (event.down) 1 else 0) + i32(event.codepoint.toInt()))
            is ClientEvent.EdgeReturned -> releaseModifiers()
            else -> Unit
        }
    }

    override fun releaseModifiers() {
        write(6, i16(0) + i16(0) + byteArrayOf(0, 0) + i32(0))
    }

    override fun close() {
        try { socket?.close() } catch (_: Exception) {}
    }

    private fun write(type: Int, payload: ByteArray) {
        val body = ByteArray(1 + payload.size)
        body[0] = type.toByte()
        payload.copyInto(body, 1)
        val frame = ByteArray(2 + body.size)
        frame[0] = body.size.toByte()
        frame[1] = (body.size shr 8).toByte()
        body.copyInto(frame, 2)
        out?.write(frame)
        out?.flush()
    }

    private fun i16(v: Int) = byteArrayOf(v.toByte(), (v shr 8).toByte())
    private fun i32(v: Int) = byteArrayOf(v.toByte(), (v shr 8).toByte(), (v shr 16).toByte(), (v shr 24).toByte())
}

class SplitInjector(private val keys: Injector, private val pointer: Injector) : Injector {
    override fun onEvent(event: ClientEvent) {
        when (event) {
            is ClientEvent.Key -> keys.onEvent(event)
            else -> pointer.onEvent(event)
        }
    }

    override fun close() {
        pointer.close()
        keys.close()
    }

    override fun releaseModifiers() {
        keys.releaseModifiers()
    }

    override fun hidePointer() {
        pointer.hidePointer()
    }
}

class AccessInjector(private val service: AccessibilityService) : Injector {
    private val main = Handler(Looper.getMainLooper())
    private val wm = service.getSystemService(WindowManager::class.java)
    private val cursor = View(service).apply { setBackgroundColor(0xFF3DDC84.toInt()) }
    private var added = false
    private var down = false

    override fun onEvent(event: ClientEvent) {
        main.post { apply(event) }
    }

    private fun apply(event: ClientEvent) {
        when (event) {
            is ClientEvent.EdgeEntered -> show(event.x, event.y)
            is ClientEvent.Mouse -> show(event.x, event.y)
            is ClientEvent.Button -> {
                show(event.x, event.y)
                if (event.down && !down) {
                    down = true
                    val hold = if (event.button == 1) 480L else 40L
                    stroke(event.x, event.y, event.x + 2, event.y + 2, hold)
                }
                if (!event.down) down = false
            }
            is ClientEvent.Scroll -> swipe(event.x, event.y, event.dx, event.dy)
            is ClientEvent.Key -> if (event.down && event.codepoint != 0L) ImeBridge.commit(String(Character.toChars(event.codepoint.toInt())))
            is ClientEvent.EdgeReturned -> hide()
            else -> Unit
        }
    }

    private fun show(x: Int, y: Int) {
        DisplaySize.ensure(service)
        val size = (12 * DisplaySize.density).toInt().coerceAtLeast(8)
        val placed = PointerBounds.clamp(x, y, DisplaySize.width, DisplaySize.height)
        val params = WindowManager.LayoutParams(
            size, size,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        )
        params.gravity = Gravity.TOP or Gravity.START
        EdgeGlowOverlay.coverBars(params)
        params.x = placed.first
        params.y = placed.second
        if (!added) {
            wm.addView(cursor, params)
            added = true
        } else wm.updateViewLayout(cursor, params)
    }

    override fun hidePointer() = hide()

    private fun hide() {
        if (added) {
            wm.removeView(cursor)
            added = false
        }
    }

    private fun stroke(x1: Int, y1: Int, x2: Int, y2: Int, duration: Long) {
        val path = Path().apply { moveTo(x1.toFloat(), y1.toFloat()); lineTo(x2.toFloat(), y2.toFloat()) }
        val stroke = GestureDescription.StrokeDescription(path, 0, duration)
        service.dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    private fun swipe(x: Int, y: Int, dx: Int, dy: Int) {
        val vertical = if (dy == 0) 0 else -dy.coerceIn(-8, 8) * 48
        val horizontal = if (dx == 0) 0 else -dx.coerceIn(-8, 8) * 48
        if (vertical == 0 && horizontal == 0) return
        stroke(x, y, x + horizontal, y + vertical, 50)
    }

    override fun close() {
        main.post { hide() }
    }
}

object ImeBridge {
    fun commit(text: String) {
        val ime = Input.current ?: return
        ime.currentInputConnection?.commitText(text, 1)
    }
}

object Input {
    var current: android.inputmethodservice.InputMethodService? = null
}

@Suppress("unused")
private val utf8: Charset = Charsets.UTF_8
