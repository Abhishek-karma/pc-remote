// Screen streamer: captures the desktop and pushes H.264 into a write-sharing
// fragmented-MP4 file that the SYSTEM service tails and forwards to the Android
// client. This keeps capture+encode in the user-session agent (the only place a
// desktop can be captured) while networking stays in the service — the agent never
// touches the network.
//
// The stream file is granted SYSTEM read access explicitly: the service runs as
// LocalSystem and must be able to open a file created by a user token.

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PcRemote.Core;

namespace PcRemote.Session.Streaming;

/// <summary>
/// Owns one capture+encode pipeline. Call <see cref="Start"/> to begin; the loop
/// captures at a target FPS, feeds the H.264 encoder, and appends to
/// <see cref="OutputPath"/>. A consumer tails that file for new bytes.
/// </summary>
public sealed class VideoStreamer : IVideoStream
{
    private readonly ScreenCapture _capture;
    private readonly H264Encoder _encoder;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private Task? _loop;
    private int _forceKeyframe;
    private volatile int _targetFps;
    private long _framesEncoded;
    private long _encodeFailures;
    private long _lastHealthLogMs;
    private long _framesAtLastHealthLog;
    private int _stopped;

    /// <summary>fMP4 file the encoder appends fragments to (SYSTEM-readable).</summary>
    public string OutputPath { get; }

    public int Width => _capture.Width;
    public int Height => _capture.Height;
    public int CurrentFps => _targetFps;
    public long FramesEncoded => Interlocked.Read(ref _framesEncoded);
    public long EncodeFailures => Interlocked.Read(ref _encodeFailures);

    public VideoStreamer(int fps, int bitrateBitsPerSecond, string? outputPath = null)
    {
        _capture = new ScreenCapture();
        OutputPath = outputPath ?? Path.Combine(Path.GetTempPath(), $"pcr-{Environment.ProcessId}-{Guid.NewGuid():N}.mp4");
        _encoder = new H264Encoder(OutputPath, _capture.Width, _capture.Height, fps, bitrateBitsPerSecond);
        GrantSystemRead(OutputPath);
        _targetFps = Math.Clamp(fps, 1, 60);
    }

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("streamer already started");
        // First frame must be a keyframe so a freshly-joined client can decode.
        Interlocked.Exchange(ref _forceKeyframe, 1);
        _clock.Start();
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Asks the encoder to emit an IDR on the next frame (join/loss recovery).</summary>
    public void RequestKeyframe() => Interlocked.Exchange(ref _forceKeyframe, 1);

    /// <summary>Runtime FPS adjustment (congestion adaptation): takes effect on the
    /// next capture tick.</summary>
    public void SetFps(int fps) => _targetFps = Math.Clamp(fps, 1, 60);

    private async Task LoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long startedMs = _clock.ElapsedMilliseconds;
                var force = Interlocked.Exchange(ref _forceKeyframe, 0) == 1;
                try
                {
                    var bgra = _capture.Capture();
                    _encoder.WriteFrame(bgra, TimeSpan.FromMilliseconds(startedMs), forceKeyframe: force);
                    Interlocked.Increment(ref _framesEncoded);
                }
                catch (Exception ex)
                {
                    // A single failed capture/encode must never kill the stream.
                    Interlocked.Increment(ref _encodeFailures);
                    AgentLog.Warn($"stream frame failed: {ex.Message}");
                }

                LogHealth();

                // Throttle to the (adjustable) target FPS.
                long frameInterval = Math.Max(1, 1000 / _targetFps);
                var delay = (int)(frameInterval - (_clock.ElapsedMilliseconds - startedMs));
                if (delay > 0) await Task.Delay(delay, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AgentLog.Error($"VideoStreamer loop failed: {ex.Message}");
        }
        finally
        {
            _encoder.Finish();
            AgentLog.Info($"stream stopped after {Interlocked.Read(ref _framesEncoded)} frames " +
                $"({_encodeFailures} failed)");
        }
    }

    /// <summary>Periodic observed-FPS line so latency/stall diagnosis is possible
    /// from the agent log alone (Debug: quiet unless someone is looking).</summary>
    private void LogHealth()
    {
        var nowMs = _clock.ElapsedMilliseconds;
        if (nowMs - _lastHealthLogMs < 5000) return;
        var frames = Interlocked.Read(ref _framesEncoded);
        var sinceLast = frames - Interlocked.Read(ref _framesAtLastHealthLog);
        var observedFps = sinceLast * 1000.0 / Math.Max(1, nowMs - _lastHealthLogMs);
        AgentLog.Debug($"stream health: target {_targetFps} fps, observed {observedFps:F1} fps, " +
            $"{frames} frames total, {_encodeFailures} failures");
        _lastHealthLogMs = nowMs;
        Interlocked.Exchange(ref _framesAtLastHealthLog, frames);
    }

    /// <summary>The service (LocalSystem) must be able to open the stream file even
    /// though a user token created it. Default user-file DACLs often omit SYSTEM.</summary>
    private static void GrantSystemRead(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var fs = file.GetAccessControl();
            fs.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(fs);
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"GrantSystemRead failed for stream file: {ex.Message}");
        }
    }

    public void Stop()
    {
        // Idempotent: Stop() and Dispose() both funnel here, and a second pass would
        // call MFShutdown more often than MFStartup (unbalanced Media Foundation
        // global state) and re-delete an already-deleted file.
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _cts.Cancel();
        try { _loop?.GetAwaiter().GetResult(); } catch { }
        _encoder.Dispose();
        _capture.Dispose();
        try { File.Delete(OutputPath); } catch { }
    }

    public void Dispose() => Stop();
}
