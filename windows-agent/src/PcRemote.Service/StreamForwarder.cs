// Tails the agent's fragmented-MP4 stream file and pushes every new byte to one
// client as a MediaFrame binary WebSocket message. The Android side concatenates
// payloads in sequence order and feeds the byte stream to MediaExtractor, so
// continuity matters: bytes are NEVER dropped silently.
//
// Backpressure: if a stalled client lets the unread gap grow past the high-water
// mark, the forwarder fast-forwards to the newest bytes and asks the agent for a
// keyframe, so the next fragment resyncs the decoder (the fMP4 equivalent of
// drop-oldest). The client's keyframe_request does the same on demand.

using System.Diagnostics;
using System.Text;
using PcRemote.Core;

namespace PcRemote.Service;

/// <summary>Where a live stream comes from: the agent's fMP4 file plus negotiated dimensions.</summary>
public readonly record struct StreamBinding(string Path, int Width, int Height, int Fps);

public sealed class StreamForwarder
{
    public const int ChunkSize = 64 * 1024;
    public const long MaxBehindBytes = 8 * 1024 * 1024;
    public const int PollMs = 15;
    public const int HealthyProbeMs = 2000;

    private readonly string _path;
    private readonly Func<MediaFrame, Task> _send;
    private readonly Func<Task>? _onDropped;
    private readonly Func<Task>? _onHealthy;
    private readonly int _pollMs;
    private readonly long _maxBehindBytes;
    private long _lastHealthyProbeMs;

    public StreamForwarder(string path, Func<MediaFrame, Task> send,
        Func<Task>? onDropped = null, Func<Task>? onHealthy = null,
        int pollMs = PollMs, long maxBehindBytes = MaxBehindBytes)
    {
        _path = path;
        _send = send;
        _onDropped = onDropped;
        _onHealthy = onHealthy;
        _pollMs = pollMs;
        _maxBehindBytes = maxBehindBytes;
    }

    /// <summary>
    /// Walks the top-level MP4 boxes from the start of the file and returns the
    /// offset where the first <c>moof</c> begins — i.e. the length of the init
    /// segment (ftyp + moov, which carries the avcC SPS/PPS).
    ///
    /// A client that joins a stream already in progress must receive this segment
    /// or MediaCodec can never be configured: an IDR alone does not carry the
    /// parameter sets. Returns 0 when the init segment is not (yet) fully written,
    /// in which case the caller simply reads from the beginning and lets it arrive
    /// inline.
    /// </summary>
    internal static long FindInitSegmentEnd(FileStream fs)
    {
        var header = new byte[8];
        long pos = 0;
        var start = fs.Position;
        try
        {
            fs.Position = 0;
            while (pos < fs.Length)
            {
                fs.Position = pos;
                if (fs.Read(header, 0, 8) < 8) return 0;

                long size = (uint)((header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                long headerLength = 8;
                if (size == 1)
                {
                    // 64-bit largesize: the real length follows the type field.
                    var ext = new byte[8];
                    if (fs.Read(ext, 0, 8) < 8) return 0;
                    size = 0;
                    foreach (var b in ext) size = (size << 8) | b;
                    headerLength = 16;
                }
                if (size < headerLength || pos + size > fs.Length) return 0; // still being written
                if (type == "moof") return pos;
                pos += size;
            }
        }
        catch (IOException) { return 0; }
        finally { fs.Position = start; }
        return 0;
    }

    /// <summary>Runs until cancelled. Opens the file once it exists; ends when the
    /// file disappears (agent stopped streaming) or cancellation fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long offset = 0;
        uint seq = 0;
        var initDelivered = false;
        FileStream? fs = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (fs is null || !fs.CanRead)
                {
                    try
                    {
                        // Read sharing so the agent's encoder keeps writing; delete
                        // sharing so its cleanup can remove the file under us.
                        fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (FileNotFoundException) { await Wait(ct); continue; }
                    catch (IOException) { await Wait(ct); continue; }
                    catch (UnauthorizedAccessException)
                    {
                        // File replaced by a new stream under a different ACL: re-open.
                        try { fs?.Dispose(); } catch { }
                        fs = null;
                        await Wait(ct);
                        continue;
                    }
                }

                if (fs is null) { await Wait(ct); continue; }

                long size;
                try { size = fs.Length; }
                catch (IOException) { break; } // file gone: agent stopped streaming

                if (size <= offset)
                {
                    await Wait(ct);
                    continue;
                }

                var behind = size - offset;
                if (behind > _maxBehindBytes)
                {
                    // Before jumping to the live edge, make sure this client has the
                    // init segment. Without ftyp+moov the decoder can never be
                    // configured and the stream stays black no matter how many
                    // keyframes follow.
                    if (!initDelivered)
                    {
                        if (await TrySendInitSegmentAsync(fs, seq, ct)) initDelivered = true;
                    }

                    AgentLog.Warn($"stream client {behind >> 10} KiB behind; fast-forwarding and requesting keyframe");
                    offset = size;
                    fs.Seek(offset, SeekOrigin.Begin);
                    if (_onDropped is not null)
                    {
                        try { await _onDropped(); } catch { /* best effort */ }
                    }
                    continue;
                }

                var toRead = (int)Math.Min(behind, ChunkSize);
                var buffer = new byte[toRead];
                int read;
                try { read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct); }
                catch (IOException) { break; }
                if (read <= 0) { await Wait(ct); continue; }

                offset += read;
                await _send(new MediaFrame(
                    IsKeyframe: false,
                    Seq: seq++,
                    PtsMilliseconds: (uint)Math.Min(uint.MaxValue, sw.ElapsedMilliseconds),
                    buffer.AsSpan(0, read).ToArray()));

                // While the client keeps up, periodically report health so the FPS
                // policy can step back up after congestion has cleared.
                if (_onHealthy is not null && sw.ElapsedMilliseconds - _lastHealthyProbeMs >= HealthyProbeMs)
                {
                    _lastHealthyProbeMs = sw.ElapsedMilliseconds;
                    try { await _onHealthy(); } catch { /* adaptation is best-effort */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AgentLog.Warn($"stream forwarder ended: {ex.Message}");
        }
        finally
        {
            try { fs?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Reads the ftyp+moov init segment from the start of the stream file and pushes
    /// it to the client ahead of any live-edge fast-forward. Returns false when the
    /// segment is not yet fully written (the caller retries later).
    /// </summary>
    private async Task<bool> TrySendInitSegmentAsync(FileStream fs, uint seq, CancellationToken ct)
    {
        var end = FindInitSegmentEnd(fs);
        if (end <= 0) return false;

        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            var remaining = end;
            var buf = new byte[ChunkSize];
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, ChunkSize);
                var read = await fs.ReadAsync(buf.AsMemory(0, toRead), ct);
                if (read <= 0) return false;
                remaining -= read;
                // isKeyframe=false: init bytes are not a decodable frame on their own,
                // so they must never be fed to the decoder as one.
                await _send(new MediaFrame(
                    IsKeyframe: false,
                    Seq: seq++,
                    PtsMilliseconds: 0,
                    buf.AsSpan(0, read).ToArray()));
            }
            AgentLog.Info($"sent {end} byte init segment to joining stream client");
            return true;
        }
        catch (IOException) { return false; }
    }

    private async Task Wait(CancellationToken ct)
    {
        try { await Task.Delay(_pollMs, ct); }
        catch (OperationCanceledException) { throw; }
    }
}
