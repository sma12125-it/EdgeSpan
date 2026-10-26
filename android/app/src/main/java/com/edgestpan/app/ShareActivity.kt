package com.edgestpan.app

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity

/** Share target so a file copied from the Android file manager can cross to the PC. */
class ShareActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = mutableListOf<Uri>()
        when (intent?.action) {
            Intent.ACTION_SEND -> intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)?.let { uris += it }
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.let { uris += it }
        }
        uris.forEach { uri ->
            runCatching { ClipHub.readUri(this, uri, cut = false) }
        }
        val text = intent?.getStringExtra(Intent.EXTRA_TEXT)
        if (!text.isNullOrBlank()) ClipHub.offerText(text)
        startForegroundService(Intent(this, SessionService::class.java))
        startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP))
        finish()
    }
}
