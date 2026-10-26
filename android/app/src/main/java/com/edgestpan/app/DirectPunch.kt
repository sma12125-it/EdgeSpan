package com.edgestpan.app

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.security.SecureRandom

/** UDP socket that asks a configurable STUN host for its mapped address, then probes a peer. */
class DirectPunch : AutoCloseable {
    val socket = DatagramSocket(0)
    val localPort = socket.localPort
    val probeHits = mutableListOf<InetSocketAddress>()

    init {
        socket.soTimeout = 40
        socket.broadcast = true
    }

    fun lan(): String? {
        val faces = NetworkInterface.getNetworkInterfaces() ?: return null
        for (face in faces) {
            if (!face.isUp || face.isLoopback) continue
            for (addr in face.inetAddresses) {
                if (addr is Inet4Address && !addr.isLoopbackAddress) return addr.hostAddress
            }
        }
        return null
    }

    fun stun(host: String, port: Int, waitMs: Int = 900): InetSocketAddress? {
        if (host.isBlank() || port !in 1..65535) return null
        val tx = ByteArray(12).also { SecureRandom().nextBytes(it) }
        val req = ByteArray(20)
        req[1] = 1
        req[4] = 0x21
        req[5] = 0x12
        req[6] = 0xA4.toByte()
        req[7] = 0x42
        tx.copyInto(req, 8)
        val remote = InetAddress.getByName(host)
        socket.send(DatagramPacket(req, req.size, remote, port))
        val deadline = System.nanoTime() + waitMs * 1_000_000L
        val buf = ByteArray(512)
        while (System.nanoTime() < deadline) {
            val packet = DatagramPacket(buf, buf.size)
            try {
                socket.receive(packet)
            } catch (_: SocketTimeoutException) {
                continue
            }
            readMapped(packet.data, packet.length, tx)?.let { return it }
        }
        return null
    }

    fun probe(targets: List<InetSocketAddress>) {
        val msg = "ESPN".toByteArray()
        for (target in targets) {
            try {
                socket.send(DatagramPacket(msg, msg.size, target.address, target.port))
            } catch (_: Exception) {
            }
        }
    }

    fun send(bytes: ByteArray, targets: List<InetSocketAddress>) {
        for (target in targets) {
            try {
                socket.send(DatagramPacket(bytes, bytes.size, target.address, target.port))
            } catch (_: Exception) {
            }
        }
    }

    /** Session datagrams start with 0xE5. STUN replies and ESPN probes are not returned. */
    fun pollSession(): ByteArray? {
        val buf = ByteArray(65535)
        val packet = DatagramPacket(buf, buf.size)
        return try {
            socket.receive(packet)
            val data = packet.data.copyOf(packet.length)
            if (data.size == 4 && data.decodeToString() == "ESPN") {
                probeHits += InetSocketAddress(packet.address, packet.port)
                null
            } else if (data.size >= 2 && data[0] == 0xE5.toByte()) {
                data
            } else null
        } catch (_: SocketTimeoutException) {
            null
        } catch (_: Exception) {
            null
        }
    }

    override fun close() {
        runCatching { socket.close() }
    }

    private fun readMapped(buf: ByteArray, len: Int, tx: ByteArray): InetSocketAddress? {
        if (len < 32 || buf[0] != 0x01.toByte() || buf[1] != 0x01.toByte()) return null
        if (buf[4] != 0x21.toByte() || buf[5] != 0x12.toByte() || buf[6] != 0xA4.toByte() || buf[7] != 0x42.toByte()) return null
        for (i in 0 until 12) if (buf[8 + i] != tx[i]) return null
        if (buf[20] != 0x00.toByte() || buf[21] != 0x20.toByte()) return null
        val xport = ((buf[26].toInt() and 0xff) shl 8) or (buf[27].toInt() and 0xff)
        val port = xport xor 0x2112
        val ip = byteArrayOf(
            (buf[28].toInt() xor 0x21).toByte(),
            (buf[29].toInt() xor 0x12).toByte(),
            (buf[30].toInt() xor 0xA4).toByte(),
            (buf[31].toInt() xor 0x42).toByte()
        )
        return InetSocketAddress(InetAddress.getByAddress(ip), port)
    }
}
