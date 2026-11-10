using NFS.Data.Track;

namespace NFS.Core;

/// <summary>
/// Collisions with the road edge "walls" (collision stage prio 0x28): chassis corners tested against the wall polygons. [ref:0688]
/// A corner whose located road polygon has surface <c>flags &amp; 0x3F == 0</c> hits a wall. [ref:0689]
/// Force feedback is not ported. [ref:0690]
/// </summary>
public static class WallCollision
{
    /// <summary>Simtune index of the wall slide friction (+0x4C). [ref:0691]</summary>
    public const int StWallFriction = 19;

    /// <summary>
    /// F = fwd·(halfL/256)/256, R = right·(halfW/256)/256 (drive frame, int division); [ref:0692]
    /// base = (pos.x, prevRoadHeight, pos.z) when up·n &lt; 0xCCCD, else pos + (−hAbove − halfH)·roadUp (FX).
    /// Corners: 0 = base+F−R, 1 = base+F+R, 2 = base−R, 3 = base+R (the rear pair sits at the centre, as in the original).
    /// </summary>
    public static void CalcCorners(CarObj c)
    {
        int l = c.HalfLength / 256, w = c.HalfWidth / 256;
        var df = c.DriveFrame.Row2; var dr = c.DriveFrame.Row0;
        var f = new Vec3(df.X * l / 256, df.Y * l / 256, df.Z * l / 256);
        var r = new Vec3(dr.X * w / 256, dr.Y * w / 256, dr.Z * w / 256);
        Vec3 b;
        if (c.UpDotN < 0xCCCD) b = new Vec3(c.Pos.X, c.PrevRoadHeight, c.Pos.Z);
        else
        {
            int k = -c.HeightAboveRoad - c.HalfHeight;
            var n = c.RoadFrame.Row1;
            b = new Vec3(c.Pos.X + Fx.Mul(k, n.X), c.Pos.Y + Fx.Mul(k, n.Y), c.Pos.Z + Fx.Mul(k, n.Z));
        }
        var front = b + f;
        c.Corners[0] = front - r;
        c.Corners[1] = front + r;
        c.Corners[2] = b - r;
        c.Corners[3] = b + r;
    }

    /// <summary>
    /// On the ground the front corners (the rear ones when reversing, +0x2B8 &lt; 0) are [ref:0693]
    /// tested; airborne / parked on track 0 all four. Above 40 m/s the test point leads by v·5/256. The first corner on a
    /// wall polygon gives the normal (car road point − wall quad corner, XZ, normalised) for
    /// <see cref="WallImpulse"/>; the corner is stored in +0x170. Returns true on a hit.
    /// </summary>
    public static bool CheckCorners(CarObj c, TrackLocator loc, int trackId, ReadOnlySpan<int> st, int raceTick = 0)
    {
        int i = 0, end = 2;
        if (c.AirTicks == 0 && (trackId != 0 || (c.Flags & CarFlags.Static) == 0))
        {
            if (c.LocalVel.Z < 0) { i = 2; end = 4; }
        }
        else end = 4;
        var lead = Vec3.Zero;
        if (c.SpeedApprox > 0x280000) lead = new Vec3(c.Vel.X * 5 / 256, c.Vel.Y * 5 / 256, c.Vel.Z * 5 / 256);
        var t = TrackLocator.Clone(c.Trk);
        var g = loc.Geometry;
        for (; i < end; i++)
        {
            loc.Update(c.Corners[i] + lead, t, true);
            if ((TrackLocator.SurfaceFlags(t) & 0x3F) != 0) continue;
            var wp = t.RoadPoly == null ? g.SliceCentre(t.Slice) : t.Corners[0];
            var n = new Vec3(c.RoadPoint.X - wp.X, 0, c.RoadPoint.Z - wp.Z);
            FxMath.NormalizeBig(ref n);
            WallImpulse(c, n, g, st);
            c.LastHit = c.Corners[i];
            AiReactions.OnWallHit(c, raceTick); // after impulse and contact point [ref:0694]
            return true;
        }
        return false;
    }

    /// <summary>
    /// (c, n): if |right·n| &gt; 0.5 the normal snaps to ±slice right (s8 ×512, with the sign of the dot product). [ref:0695]
    /// dot); <c>j = min(−|n·v|/16, −0x7AE)</c>; then with the frame rows n, (0,1,0), (−n.z, 0, n.x). [ref:0696]
    /// </summary>
    public static int WallImpulse(CarObj c, Vec3 n, TrackGeometry g, ReadOnlySpan<int> st)
    {
        var sl = g.Slices[c.Trk.Slice];
        var r = new Vec3(sl.Right[0] << 9, sl.Right[1] << 9, sl.Right[2] << 9);
        int d = Vec3.Dot(r, n);
        if (Math.Abs(d) > 0x8000)
        {
            if (d > 0) n = r;
            if (d < 0) n = -r;
        }
        int vn = Vec3.Dot(n, c.Vel);
        int a = vn < 1 ? -vn : vn;
        int j = -a / 16;
        if (j > -0x7AE) j = -0x7AE;
        int half = WallSlide(c, j, new Mat3(n.X, n.Y, n.Z, 0, Fx.One, 0, -n.Z, 0, n.X), st);
        WallSpin(c, half, new Vec3(-n.Z, 0, n.X), ImpactFlagsWall, st);
        return half;
    }

