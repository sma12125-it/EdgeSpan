package com.edgestpan.app

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent

class EdgeAccessibility : AccessibilityService() {
    override fun onServiceConnected() {
        instance = this
        CrashLog.note(this, "accessibility connected")
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        val type = event?.eventType ?: return
        if (type == AccessibilityEvent.TYPE_TOUCH_INTERACTION_START && EdgeGlowOverlay.allowFinger()) {
            EdgeGlowOverlay.serverFinger()
        }
        if (type == AccessibilityEvent.TYPE_VIEW_FOCUSED || type == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED || type == AccessibilityEvent.TYPE_VIEW_CLICKED) {
            ClipHub.readIfAllowed(this)
        }
    }
    override fun onInterrupt() = Unit

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        if (instance === this) instance = null
        CrashLog.note(this, "accessibility unbound")
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    companion object {
        @Volatile var instance: EdgeAccessibility? = null

        fun isEnabled(context: android.content.Context): Boolean = try {
            val list = android.provider.Settings.Secure.getString(context.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: ""
            list.split(':').any { it.equals("${context.packageName}/${EdgeAccessibility::class.java.name}", true) || it.equals("${context.packageName}/.EdgeAccessibility", true) }
        } catch (_: Exception) {
            false
        }
    }
}
