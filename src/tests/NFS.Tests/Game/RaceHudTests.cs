using NFS.Core;
using NFS.Game.Race;
using NFS.Render;
using Xunit;

namespace NFS.Tests.Game;

public class RaceHudTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x200, 0)]
    [InlineData(0x200 + 65, 65)]
    public void FirstLap_TimerStartsAtGreen(int tick, int expected)
    {
        var c = new CarObj { LapStartTick = 0x200, RacePosition = 3, Gear = 1, Rpm = 2000 };
        var hud = RaceHud.Read(c, tick, 8, 8, 7000, true);
        Assert.Equal(expected, hud.LapTicks);
        Assert.Equal((1, 8, 3, 8, 1, 2000), (hud.Lap, hud.Laps, hud.Place, hud.Racers, hud.Gear, hud.Rpm));
    }

    [Theory]
    [InlineData(100, 1500)]
    [InlineData(319, 1500)]
    [InlineData(320, 320)]
    public void NewLap_HoldsPreviousLapForFiveSeconds(int elapsed, int shown)
    {
        var c = new CarObj { LapsDone = 1, LapStartTick = 2012, LastProgressTick = 2012 };
        c.LapTimes[0] = 1500;
        var hud = RaceHud.Read(c, 2012 + elapsed, 2, 8, 7000, true, 1600);
        Assert.Equal(shown, hud.LapTicks);
        Assert.Equal(1500, hud.BestTicks);
        Assert.Equal(2, hud.Lap);
        Assert.Equal(elapsed < 320, hud.FlashLap);
        Assert.Equal(elapsed <= 192, hud.FlashBest);
    }

    [Fact]
    public void FinishedCar_ShowsLastLapAndClampsLapCount_KeepsFasterRecord()
    {
        var c = new CarObj { LapsDone = 2, RaceState = 2, LapStartTick = 3000, RacePosition = 1 };
        c.LapTimes[0] = 1500; c.LapTimes[1] = 1600;
        var hud = RaceHud.Read(c, 10000, 2, 8, 7000, false, 1400);
        Assert.Equal((2, 1600, 1400, 1), (hud.Lap, hud.LapTicks, hud.BestTicks, hud.Place));
    }

    [Theory]
    [InlineData(655360, true, 35)] // 10 m/s: factor 0x39999 is just below 3.6; truncate, don't round to 36
    [InlineData(-655360, true, 35)]
    [InlineData(655360, false, 22)]
    [InlineData(0, true, 0)]
    public void Speed_UsesDriveForwardVelocityAndOriginalFixedPointFactor(int speed, bool metric, int expected)
    {
        var c = new CarObj { SpeedApprox = 999 * Fx.One, LocalVel = new(0, 0, speed), LapStartTick = 512 };
        Assert.Equal(expected, RaceHud.Read(c, 512, 8, 8, 7000, metric).Speed);
    }
}
