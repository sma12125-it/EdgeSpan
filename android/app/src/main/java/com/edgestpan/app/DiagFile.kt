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

    /** Handoff / app-stream stage line: logcat plus the exported diagnostics file. */
    fun stage(context: Context, line: String) {
        android.util.Log.i("EdgeSpan", line)
        append(context.applicationContext ?: context, line)
    }

    fun read(context: Context): String {
        return try {
            val file = file(context)
            if (!file.exists()) return ""
            val lines = file.readLines()
            // Injector/session chatter used to push every handoff line out of the last-200 window, so handoff and
            // app-stream stages are exported separately from the whole file.
            val stages = lines.filter { it.contains("  handoff") || it.contains("  appstream") }.takeLast(300)
            buildString {
                if (stages.isNotEmpty()) append("--- handoff / appstream stages ---\n").append(stages.joinToString("\n")).append("\n--- recent ---\n")
                append(lines.takeLast(400).joinToString("\n"))
            }
        } catch (_: Exception) {
            ""
        }
    }
}
