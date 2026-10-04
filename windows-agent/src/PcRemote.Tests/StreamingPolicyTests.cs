// Increment E tests: FPS adaptation policy (pure) and the streaming coordinator's
// lifecycle behavior (keyframe-on-join, FPS relay, stop) against a fake stream.

using PcRemote.Session.Streaming;
using PcRemote.Service;
using Xunit;

namespace PcRemote.Tests;

public class FpsPolicyTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CongestionWalksDownToTheMinimum()
    {
        var policy = new FpsPolicy(startFps: FpsPolicy.MaxFps);
        var t = T0;
        Assert.Equal(12, policy.OnCongestion(t));
        Assert.Equal(9, policy.OnCongestion(t));
        Assert.Equal(7, policy.OnCongestion(t));
        Assert.Equal(5, policy.OnCongestion(t));
        Assert.Equal(FpsPolicy.MinFps, policy.OnCongestion(t)); // floor holds
    }

    [Fact]
    public void HealthyTicksStepBackUpOnlyAfterTheHold()
    {
        var policy = new FpsPolicy(startFps: 7);
        var t = T0;
        // First tick arms the timer; the hold must elapse before a step up.
        Assert.Null(policy.OnHealthyTick(t));
        Assert.Null(policy.OnHealthyTick(t + FpsPolicy.HealthyHold - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(9, policy.OnHealthyTick(t + FpsPolicy.HealthyHold));
        Assert.Equal(12, policy.OnHealthyTick(t + 2 * FpsPolicy.HealthyHold));
        Assert.Equal(FpsPolicy.MaxFps, policy.OnHealthyTick(t + 3 * FpsPolicy.HealthyHold));
        Assert.Null(policy.OnHealthyTick(t + 4 * FpsPolicy.HealthyHold)); // ceiling holds
    }

    [Fact]
    public void CongestionResetsTheRecoveryTimer()
    {
        var policy = new FpsPolicy(startFps: 9);
        Assert.Equal(7, policy.OnCongestion(T0));
        // Healthy period shorter than the hold: no step up.
        Assert.Null(policy.OnHealthyTick(T0 + FpsPolicy.HealthyHold - TimeSpan.FromSeconds(1)));
        // New congestion resets the clock even late in the hold.
        var secondCongestion = T0 + FpsPolicy.HealthyHold - TimeSpan.FromMilliseconds(500);
        Assert.Equal(FpsPolicy.MinFps, policy.OnCongestion(secondCongestion));
        // Recovery now counts from the SECOND congestion.
        Assert.Null(policy.OnHealthyTick(secondCongestion + FpsPolicy.HealthyHold - TimeSpan.FromSeconds(1)));
        Assert.Equal(7, policy.OnHealthyTick(secondCongestion + FpsPolicy.HealthyHold));
    }
}

public class StreamingCoordinatorTests
{
    private sealed class FakeStream : IVideoStream
    {
        public string OutputPath { get; } = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.mp4");
        public int Width { get; } = 320;
        public int Height { get; } = 240;
        public int CurrentFps { get; private set; }
        public long FramesEncoded => 0;
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public int DisposeCount { get; private set; }
        public int KeyframeRequests { get; private set; }

        public FakeStream(int fps) => CurrentFps = fps;
        public void Start() => Started = true;
        public void Stop() => Stopped = true;
        public void RequestKeyframe() => KeyframeRequests++;
        public void SetFps(int fps) => CurrentFps = fps;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void StartWhenIdleCreatesAndStartsTheStream()
    {
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((fps, bitrate) => fake = new FakeStream(fps));

        var info = coordinator.Start(fps: 15, bitrate: 4_000_000);

        Assert.NotNull(fake);
        Assert.True(fake!.Started);
        Assert.NotNull(info);
        Assert.Equal(fake.OutputPath, info!.Value.Path);
        Assert.Equal(320, info.Value.Width);
        Assert.Equal(15, info.Value.Fps);
        Assert.True(coordinator.IsRunning);
    }

    [Fact]
    public void JoiningAnAlreadyRunningStreamRequestsAKeyframe()
    {
        // A client attaching to a live stream can only decode from the next IDR:
        // without keyframe-on-join it would freeze until the next GOP boundary.
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((_, _) => fake = new FakeStream(15));
        var first = coordinator.Start(15, 4_000_000);

        Assert.NotNull(fake);
        Assert.Equal(0, fake!.KeyframeRequests); // first start: the streamer itself forces the IDR

        var second = coordinator.Start(15, 4_000_000);

        Assert.NotNull(second);
        Assert.Equal(first!.Value.Path, second!.Value.Path); // same stream file
        Assert.Equal(1, fake.KeyframeRequests); // join re-synced via keyframe request
    }

    [Fact]
    public void SetFpsRelaysToTheLiveStreamer()
    {
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((_, _) => fake = new FakeStream(15));
        coordinator.Start(15, 4_000_000);

        coordinator.SetFps(7);

        Assert.NotNull(fake);
        Assert.Equal(7, fake!.CurrentFps);
    }

    [Fact]
    public void StopTearsTheStreamDown()
    {
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((_, _) => fake = new FakeStream(15));
        coordinator.Start(15, 4_000_000);

        coordinator.Stop();

        Assert.NotNull(fake);
        // Tear-down goes through Dispose() exactly once (calling Stop() as well would
        // unbalance MFStartup/MFShutdown).
        Assert.Equal(1, fake!.DisposeCount);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public void StartReportsUnavailableWhenNoDesktopExists()
    {
        var coordinator = new StreamingCoordinator((_, _) => throw new InvalidOperationException("no desktop"));
        Assert.Null(coordinator.Start(15, 4_000_000));
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public void OneClientLeavingDoesNotKillTheStreamForTheOthers()
    {
        // Regression: the stream is shared, so a disconnecting device must not stop
        // the encoder out from under everyone still watching.
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((_, _) => fake = new FakeStream(15));
        coordinator.Start(15, 4_000_000);
        coordinator.Start(15, 4_000_000); // second device joins the same stream
        Assert.Equal(2, coordinator.AttachedClients);

        coordinator.Stop(); // first device disconnects

        Assert.True(coordinator.IsRunning);
        Assert.Equal(1, coordinator.AttachedClients);
        Assert.Equal(0, fake!.DisposeCount); // encoder untouched

        coordinator.Stop(); // second device disconnects

        Assert.False(coordinator.IsRunning);
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public void TeardownHappensExactlyOnce()
    {
        // Regression: Stop() + Dispose() used to both run, unbalancing MFStartup/MFShutdown.
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((_, _) => fake = new FakeStream(15));
        coordinator.Start(15, 4_000_000);

        coordinator.Stop();
        coordinator.Dispose();

        Assert.Equal(1, fake!.DisposeCount);
    }

    [Fact]
    public void StopAllForcesTheStreamDownEvenWithClientsAttached()
    {
        // Agent exit must not leave an encoder running.
        FakeStream? fake = null;
        var coordinator = new StreamingCoordinator((_, _) => fake = new FakeStream(15));
        coordinator.Start(15, 4_000_000);
        coordinator.Start(15, 4_000_000);

        coordinator.StopAll();

        Assert.False(coordinator.IsRunning);
        Assert.Equal(0, coordinator.AttachedClients);
        Assert.Equal(1, fake!.DisposeCount);
    }
}