    /// <summary>Impact flags of wall hits (value 4). [ref:0697]</summary>
    public const int ImpactFlagsWall = 4;

    /// <summary>
    /// (c, half, a = frame row 2 (−n.z, 0, n.x)). AI cars (+0x1F4 bit 2 clear) go to [ref:0698]
    /// <see cref="AiWallReact"/>. Local humans: +0x160 = half·6, +0x164 = 0, +0x168 = flags | 0x40000; unless half &gt; 0xEFFFF and the [ref:0699]
    /// forward speed +0x2B8 &gt; 20.0, ω += a × driveFwd (FX), negated when a·driveFwd &gt; 0.
    /// </summary>
    public static void WallSpin(CarObj c, int half, Vec3 a, int impactFlags, ReadOnlySpan<int> st = default)
    {
        if ((c.Flags & CarFlags.LocalHuman) == 0) { AiWallReact(c, half, a, impactFlags, st); return; }
        c.Impact = half * 6;
        c.ImpactOther = null;
        c.ImpactType = impactFlags | 0x40000;
        if (half > 0xEFFFF && c.LocalVel.Z > 0x140000) return;
        var f = c.DriveFrame.Row2;
        int wx = Fx.Mul(a.Y, f.Z) - Fx.Mul(a.Z, f.Y);
        int wy = Fx.Mul(a.Z, f.X) - Fx.Mul(a.X, f.Z);
        int wz = Fx.Mul(a.X, f.Y) - Fx.Mul(a.Y, f.X);
        if (Fx.Mul(a.Z, f.Z) + Fx.Mul(a.X, f.X) + Fx.Mul(a.Y, f.Y) > 0) { wx = -wx; wy = -wy; wz = -wz; }
        c.AngVel = new Vec3(c.AngVel.X + wx, c.AngVel.Y + wy, c.AngVel.Z + wz);
    }

    /// <summary>
    /// (c, j, M): n = M.row0, t = M.row2. pos.x −= j·M00, pos.y += j·M10, pos.z += j·M20. [ref:0700]
    /// original code walks the first <b>column</b> – for the wall frame (n, up, (−n.z,0,n.x)) that is pos −= j·n; for the road
    /// frame used by the edge collision it is a quirk kept as is). The normal velocity is dropped, the tangential one
    /// is reduced by |v·n|/2 · st[19]/4 when |v·t| &gt; 5.0 (not past 0); v.x = vt·M20, v.z = vt·M22, v.y kept; local
    /// velocity +0x2B0 and the speed estimate +0xBC recomputed. Returns |v·n|/2.
    /// </summary>
    public static int WallSlide(CarObj c, int j, in Mat3 m, ReadOnlySpan<int> st)
    {
        c.Pos = new Vec3(c.Pos.X - Fx.Mul(j, m.M00), c.Pos.Y + Fx.Mul(j, m.M10), c.Pos.Z + Fx.Mul(j, m.M20));
        int vn = Vec3.Dot(c.Vel, m.Row0), vt = Vec3.Dot(c.Vel, m.Row2);
        if (-vn > 0) vn = -vn;
        int half = vn / 2;
        int fr = st.Length > StWallFriction ? st[StWallFriction] / 4 : 0;
        if (vt < 1)
        {
            if (vt < -0x50000) { vt += Fx.Mul(half, fr); if (vt > 0) vt = 0; }
        }
        else if (vt > 0x50000) { vt -= Fx.Mul(half, fr); if (vt < 0) vt = 0; }
        c.Vel = new Vec3(Fx.Mul(vt, m.M20), c.Vel.Y > 0 ? 0 : c.Vel.Y, Fx.Mul(vt, m.M22));   // upward v.y dropped [ref:0701]
        c.LocalVel = new Vec3(Vec3.Dot(c.Vel, c.Orient.Row0), Vec3.Dot(c.Vel, c.Orient.Row1), Vec3.Dot(c.Vel, c.Orient.Row2));
        int ax = Math.Abs(c.Vel.X), az = Math.Abs(c.Vel.Z);
        c.SpeedApprox = ax <= az ? az + (ax >> 2) : (az >> 2) + ax;
        return half;
    }

