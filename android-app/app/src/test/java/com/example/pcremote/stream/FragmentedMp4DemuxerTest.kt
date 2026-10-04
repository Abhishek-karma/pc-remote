package com.example.pcremote.stream

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

/**
 * Parses the REAL fragmented-MP4 fixture produced by the product's own Windows
 * encoder (H264EncoderTests fixture: Media Foundation fragmented-MP4 sink,
 * 320x240, 8 frames, first frame a forced keyframe). This is the actual
 * cross-platform contract: Windows-encoded bytes in, MediaCodec-ready samples out.
 */
class FragmentedMp4DemuxerTest {

    private class Events : FragmentedMp4Demuxer.Listener {
        var csd0: ByteArray? = null
        var csd1: ByteArray? = null
        var width = 0
        var height = 0
        val samples = mutableListOf<Triple<ByteArray, Long, Boolean>>() // data, ptsUs, sync

        override fun onInit(csd0: ByteArray, csd1: ByteArray, width: Int, height: Int) {
            this.csd0 = csd0
            this.csd1 = csd1
            this.width = width
            this.height = height
        }

        override fun onSample(data: ByteArray, ptsUs: Long, isSync: Boolean) {
            samples.add(Triple(data, ptsUs, isSync))
        }
    }

    private fun fixtureBytes(): ByteArray =
        javaClass.classLoader.getResourceAsStream("sample-stream.mp4")!!.readBytes()

    private fun feedAll(bytes: ByteArray): Events {
        val events = Events()
        val demuxer = FragmentedMp4Demuxer(events)
        demuxer.feed(bytes)
        return events
    }

    /** First NAL type in an Annex-B byte stream (after the 4-byte start code). */
    private fun firstNalType(annexB: ByteArray): Int =
        annexB[4].toInt() and 0x1F

    /** True when the Annex-B stream contains a NAL of [nalType] (scans all start codes). */
    private fun containsNalType(annexB: ByteArray, nalType: Int): Boolean {
        var i = 0
        while (i + 4 < annexB.size) {
            if (annexB[i].toInt() == 0 && annexB[i + 1].toInt() == 0 &&
                annexB[i + 2].toInt() == 0 && annexB[i + 3].toInt() == 1 &&
                (annexB[i + 4].toInt() and 0x1F) == nalType
            ) return true
            i++
        }
        return false
    }

    @Test
    fun `init segment yields Annex-B parameter sets and track dimensions`() {
        val e = feedAll(fixtureBytes())

        // JUnit's assertNotNull is void here; !! gives a clear failure if null.
        val csd0 = e.csd0!!
        val csd1 = e.csd1!!
        // csd-0 = Annex-B SPS (NAL type 7), csd-1 = Annex-B PPS (NAL type 8).
        assertTrue(csd0.size > 4)
        assertEquals(0, csd0[0].toInt()); assertEquals(0, csd0[1].toInt())
        assertEquals(0, csd0[2].toInt()); assertEquals(1, csd0[3].toInt())
        assertEquals(7, firstNalType(csd0))
        assertEquals(8, firstNalType(csd1))

        // The fixture was encoded at 320x240.
        assertEquals(320, e.width)
        assertEquals(240, e.height)
    }

    @Test
    fun `all encoded frames are emitted in order with increasing timestamps`() {
        val e = feedAll(fixtureBytes())

        // The fixture has 8 encoded frames.
        assertEquals(8, e.samples.size)

        // First sample is the forced keyframe. MF prepends an AUD (type 9) to the
        // access unit, so scan for the IDR (type 5) instead of checking NAL #1.
        val first = e.samples.first()
        assertTrue(first.third, "first sample must be marked sync")
        assertTrue(first.first.size > 4)
        assertTrue(containsNalType(first.first, 5), "keyframe sample must contain an IDR NAL")

        // Timestamps strictly increase and are plausible for a 15 fps encode.
        for (i in 1 until e.samples.size) {
            val delta = e.samples[i].second - e.samples[i - 1].second
            assertTrue(delta > 0, "timestamps must strictly increase (frame $i)")
            assertTrue(delta in 1_000..200_000, "frame delta ${delta}us out of range at frame $i")
        }

        // Sanity: every sample carried real NAL data (Annex-B start code present).
        assertTrue(e.samples.all { it.first.size > 5 })
    }

