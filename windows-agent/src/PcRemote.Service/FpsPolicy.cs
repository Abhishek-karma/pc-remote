// FPS adaptation policy for a live stream (pure, clock-injected, testable).
//
// The service-side forwarder knows when a client falls behind (its high-water
// mark fires), so congestion adaptation lives NEXT TO the signal instead of
// requiring a client→server protocol. Stepping FPS down sheds encoder+tail
// load immediately; bitrate stays fixed (CBR) because changing it would need
// encoder re-creation — a documented later optimization if physical testing
// shows FPS alone is insufficient.

namespace PcRemote.Service;

public sealed class FpsPolicy
{
    public const int MaxFps = 15;
    public const int MinFps = 5;

    /// <summary>Healthy time required at one level before stepping back up.</summary>
    public static readonly TimeSpan HealthyHold = TimeSpan.FromSeconds(10);

    private int _current;
    private DateTime _healthySince = DateTime.MinValue;

    public FpsPolicy(int startFps = MaxFps)
    {
        _current = Math.Clamp(startFps, MinFps, MaxFps);
    }

    public int Current => _current;

    /// <summary>A client fell behind: step down one level, return the new FPS.</summary>
    public int OnCongestion(DateTime now)
    {
        _current = _current switch
        {
            > 12 => 12,
            > 9 => 9,
            > 7 => 7,
            _ => MinFps,
        };
        _healthySince = now; // congestion resets the recovery timer
        return _current;
    }

    /// <summary>Called while the client keeps up. Returns the new FPS when a step
    /// up fires, or null when nothing changed.</summary>
    public int? OnHealthyTick(DateTime now)
    {
        if (_current >= MaxFps) return null;
        if (_healthySince == DateTime.MinValue) _healthySince = now;
        if (now - _healthySince < HealthyHold) return null;
        _current = _current switch
        {
            >= 12 => MaxFps,
            >= 9 => 12,
            >= 7 => 9,
            _ => 7,
        };
        _healthySince = now;
        return _current;
    }
}
