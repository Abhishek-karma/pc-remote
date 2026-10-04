package com.example.pcremote.stream

/**
 * Incremental parser for the fragmented-MP4 byte stream the Windows agent emits
 * (Media Foundation's fragmented-MP4 sink: ftyp/uuid/pdin/moov init, then
 * moof+mdat fragments, optionally mfra). Turns it into MediaCodec-ready H.264:
 *
 *  - [Listener.onInit]: csd-0 (Annex-B SPS) + csd-1 (Annex-B PPS) from the avcC
 *    record, plus the track's display dimensions.
 *  - [Listener.onSample]: one access unit per sample, length-prefixed NALs
 *    converted to Annex-B start codes, with decode time in microseconds.
 *
 * [feed] may be called with any chunking (a byte at a time works): boxes are
 * only interpreted once fully received, and consumed bytes are compacted away so
 * a long session does not grow the buffer without bound.
 *
 * Box knowledge is deliberately scoped to what the agent's muxer emits; unknown
 * boxes (uuid, pdin, mfra, pasp, ...) are skipped.
 */
class FragmentedMp4Demuxer(private val listener: Listener) {

    interface Listener {
        /** Configuration samples for MediaCodec (BUFFER_FLAG_CODEC_CONFIG). */
        fun onInit(csd0: ByteArray, csd1: ByteArray, width: Int, height: Int)

        /** One encoded access unit, Annex-B, ready for an H.264 decoder input buffer. */
        fun onSample(data: ByteArray, ptsUs: Long, isSync: Boolean)
    }

    // Growable byte sink. bufferBase is the absolute stream offset of buffer[0].
    private var buffer = ByteArray(0)
    private var bufferBase = 0L
    private var readPos = 0L // absolute offset of the next uninterpreted byte

    // Init-segment state.
    private var timescale = 0L
    private var width = 0
    private var height = 0
    private var sps: ByteArray? = null
    private var pps: ByteArray? = null
    private var nalLengthSize = 4
    private var initEmitted = false

    // The moof immediately before the mdat currently arriving.
    private var pendingFragment: Fragment? = null

    // Accumulated decode-time ticks of all emitted fragments (used when a
    // fragment carries no tfdt — decode time then continues implicitly).
    private var decodeTickBase = 0L

    private class Fragment(
        val base: Long,          // absolute offset sample data offsets are relative to
        val dataOffset: Long,    // trun data_offset (relative to [base])
        val sizes: IntArray,
        val sync: BooleanArray,
        val decodeTimeUs: LongArray, // per-sample ABSOLUTE decode time, microseconds
        val totalDurationTicks: Long, // for continuing decode time when the next fragment has no tfdt
    )

    /** Appends freshly received stream bytes and parses everything now complete. */
    fun feed(bytes: ByteArray) {
        if (bytes.isEmpty()) return
        val grown = buffer.copyOf(buffer.size + bytes.size)
        bytes.copyInto(grown, buffer.size)
        buffer = grown
        parse()
        compact()
    }

    /**
     * Forgets everything about the current stream so the parser can be reused for a
     * NEW one.
     *
     * Required on reconnect: without it [initEmitted] stays true, so a fresh stream's
     * init segment is parsed but never delivered, and the decoder would keep the
     * previous stream's SPS/PPS and dimensions — wrong whenever the new stream starts
     * at a different resolution. Unconsumed bytes are dropped too, since they belong
     * to the abandoned stream.
     */
    fun reset() {
        buffer = ByteArray(0)
        bufferBase = 0L
        readPos = 0L
        timescale = 0L
        width = 0
        height = 0
        sps = null
        pps = null
        nalLengthSize = 4
        initEmitted = false
        pendingFragment = null
        decodeTickBase = 0L
    }

    private fun absToIdx(abs: Long): Int = (abs - bufferBase).toInt()

    private fun have(abs: Long): Boolean = abs - bufferBase < buffer.size

