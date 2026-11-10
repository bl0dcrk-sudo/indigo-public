using System.Numerics;
using NFS.Core;
using NFS.Data.Track;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Camera 6 (entry): the camera drives along the track; tilt of mode 4; cycle with mode 6. [ref:2859]</summary>
public class RaceCameraMode6Tests
{
    static readonly RenderPose Pose = new(new Vector3(0, 0, 500), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    /// <summary>The road runs along +Z; slice i stands at Z = i·zStep (16.16). Right = +X, Up = +Y, no mask gaps.
    /// Edges of 1024 (×256 → 4 m), so the side offset = LeftEdge·128 = 2 m.</summary>
    static TrackSlice[] Road(int count = 100, int zStep = 10 * Fx.One, Func<int, ushort>? mask = null) => Enumerable.Range(0, count).Select(i => new TrackSlice(
        0, 0, i * zStep,
        [0, 127, 0], [0, 0, 127], [127, 0, 0],
        0, 0, (ushort)(mask?.Invoke(i) ?? 1), 1024, 1024, 0, 0x11, 8, 12, 0, 0)).ToArray();

    static CarObj Car(int slice, double speedMs = 0, int lateral = 0) => new()
    {
        UpDotN = Fx.One, Orient = Mat3.Identity, RoadFrame = Mat3.Identity,
        Trk = new TrackPos { Slice = slice }, LateralOffset = lateral, AiSpeed = (int)(speedMs * Fx.One),
    };

    /// <summary>Camera in mode 6 over the road: cycle 2 → 3 → 4 → 5 → 6 (orbit enabled); entry is counted at the last cycle.
    /// Cycle with race clock 0 (as in the game, where each view gets raceTick), so the first step has a time base.</summary>
    static RaceCamera Mode6(TrackSlice[]? slices, CarObj car, bool raceStarted = true)
    {
        var camera = new RaceCamera(0) { OrbitEnabled = true, Slices = slices, RaceStarted = raceStarted };
        for (int i = 0; i < 10 && camera.Mode != 6; i++) camera.Cycle(Pose, car, raceTick: 0);
        Assert.Equal(6, camera.Mode);
        return camera;
    }

    static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.0001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, actual {actual}");

    static float MetresZ(RaceCamera camera) => camera.Position.Z;

    // ---- Entry ---- [ref:2860]

    [GameDataFact]
    public void EntryStartsTheCameraAtTheSpeedBandOffsetFromTheCarSlice()
    {
        // Speed bands (m/s) and slice offset: below 4 → −3; 4..(4+3) → +3; up to 20 m/s (remainder) → +5; above → +7.
        var cases = new (double Speed, int Offset)[] { (3, -3), (5, 3), (7, 5), (30, 5), (100, 7), (200, 7) };
        foreach (var (speed, offset) in cases)
        {
            var camera = Mode6(Road(), Car(50, speed));
            Assert.Equal((50 + offset) * 10f, MetresZ(camera), 0.001f);
        }
    }

    [GameDataFact]
    public void EntryWrapsTheCameraSliceAroundTheTrack()
    {
        // slice 1 − 3 = −2 → 98 (100 slices), slice 98 + 7 = 105 → 5.
        Assert.Equal(980f, MetresZ(Mode6(Road(), Car(1, 3))), 0.001f);
        Assert.Equal(50f, MetresZ(Mode6(Road(), Car(98, 100))), 0.001f);
    }

    // ---- Movement (a step every K ticks, interpolated) ----

    [GameDataFact]
    public void CameraAdvancesOneSliceEveryKTicksOnceTheRaceStarted()
    {
        // 5 m/s → band 0 (K = 64 ticks per slice). Entry: slice 53.
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        Assert.Equal(530f, MetresZ(camera), 0.001f);
        for (int i = 0; i < 63; i++) camera.Update(Pose, car, 1.0 / 64);
        Assert.Equal(530 + 63 / 64f * 10, MetresZ(camera), 0.001f); // 539.84375: 63 of 64 steps to the next slice
        camera.Update(Pose, car, 1.0 / 64);
        Assert.Equal(540f, MetresZ(camera), 0.001f);
    }

    [GameDataFact]
    public void CounterStandsStillBeforeTheRaceStarts()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car, raceStarted: false);
        var start = camera.Position;
        for (int i = 0; i < 200; i++) camera.Update(Pose, car, 1.0 / 64);
        Near(start, camera.Position, 1e-6f);
        Assert.Equal(6, camera.Mode);
    }

    [GameDataFact]
    public void InterpolationUsesIntegerStepsAndTruncatesTheStep()
    {
        // A slice every 100 units of 16.16: step = 100 / 64 = 1 (integer division), after 63 ticks: 5300 + 63 units, not 5398.4.
        var car = Car(50, 5);
        var camera = Mode6(Road(zStep: 100), car);
        for (int i = 0; i < 63; i++) camera.Update(Pose, car, 1.0 / 64);
        Assert.Equal((5300 + 63) / 65536f, MetresZ(camera), 1e-7f);
    }

    [GameDataFact]
    public void TheClockCountsCameraTicksFromRaceTicks()
    {
        // 1 race clock unit (64 Hz) = 1 camera update (in every mode). 63 ticks → no step, 64th → step. [ref:2861]
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        camera.Snap(Pose, car, raceTick: 1000);
        for (int i = 1; i <= 63; i++) camera.Update(Pose, car, 1.0 / 64, raceTick: 1000 + i);
        Assert.Equal(530 + 63 / 64f * 10, MetresZ(camera), 0.001f);
        camera.Update(Pose, car, 1.0 / 64, raceTick: 1000 + 64);
        Assert.Equal(540f, MetresZ(camera), 0.001f);
    }

    // ---- Jumps (d ≥ 8 forward, d ≤ −7 backward) ----

    [GameDataFact]
    public void CameraJumpsForwardWhenItIsEightSlicesBehindTheCar()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car); // kamera 53
        car.Trk.Slice = 80;                 // car far ahead of the camera: after the step d = 80 − 54 = 26 ≥ 8
        for (int i = 0; i < 64; i++) camera.Update(Pose, car, 1.0 / 64);
        // jump: idx 1 (10 m/s), r = 5 − 10 = −5 m < 3 m → +3 → slice 83
        Assert.Equal(830f, MetresZ(camera), 0.001f);
    }

    [GameDataFact]
    public void CameraJumpsBackwardWhenItIsSevenSlicesAheadOfTheCar()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car); // kamera 53
        car.Trk.Slice = 46;                 // after the step d = 46 − 54 = −8 ≤ −7
        for (int i = 0; i < 64; i++) camera.Update(Pose, car, 1.0 / 64);
        // backward jump: r = 5 − 10 = −5 m ≥ −8 m → −3 → slice 43
        Assert.Equal(430f, MetresZ(camera), 0.001f);
    }

    [GameDataFact]
    public void NoJumpWhenTheCameraIsFourSlicesFromTheCar()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car); // kamera 53
        car.Trk.Slice = 58;                 // after the step d = 58 − 54 = 4: no jump
        for (int i = 0; i < 64; i++) camera.Update(Pose, car, 1.0 / 64);
        Assert.Equal(540f, MetresZ(camera), 0.001f);
    }

    // ---- Height ----

    [GameDataFact]
    public void CameraIsFiveMetresAboveTheRoadWhenTheCarIsUnderIt()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        var over = new RenderPose(new Vector3(camera.Position.X, 0, camera.Position.Z), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        camera.Snap(over, car);
        Assert.Equal(5f, camera.Position.Y, 0.0001f); // v = 0 → 0x50000
    }

    [GameDataFact]
    public void HeightIsCappedSoTheCameraNeverGoesBelowOneAndAHalfMetres()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        var far = new RenderPose(new Vector3(0, 0, 800), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        camera.Snap(far, car);
        Assert.Equal(1.5f, camera.Position.Y, 0.0001f); // v > 0x37FFF → 0x38000 (3.5 m): 5 − 3.5
    }

    // ---- Road side (gap in the mask, LateralOffset sign) ----

    [GameDataFact]
    public void RoadSideOffsetAppearsNextToAMaskGapOnTheLeftAndTheRight()
    {
        // LeftEdge = RightEdge = 1024 → 1024·128 = 2 m; × Right (127·512 >> 16) = 1.984375 m.
        var gap = Road(mask: _ => 5); // 0b101: gap in the bit
        var left = Mode6(gap, Car(50, 5, lateral: -1));
        Assert.Equal(-1.984375f, left.Position.X, 0.0001f);
        var right = Mode6(gap, Car(50, 5, lateral: 1));
        Assert.Equal(1.984375f, right.Position.X, 0.0001f);
    }

    [GameDataFact]
    public void NoRoadSideOffsetWithoutAMaskGap()
    {
        var camera = Mode6(Road(), Car(50, 5, lateral: -1));
        Assert.Equal(0f, camera.Position.X, 0.0001f);
    }

    // ---- Look-behind, display, no fallback ----

    [GameDataFact]
    public void LookBehindDoesNotMoveTheTrackCamera()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        camera.Snap(Pose, car);
        var position = camera.Position;
        var forward = camera.Forward;
        camera.SetLookBehind(true, Pose, car);
        Assert.True(camera.LookingBehind);
        Near(position, camera.Position, 1e-6f);
        Near(forward, camera.Forward, 1e-6f);
        Assert.Equal(6, camera.DisplayMode);
        camera.SetLookBehind(false, Pose, car);
        Near(position, camera.Position, 1e-6f);
    }

    [GameDataFact]
    public void DisplayModeIsSixAndForwardLooksAtTheCar()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        camera.Snap(Pose, car);
        Assert.Equal(6, camera.DisplayMode);
        Assert.False(camera.InCar);
        Near(Vector3.Normalize(Pose.Position - camera.Position), camera.Forward);
    }

    [GameDataFact]
    public void TiltedCarDoesNotFallBackToTheChaseView()
    {
        var car = Car(50, 5);
        var camera = Mode6(Road(), car);
        car.UpDotN = 0;
        camera.Update(Pose, car, 1.0 / 64);
        Assert.False(camera.Flipping);
    }

    // ---- Cycle with mode 6 (NextMode) ----

    [GameDataFact]
    public void ModeSixIsInTheCycleAfterFiveAndBeforeSeven()
    {
        // C: 3 → 4 → 5 → 6 → 7 → 0
        var seen = new List<int>();
        int mode = 3;
        for (int i = 0; i < 6; i++) { mode = RaceCamera.NextMode(mode, 0, orbitEnabled: true); seen.Add(mode); }
        Assert.Equal(new[] { 4, 5, 6, 7, 0, 1 }, seen);
        // C + B: … 6 → 7 → 8 → 0
        seen.Clear(); mode = 3;
        for (int i = 0; i < 6; i++) { mode = RaceCamera.NextMode(mode, 0, orbitEnabled: true, fixedEnabled: true); seen.Add(mode); }
        Assert.Equal(new[] { 4, 5, 6, 7, 8, 0 }, seen);
        // finish mode alone (0xC) → 6 → 7 → 0
        Assert.Equal(6, RaceCamera.NextMode(RaceCamera.FinishMode, 0));
        Assert.Equal(7, RaceCamera.NextMode(6, 0));
        Assert.Equal(0, RaceCamera.NextMode(7, 0));
    }

    // ---- Tilt of mode 4 (writes +0x4C = 0) ---- [ref:2862]

    [GameDataFact]
    public void ModeFourLooksExactlyAtTheCarWithPitchZero()
    {
        var car = Car(50, 5);
        var camera = new RaceCamera(0) { OrbitEnabled = true };
        for (int i = 0; i < 10 && camera.Mode != 4; i++) camera.Cycle(Pose, car);
        Assert.Equal(4, camera.Mode);
        camera.Snap(Pose, car);
        Near(Vector3.Normalize(Pose.Position - camera.Position), camera.Forward);
    }

    [GameDataFact]
    public void ModeFourKeepsItsPitchDuringTheFlipFallback()
    {
        var car = Car(50, 5);
        var camera = new RaceCamera(0) { OrbitEnabled = true };
        for (int i = 0; i < 10 && camera.Mode != 4; i++) camera.Cycle(Pose, car);
        car.UpDotN = 0x4000;
        camera.Update(Pose, car, 1.0 / 64);
        Assert.True(camera.Flipping);
        // 0x2A (42/65536 turn ≈ 0.004 rad) still works [ref:2863]
        float off = Vector3.Distance(Vector3.Normalize(Pose.Position - camera.Position), camera.Forward);
        Assert.True(off > 0.0001f && off < 0.01f, $"pitch 0x2A expected, offset {off}");
    }

    // ---- Review dump (headless) ----

    /// <summary>Frame of mode 6 in a real race scene – PNG only when <c>NFS_MODE6_PNG_DIR</c> is set.</summary>
    [GameDataFact]
    public void ModeSixRendersARealRaceSceneAndCanWriteReviewPngs()
    {
        var drive = new RaceDrive(new RaceScene("replay"), countdown: false, orbitCameraActivation: true);
        drive.RunAutopilot(10); // ~ race start – the camera counts steps from this point [ref:2864]
        var pose = drive.Scene.PresentPoses(drive.Clock.Accumulator / drive.Clock.Step);
        for (int i = 0; i < 4; i++) drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick); // 2 → 3 → 4 → 5 → 6
        Assert.Equal(6, drive.Cam.Mode);
        var first = new FrameBuffer(640, 480);
        drive.Render(first);
        Assert.Contains(first.Rgba, b => b != 0);
        drive.Frame(1.0 / 32, default); // a later frame: camera step from the race clock
        Assert.Equal(6, drive.Cam.Mode);
        var later = new FrameBuffer(640, 480);
        drive.Render(later);
        Assert.Contains(later.Rgba, b => b != 0);
        if (Environment.GetEnvironmentVariable("NFS_MODE6_PNG_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            first.SavePng(Path.Combine(dir, "mode6_track.png"));
            later.SavePng(Path.Combine(dir, "mode6_track_later.png"));
        }
    }
}
