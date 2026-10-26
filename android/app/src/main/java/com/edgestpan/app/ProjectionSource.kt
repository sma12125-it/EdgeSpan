package com.edgestpan.app

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Path
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import android.view.Surface
import com.edgestpan.protocol.AppStreamWire
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Fallback when no shell server runs: mirrors the whole phone screen with MediaProjection while the chosen
 * app is in front. Input from the PC becomes accessibility taps/swipes when that service is on.
 */
class ProjectionSource(private val projection: MediaProjection, override val width: Int, override val height: Int, dpi: Int) : AppFrameSource {
    private val codec: MediaCodec
    private val surface: Surface
    private val display: VirtualDisplay
    private var downX = 0
    private var downY = 0

    init {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
        format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
        format.setInteger(MediaFormat.KEY_BIT_RATE, AppStreamBitrate.forSize(width, height))
        format.setInteger(MediaFormat.KEY_FRAME_RATE, 60)
        format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
        format.setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000)
        codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        surface = codec.createInputSurface()
        codec.start()
        if (Build.VERSION.SDK_INT >= 34) projection.registerCallback(object : MediaProjection.Callback() {}, Handler(Looper.getMainLooper()))
        display = projection.createVirtualDisplay("EdgeSpan-mirror", width, height, dpi, DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, surface, null, null)
    }

    override fun run(stop: AtomicBoolean, sink: (Long, Boolean, ByteArray) -> Unit) {
        val info = MediaCodec.BufferInfo()
        var config: ByteArray? = null
        while (!stop.get()) {
            val index = try { codec.dequeueOutputBuffer(info, 100_000) } catch (_: IllegalStateException) { break }
            if (index < 0) continue
            val buffer = codec.getOutputBuffer(index)
            if (buffer != null && info.size > 0) {
                buffer.position(info.offset)
                buffer.limit(info.offset + info.size)
                val data = ByteArray(info.size).also { buffer.get(it) }
                if ((info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) config = data
                else {
                    val key = (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0
                    val c = config
                    try { sink(info.presentationTimeUs, key, if (key && c != null) c + data else data) } catch (_: Exception) { stop.set(true) }
                }
            }
            codec.releaseOutputBuffer(index, false)
        }
    }

    override fun input(event: AppStreamWire.Input) {
        val service = EdgeAccessibility.instance ?: return
        when (event.kind) {
            AppStreamWire.InputButton -> if (event.button == 1) {
                if (event.down) { downX = event.x; downY = event.y }
                else stroke(service, downX, downY, event.x, event.y, if (downX == event.x && downY == event.y) 40 else 220)
            } else if (event.button == 2 && !event.down) service.performGlobalAction(AccessibilityService.GLOBAL_ACTION_BACK)
            AppStreamWire.InputWheel -> stroke(service, event.x, event.y, event.x, event.y + if (event.wheel > 0) 300 else -300, 120)
            AppStreamWire.InputBack -> service.performGlobalAction(AccessibilityService.GLOBAL_ACTION_BACK)
            AppStreamWire.InputIdr -> runCatching {
                codec.setParameters(android.os.Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
            }
        }
    }

    private fun stroke(service: AccessibilityService, x1: Int, y1: Int, x2: Int, y2: Int, ms: Long) {
        val path = Path().apply { moveTo(x1.toFloat(), y1.toFloat()); lineTo(x2.toFloat(), y2.toFloat()) }
        runCatching { service.dispatchGesture(GestureDescription.Builder().addStroke(GestureDescription.StrokeDescription(path, 0, ms)).build(), null, null) }
    }

    override fun close() {
        runCatching { display.release() }
        runCatching { codec.stop() }
        runCatching { codec.release() }
        runCatching { surface.release() }
        runCatching { projection.stop() }
    }
}

/** Foreground service required by Android 10+ before a MediaProjection may be used. */
class ProjectionService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26) manager.createNotificationChannel(NotificationChannel("appstream", "Send to PC", NotificationManager.IMPORTANCE_LOW))
        val note = (if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, "appstream") else @Suppress("DEPRECATION") Notification.Builder(this))
            .setSmallIcon(android.R.drawable.ic_menu_share)
            .setContentTitle(AppSender.strings(this)["sendPcActive"] ?: "Showing an app on your PC")
            .build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(7, note, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION) else startForeground(7, note)
        val code = intent?.getIntExtra("code", Activity.RESULT_CANCELED) ?: Activity.RESULT_CANCELED
        @Suppress("DEPRECATION") val data = intent?.getParcelableExtra<Intent>("data")
        val component = intent?.getStringExtra("component") ?: ""
        val title = intent?.getStringExtra("title") ?: component
        if (data == null || code != Activity.RESULT_OK) {
            stopSelf()
            return START_NOT_STICKY
        }
        val projection = getSystemService(MediaProjectionManager::class.java).getMediaProjection(code, data)
        if (projection == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        DisplaySize.ensure(this)
        val dpi = resources.displayMetrics.densityDpi
        val w = (DisplaySize.width / 2) * 2
        val h = (DisplaySize.height / 2) * 2
        Log.i("EdgeSpan", "appstream projection fallback $component ${w}x$h")
        SendToPcActivity.launchOnPhone(this, component)
        AppSender.send(this, component, title) { runCatching { ProjectionSource(projection, w, h, dpi) }.getOrNull() }
        Thread({
            while (AppSender.active) Thread.sleep(500)
            Handler(Looper.getMainLooper()).post { stopForeground(true); stopSelf() }
        }, "edgestpan-projection-watch").apply { isDaemon = true }.start()
        return START_NOT_STICKY
    }

    companion object {
        fun start(context: Context, code: Int, data: Intent, component: String, title: String) {
            val intent = Intent(context, ProjectionService::class.java)
                .putExtra("code", code).putExtra("data", data).putExtra("component", component).putExtra("title", title)
            if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent) else context.startService(intent)
        }
    }
}