    /** Big-endian u32 — MP4 box fields are network byte order (unlike MediaFrame's LE header). */
    private fun u32(abs: Long): Long {
        val i = absToIdx(abs)
        return ((buffer[i].toLong() and 0xFF) shl 24) or
            ((buffer[i + 1].toLong() and 0xFF) shl 16) or
            ((buffer[i + 2].toLong() and 0xFF) shl 8) or
            (buffer[i + 3].toLong() and 0xFF)
    }

    private fun u64(abs: Long): Long {
        var v = 0L
        for (k in 0 until 8) v = (v shl 8) or (buffer[absToIdx(abs) + k].toLong() and 0xFF)
        return v
    }

    private fun type4cc(abs: Long): String {
        val i = absToIdx(abs)
        return String(buffer, i, 4, Charsets.ISO_8859_1)
    }

    /** Parses complete boxes from [readPos]; advances readPos past consumed data. */
    private fun parse() {
        while (true) {
            if (!have(readPos + 8)) return
            var size = u32(readPos)
            var header = 8
            if (size == 1L) {
                if (!have(readPos + 16)) return
                size = u64(readPos + 8)
                header = 16
            }
            if (size < header.toLong()) return // corrupt; stop parsing this stream
            val end = readPos + size
            if (!have(end)) return // box not fully received yet

            when (type4cc(readPos + 4)) {
                "moov" -> parseContainer(readPos + header, end)
                "moof" -> parseMoof(readPos, readPos + header, end)
                "mdat" -> emitFragment(end)
                // moov's children (also reached via parseContainer):
                "trak", "mdia", "minf", "stbl", "mvex", "edts" -> parseContainer(readPos + header, end)
                else -> Unit // unknown top-level box: skipped wholesale
            }
            readPos = end
        }
    }

    private fun parseContainer(start: Long, end: Long) {
        var pos = start
        while (pos + 8 <= end) {
            var size = u32(pos)
            var header = 8
            if (size == 1L) {
                if (!have(pos + 16)) return
                size = u64(pos + 8)
                header = 16
            }
            if (size < header.toLong() || pos + size > end) return
            when (type4cc(pos + 4)) {
                "trak", "mdia", "minf", "stbl", "mvex", "edts" -> parseContainer(pos + header, pos + size)
                "tkhd" -> parseTkhd(pos + header)
                "mdhd" -> parseMdhd(pos + header)
                "stsd" -> parseStsd(pos + header, pos + size)
                else -> Unit
            }
            pos += size
        }
    }

    /** tkhd: width/height are the trailing two 16.16 fixed-point values. */
    private fun parseTkhd(payload: Long) {
        val version = buffer[absToIdx(payload)].toInt() and 0x7F
        // payload layout before width: ver/flags + creation + modification + id +
        // reserved + duration + reserved + layer/alt/volume/res + matrix.
        val widthPos = payload + 4 + (if (version == 1) 16 else 8) + 4 + 4 +
            (if (version == 1) 8 else 4) + 8 + 2 + 2 + 2 + 2 + 36
        if (!have(widthPos + 8)) return
        width = (u32(widthPos) ushr 16).toInt()
        height = (u32(widthPos + 4) ushr 16).toInt()
    }

    /** mdhd v0/v1: the timescale sits after creation+modification. */
    private fun parseMdhd(payload: Long) {
        val version = buffer[absToIdx(payload)].toInt() and 0x7F
        val timescalePos = payload + 4 + (if (version == 1) 16 else 8)
        if (!have(timescalePos + 4)) return
        timescale = u32(timescalePos)
    }

    /** stsd: 4 version/flags + 4 entry_count, then the avc1 sample entry. */
    private fun parseStsd(payload: Long, end: Long) {
        var pos = payload + 8
        while (pos + 8 <= end) {
            val size = u32(pos)
            if (size < 8L || pos + size > end) return
            if (type4cc(pos + 4) == "avc1" || type4cc(pos + 4) == "avc3") parseAvc1(pos, pos + size)
            pos += size
        }
    }

