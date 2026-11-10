using NFS.Data.Track;

namespace NFS.Core;

/// <summary>
/// <see cref="IRoadQuery"/> on a real track: locator update of the car, the road frame, [ref:0651]
/// gravity scale, front ground probe, tunnel ceiling [ref:0652]
/// ffb keeps the interface default. [ref:0653]
/// </summary>
public sealed class TrackRoadQuery(TrackLocator locator, int trackId, int[]? simTune = null) : IRoadQuery
{
    public TrackLocator Locator => locator;
    public int TrackId => trackId;
    public int[] SimTune { get; } = simTune ?? new int[29];

    /// <summary>Surfaces on which the probe runs (int[16]). [ref:0654]</summary>
    public static readonly int[] ProbeSurfaces = [1, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 1];

    /// <summary>track 7, slices 0x145..0x22F → 3.0, else 1.0. [ref:0655]</summary>
    public int GravityScale(CarObj c) => trackId == 7 && c.Trk.Slice >= 0x145 && c.Trk.Slice <= 0x22F ? 3 * Fx.One : Fx.One;

    /// <summary>The local player the car measures its distance to. Null [ref:0656]
    /// (nobody set it) keeps the LOD neutral: distance 0, rails off, every car updated at 32 Hz.</summary>
    public CarObj? LocalHuman { get; set; }
    /// <summary>3+ humans → the original sets distance 0 and rails off. [ref:0657]</summary>
    public int HumanCount { get; set; } = 1;
    /// <summary>Second human (split screen, <see cref="HumanCount"/> == 2): the LOD distance is the minimum over both humans. [ref:0658]</summary>
    public CarObj? OtherHuman { get; set; }

    /// <summary>
    /// Per-car update (task prio 6): first the LOD, then, when not [ref:0659]
    /// crashed (or still on a polygon), locate (pos, car+8, full = !rails), the [ref:0660]
    /// road frame when TrackPos+0x6E ≠ 0, and off rails the surface under the car (+0x180 byte, +0x184 = byte &amp; 0x3F).
    /// </summary>
    public void UpdateCar(CarObj c)
    {
        if (c.Active == 0) return;
        LodUpdate(c);
        if (c.CrashSlice != 0 && c.Trk.RoadPoly == null) return;
        locator.Update(c.Pos, c.Trk, c.Rail == 0);
        if (c.Trk.Unk6E != 0) UpdateRoadFrame(c, locator.Geometry.Slices);
        if (c.Rail == 0)
        {
            int b = TrackLocator.SurfaceFlags(c.Trk);
            c.SurfaceByte = (byte)b;
            c.SurfaceType = b & 0x3F;
        }
    }

    static int Metric(CarObj c, CarObj h)
    {
        int dx = Math.Abs(c.Pos.X - h.Pos.X), dz = Math.Abs(c.Pos.Z - h.Pos.Z);
        if (dz < dx) dz >>= 2; else dx >>= 2;
        return dx + dz;
    }

