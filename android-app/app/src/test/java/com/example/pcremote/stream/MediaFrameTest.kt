package com.example.pcremote.stream

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * Parity tests for the MediaFrame envelope against the DOCUMENTED wire format
 * (18-byte little-endian header) — the bytes here are built by hand from the spec,
 * not via the parser, so a shared-bug between writer and reader cannot hide.
 * The Windows writer is PcRemote.Core.MediaFrame (C#).
 */
class MediaFrameTest {

    /** Builds a frame exactly per the wire spec. */
    private fun frameBytes(isKeyframe: Boolean, seq: Long, ptsMs: Long, payload: ByteArray): ByteArray {
        val out = ByteArray(MediaFrame.HEADER_LENGTH + payload.size)
        fun put32(offset: Int, v: Long) {
            out[offset] = (v and 0xFF).toByte()
            out[offset + 1] = ((v shr 8) and 0xFF).toByte()
            out[offset + 2] = ((v shr 16) and 0xFF).toByte()
            out[offset + 3] = ((v shr 24) and 0xFF).toByte()
        }
        put32(0, 0x5043524DL) // "PCRM"
        out[4] = MediaFrame.VERSION.toByte()
        out[5] = if (isKeyframe) 1 else 0
        put32(6, seq)
        put32(10, ptsMs)
        put32(14, payload.size.toLong())
        payload.copyInto(out, MediaFrame.HEADER_LENGTH)
        return out
    }

    @Test
    fun `parses a spec-conformant frame`() {
        val payload = byteArrayOf(0, 0, 0, 1, 0x65)
        val f = MediaFrame.parse(frameBytes(isKeyframe = true, seq = 7, ptsMs = 1234, payload = payload))!!

        assertTrue(f.isKeyframe)
        assertEquals(7L, f.seq)
        assertEquals(1234L, f.ptsMs)
        assertTrue(f.payload.contentEquals(payload))
    }

    @Test
    fun `non-keyframe flag parses as false`() {
        val f = MediaFrame.parse(frameBytes(false, 1, 0, byteArrayOf(1, 2, 3)))!!
        assertFalse(f.isKeyframe)
    }

    @Test
    fun `rejects malformed frames`() {
        val good = frameBytes(true, 1, 0, byteArrayOf(1, 2, 3))

        assertNull(MediaFrame.parse(ByteArray(0))) // empty
        assertNull(MediaFrame.parse(good.copyOf(10))) // shorter than the header
        assertNull(MediaFrame.parse(good.copyOf(17))) // header minus one byte
        assertNull(MediaFrame.parse(good.copyOf(good.size + 2))) // trailing garbage

        val badMagic = good.copyOf(); badMagic[0] = 0
        assertNull(MediaFrame.parse(badMagic))

        val badVersion = good.copyOf(); badVersion[4] = 99
        assertNull(MediaFrame.parse(badVersion))
    }
}
