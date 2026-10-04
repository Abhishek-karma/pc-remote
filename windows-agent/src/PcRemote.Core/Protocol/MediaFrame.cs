// Binary frame envelope for the media channel (carried as WebSocket binary
// frames between the PC service and the Android client).
//
// Header: little-endian, fixed 18 bytes, followed by the frame payload
// (compressed H.264 Annex-B NAL bytes — never raw bitmaps).
//   0   magic u32  0x5043524D ("PCRM")
//   4   version u8  1
//   5   flags u8    bit0 = keyframe (SPS/PPS + IDR present)
//   6   seq u32     monotonic per stream; lets the client drop stale frames
//   10  pts u32     presentation timestamp in milliseconds
//   14  length u32  payload length in bytes
//
// Unknown future versions are rejected by Parse so a protocol mismatch fails
// loudly instead of silently mangling a frame.

using System.Buffers.Binary;

namespace PcRemote.Core;

public readonly record struct MediaFrame(bool IsKeyframe, uint Seq, uint PtsMilliseconds, byte[] Payload)
{
    public const uint Magic = 0x5043524D; // "PCRM"
    public const byte Version = 1;
    public const int HeaderLength = 18;

    private const byte KeyframeFlag = 0x01;

    public byte[] ToBytes()
    {
        var b = new byte[HeaderLength + Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), Magic);
        b[4] = Version;
        b[5] = IsKeyframe ? KeyframeFlag : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6, 4), Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10, 4), PtsMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(14, 4), (uint)Payload.Length);
        Payload.CopyTo(b.AsSpan(HeaderLength));
        return b;
    }

    /// <summary>Parses a single binary WS frame payload into a MediaFrame, or
    /// null when the header is malformed or the declared length does not match
    /// the buffer (so a framing bug is never silently accepted).</summary>
    public static MediaFrame? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) != Magic) return null;
        if (data[4] != Version) return null;
        var flags = data[5];
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(6, 4));
        var pts = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10, 4));
        var len = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
        if (len != (uint)(data.Length - HeaderLength)) return null;
        return new MediaFrame(
            (flags & KeyframeFlag) != 0,
            seq,
            pts,
            data.Slice(HeaderLength, (int)len).ToArray());
    }
}
