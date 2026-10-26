package com.edgestpan.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.os.PowerManager
import android.os.SystemClock
import android.util.Log
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class SessionService : Service() {
    private val started = AtomicBoolean(false)
    /** False while the session should keep running. Distinct from [started]: that flag is already true on the worker thread. */
    private val stop = AtomicBoolean(false)
    private val generation = AtomicInteger()
    private var wake: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        try {
            startForeground(7, notification(getString(R.string.notification_text)))
        } catch (thrown: Throwable) {
            Log.e(PhoneSession.TAG, "foreground", thrown)
            report("error", thrown.message ?: "EdgeSpan could not stay running in the background.")
            stopSelf()
            return START_NOT_STICKY
        }
        if (wake?.isHeld != true) {
            runCatching {
                val pm = getSystemService(PowerManager::class.java)
                wake = pm?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "edgestpan:session")?.apply {
                    setReferenceCounted(false)
                    acquire()
                }
            }
        }
        val restart = intent?.getBooleanExtra("restart", false) == true
        if (restart) stop.set(true)
        if (started.get() && !restart) return START_STICKY
        val ticket = generation.incrementAndGet()
        Thread({
            if (restart) {
                val waitUntil = SystemClock.elapsedRealtime() + 4_000
                while (started.get() && SystemClock.elapsedRealtime() < waitUntil) {
                    try { Thread.sleep(40) } catch (_: InterruptedException) { break }
                }
            }
            if (!started.compareAndSet(false, true)) return@Thread
            stop.set(false)
            try {
                PhoneSession(this, stop, this::report).run()
            } catch (thrown: Throwable) {
                Log.e(PhoneSession.TAG, "uncaught", thrown)
                report("error", thrown.message ?: "Something went wrong while pairing.")
            } finally {
                started.set(false)
                if (generation.get() == ticket) {
                    val state = getSharedPreferences("edgestpan", MODE_PRIVATE).getString("pairState", "")
                    if (state != "ok") {
                        stopForeground(STOP_FOREGROUND_REMOVE)
                        stopSelf()
                    }
                }
            }
        }, "edgestpan-net").apply {
            setUncaughtExceptionHandler { _, thrown ->
                Log.e(PhoneSession.TAG, "thread", thrown)
                report("error", thrown.message ?: "Something went wrong while pairing.")
            }
            start()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        stop.set(true)
        runCatching { if (wake?.isHeld == true) wake?.release() }
        wake = null
        super.onDestroy()
    }

    private fun report(state: String, detail: String) {
        getSharedPreferences("edgestpan", MODE_PRIVATE).edit().putString("pairState", state).putString("pairDetail", detail).apply()
        Log.i(PhoneSession.TAG, "status $state $detail")
        sendBroadcast(Intent(ACTION_STATUS).setPackage(packageName))
        if (state == "ok") {
            val manager = getSystemService(NotificationManager::class.java)
            manager.notify(7, notification(detail))
        }
    }

    private fun notification(text: String): Notification {
        val channel = NotificationChannel("edgestpan", "EdgeSpan", NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        val pending = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, "edgestpan")
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_stat_edgestpan)
            .setContentIntent(pending)
            .build()
    }

    companion object {
        const val ACTION_STATUS = "com.edgestpan.STATUS"
    }
}
