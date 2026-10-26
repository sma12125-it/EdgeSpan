package com.edgestpan.protocol

/**
 * The glow belongs to the device the pointer just left.
 * It uses that one edge, and only while the other device has the pointer.
 * A finger hides it and must not turn it back on.
 */
object GlowPolicy {
    data class Decision(val show: Boolean, val edge: Int, val finger: Boolean)

    fun enter(edge: Int) = Decision(show = false, edge = edge, finger = false)

    fun leave(edge: Int, finger: Boolean) = Decision(show = !finger, edge = edge, finger = false)

    fun touch(edge: Int) = Decision(show = false, edge = edge, finger = true)
}
