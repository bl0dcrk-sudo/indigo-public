using System.Numerics;
using Microsoft.Xna.Framework.Input;
using NFS.Core;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class RaceCameraTests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    static CarObj Car() => new() { UpDotN = Fx.One };
    static RaceCamera Camera(int mode, int type = 0)
    {
        var c = new RaceCamera(type);
        while (c.Mode != mode) c.Cycle(Pose, Car());
        c.Snap(Pose, Car());
        return c;
    }
    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.00002f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, actual {actual}");

    [GameDataFact]
    public void NormalCycleMatchesOriginalInitializationTableAndWraps()
    {
        var c = Camera(0);
        foreach (int want in new[] { 1, 2, 3, 0, 1, 2, 3, 0 })
        {
            c.Cycle(Pose, Car());
            Assert.Equal(want, c.Mode);
        }
    }

    [GameDataFact]
    public void OrbitUsesOriginalRadiusHeightAndThreeSixtyFourHzUpdatesPerPhase()
    {
        RaceCamera Create()
        {
            var c = new RaceCamera(0) { OrbitEnabled = true };
            c.Cycle(Pose, Car());
            c.Cycle(Pose, Car(), raceTick: 0);
            return c;
        }
        var camera = Create();
        Near(Pose.Position + new Vector3(6, 2, 0), camera.Position);
        camera.Update(Pose, Car(), 1.0 / 60, raceTick: 2.99);
        Near(Pose.Position + new Vector3(6, 2, 0), camera.Position);
        camera.Update(Pose, Car(), 1.0 / 60, raceTick: 3);
        FxMath.SinCosTab(1, out int sin, out int cos);
        FxMath.SinCosTab(2 / 3, out int heightSin, out _);
        Near(Pose.Position + new Vector3(cos * 6 / 65536f, (2 * 65536 + heightSin) / 65536f,
            sin * 6 / 65536f), camera.Position);
        camera.Snap(Pose, Car(), raceTick: 3);
        camera.Update(Pose, Car(), 1.0 / 60, raceTick: 5.99);
        Near(Pose.Position + new Vector3(cos * 6 / 65536f, (2 * 65536 + heightSin) / 65536f,
            sin * 6 / 65536f), camera.Position);
        camera.Update(Pose, Car(), 1.0 / 60, raceTick: 6);
        Assert.NotEqual(Pose.Position + new Vector3(cos * 6 / 65536f, (2 * 65536 + heightSin) / 65536f,
            sin * 6 / 65536f), camera.Position);
    }

    [GameDataFact]
    public void OrbitPhaseDependsOnAuthoritativeTicksAndNotRenderFrameRate()
    {
        RaceCamera Create()
        {
            var c = new RaceCamera(0) { OrbitEnabled = true };
            c.Cycle(Pose, Car());
            c.Cycle(Pose, Car(), raceTick: 0);
            return c;
        }
        var at30 = Create(); var at144 = Create();
        for (int i = 1; i <= 30; i++) at30.Update(Pose, Car(), 1.0 / 30, raceTick: i * 0.4);
        for (int i = 1; i <= 144; i++) at144.Update(Pose, Car(), 1.0 / 144, raceTick: i / 12.0);
        Near(at30.Position, at144.Position, 1e-5f);
        Assert.Equal(4, at30.Mode);
    }

    [GameDataFact]
    public void OrbitRenderCanBeSavedForVisualReviewWhenRequested()
    {
        string? directory = Environment.GetEnvironmentVariable("NFS_ORBIT_PNG_DIR");
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        var drive = new RaceDrive(new RaceScene("replay"), countdown: false, orbitCameraActivation: true);
        var pose = drive.Scene.PresentPoses(1);
        drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
        drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
        var fb = new FrameBuffer();
        drive.Render(fb);
        fb.SavePng(Path.Combine(directory, "mode4-orbit.png"));
    }

    [GameDataFact]
    public void BonusCarsSkipDashboardLikeCamNextMode()
    {
        var c = Camera(3, 12);
        c.Cycle(Pose, Car());
        Assert.Equal(1, c.Mode);
    }

    [GameDataFact]
    public void EachViewHasOriginalLocalOffsetAndFov()
    {
        // 48/60/54; type0 near-chase Y correction = 0x4000. [ref:2876]
        var offsets = new[] { new Vector3(0, 0x4CCC / 65536f, .5f),
            new Vector3(0, 0x4CCC / 65536f, 0xE666 / 65536f),
            new Vector3(0, 2, -6), new Vector3(0, (0x10CCC + 0x4000) / 65536f, -0x4D999 / 65536f) };
        for (int mode = 0; mode < 4; mode++)
        {
            var c = Camera(mode);
            Near(Pose.Position + offsets[mode], c.Position);
            var view = c.Camera();
            Assert.Equal(320, view.Fx, 8);
            Assert.Equal(-240 / Math.Tan(36 * Math.PI / 180), view.Fy, 8);
            Assert.Equal(mode == 0 ? 200 : 240, view.Cy);
            Assert.Equal(c.Position, view.Position);
        }
    }

    [GameDataFact]
    public void OffsetsUseInterpolatedCarBasisOnRotatedAndBankedPose()
    {
        var pose = new RenderPose(new Vector3(-3, 12, 81), -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY);
        foreach (int mode in new[] { 0, 1, 2, 3 })
        {
            var c = Camera(mode);
            var local = c.Position - Pose.Position;
            c.Snap(pose, Car());
            Near(pose.Position + pose.Right * local.X + pose.Up * local.Y + pose.Forward * local.Z, c.Position);
            Assert.Equal(1, c.Forward.Length(), 5);
            Assert.Equal(1, c.Up.Length(), 5);
        }
    }

    [GameDataFact]
    public void FarChaseExtendsWithSpeedAndStopsAtSixMetres()
    {
        var c = Camera(2);
        var car = Car();
        car.LocalVel = new Vec3(8 * Fx.One, 0, 20 * Fx.One);
        c.Snap(Pose, car);
        Near(Pose.Position + new Vector3(0, 2, -7.1f), c.Position);
        car.LocalVel = new Vec3(-300 * Fx.One, 0, -200 * Fx.One);
        c.Snap(Pose, car);
        Near(Pose.Position + new Vector3(0, 2, -12), c.Position);
    }

    [GameDataFact]
    public void NearChaseHasCarSpecificHeight()
    {
        // [type]: type1=0, type4=0, type7=0x4000, type9=0x4CCC. [ref:2877]
        foreach (var (type, extra) in new[] { (1, 0), (4, 0), (7, 0x4000), (9, 0x4CCC) })
        {
            var c = Camera(3, type);
            Near(Pose.Position + new Vector3(0, (0x10CCC + extra) / 65536f, -0x4D999 / 65536f), c.Position);
        }
    }

    [GameDataFact]
    public void InCarHeightUsesRoadNormalDotAndFollowsWithoutLag()
    {
        foreach (int mode in new[] { 0, 1 })
        {
            var c = Camera(mode);
            var car = Car(); car.UpDotN = Fx.One / 2;
            var moved = Pose with { Position = Pose.Position + new Vector3(7, -2, 8) };
            c.Update(moved, car, 1.0 / 60);
            Near(moved.Position + new Vector3(0, Fx.Mul(0x4CCC, car.UpDotN) / 65536f,
                mode == 0 ? .5f : 0xE666 / 65536f), c.Position);
        }
    }

    [GameDataFact]
    public void ChaseTranslatesWithCarWhileItsRelativeOffsetIsSmoothed()
    {
        var c = Camera(2);
        var moved = Pose with { Position = Pose.Position + new Vector3(12, 3, -10) };
        c.Update(moved, Car(), 1.0 / 32);
        Near(moved.Position + new Vector3(0, 2, -6), c.Position);
        var car = Car(); car.LocalVel = new Vec3(0, 0, 20 * Fx.One);
        c.Update(moved, car, 1.0 / 32);
        Near(moved.Position + new Vector3(0, 2, -6 - 0x1999 / 65536f), c.Position);
        // After transition (50 updates), mode2 closes 0x3333 of the gap per 32Hz step.
        c.Update(moved, car, 50.0 / 32);
        var before = c.Position;
        car.LocalVel = new Vec3(0, 0, 40 * Fx.One);
        c.Update(moved, car, 1.0 / 32);
        float want = before.Z + ((moved.Position.Z - 8) - before.Z) * (0x3333 / 65536f);
        Assert.Equal(want, c.Position.Z, 4);
    }

    [GameDataFact]
    public void SwitchingSnapsToNewViewEvenWithZeroElapsedTime()
    {
        var c = Camera(2);
        var moved = Pose with { Position = Pose.Position + new Vector3(50, 0, 10) };
        c.Cycle(moved, Car());
        Assert.Equal(3, c.Mode);
        Near(moved.Position + new Vector3(0, (0x10CCC + 0x4000) / 65536f, -0x4D999 / 65536f), c.Position);
    }

    [GameDataFact]
    public void SmoothingUsesOriginalNearFractionAndIsIndependentOfRenderRate()
    {
        var rotated = Pose with { Right = -Vector3.UnitZ, Forward = Vector3.UnitX };
        var whole = Camera(3); var halves = Camera(3);
        var before = whole.Position - Pose.Position;
        whole.Update(rotated, Car(), 0);
        Assert.Equal(Pose.Position + before, whole.Position);
        whole.Update(rotated, Car(), 1.0 / 32);
        halves.Update(rotated, Car(), 1.0 / 64); halves.Update(rotated, Car(), 1.0 / 64);
        var target = new Vector3(-0x4D999 / 65536f, (0x10CCC + 0x4000) / 65536f, 0);
        Near(Pose.Position + Vector3.Lerp(before, target, 0x1999 / 65536f), whole.Position);
        Near(whole.Position, halves.Position);
    }

    [GameDataFact]
    public void InteriorPitchAndRollFollowOriginalAnglesAndBodyAnimation()
    {
        var anim = new CarAnim(0) { Pitch = 0x100, Roll = 0x200 };
        foreach (int mode in new[] { 0, 1 })
        {
            var camera = Camera(mode); camera.Snap(Pose, Car(), anim);
            float pitch = (mode == 0 ? 0x200 : 0x100 - 0x38E) * (2 * MathF.PI / 65536);
            float roll = (mode == 0 ? 0x200 : 0x100) * (2 * MathF.PI / 65536);
            Near(new Vector3(0, -MathF.Sin(pitch), MathF.Cos(pitch)), camera.Forward);
            Near(new Vector3(-MathF.Sin(roll), MathF.Cos(pitch) * MathF.Cos(roll), MathF.Sin(pitch) * MathF.Cos(roll)),
                camera.Up);
        }
    }

    [GameDataFact]
    public void EachViewRendersADifferentImageAndCyclingBackRestoresIt()
    {
        var d = new RaceDrive(new RaceScene("config"), countdown: false);
        d.RunAutopilot(4);
        var fb = new FrameBuffer();
        var images = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            d.Render(fb);
            images.Add((byte[])fb.Rgba.Clone());
            d.Frame(0, default(DriveKeys) with { CycleCamera = true }); d.Frame(0, default);
        }
        for (int a = 0; a < 4; a++) for (int b = 0; b < a; b++)
            Assert.False(images[a].SequenceEqual(images[b]));
        d.Render(fb);
        Assert.Equal(images[0], fb.Rgba);
    }

    [GameDataFact]
    public void PauseFreezesViewAndResumeDiscardsHeldCameraPress()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false);
        d.RunAutopilot(12);
        Assert.True(d.Pause());
        var position = d.Cam.Position; var forward = d.Cam.Forward;
        var keys = default(DriveKeys) with { CycleCamera = true };
        d.Frame(1, keys);
        Assert.Equal(2, d.Cam.Mode);
        Assert.Equal(position, d.Cam.Position); Assert.Equal(forward, d.Cam.Forward);
        d.Resume(keys); d.Frame(0, keys);
        Assert.Equal(2, d.Cam.Mode);
        d.Frame(0, default); d.Frame(0, keys);
        Assert.Equal(3, d.Cam.Mode);
    }

    [GameDataFact]
    public void CyclingViewsDoesNotChangeSimulation()
    {
        var a = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false);
        var b = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false);
        for (int i = 0; i < 128; i++)
        {
            var keys = default(DriveKeys) with { Up = true };
            a.Frame(1.0 / 32, keys);
            b.Frame(1.0 / 32, keys with { CycleCamera = (i & 1) != 0 });
        }
        for (int i = 0; i < a.Runner.Cars.Count; i++)
        {
            Assert.Equal(a.Runner.Cars[i].Obj.Pos, b.Runner.Cars[i].Obj.Pos);
            Assert.Equal(a.Runner.Cars[i].Obj.Vel, b.Runner.Cars[i].Obj.Vel);
            Assert.Equal(a.Runner.Cars[i].Obj.Orient, b.Runner.Cars[i].Obj.Orient);
        }
    }

    [Fact]
    public void OrbitActivationAddsModesFourAndFiveAndStillSkipsUnportedModes()
    {
        Assert.Equal(0, RaceCamera.NextMode(3, 0));
        Assert.Equal(4, RaceCamera.NextMode(3, 0, orbitEnabled: true));
        Assert.Equal(5, RaceCamera.NextMode(4, 0, orbitEnabled: true));
        Assert.Equal(4, RaceCamera.NextMode(3, 12, orbitEnabled: true));
        Assert.Equal(5, RaceCamera.NextMode(4, 12, orbitEnabled: true));
        Assert.Equal(4, RaceCamera.NextMode(3, 0, split: true, finished: true, orbitEnabled: true));
    }

    [GameDataFact]
    public void KeyboardAndPadCycleOncePerPressIncludingFramesWithoutSimStep()
    {
        foreach (bool pad in new[] { false, true })
        {
            var d = new RaceDrive(new RaceScene("replay"), countdown: false);
            var input = new InputMapping();
            DriveKeys Read(bool held) => input.Read(held && !pad ? new KeyboardState(Keys.C) : default,
                pad ? new GamePadState(Microsoft.Xna.Framework.Vector2.Zero, Microsoft.Xna.Framework.Vector2.Zero,
                    0, 0, held ? [Buttons.Back] : []) : default).Drive;
            Assert.Equal(0, d.Frame(.001, Read(true)));
            Assert.Equal(3, d.Cam.Mode);
            var drawn = d.Scene.Cars[d.Scene.PlayerInstance];
            int type = RaceScene.PlayerCar(d.Scene.Session.Cars)!.Setup.CarType;
            int extra = NFS.Core.Constants.CameraConstants.CarHeights[Math.Min(type, 9)];
            Near(drawn.Position + drawn.Up * ((0x10CCC + extra) / 65536f) + drawn.Forward * (-0x4D999 / 65536f),
                d.Cam.Position);
            for (int i = 0; i < 60; i++) d.Frame(1.0 / 60, Read(true));
            Assert.Equal(3, d.Cam.Mode);
            d.Frame(0, Read(false)); d.Frame(0, Read(true));
            Assert.Equal(0, d.Cam.Mode);
        }
    }
}