    @Test
    fun `byte-at-a-time feeding produces identical output`() {
        val bytes = fixtureBytes()
        val whole = feedAll(bytes)

        val chunked = Events()
        val demuxer = FragmentedMp4Demuxer(chunked)
        var i = 0
        while (i < bytes.size) {
            val end = minOf(i + 7, bytes.size) // awkward slice sizes stress the parser
            demuxer.feed(bytes.copyOfRange(i, end))
            i = end
        }

        assertEquals(whole.csd0!!.contentHashCode(), chunked.csd0!!.contentHashCode())
        assertEquals(whole.csd1!!.contentHashCode(), chunked.csd1!!.contentHashCode())
        assertEquals(whole.samples.map { it.first.contentHashCode() to it.second }, chunked.samples.map { it.first.contentHashCode() to it.second })
        assertEquals(whole.samples.map { it.third }, chunked.samples.map { it.third })
    }

    @Test
    fun `MediaFrame framing over the fixture preserves the stream`() {
        // The Windows service wraps stream bytes in MediaFrames (18-byte header).
        // Frame the fixture per the wire spec, then parse+demux as the app will,
        // and require the exact same events as feeding the raw file directly.
        val bytes = fixtureBytes()
        val framed = Events()
        val framedDemuxer = FragmentedMp4Demuxer(framed)
        val chunk = 4096
        var seq = 0L
        var offset = 0
        while (offset < bytes.size) {
            val end = minOf(offset + chunk, bytes.size)
            val payload = bytes.copyOfRange(offset, end)
            val packet = ByteArray(18 + payload.size)
            fun put32(o: Int, v: Long) {
                packet[o] = (v and 0xFF).toByte()
                packet[o + 1] = ((v shr 8) and 0xFF).toByte()
                packet[o + 2] = ((v shr 16) and 0xFF).toByte()
                packet[o + 3] = ((v shr 24) and 0xFF).toByte()
            }
            put32(0, 0x5043524DL)
            packet[4] = MediaFrame.VERSION.toByte()
            packet[5] = 0
            put32(6, seq++)
            put32(10, 0)
            put32(14, payload.size.toLong())
            payload.copyInto(packet, 18)

            val parsed = MediaFrame.parse(packet)!!
            framedDemuxer.feed(parsed.payload)
            offset = end
        }

        val direct = feedAll(bytes)
        assertEquals(direct.csd0!!.contentHashCode(), framed.csd0!!.contentHashCode())
        assertEquals(direct.csd1!!.contentHashCode(), framed.csd1!!.contentHashCode())
        assertEquals(direct.samples.map { it.first.contentHashCode() to it.second }, framed.samples.map { it.first.contentHashCode() to it.second })
    }

    @Test
    fun `length-prefixed NALs convert to Annex-B`() {
        // Two NALs with 4-byte BE length prefixes: [3 bytes][2 bytes].
        val raw = byteArrayOf(0, 0, 0, 3, 0x67, 0xAA.toByte(), 0xBB.toByte(), 0, 0, 0, 2, 0x68, 0x01)
        val out = FragmentedMp4Demuxer.nalLengthPrefixedToAnnexB(raw)!!
        // [00 00 00 01][67 AA BB][00 00 00 01][68 01]
        assertEquals(4 + 3 + 4 + 2, out.size)
        assertEquals(1, out[3].toInt())
        assertEquals(0x67, out[4].toInt() and 0xFF)
        assertEquals(1, out[10].toInt())
        assertEquals(0x68, out[11].toInt() and 0xFF)
    }

    @Test
    fun `malformed length prefixes are rejected`() {
        // Declares 10 bytes of NAL but only 3 follow.
        val raw = byteArrayOf(0, 0, 0, 10, 1, 2, 3)
        assertNull(FragmentedMp4Demuxer.nalLengthPrefixedToAnnexB(raw))
        assertNull(FragmentedMp4Demuxer.nalLengthPrefixedToAnnexB(ByteArray(0)))
    }
}
