// One live screen stream, owned by the agent's StreamingCoordinator. The
// interface exists so the coordinator's lifecycle logic (notably keyframe-on-join)
// is unit-testable without a real desktop.

namespace PcRemote.Session.Streaming;

public interface IVideoStream : IDisposable
{
    string OutputPath { get; }
    int Width { get; }
    int Height { get; }
    int CurrentFps { get; }
    long FramesEncoded { get; }

    void Start();

    /// <summary>Full teardown: stop the capture loop, finalize the encoder, delete
    /// the stream file. Safe to call more than once.</summary>
    void Stop();

    /// <summary>Ask the encoder for an IDR on the next frame (join / loss recovery).</summary>
    void RequestKeyframe();

    /// <summary>Adjust the target capture FPS at runtime (congestion adaptation).</summary>
    void SetFps(int fps);
}
