using System.Numerics;
using NFS.Core;
using NFS.Data.Track;
using NFS.Desktop;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

public class StartCameraAnimationTests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX);
    static CarObj Car() => new() { UpDotN = Fx.One, Flags = CarFlags.LocalHuman, Orient = Mat3.Identity, RoadFrame = Mat3.Identity };

    static Can TwoKeyCan(int ticksPerKey = 4)
    {
        var bytes = new byte[8 + 2 * 20];
        BitConverter.GetBytes((ushort)2).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)ticksPerKey).CopyTo(bytes, 6);
        Key(bytes, 8, 2, 3, 4);
        Key(bytes, 28, 6, 7, 8);
        return Can.Parse(bytes);
    }

    static void Key(byte[] bytes, int offset, int x, int y, int z)
    {
        BitConverter.GetBytes(x * Fx.One).CopyTo(bytes, offset);
        BitConverter.GetBytes(y * Fx.One).CopyTo(bytes, offset + 4);
        BitConverter.GetBytes(z * Fx.One).CopyTo(bytes, offset + 8);
        BitConverter.GetBytes((short)0x4000).CopyTo(bytes, offset + 18); // identity quaternion
    }

    static RaceCamera Camera(bool split = false)
    {
        var camera = new RaceCamera(0) { Split = split };
        return camera;
    }

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.002f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, actual {actual}");

    [Theory]
    [InlineData(0, 0, false, 0)]
    [InlineData(0, 1, false, 0)]
    [InlineData(1, 0, false, 4)]
    [InlineData(0, 0, true, 5)]
    public void CamInitSelectsForwardCircuitForwardOtherAndReverseCanSlots(int raceType, int commMode, bool reverse, int expected)
        => Assert.Equal(expected, RaceCamera.StartCanIndex(raceType, commMode, reverse));

    [GameDataTheory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void RelativeIntroUsesRoadFrameEvenWhenOrientationAndRenderPoseDiffer(int slot, bool reverse)
    {
        var car = Car();
        car.RoadFrame = new Mat3(0, 0, Fx.One, Fx.One, 0, 0, 0, Fx.One, 0);
        car.Orient = reverse ? new Mat3(-Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, -Fx.One) : Mat3.Identity;
        var camera = Camera();
        camera.TrackReverse = reverse;
        camera.StartIntro(TwoKeyCan(), slot, Pose, car);
        camera.Update(Pose, car, 0, raceTick: 2);
        Near(Pose.Position + new Vector3(5, 6, 4), camera.Position);
        Near(-Vector3.UnitX, camera.Forward);
        Near(Vector3.UnitY, camera.Up);
    }

    [GameDataFact]
    public void WorldIntroIgnoresTheCarAndRoadOrientation()
    {
        var camera = Camera();
        camera.StartIntro(TwoKeyCan(), 0, Pose, Car());
        camera.Update(Pose, Car(), 0, raceTick: 2);
        Near(Pose.Position + new Vector3(4, 5, 6), camera.Position);
        Near(-Vector3.UnitY, camera.Forward);
        Near(Vector3.UnitZ, camera.Up);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void IntroRemainsActiveJustBeforeCanEndAndTransitionsAtExactTick(bool split, int normalMode)
    {
        var camera = Camera(split);
        var can = TwoKeyCan();
        var car = Car();
        camera.StartIntro(can, split ? 4 : 0, Pose, car);
        Assert.Equal(RaceCamera.StartAnimationMode, camera.Mode);

        camera.Update(Pose, car, 0, raceTick: can.TicksPerKey - 1);
        Assert.Equal(RaceCamera.StartAnimationMode, camera.Mode);
        var beforeCycle = camera.Position;
        camera.Cycle(Pose, car); // returns immediately for mode 0xB [ref:2940]
        Assert.Equal(RaceCamera.StartAnimationMode, camera.Mode);
        Assert.Equal(beforeCycle, camera.Position);

        camera.Update(Pose, car, 0, raceTick: can.TicksPerKey);
        Assert.Equal(normalMode, camera.Mode);
        var (height, back) = split ? (0x4CCC / 65536f, 0xE666 / 65536f) : (0x4CCC / 65536f, 0.5f);
        Near(Pose.Position + Pose.Up * height + Pose.Forward * back, camera.Position);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void MissingCanFallsBackToTheNormalSingleOrSplitView(bool split, int normalMode)
    {
        var camera = Camera(split);
        camera.StartIntro(null, split ? 4 : 0, Pose, Car());
        Assert.Equal(normalMode, camera.Mode);
        Assert.Null(camera.StartAnimation);
        var (height, back) = split ? (0x4CCC / 65536f, 0xE666 / 65536f) : (0x4CCC / 65536f, 0.5f);
        Near(Pose.Position + Pose.Up * height + Pose.Forward * back, camera.Position);
    }

    [GameDataFact]
    public void CountdownDisabledDriveStartsInItsNormalView()
    {
        var drive = new RaceDrive(new RaceScene("replay"), countdown: false);
        Assert.NotEqual(RaceCamera.StartAnimationMode, drive.Cam.Mode);
        Assert.Equal(2, drive.Cam.Mode);
    }

    [GameDataFact]
    public void CountdownRaceRendersTheIntroPathAndItsNormalViewTransition()
    {
        var drive = new RaceDrive(new RaceScene("replay"));
        var can = Assert.IsType<Can>(drive.Cam.StartAnimation);
        Assert.Equal(RaceCamera.StartAnimationMode, drive.Cam.Mode);
        string? dir = Environment.GetEnvironmentVariable("NFS_INTRO_PNG_DIR");
        if (dir != null) Directory.CreateDirectory(dir);
        void Shot(string name)
        {
            var frame = new FrameBuffer();
            drive.Render(frame);
            byte[] png = frame.EncodePng();
            Assert.True(png.Length > 5000);
            if (dir != null) File.WriteAllBytes(Path.Combine(dir, name + ".png"), png);
        }

        Shot("intro_start");
        int duration = (can.Keys.Count - 1) * can.TicksPerKey;
        while (drive.Runner.Tick < duration / 2) drive.Frame(1.0 / 32, default);
        Assert.Equal(RaceCamera.StartAnimationMode, drive.Cam.Mode);
        Shot("intro_middle");
        while (drive.Runner.Tick <= duration) drive.Frame(1.0 / 32, default);
        int returnMode = drive.Scene.IntroReturnMode;
        if (returnMode == 0 && RaceScene.PlayerCar(drive.Scene.Session.Cars)!.Setup.CarType > 11) returnMode = 1;
        Assert.Equal(returnMode, drive.Cam.Mode);
        Shot("intro_view");
    }
    [GameDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookBehindPressAndReleaseAtZeroDtPreserveIntroPose(bool split)
    {
        var camera = Camera(split);
        var car = Car();
        camera.StartIntro(TwoKeyCan(), split ? 4 : 0, Pose, car);
        var expected = (camera.Position, camera.Forward, camera.Up);
        foreach (bool held in new[] { true, false })
        {
            camera.SetLookBehind(held, Pose, car);
            Assert.Equal(expected, (camera.Position, camera.Forward, camera.Up));
            camera.Update(Pose, car, 0, raceTick: 0);
            Assert.Equal(expected, (camera.Position, camera.Forward, camera.Up));
        }
    }

    [Fact]
    public void TruncatedCanHeaderAndKeysFallBackWithoutParsing()
    {
        var valid = new byte[48];
        BitConverter.GetBytes((ushort)2).CopyTo(valid, 4);
        for (int length = 0; length < valid.Length; length++)
            Assert.Null(RaceScene.ParseIntroCan(valid[..length]));
        Assert.NotNull(RaceScene.ParseIntroCan(valid));
    }

    static RaceDrive IntroDrive()
    {
        var drive = new RaceDrive(new RaceScene("replay"));
        drive.Cam.StartIntro(TwoKeyCan(300), 0, RenderPose.From(drive.Car.Pos, drive.Car.Orient), drive.Car,
            raceTick: drive.Runner.Tick);
        return drive;
    }

    [GameDataFact]
    public void CappedFrameMatchesSteppedSimulationAndEqualInterpolation()
    {
        var capped = IntroDrive();
        var stepped = IntroDrive();
        capped.Frame(10, default); // clock caps this at eight simulation steps = sixteen race ticks
        for (int i = 0; i < 8; i++) stepped.Frame(1.0 / 32, default);
        Assert.Equal(16, capped.Runner.Tick);
        Assert.Equal(stepped.Runner.Tick, capped.Runner.Tick);
        Assert.Equal(RaceCamera.StartAnimationMode, capped.Cam.Mode);
        Near(stepped.Cam.Position, capped.Cam.Position);
        Near(stepped.Cam.Forward, capped.Cam.Forward);
        foreach (var drive in new[] { capped, stepped }) drive.Frame(1.0 / 64, default);
        Near(stepped.Cam.Position, capped.Cam.Position);
        var pose = capped.Scene.PresentPoses(0.5);
        float blend = 4 * 17 / 300f; // tick16 + half a 2-tick step
        Near(pose.Position + new Vector3(2 + blend, 3 + blend, 4 + blend), capped.Cam.Position);
        // A cycle at the same authoritative time must not restart or freeze the intro.
        capped.Frame(0, default(DriveKeys) with { CycleCamera = true });
        Near(stepped.Cam.Position, capped.Cam.Position);
        capped.Cam.Snap(pose, capped.Car, raceTick: 17);
        Near(stepped.Cam.Position, capped.Cam.Position);
    }

    [GameDataFact]
    public void AutopilotSnapExpiresIntroAtTheAuthoritativeRaceTick()
    {
        var drive = IntroDrive();
        drive.RunAutopilot(302.0 / 64);
        Assert.Equal(302, drive.Runner.Tick);
        Assert.NotEqual(RaceCamera.StartAnimationMode, drive.Cam.Mode);
    }

}