    /** avc1 VisualSampleEntry: 78 bytes of fixed fields, then boxes (avcC, pasp...). */
    private fun parseAvc1(start: Long, end: Long) {
        // width/height are also here (u16 at +32/+34 after the box header) as a
        // fallback when tkhd is absent; tkhd normally wins.
        if (width == 0 && have(start + 36)) {
            val dims = u32(start + 32)
            width = (dims ushr 16).toInt()
            height = (dims and 0xFFFF).toInt()
        }
        var pos = start + 8 + 78
        while (pos + 8 <= end) {
            val size = u32(pos)
            if (size < 8L || pos + size > end) return
            if (type4cc(pos + 4) == "avcC") parseAvcC(pos + 8, pos + size)
            pos += size
        }
    }

    /** avcC: SPS/PPS parameter sets + NAL length prefix size. */
    private fun parseAvcC(payload: Long, @Suppress("UNUSED_PARAMETER") end: Long) {
        var pos = payload
        if (!have(pos + 7)) return
        nalLengthSize = (buffer[absToIdx(pos + 4)].toInt() and 0x03) + 1
        var numSps = buffer[absToIdx(pos + 5)].toInt() and 0x1F
        pos += 6
        while (numSps-- > 0) {
            if (!have(pos + 2)) return
            val l = ((buffer[absToIdx(pos)].toLong() and 0xFF) shl 8) or
                (buffer[absToIdx(pos + 1)].toLong() and 0xFF)
            if (!have(pos + 2 + l)) return
            sps = buffer.copyOfRange(absToIdx(pos + 2), absToIdx(pos + 2) + l.toInt())
            pos += 2 + l
        }
        if (!have(pos)) return
        var numPps = buffer[absToIdx(pos)].toInt() and 0xFF
        pos += 1
        while (numPps-- > 0) {
            if (!have(pos + 2)) return
            val l = ((buffer[absToIdx(pos)].toLong() and 0xFF) shl 8) or
                (buffer[absToIdx(pos + 1)].toLong() and 0xFF)
            if (!have(pos + 2 + l)) return
            pps = buffer.copyOfRange(absToIdx(pos + 2), absToIdx(pos + 2) + l.toInt())
            pos += 2 + l
        }
    }

