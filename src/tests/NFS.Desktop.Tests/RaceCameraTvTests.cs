using System.Numerics;
using NFS.Core;
using NFS.Data.Track;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class RaceCameraTvTests
{
    static readonly RenderPose Origin = new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    /// <summary>Road running along +Z every 10 m. Right = +X (lateral), Up = +Y (height). Lanes 0x11, HalfWidthLeft 8 (4 m),
    /// HalfWidthRight 12 (6 m). <paramref name="flat"/> zeroes Right/Up and the half widths, so a station sits exactly on its centre.</summary>
    static TrackSlice[] Road(int count = 100, Func<int, ushort>? mask = null, bool flat = false) => Enumerable.Range(0, count).Select(i => new TrackSlice(
        0, 0, i * 10 * Fx.One,
        flat ? [0, 0, 0] : [0, 127, 0], [0, 0, 127], flat ? [0, 0, 0] : [127, 0, 0],
        0, 0, (ushort)(mask?.Invoke(i) ?? 1), 0, 0, 0, 0x11, (byte)(flat ? 0 : 8), (byte)(flat ? 0 : 12), 0, 0)).ToArray();

    static CarObj Car(int slice, int lateral = 0) => new()
    {
        UpDotN = Fx.One, Flags = CarFlags.LocalHuman, Orient = Mat3.Identity, RoadFrame = Mat3.Identity,
        Trk = new TrackPos { Slice = slice }, LateralOffset = lateral, LocalVel = new Vec3(0, 0, Fx.One),
    };

    static RaceCamera Tv(TrackSlice[] slices, bool split = false)
    {
        var camera = new RaceCamera(0) { OrbitEnabled = true, Split = split, Slices = slices };
        var car = Car(0);
        camera.Cycle(Origin, car); // 2 → 3
        camera.Cycle(Origin, car); // 3 → 4
        camera.Cycle(Origin, car); // 4 → 5
        return camera;
    }

    /// <summary>Flat road, TV on station 30 with zoom enabled, camera <paramref name="distance"/> m short of the station.</summary>
    static (RaceCamera Camera, RenderPose Pose, CarObj Car) Zoomed(float distance, bool split = false)
    {
        var camera = Tv(Road(flat: true), split);
        var car = Car(16); // (16 + 15) rounds down to station 30 and toggles the zoom flag on
        var pose = new RenderPose(new Vector3(0, 0, 300 - distance), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        camera.Snap(pose, car);
        return (camera, pose, car);
    }

    [Fact]
    public void StationsAdvanceEveryThirtySlicesWrapAndToggleZoomAtEachChange()
    {
        var camera = Tv(Road());
        Assert.Equal(5, camera.Mode);
        // starts with zoom on; the first station set (from −1) toggles it off. [ref:2878]
        Assert.Equal(0, camera.TvStationSlice);
        Assert.False(camera.TvZoomEnabled);

        var car = Car(44);
        camera.Snap(Origin, car);
        Assert.Equal(30, camera.TvStationSlice);
        Assert.True(camera.TvZoomEnabled);

        car.Trk.Slice = 45;
        camera.Snap(Origin, car);
        Assert.Equal(60, camera.TvStationSlice);
        Assert.False(camera.TvZoomEnabled);

        car.Trk.Slice = 94; // (94 + 15) wraps to 9, rounds down to station 0
        camera.Snap(Origin, car);
        Assert.Equal(0, camera.TvStationSlice);
        Assert.True(camera.TvZoomEnabled);

        Assert.Equal(0, camera.TvOrientation); // +0x50 is zero-initialised and stays zero under negation
    }

    [Fact]
    public void StationChangeKeepsTheCameraUpright()
    {
        var camera = Tv(Road());
        camera.Snap(Origin, Car(44)); // station 30
        Assert.True(camera.Up.Y > 0.9f, $"Up {camera.Up}");
        camera.Snap(Origin, Car(45)); // station 60, orientation negation must not flip the view
        Assert.Equal(60, camera.TvStationSlice);
        Assert.True(camera.Up.Y > 0.9f, $"Up {camera.Up}");
    }

    [Fact]
    public void StationPlacesTheTvOnTheSliceRightVectorAndThreeMetresAboveUp()
    {
        var camera = Tv(Road());
        camera.Snap(Origin, Car(44)); // station 30, right side (no mask gap, lateral 0)
        Assert.True(camera.TvSide);
        Near(300, camera.Position.Z);
        Near(6 * 127 / 128f, camera.Position.X); // lanes 1 × HalfWidthRight 12 × 0.5 m along Right (+X)
        Near(3 * 127 / 128f, camera.Position.Y); // 3 m along Up (+Y)
    }

    static void Near(float expected, float actual) => Assert.True(MathF.Abs(expected - actual) < 0.02f, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(0, true)]      // no bits
    [InlineData(1, true)]      // one bit
    [InlineData(3, true)]      // run from bit 0
    [InlineData(0x0C, true)]   // run at bit 2
    [InlineData(0x0180, true)] // run at bits 7–8
    [InlineData(0x7000, true)] // run at bits 12–14
    [InlineData(0x8000, true)] // top bit
    [InlineData(0xFFFF, true)] // full run
    [InlineData(5, false)]     // gap between bits 0 and 2
    [InlineData(0x0A, false)]  // gap between bits 1 and 3
    [InlineData(0x0101, false)] // gap at bits 0 and 8
    [InlineData(0x8001, false)] // gap at bits 0 and 15
    public void SideFollowsTheLateralOffsetOnlyWhenTheSliceMaskHasAGap(int mask, bool rightSide)
    {
        var camera = Tv(Road(mask: _ => (ushort)mask));
        camera.Snap(Origin, Car(44, lateral: -Fx.One)); // negative lateral: the left side only when a gap is present
        Assert.Equal(rightSide, camera.TvSide);
    }

    [Fact]
    public void OppositeLateralOffsetsPickOppositeRoadSidesOnAMaskGapAtTheSameForwardVelocity()
    {
        var slices = Road(mask: i => i == 44 ? (ushort)5 : (ushort)1);

        var right = Tv(slices);
        right.Snap(Origin, Car(44, lateral: Fx.One));
        Assert.True(right.TvSide);
        Assert.True(right.Position.X > 0);

        var left = Tv(slices);
        left.Snap(Origin, Car(44, lateral: -Fx.One));
        Assert.False(left.TvSide);
        Assert.True(left.Position.X < 0);
    }

    [Fact]
    public void NewStationDoesNotInventAnAlternatingRoadSide()
    {
        var camera = Tv(Road());
        camera.Snap(Origin, Car(44, lateral: -Fx.One)); // station 30
        Assert.True(camera.TvSide);
        Assert.True(camera.Position.X > 0);

        camera.Snap(Origin, Car(45, lateral: -Fx.One)); // station 60: still the right side, no gap anywhere
        Assert.Equal(60, camera.TvStationSlice);
        Assert.True(camera.TvSide);
        Assert.True(camera.Position.X > 0);
    }

    [GameDataFact]
    public void CameraLocatorSliceMaskDecidesTheSideBeforeTheCarSliceMask()
    {
        var track = new TrackRenderer(6);
        var slices = track.Slices;
        var locator = new TrackLocator(new TrackGeometry(track.Trk, slices));
        int n = slices.Length;
        // A mask has a gap when its set bits, shifted down to bit 0, are not of the form 2^k − 1.
        static bool Gap(ushort m) => m != 0 && ((m >> BitOperations.TrailingZeroCount(m)) & ((m >> BitOperations.TrailingZeroCount(m)) + 1)) != 0;

        RaceCamera Setup(int station, int lateral)
        {
            var camera = new RaceCamera(0) { OrbitEnabled = true, Locator = locator, Slices = slices };
            var sl = slices[station];
            var pose = new RenderPose(new Vector3(sl.X, sl.Y, sl.Z) / 65536f, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
            var car = Car(station, lateral);
            camera.Cycle(pose, car); // 2 → 3
            camera.Cycle(pose, car); // 3 → 4
            camera.Cycle(pose, car); // 4 → 5
            camera.Snap(pose, car); // retained camera slice comes from this position
            return camera;
        }

        // Find a station whose retained camera slice has a mask gap, and a car slice on the same station with no gap.
        for (int station = 30; station + 15 < n; station += 30)
        {
            var probe = Setup(station, lateral: Fx.One);
            int located = probe.TvLocatorSlice;
            if (located < 0 || !Gap(slices[located].Mask)) continue;
            int carSlice = Enumerable.Range(station - 15, 30).Select(i => (i + n) % n)
                .FirstOrDefault(c => !Gap(slices[c].Mask) && !Gap(slices[(c + n - 1) % n].Mask), -1);
            if (carSlice < 0) continue;
            // Each side is decided by the lateral offset of the car. Only the camera slice carries the gap.
            var right = Setup(station, lateral: Fx.One);
            right.Snap(Origin, Car(carSlice, lateral: Fx.One));
            Assert.Equal(located, right.TvLocatorSlice);
            Assert.True(right.TvSide);
            var left = Setup(station, lateral: Fx.One);
            left.Snap(Origin, Car(carSlice, lateral: -Fx.One));
            Assert.False(left.TvSide);
            return;
        }
        Assert.Fail("no station with a camera-locator mask gap on TR06");
    }

    [Fact]
    public void ZoomUsesStrictSixMetreThresholdAndOriginalIntegerRounding()
    {
        var camera = Tv(Road(flat: true));
        var car = Car(16); // station 30 with zoom on
        camera.Snap(new RenderPose(new Vector3(0, 0, 294), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), car);
        FxMath.SinCosTab((36 << 10) / 360, out int baseSin, out int baseCos);
        var atThreshold = camera.Camera();
        Assert.Equal(320, atThreshold.Fx); // d = 6 m is not above 0x60000
        Assert.Equal(-(240 * baseCos / baseSin), atThreshold.Fy);

        var pose = new RenderPose(new Vector3(0, 0, 291), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        camera.Snap(pose, car);
        var zoomed = camera.Camera();
        int d = 9 * Fx.One;
        Assert.Equal((int)((long)320 * d / 0x60000), zoomed.Fx);
        int vertical = (int)((long)240 * d / 0x60000);
        Assert.Equal(-Fx.Mul(vertical, 0x1592C), zoomed.Fy);

        pose = pose with { Position = new Vector3(0, 0, 177) };
        camera.Snap(pose, car);
        int farD = 123 * Fx.One;
        Assert.Equal(unchecked(320 * farD) / 0x60000, camera.Camera().Fx); // the product wraps to the low 32 bits before the division
    }

    [Fact]
    public void SplitViewKeepsTheSplitLensAndIgnoresTheDistanceZoom()
    {
        var near = Zoomed(9, split: true);
        var far = Zoomed(123, split: true);
        Assert.Equal(5, near.Camera.Mode);
        Assert.True(near.Camera.TvZoomEnabled);

        var split = SplitLayout.CreateCamera();
        Assert.Equal(split.Fx, near.Camera.Camera().Fx);
        Assert.Equal(split.Fx, far.Camera.Camera().Fx);
        Assert.Equal(split.Fy, near.Camera.Camera().Fy);
        Assert.Equal(split.Fy, far.Camera.Camera().Fy);
        Assert.Equal(240, near.Camera.Camera().Height); // viewport height of the half screen
        Assert.Equal(new Vector3(0, 0, 300), near.Camera.Position);
    }

    [Fact]
    public void MirrorKeepsItsOwnLensWhileTheMainTvViewIsZoomed()
    {
        var (camera, pose, _) = Zoomed(9);
        var mirror = camera.MirrorCamera(pose);
        FxMath.SinCosTab((68 << 10) / 360, out int sx, out int cx);
        Assert.Equal(225 * cx / sx, mirror.Fx);
        Assert.NotEqual(camera.Camera().Fx, mirror.Fx);
    }

    [Fact]
    public void CrashAndResetRestoreTheNormalLensAndTheZoomReturnsAfterThem()
    {
        var (camera, pose, car) = Zoomed(9);
        var normal = new Camera(640, 480, 45, 36, 0);
        var zoomedFx = camera.Camera().Fx;
        Assert.NotEqual(normal.Fx, zoomedFx);

        car.CrashSlice = 1;
        camera.Snap(pose, car);
        Assert.True(camera.Crashing);
        Assert.Equal(9, camera.DisplayMode);
        Assert.Equal(normal.Fx, camera.Camera().Fx);
        Assert.Equal(normal.Fy, camera.Camera().Fy);

        car.CrashSlice = 0;
        camera.Snap(pose, car);
        Assert.Equal(5, camera.DisplayMode);
        Assert.Equal(zoomedFx, camera.Camera().Fx);

        car.ManualResetLock = 1;
        camera.Snap(pose, car);
        Assert.Equal(normal.Fx, camera.Camera().Fx);
        Assert.Equal(normal.Fy, camera.Camera().Fy);

        car.ManualResetLock = 0;
        camera.Snap(pose, car);
        Assert.Equal(zoomedFx, camera.Camera().Fx);
    }

    [Fact]
    public void LookingBehindLeavesTheTvViewUntouched()
    {
        var (camera, pose, car) = Zoomed(9);
        var position = camera.Position;
        var forward = camera.Forward;
        var up = camera.Up;

        camera.SetLookBehind(true, pose, car);
        Assert.True(camera.LookingBehind);
        Assert.Equal(5, camera.DisplayMode);
        Assert.Equal(position, camera.Position);
        Assert.Equal(forward, camera.Forward);
        Assert.Equal(up, camera.Up);

        camera.Update(pose, car, 0); // dt 0 does nothing
        camera.SetLookBehind(false, pose, car);
        Assert.Equal(position, camera.Position);
        Assert.Equal(forward, camera.Forward);
        Assert.Equal(up, camera.Up);
    }

    [GameDataFact]
    public void LocatingRoadsideStationsDoesNotClampThemOntoTheRoad()
    {
        var track = new TrackRenderer(4);
        var locator = new TrackLocator(new TrackGeometry(track.Trk, track.Slices));
        int movedByChaseCorrection = 0;
        for (int station = 0; station < track.Slices.Length; station += 30)
        {
            var slice = track.Slices[station];
            var pose = Origin with { Position = Camera.SliceCentre(slice) };
            var car = Car(station, Fx.One);
            var raw = Tv(track.Slices);
            raw.Snap(pose, car);
            var located = Tv(track.Slices);
            located.Locator = locator;
            located.Snap(pose, car);
            Assert.Equal(raw.Position, located.Position);
            Assert.InRange(located.TvLocatorSlice, 0, track.Slices.Length - 1);
            located.Update(pose, car, 1.0 / 64);
            Assert.Equal(raw.Position, located.Position);

            var clamped = raw.Position;
            new CameraTrackCorrector(locator).Correct(ref clamped, pose.Position, station, true);
            if (Vector3.Distance(clamped, raw.Position) > 0.001f) movedByChaseCorrection++;
        }
        Assert.True(movedByChaseCorrection > 0, "Fixture must include stations that chase correction would move.");
    }

    [GameDataFact]
    public void CrashDriftKeepsTheCameraLocatorCurrentForTheNextTvFrame()
    {
        var track = new TrackRenderer(6);
        var locator = new TrackLocator(new TrackGeometry(track.Trk, track.Slices));
        var camera = Tv(track.Slices);
        camera.Locator = locator;
        var pose = Origin with { Position = Camera.SliceCentre(track.Slices[300]) };
        var car = Car(300, Fx.One);
        camera.Snap(pose, car);
        int before = camera.TvLocatorSlice;
        camera.Slices = null; // Exercise locator history independently of crash road-edge movement constraints.
        car.CrashSlice = 1;
        car.Trk.Slice = 304;
        pose = pose with { Position = Camera.SliceCentre(track.Slices[304]) };
        for (int i = 0; i < 20; i++) camera.Update(pose, car, 0.125);
        var expected = new TrackPos { Slice = before };
        locator.Update(CameraTrackCorrector.ToFixed(camera.Position), expected, true);
        Assert.NotEqual(before, expected.Slice);
        Assert.Equal(expected.Slice, camera.TvLocatorSlice);
    }

    [Fact]
    public void LeavingTvRestoresNormalProjection()
    {
        var (camera, pose, car) = Zoomed(9);
        Assert.NotEqual(new Camera().Fx, camera.Camera().Fx);
        camera.Cycle(Origin, car); // 5 → 6 (camera along the track: plain projection)
        Assert.Equal(6, camera.Mode);
        Assert.Equal(new Camera().Fx, camera.Camera().Fx);
        Assert.Equal(new Camera().Fy, camera.Camera().Fy);
        camera.Cycle(Origin, car); // 6 → 7
        Assert.Equal(7, camera.Mode);
        Assert.Equal(new Camera().Fx, camera.Camera().Fx);
        Assert.Equal(new Camera().Fy, camera.Camera().Fy);
        camera.Cycle(Origin, car); // 7 → 0
        Assert.Equal(0, camera.Mode);
        Assert.Equal(new Camera().Fx, camera.Camera().Fx);
        Assert.Equal(new Camera().Fy, camera.Camera().Fy);
    }

    [GameDataFact]
    public void TvModeRendersARealRaceSceneAndCanWriteCoordinatorReviewPng()
    {
        var drive = new RaceDrive(new RaceScene("replay"), countdown: false, orbitCameraActivation: true);
        drive.RunAutopilot(2);
        var pose = drive.Scene.PresentPoses(drive.Clock.Accumulator / drive.Clock.Step);
        drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
        drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
        drive.Cam.Cycle(pose, drive.Car, drive.Scene.PlayerAnimation, raceTick: drive.Runner.Tick);
        Assert.Equal(5, drive.Cam.Mode);
        var frame = new FrameBuffer(640, 480);
        drive.Render(frame);
        Assert.Contains(frame.Rgba, b => b != 0);
        if (Environment.GetEnvironmentVariable("NFS_TV_PNG_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame.SavePng(Path.Combine(dir, "race_mode5_tv.png"));
        }
    }
}
