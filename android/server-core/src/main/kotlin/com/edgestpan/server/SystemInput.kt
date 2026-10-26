package com.edgestpan.server

import java.lang.reflect.Method

/**
 * scrcpy-style injection through the hidden InputManager API, done by reflection because this
 * module is compiled as plain JVM code and only runs inside app_process (shell uid holds
 * INJECT_EVENTS). Mouse events use SOURCE_MOUSE + TOOL_TYPE_MOUSE (never gestures/touch),
 * keys are real KeyEvents from HID usages, so the IME is not needed.
 */
class SystemInput {
    private val manager: Any
    private val inject: Method
    private val motion = Class.forName("android.view.MotionEvent")
    private val props = Class.forName("android.view.MotionEvent\$PointerProperties")
    private val coords = Class.forName("android.view.MotionEvent\$PointerCoords")
    private val keyClass = Class.forName("android.view.KeyEvent")
    private val inputEvent = Class.forName("android.view.InputEvent")
    private val obtain: Method
    private val uptime: Method = Class.forName("android.os.SystemClock").getMethod("uptimeMillis")
    private var downTime = 0L
    private var lastButtons = 0
    private var meta = 0

    init {
        val global = runCatching { Class.forName("android.hardware.input.InputManagerGlobal") }.getOrNull()
        val holder = global ?: Class.forName("android.hardware.input.InputManager")
        manager = holder.getDeclaredMethod("getInstance").invoke(null)!!
        inject = manager.javaClass.getMethod("injectInputEvent", inputEvent, Int::class.javaPrimitiveType)
        obtain = motion.getMethod(
            "obtain", Long::class.javaPrimitiveType, Long::class.javaPrimitiveType, Int::class.javaPrimitiveType,
            Int::class.javaPrimitiveType, java.lang.reflect.Array.newInstance(props, 0).javaClass,
            java.lang.reflect.Array.newInstance(coords, 0).javaClass, Int::class.javaPrimitiveType,
            Int::class.javaPrimitiveType, Float::class.javaPrimitiveType, Float::class.javaPrimitiveType,
            Int::class.javaPrimitiveType, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType
        )
    }

    private fun now() = uptime.invoke(null) as Long

    private fun send(event: Any) {
        inject.invoke(manager, event, 0) // INJECT_INPUT_EVENT_MODE_ASYNC
    }

    private fun motionEvent(action: Int, x: Int, y: Int, buttons: Int, actionButton: Int = 0, vscroll: Float = 0f): Any {
        val p = props.getDeclaredConstructor().newInstance()
        props.getField("id").setInt(p, 0)
        props.getField("toolType").setInt(p, TOOL_MOUSE)
        val c = coords.getDeclaredConstructor().newInstance()
        coords.getField("x").setFloat(c, x.toFloat())
        coords.getField("y").setFloat(c, y.toFloat())
        coords.getField("pressure").setFloat(c, if (buttons != 0) 1f else 0f)
        coords.getField("size").setFloat(c, 1f)
        if (vscroll != 0f) coords.getMethod("setAxisValue", Int::class.javaPrimitiveType, Float::class.javaPrimitiveType).invoke(c, AXIS_VSCROLL, vscroll)
        val pa = java.lang.reflect.Array.newInstance(props, 1).also { java.lang.reflect.Array.set(it, 0, p) }
        val ca = java.lang.reflect.Array.newInstance(coords, 1).also { java.lang.reflect.Array.set(it, 0, c) }
        val t = now()
        val event = obtain.invoke(null, if (downTime == 0L) t else downTime, t, action, 1, pa, ca, meta, buttons, 1f, 1f, VIRTUAL_DEVICE, 0, SOURCE_MOUSE, 0)!!
        if (actionButton != 0) runCatching { motion.getMethod("setActionButton", Int::class.javaPrimitiveType).invoke(event, actionButton) }
        return event
    }

    /** Absolute pointer state from the server: position, Windows-style button bits (1 left, 2 right, 4 middle), wheel notches. */
    @Synchronized
    fun pointer(x: Int, y: Int, buttonBits: Int, wheel: Int) {
        val buttons = toAndroid(buttonBits)
        val changed = buttons xor lastButtons
        if (changed == 0) {
            send(motionEvent(if (buttons == 0) HOVER_MOVE else MOVE, x, y, buttons))
        } else {
            for (bit in listOf(BUTTON_PRIMARY, BUTTON_SECONDARY, BUTTON_TERTIARY)) {
                if ((changed and bit) == 0) continue
                val pressed = (buttons and bit) != 0
                if (pressed) {
                    val before = lastButtons
                    lastButtons = lastButtons or bit
                    if (before == 0) { downTime = now(); send(motionEvent(DOWN, x, y, lastButtons)) }
                    send(motionEvent(BUTTON_PRESS, x, y, lastButtons, bit))
                } else {
                    lastButtons = lastButtons and bit.inv()
                    send(motionEvent(BUTTON_RELEASE, x, y, lastButtons, bit))
                    if (lastButtons == 0) { send(motionEvent(UP, x, y, 0)); downTime = 0L }
                }
            }
        }
        lastButtons = buttons
        if (wheel != 0) send(motionEvent(SCROLL, x, y, buttons, vscroll = wheel.toFloat()))
    }