    /** moof: traf(tfhd/tfdt/trun) — record where this fragment's samples live. */
    private fun parseMoof(moofStart: Long, payload: Long, end: Long) {
        var base = moofStart // default-base-is-moof unless an explicit offset says otherwise
        var dataOffset = 0L
        var sizes: IntArray? = null
        var sync: BooleanArray? = null
        var decodeUs: LongArray? = null
        var baseDecodeTime = 0L
        var sawTfdt = false
        var defaultDuration = -1L
        var defaultSize = -1L
        var defaultFlags = -1L
        var durations: LongArray? = null

        // Interprets one tfhd/tfdt/trun box, capturing into this fragment's state.
        fun child(pos: Long, boxEnd: Long, hdr: Int) {
            when (type4cc(pos + 4)) {
                "tfhd" -> {
                    val flags = u32(pos + hdr) and 0xFFFFFF
                    var p = pos + hdr + 4
                    p += 4 // track_ID (assumed ours; single-track stream)
                    if (flags and 0x000001L != 0L) { base = u64(p); p += 8 } // base_data_offset
                    if (flags and 0x000002L != 0L) p += 4 // sample_description_index
                    if (flags and 0x000008L != 0L) { defaultDuration = u32(p); p += 4 }
                    if (flags and 0x000010L != 0L) { defaultSize = u32(p); p += 4 }
                    if (flags and 0x000020L != 0L) { defaultFlags = u32(p); p += 4 }
                    if (flags and 0x020000L != 0L) base = moofStart // default-base-is-moof
                }
                "tfdt" -> {
                    val version = buffer[absToIdx(pos + hdr)].toInt() and 0x7F
                    baseDecodeTime = if (version == 1) u64(pos + hdr + 4) else u32(pos + hdr + 4)
                    sawTfdt = true
                }
                "trun" -> {
                    val r = parseTrun(pos + hdr, boxEnd, defaultDuration, defaultSize, defaultFlags)
                    dataOffset = r.dataOffset
                    sizes = r.sizes
                    sync = r.sync
                    decodeUs = r.decodeTimes
                    durations = r.durations
                }
                else -> Unit
            }
        }

        var pos = payload
        while (pos + 8 <= end) {
            var size = u32(pos)
            var hdr = 8
            if (size == 1L) {
                if (!have(pos + 16)) return
                size = u64(pos + 8)
                hdr = 16
            }
            if (size < hdr.toLong() || pos + size > end) return
            if (type4cc(pos + 4) == "traf") {
                // tfhd/tfdt/trun live INSIDE traf — descend.
                var tp = pos + hdr
                val tend = pos + size
                while (tp + 8 <= tend) {
                    var tsize = u32(tp)
                    var thdr = 8
                    if (tsize == 1L) {
                        if (!have(tp + 16)) return
                        tsize = u64(tp + 8)
                        thdr = 16
                    }
                    if (tsize < thdr.toLong() || tp + tsize > tend) return
                    child(tp, tp + tsize, thdr)
                    tp += tsize
                }
            } else {
                child(pos, pos + size, hdr)
            }
            pos += size
        }

        val s = sizes ?: return
        if (s.isEmpty()) return
        // trun gives decode times RELATIVE to the fragment. With tfdt they are
        // absolute; MF's muxer OMITS tfdt, in which case decode time continues
        // from every prior fragment's total duration.
        val relative = decodeUs ?: LongArray(s.size)
        val tickBase = if (sawTfdt) baseDecodeTime else decodeTickBase
        val absolute = LongArray(relative.size) { tickBase + relative[it] }
        pendingFragment = Fragment(
            base, dataOffset, s,
            sync ?: BooleanArray(s.size) { true },
            absolute,
            tickBase + (durations?.sum() ?: 0L),
        )
    }

    private class TrunResult(
        val dataOffset: Long,
        val sizes: IntArray,
        val durations: LongArray,
        val sync: BooleanArray,
        val decodeTimes: LongArray, // relative decode times (sum of prior durations + ctso)
    )

    /** trun: per-sample sizes/flags/durations and the run's data offset. */
    private fun parseTrun(
        payload: Long,
        end: Long,
        defaultDuration: Long,
        defaultSize: Long,
        defaultFlags: Long,
    ): TrunResult {
        val flags = u32(payload) and 0xFFFFFF
        val sampleCount = u32(payload + 4).toInt()
        var p = payload + 8
        var dataOffset = 0L
        var firstFlagValue = 0L
        if (flags and 0x000001L != 0L) { dataOffset = u32(p).toInt().toLong(); p += 4 } // signed
        if (flags and 0x000004L != 0L) { firstFlagValue = u32(p); p += 4 }

        val withDuration = flags and 0x000100L != 0L
        val withSize = flags and 0x000200L != 0L
        val withFlags = flags and 0x000400L != 0L
        val withCtso = flags and 0x000800L != 0L

        val sizes = IntArray(sampleCount)
        val durations = LongArray(sampleCount)
        val sync = BooleanArray(sampleCount)
        val decode = LongArray(sampleCount)
        var acc = 0L
        for (i in 0 until sampleCount) {
            durations[i] = if (withDuration) { val d = u32(p); p += 4; d } else defaultDuration
            sizes[i] = if (withSize) { val s = u32(p); p += 4; s.toInt() } else defaultSize.toInt()
            val f: Long = when {
                withFlags -> { val v = u32(p); p += 4; v }
                i == 0 && flags and 0x000004L != 0L -> firstFlagValue
                defaultFlags >= 0 -> defaultFlags
                else -> if (i == 0) 0x02000000L else 0x01010000L // heuristic: first sample sync
            }
            sync[i] = (f and 0x10000L) == 0L // sample_is_non_sync_sample == 0
            var ctso = 0L
            if (withCtso) { ctso = u32(p).toInt().toLong(); p += 4 }
            decode[i] = acc + ctso
            acc += durations[i]
        }
        if (p > end) return TrunResult(dataOffset, IntArray(0), LongArray(0), BooleanArray(0), LongArray(0))
        return TrunResult(dataOffset, sizes, durations, sync, decode)
    }

