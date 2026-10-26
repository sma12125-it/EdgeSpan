package com.edgestpan.app

import androidx.test.ext.junit.rules.ActivityScenarioRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertNotNull
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class LaunchTest {
    @get:Rule
    val rule = ActivityScenarioRule(MainActivity::class.java)

    @Test
    fun activityStaysOpenAfterLaunch() {
        Thread.sleep(1500)
        rule.scenario.onActivity { activity ->
            assertNotNull(activity)
            activity.finish()
        }
    }
}