    /// <summary>
    /// Distance to the nearest local player into <c>+0x88</c> (metric [ref:0661]
    /// <c>max(|dx|,|dz|) + min/4</c>, at least 4.0 m per slice of track distance) and, above 244 m, the [ref:0662]
    /// simplified "on rails" mode <c>+0x8C</c> (flat surface, ω.x/ω.z zeroed, only keeps the car on the [ref:0663]
    /// road). Coming back under 244 m the car is put back on the road (rails clear → locate + road frame +
    /// road height +0x10C, effects and crash slice cleared). [ref:0664]
    /// </summary>
    public void LodUpdate(CarObj c)
    {
        if (LocalHuman is not { } h || HumanCount > 2) { c.Rail = 0; c.DistToPlayer = 0; return; }
        int d = Metric(c, h);
        int sliceDistance = TrackLocator.SliceDistance(c.Trk.Slice, h.Trk.Slice, locator.Geometry.Slices.Length);
        if (HumanCount == 2 && OtherHuman is { } other)
        {
            d = Math.Min(d, Metric(c, other));
            sliceDistance = Math.Min(sliceDistance, TrackLocator.SliceDistance(c.Trk.Slice, other.Trk.Slice, locator.Geometry.Slices.Length));
        }
        int slice = sliceDistance * RailSlice;
        if (slice > d) d = slice;
        c.DistToPlayer = d;
        if (d < RailDistance)
        {
            if (c.Rail != 0)
            {
                c.Rail = 0;
                locator.Update(c.Pos, c.Trk, true);                       // [ref:0665]
                UpdateRoadFrame(c, locator.Geometry.Slices);
                SnapToRoad(c);                                            // [ref:0666]
                int rh = Newton.RoadHeightAtCar(c);
                c.SurfaceByte = 1;
                c.SurfaceType = 1;
                c.RoadVySmoothed = 0;
                c.PrevRoadHeight = rh;
                c.Impact = 0;
                c.Pos.Y = c.PrevRoadHeight + c.HalfHeight;
                c.ImpactOther = null;
                int hh = Newton.HeightAboveRoad(c, c.RoadFrame.Row1, c.RoadPoint);
                c.HeightAboveRoad = 0;
                c.Pos.Y -= hh;
                c.CrashSlice = 0;                                         // [ref:0667]
                c.Crashing = 0;
            }
            return;
        }
        if (c.Rail != 1)
        {
            c.SurfaceByte = 1;
            c.SurfaceType = 1;
        }
        c.AngVel = new Vec3(0, c.AngVel.Y, 0);
        c.Rail = 1;
    }

    /// <summary>On a hit, clear the impact [ref:0668]
    /// locate the car on the track again.</summary>
    private void SnapToRoad(CarObj c)
    {
        if (WallCollision.RoadEdgeCollision(c, locator.Geometry.Slices, SimTune) == 0) return;
        c.Impact = 0;
        c.ImpactOther = null;
        locator.Update(c.Pos, c.Trk, c.Rail == 0);
        UpdateRoadFrame(c, locator.Geometry.Slices);
    }

    /// <summary><c>&gt; 2</c> is the "no distance" case and 0xF40001 = 244.0 m is the rail [ref:0669]
    /// threshold; one slice of track distance counts as 4.0 m (0x40000).</summary>
    public const int RailDistance = 0xF40001;
    private const int RailSlice = 0x40000;

    /// <summary>
    /// Locates the point halfLength ahead (c, N): a step in the road [ref:0670]
    /// normal smooths N toward the VROAD normal V of the front polygon, a big step against the motion is a wall hit
    /// (with H = V.xz). The original jumps to the wall branch with an uninitialised V when the [ref:0671]
    /// front point is off the road; that path is skipped here.
    /// </summary>
    public void FrontGroundProbe(CarObj c, ref Vec3 n)
    {
        if ((uint)c.SurfaceType >= ProbeSurfaces.Length || ProbeSurfaces[c.SurfaceType] == 0) return;
        var g = locator.Geometry;
        if (c.Trk.Xbid6Slice is SliceRoad rec && rec.AltSlice1 + rec.AltSlice2 != -2)
        {
            var sl = g.Slices[c.Trk.Slice];
            int w = Math.Max(sl.LeftEdge << 8, sl.RightEdge << 8);
            if (Math.Abs(Math.Abs(c.LateralOffset) - w) <= 0x30000) return;
        }
        var t = TrackLocator.Clone(c.Trk);
        int h = c.HalfLength / 256;
        var f = c.Orient.Row2;
        locator.Update(new Vec3(c.Pos.X + f.X * h / 256, c.Pos.Y + f.Y * h / 256, c.Pos.Z + f.Z * h / 256), t, true);
        if (t.RoadPoly == null) return; // (crashSlice ≠ 0 → return; else WALL with garbage V – skipped)
        if (t.Unk6E == 0 || t.VRoad is not short[] vr) return;
        var v = new Vec3(vr[0] * 2, vr[1] * 2, vr[2] * 2);
        int d = Vec3.Dot(v, n);
        if (d >= 0xFAE1) return;
        if (d < 0xDEB8)
        {
            int vn = Vec3.Dot(c.Vel, v);
            if (vn < 0 && v.Y < n.Y && (c.SpeedApprox < 0x1B0000 || d < 0xB333) && c.LocalVel.Z >= 0)
            {
                var hn = new Vec3(v.X, 0, v.Z);
                FxMath.Normalize(ref hn);
                WallCollision.WallImpulse(c, hn, g, SimTune);
                return;
            }
        }
        n = new Vec3((n.X + v.X) / 2, (n.Y + v.Y) / 2, (n.Z + v.Z) / 2);
        FxMath.Normalize(ref n);
    }

