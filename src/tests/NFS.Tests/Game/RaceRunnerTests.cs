using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Game;

public class RaceRunnerTests(ITestOutputHelper output)
{
    [GameFact]
    public void Idle_StaysOnGrid()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
        while (!run.RaceStarted) run.Step(default); // countdown (default) – the car starts to move at the green light
        int s0 = run.Car.Trk.Slice, y0 = run.Car.Pos.Y;
        for (int t = 0; t < 64; t++) run.Step(default);
        Assert.Equal(s0, run.Car.Trk.Slice);
        Assert.InRange(run.Car.Pos.Y - y0, -0x8000, 0x8000);
        Assert.Equal(0x200 + 128, run.Tick);
    }

    [GameFact]
    public void FullThrottle_DrivesAlongTheRoad()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
        while (!run.RaceStarted) run.Step(default); // countdown (default) – the car starts to move at the green light
        var c = run.Car;
        int start = c.Trk.Slice, n = run.Session.Slices.Length, onRoad = 0, maxSpeed = 0;
        const int ticks = 32 * 12;
        for (int t = 0; t < ticks; t++)
        {
            run.Step(new PlayerInput(0, 255, 0, 0));
            if (c.Trk.State == 2) onRoad++;
            maxSpeed = Math.Max(maxSpeed, c.SpeedApprox);
            int road = Newton.RoadHeightAtCar(c);
            Assert.InRange(c.Pos.Y - road, 0, 3 * Fx.One); // never under the road, no flying off
        }
        int moved = run.Session.Backward ? start - c.Trk.Slice : c.Trk.Slice - start; // in race direction
        int progress = (moved % n + n) % n;
        output.WriteLine($"slice {start} → {c.Trk.Slice} (+{progress}), gear {c.Gear}, v {c.Vel}, max speed {maxSpeed / (double)Fx.One:F1} m/s, " +
            $"on road {onRoad}/{ticks}, wall hits {run.WallHits}, edge hits {run.EdgeHits}");
        Assert.True(progress is > 30 and < 1000, $"progress {progress}");
        Assert.True(maxSpeed > 10 * Fx.One);
        Assert.True(onRoad > ticks * 9 / 10);
        Assert.True(run.EdgeHits > 0, "no steering: the car must meet the road edge and slide along it");
    }

    /// <summary>
    /// Countdown (ticks 0..0x1FF, = 0) before the green light: the car settles on the grid but does not [ref:3211]
    /// move, forces full brake and skips the gear change (the gear stays neutral, 1) and the race [ref:3212]
    /// only starts at tick 0x200. Data only – the bit-exact check against the original lives in the private trace tests.
    /// </summary>
    /// <summary>plays bank samples 0 → 1 → 2 → 3 (state 3/2/1/0) at ticks 0x140/0x180/0x1C0/0x200; [ref:3213]
    /// sample 3 is "GO". Regression: the port fired them in reverse (GO first).</summary>
    [GameFact]
    public void Countdown_SoundSamplesFollowOriginalOrder()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, countdown: true);
        var heard = new List<(int Tick, int Sample)>();
        run.OnCountdown += s => heard.Add((run.Tick, s));
        while (run.Tick <= 0x210) run.Step(default);
        Assert.Equal([(0x140, 0), (0x180, 1), (0x1C0, 2), (0x200, 3)], heard);
    }

    [GameFact]
    public void Countdown_HoldsCarUntilGreenLight()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, countdown: true);
        var c = run.Car;
        Assert.Equal(0, run.Tick);
        Assert.False(run.RaceStarted);
        int slice = c.Trk.Slice;
        int x0 = c.Pos.X, z0 = c.Pos.Z;
        int maxDrift = 0;
        while (!run.RaceStarted)
        {
            run.Step(new PlayerInput(0, 255, 0, 0));           // full throttle + 1st gear requested – must be ignored
            Assert.Equal(1, c.Gear);                            // neutral: no gear change before the start
            Assert.Equal(0xFF, c.BrakeRamped);                  // forced full brake
            Assert.Equal(slice, c.Trk.Slice);
            maxDrift = Math.Max(maxDrift, Math.Max(Math.Abs(c.Pos.X - x0), Math.Abs(c.Pos.Z - z0)));
        }
        Assert.Equal(0x200, run.Tick);
        output.WriteLine($"countdown stopped at tick 0x{run.Tick:X}, gear {c.Gear}, brake {c.BrakeRamped}, "
            + $"drift {maxDrift} ({maxDrift / (double)Fx.One:F4} m), rpm {c.Rpm}");
        Assert.True(maxDrift <= 0x10, $"car drifted {maxDrift} on the grid");
        // the green light releases the brake: with the same full-throttle input the car now gears up and moves
        for (int t = 0; t < 32; t++) run.Step(new PlayerInput(0, 255, 0, 0));
        Assert.True(run.RaceStarted);
        Assert.True(c.Gear >= 2, $"gear {c.Gear} after the start");
        Assert.True(c.Vel.Z > 0 || c.LocalVel.Z > 0, $"speed {c.LocalVel.Z} after the start");
    }

    /// <summary>
    /// Gearbox bytes of the freshly created cars (original's trace at tick 1: RequestedGear +0x2D6, PrevGear +0x2D9
    /// and Gear +0x2DA = 1 = neutral for all eight cars, shift counter 0). The AI cars start from the same neutral
    /// gear – downshifts them to its own index 0 on their first driving tick (original: [ref:3214]
    /// AI Gear 1 at tick 0x200, 0 at tick 0x201), while the player keeps shifting N → 1st later.
    /// Data only – the bit-exact check against the original lives in the private trace tests.
    /// </summary>
    [GameFact]
    public void FreshCars_StartInNeutral()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: null, withAi: true, countdown: true);
        Assert.Equal(8, run.Cars.Count);
        foreach (var car in run.Cars)
        {
            Assert.Equal(1, car.Obj.Gear);
            Assert.Equal(1, car.Obj.RequestedGear);
            Assert.Equal(1, car.Obj.PrevGear);
            Assert.Equal(0, car.Obj.GearChangeCounter);
        }
        var ai = run.Cars.First(c => c.Ai is { UseArcadeFallback: false });
        while (!run.RaceStarted) run.Step(default);
        run.Step(default);
        Assert.Equal(0, ai.Obj.Gear);   // the AI's own gear index on its first driving tick
    }

    /// <summary>Simple autopilot (not the game's AI): steer toward the slice heading in race direction and back to the
    /// centre line; the car must keep going along the track for a long distance.</summary>
    [GameFact]
    public void Autopilot_FollowsTrack()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
        while (!run.RaceStarted) run.Step(default); // countdown (default) – the car starts to move at the green light
        var c = run.Car;
        int n = run.Session.Slices.Length, start = c.Trk.Slice, dir = run.Session.Backward ? -1 : 1;
        const int ticks = 32 * 40;
        for (int t = 0; t < ticks; t++)
        {
            var sl = run.Session.Slices[c.Trk.Slice];
            int want = FxMath.Atan2(sl.Forward[0] * dir, sl.Forward[2] * dir);          // 0x400 = 360°
            int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
            int err = ((want - have + 0x200) & 0x3FF) - 0x200;
            int lat = c.LateralOffset * dir;                                              // + = right of centre in race direction
            int steer = Math.Clamp(err * 2 - lat / 0x4000, -127, 127);                   // input steer > 0 = right
            run.Step(new PlayerInput((sbyte)steer, 200, 0, 0));
        }
        int moved = dir * (c.Trk.Slice - start);
        int progress = (moved % n + n) % n;
        output.WriteLine($"autopilot: slice {start} → {c.Trk.Slice} (+{progress}), edge hits {run.EdgeHits}, speed {c.SpeedApprox / (double)Fx.One:F1}");
        Assert.True(progress > 200, $"progress {progress}");
        Assert.True(run.EdgeHits < 20, $"edge hits {run.EdgeHits}");
    }

    /// <summary>reads its three ramps from the car's own config record (car+0x21C = [idx] [ref:3215]
    /// +0x10/+0x14/+0x18). The shipped replay has a manual human car with 0/1/1 (steering snap, slow throttle step 0x10,
    /// brake ramp) and AI cars with 0/0/0 — the runner has to pass them through instead of hardcoding false/false/false.</summary>
    [GameFact]
    public void RaceConfig_DriveRamps_ReachTheCars()
    {
        var setup = TrackSessions.FreshReplay().Setup;
        var human = setup.Car(0);
        Assert.Equal(1, human.CarClass);
        Assert.False(human.SteerRamp);
        Assert.True(human.SlowThrottle, "replay human car +0x14");
        Assert.True(human.BrakeRamp, "replay human car +0x18");
        var ai = setup.Car(1);
        Assert.Equal(2, ai.CarClass);
        Assert.False(ai.SteerRamp || ai.SlowThrottle || ai.BrakeRamp, "replay AI car +0x10/+0x14/+0x18");

        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: null, withAi: true);
        Assert.Equal(new DriveOptions(false, false, true, true), run.Options);
        Assert.All(run.Cars.Where(c => !ReferenceEquals(c, run.PlayerSim)),
            c => Assert.False(c.Options.SteerRamp || c.Options.SlowThrottle || c.Options.BrakeRamp));

        // observable: full throttle for one 32 Hz tick ramps by 0x10 (SlowThrottle) instead of 0x18 — a 0x18 car
        // (SteerRamp off, so Steer snaps and only the throttle/brake ramps differ) is the control.
        var fast = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: null);
        fast.Car.Throttle = 0xF8;
        DriveControls.Filter(fast.Car, fast.Controls, fast.Options with { SlowThrottle = false }, true, RaceRunner.StartTick);
        Assert.Equal(0x18, fast.Car.ThrottleRamped);
        run.Car.Throttle = 0xF8;
        DriveControls.Filter(run.Car, run.Controls, run.Options, true, RaceRunner.StartTick);
        Assert.Equal(0x10, run.Car.ThrottleRamped);
    }
}
