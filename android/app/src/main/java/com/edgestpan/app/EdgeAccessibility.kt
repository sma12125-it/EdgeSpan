package com.edgestpan.app

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent

class EdgeAccessibility : AccessibilityService() {
    override fun onServiceConnected() {
        instance = this
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

    override fun onDestroy() {
        if (instance === this) instance = null
        super.onDestroy()
    }

    companion object {
        var instance: EdgeAccessibility? = null
    }
}
