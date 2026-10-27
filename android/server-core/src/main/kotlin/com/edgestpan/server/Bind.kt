package com.edgestpan.server

import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket

/**
 * Binds a loopback server socket with SO_REUSEADDR, retrying for a few seconds: after a reconnect the previous
 * helper's socket can sit in TIME_WAIT (or the old process is still dying), which used to fail with EADDRINUSE.
 */
internal fun bindLoopback(port: Int, backlog: Int): ServerSocket {
    var last: Exception? = null
    repeat(20) { attempt ->
        val socket = ServerSocket()
        try {
            socket.reuseAddress = true
            socket.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), backlog)
            if (attempt > 0) System.err.println("bind port=$port ok after retry $attempt")
            return socket
        } catch (e: Exception) {
            last = e
            runCatching { socket.close() }
            Thread.sleep(250)
        }
    }
    throw last ?: IllegalStateException("bind $port")
}
