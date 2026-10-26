package com.edgestpan.app

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Append-only log the user can share when a window handoff fails. */
object DiagFile {
    private const val Name = "edgestpan.log"

    fun file(context: Context): File = File(context.filesDir, Name)

    fun append(context: Context, line: String) {
        try {
            val file = file(context)
            val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
            file.appendText("$stamp  $line\n")
            if (file.length() <= 200_000) return
            val text = file.readText()
            val cut = text.length / 2
            val nl = text.indexOf('\n', cut)
            file.writeText(text.substring(if (nl < 0) cut else nl + 1))
        } catch (_: Exception) {
        }
    }

    fun read(context: Context): String {
        return try {
            val file = file(context)
            if (!file.exists()) "" else file.readLines().takeLast(200).joinToString("\n")
        } catch (_: Exception) {
            ""
        }
    }
}
