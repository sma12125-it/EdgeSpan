package com.edgestpan.app

import app.cash.paparazzi.DeviceConfig
import app.cash.paparazzi.Paparazzi
import org.json.JSONObject
import org.junit.Rule
import org.junit.Test

/** Golden renders of the phone UI. */
class EdgeSpanShots {
    @get:Rule
    val paparazzi = Paparazzi(deviceConfig = DeviceConfig.PIXEL_6.copy(softButtons = false))

    @Test
    fun onboardingDarkEn() = render("onboarding-dark-en", rtl = false, dark = true, onboarded = false)

    @Test
    fun homeDarkEn() = render(
        "home-dark-en",
        rtl = false,
        dark = true,
        onboarded = true,
        status = "has",
        link = "edgestpan://pair?v=1"
    )

    @Test
    fun homeLightFa() = render(
        "home-light-fa",
        rtl = true,
        dark = false,
        onboarded = true,
        status = "has",
        link = "edgestpan://pair?v=1"
    )

    private fun render(
        name: String,
        rtl: Boolean,
        dark: Boolean,
        onboarded: Boolean,
        status: String = "",
        link: String = ""
    ) {
        val lang = if (rtl) "fa" else "en"
        val strings = load(lang)
        paparazzi.snapshot(name) {
            EdgeSpanApp(
                strings = strings,
                rtl = rtl,
                dark = dark,
                link = link,
                name = if (rtl) "تبلت" else "Tablet",
                backend = "access",
                status = status,
                message = if (status == "has") "Connected on the local network." else "",
                pairState = if (status == "has") "ok" else "",
                relay = "",
                stun = "",
                onboarded = onboarded,
                onLanguage = {},
                onTheme = {},
                onSave = { _, _, _ -> },
                onPair = {},
                onRelay = {},
                onStun = {},
                onStart = {},
                onStop = {},
                onScan = {},
                onFinishOnboarding = {}
            )
        }
    }

    private fun load(lang: String): Map<String, String> {
        val text = paparazzi.context.assets.open("strings.json").bufferedReader().use { it.readText() }
        val root = JSONObject(text)
        val map = LinkedHashMap<String, String>()
        val keys = root.keys()
        while (keys.hasNext()) {
            val key = keys.next()
            val value = root.getJSONObject(key)
            map[key] = value.optString(lang, value.optString("en"))
        }
        return map
    }
}
