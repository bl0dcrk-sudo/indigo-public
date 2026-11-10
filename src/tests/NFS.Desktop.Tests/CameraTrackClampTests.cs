using System.Numerics;
using System.Buffers.Binary;
using NFS.Core;
using NFS.Data.Track;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Camera clamping on TR04: the camera stays on the road side of the walls and above the ground. [ref:2725]</summary>
public class CameraTrackClampTests
{
    sealed class Scene
    {
        public readonly TrackRenderer Track = new(4);
        public readonly TrackLocator Locator;
        public TrackSlice[] Slices => Track.Slices;
        public Scene() => Locator = new TrackLocator(new TrackGeometry(Track.Trk, Track.Slices));

        public static Vector3 Dir(sbyte[] v) => Vector3.Normalize(new Vector3(v[0], v[1], v[2]));
        public Vector3 Centre(int s) => new Vector3(Slices[s].X, Slices[s].Y, Slices[s].Z) / 65536f;

        /// <summary>Car on slice <paramref name="s"/>, <paramref name="lateral"/> m to the right of the centre line; optionally
        /// facing downhill and/or with a level (not road-aligned) body.</summary>
        public (RenderPose Pose, CarObj Car) CarAt(int s, float lateral, float lift = 0, bool downhill = false, bool level = false)
        {
            var sl = Slices[s];
            var right = Dir(sl.Right);
            var fwd = Dir(sl.Forward);
            if (downhill != fwd.Y < 0) { fwd = -fwd; right = -right; }
            var up = Dir(sl.Up);
            if (level) { fwd = Vector3.Normalize(new Vector3(fwd.X, 0, fwd.Z)); up = Vector3.UnitY; }
            var pose = new RenderPose(Centre(s) + Dir(sl.Right) * lateral + Vector3.UnitY * lift, right, up, fwd);
            var car = new CarObj { UpDotN = Fx.One };
            car.Trk.Slice = s;
            return (pose, car);
        }

        public RaceCamera Camera(int mode, bool withLocator, RenderPose pose, CarObj car)
        {
            var c = new RaceCamera(0) { Locator = withLocator ? Locator : null };
            while (c.Mode != mode) c.Cycle(pose, car);
            c.Snap(pose, car);
            return c;
        }

        public RaceCamera OrbitCamera(bool withLocator, RenderPose pose, CarObj car)
        {
            var c = new RaceCamera(0)
            { OrbitEnabled = true, Locator = withLocator ? Locator : null };
            c.Cycle(pose, car);
            c.Cycle(pose, car, raceTick: 0);
            return c;
        }

        /// <summary>Lateral offset in the orthonormal frame the original rebuilds from the slice (right = up x forward).</summary>
        public float Lateral(int s, Vector3 p)
        {
            var f = Dir(Slices[s].Forward);
            var u = Vector3.Normalize(Vector3.Cross(f, Dir(Slices[s].Right)));
            return Vector3.Dot(p - Centre(s), Vector3.Cross(u, f));
        }

        /// <summary>Straight, level, wide slice with a wall on the given side.</summary>
        public int FindWall(bool rightSide, float minEdge = 5)
        {
            for (int s = 40; s < Slices.Length - 40; s++)
            {
                var sl = Slices[s];
                int nib = rightSide ? sl.Surroundings & 0xF : sl.Surroundings >> 4;
                float edge = (rightSide ? sl.RightEdge : sl.LeftEdge) / 256f;
                float other = (rightSide ? sl.LeftEdge : sl.RightEdge) / 256f;
                if (nib == 0 || edge < minEdge || other < minEdge) continue;
                if (Vector3.Dot(Dir(sl.Forward), Dir(Slices[s - 3].Forward)) < 0.9999f || Vector3.Dot(Dir(sl.Forward), Dir(Slices[s + 3].Forward)) < 0.9999f) continue;
                if (MathF.Abs(Dir(sl.Forward).Y) > 0.02f) continue;
                return s;
            }
            return -1;
        }
    }

    static readonly Lazy<Scene> Shared = new(() => new Scene());
    static Scene S => Shared.Value;

