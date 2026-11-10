using System.Numerics;
using NFS.Core;
using NFS.Data.Track;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Finish-line camera, camera for an inverted car, crash and reset. [ref:2867]</summary>
public class RaceCameraSpecialTests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    static CarObj Car() => new()
    {
        UpDotN = Fx.One, Flags = CarFlags.LocalHuman,
        Orient = Mat3.Identity,
        RoadFrame = Mat3.Identity,
    };

    /// <summary>Target of an inverted car: camera offset (<c>CameraConstants.ChaseOffset</c>) in the road frame; [ref:2868]
    /// with the opposite sign when inverted relative to the track direction.</summary>
    static Vector3 FlipTarget(CarObj car, bool negateZ)
    {
        var o = NFS.Core.Constants.CameraConstants.ChaseOffset;
        int x = o[0], y = o[1], z = o[2];
        if (negateZ) z = -z;
        var r = car.RoadFrame;
        return RaceScene.ToWorld(r.Row0) * (x / 65536f) + RaceScene.ToWorld(r.Row1) * (y / 65536f) + RaceScene.ToWorld(r.Row2) * (z / 65536f);
    }

    static RaceCamera Camera(int mode, int type = 0)
    {
        var c = new RaceCamera(type);
        while (c.Mode != mode) c.Cycle(Pose, Car());
        c.Snap(Pose, Car());
        return c;
    }

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.0002f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, actual {actual}");

    static Vector3 World(int x, int y, int z) => RaceScene.ToWorld(new Vec3(x, y, z));

    static Can FinishCan(bool reverse = false) => Can.Load($"GAMEDATA/TRACKS/DATA/PC/TR00{(reverse ? 6 : 1):00}.CAN");

    /// <summary>flag 1: new row 1 = old row 2, new row 2 = −old row 1. [ref:2869]</summary>
    static (Vector3 Forward, Vector3 Up) CanBasis(CanKey k)
    {
        var m = KnockedObjectPose.QuatToMatrix(k.Qx, k.Qy, k.Qz, k.Qw);
        return (Vector3.Normalize(World(-m[3], -m[4], -m[5])), Vector3.Normalize(World(m[6], m[7], m[8])));
    }

    // ---------------------------------------------------------------- finish line [ref:2870]

    [GameDataFact]
    public void FinishingSwitchesToFinishModeAndStartsAtFirstCanKey()
    {
        var can = FinishCan();
        var c = Camera(2);
        c.FinishAnimation = can;
        var car = Car();
        c.Update(Pose, car, 1.0 / 32);
        Assert.NotEqual(RaceCamera.FinishMode, c.Mode);

        car.RaceState = 2;
        c.Update(Pose, car, 0.001);
        Assert.Equal(RaceCamera.FinishMode, c.Mode);
        Assert.False(c.InCar);
        var k0 = can.Keys[0];
        // t ≈ 0: car position + CAN key 0 position, orientation from the key (axes swapped).
        Near(Pose.Position + World(k0.X, k0.Y, k0.Z), c.Position, 0.01f);
        var (forward, up) = CanBasis(k0);
        Near(forward, c.Forward, 0.01f);
        Near(up, c.Up, 0.01f);
    }

    [GameDataFact]
    public void FinishCameraFollowsCanKeysThenHoldsTheLastKey()
    {
        var can = FinishCan();
        var c = Camera(3);
        c.FinishAnimation = can;
        var car = Car(); car.RaceState = 2;
        c.Update(Pose, car, 1.0 / 32);
        // half way through the first key (ticks-per-key from the file): linear blend of keys 0 and 1.
        int tpk = can.TicksPerKey;
        c.Update(Pose, car, (tpk / 2.0 - 1) / 32);
        var (a, b) = (can.Keys[0], can.Keys[1]);
        Near(Pose.Position + World((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2), c.Position, 0.02f);
        // The car moves on: the camera keeps the CAN offset relative to it.
        var moved = Pose with { Position = Pose.Position + new Vector3(40, 0, 5) };
        var before = c.Position;
        c.Update(moved, car, 1.0 / 32 / 1000);
        Near(before + new Vector3(40, 0, 5), c.Position, 0.05f);
        // After the animation (nKeys−1 segments) the last key stays forever.
        c.Update(moved, car, (can.Keys.Count * tpk + 100) / 32.0);
        var last = can.Keys[^1];
        Near(moved.Position + World(last.X, last.Y, last.Z), c.Position, 0.001f);
        var (forward, up) = CanBasis(last);
        Near(forward, c.Forward, 0.001f);
        Near(up, c.Up, 0.001f);
        c.Update(moved, car, 10);
        Near(moved.Position + World(last.X, last.Y, last.Z), c.Position, 0.001f);
    }

    [GameDataFact]
    public void FinishModeStartsOnlyOnceForTheLocalPlayerAndCycleLeavesIt()
    {
        var can = FinishCan();
        var c = Camera(2);
        c.FinishAnimation = can;
        var ai = Car(); ai.Flags = 0; ai.RaceState = 2;
        c.Update(Pose, ai, 1.0 / 32);
        Assert.Equal(2, c.Mode); // not a local human car

        var car = Car(); car.RaceState = 2;
        c.Update(Pose, car, 1.0 / 32);
        Assert.Equal(RaceCamera.FinishMode, c.Mode);
        c.Cycle(Pose, car);
        Assert.Equal(6, c.Mode); // table 0xC → 6 (camera along the track) [ref:2871]
        c.Update(Pose, car, 1.0 / 32);
        Assert.NotEqual(RaceCamera.FinishMode, c.Mode); // flag +0x30CC is never cleared
    }

    [GameDataFact]
    public void WithoutCanFileTheCameraStaysInTheDrivingMode()
    {
        var c = Camera(2);
        var car = Car(); car.RaceState = 2;
        c.Update(Pose, car, 1.0 / 32);
        Assert.Equal(2, c.Mode);
    }

    [GameDataTheory]
    [InlineData(2)]
    [InlineData(3)]
    public void TiltedCarMakesChaseCameraEaseToTheRoadFrameViewpoint(int mode)
    {
        var c = Camera(mode);
        var car = Car(); car.UpDotN = 0x8000;
        var start = c.Position - Pose.Position;
        var target = FlipTarget(car, false);
        c.Update(Pose, car, 1.0 / 32);
        Assert.True(c.Flipping);
        // one tick: 0x51E/65536 of the way (fixed 32 Hz rate, no slow phase).
        Near(Pose.Position + Vector3.Lerp(start, target, 0x51E / 65536f), c.Position, 0.001f);
        for (int i = 0; i < 500; i++) c.Update(Pose, car, 1.0 / 32);
        Near(Pose.Position + target, c.Position, 0.01f);
    }

    [GameDataFact]
    public void FlipCameraNeverFliesThroughTheCarAndLooksAtIt()
    {
        var c = Camera(2);
        var car = Car(); car.UpDotN = 0x4000;
        float min = float.MaxValue, max = 0;
        for (int i = 0; i < 300; i++)
        {
            c.Update(Pose, car, 1.0 / 60);
            float d = Vector3.Distance(c.Position, Pose.Position);
            min = Math.Min(min, d); max = Math.Max(max, d);
            Near(Vector3.Normalize(Pose.Position - c.Position), c.Forward, 0.01f); // + 0x2A pitch
        }
        Assert.True(min > 2, $"camera came within {min} m of the car");
        Assert.True(max < 40, $"camera flew away to {max} m");
    }

    [GameDataFact]
    public void ChaseCameraKeepsTheFlipViewFor100TicksAfterTheCarRecovers()
    {
        var c = Camera(2);
        var car = Car(); car.UpDotN = 0x8000;
        c.Update(Pose, car, 1.0 / 32);
        car.UpDotN = Fx.One;
        for (int i = 0; i < 90; i++) c.Update(Pose, car, 1.0 / 32);
        Assert.True(c.Flipping);
        for (int i = 0; i < 12; i++) c.Update(Pose, car, 1.0 / 32);
        Assert.False(c.Flipping);
        // then the ordinary far chase takes over again and converges to its offset.
        for (int i = 0; i < 300; i++) c.Update(Pose, car, 1.0 / 32);
        Near(Pose.Position + new Vector3(0, 2, -6), c.Position, 0.01f);
    }

    [GameDataFact]
    public void FlipAgainstTheTrackDirectionNegatesZAndBackwardsCarReleasedCutsBehindIt()
    {
        var c = Camera(2);
        var car = Car(); car.UpDotN = 0x8000; car.WrongWayTicks = 1;
        c.TrackReverse = false;
        c.Update(Pose, car, 1.0 / 32);
        // flag (+0x30E0 = WrongWayTicks>0) set → z negated. [ref:2872]
        for (int i = 0; i < 60; i++) c.Update(Pose, car, 1.0 / 32);
        var want = FlipTarget(car, true);
        Assert.True(Vector3.Distance(c.Position - Pose.Position, want) < Vector3.Distance(c.Position - Pose.Position, FlipTarget(car, false)));
        // WrongWayTicks back to 0: camera 8 units behind the heading, 1 up, and the counter ends.
        car.WrongWayTicks = 0;
        car.Orient = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0x8000, 0, 0x8000);
        c.Update(Pose, car, 1.0 / 32);
        Assert.False(c.Flipping);
        Near(Pose.Position + new Vector3(-4, 1, -4), c.Position, 0.001f);
    }

    [GameDataFact]
    public void CockpitViewsIgnoreTheFlipCamera()
    {
        var c = Camera(1);
        var car = Car(); car.UpDotN = 0x4000;
        c.Update(Pose, car, 1.0 / 32);
        Assert.False(c.Flipping);
        Assert.True(c.InCar);
    }

    // ---------------------------------------------------------------- crash [ref:2873]

    static TrackSlice Slice(int x, int y, int z, ushort left, ushort right) => new(x, y, z,
        [0, 127, 0], [0, 0, 127], [127, 0, 0], 0, 0, 0, left, right, 0, 0, 0, 0, 0, 0);

    [GameDataFact]
    public void CrashCameraStaysPutAndDriftsTowardsTheCarLookingAtIt()
    {
        var c = Camera(0);
        var car = Car(); car.CrashSlice = 7;
        var from = c.Position;
        var pose = Pose with { Position = Pose.Position + new Vector3(20, -10, 0) };
        c.Update(pose, car, 1.0 / 32);
        Assert.True(c.Crashing);
        Assert.False(c.InCar);
        Assert.Equal(9, c.DisplayMode);
        // one tick: 0xCCC/65536 of the way, y aims 4 m above the car.
        float f = 0xCCC / 65536f;
        Near(from + (pose.Position - from + new Vector3(0, 4, 0)) * f, c.Position, 0.001f);
        Near(Vector3.Normalize(pose.Position - c.Position), c.Forward, 0.001f);
        var one = c.Position;
        c.Update(pose, car, 1.0 / 32);
        Assert.True(Vector3.Distance(c.Position, pose.Position) < Vector3.Distance(one, pose.Position));
        // the camera lags behind a car that keeps moving (does not follow rigidly).
        for (int i = 0; i < 10; i++) c.Update(pose with { Position = pose.Position + new Vector3(0, 0, 5 * (i + 1)) }, car, 1.0 / 32);
        Assert.True(c.Position.Z < pose.Position.Z + 50);
    }

    [GameDataFact]
    public void CrashCameraIgnoresCarsFurtherThan150Metres()
    {
        var c = Camera(2);
        var car = Car(); car.CrashSlice = 3;
        var far = Pose with { Position = Pose.Position + new Vector3(200, 0, 0) };
        c.Update(Pose, car, 1.0 / 32);
        var before = c.Position;
        c.Update(far, car, 1.0 / 32);
        Near(before, c.Position, 1e-5f);
    }

    [GameDataFact]
    public void CrashCameraDoesNotLeaveTheRoadEdgesOfTheCarSlice()
    {
        var c = Camera(2);
        c.Slices = [Slice(0, 0, 0, 0x40, 0x40)]; // edges ±0x4000 (0x40·0x100) = ±0.25 m around x = 0, right = +x
        var car = Car(); car.CrashSlice = 1; car.Trk.Slice = 0;
        var pose = Pose with { Position = new Vector3(100, 20, 30) };
        c.Update(Pose, car, 1.0 / 32);
        var before = c.Position;
        c.Update(pose, car, 1.0 / 32);
        Near(before, c.Position, 1e-5f); // the step would end outside the edges → rejected
    }

    [GameDataFact]
    public void LeavingTheCrashReturnsToTheSelectedChaseView()
    {
        var c = Camera(2);
        var car = Car(); car.CrashSlice = 2;
        var pose = Pose with { Position = Pose.Position + new Vector3(30, 0, 0) };
        for (int i = 0; i < 5; i++) c.Update(pose, car, 1.0 / 32);
        car.CrashSlice = 0;
        for (int i = 0; i < 200; i++) c.Update(pose, car, 1.0 / 32);
        Assert.False(c.Crashing);
        Near(pose.Position + new Vector3(0, 2, -6), c.Position, 0.01f);
    }

    // ---------------------------------------------------------------- manual reset [ref:2874]

    [GameDataFact]
    public void ManualResetOrbitsFromTheSideToBehindTheCarForAllModes()
    {
        foreach (int mode in new[] { 0, 2 })
        {
            var c = Camera(mode);
            var car = Car(); car.ManualResetLock = 0x30; car.Trk.Slice = 1; // odd slice: +x
            c.Update(Pose, car, 1.0 / 32);
            Assert.False(c.InCar);
            Assert.Equal(9, c.DisplayMode);
            Near(Pose.Position + new Vector3(6, 2, 0), c.Position, 0.001f);
            Near(Vector3.Normalize(Pose.Position - c.Position), Vector3.Normalize(c.Forward), 0.05f);

            car.Trk.Slice = 2; // even slice: −x
            c.Update(Pose, car, 1.0 / 32);
            Near(Pose.Position + new Vector3(-6, 2, 0), c.Position, 0.001f);

            car.ManualResetLock = 1; // angle ≈ −90°: 6 m behind
            c.Update(Pose, car, 1.0 / 32);
            Assert.True(c.Position.Z < Pose.Position.Z - 5.5f && Math.Abs(c.Position.X - Pose.Position.X) < 0.5f);

            car.ManualResetLock = 0;
            c.Update(Pose, car, 1.0 / 32);
            Assert.False(c.Crashing);
            Assert.Equal(mode, c.Mode);
        }
    }

    // ---------------------------------------------------------------- whole race (RaceDrive)

    [GameDataFact]
    public void RaceDriveSwitchesToTheFinishCameraAfterTheFinishAndRendersIt()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false);
        d.RunAutopilot(12);
        Assert.NotEqual(RaceCamera.FinishMode, d.Cam.Mode);
        var frames = new List<FrameBuffer>();
        void Shot() { var fb = new FrameBuffer(); d.Render(fb); frames.Add(fb); }
        Shot();
        d.Car.RaceState = 2; // the player crosses the line [ref:2875]
        void Tick() => d.Cam.Update(d.Scene.PresentPoses(1), d.Car, 1.0 / 32, d.Scene.PlayerAnimation); // camera only, no sim step
        Tick();
        Assert.Equal(RaceCamera.FinishMode, d.Cam.Mode);
        Assert.False(d.ShowMirror);
        Shot();
        var first = d.Cam.Position - RaceScene.ToWorld(d.Car.Pos);
        foreach (double seconds in new[] { 1.5, 3, 20 })
        {
            for (int i = 0; i < (int)(seconds * 32); i++) Tick();
            Shot();
        }
        Assert.Equal(RaceCamera.FinishMode, d.Cam.Mode);
        Assert.True(Vector3.Distance(first, d.Cam.Position - RaceScene.ToWorld(d.Car.Pos)) > 0.5f, "camera must move along the CAN path");
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "out", "sonnet_t7");
        Directory.CreateDirectory(dir);
        var mosaic = new FrameBuffer(1280, 960);
        for (int i = 0; i < 4; i++)
            for (int y = 0; y < 480; y++)
                Array.Copy(frames[i].Rgba, y * 640 * 4, mosaic.Rgba, ((i / 2 * 480 + y) * 1280 + i % 2 * 640) * 4, 640 * 4);
        mosaic.SavePng(Path.Combine(dir, "finish_camera.png"));
    }
}
