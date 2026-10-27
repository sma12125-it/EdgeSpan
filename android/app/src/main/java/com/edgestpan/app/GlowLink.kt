package com.edgestpan.app

import android.content.SharedPreferences
import com.edgestpan.protocol.ElasticLine

/** Edge-line shape shared by the overlay, settings screen and the live session (synced with the PC). */
object GlowLink {
    /** Sends the current glow prefs to the PC; set while a session runs. */
    @Volatile var send: (() -> Unit)? = null
    /** Stroke multiplier: lineWidth / 9 (9 = the original stroke). */
    @Volatile var lineScale = 1f

    fun apply(prefs: SharedPreferences) {
        lineScale = prefs.getInt("glowLine", 9).coerceIn(1, 20) / 9f
        ElasticLine.Sigma = ElasticLine.sigmaFor(prefs.getInt("glowBend", 55))
    }
}
