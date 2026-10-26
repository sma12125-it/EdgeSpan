package com.edgestpan.app

import android.content.Context
import android.media.AudioManager
import android.media.ToneGenerator
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.provider.Settings
import android.util.Log
import android.view.Choreographer
import android.view.HapticFeedbackConstants
import android.view.View
import com.edgestpan.protocol.ScreenEdge
import java.lang.ref.WeakReference
import kotlin.math.PI
import kotlin.math.abs

/** The same response and damping as docs/DESIGN-SYSTEM.md. A retarget keeps velocity. */
internal class Spring1D(response: Double, damping: Double, value: Double = 0.0) {
    private val stiffness = (2.0 * PI / response) * (2.0 * PI / response)
    private val damp = 4.0 * PI * damping / response
    var value = value
    var velocity = 0.0
    var target = value

    fun retarget(next: Double) {
        target = next
    }

    fun nudge(next: Double) {
        value = next
    }

    fun step(dt: Double, reduce: Boolean): Boolean {
        if (reduce || dt <= 0) {
            value = target
            velocity = 0.0
            return true
        }
        val stepDt = dt.coerceAtMost(0.034)
        val acc = -stiffness * (value - target) - damp * velocity
        velocity += acc * stepDt
        value += velocity * stepDt
        if (abs(value - target) < 0.001 && abs(velocity) < 0.01) {
            value = target
            velocity = 0.0
            return true
        }
        return false
    }
}

internal object Motion {
    fun reduce(context: Context): Boolean {
        return try {
            val prefs = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE)
            if (prefs.getBoolean("reduceMotion", false)) return true
            val scale = Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f)
            scale == 0f
        } catch (thrown: SecurityException) {
            Log.w(TAG, "reduce", thrown)
            false
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "reduce", thrown)
            false
        }
    }

    fun breakthrough(context: Context): Boolean {
        return try {
            val prefs = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE)
            !prefs.contains("breakthrough") || prefs.getBoolean("breakthrough", true)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "breakthrough", thrown)
            true
        }
    }

    fun settle(view: View, edge: Int, reduce: Boolean) {
        try {
            if (reduce) return
            val density = view.resources.displayMetrics.density
            val fromX = when (edge) {
                ScreenEdge.Left -> -18f * density
                ScreenEdge.Right -> 18f * density
                else -> 0f
            }
            val fromY = when (edge) {
                ScreenEdge.Top -> -18f * density
                ScreenEdge.Bottom -> 18f * density
                else -> 0f
            }
            view.translationX = fromX
            view.translationY = fromY
            view.scaleX = 0.96f
            view.scaleY = 0.96f
            val spring = Spring1D(0.32, 0.72)
            spring.retarget(1.0)
            var last = 0L
            val frames = Choreographer.getInstance()
            val callback = object : Choreographer.FrameCallback {
                override fun doFrame(time: Long) {
                    try {
                        val dt = if (last == 0L) 0.016 else ((time - last) / 1_000_000_000.0).coerceIn(0.001, 0.05)
                        last = time
                        val done = spring.step(dt, false)
                        val t = spring.value.toFloat()
                        view.translationX = fromX * (1f - t)
                        view.translationY = fromY * (1f - t)
                        view.scaleX = 0.96f + 0.04f * t
                        view.scaleY = 0.96f + 0.04f * t
                        if (!done) frames.postFrameCallback(this)
                    } catch (thrown: RuntimeException) {
                        Log.w(TAG, "settle", thrown)
                    }
                }
            }
            frames.postFrameCallback(callback)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "settle", thrown)
        }
    }
}

internal object Haptics {
    @Volatile private var anchor: WeakReference<View>? = null

    /** The activity decor view, so a service can tick without a Vibrator call. */
    fun bind(view: View?) {
        anchor = view?.let { WeakReference(it) }
    }

    fun tick(context: Context, view: View? = null) {
        try {
            if (Motion.reduce(context)) return
            val target = (view ?: anchor?.get())?.takeIf { it.isAttachedToWindow }
            if (target != null) {
                val run = Runnable {
                    try {
                        if (target.isAttachedToWindow) {
                            target.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                        }
                    } catch (thrown: SecurityException) {
                        Log.w(TAG, "haptic", thrown)
                        vibrate(context)
                    } catch (thrown: RuntimeException) {
                        Log.w(TAG, "haptic", thrown)
                    }
                }
                if (Looper.myLooper() == Looper.getMainLooper()) run.run()
                else Handler(Looper.getMainLooper()).post(run)
                return
            }
            vibrate(context)
        } catch (thrown: SecurityException) {
            Log.w(TAG, "haptic", thrown)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "haptic", thrown)
        }
    }

    private fun vibrate(context: Context) {
        try {
            val vibrator = context.getSystemService(Vibrator::class.java) ?: return
            if (Build.VERSION.SDK_INT >= 29) vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
            else @Suppress("DEPRECATION") vibrator.vibrate(12)
        } catch (thrown: SecurityException) {
            Log.w(TAG, "haptic", thrown)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "haptic", thrown)
        }
    }
}

internal object EdgeTone {
    fun play(context: Context) {
        try {
            val prefs = context.getSharedPreferences("edgestpan", Context.MODE_PRIVATE)
            if (!prefs.getBoolean("sound", false) || Motion.reduce(context)) return
            val tone = ToneGenerator(AudioManager.STREAM_MUSIC, 30)
            tone.startTone(ToneGenerator.TONE_PROP_ACK, 36)
        } catch (thrown: SecurityException) {
            Log.w(TAG, "tone", thrown)
        } catch (thrown: RuntimeException) {
            Log.w(TAG, "tone", thrown)
        }
    }
}

private const val TAG = "EdgeSpan"
