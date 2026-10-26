package com.edgestpan.server

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class AppStreamTextTest {
    @Test
    fun findsTopResumedActivity() {
        val dump = "  topResumedActivity=ActivityRecord{2b1c u0 com.android.chrome/com.google.android.apps.chrome.Main t12}\n"
        assertEquals("com.android.chrome/com.google.android.apps.chrome.Main", AppStreamText.resumedComponent(dump))
    }

    @Test
    fun findsOlderResumedActivity() {
        val dump = "    mResumedActivity: ActivityRecord{9f1 u0 org.example/.MainActivity t3}"
        assertEquals("org.example/.MainActivity", AppStreamText.resumedComponent(dump))
        assertNull(AppStreamText.resumedComponent("nothing here"))
    }
}
