package com.example.pcremote.stream

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import java.util.concurrent.LinkedBlockingQueue

/**
 * MediaCodec H.264 decoder rendering to a Surface. Fed by [StreamClient] with
 * Annex-B parameter sets and access units from [FragmentedMp4Demuxer]. All codec
 * work runs on one dedicated thread so the WebSocket thread never blocks; if the
 * decoder falls behind, stale samples are dropped and [onDecoderBehind] fires so
 * the client can request a keyframe and resync.
 */
class H264StreamDecoder(private val onDecoderBehind: () -> Unit = {}) {

    private sealed interface Job {
        class Init(val csd0: ByteArray, val csd1: ByteArray, val width: Int, val height: Int) : Job
        class Sample(val data: ByteArray, val ptsUs: Long, val isSync: Boolean) : Job
        data object Reset : Job
        data object Stop : Job
    }

    private val queue = LinkedBlockingQueue<Job>(MAX_QUEUED_JOBS)
    private val thread = Thread({ loop() }, "stream-decoder").apply {
        isDaemon = true
        start()
    }

    @Volatile private var surface: Surface? = null
    private var pendingInit: Job.Init? = null
    private var codec: MediaCodec? = null
    private var bufferInfo = MediaCodec.BufferInfo()

    /** Called by the UI thread when the render surface becomes available. */
    fun attach(surface: Surface) {
        this.surface = surface
        queue.offer(Job.Reset)
    }

    fun detach() {
        this.surface = null
        queue.offer(Job.Reset)
    }

    fun onInit(csd0: ByteArray, csd1: ByteArray, width: Int, height: Int) {
        pendingInit = Job.Init(csd0, csd1, width, height)
        queue.offer(Job.Reset)
    }

    fun onSample(data: ByteArray, ptsUs: Long, isSync: Boolean) {
        if (!queue.offer(Job.Sample(data, ptsUs, isSync))) {
            // Decoder behind: drop everything queued (stale frames are worthless)
            // and resync from the next keyframe.
            queue.clear()
            queue.offer(Job.Reset)
            onDecoderBehind()
        }
    }

    /** Discards queued + decoded state; the next keyframe rebuilds the picture. */
    fun reset() {
        queue.offer(Job.Reset)
    }

    fun release() {
        // The queue may already be full, in which case a plain offer() would silently
        // drop Job.Stop: the thread would keep running while holding a live MediaCodec
        // (leaked codec + native thread). Make room and force the stop through.
        queue.clear()
        while (!queue.offer(Job.Stop)) {
            queue.clear()
        }
        thread.join(2000)
    }

    private fun loop() {
        while (true) {
            val job = try { queue.take() } catch (_: InterruptedException) { return }
            when (job) {
                is Job.Init -> {
                    pendingInit = job
                    releaseCodec()
                }
                is Job.Sample -> {
                    if (!feed(job)) { /* codec unavailable: sample dropped */ }
                }
                Job.Reset -> releaseCodec()
                Job.Stop -> { releaseCodec(); return }
            }
            drain()
        }
    }

    private fun releaseCodec() {
        try { codec?.stop() } catch (_: Exception) { }
        try { codec?.release() } catch (_: Exception) { }
        codec = null
    }

    private fun tryConfigure(): Boolean {
        val init = pendingInit ?: return false
        val sfc = surface ?: return false
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, init.width, init.height).apply {
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, init.width * init.height)
            setByteBuffer("csd-0", java.nio.ByteBuffer.wrap(init.csd0))
            setByteBuffer("csd-1", java.nio.ByteBuffer.wrap(init.csd1))
            if (Build.VERSION.SDK_INT >= 30) {
                setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
        }
        return try {
            val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            c.configure(format, sfc, null, 0)
            c.start()
            codec = c
            true
        } catch (e: Exception) {
            releaseCodec()
            false
        }
    }

    /** Feeds one access unit and drains whatever output is ready. */
    private fun feed(job: Job.Sample): Boolean {
        if (codec == null && !tryConfigure()) return false
        val c = codec ?: return false
        val index = try { c.dequeueInputBuffer(10_000) } catch (_: Exception) { releaseCodec(); return false }
        if (index < 0) return true // decoder busy: frame dropped silently this tick
        val input = c.getInputBuffer(index) ?: return false
        if (job.data.size > input.capacity()) return true
        input.clear()
        input.put(job.data)
        val flags = if (job.isSync) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
        c.queueInputBuffer(index, 0, job.data.size, job.ptsUs, flags)
        return true
    }

    private fun drain() {
        val c = codec ?: return
        while (true) {
            val index = try { c.dequeueOutputBuffer(bufferInfo, 0) } catch (_: Exception) { releaseCodec(); return }
            when {
                index == MediaCodec.INFO_TRY_AGAIN_LATER -> return
                index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> Unit // new format: nothing to do
                index >= 0 -> {
                    // Render at once: this is a live stream, late frames are worthless.
                    try { c.releaseOutputBuffer(index, true) } catch (_: Exception) { }
                }
            }
        }
    }

    private companion object {
        const val MAX_QUEUED_JOBS = 240
    }
}