    /** mdat fully received: emit the preceding fragment's samples as Annex-B. */
    private fun emitFragment(mdatEnd: Long) {
        val frag = pendingFragment ?: return
        pendingFragment = null
        if (!initEmitted) emitInitIfNeeded()

        var sampleStart = frag.base + frag.dataOffset
        for (i in frag.sizes.indices) {
            val size = frag.sizes[i]
            if (size <= 0) continue
            if (!have(sampleStart + size) || sampleStart + size > mdatEnd) return
            val from = absToIdx(sampleStart)
            val raw = buffer.copyOfRange(from, from + size)
            val annexB = nalLengthPrefixedToAnnexB(raw, nalLengthSize)
            if (annexB != null) {
                val absDecodeUs = frag.decodeTimeUs.getOrElse(i) { 0L }
                val ptsUs = if (timescale > 0) absDecodeUs * 1_000_000 / timescale else absDecodeUs
                listener.onSample(annexB, ptsUs, frag.sync.getOrElse(i) { false })
            }
            sampleStart += size
        }
        // The next fragment without a tfdt continues from here.
        decodeTickBase = maxOf(decodeTickBase, frag.totalDurationTicks)
    }

    private fun emitInitIfNeeded() {
        val s = sps ?: return
        val p = pps ?: return
        listener.onInit(annexB(s), annexB(p), width, height)
        initEmitted = true
    }

    /** Drops bytes the parser has fully consumed (the prefix before readPos). */
    private fun compact() {
        val drop = readPos - bufferBase
        if (drop <= 0) return
        val keep = buffer.size - drop.toInt()
        buffer = if (keep <= 0) ByteArray(0) else buffer.copyOfRange(drop.toInt(), buffer.size)
        bufferBase += drop
    }

    companion object {
        /** Prefixes one NAL with a 4-byte Annex-B start code. */
        fun annexB(nal: ByteArray): ByteArray {
            val out = ByteArray(nal.size + 4)
            out[3] = 1
            nal.copyInto(out, 4)
            return out
        }

        /**
         * Converts avcC-style length-prefixed NAL bytes (4-byte big-endian lengths,
         * or [lengthSize]) into an Annex-B byte stream. Returns null on malformed input.
         */
        fun nalLengthPrefixedToAnnexB(raw: ByteArray, lengthSize: Int = 4): ByteArray? {
            if (raw.isEmpty() || lengthSize < 1 || lengthSize > 4) return null
            var out = ByteArray(raw.size + 16)
            var outPos = 0
            var pos = 0
            while (pos + lengthSize <= raw.size) {
                var len = 0L
                for (k in 0 until lengthSize) len = (len shl 8) or (raw[pos + k].toLong() and 0xFF)
                pos += lengthSize
                if (len <= 0 || len > raw.size - pos) return null
                val needed = outPos + 4 + len.toInt()
                if (needed > out.size) out = out.copyOf(maxOf(needed, out.size * 2))
                out[outPos] = 0; out[outPos + 1] = 0; out[outPos + 2] = 0; out[outPos + 3] = 1
                outPos += 4
                raw.copyInto(out, outPos, pos, pos + len.toInt())
                outPos += len.toInt()
                pos += len.toInt()
            }
            return out.copyOf(outPos)
        }
    }
}