    /// <summary>Slice the original finds for a camera position (hill-climb from the car slice). [ref:2726]</summary>
    static int SliceOf(Vector3 p, int carSlice)
    {
        var tp = new TrackPos { Slice = carSlice };
        S.Locator.UpdateSlice(CameraTrackCorrector.ToFixed(p), tp);
        return tp.Slice;
    }

    [GameDataTheory]
    [InlineData(0, 0f)] [InlineData(1, 0f)] [InlineData(2, 0f)] [InlineData(3, 0f)]
    [InlineData(2, 6f)] [InlineData(3, -6f)] [InlineData(0, 8f)] [InlineData(1, -8f)]
    public void OpenRoadLeavesTheCameraWhereItWas(int mode, float lateral)
    {
        var s = S;
        int slice = s.FindWall(true);
        var (pose, car) = s.CarAt(slice, lateral);
        var plain = s.Camera(mode, false, pose, car);
        var clamped = s.Camera(mode, true, pose, car);
        Assert.True(Vector3.Distance(plain.Position, clamped.Position) < 0.001f, $"{plain.Position} vs {clamped.Position}");
        Assert.True(Vector3.Distance(plain.Forward, clamped.Forward) < 0.001f);
    }

    [GameDataTheory]
    [InlineData(2, 1.0f)] [InlineData(3, 1.0f)] [InlineData(2, 1.5f)] [InlineData(3, 0.4f)]
    public void CarBesideTheRightWallKeepsTheCameraOnTheRoadSideByTheOriginalCurve(int mode, float gapToEdge)
    {
        var s = S;
        int slice = s.FindWall(true);
        var (pose, car) = s.CarAt(slice, s.Slices[slice].RightEdge / 256f - gapToEdge);
        var plain = s.Camera(mode, false, pose, car);
        var moved = s.Camera(mode, true, pose, car);
        int cs = SliceOf(plain.Position, slice);
        float edge = s.Slices[cs].RightEdge / 256f, lat = s.Lateral(cs, plain.Position);
        // right wall: d = lat + 3.5 - edge; the camera moves in by (d - 1)^2/d and rises by 3 * that / lat. [ref:2727]
        float d = lat + 3.5f - edge, pull = (d - 1) * (d - 1) / d;
        Assert.True(pull > 0.1f);
        Assert.Equal(lat - pull, s.Lateral(cs, moved.Position), 0.15f); // the pull of the original is approximate (about 0.7 %) [ref:2728]
        Assert.Equal(plain.Position.Y + 3 * pull / lat, moved.Position.Y, 0.06f);
        Assert.True(s.Lateral(cs, moved.Position) < edge - 1.5f);
    }

    [GameDataTheory]
    [InlineData(2, 1.0f)] [InlineData(3, 1.5f)]
    public void CarBesideTheLeftWallKeepsTheCameraOnTheRoadSideByTheOriginalCurve(int mode, float gapToEdge)
    {
        var s = S;
        int slice = s.FindWall(false);
        var (pose, car) = s.CarAt(slice, -(s.Slices[slice].LeftEdge / 256f - gapToEdge));
        var plain = s.Camera(mode, false, pose, car);
        var moved = s.Camera(mode, true, pose, car);
        int cs = SliceOf(plain.Position, slice);
        float edge = s.Slices[cs].LeftEdge / 256f, lat = s.Lateral(cs, plain.Position);
        float gap = -lat + 3.5f - edge, pull = (gap - 1) * (gap - 1) / gap;
        Assert.True(pull > 0.1f);
        Assert.Equal(lat + pull, s.Lateral(cs, moved.Position), 0.15f);
        Assert.Equal(plain.Position.Y + 3 * pull / -lat, moved.Position.Y, 0.06f);
        Assert.True(s.Lateral(cs, moved.Position) > -edge + 1.5f);
    }

