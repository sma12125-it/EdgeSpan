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

class AdbInjector(private val context: android.content.Context? = null) : Injector {
    private var socket: Socket? = null
    @Volatile private var out: OutputStream? = null

    /** True while the socket to the UHID injection server is usable. */
    val alive: Boolean get() = out != null && socket?.isClosed == false

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
            try {
                val input = s.getInputStream()
                val header = ByteArray(3)
                while (!s.isClosed) {
                    if (input.read(header) < 3) break
                    if ((header[2].toInt() and 0xff) == 9) EdgeGlowOverlay.fingerFromServer()
                }
            } catch (thrown: Throwable) {
                android.util.Log.e("EdgeSpan", "finger reader", thrown)
            }
        }, "edgestpan-finger").apply { isDaemon = true }.start()
        true
    } catch (thrown: Exception) {
        context?.let { CrashLog.note(it, "adb connect 127.0.0.1:$port failed: ${thrown.javaClass.simpleName} ${thrown.message}") }
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
        try {
            out?.write(frame)
            out?.flush()
        } catch (thrown: java.io.IOException) {
            android.util.Log.e("EdgeSpan", "adb write", thrown)
            context?.let { CrashLog.caught(it, "adb write (injection server gone)", thrown) }
            out = null
            runCatching { socket?.close() }
        }
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

class AccessInjector(
    val service: AccessibilityService,
    private val onBroken: (String, Throwable?) -> Unit = { _, _ -> }
) : Injector {
    @Volatile var broken = false
        private set
    private val main = Handler(Looper.getMainLooper())
    private val wm = service.getSystemService(WindowManager::class.java)
    private val cursor = View(service).apply { setBackgroundColor(0xFF3DDC84.toInt()) }
    private var added = false
    private var down = false

    override fun onEvent(event: ClientEvent) {
        main.post {
            try {
                apply(event)
            } catch (thrown: Throwable) {
                // A stale accessibility token (service restarted) throws BadTokenException here.
                android.util.Log.e("EdgeSpan", "access inject ${event.javaClass.simpleName}", thrown)
                added = cursor.parent != null
                if (event is ClientEvent.EdgeEntered || event is ClientEvent.Mouse) {
                    if (!broken) {
                        broken = true
                        CrashLog.caught(service, "access cursor overlay", thrown)
                        onBroken("overlay", thrown)
                    }
                }
            }
        }
    }

    private fun apply(event: ClientEvent) {
        if (EdgeAccessibility.instance !== service && (event is ClientEvent.EdgeEntered || event is ClientEvent.Mouse)) {
            throw IllegalStateException("accessibility service instance is stale or unbound")
        }
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
        val manager = wm ?: throw IllegalStateException("no WindowManager for accessibility overlay")
        if (!added || cursor.parent == null) {
            manager.addView(cursor, params)
            added = true
        } else manager.updateViewLayout(cursor, params)
    }

    override fun hidePointer() = hide()

    private fun hide() {
        if (added) {
            runCatching { wm?.removeView(cursor) }
            added = false
        }
    }

    private fun stroke(x1: Int, y1: Int, x2: Int, y2: Int, duration: Long) {
        val path = Path().apply { moveTo(x1.toFloat(), y1.toFloat()); lineTo(x2.toFloat(), y2.toFloat()) }
        val stroke = GestureDescription.StrokeDescription(path, 0, duration)
        runCatching { service.dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null) }
            .onSuccess { ok -> if (!ok) CrashLog.note(service, "dispatchGesture rejected (service not ready?)") }
            .onFailure { CrashLog.caught(service, "gesture", it) }
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

/**
 * Picks a working injection path every time control enters Android, instead of once per session:
 *   1. UHID injection server (USB/adb) when its socket is alive and the "EdgeSpan Mouse" device exists,
 *   2. the accessibility overlay cursor + gestures, re-acquiring the current service instance,
 *   3. UHID without a verified mouse device (last resort).
 * Keys go to the UHID keyboard when alive, else to the IME bridge.
 * If nothing works, [onNoPath] is called (on any thread) so the session can return control and tell the user.
 */
class InjectorRouter(
    private val context: android.content.Context,
    private val adbAllowed: Boolean,
    private val size: () -> Pair<Int, Int>,
    private val mousePresent: () -> Boolean,
    private val onNoPath: (String) -> Unit
) : Injector {
    private var adb: AdbInjector? = null
    private var access: AccessInjector? = null
    private var pointer: Injector? = null
    private var lastAdbTry = 0L
    var path: String = "none"
        private set

    /** Used before the session starts to prime the UHID server connection. */
    fun primeAdb(): Boolean = ensureAdb(force = true)

    val adbAlive: Boolean get() = adb?.alive == true

    fun resize(width: Int, height: Int) { adb?.resize(width, height) }

    private fun ensureAdb(force: Boolean = false): Boolean {
        if (!adbAllowed) return false
        if (adb?.alive == true) return true
        val now = android.os.SystemClock.elapsedRealtime()
        if (!force && now - lastAdbTry < 1500) return false
        lastAdbTry = now
        adb?.close()
        val (w, h) = size()
        val fresh = AdbInjector(context)
        val ok = fresh.connect(w, h)
        adb = if (ok) fresh else null
        CrashLog.note(context, if (ok) "uhid-connected" else "uhid-unavailable")
        return ok
    }

    private fun currentAccess(): AccessInjector? {
        var service = EdgeAccessibility.instance
        if (service == null && EdgeAccessibility.isEnabled(context)) {
            // Enabled but between onUnbind and onServiceConnected: wait briefly for the rebind.
            val deadline = android.os.SystemClock.elapsedRealtime() + 700
            while (service == null && android.os.SystemClock.elapsedRealtime() < deadline) {
                Thread.sleep(50)
                service = EdgeAccessibility.instance
            }
        }
        if (service == null) {
            access?.close(); access = null
            return null
        }
        val existing = access
        if (existing != null && existing.service === service && !existing.broken) return existing
        existing?.close()
        val made = AccessInjector(service) { why, _ ->
            CrashLog.note(context, "accessibility cursor failed ($why); falling back")
            if (pointer === access) {
                pointer = null
                path = "none"
                if (!adbAliveWithMouse()) onNoPath("overlay")
            }
        }
        access = made
        return made
    }

    private fun adbAliveWithMouse() = ensureAdb() && mousePresent()

    private fun select(): Injector? {
        val chosen: Injector?
        if (adbAliveWithMouse()) {
            chosen = adb; path = "uhid"
        } else {
            val a = currentAccess()
            if (a != null) {
                chosen = a; path = "accessibility"
            } else if (adb?.alive == true) {
                chosen = adb; path = "uhid-unverified"
            } else {
                chosen = null; path = "none"
            }
        }
        CrashLog.note(context, "injector path=$path adbAllowed=$adbAllowed adbAlive=${adb?.alive == true} mouse=${runCatching { mousePresent() }.getOrDefault(false)} access=${EdgeAccessibility.instance != null} accessEnabled=${EdgeAccessibility.isEnabled(context)}")
        if (pointer !== chosen && pointer is AccessInjector) pointer?.hidePointer()
        pointer = chosen
        return chosen
    }

    override fun onEvent(event: ClientEvent) {
        when (event) {
            is ClientEvent.EdgeEntered -> {
                val p = select()
                if (p == null) {
                    onNoPath(if (EdgeAccessibility.isEnabled(context)) "unbound" else "access-off")
                    return
                }
                p.onEvent(event)
            }
            is ClientEvent.Key -> {
                if (ensureAdb()) adb?.onEvent(event) else (access ?: currentAccess())?.onEvent(event)
            }
            else -> {
                var p = pointer
                if (p is AdbInjector && !p.alive) {
                    CrashLog.note(context, "uhid socket lost mid-control; reselecting")
                    p = select()
                    if (p == null) { onNoPath("lost"); return }
                }
                p?.onEvent(event)
                if (p === adb && adb?.alive == false) {
                    p = select()
                    if (p == null) onNoPath("lost") else p.onEvent(event)
                }
            }
        }
    }

    override fun releaseModifiers() { adb?.releaseModifiers() }

    override fun hidePointer() { access?.hidePointer(); pointer?.hidePointer() }

    override fun close() {
        adb?.close(); access?.close()
    }
}

/** User-facing alert when no injection path works. */
object InjectAlert {
    private const val Channel = "edgestpan-alert"

    fun show(context: android.content.Context, reason: String) {
        val accessOff = reason == "access-off" || reason == "unbound" || reason == "overlay"
        val title = "EdgeSpan can't control this device"
        val text = if (accessOff) "Accessibility service is off \u2013 tap to enable" else "Input connection lost \u2013 control returned to Windows"
        CrashLog.note(context, "no injection path ($reason): $text")
        try {
            val manager = context.getSystemService(android.app.NotificationManager::class.java) ?: return
            manager.createNotificationChannel(android.app.NotificationChannel(Channel, "EdgeSpan alerts", android.app.NotificationManager.IMPORTANCE_HIGH))
            val intent = if (accessOff) android.content.Intent(android.provider.Settings.ACTION_ACCESSIBILITY_SETTINGS)
            else android.content.Intent(context, MainActivity::class.java)
            intent.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
            val pending = android.app.PendingIntent.getActivity(context, 11, intent, android.app.PendingIntent.FLAG_IMMUTABLE or android.app.PendingIntent.FLAG_UPDATE_CURRENT)
            val note = android.app.Notification.Builder(context, Channel)
                .setContentTitle(title)
                .setContentText(text)
                .setSmallIcon(R.drawable.ic_stat_edgestpan)
                .setContentIntent(pending)
                .setAutoCancel(true)
                .build()
            manager.notify(12, note)
        } catch (thrown: Throwable) {
            CrashLog.caught(context, "inject alert", thrown)
        }
        Handler(Looper.getMainLooper()).post {
            runCatching { android.widget.Toast.makeText(context.applicationContext, text, android.widget.Toast.LENGTH_LONG).show() }
        }
    }
}

object ImeBridge {
    fun commit(text: String) {
        val ime = Input.current ?: return
        runCatching { ime.currentInputConnection?.commitText(text, 1) }
    }
}

object Input {
    var current: android.inputmethodservice.InputMethodService? = null
}

@Suppress("unused")
private val utf8: Charset = Charsets.UTF_8
