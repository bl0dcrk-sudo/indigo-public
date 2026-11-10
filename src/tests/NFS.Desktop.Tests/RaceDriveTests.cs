using System.Numerics;
using NFS.Core;
using NFS.Desktop;
using NFS.Game.Race;
using Xunit;

namespace NFS.Desktop.Tests;

public class RaceDriveTests
{
    [GameDataFact]
    public void LocalPresentationUsesNewestCompletedPoseWithoutOneTickInterpolationDelay()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 24; i++) d.Frame(1.0 / 32, gas);
        var initial = d.PlayerView;
        var driveRight = new DriveKeys(true, false, false, true, false, false, false);

        d.Frame(1.5 / 32, driveRight); // one steering step, with half a step left accumulated

        var previous = RenderPose.From(initial.Pos, initial.Orient);
        var expected = RenderPose.Interpolate(previous, RenderPose.From(d.PlayerView.Pos, d.PlayerView.Orient), 1.5);
        var shown = d.Scene.Cars[d.Scene.PlayerInstance];
        Assert.NotEqual(initial.Pos, d.PlayerView.Pos);
        Assert.NotEqual(initial.Orient, d.PlayerView.Orient);
        Assert.Equal(expected.Position, shown.Position);
        Assert.Equal(expected.Forward, shown.Forward);
        Assert.NotEqual(RaceScene.ToWorld(d.PlayerView.Pos), shown.Position); // no zero-order hold at the newest tick
    }

    [GameDataFact]
    public void ManualConfiguration_ChangesGearOnlyOnPressedKey_AndDoesNotAutoShift()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false);
        Assert.False(d.Runner.Automatic); // shipped replay header: manual, constructor must respect it
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 32; i++) d.Frame(1.0 / 32, gas);
        Assert.Equal(1, d.Car.Gear); // N: throttle alone cannot select first
        d.Frame(1.0 / 32, gas with { GearUp = true });
        Assert.Equal(2, d.Car.Gear);
        for (int i = 0; i < 64; i++) d.Frame(1.0 / 32, gas with { GearUp = true });
        Assert.Equal(2, d.Car.Gear); // holding A is one press, high rpm never invokes automatic gearbox
        d.Frame(1.0 / 32, gas);
        d.Frame(1.0 / 32, gas with { GearUp = true });
        Assert.Equal(3, d.Car.Gear);
        d.Frame(1.0 / 32, gas with { GearDown = true });
        Assert.Equal(2, d.Car.Gear);
    }

    [GameDataFact]
    public void DriveDelegatesToHost_SameStateAsBareRunnerWithSameKeys_AndPauseFreezesIt()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: true, automatic: true);
        var bare = new RaceRunner(new RaceScene("replay").Session, 0, automatic: true, withAi: true);
        var kb = new KeyboardDriver();
        Assert.Same(d.Host.Runner, d.Runner);
        for (int i = 0; i < 700; i++)
        {
            var keys = new DriveKeys(true, i % 90 > 80, i % 60 < 20, i % 60 > 40, false, false, false);
            Assert.Equal(1, d.Frame(1.0 / 32, keys));
            bare.Step(kb.Next(keys));
        }
        Assert.Equal(700, d.Steps);
        Assert.Equal(bare.Tick, d.Runner.Tick);
        for (int i = 0; i < bare.Cars.Count; i++)
        {
            Assert.Equal(bare.Cars[i].Obj.Pos, d.Runner.Cars[i].Obj.Pos);
            Assert.Equal(bare.Cars[i].Obj.Vel, d.Runner.Cars[i].Obj.Vel);
            Assert.Equal(bare.Cars[i].Obj.Orient, d.Runner.Cars[i].Obj.Orient);
        }
        Assert.Equal(700, d.Recorder.Samples);
        Assert.True(d.Pause());
        Assert.True(d.Host.Paused);
        var pos = d.Car.Pos;
        Assert.Equal(0, d.Frame(1.0 / 32, default));
        Assert.Equal((700, 700, pos), (d.Steps, d.Recorder.Samples, d.Car.Pos));
        d.Resume();
        Assert.Equal(1, d.Frame(1.0 / 32, default));
        Assert.Equal(701, d.Steps);
    }

    [Fact]
    public void Keyboard_HandbrakeAndHornRemainActiveWhileHeld()
    {
        var keys = new KeyboardDriver();
        var k = new DriveKeys(false, false, false, false, false, false, false, true, true);
        Assert.Equal(PlayerInput.BtnHorn | PlayerInput.BtnHandbrake, keys.Next(k).Buttons);
        Assert.Equal(PlayerInput.BtnHorn | PlayerInput.BtnHandbrake, keys.Next(k).Buttons);
        Assert.Equal(0, keys.Next(default).Buttons);
    }

    [GameDataFact]
    public void FinishUsesCarObjForHud_ShowsResultsAndFreezesSimulation()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        d.Runner.SetLaps(2);
        for (int i = 0; i < 100; i++) d.Step(default);
        d.Car.Lap = 1;
        d.Step(default);
        Assert.Equal(2, d.Hud().Lap);
        Assert.Equal(d.Car.RacePosition, d.Hud().Place);
        for (int i = 0; i < 50; i++) d.Step(default);
        d.Car.Lap = 2;
        d.Step(default);
        Assert.Equal(2, d.Car.RaceState);
        Assert.Equal(d.Car.LapStartTick, d.Hud().RaceTicks64);
        Assert.Null(d.Results);
        while (!d.Runner.Laps.RaceOver) d.Step(default);
        var row = Assert.Single(d.Results!.Racers, r => r.Human);
        Assert.Equal(d.Car.RacePosition, row.Place);
        Assert.Equal([200, 102], row.LapTicks);
        Assert.Equal(102, row.BestLapTicks);
        Assert.False(row.Estimated);
        Assert.Equal(d.Car.LapStartTick, row.RaceTicks);
        int tick = d.Runner.Tick, steps = d.Steps;
        var pose = d.Car.Pos;
        d.Step(new PlayerInput(0, 255, 0, PlayerInput.BtnReset));
        Assert.Equal(tick, d.Runner.Tick);
        Assert.Equal(steps, d.Steps);
        Assert.Equal(pose, d.Car.Pos);
        Assert.Equal(0, d.Resets);
        var fb = new NFS.Render.FrameBuffer();
        d.Render(fb); // CPU renderer only: no MonoGame device/window.
        var background = new NFS.Render.FrameBuffer();
        d.Scene.ResultScreen.Background(background);
        Assert.Equal(background.Get(0, 0), fb.Get(0, 0));
        Assert.Contains((byte)(255 * 15 >> 4), fb.Rgba.Where((_, i) => i % 4 == 0)); // full glyph pixel = ramp entry 15 [ref:2879]
    }

    [Fact]
    public void KeyboardDriver_MapsKeys()
    {
        var k = new KeyboardDriver();
        Assert.Equal(new PlayerInput(0, 0, 0, 0), k.Next(default));
        Assert.Equal(new PlayerInput(0, 255, 0, 0), k.Next(new DriveKeys(Up: true, false, false, false, false, false, false)));
        Assert.Equal(new PlayerInput(0, 0, 255, 0), k.Next(new DriveKeys(false, Down: true, false, false, false, false, false)));
        Assert.Equal(new PlayerInput(-127, 0, 0, 0), k.Next(new DriveKeys(false, false, Left: true, false, false, false, false)));
        Assert.Equal(new PlayerInput(127, 255, 0, 0), k.Next(new DriveKeys(true, false, false, Right: true, false, false, false)));
        Assert.Equal(0, k.Next(new DriveKeys(false, false, true, true, false, false, false)).Steer); // both = straight
    }

    [GameDataFact]
    public void HorizonKey_FlipsTheSkyFlagOnTheEdge_AndLeavesTheSimulationAlone()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false);
        Assert.True(d.HorizonDisplay);   // GameLoop supplies CONFIG.DAT +0x132 [ref:2880]
        var held = default(DriveKeys) with { HorizonToggle = true };
        d.Frame(1.0 / 32, held);
        Assert.False(d.HorizonDisplay);  // = XOR of the bit [ref:2881]
        for (int i = 0; i < 8; i++) d.Frame(1.0 / 32, held);
        Assert.False(d.HorizonDisplay);  // holding the key does not toggle again ((0xE,1) = edge) [ref:2882]
        d.Frame(1.0 / 32, default);
        d.Frame(1.0 / 32, held);
        Assert.True(d.HorizonDisplay);
        // Presentation only draws: the simulation with and without the key is identical.
        var a = new RaceDrive(new RaceScene("replay"), countdown: false);
        var b = new RaceDrive(new RaceScene("replay"), countdown: false);
        for (int i = 0; i < 64; i++) { a.Frame(1.0 / 32, default); b.Frame(1.0 / 32, held); }
        Assert.Equal(a.Runner.Tick, b.Runner.Tick);
        Assert.Equal(a.Car.Pos, b.Car.Pos);
        Assert.Equal(a.Car.SpeedApprox, b.Car.SpeedApprox);
        Assert.False(b.HorizonDisplay);
    }

    [Fact]
    public void KeyboardDriver_ButtonsAreEdgeTriggered()
    {
        var k = new KeyboardDriver();
        var up = new DriveKeys(false, false, false, false, GearUp: true, false, false);
        Assert.Equal(PlayerInput.BtnGearUp, k.Next(up).Buttons);
        Assert.Equal(0, k.Next(up).Buttons);            // held → no repeat
        Assert.Equal(0, k.Next(default).Buttons);
        Assert.Equal(PlayerInput.BtnGearUp, k.Next(up).Buttons);
        Assert.Equal(PlayerInput.BtnGearDown, k.Next(new DriveKeys(false, false, false, false, false, GearDown: true, false)).Buttons);
        Assert.Equal(PlayerInput.BtnReset, k.Next(new DriveKeys(false, false, false, false, false, false, Reset: true)).Buttons);
    }

    [Theory]
    [InlineData(1.0 / 60, new[] { 0, 1, 0, 1, 0, 1 })]    // 60 FPS → 32 Hz: steps alternate (accumulated)
    [InlineData(1.0 / 32, new[] { 1, 1, 1, 1 })]
    [InlineData(0.1, new[] { 3, 3, 3, 3 })]               // 3.2 steps per frame: remainder carried
    public void FixedStepClock_Accumulates(double dt, int[] expected)
    {
        var c = new FixedStepClock(32);
        var got = expected.Select(_ => c.Advance(dt)).ToArray();
        Assert.Equal(expected, got);
    }

    [Fact]
    public void FixedStepClock_TotalMatchesTimeAndCaps()
    {
        var c = new FixedStepClock(32);
        int total = 0;
        for (int i = 0; i < 600; i++) total += c.Advance(1.0 / 60); // 10 s
        Assert.InRange(total, 319, 320);
        var c2 = new FixedStepClock(32, maxSteps: 8);
        Assert.Equal(8, c2.Advance(5.0));    // long stall: capped, backlog dropped
        Assert.Equal(0, c2.Advance(0));
        Assert.Equal(0, c2.Advance(-1));
    }

    [Fact]
    public void ChaseCam_SnapsBehindAndSmoothsToward()
    {
        var o = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One); // forward +Z
        var cam = new ChaseCam();
        cam.Snap(new Vec3(0, 0, 0), o);
        Assert.Equal(new Vector3(0, 2, -6), cam.Position);
        Assert.Equal(Vector3.UnitZ, cam.Forward);

        cam.Update(new Vec3(0, 0, 10 * Fx.One), o, 1.0 / 32);       // car jumps 10 m ahead
        Assert.InRange(cam.Position.Z, -5.9f, 3.9f);                 // moved part of the way, not all
        for (int i = 0; i < 200; i++) cam.Update(new Vec3(0, 0, 10 * Fx.One), o, 1.0 / 32);
        Assert.Equal(4f, cam.Position.Z, 3);                         // converged: 6 m behind
        Assert.Equal(2f, cam.Position.Y, 3);
        var c = cam.Camera();
        Assert.Equal(cam.Position, c.Position);
    }

    [Fact]
    public void ChaseCam_ZeroDtKeepsPosition()
    {
        var o = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One);
        var cam = new ChaseCam();
        cam.Snap(Vec3.Zero, o);
        cam.Update(new Vec3(50 * Fx.One, 0, 0), o, 0);
        Assert.Equal(new Vector3(0, 2, -6), cam.Position);
    }

    [Fact]
    public void Options_ParseDrive()
    {
        var o = DesktopOptions.Parse(["--race", "replay", "--autotest", "out", "--drive-autopilot", "--seconds", "12.5"]);
        Assert.True(o.DriveAutopilot);
        Assert.Equal(12.5, o.Seconds);
        Assert.False(DesktopOptions.Parse([]).DriveAutopilot);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--seconds", "x"]));
        Assert.Contains("--drive-autopilot", DesktopOptions.Usage);
    }

    static string Fingerprint(RaceDrive d) =>
        $"{d.Car.Pos} {d.Car.Vel} {d.Car.Orient.Row2} {d.Car.Trk.Slice} {d.Runner.Tick} {d.Runner.EdgeHits} {d.Runner.WallHits}";

    [GameDataFact]
    public void Drive_DefaultStartsWithCountdown_CarsHoldUntilGreenLight()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true);
        Assert.True(d.Runner.Countdown);
        Assert.False(d.Runner.RaceStarted);
        var gas = new PlayerInput(0, 255, 0, 0);
        while (!d.Runner.RaceStarted) d.Step(gas);
        Assert.Equal(RaceRunner.StartTick, d.Runner.Tick);
        Assert.Equal(0, d.Hud().RaceTicks64);
        Assert.True(d.Car.SpeedApprox < 2 * Fx.One, "held on the grid by the full brake until the light");
    }

    [GameDataFact]
    public void Drive_AutopilotIsDeterministicAndMoves()
    {
        var a = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
        var b = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
        a.RunAutopilot(5);
        b.RunAutopilot(5);
        Assert.Equal(160, a.Steps);
        Assert.Equal(Fingerprint(a), Fingerprint(b));
        Assert.True(a.Progress > 10, a.Summary());
        var inst = a.Scene.Cars[a.Scene.PlayerInstance];       // render instance follows the car
        Assert.Equal(RaceScene.ToWorld(a.Car.Pos), inst.Position);
    }

    [GameDataFact]
    public void Drive_KeyboardFramesAndReset()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
        var gas = new DriveKeys(Up: true, false, false, false, false, false, false);
        int steps = 0;
        for (int i = 0; i < 120; i++) steps += d.Frame(1.0 / 60, gas); // 2 s at 60 FPS
        Assert.InRange(steps, 63, 64);
        Assert.True(d.Car.SpeedApprox > 5 * Fx.One, d.Summary());
        d.Frame(1.0 / 32, new DriveKeys(false, false, false, false, false, false, Reset: true));
        Assert.Equal(1, d.Resets);
        Assert.True(d.Car.SpeedApprox < 2 * Fx.One, d.Summary());
    }

    [GameDataFact]
    public void Drive_WithAi_AllRenderInstancesFollowTheirCars()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        d.RunAutopilot(3);
        Assert.Equal(d.Scene.Session.Cars.Count, d.Runner.Cars.Count);
        Assert.Contains("AI progress", d.Summary());
        int moved = 0;
        foreach (var car in d.Runner.Cars.Skip(1))
        {
            var p = RaceScene.ToWorld(car.Obj.Pos);
            Assert.Contains(d.Scene.Cars, inst => inst.Position == p);
            if (d.Runner.Progress(car) > 5) moved++;
        }
        Assert.Equal(d.Runner.Cars.Count - 1, moved);
    }
}