    [GameDataFact]
    public void OrbitCameraUsesTrackCorrectionAndOverturnedCarFallback()
    {
        var s = S;
        int slice = s.FindWall(true, 10);
        var (pose, car) = s.CarAt(slice, s.Slices[slice].RightEdge / 256f - 4f);
        var plain = s.OrbitCamera(false, pose, car);
        var corrected = s.OrbitCamera(true, pose, car);
        int correctedSlice = SliceOf(corrected.Position, slice);
        float edge = s.Slices[correctedSlice].RightEdge / 256f;
        Assert.True(s.Lateral(correctedSlice, corrected.Position) < edge - 1.5f,
            $"orbit camera remained in wall zone at {s.Lateral(correctedSlice, corrected.Position):F2} m (edge {edge:F2})");
        Assert.True(Vector3.Distance(pose.Position, corrected.Position) > 5.5f);

        car.UpDotN = 0;
        car.WrongWayTicks = 1;
        corrected.Update(pose, car, 1.0 / 32, raceTick: 2);
        Assert.True(corrected.Flipping);
        Assert.True(float.IsFinite(corrected.Position.X) && float.IsFinite(corrected.Position.Y) && float.IsFinite(corrected.Position.Z));
    }

    [GameDataFact]
    public void FinishCameraUsesTheOriginalWallCorrection()
    {
        var s = S;
        int slice = s.FindWall(true);
        var (pose, car) = s.CarAt(slice, 0);
        car.Flags = CarFlags.LocalHuman;
        float edge = s.Slices[slice].RightEdge / 256f;
        // Put CAN key 0 one metre inside the wall zone. Mode 0xC must run [ref:2729]
        // just like the driving views, then apply its look-at behavior.
        var cameraOffset = Scene.Dir(s.Slices[slice].Right) * (edge - 1f);
        var animation = CameraAtOffset(cameraOffset);
        var plain = new RaceCamera(0) { FinishAnimation = animation };
        var corrected = new RaceCamera(0) { Locator = s.Locator, FinishAnimation = animation };
        car.RaceState = 2;
        plain.Update(pose, car, 1.0 / 32);
        corrected.Update(pose, car, 1.0 / 32);

        Assert.Equal(RaceCamera.FinishMode, corrected.Mode);
        int correctedSlice = SliceOf(corrected.Position, slice);
        float correctedEdge = s.Slices[correctedSlice].RightEdge / 256f;
        Assert.True(s.Lateral(correctedSlice, corrected.Position) < correctedEdge - 1.5f,
            $"finish camera remained in the wall zone at {s.Lateral(correctedSlice, corrected.Position):F2} m (edge {correctedEdge:F2})");
        Assert.True(Vector3.Distance(plain.Position, corrected.Position) > 0.1f,
            "finish camera wall correction did not move its CAN position");
        Near(Vector3.Normalize(pose.Position - corrected.Position), corrected.Forward, 0.01f);
    }

