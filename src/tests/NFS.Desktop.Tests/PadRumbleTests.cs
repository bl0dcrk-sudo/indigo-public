using NFS.Core;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Pad vibration (<see cref="PadRumble"/>): the logic with a fake device. No window, no scene.</summary>
public class PadRumbleTests
{
    sealed class FakePad : IRumbleSink
    {
        public readonly List<(int Player, float Left, float Right)> Calls = [];
        public readonly float[] Left = new float[2], Right = new float[2];
        public void Set(int player, float left, float right) { Calls.Add((player, left, right)); Left[player] = left; Right[player] = right; }
    }

    static RumbleSample Calm(long tick, int speed = 0, int surface = 1, int gear = 3) => new(tick, 0, 0, surface, speed, gear, 3000);
    static RumbleSample Wall(long tick, int speed = 60) => new(tick, 0x100000, 0x30000 | 1, 1, speed, 3, 3000);
    const double Dt = 1.0 / 60;

    static (PadRumble Rumble, FakePad Pad) Make() { var pad = new FakePad(); return (new PadRumble(pad), pad); }

    [Fact]
    public void CollisionGivesAnImpulseThatFadesToZero()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1, 60));
        r.Update(Dt, true, true, Wall(2));
        float first = pad.Left[0];
        Assert.True(first > 0.8f, "a hard wall hit is strong");
        float previous = first;
        for (int t = 3; t < 40; t++)
        {
            r.Update(Dt, true, true, Calm(t, 60));
            Assert.True(pad.Left[0] <= previous + 1e-6f, "never grows");
            previous = pad.Left[0];
        }
        Assert.Equal(0f, pad.Left[0]);
        Assert.Equal(0f, pad.Right[0]);
    }

    [Fact]
    public void TheSameImpactOfOneTickIsTakenOnce()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1));
        r.Update(Dt, true, true, Wall(2));
        for (int i = 0; i < 40; i++) r.Update(Dt, true, true, Wall(2));   // frames without a new simulation step
        Assert.Equal(0f, pad.Left[0]);
    }

    [Fact]
    public void FasterWallHitIsStrongerAndShorterThanASlowOne()
    {
        var slow = PadRumble.ImpactJolt(0x30001, 1, 10);
        var fast = PadRumble.ImpactJolt(0x30001, 1, 80);
        Assert.True(fast.Magnitude > slow.Magnitude);
        Assert.True(fast.Ms < slow.Ms);
        Assert.True(PadRumble.ImpactJolt(0x50001, 0x200000, 0).Magnitude > PadRumble.ImpactJolt(0x50001, 0x1000, 0).Magnitude);
    }

    [Fact]
    public void RoughSurfaceRumblesWithSpeedAndSmoothRoadDoesNot()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1, 60, surface: 7));
        Assert.True(pad.Left[0] > 0 && pad.Right[0] > 0);
        r.Update(Dt, true, true, Calm(2, 60, surface: 6));
        Assert.Equal(0f, pad.Left[0]);
        r.Update(Dt, true, true, Calm(3, 1, surface: 7));        // standing: no road effect
        Assert.Equal(0f, pad.Left[0]);
        Assert.True(PadRumble.RoadMagnitude(7, 50) > PadRumble.RoadMagnitude(3, 50));
    }

    [Fact]
    public void PauseAndEndOfTheRaceZeroTheMotors()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1, 60, surface: 7));
        r.Update(Dt, true, true, Wall(2));
        Assert.True(pad.Left[0] > 0);
        r.Update(Dt, true, false, Wall(3));                     // paused / menu / results / replay / no focus
        Assert.Equal((0f, 0f), (pad.Left[0], pad.Right[0]));
        r.Update(Dt, true, true, Calm(4, 60, surface: 7));      // back: the old jolt does not come back
        Assert.Equal(PadRumble.RoadMagnitude(7, 60) / 10000f * 0.5f, pad.Left[0], 4);
        r.Update(Dt, true, true, null);                         // no car any more
        Assert.Equal(0f, pad.Left[0]);
    }

    [Fact]
    public void OptionOffKeepsTheMotorsAtZeroAndNeverVibrates()
    {
        var (r, pad) = Make();
        r.Update(Dt, false, true, Calm(1, 60, surface: 7));
        r.Update(Dt, false, true, Wall(2));
        Assert.All(pad.Calls, c => Assert.Equal((0f, 0f), (c.Left, c.Right)));
        // switching it off in the middle of a vibration stops it at once
        var (r2, pad2) = Make();
        r2.Update(Dt, true, true, Calm(1, 60, surface: 7));
        r2.Update(Dt, true, true, Wall(2));
        r2.Update(Dt, false, true, Calm(3, 60, surface: 7));
        Assert.Equal((0f, 0f), (pad2.Left[0], pad2.Right[0]));
    }

    [Fact]
    public void SplitScreenSendsEachPlayerToTheirOwnPad()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1, 60), Calm(1, 60));
        r.Update(Dt, true, true, Calm(2, 60), Wall(2));
        Assert.Equal(0f, pad.Left[0]);
        Assert.True(pad.Left[1] > 0.8f);
        r.Update(Dt, true, true, Wall(3), Calm(3, 60));
        Assert.True(pad.Left[0] > 0.8f);
        Assert.Contains(pad.Calls, c => c.Player == 1);
    }

    [Fact]
    public void GearChangeIsAShortJoltAndOnlyForwardGears()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1, 40, gear: 2));
        r.Update(Dt, true, true, Calm(2, 40, gear: 3));
        Assert.True(pad.Left[0] > 0);
        for (int t = 3; t < 30; t++) r.Update(Dt, true, true, Calm(t, 40, gear: 3));
        Assert.Equal(0f, pad.Left[0]);
        r.Update(Dt, true, true, Calm(40, 40, gear: 1));        // neutral: nothing
        Assert.Equal(0f, pad.Left[0]);
    }

    [Fact]
    public void ReadingTheCarDoesNotChangeIt()
    {
        var car = new CarObj { Impact = 0x1234, ImpactType = 0x50001, SurfaceType = 7, Gear = 4, Rpm = 5000 };
        car.LocalVel.Z = 30 * 65536;   // 30 m/s ≈ 67 mph
        var s = RumbleSample.From(car, 9);
        Assert.Equal(67, s.SpeedMph);
        Assert.Equal((0x1234, 0x50001, 7, 4, 5000, 9L), (s.Impact, s.ImpactType, s.SurfaceType, s.Gear, s.Rpm, s.Tick));
        Assert.Equal(0x1234, car.Impact);
    }

    [Fact]
    public void StopZeroesBothPads()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Wall(1), Wall(1));
        r.Update(Dt, true, true, Wall(2), Wall(2));
        r.Stop();
        Assert.Equal((0f, 0f, 0f, 0f), (pad.Left[0], pad.Right[0], pad.Left[1], pad.Right[1]));
    }

    [Fact]
    public void PortJsonRumbleDefaultsToOnAndRoundTrips()
    {
        Assert.True(new PortSettings().RumbleOn);
        var port = new PortSettings { Rumble = false };
        string path = Path.Combine(Path.GetTempPath(), "nfs2se-rumble-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(port.Save(path));
            Assert.Contains("\"rumble\": false", File.ReadAllText(path));
            Assert.False(PortSettings.Load(path).RumbleOn);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PortOptionsRowTogglesRumbleOnly()
    {
        var s = new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Enhanced, 0));
        while (s.SelectedOption != PortOption.Rumble) s.Handle(MenuKey.Down);
        Assert.Equal("ON", s.Value(PortOption.Rumble));
        var r = s.Handle(MenuKey.Right);
        Assert.True(r.RumbleChanged); Assert.False(r.Changed);
        Assert.Equal("OFF", s.Value(PortOption.Rumble));
        Assert.False(s.Rumble);
        Assert.Equal("OFF", new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Enhanced, 0), rumble: false).Value(PortOption.Rumble));
    }

    [Fact]
    public void PadBackOpensPortOptionsOnlyInTheRacePauseMenu()
    {
        Assert.True(PortOptionsScreen.PadToggles(true, false, false, racePaused: true, replayPlayback: false));
        Assert.False(PortOptionsScreen.PadToggles(true, true, false, true, false));    // held, not a new press
        Assert.False(PortOptionsScreen.PadToggles(true, false, true, true, false));    // Back+Start chord
        Assert.False(PortOptionsScreen.PadToggles(true, false, false, false, false));  // running race: Back = camera
        Assert.False(PortOptionsScreen.PadToggles(true, false, false, true, true));    // replay playback
        Assert.False(PortOptionsScreen.PadToggles(false, false, false, true, false));
    }

    [Fact]
    public void RumbleStaysZeroWhilePausedAndAfterLeavingTheRaceEvenWithTheOptionOn()
    {
        var (r, pad) = Make();
        r.Update(Dt, true, true, Calm(1, 60, surface: 7));
        Assert.True(pad.Left[0] > 0);
        for (int t = 0; t < 120; t++) r.Update(Dt, true, false, Calm(2 + t, 60, surface: 7));   // pause menu / PORT OPTIONS open
        Assert.Equal((0f, 0f), (pad.Left[0], pad.Right[0]));
        r.Update(Dt, true, true, Calm(200, 60, surface: 7));
        r.Stop();                                                                               // leaving the race
        Assert.Equal((0f, 0f), (pad.Left[0], pad.Right[0]));
    }
}
