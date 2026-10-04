// Media Foundation H.264 encoder driven through the SinkWriter.
//
// The raw MFT path (ProcessInput/ProcessOutput) is unreliable here: the Microsoft
// H.264 encoder MFT is an asynchronously-driven transform and only answers
// NEED_MORE_INPUT without an event pump. The SinkWriter manages that pump internally,
// so it is both reliable and low-complexity.
//
// Output is a FRAGMENTED MP4 stream (ftyp+moov init, then self-contained moof+mdat
// fragments) written to a write-sharing file. A reader tails the file and forwards
// new bytes to the client — the proven CouchDesk live-streaming pattern. Low-latency
// knobs are forced: real-time mode, zero B-frames (no decode reordering), CBR capped
// at the target bitrate, and keyframe-on-demand via the CleanPoint sample flag.
//
// The Android side demuxes this fMP4 (MediaExtractor over a MediaDataSource).

using Vortice.MediaFoundation;

namespace PcRemote.Session.Streaming;

public sealed class H264Encoder : IDisposable
{
    private static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-4e1b-8c3b-7d9dfc5e80c0");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    private const uint eAVEncH264VProfile_High = 100; // 100=High 77=Main 66=Baseline

    private readonly int _height, _fps, _stride;
    private readonly long _frameDurationTicks;
    private readonly IMFSinkWriter _writer;
    private readonly IMFMediaSink? _sink;
    private readonly IMFByteStream? _byteStream;
    private const int StreamIndex = 0;
    private bool _finished;

    /// <summary>The fragmented-MP4 file the encoder appends fragments to.</summary>
    public string OutputPath { get; }

    public H264Encoder(string outputPath, int width, int height, int fps, int bitrateBitsPerSecond)
    {
        if (width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("H.264 frame dimensions must be even.");
        OutputPath = outputPath;
        _height = height;
        _fps = fps;
        _stride = width * 4;
        _frameDurationTicks = TimeSpan.FromSeconds(1.0 / fps).Ticks;

        MediaFactory.MFStartup(false);

        // Encoded stream type (configures the encoder the Sink Writer inserts).
        using (var h264 = MediaFactory.MFCreateMediaType())
        {
            h264.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            h264.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            h264.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrateBitsPerSecond);
            h264.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            h264.Set(MediaTypeAttributeKeys.Mpeg2Profile, eAVEncH264VProfile_High);
            h264.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
            h264.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(fps, 1));
            h264.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));

            // Fragmented-MP4 sink over a write-shared file so a reader can tail it live.
            _byteStream = MediaFactory.MFCreateFile(
                FileAccessMode.MfAccessModeReadwrite,
                FileOpenMode.MfOpenModeDeleteIfExist,
                FileFlags.FlagsAllowWriteSharing,
                outputPath);
            MediaFactory.MFCreateFMPEG4MediaSink(_byteStream, h264, null, out var sink);
            _sink = sink;

            using (var attrs = MediaFactory.MFCreateAttributes(2))
            {
                attrs.Set(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
                attrs.Set(MF_SINK_WRITER_DISABLE_THROTTLING, 1u);
                _writer = MediaFactory.MFCreateSinkWriterFromMediaSink(_sink, attrs);
            }
        }

        // Uncompressed input: RGB32 (BGRA byte order), top-down.
        using (var rgb = MediaFactory.MFCreateMediaType())
        {
            rgb.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            rgb.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            rgb.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            rgb.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            rgb.Set(MediaTypeAttributeKeys.DefaultStride, (uint)_stride);
            rgb.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
            rgb.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(fps, 1));
            rgb.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));

            // Low-latency encoder config: real-time mode, no B-frames, CBR capped at
            // the target bitrate. If the selected encoder rejects these, fall back to
            // defaults so the stream still starts.
            using (var encodingParams = MediaFactory.MFCreateAttributes(4))
            {
                encodingParams.Set(CODECAPI_AVLowLatencyMode, 1u);
                encodingParams.Set(CODECAPI_AVEncMPVDefaultBPictureCount, 0u);
                encodingParams.Set(CODECAPI_AVEncCommonRateControlMode, 0u); // 0 = CBR
                encodingParams.Set(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrateBitsPerSecond);
                try { _writer.SetInputMediaType(StreamIndex, rgb, encodingParams); }
                catch { _writer.SetInputMediaType(StreamIndex, rgb, null); }
            }
        }

        _writer.BeginWriting();
    }

    /// <summary>Feeds one BGRA (RGB32) frame into the encoder. A false return means
    /// the frame was rejected (e.g. wrong size); callers treat it as a dropped frame,
    /// never as a crash.</summary>
    public bool WriteFrame(ReadOnlySpan<byte> bgra, TimeSpan timestamp, bool forceKeyframe)
    {
        int size = _stride * _height;
        if (bgra.Length < size) return false;

        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(size);
        buffer.Lock(out var dst, out _, out _);
        try
        {
            unsafe
            {
                fixed (byte* src = bgra)
                    Buffer.MemoryCopy(src, dst.ToPointer(), size, size);
            }
        }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = size;

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp.Ticks;
        sample.SampleDuration = _frameDurationTicks;
        if (forceKeyframe) sample.Set(MFSampleExtension_CleanPoint, 1u);

        _writer.WriteSample(StreamIndex, sample);
        sample.Dispose();
        buffer.Dispose();
        return true;
    }

    /// <summary>Finalizes the stream (writes trailing boxes). Safe to call once.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        try { _writer.Finalize(); } catch { }
    }

    public void Dispose()
    {
        Finish();
        try { _writer?.Dispose(); } catch { }
        try { _sink?.Shutdown(); } catch { }
        try { _sink?.Dispose(); } catch { }
        try { _byteStream?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }
    }
}