    @Synchronized
    fun key(vk: Int, hid: Int, down: Boolean) {
        if (vk == 0 && hid == 0 && !down) { meta = 0; return }
        val code = modifier(vk) ?: HidKeys.toAndroid(hid) ?: return
        meta = when (code) {
            113, 114 -> flag(meta, META_CTRL, down)
            59, 60 -> flag(meta, META_SHIFT, down)
            57, 58 -> flag(meta, META_ALT, down)
            117, 118 -> flag(meta, META_META, down)
            else -> meta
        }
        val t = now()
        val event = keyClass.getConstructor(
            Long::class.javaPrimitiveType, Long::class.javaPrimitiveType, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType,
            Int::class.javaPrimitiveType, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType, Int::class.javaPrimitiveType,
            Int::class.javaPrimitiveType, Int::class.javaPrimitiveType
        ).newInstance(t, t, if (down) 0 else 1, code, 0, meta, VIRTUAL_DEVICE, 0, 0, SOURCE_KEYBOARD)
        send(event)
    }

    private fun flag(m: Int, bit: Int, on: Boolean) = if (on) m or bit else m and bit.inv()

    private fun modifier(vk: Int): Int? = when (vk) {
        0xA2, 0x11 -> 113; 0xA3 -> 114; 0xA0, 0x10 -> 59; 0xA1 -> 60
        0xA4, 0x12 -> 57; 0xA5 -> 58; 0x5B -> 117; 0x5C -> 118
        else -> null
    }

    companion object {
        const val SOURCE_MOUSE = 0x2002
        const val SOURCE_KEYBOARD = 0x101
        const val TOOL_MOUSE = 3
        const val AXIS_VSCROLL = 9
        const val VIRTUAL_DEVICE = -1
        const val DOWN = 0; const val UP = 1; const val MOVE = 2; const val SCROLL = 8
        const val HOVER_MOVE = 7; const val BUTTON_PRESS = 11; const val BUTTON_RELEASE = 12
        const val BUTTON_PRIMARY = 1; const val BUTTON_SECONDARY = 2; const val BUTTON_TERTIARY = 4
        const val META_SHIFT = 0x1; const val META_ALT = 0x2; const val META_CTRL = 0x1000; const val META_META = 0x10000

        /** Server button bits follow the HID report: bit0 left, bit1 right, bit2 middle. */
        fun toAndroid(bits: Int): Int =
            (if ((bits and 1) != 0) BUTTON_PRIMARY else 0) or
                (if ((bits and 2) != 0) BUTTON_SECONDARY else 0) or
                (if ((bits and 4) != 0) BUTTON_TERTIARY else 0)
    }
}

/** HID keyboard usage (page 7) to Android KeyEvent keycode. */
object HidKeys {
    private val table: Map<Int, Int> = buildMap {
        for (i in 0 until 26) put(0x04 + i, 29 + i)          // A..Z
        for (i in 0 until 9) put(0x1E + i, 8 + i)            // 1..9
        put(0x27, 7)                                          // 0
        put(0x28, 66); put(0x29, 111); put(0x2A, 67); put(0x2B, 61); put(0x2C, 62)
        put(0x2D, 69); put(0x2E, 70); put(0x2F, 71); put(0x30, 72); put(0x31, 73)
        put(0x33, 74); put(0x34, 75); put(0x35, 68); put(0x36, 55); put(0x37, 56); put(0x38, 76)
        put(0x39, 115)
        for (i in 0 until 12) put(0x3A + i, 131 + i)         // F1..F12
        put(0x46, 120); put(0x47, 116); put(0x48, 121); put(0x49, 124); put(0x4A, 122); put(0x4B, 92)
        put(0x4C, 112); put(0x4D, 123); put(0x4E, 93); put(0x4F, 22); put(0x50, 21); put(0x51, 20); put(0x52, 19)
        put(0x53, 143); put(0x54, 154); put(0x55, 155); put(0x56, 156); put(0x57, 157); put(0x58, 160)
        for (i in 0 until 9) put(0x59 + i, 145 + i)          // keypad 1..9
        put(0x62, 144); put(0x63, 158); put(0x65, 82)
        put(0xE0, 113); put(0xE1, 59); put(0xE2, 57); put(0xE3, 117); put(0xE4, 114); put(0xE5, 60); put(0xE6, 58); put(0xE7, 118)
    }

    fun toAndroid(hid: Int): Int? = table[hid]
}
