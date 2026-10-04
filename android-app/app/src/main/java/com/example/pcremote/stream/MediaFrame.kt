package com.example.pcremote.stream

/**
 * Android mirror of the Windows agent's MediaFrame envelope (PcRemote.Core.MediaFrame).
 * Binary WebSocket frames on the /stream connection carry this 18-byte little-endian
 * header followed by the payload (fragmented-MP4 bytes):
 *
 *   0   magic u32  0x5043524D ("PCRM")
 *   4   version u8  1
 *   5   flags u8    bit0 = keyframe
 *   6   seq u32     monotonic per stream; stale frames are detectable by the client
 *   10  pts u32     presentation timestamp in milliseconds
 *   14  length u32  payload length in bytes
 *
 * Unknown versions and length mismatches are rejected (fail loudly, never mangle).
 */
data class MediaFrame(val isKeyframe: Boolean, val seq: Long, val ptsMs: Long, val payload: ByteArray) {
    companion object {
        const val MAGIC: Long = 0x5043524DL // "PCRM"
        const val VERSION: Int = 1
        const val HEADER_LENGTH: Int = 18
        private const val KEYFRAME_FLAG: Int = 0x01

        /** Little-endian u32 at [offset]. */
        private fun ByteArray.u32le(offset: Int): Long =
            (this[offset].toLong() and 0xFF) or
                ((this[offset + 1].toLong() and 0xFF) shl 8) or
                ((this[offset + 2].toLong() and 0xFF) shl 16) or
                ((this[offset + 3].toLong() and 0xFF) shl 24)

        /** Parses one binary WS frame payload, or null when malformed. */
        fun parse(data: ByteArray): MediaFrame? {
            if (data.size < HEADER_LENGTH) return null
            if (data.u32le(0) != MAGIC) return null
            if (data[4].toInt() and 0xFF != VERSION) return null
            val flags = data[5].toInt() and 0xFF
            val seq = data.u32le(6)
            val pts = data.u32le(10)
            val length = data.u32le(14)
            if (length != (data.size - HEADER_LENGTH).toLong()) return null
            return MediaFrame(
                isKeyframe = (flags and KEYFRAME_FLAG) != 0,
                seq = seq,
                ptsMs = pts,
                payload = data.copyOfRange(HEADER_LENGTH, data.size),
            )
        }
    }

    override fun equals(other: Any?): Boolean =
        other is MediaFrame && other.isKeyframe == isKeyframe && other.seq == seq &&
            other.ptsMs == ptsMs && other.payload.contentEquals(payload)

    override fun hashCode(): Int = 31 * (31 * seq.hashCode() + ptsMs.hashCode()) + payload.contentHashCode()
}
