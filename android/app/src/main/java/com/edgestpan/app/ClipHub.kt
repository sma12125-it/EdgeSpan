package com.edgestpan.app

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import java.io.File
import java.util.ArrayDeque
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Text and files waiting to cross to the PC, and files that arrived from it.
 * Android 10+ only lets the focused app, the IME, or an active accessibility pass read the clipboard.
 */
object ClipHub {
    data class OutFile(val name: String, val bytes: ByteArray, val cut: Boolean, val delete: File?)

    private val texts = ArrayDeque<String>()
    private val files = ArrayDeque<OutFile>()
    private val suppress = AtomicBoolean(false)
    @Volatile var pendingRead = false
    @Volatile var lastIncoming = ""
    @Volatile var lastOffered = ""

    fun onClipboardChanged() {
        if (suppress.get()) return
        pendingRead = true
    }

    fun offerText(text: String) {
        if (text.isBlank() || text == lastIncoming) return
        synchronized(texts) {
            if (texts.isNotEmpty() && texts.last() == text) return
            while (texts.size >= 2) texts.removeFirst()
            texts.addLast(text)
        }
        lastOffered = text
    }

    fun offerFile(name: String, bytes: ByteArray, cut: Boolean, delete: File? = null) {
        if (bytes.size > 512 * 1024 * 1024) return
        synchronized(files) { files.addLast(OutFile(name, bytes, cut, delete)) }
    }

    fun pollText(): String? = synchronized(texts) { if (texts.isEmpty()) null else texts.removeFirst() }

    fun pollFile(): OutFile? = synchronized(files) { if (files.isEmpty()) null else files.removeFirst() }

    fun readIfAllowed(context: Context): Boolean {
        if (!pendingRead) return false
        val manager = context.getSystemService(ClipboardManager::class.java) ?: return false
        return try {
            val clip = manager.primaryClip
            pendingRead = false
            if (clip == null || clip.itemCount == 0) return false
            val item = clip.getItemAt(0)
            val uri = item.uri
            if (uri != null) {
                readUri(context, uri, cut = false)
                true
            } else {
                val text = item.coerceToText(context)?.toString().orEmpty()
                if (text.isNotBlank() && text != lastIncoming && text != lastOffered) offerText(text)
                text.isNotBlank()
            }
        } catch (_: SecurityException) {
            pendingRead = true
            false
        } catch (_: Exception) {
            false
        }
    }

    fun readUri(context: Context, uri: Uri, cut: Boolean) {
        val name = queryName(context, uri)
        val bytes = context.contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return
        val file = if (cut && uri.scheme == "file") uri.path?.let { File(it) } else null
        offerFile(name, bytes, cut, file)
    }

    fun putText(context: Context, text: String) {
        lastIncoming = text
        lastOffered = text
        suppress.set(true)
        try {
            val manager = context.getSystemService(ClipboardManager::class.java) ?: return
            manager.setPrimaryClip(ClipData.newPlainText("EdgeSpan", text))
        } catch (_: Exception) {
        } finally {
            suppress.set(false)
        }
    }

    fun receivedDir(context: Context): File {
        val dir = File(context.getExternalFilesDir(null) ?: context.filesDir, "Received")
        dir.mkdirs()
        return dir
    }

    private fun queryName(context: Context, uri: Uri): String {
        val cursor = context.contentResolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)
        cursor?.use {
            if (it.moveToFirst()) {
                val index = it.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                if (index >= 0) return it.getString(index) ?: "file"
            }
        }
        return uri.lastPathSegment ?: "file"
    }
}
