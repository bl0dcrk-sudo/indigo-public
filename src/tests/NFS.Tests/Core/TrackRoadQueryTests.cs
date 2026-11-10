using NFS.Core;
using NFS.Data.Track;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Core;

public class TrackRoadQueryTests(ITestOutputHelper output)
{
    private static TrackSlice Slice(sbyte[] up, sbyte[] fwd, sbyte[] right) =>
        new(0x10000, 0x20000, 0x30000, up, fwd, right, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void RoadFrame_FromSlice_WhenOffPolygon()
    {
        var sl = new[] { Slice([0, 126, 0], [0, 0, 126], [126, 0, 0]), Slice([0, 126, 0], [0, 0, 126], [126, 0, 0]) };
        var c = new CarObj { Active = 1 };
        c.Trk.Slice = 0;
        TrackRoadQuery.UpdateRoadFrame(c, sl);
        Assert.Equal(new Vec3(0x10000, 0x20000, 0x30000), c.RoadPoint);       // slice centre
        Assert.Equal(new Vec3(0, 126 << 9, 0), c.RoadFrame.Row1);             // up s8 ×512
        Assert.Equal(new Vec3(0, 0, 126 << 9), c.RoadFrame.Row2);
        // right = up × fwd (FX): 0xFC00² >> 16 rounded
        Assert.Equal(new Vec3(Fx.Mul(126 << 9, 126 << 9), 0, 0), c.RoadFrame.Row0);
    }

    [Fact]
    public void RoadFrame_FromVRoad_OnPolygon_AndRails()
    {
        var sl = new[] { Slice([0, 126, 0], [0, 0, 126], [126, 0, 0]), Slice([0, 126, 0], [0, 0, 126], [126, 0, 0]) };
        var c = new CarObj { Active = 1 };
        c.Trk.Slice = 0;
        c.Trk.RoadPoly = new TrackPolygon();
        c.Trk.VRoad = new short[] { 0, 0x7000, 0x1000, 0, -0x1000, 0x7000 };
        c.Trk.Corners[0] = new Vec3(5, 6, 7);
        TrackRoadQuery.UpdateRoadFrame(c, sl);
        Assert.Equal(new Vec3(5, 6, 7), c.RoadPoint);
        Assert.Equal(new Vec3(0, 0xE000, 0x2000), c.RoadFrame.Row1);           // s16 ×2
        Assert.Equal(new Vec3(0, -0x2000, 0xE000), c.RoadFrame.Row2);
        Assert.Equal(Fx.Mul(0xE000, 0xE000) - Fx.Mul(0x2000, -0x2000), c.RoadFrame.M00);

        c.Rail = 1; // rails: all 9 from the slice, point = slice centre
        TrackRoadQuery.UpdateRoadFrame(c, sl);
        Assert.Equal(new Vec3(0x10000, 0x20000, 0x30000), c.RoadPoint);
        Assert.Equal(new Vec3(126 << 9, 0, 0), c.RoadFrame.Row0);

        c.Active = 0; c.RoadPoint = Vec3.Zero; // inactive: untouched
        TrackRoadQuery.UpdateRoadFrame(c, sl);
        Assert.Equal(Vec3.Zero, c.RoadPoint);
    }

    [Fact]
    public void GravityScale_Track7Range()
    {
        var q = new TrackRoadQuery(null!, 7);
        var c = new CarObj();
        c.Trk.Slice = 0x145;
        Assert.Equal(3 * Fx.One, q.GravityScale(c));
        c.Trk.Slice = 0x230;
        Assert.Equal(Fx.One, q.GravityScale(c));
        Assert.Equal(Fx.One, new TrackRoadQuery(null!, 4).GravityScale(new CarObj { Trk = { Slice = 0x145 } }));
    }

    /// <summary>Horseshoe of 128 slices with axis-aligned segments, 1 m apart: [0..31] +z at x = 0, [32..63] +x at
    /// z = 32, [64..95] −z at x = 32, [96..127] −x at z = 32. The values are exactly 0, 0x100, 0x200 resp. [ref:3105]
    /// −0x100 on every slice except the three elbows (31 has heading 0, 63 and 127 are off-axis) — tests avoid those.</summary>
    private static TrackSlice[] Horseshoe()
    {
        var a = new List<TrackSlice>();
        for (int i = 0; i < 32; i++) a.Add(Pos(0, i));
        for (int i = 0; i < 32; i++) a.Add(Pos(i, 32));
        for (int i = 0; i < 32; i++) a.Add(Pos(32, 64 - i));
        for (int i = 0; i < 32; i++) a.Add(Pos(32 - i, 32));
        return a.ToArray();

        static TrackSlice Pos(int x, int z) =>
            new(x * Fx.One, 0, z * Fx.One, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Heading of every slice of the <see cref="Horseshoe"/> used by the tests below (the four axis-aligned
    /// legs are exact; the elbows at 63/127 are deliberately not asserted).</summary>
    [Fact]
    public void Horseshoe_Headings()
    {
        var sl = Horseshoe();
        Assert.Equal(0, TrackRoadQuery.SliceHeading(sl, 31));
        Assert.Equal(0x100, TrackRoadQuery.SliceHeading(sl, 32));
        Assert.Equal(0x100, TrackRoadQuery.SliceHeading(sl, 62));
        Assert.Equal(0x200, TrackRoadQuery.SliceHeading(sl, 64));
        Assert.Equal(0x200, TrackRoadQuery.SliceHeading(sl, 95));
        Assert.Equal(-0x100, TrackRoadQuery.SliceHeading(sl, 96));
        Assert.Equal(-0x100, TrackRoadQuery.SliceHeading(sl, 126));
    }

    /// <summary>which slice is sampled (15 ahead when |road heading +0x148 − car heading +0x204| folded to [ref:3106]
    /// ≤ 0x100, else 15 behind), the index wrap and the ±0x200 normalisation. In the game <c>roadHeading</c> is
    /// the heading of the car's slice and <c>carHeading</c> a result (−0x200..0x200). [ref:3107]</summary>
    [Theory]
    // look ahead (d ≤ 0x100); heading(15) = 0, heading(35) = 0x100, heading(105) = −0x100
    [InlineData(0, 0, 0, 0)]                 // t = 15 → 0
    [InlineData(20, 0, 0x30, 0x100)]         // t = 35 → 0x100 − 0 (the car heading only picks the slice!)
    [InlineData(20, 0, -0x30, 0x100)]        // t = 35 → 0x100, |0 − (−0x30)| still ≤ 0x100
    [InlineData(20, 0, 0x100, 0x100)]        // d = 0x100 exactly → still ahead (the branch is "> 0x100")
    [InlineData(120, -0x100, -0xE0, 0x100)]  // t = 135 − 128 = 7 → 0; 0 − (−0x100): the +15 index wraps
    [InlineData(0, 0x1F0, -0x1F0, -0x1F0)]   // raw 0x3E0 folds to 0x20 → ahead: 0 − 0x1F0
    // look behind (d > 0x100)
    [InlineData(5, 0, 0x101, -0x100)]        // t = 5 − 15 + 128 = 118 (the −x leg); ahead would give slice 20 → 0
    [InlineData(34, 0x100, -0xF0, -0x100)]   // d = 0x1F0 → t = 19 → 0; ahead would give slice 49 → 0x100 − 0x100 = 0
    // normalisation to ±0x200
    [InlineData(90, 0x180, 0x100, 0x180)]    // d = 0x80 → t = 105 → −0x100; −0x280 < −0x200 → +0x400
    [InlineData(20, -0x1C0, -0x160, -0x140)] // d = 0x60 → t = 35 → 0x100; 0x2C0 > 0x200 → −0x400
    public void RoadCurveAhead_ChoosesLookAheadOrBehind(int slice, int roadHeading, int carHeading, int expected)
    {
        var sl = Horseshoe();
        var c = new CarObj();
        c.Trk.Slice = slice;
        c.RoadHeading = roadHeading;
        c.Heading = carHeading;
        Assert.Equal(expected, TrackRoadQuery.RoadCurveAhead(c, sl));
    }

    /// <summary>(Pos − slice centre) · road right (+0x118), each product a rounded FX. [ref:3108]</summary>
    [Fact]
    public void LateralOffset_DotsThePositionWithTheRoadRight()
    {
        var sl = Horseshoe();
        var c = new CarObj();
        c.Trk.Slice = 40;                                       // slice 40 is at (8, 0, 32) m
        c.Pos = new Vec3(8 * Fx.One, 0, 32 * Fx.One);
        c.RoadFrame = Mat3.Identity;
        Assert.Equal(0, TrackRoadQuery.LateralOffset(c, sl));    // exactly on the centre line

        c.RoadFrame = new Mat3(0x8000, 0, 0x8000, 0, 0x10000, 0, 0x8000, 0, -0x8000);  // right = (0.5, 0, 0.5)
        c.Pos = new Vec3(8 * Fx.One + 0x14000, 3 * Fx.One, 32 * Fx.One + 0x4000);
        // FX(1.25, 0.5) + FX(3.0, 0) + FX(0.25, 0.5) = 0xA000 + 0 + 0x2000
        Assert.Equal(0xC000, TrackRoadQuery.LateralOffset(c, sl));

        c.RoadFrame = new Mat3(0xFFFF, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000);          // right = x, a hair short
        c.Pos = new Vec3(8 * Fx.One + 0x10001, 0, 32 * Fx.One);
        Assert.Equal(0x10000, TrackRoadQuery.LateralOffset(c, sl)); // (0x10001·0xFFFF + 0x8000) >> 16: rounds up
    }

    [GameFact]
    public void UpdateCar_SetsSurface_AndFrontProbeSmoothsNormal()
    {
        var r = TrackSessions.FreshReplay();
        var q = new TrackRoadQuery(TrackSessions.Locator(r), r.TrackNumber, r.SimTune!.For(true).Values);
        var c = r.Cars[0].Obj;
        Prepare(c);
        q.UpdateCar(c);
        Assert.Equal(2, c.Trk.State);
        Assert.Equal(((PolyAttr)c.Trk.Xbid5Entry!).Flags, c.SurfaceByte);
        Assert.Equal(c.SurfaceByte & 0x3F, c.SurfaceType);
        Assert.NotEqual(0, c.SurfaceType);

        c.SurfaceType = 0;                       // a probe surface (table entry 0 = 1)
        var road = c.RoadFrame.Row1;
        var n = road;
        q.FrontGroundProbe(c, ref n);            // same normal ahead (grid is flat): unchanged
        Assert.Equal(road, n);
        var tilted = FxMath.Normalized(new Vec3(road.X + 0x4000, road.Y, road.Z)); // ~14° off: smoothed halfway
        n = tilted;
        q.FrontGroundProbe(c, ref n);
        Assert.NotEqual(tilted, n);
        Assert.True(Vec3.Dot(n, road) > Vec3.Dot(tilted, road));
        c.SurfaceType = 1;                       // not a probe surface: untouched
        n = tilted;
        q.FrontGroundProbe(c, ref n);
        Assert.Equal(tilted, n);
    }

    /// <summary>Prepares a grid car for stepping (box dimensions as in the flat Newton tests).</summary>
    internal static void Prepare(CarObj c)
    {
        c.Active = 1;
        c.TimeScale = Fx.One;
        c.HalfWidth = 0xE000; c.HalfHeight = 0x8000; c.HalfLength = 0x20000;
        c.Radius = 0x28000;
        c.InvMass = Fx.Div(Fx.One, c.Mass);
        c.InvInertia = Fx.Div(Fx.One, c.Inertia);
        c.OrthoCount = 16;
    }

    /// <summary>One 32 Hz physics tick: locator + road frame, newton step, two 64 Hz position/rotation integrations.</summary>
    internal static void Tick(CarObj c, TrackRoadQuery q, ReadOnlySpan<int> st, int tick)
    {
        q.UpdateCar(c);
        Newton.Step(c, st, tick, q);
        NewtonIntegrate.Pos(c); NewtonIntegrate.Rot64(c);
        NewtonIntegrate.Pos(c); NewtonIntegrate.Rot64(c);
    }

    [GameFact]
    public void GridCar_StandsOnRoad()
    {
        var r = TrackSessions.FreshReplay();
        var q = new TrackRoadQuery(TrackSessions.Locator(r), r.TrackNumber);
        var st = r.SimTune!.For(true).Values;
        foreach (var car in r.Cars.Where(x => x.Setup.CarClass is 1 or 2))
        {
            var c = car.Obj;
            Prepare(c);
            int y0 = c.Pos.Y;
            for (int t = 0; t < 256; t++)
            {
                Tick(c, q, st, 0x300 + t * 2);
                int road = Newton.RoadHeightAtCar(c);
                Assert.InRange(c.Pos.Y - road, c.HalfHeight - 0x4000, c.HalfHeight + 0x10000); // never through / off the road
            }
            output.WriteLine($"car {car.Index}: y {y0:X} → {c.Pos.Y:X}, slice {c.Trk.Slice}, v {c.Vel}, air {c.AirTicks}");
            Assert.InRange(c.Pos.Y - y0, -0x8000, 0x8000);
            Assert.Equal(car.StartSlice, c.Trk.Slice);
            Assert.Equal(2, c.Trk.State);
        }
    }
}
