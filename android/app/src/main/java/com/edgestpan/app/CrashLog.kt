package com.edgestpan.app

import android.app.Application
import android.content.Context
import android.os.Build
import android.util.Log
import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Writes uncaught exceptions to files/crash.log so Settings > About can export them. */
object CrashLog {
    private const val Name = "crash.log"
    @Volatile private var installed = false

    fun file(context: Context): File = File(context.filesDir, Name)

    fun install(context: Context) {
        if (installed) return
        installed = true
        val app = context.applicationContext ?: context
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, thrown ->
            try {
                Log.e(PhoneSession.TAG, "crash in ${thread.name}", thrown)
                write(app, "uncaught in ${thread.name}", thrown)
            } catch (_: Throwable) {
            }
            previous?.uncaughtException(thread, thrown)
        }
    }

    /** Records a caught failure without crashing. */
    fun caught(context: Context, where: String, thrown: Throwable) {
        Log.e(PhoneSession.TAG, where, thrown)
        try {
            write(context.applicationContext ?: context, "caught $where", thrown)
        } catch (_: Throwable) {
        }
    }

    private fun write(context: Context, title: String, thrown: Throwable) {
        val trace = StringWriter().also { thrown.printStackTrace(PrintWriter(it)) }.toString()
        val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
        val file = file(context)
        file.appendText("=== $stamp $title (Android ${Build.VERSION.RELEASE} / SDK ${Build.VERSION.SDK_INT}, ${Build.MANUFACTURER} ${Build.MODEL}, app ${BuildConfigVersion.name(context)})\n$trace\n")
        if (file.length() > 300_000) {
            val text = file.readText()
            file.writeText(text.substring(text.length - 150_000))
        }
    }

    fun read(context: Context): String = try {
        val file = file(context)
        if (file.exists()) file.readText() else ""
    } catch (_: Exception) {
        ""
    }
}

internal object BuildConfigVersion {
    fun name(context: Context): String = try {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "?"
    } catch (_: Exception) {
        "?"
    }
}

class EdgeSpanApp : Application() {
    override fun onCreate() {
        CrashLog.install(this)
        super.onCreate()
    }
}