    static Can CameraAtOffset(Vector3 offset)
    {
        // A one-key CAN lets the test place the finish camera deterministically without depending on a track's finish path.
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 6);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), (int)MathF.Round(offset.X * 65536));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), (int)MathF.Round(offset.Y * 65536));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), (int)MathF.Round(offset.Z * 65536));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 16384); // identity quaternion
        return Can.Parse(bytes);
    }

    static void Near(Vector3 expected, Vector3 actual, float tolerance)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, actual {actual}");

    [GameDataFact]
    public void CockpitAndHoodAreAlsoPulledFromTheWallButNeverClamped()
    {
        // The chase-position correction runs for every mode with the chase flag; the ground clamp only for modes 2 and up.
        var s = S;
        int slice = s.FindWall(true);
        var (pose, car) = s.CarAt(slice, s.Slices[slice].RightEdge / 256f - 1);
        foreach (int mode in new[] { 0, 1 })
        {
            var plain = s.Camera(mode, false, pose, car);
            var moved = s.Camera(mode, true, pose, car);
            Assert.True(s.Lateral(slice, moved.Position) < s.Lateral(slice, plain.Position) - 0.3f, $"mode {mode}");
        }
        // Underground car: the chase views are lifted to the road + 1 m, the cockpit is not.
        var (low, lowCar) = s.CarAt(slice, 0, -3);
        Assert.Equal(s.Camera(0, false, low, lowCar).Position.Y, s.Camera(0, true, low, lowCar).Position.Y, 0.001f);
        Assert.True(s.Camera(3, true, low, lowCar).Position.Y > s.Centre(slice).Y + 0.9f);
    }

    [GameDataTheory]
    [InlineData(2)] [InlineData(3)]
    public void CameraDoesNotGoUnderTheGroundBehindACarOnASteepDescent(int mode)
    {
        var s = S;
        int slice = Enumerable.Range(10, s.Slices.Length - 20).OrderByDescending(i => MathF.Abs(Scene.Dir(s.Slices[i].Forward).Y)).First();
        // level car on the steepest slice facing downhill (jump / crest): the camera behind it would be inside the hill.
        var (pose, car) = s.CarAt(slice, 0, 0, downhill: true, level: true);
        car.LocalVel = new Vec3(0, 0, 0x780000);
        var plain = s.Camera(mode, false, pose, car);
        var moved = s.Camera(mode, true, pose, car);
        int cs = SliceOf(plain.Position, slice);
        var up = Scene.Dir(s.Slices[cs].Up);
        var c = s.Centre(cs);
        // height of the slice plane under the (corrected) camera
        float ground = c.Y - (up.X * (moved.Position.X - c.X) + up.Z * (moved.Position.Z - c.Z)) / up.Y;
        Assert.True(plain.Position.Y < ground, "scene must put the unclamped camera into the hill");
        Assert.True(moved.Position.Y >= ground + 1 - 0.5f, $"camera {moved.Position.Y}, ground {ground}");
    }

    [GameDataFact]
    public void CameraStillFollowsTheInterpolatedPoseAndHandsTheMirrorTheSamePosition()
    {
        var s = S;
        int slice = s.FindWall(true);
        var (pose, car) = s.CarAt(slice, s.Slices[slice].RightEdge / 256f - 1);
        var cam = s.Camera(3, true, pose, car);
        var shifted = pose with { Position = pose.Position + pose.Forward * 0.25f };
        cam.Update(shifted, car, 1.0 / 32);
        Assert.True(Vector3.Distance(cam.Position, shifted.Position) < 6f);
        Assert.Equal(cam.Position, cam.MirrorCamera(shifted).Position);
        // looks at the car from the corrected position
        Assert.True(Vector3.Dot(cam.Forward, Vector3.Normalize(shifted.Position - cam.Position)) > 0.95f);
    }

    [GameDataFact]
    public void AutopilotLapOnTr04PullsTheCameraOutOfTheWallsWithoutJumps()
    {
        var setup = NFS.Game.Race.RaceInfo.Setup("replay");
        setup.Set(0x24, 4);
        var drive = new RaceDrive(new RaceScene(new NFS.Game.Race.RaceLoader().Load(setup)),
            withAi: true, automatic: true, countdown: false) { ShowHud = false };
        var slices = drive.Scene.Session.Slices;
        var plain = new RaceCamera(setup.Car(0).CarType);
        var fixedCam = new RaceCamera(setup.Car(0).CarType) { Locator = drive.Runner.Locator };
        foreach (var c in new[] { plain, fixedCam }) while (c.Mode != 3) c.Cycle(RenderPose.From(drive.Car.Pos, drive.Car.Orient), drive.Car);
        Vector3 lastPlain = default, lastFixed = default;
        int moved = 0, jumps = 0, insideWallBefore = 0, insideWallAfter = 0;
        float worstJump = 0;
        var picks = new List<(int Step, float Dist)>();
        for (int step = 0; step < 3200; step++)
        {
            drive.Step(WallHugger(drive.Runner, step < 1600));
            drive.SnapToHost();
            var pose = RenderPose.From(drive.Car.Pos, drive.Car.Orient);
            plain.Snap(pose, drive.Car); fixedCam.Snap(pose, drive.Car);
            if (step < 40) { lastPlain = plain.Position; lastFixed = fixedCam.Position; continue; }
            float dist = Vector3.Distance(plain.Position, fixedCam.Position);
            if (dist > 0.4f) moved++;
            float jump = Vector3.Distance(fixedCam.Position - lastFixed, plain.Position - lastPlain);
            worstJump = Math.Max(worstJump, jump);
            if (jump > 2f) jumps++;
            (lastPlain, lastFixed) = (plain.Position, fixedCam.Position);
            int s = drive.Car.Trk.Slice;
            if ((uint)s < (uint)slices.Length)
            {
                var sl = slices[s];
                var c = new Vector3(sl.X, sl.Y, sl.Z) / 65536f;
                var r = Vector3.Normalize(new Vector3(sl.Right[0], sl.Right[1], sl.Right[2]));
                float edge = sl.RightEdge / 256f;
                if (Vector3.Dot(plain.Position - c, r) > edge - 0.2f) insideWallBefore++;
                if (Vector3.Dot(fixedCam.Position - c, r) > edge - 0.2f) insideWallAfter++;
            }
            if (dist > 0.8f && (picks.Count == 0 || step - picks[^1].Step > 150)) picks.Add((step, dist));
            if (Environment.GetEnvironmentVariable("NFS_CLAMP_REVIEW_DIR") is { Length: > 0 } && picks.Count > 0 && picks[^1].Step == step)
                Review(drive, plain, fixedCam, step, picks.Count - 1);
        }
        Console.WriteLine($"CLAMP moved>{0.4f}m frames {moved}, jump>2m {jumps} (worst {worstJump:F2}), camera beyond right edge-0.2: before {insideWallBefore} after {insideWallAfter}, picks {picks.Count}");
        Assert.True(moved > 0, "the lap never brought the camera near a wall");
        Assert.True(insideWallAfter <= insideWallBefore);
        Assert.True(worstJump < 3f, $"camera jumps by {worstJump} m between two frames");
    }

    /// <summary>Autopilot that keeps the car 1.6 m inside the right (then left) road edge, scraping along the wall.</summary>
    static PlayerInput WallHugger(RaceRunner run, bool rightSide)
    {
        var c = run.Car;
        var sl = run.Session.Slices[c.Trk.Slice];
        int dir = run.Session.Backward ? -1 : 1;
        int want = FxMath.Atan2(sl.Forward[0] * dir, sl.Forward[2] * dir);
        int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        int err = ((want - have + 0x200) & 0x3FF) - 0x200;
        int target = rightSide ? sl.RightEdge * 0x100 - 0x19999 : -(sl.LeftEdge * 0x100 - 0x19999);
        int steer = Math.Clamp(err * 2 - (c.LateralOffset * dir - target * dir) / 0x4000, -127, 127);
        return new PlayerInput((sbyte)steer, 120, 0, 0);
    }

    static readonly List<FrameBuffer> ReviewFrames = [];

    static void Review(RaceDrive drive, RaceCamera plain, RaceCamera fixedCam, int step, int index)
    {
        if (index >= 2) return;
        var frames = new FrameBuffer[2];
        for (int k = 0; k < 2; k++)
        {
            frames[k] = new FrameBuffer();
            drive.Scene.Track.Render(frames[k], (k == 0 ? plain : fixedCam).Camera(), new RenderOptions(),
                drive.Scene.RenderCars, drive.Runner.DynObjects.Knocked, drive.Runner.Tick, drive.Skids.Buffer);
        }
        ReviewFrames.AddRange(frames);
        if (ReviewFrames.Count < 4) return;
        var sheet = new FrameBuffer(1280, 960);
        for (int i = 0; i < 4; i++)
            for (int y = 0; y < 480; y++)
                Array.Copy(ReviewFrames[i].Rgba, y * 640 * 4, sheet.Rgba, ((i / 2) * 480 + y) * 1280 * 4 + (i % 2) * 640 * 4, 640 * 4);
        var dir = Environment.GetEnvironmentVariable("NFS_CLAMP_REVIEW_DIR")!;
        Directory.CreateDirectory(dir);
        sheet.SavePng(Path.Combine(dir, "camera_clamp_before_after.png"));
    }
}
