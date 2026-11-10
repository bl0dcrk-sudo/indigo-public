using System.Numerics;
using NFS.Core;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Camera 7, camera 8 and the successor table from RShift+B. [ref:2865]</summary>
public class RaceCameraMode78Tests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    // Car rotated 90° about the Y axis: right = +Z, up = +Y, forward = −X (right-handed system: right × up = forward).
    static readonly RenderPose Turned = new(new Vector3(-5, 2, 40), new Vector3(0, 0, 1), Vector3.UnitY, new Vector3(-1, 0, 0));

    static CarObj Car() => new() { UpDotN = Fx.One, RoadFrame = Mat3.Identity, Orient = Mat3.Identity };

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.0001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, actual {actual}");

    /// <summary>Camera in the given mode: cycle from mode 2 (default start) via RaceCamera.Cycle.</summary>
    static RaceCamera At(int mode, bool orbit = false, bool fixedMode = false)
    {
        var camera = new RaceCamera(0) { OrbitEnabled = orbit, FixedEnabled = fixedMode };
        for (int i = 0; i < 20 && camera.Mode != mode; i++) camera.Cycle(Pose, Car());
        Assert.Equal(mode, camera.Mode);
        return camera;
    }

    static int Next(int mode, bool orbit = false, bool fixedMode = false, bool split = false, bool finished = false)
        => RaceCamera.NextMode(mode, 0, split, finished, orbit, fixedMode);

    /// <summary>Successive modes after <paramref name="steps"/> steps from <paramref name="start"/>.</summary>
    static List<int> Sequence(int start, int steps, bool orbit = false, bool fixedMode = false, bool split = false, bool finished = false)
    {
        var seen = new List<int>();
        int mode = start;
        for (int i = 0; i < steps; i++) { mode = Next(mode, orbit, fixedMode, split, finished); seen.Add(mode); }
        return seen;
    }

    // ---- Successor table (NextMode) ----

    [GameDataFact]
    public void WithoutShortcutsTheCycleIsUnchanged()
        => Assert.Equal(new[] { 1, 2, 3, 0, 1, 2, 3, 0 }, Sequence(0, 8));

    [GameDataFact]
    public void RightShiftCSendsModeThreeThroughFourFiveSevenBackToZeroSkippingSix()
        => Assert.Equal(new[] { 4, 5, 6, 7, 0, 1, 2 }, Sequence(3, 7, orbit: true));

    [GameDataFact]
    public void RightShiftBSendsModeThreeToFixedCameraAndBackToZero()
        => Assert.Equal(new[] { 8, 0, 1, 2, 3 }, Sequence(3, 5, fixedMode: true));

    [GameDataFact]
    public void RightShiftCAndBRunFiveSevenEightThenZero()
        => Assert.Equal(new[] { 4, 5, 6, 7, 8, 0 }, Sequence(3, 6, orbit: true, fixedMode: true));

    [GameDataFact]
    public void SplitFinishTableIsNotChangedByTheStartupChords()
    {
        foreach (bool c in new[] { false, true })
            foreach (bool b in new[] { false, true })
            {
                // after the finish in split: 5 → 6 → 7 → 1; 8 → 4; 4 → 5 – regardless of the starting keys
                Assert.Equal(new[] { 6, 7, 1 }, Sequence(5, 3, c, b, split: true, finished: true));
                Assert.Equal(4, Next(8, c, b, split: true, finished: true));
                Assert.Equal(5, Next(4, c, b, split: true, finished: true));
            }
        Assert.Equal(4, RaceCamera.NextMode(3, 0, split: true, finished: true, orbitEnabled: true, fixedEnabled: true));
    }

    [GameDataFact]
    public void ModeSixIsPartOfTheCycleBetweenFiveAndSeven()
    {
        Assert.Equal(6, Next(5, orbit: true));
        Assert.Equal(7, Next(6));
        Assert.Equal(0, Next(7));
    }

    // ---- Key wiring (InputMapping / RaceDrive) ----

    [GameDataFact]
    public void FixedEnabledIsSampledAtRaceStartLikeOrbit()
    {
        var direct = new GameLoop();
        direct.StartRace(new RaceScene("replay"), fixedCameraActivation: true);
        Assert.True(direct.Drive!.Cam.FixedEnabled);
        Assert.False(direct.Drive.Cam.OrbitEnabled);

        var normal = new GameLoop();
        normal.StartRace(new RaceScene("replay"));
        Assert.False(normal.Drive!.Cam.FixedEnabled);
    }

    [GameDataFact]
    public void FixedEnabledReachesTheCameraThroughTheMenuStart()
    {
        var loop = new GameLoop();
        for (int i = 0; i < 4; i++) loop.Frame(0, new DesktopInput(default, [MenuKey.Down], false));
        loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false, FixedCameraActivation: true));
        Assert.True(loop.Drive!.Cam.FixedEnabled);
        loop.Frame(0, new DesktopInput(default, [], false, FixedCameraActivation: false));
        Assert.True(loop.Drive.Cam.FixedEnabled); // sampled at initialization, not live-toggled.
    }

    [GameDataFact]
    public void ModeSevenSitsAtTheCarOffsetInTheRotatedCarAxes()
    {
        var camera = At(7, orbit: true);
        var car = Car();
        camera.Snap(Turned, car);
        var expected = Turned.Position + Turned.Right * 3f + Turned.Up * 1.3f + Turned.Forward * -3.9f;
        Near(expected, camera.Position, 0.001f);
        camera.Update(Turned, car, 1.0 / 32);
        Near(expected, camera.Position, 0.001f);
        Near(Vector3.Normalize(Turned.Position - camera.Position), camera.Forward); // look-at: pitch 0, aimed at the car
        Assert.Equal(7, camera.DisplayMode);
        Assert.False(camera.InCar);
    }

    [GameDataFact]
    public void ModeSevenLookBehindNegatesZAndRestoresOnRelease()
    {
        var camera = At(7, orbit: true);
        var car = Car();
        camera.Snap(Turned, car);
        camera.SetLookBehind(true, Turned, car);
        Assert.True(camera.LookingBehind);
        Assert.Equal(7, camera.DisplayMode);
        Near(Turned.Position + Turned.Right * 3f + Turned.Up * 1.3f + Turned.Forward * 3.9f, camera.Position, 0.001f);
        Near(Vector3.Normalize(Turned.Position - camera.Position), camera.Forward);
        camera.Update(Turned, car, 1.0 / 32);
        Near(Turned.Position + Turned.Right * 3f + Turned.Up * 1.3f + Turned.Forward * 3.9f, camera.Position, 0.001f);
        camera.SetLookBehind(false, Turned, car);
        Near(Turned.Position + Turned.Right * 3f + Turned.Up * 1.3f + Turned.Forward * -3.9f, camera.Position, 0.001f);
    }

    [GameDataFact]
    public void OverturnedCarEasesModeSevenAwayFromItsOffset()
    {
        var camera = At(7, orbit: true);
        var car = Car();
        camera.Snap(Turned, car);
        var offset = camera.Position;
        car.UpDotN = 0;
        car.WrongWayTicks = 0;
        camera.Update(Turned, car, 1.0 / 32);
        Assert.True(camera.Flipping);
        Assert.True(Vector3.Distance(offset, camera.Position) > 0.001f, "fallback must ease away from the mode 7 offset");
    }

    [GameDataFact]
    public void ModeEightStaysFifteenMetresUpAndTwoBehindInWorldAxesWhateverTheHeading()
    {
        var camera = At(8, fixedMode: true);
        var car = Car();
        var world = new Vector3(0, 15, -2);
        camera.Snap(Turned, car);
        Near(Turned.Position + world, camera.Position, 0.001f);
        camera.Update(Turned, car, 1.0 / 32);
        Near(Turned.Position + world, camera.Position, 0.001f);
        camera.Snap(Pose, car);
        Near(Pose.Position + world, camera.Position, 0.001f);
        Assert.False(camera.InCar);
        Assert.Equal(8, camera.DisplayMode);
    }

    [GameDataFact]
    public void ModeEightLooksAtTheCarWithPitchZero()
    {
        var camera = At(8, fixedMode: true);
        var car = Car();
        camera.Snap(Turned, car);
        Near(Vector3.Normalize(Turned.Position - camera.Position), camera.Forward);
        Assert.True(Vector3.Dot(camera.Up, Vector3.UnitY) > 0, "view stays upright");
        Assert.True(MathF.Abs(Vector3.Dot(camera.Up, camera.Forward)) < 0.0001f);
    }

    [GameDataFact]
    public void ModeEightIgnoresLookBehindInPositionAndDisplayMode()
    {
        var camera = At(8, fixedMode: true);
        var car = Car();
        camera.Snap(Turned, car);
        var position = camera.Position;
        var forward = camera.Forward;
        camera.SetLookBehind(true, Turned, car);
        Assert.True(camera.LookingBehind);
        Assert.Equal(8, camera.DisplayMode);
        Near(position, camera.Position, 0.00001f);
        Near(forward, camera.Forward, 0.00001f);
        camera.Update(Turned, car, 1.0 / 32);
        Near(position, camera.Position, 0.001f);
        camera.SetLookBehind(false, Turned, car);
        Near(position, camera.Position, 0.00001f);
    }

    [GameDataFact]
    public void ModeEightIsNotTiltCorrectedAndNeverFallsBackToChase()
    {
        var camera = At(8, fixedMode: true);
        var car = Car();
        camera.Snap(Turned, car);
        var position = camera.Position;
        car.UpDotN = 0;
        camera.Update(Turned, car, 1.0 / 32);
        Assert.False(camera.Flipping);
        Near(position, camera.Position, 0.00001f);
    }

    [GameDataFact]
    public void CrashCameraTakesPrecedenceOverModeEight()
    {
        var camera = At(8, fixedMode: true);
        var car = Car();
        camera.Snap(Turned, car);
        var fixedPosition = camera.Position;
        car.CrashSlice = 3;
        camera.Update(Turned, car, 1.0 / 32);
        Assert.True(camera.Crashing);
        Assert.True(Vector3.Distance(fixedPosition, camera.Position) > 0.001f);
    }

    // ---- Review dump (headless) ----

    /// <summary>PNG dumps of modes 7 and 8 in a real race scene – only when <c>NFS_MODE78_PNG_DIR</c> is set.</summary>
    [GameDataFact]
    public void ModesSevenAndEightRenderARealRaceSceneAndCanWriteReviewPngs()
    {
        var drive = new RaceDrive(new RaceScene("replay"), countdown: false, orbitCameraActivation: true, fixedCameraActivation: true);
        drive.RunAutopilot(2);
        var pose = drive.Scene.PresentPoses(drive.Clock.Accumulator / drive.Clock.Step);
        void Cycle() => drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
        for (int i = 0; i < 5; i++) Cycle(); // 2 → 3 → 4 → 5 → 6 → 7
        Assert.Equal(7, drive.Cam.Mode);
        var chase = new FrameBuffer(640, 480);
        drive.Render(chase);
        Assert.Contains(chase.Rgba, b => b != 0);
        drive.Cam.SetLookBehind(true, pose, drive.Car, drive.Scene.PlayerAnimation);
        var rear = new FrameBuffer(640, 480);
        drive.Render(rear);
        Assert.Contains(rear.Rgba, b => b != 0);
        drive.Cam.SetLookBehind(false, pose, drive.Car, drive.Scene.PlayerAnimation);
        Cycle(); // 7 → 8 (RShift+C and RShift+B)
        Assert.Equal(8, drive.Cam.Mode);
        var fixedView = new FrameBuffer(640, 480);
        drive.Render(fixedView);
        Assert.Contains(fixedView.Rgba, b => b != 0);
        if (Environment.GetEnvironmentVariable("NFS_MODE78_PNG_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            chase.SavePng(Path.Combine(dir, "mode7_chase.png"));
            rear.SavePng(Path.Combine(dir, "mode7_rear.png"));
            fixedView.SavePng(Path.Combine(dir, "mode8_fixed.png"));
        }
    }

    [GameDataFact]
    public void TiltedCarMakesModeSevenEaseToTheRoadFrameViewpoint()
    {
        var camera = At(7, orbit: true);
        var car = Car();
        car.UpDotN = 0x8000;
        camera.Snap(Pose, car);
        var start = camera.Position - Pose.Position;
        // target: offset in the road frame (identity here). [ref:2866]
        var target = new Vector3(NFS.Core.Constants.CameraConstants.ChaseOffset[0], NFS.Core.Constants.CameraConstants.ChaseOffset[1], NFS.Core.Constants.CameraConstants.ChaseOffset[2]) / 65536f;
        camera.Update(Pose, car, 1.0 / 32);
        Assert.True(camera.Flipping);
        Near(Pose.Position + Vector3.Lerp(start, target, 0x51E / 65536f), camera.Position, 0.001f);
        for (int i = 0; i < 500; i++) camera.Update(Pose, car, 1.0 / 32);
        Near(Pose.Position + target, camera.Position, 0.01f);
    }
}