    /// <summary>Plane collision; forwards to <see cref="PlaneCollision.CollPlane"/>. [ref:0672]</summary>
    public void CollPlane(CarObj c, Vec3 n, Vec3 p) => PlaneCollision.CollPlane(c, ref n, p);

    /// <summary>Arcade mode used by <see cref="LandingReset"/> (the runner only drives the arcade model). [ref:0673]</summary>
    public int ArcadeMode { get; init; } = 1;

    /// <summary>Landing reset of a local human. [ref:0674]</summary>
    public void LandingReset(CarObj c) => CarPost.DriveLandingReset(c, ArcadeMode);

    /// <summary>Slice +0x1E nibble 2 (either side) → ceiling 10.0 above the road [ref:0675]
    /// (track 4: slices 0x573..0x59F 24.0, 0x3ED..0x429 14.0; track 8: 20.0). Above it: plane collision with normal −roadUp at the road point [ref:0676]
    /// then the angular velocity is clamped. [ref:0677]</summary>
    public void TunnelCeiling(CarObj c, int roadHeight)
    {
        int s = c.Trk.Slice;
        var sl = locator.Geometry.Slices[s];
        if (sl.Surroundings >> 4 != 2 && (sl.Surroundings & 0xF) != 2) return;
        int ceil = 0xA0000;
        if (trackId == 4)
        {
            if (s >= 0x573 && s <= 0x59F) ceil = 0x180000;
            else if (s > 0x3EC && s < 0x42A) ceil = 0xE0000;
        }
        else if (trackId == 8) ceil = 0x140000;
        if (ceil + roadHeight < c.Pos.Y)
        {
            var n = -c.RoadFrame.Row1;
            PlaneCollision.CollPlane(c, ref n, c.RoadPoint);
            Newton.ClampAngVel(c);
        }
    }

    private static int S8(sbyte v) => v << 9;

