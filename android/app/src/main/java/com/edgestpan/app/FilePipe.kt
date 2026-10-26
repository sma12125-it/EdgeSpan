package com.edgestpan.app

import com.edgestpan.protocol.ClientEngine
import com.edgestpan.protocol.ClientEvent
import com.edgestpan.protocol.Payloads
import com.edgestpan.protocol.PacketType
import java.io.File
import java.io.FileOutputStream
import java.io.RandomAccessFile

/** Chunked encrypted file copy. The other side deletes a cut file only after FileDone. */
class FilePipe(private val engine: ClientEngine, private val dir: File, private val onNote: (String, Long, Long, String) -> Unit) {
    private class OutJob(val name: String, val cut: Boolean, val delete: File?, val file: RandomAccessFile, val size: Long) {
        var id = 0L
        var offered = false
        var queued = 0L
        var acked = 0L
    }

    private class InJob(val name: String, val size: Long, val out: FileOutputStream, val path: File) {
        var got = 0L
    }

    private val outgoing = ArrayDeque<OutJob>()
    private val incoming = HashMap<Long, InJob>()
    private var seq = 1L

    fun offer(name: String, bytes: ByteArray, cut: Boolean, delete: File?) {
        val path = File(dir, "out-" + seq + "-" + safe(name))
        path.writeBytes(bytes)
        outgoing.add(OutJob(safe(name), cut, delete, RandomAccessFile(path, "r"), bytes.size.toLong()))
        onNote(safe(name), 0, bytes.size.toLong(), "copy")
    }

    fun onEvent(event: ClientEvent) {
        when (event) {
            is ClientEvent.FileOffer -> {
                if (event.size < 0 || event.size > 512L * 1024 * 1024) return
                val path = unique(safe(event.name))
                val job = InJob(safe(event.name), event.size, FileOutputStream(path), path)
                incoming[event.id] = job
                onNote(job.name, 0, event.size, "copy")
                if (event.size == 0L) finishIn(event.id, job)
            }
            is ClientEvent.FileChunk -> {
                val job = incoming[event.id] ?: return
                if (event.offset != job.got) {
                    engine.post(PacketType.FileAck, Payloads.fileAck(event.id, job.got))
                    return
                }
                job.out.write(event.data)
                job.got += event.data.size
                onNote(job.name, job.got, job.size, "copy")
                engine.post(PacketType.FileAck, Payloads.fileAck(event.id, job.got))
                if (job.got >= job.size) finishIn(event.id, job)
            }
            is ClientEvent.FileAck -> {
                val job = outgoing.firstOrNull { it.id == event.id } ?: return
                job.acked = maxOf(job.acked, event.next)
                onNote(job.name, job.acked, job.size, "copy")
            }
            is ClientEvent.FileDone -> {
                val job = outgoing.firstOrNull { it.id == event.id } ?: return
                job.file.close()
                outgoing.remove(job)
                if (event.ok && job.cut) runCatching { job.delete?.delete() }
                onNote(job.name, job.size, job.size, if (event.ok && job.cut) "moved" else if (event.ok) "ready" else "failed")
            }
            else -> Unit
        }
    }

    fun pump() {
        val job = outgoing.firstOrNull() ?: return
        if (!job.offered) {
            job.id = seq++
            job.offered = true
            engine.post(PacketType.FileOffer, Payloads.fileOffer(job.id, job.cut, job.size, job.name))
        }
        if (job.queued > job.acked || job.queued >= job.size) return
        val n = minOf(800L, job.size - job.queued).toInt()
        val buf = ByteArray(n)
        job.file.seek(job.queued)
        val read = job.file.read(buf)
        if (read <= 0) return
        val data = if (read == buf.size) buf else buf.copyOf(read)
        engine.post(PacketType.FileChunk, Payloads.fileChunk(job.id, job.queued, data))
        job.queued += read
    }

    private fun finishIn(id: Long, job: InJob) {
        job.out.close()
        incoming.remove(id)
        engine.post(PacketType.FileDone, Payloads.fileDone(id, true))
        onNote(job.name, job.size, job.size, "ready")
    }

    private fun unique(name: String): File {
        dir.mkdirs()
        var file = File(dir, name)
        if (!file.exists()) return file
        val stem = file.nameWithoutExtension
        val ext = file.extension.let { if (it.isEmpty()) "" else ".$it" }
        var i = 2
        while (i < 1000) {
            file = File(dir, "$stem $i$ext")
            if (!file.exists()) return file
            i++
        }
        return File(dir, "$stem-${System.nanoTime()}$ext")
    }

    private fun safe(name: String) = name.substringAfterLast('/').substringAfterLast('\\').ifBlank { "file" }.replace(Regex("[^A-Za-z0-9._ -]"), "_")
}
