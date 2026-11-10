using NFS.Desktop;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>CPU / battery: the frame-rate caps come from port.json, the limiter sleeps the rest of the period instead of spinning,
/// and rendering the same race state twice gives the same pixels (the z-sort buffers are reused between frames).</summary>
public class FramePacingTests
{
    [Fact]
    public void DefaultsAre60InRaceAnd30InMenusAndSettingsOverrideAndClamp()
    {
        Assert.Equal(60, FramePacing.TargetFps(null, racing: true, windowActive: true));
        Assert.Equal(30, FramePacing.TargetFps(null, racing: false, windowActive: true));
        var s = new PortSettings { MaxFps = 30, MenuFps = 20 };
        Assert.Equal(30, FramePacing.TargetFps(s, true, true));
        Assert.Equal(20, FramePacing.TargetFps(s, false, true));
        Assert.Equal(0, FramePacing.TargetFps(new PortSettings { MaxFps = 0 }, true, true));         // 0 = vsync only
        Assert.Equal(FramePacing.MinFps, FramePacing.TargetFps(new PortSettings { MaxFps = 1 }, true, true));
        Assert.Equal(FramePacing.MaxFpsLimit, FramePacing.TargetFps(new PortSettings { MaxFps = 9999 }, true, true));
    }

    [Fact]
    public void UnfocusedWindowIsThrottledEvenWithoutACap()
    {
        Assert.Equal(FramePacing.InactiveFps, FramePacing.TargetFps(null, true, windowActive: false));
        Assert.Equal(FramePacing.InactiveFps, FramePacing.TargetFps(new PortSettings { MaxFps = 0 }, true, windowActive: false));
        Assert.Equal(FramePacing.InactiveFps, FramePacing.TargetFps(new PortSettings { MenuFps = 5 }, false, windowActive: false));
    }

    [Fact]
    public void FpsKeysSurviveThePortJsonRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-fps-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            Assert.Null(new PortSettings { MaxFps = 30, MenuFps = 15 }.Save(path));
            var p = PortSettings.Load(path);
            Assert.Equal(30, p.MaxFps);
            Assert.Equal(15, p.MenuFps);
            Assert.Null(PortSettings.Load(Path.Combine(dir, "none.json")).MaxFps);
        }
        finally { Directory.Delete(dir, true); }
    }

    sealed class FakeClock
    {
        public long Ticks = 1_000_000;                    // TimeSpan ticks (100 ns)
        public readonly List<int> Sleeps = [];
        public void Sleep(int ms) { Sleeps.Add(ms); Ticks += ms * TimeSpan.TicksPerMillisecond; }
        public void Work(double ms) => Ticks += (long)(ms * TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void FastFramesSleepTheRestOfThePeriodSlowOnesDoNot()
    {
        var c = new FakeClock();
        var limiter = new FrameLimiter(() => c.Ticks, c.Sleep);
        Assert.Equal(0, limiter.Wait(60));                // the first frame has no predecessor
        c.Work(4);                                        // 4 ms of work -> 60 fps period 16.67 ms: sleep 12 ms
        Assert.Equal(12, limiter.Wait(60));
        c.Work(20);                                       // slower than the period: no sleep, no catch-up debt
        Assert.Equal(0, limiter.Wait(60));
        c.Work(1);
        Assert.Equal(15, limiter.Wait(60));
    }

    [Fact]
    public void NoCapNeverSleepsAndHighLoadIsBelowOneCore()
    {
        var c = new FakeClock();
        var limiter = new FrameLimiter(() => c.Ticks, c.Sleep);
        for (int i = 0; i < 100; i++) { c.Work(0.5); Assert.Equal(0, limiter.Wait(0)); }
        Assert.Empty(c.Sleeps);
        // Behaviour: a 2 ms frame at a 30 fps cap leaves about 94 % of the time asleep (the CPU is idle meanwhile).
        c = new FakeClock();
        limiter = new FrameLimiter(() => c.Ticks, c.Sleep);
        limiter.Wait(30);
        long start = c.Ticks;
        for (int i = 0; i < 300; i++) { c.Work(2); limiter.Wait(30); }
        double total = (c.Ticks - start) / (double)TimeSpan.TicksPerMillisecond, slept = c.Sleeps.Sum();
        Assert.InRange(slept / total, 0.85, 0.97);
        Assert.InRange(300 / (total / 1000), 29, 31);     // ~30 frames per second
    }

    [GameDataFact]
    public void RenderingTheSameStateTwiceGivesTheSamePixels()
    {
        var drive = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 300; i++) drive.Frame(1.0 / 60, gas);
        var a = new FrameBuffer(); var b = new FrameBuffer();
        drive.Render(a);
        drive.Render(b);
        Assert.True(a.Rgba.AsSpan().SequenceEqual(b.Rgba));
    }
}
