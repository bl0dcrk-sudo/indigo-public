using System.Numerics;
using NFS.Core;

namespace NFS.Desktop.Tests;

public class Mode4ReviewRegressionTests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    static CarObj Car() => new() { UpDotN = Fx.One };
    static RaceCamera Orbit(CarObj car)
    {
        var camera = new RaceCamera(0) { OrbitEnabled = true };
        camera.Cycle(Pose, car, raceTick: 0);
        camera.Cycle(Pose, car, raceTick: 0);
        return camera;
    }
    static Vector3 Offset(int phase)
    {
        FxMath.SinCosTab(phase, out int sin, out int cos);
        FxMath.SinCosTab(phase * 2 / 3, out int height, out _);
        return new Vector3(cos * 6 / 65536f, 2 + height / 65536f, sin * 6 / 65536f);
    }
    static void Near(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < 0.0001f, $"expected {expected}, actual {actual}");

    [GameDataFact]
    public void RearPressHoldAndReleasePreserveOrbitPositionAndDirection()
    {
        var car = Car(); var camera = Orbit(car);
        camera.Update(Pose, car, 7.0 / 64, raceTick: 7);
        var position = camera.Position; var forward = camera.Forward; var up = camera.Up;
        foreach (bool held in new[] { true, true, false })
        {
            camera.SetLookBehind(held, Pose, car);
            Near(position, camera.Position); Near(forward, camera.Forward); Near(up, camera.Up);
            camera.Update(Pose, car, 1.0 / 64, raceTick: 7);
            Near(position, camera.Position); Near(forward, camera.Forward); Near(up, camera.Up);
        }
    }

    [GameDataFact]
    public void SustainedTiltRefreshesRecoveryTimerAndOnlyCapturesBackwardsOnEntry()
    {
        var car = Car(); var camera = Orbit(car);
        car.UpDotN = 0;
        for (int tick = 1; tick <= 150; tick++)
        {
            car.WrongWayTicks = tick > 1 && tick < 150 ? 1 : 0;
            camera.Update(Pose, car, 1.0 / 64, raceTick: tick);
            Assert.True(camera.Flipping);
        }
        car.UpDotN = Fx.One;
        for (int tick = 151; tick <= 248; tick++) camera.Update(Pose, car, 1.0 / 64, raceTick: tick);
        Assert.True(camera.Flipping);
        camera.Update(Pose, car, 1.0 / 64, raceTick: 249);
        Assert.False(camera.Flipping);
        camera.Update(Pose, car, 1.0 / 64, raceTick: 250);
        Near(Pose.Position + Offset(0), camera.Position); // fallback time never advances the orbit.
    }

    [GameDataFact]
    public void ReentryPreservesPhaseAndRemainderWithoutAccruingOtherModeTime()
    {
        var car = Car(); var camera = Orbit(car);
        camera.Update(Pose, car, 7.0 / 64, raceTick: 7);
        Near(Pose.Position + Offset(2), camera.Position);
        camera.Cycle(Pose, car, raceTick: 7);
        camera.Update(Pose, car, 90.0 / 64, raceTick: 97);
        // 5 → 6 → 7 → 0 → 1 → 2 → 3 → 4.
        for (int i = 0; i < 7; i++) camera.Cycle(Pose, car, raceTick: 100);
        Assert.Equal(4, camera.Mode);
        Near(Pose.Position + Offset(2), camera.Position);
        camera.Update(Pose, car, 2.0 / 64, raceTick: 102);
        Near(Pose.Position + Offset(3), camera.Position);
    }

    [GameDataFact]
    public void SnapConsumesElapsedOrbitTimeAndPausesDuringResetFallback()
    {
        var car = Car(); var camera = Orbit(car);
        camera.Update(Pose, car, 7.0 / 64, raceTick: 7);
        camera.Snap(Pose, car, raceTick: 10);
        Near(Pose.Position + Offset(3), camera.Position);
        car.ManualResetLock = 10;
        camera.Snap(Pose, car, raceTick: 100);
        car.ManualResetLock = 0;
        camera.Snap(Pose, car, raceTick: 101);
        Near(Pose.Position + Offset(3), camera.Position);
        camera.Snap(Pose, car, raceTick: 102);
        Near(Pose.Position + Offset(4), camera.Position);
    }

    [GameDataFact]
    public void UprightFrameAndRunAutopilotAdvanceTheSameOrbitPhase()
    {
        RaceDrive Drive()
        {
            var drive = new RaceDrive(new RaceScene("replay"), countdown: false, orbitCameraActivation: true);
            drive.RunAutopilot(1); // let the grid car settle before measuring an upright orbit interval.
            drive.Cam.Locator = null;
            var pose = RenderPose.From(drive.Car.Pos, drive.Car.Orient);
            drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
            drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
            return drive;
        }
        var normal = Drive(); var autopilot = Drive();
        for (int i = 0; i < 8; i++) normal.Frame(1.0 / 32, default);
        autopilot.RunAutopilot(.25);
        Assert.Equal(normal.Runner.Tick, autopilot.Runner.Tick);
        Assert.False(normal.Cam.Flipping); Assert.False(autopilot.Cam.Flipping);
        void Check(RaceDrive drive, RenderPose pose)
        {
            Assert.Equal(4, drive.Cam.Mode);
            Assert.Equal(0, drive.Car.CrashSlice);
            Assert.Equal(0, drive.Car.ManualResetLock);
            Assert.True(drive.Car.UpDotN >= 0xE666, $"UpDotN={drive.Car.UpDotN}");
            var expected = Offset(16 / 3);
            Near(pose.Position + pose.Right * expected.X + pose.Up * expected.Y + pose.Forward * expected.Z, drive.Cam.Position);
        }
        Check(normal, normal.Scene.PresentPoses(normal.Clock.Accumulator / normal.Clock.Step));
        Check(autopilot, RenderPose.From(autopilot.Car.Pos, autopilot.Car.Orient));
    }

    [GameDataFact]
    public void FinishedSplitAllowsOrbitWithoutStartupChordWhileNormalTableRequiresIt()
    {
        Assert.Equal(0, RaceCamera.NextMode(3, 0));
        Assert.Equal(1, RaceCamera.NextMode(3, 0, split: true));
        Assert.Equal(4, RaceCamera.NextMode(3, 0, orbitEnabled: true));
        Assert.Equal(4, RaceCamera.NextMode(3, 0, split: true, finished: true));
        Assert.Equal(5, RaceCamera.NextMode(4, 0, split: true, finished: true));
    }
}