    /// <summary>
    /// lateral offset +0xC0 = R·(pos + v/32 − centre) (R = slice right ×512); [ref:0702]
    /// car extent e = |halfL·(fwd·R)| + |halfW·(right·R)|; left hit when lat &lt; e − leftEdge·256 + 0.25 and the
    /// slice +0x1E high nibble ≠ 0 (side −1), right hit when lat &gt; rightEdge·256 − e − 0.25 and the low nibble ≠ 0
    /// (side +1); flags = that nibble. Contact +0x170 = pos ± R·(halfW/256)/256. Player crash rules: [ref:0703]
    /// nibble 6 with height &gt; 3.0 → fall (crash slice, spin); nibble 7 with |v·R| &gt; 10.0 → crash (+0x168 0x40004).
    /// Otherwise, on the ground: slide with the impulse 2·pen·side in the road frame, spin around the slice forward. [ref:0704]
    /// Airborne or tilted: plane collision with normal ±R through pos. Not ported: track 6 and 7 special ranges, FFB. Returns the side (0 = no hit). [ref:0705]
    /// </summary>
    public static int RoadEdgeCollision(CarObj c, TrackSlice[] slices, ReadOnlySpan<int> st)
    {
        var sl = slices[c.Trk.Slice];
        var r = new Vec3(sl.Right[0] << 9, sl.Right[1] << 9, sl.Right[2] << 9);
        var d = new Vec3(c.Pos.X + c.Vel.X / 32 - sl.X, c.Pos.Y + c.Vel.Y / 32 - sl.Y, c.Pos.Z + c.Vel.Z / 32 - sl.Z);
        int lat = Vec3.Dot(r, d);
        c.LateralOffset = lat;
        int eL = Math.Abs(Fx.Mul(c.HalfLength, Vec3.Dot(c.Orient.Row2, r)));
        int eW = Math.Abs(Fx.Mul(c.HalfWidth, Vec3.Dot(c.Orient.Row0, r)));
        int side = 0, pen = 0, flags = 0;
        int lim = eL + eW - sl.LeftEdge * 0x100 + 0x4000;
        if (lat < lim && sl.Surroundings >> 4 != 0) { side = -1; pen = lim - lat; flags = sl.Surroundings >> 4; }
        lim = sl.RightEdge * 0x100 - (eL + eW) - 0x4000;
        if (lim < lat && (sl.Surroundings & 0xF) != 0) { side = 1; pen = lat - lim; flags = sl.Surroundings & 0xF; }
        else if (side == 0) return 0;
        if (c.CrashSlice != 0) return 0;
        int w = c.HalfWidth / 256;
        var off = new Vec3(r.X * w / 256, r.Y * w / 256, r.Z * w / 256);
        c.LastHit = side < 1 ? c.Pos - off : c.Pos + off;
        if ((c.Flags & CarFlags.LocalHuman) != 0 || c.PsychoTicks > 0)
        {
            if (flags == 6 && c.HeightAboveRoad > 0x30000)
            {
                c.AutoResetCounter = 0x60; c.Crashing = 1; c.CrashSlice = c.Trk.Slice;
                c.AngVel = new Vec3(c.AngVel.X + 0x8000, c.AngVel.Y, c.AngVel.Z + 0x3333);
                return 0;
            }
            if (flags == 7 && Math.Abs(Vec3.Dot(c.Vel, r)) > 0xA0000)
            {
                c.ImpactType = 0x40004; c.ImpactOther = null; c.AutoResetCounter = 0x80; c.Crashing = 1;
                c.Impact = 0x1E0000; c.CrashSlice = c.Trk.Slice;
                c.AngVel = new Vec3(c.AngVel.X + 0x18000, c.AngVel.Y, c.AngVel.Z + 0x10000); // (s16 +0xF2)++
                return 0;
            }
        }
        if (c.HeightAboveRoad < 0x999A && c.UpDotN > 0xE665 && c.AirTicks == 0)
        {
            int a = pen * 2; if (a < 1) a = -(pen * 2);
            int half = WallSlide(c, side * a, c.RoadFrame, st);
            WallSpin(c, half, new Vec3(sl.Forward[0] << 9, sl.Forward[1] << 9, sl.Forward[2] << 9), flags, st);
            return side;
        }
        var n = side < 0 ? r : -r;   // airborne / tilted: plane through the car position
        PlaneCollision.CollPlane(c, ref n, c.Pos);
        if (c.Impact != 0) { c.ImpactOther = null; c.ImpactType = flags | 0x40000; }
        return side;
    }