    /// <summary>
    /// Road point (+0x13C) = quad corner 0 (trk+0x20) when not on rails, the block is [ref:0678]
    /// present and trk.poly ≠ 0, else the slice centre. Vectors: VROAD (s16 ×2: up +0..+4, fwd +6..+0xA) when on a
    /// polygon, else slice s8 ×512 (up +0xC, fwd +0xF); right = up × fwd (FX) – on rails all 9 from the slice.
    /// Road heading (+0x148) is taken from the slice. [ref:0679]
    /// </summary>
    public static void UpdateRoadFrame(CarObj c, TrackSlice[] slices)
    {
        if (c.Active == 0) return;
        var sl = slices[c.Trk.Slice];
        bool rail = c.Rail != 0;
        bool onPoly = !rail && c.Trk.RoadPoly != null;
        c.RoadPoint = onPoly ? c.Trk.Corners[0] : new Vec3(sl.X, sl.Y, sl.Z);
        if (rail)
        {
            c.RoadFrame = new Mat3(S8(sl.Right[0]), S8(sl.Right[1]), S8(sl.Right[2]),
                S8(sl.Up[0]), S8(sl.Up[1]), S8(sl.Up[2]), S8(sl.Forward[0]), S8(sl.Forward[1]), S8(sl.Forward[2]));
        }
        else
        {
            Vec3 up, fwd;
            if (onPoly && c.Trk.VRoad is short[] vr)
            {
                up = new(vr[0] * 2, vr[1] * 2, vr[2] * 2);
                fwd = new(vr[3] * 2, vr[4] * 2, vr[5] * 2);
            }
            else
            {
                up = new(S8(sl.Up[0]), S8(sl.Up[1]), S8(sl.Up[2]));
                fwd = new(S8(sl.Forward[0]), S8(sl.Forward[1]), S8(sl.Forward[2]));
            }
            var right = new Vec3(Fx.Mul(up.Y, fwd.Z) - Fx.Mul(up.Z, fwd.Y), Fx.Mul(up.Z, fwd.X) - Fx.Mul(up.X, fwd.Z),
                Fx.Mul(up.X, fwd.Y) - Fx.Mul(up.Y, fwd.X));
            c.RoadFrame = new Mat3(right.X, right.Y, right.Z, up.X, up.Y, up.Z, fwd.X, fwd.Y, fwd.Z);
        }
        c.RoadHeading = SliceHeading(slices, c.Trk.Slice);
    }

    /// <summary>Heading of (dx, dz) to the next slice (wrapping), 0x400 = 360°. [ref:0680]</summary>
    public static int SliceHeading(TrackSlice[] slices, int s)
    {
        int n = slices.Length, t = s + 1 >= n ? s + 1 - n : s + 1;
        return FxMath.Atan2(slices[t].X - slices[s].X, slices[t].Z - slices[s].Z);
    }

    /// <summary>(Pos − slice centre) · road right (+0x118), every product a rounded FX [ref:0681]
    /// (the sum is <c>FX(dx,R0) + FX(dy,R1) + FX(dz,R2)</c> in that order). Written to car+0x3AC by the per-car progress
    /// task; read by the AI drive step. [ref:0682]</summary>
    public static int LateralOffset(CarObj c, TrackSlice[] slices)
    {
        var sl = slices[c.Trk.Slice];
        return Fx.Dot(c.Pos.X - sl.X, c.Pos.Y - sl.Y, c.Pos.Z - sl.Z,
            c.RoadFrame.M00, c.RoadFrame.M01, c.RoadFrame.M02);
    }

    /// <summary>
    /// Returns a heading difference in ±0x200. d = |road heading +0x148 − car [ref:0683]
    /// heading +0x204| folded to ≤ 0x200 (distance to driving backwards); the road heading is then taken 15 slices
    /// <b>ahead</b> (d ≤ 0x100) or 15 slices <b>behind</b> (d &gt; 0x100, i.e. the car points away from the slice chain —
    /// on a backward track that is the real direction of travel) and the difference is normalised to ±0x200.
    /// Called at the top of the step, so +0x204 is the value written earlier in the same tick [ref:0684]
    /// (of the drive frame's forward) and +0x148 is this tick's. [ref:0685]
    /// </summary>
    public static int RoadCurveAhead(CarObj c, TrackSlice[] slices)
    {
        int d = c.RoadHeading - c.Heading;
        if (d <= 0) d = -d;
        int f = Fx.Angle10Full - d;
        if (d > f) d = f;
        int n = slices.Length, t;
        if (d > 0x100) { t = c.Trk.Slice - 0xF; if (t < 0) t += n; }
        else { t = c.Trk.Slice + 0xF; if (t >= n) t -= n; }
        int rc = SliceHeading(slices, t) - c.RoadHeading;
        if (rc > 0x200) rc -= Fx.Angle10Full;
        else if (rc < -0x200) rc += Fx.Angle10Full;
        return rc;
    }
}
