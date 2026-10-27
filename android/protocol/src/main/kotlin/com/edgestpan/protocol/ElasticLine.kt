package com.edgestpan.protocol

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.pow
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * The glowing elastic boundary shared by both screens (port of EdgeSpan.Layout.ElasticLine).
 * Exit: bulges outward at the crossing point and snaps back. Enter: stretched inward, tears,
 * fragments recoil and fade. Pure function of time so both devices stay in step.
 */
object ElasticLine {
    const val EXIT = 0
    const val ENTER = 1
    const val DurationMs = 460.0
    const val TearMs = 170.0
    const val DefaultSigma = 0.075
    /** Bend/tear width of the line, set from the synced EdgeBend setting (0..100). */
    @Volatile var Sigma = DefaultSigma
    /** Same mapping as the PC: 0 = cursor-sized dent, 100 = a curve along the full edge. */
    fun sigmaFor(bend: Int): Double = 0.01 * Math.pow(60.0, bend.coerceIn(0, 100) / 100.0)
    const val ExitDepth = 0.62
    const val EnterDepth = 0.68
    const val Accent = 0xFF14B8A6.toInt()

    private const val Omega = 2 * PI / 0.30
    private const val Zeta = 0.42
    private val exitPeak: Double by lazy {
        val wd = Omega * sqrt(1 - Zeta * Zeta)
        var best = 1e-6
        for (ms in 1 until 200) {
            val u = ms / 1000.0
            best = maxOf(best, exp(-Zeta * Omega * u) * sin(wd * u))
        }
        best
    }

    fun bell(along: Double, cross: Double, sigma: Double = Sigma): Double {
        val d = along - cross
        return exp(-(d * d) / (2 * sigma * sigma))
    }

    /** Displacement in band-thickness units: positive = into the screen, negative = out past the edge. */
    fun displacement(mode: Int, along: Double, cross: Double, ms: Double): Double {
        if (ms <= 0 || ms >= DurationMs) return 0.0
        val b = bell(along, cross.coerceIn(0.0, 1.0))
        if (mode == EXIT) return -ExitDepth * exitEnvelope(ms) * b
        if (ms < TearMs) {
            val p = ms / TearMs
            return EnterDepth * (1 - (1 - p).pow(3)) * b
        }
        val q = (ms - TearMs) / (DurationMs - TearMs)
        return EnterDepth * b * exp(-5 * q) * cos(9 * q)
    }

    /** Drawable offset from the exact edge (0 = on the edge); exit bulges point outward and are clipped. */
    fun offset(mode: Int, along: Double, cross: Double, ms: Double): Double =
        if (mode == EXIT) 0.0 else kotlin.math.max(0.0, displacement(mode, along, cross, ms))

    /** Stroke width multiplier: on exit the segment at the crossing thins as it is stretched past the edge. */
    fun thickness(mode: Int, along: Double, cross: Double, ms: Double): Double {
        if (mode != EXIT || ms <= 0 || ms >= DurationMs) return 1.0
        val pull = (-displacement(mode, along, cross, ms) / ExitDepth).coerceIn(0.0, 1.0)
        return 1 - 0.8 * pull
    }

    fun opacity(mode: Int, along: Double, cross: Double, ms: Double): Double {
        if (ms < 0 || ms >= DurationMs) return 0.0
        if (mode == EXIT) return 1 - smooth(DurationMs - 90, DurationMs, ms)
        if (ms < TearMs) return 1.0
        val q = (ms - TearMs) / (DurationMs - TearMs)
        val gap = Sigma * (0.35 + 2.4 * (1 - (1 - q).pow(3)))
        val d = abs(along - cross.coerceIn(0.0, 1.0))
        return smooth(gap, gap + 0.025, d) * (1 - q).pow(1.4)
    }

    /** Normalized 0..1 position of (x, y) along [edge] of a width x height screen. */
    fun along(edge: Int, x: Int, y: Int, width: Int, height: Int): Double {
        val vertical = edge == ScreenEdge.Left || edge == ScreenEdge.Right
        val t = if (vertical) y / maxOf(1, height).toDouble() else x / maxOf(1, width).toDouble()
        return t.coerceIn(0.0, 1.0)
    }

    private fun exitEnvelope(ms: Double): Double {
        val u = ms / 1000
        val wd = Omega * sqrt(1 - Zeta * Zeta)
        val v = exp(-Zeta * Omega * u) * sin(wd * u) / exitPeak
        return v * (1 - smooth(DurationMs - 120, DurationMs, ms))
    }

    private fun smooth(a: Double, b: Double, x: Double): Double {
        val t = ((x - a) / (b - a)).coerceIn(0.0, 1.0)
        return t * t * (3 - 2 * t)
    }
}
