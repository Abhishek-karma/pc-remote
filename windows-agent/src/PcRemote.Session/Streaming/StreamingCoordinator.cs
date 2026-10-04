// Owns the screen streamer lifecycle inside the agent. The service drives it over
// the session control pipe: stream_start creates + starts a VideoStreamer and returns
// the fMP4 file path + dimensions; stream_stop stops it; stream_keyframe asks for an
// IDR; stream_fps adjusts the target FPS at runtime.
// Only the normal (user-session) helper streams — the secure/Winlogon helper never does.

namespace PcRemote.Session.Streaming;

public sealed class StreamingCoordinator : IDisposable
{
    private readonly object _lock = new();
    private readonly Func<int, int, IVideoStream> _factory;
    private IVideoStream? _streamer;

    /// <summary>
    /// How many clients are currently attached to the running stream. The stream is
    /// SHARED: a second caller re-attaches to the existing encoder rather than
    /// starting a competing one, so tearing it down must wait for the last client to
    /// leave — otherwise one device disconnecting kills the picture for the others.
    /// </summary>
    private int _clients;

    /// <summary>Result handed back to the service so it can tail the stream file.</summary>
    public readonly record struct StreamInfo(string Path, int Width, int Height, int Fps);

    /// <summary>Default production factory: a real GDI-capture streamer.</summary>
    public StreamingCoordinator(Func<int, int, IVideoStream>? factory = null)
    {
        _factory = factory ?? ((fps, bitrate) => new VideoStreamer(fps, bitrate));
    }

    /// <summary>Number of clients currently attached (0 when nothing is running).</summary>
    public int AttachedClients { get { lock (_lock) return _clients; } }

    /// <summary>Starts (or re-attaches to) the streamer, incrementing the client
    /// count. Returns the file to tail, or null when this session has no attached
    /// desktop to capture.</summary>
    public StreamInfo? Start(int fps, int bitrate)
    {
        lock (_lock)
        {
            if (_streamer is not null)
            {
                _clients++;
                // Keyframe-on-join: a client attaching to an ALREADY-RUNNING stream
                // can only decode from the next IDR — without this it would stare
                // at a frozen frame until the next GOP boundary. (The init segment
                // is delivered separately by the service's forwarder.)
                _streamer.RequestKeyframe();
                return new StreamInfo(_streamer.OutputPath, _streamer.Width, _streamer.Height, _streamer.CurrentFps);
            }

            // ScreenCapture throws when no interactive desktop is attached; the
            // service then reports "stream unavailable" instead of crashing us.
            IVideoStream stream;
            try
            {
                stream = _factory(fps, bitrate);
                stream.Start();
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            _streamer = stream;
            _clients = 1;
            return new StreamInfo(stream.OutputPath, stream.Width, stream.Height, stream.CurrentFps);
        }
    }

    public void SetFps(int fps)
    {
        lock (_lock) _streamer?.SetFps(fps);
    }

    public void RequestKeyframe()
    {
        lock (_lock) _streamer?.RequestKeyframe();
    }

    /// <summary>Detaches ONE client. The encoder is only torn down when the last
    /// client leaves, so one device disconnecting cannot cut the others off.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_streamer is null) { _clients = 0; return; }
            if (_clients > 0) _clients--;
            if (_clients > 0) return;
            TearDownLocked();
        }
    }

    /// <summary>Forces the stream down regardless of the client count (agent exit).</summary>
    public void StopAll()
    {
        lock (_lock) TearDownLocked();
    }

    private void TearDownLocked()
    {
        // Stop() then Dispose() would tear down twice: VideoStreamer.Stop is not
        // idempotent and MFShutdown would be called more often than MFStartup.
        _streamer?.Dispose();
        _streamer = null;
        _clients = 0;
    }

    public bool IsRunning { get { lock (_lock) return _streamer is not null; } }

    public void Dispose() => StopAll();
}