    /// <summary>
    /// (c, half, a, flags), called for non-human cars: impact +0x160 = half·6 [ref:0706]
    /// +0x164 = 0, +0x168 = flags | 0x40000 (unless half &gt; 0xEFFFF and v·fwd +0x2B8 &gt; 20.0, then only these).
    /// Otherwise the forward row (+0xDC) turns toward the wall direction a (negated when a·driveFwd &lt; 0) by
    /// t = clamp(|+0x2B8|/16, 0x3333, 0xC000) when |a·driveFwd| > simtune[20]; otherwise it is nudged by [ref:0707]
    /// (right/128·steer)/16 (steer +0x2E4 only when the throttle byte +0x2D7 &gt; 0x32), then normalised, and the [ref:0708]
    /// right row (+0xC4) is rebuilt as up × forward.
    /// </summary>
    public static void AiWallReact(CarObj c, int half, Vec3 a, int impactFlags, ReadOnlySpan<int> st)
    {
        c.ImpactOther = null;
        if (half > 0xEFFFF && c.LocalVel.Z > 0x140000)
        {
            c.Impact = half * 6; c.ImpactType = impactFlags | 0x40000;
            return;
        }
        c.Impact = half * 6; c.ImpactType = impactFlags | 0x40000;
        var f = c.DriveFrame.Row2;
        int dot = Fx.Mul(a.X, f.X) + Fx.Mul(a.Y, f.Y) + Fx.Mul(a.Z, f.Z);
        int thr = st.Length > 20 ? st[20] : 0;
        var o = c.Orient;
        Vec3 n;
        if (Math.Abs(dot) > thr)
        {
            if (dot < 0) a = -a;
            int t = Math.Abs(c.LocalVel.Z) >> 4;
            t = t < 0xC001 ? Math.Max(t, 0x3333) : 0xC000;
            n = new Vec3(o.M20 + Fx.Mul(a.X - o.M20, t), o.M21 + Fx.Mul(a.Y - o.M21, t), o.M22 + Fx.Mul(a.Z - o.M22, t));
        }
        else
        {
            int k = c.ThrottleRamped > 0x32 ? c.Steer : 0;
            n = new Vec3(o.M20 + o.M00 / 128 * k / 16, o.M21 + o.M01 / 128 * k / 16, o.M22 + o.M02 / 128 * k / 16);
        }
        FxMath.NormalizeBig(ref n);
        o.M20 = n.X; o.M21 = n.Y; o.M22 = n.Z;
        o.M00 = Fx.Mul(o.M11, o.M22) - Fx.Mul(o.M12, o.M21);
        o.M01 = Fx.Mul(o.M12, o.M20) - Fx.Mul(o.M10, o.M22);
        o.M02 = Fx.Mul(o.M10, o.M21) - Fx.Mul(o.M11, o.M20);
        c.Orient = o;
    }

    /// <summary>On a hit +0x284 = side. For local humans in arcade mode [ref:0709]
    /// the rpm is recomputed from the forward speed, +0x310/+0x324/+0x328/+0x2F4 = 0; in sim mode (mode 0) [ref:0710]
    /// the landing reset is applied instead. [ref:0711]</summary>
    public static int RoadEdgeCheck(CarObj c, TrackSlice[] slices, ReadOnlySpan<int> st, bool arcade, int[]? gearRpmFactor = null, int redline = 0)
    {
        int side = RoadEdgeCollision(c, slices, st);
        if (side == 0) return 0;
        if ((c.Flags & CarFlags.LocalHuman) != 0)
        {
            if (!arcade) CarPost.DriveLandingReset(c, 0);                      // != 1 [ref:0712]
            else if (gearRpmFactor != null) ArcadeWallSlowdown(c, gearRpmFactor, redline);
        }
        c.EdgeHit = side;
        return side;
    }

    /// <summary>(1:1 with the original code): +0x2B8 = v·fwd (each product FX-rounded) [ref:0713]
    /// outside neutral: rpm = DivPow2(redline · FX(+0x2B8, specs+0x168[gear]), 16) (32-bit product, wraps);
    /// +0x310/+0x324/+0x328/+0x2F4 = 0.</summary>
    public static void ArcadeWallSlowdown(CarObj c, int[] gearRpmFactor, int redline)
    {
        var v = c.Vel; var f = c.DriveFrame;
        int vz = Fx.Mul(v.X, f.M20) + Fx.Mul(v.Y, f.M21) + Fx.Mul(v.Z, f.M22);
        c.LocalVel = new Vec3(c.LocalVel.X, c.LocalVel.Y, vz);
        if (c.Gear != 1) c.Rpm = Fx.DivPow2(unchecked(redline * Fx.Mul(vz, gearRpmFactor[c.Gear])), 16);
        c.SlipAngle = 0; c.Skid324 = 0; c.Skid328 = 0; c.SlipState = 0;
    }
}
