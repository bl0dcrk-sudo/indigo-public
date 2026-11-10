namespace NFS.Core;

/// <summary>
/// Track/subsystem services needed by <see cref="Newton.Step"/> that live outside (track loader, collisions, [ref:0479]
/// drive model, force feedback). The road frame itself (normal +0x124, point +0x13C) is read from the car – it is
/// filled during the track stage. Defaults = flat test track without effects. [ref:0480]
/// </summary>
public interface IRoadQuery
{
    /// <summary>Gravity scale: 1.0; track 7 slices 0x145..0x22F → 3.0 (only for local humans). [ref:0481]</summary>
    int GravityScale(CarObj c) => Fx.One;

    /// <summary>Ceiling collision in tunnels (slice nibble +0x1E == 2). [ref:0482]</summary>
    void TunnelCeiling(CarObj c, int roadHeight) { }

    /// <summary>Local human on wheels; may replace the normal copy <c>n</c>. [ref:0483]</summary>
    void FrontGroundProbe(CarObj c, ref Vec3 n) { }

    /// <summary>Rigid-body plane collision (collision stage). [ref:0484]</summary>
    void CollPlane(CarObj c, Vec3 n, Vec3 p) { }

    /// <summary>Called for a local human after a flight. [ref:0485]</summary>
    void LandingReset(CarObj c) { }

    /// <summary>Impact notification. [ref:0486]</summary>
    void OnHit(CarObj c) { }
}

/// <summary>Default <see cref="IRoadQuery"/> (all defaults).</summary>
public sealed class FlatRoadQuery : IRoadQuery
{
    public static readonly FlatRoadQuery Instance = new();
}

/// <summary>
/// Rigid-body step and helpers, bit-exact (verified against [ref:0487]
/// and the helper assembly). [ref:0488]
/// </summary>
public static class Newton
{
    /// <summary>Gravity per 32 Hz tick: 0x7599 = 0.4594 m/s (14.7 m/s²).</summary>
    public const int GravityPerTick = 0x7599;
    /// <summary>Height above road from which the car is airborne (0.6 m).</summary>
    public const int AirborneHeight = 0x9999;
    /// <summary>Height up to which the car is pressed onto the road (0.02 m).</summary>
    public const int SnapHeight = 0x51E;
    /// <summary>road_height_at_car result for a (nearly) vertical road normal.</summary>
    public const int NoHeight = unchecked((int)0x83000000);

    // simtune indices used here (0x74*arcade) [ref:0489]
    public const int StFlightGravity = 5, StBounceThreshold = 13, StBounceElasticity = 14, StBounceAbsorb = 15,
        StRiseSmoothing = 17, StFallSmoothing = 18;

    // ------------------------------------------------------------ road geometry (car fields only)

    /// <summary><c>ny' = |ny| &lt; 10 ? 10 : ny</c> (also for negative ny); [ref:0490]
    /// <c>y = FXDIV(-FX(nx, x-px) - FX(nz, z-pz), ny') + py</c>.</summary>
    public static int PlaneHeightAt(Vec3 pos, Vec3 n, Vec3 p)
    {
        int ny = Fx.Abs(n.Y) < 10 ? 10 : n.Y;
        return Fx.Div(-Fx.Mul(n.X, pos.X - p.X) - Fx.Mul(n.Z, pos.Z - p.Z), ny) + p.Y;
    }

    /// <summary>road normal +0x124; <c>ny &lt; 0x199A</c> → <see cref="NoHeight"/>. [ref:0491]</summary>
    public static int RoadHeightAtCar(CarObj c)
    {
        var n = c.RoadFrame.Row1;
        return n.Y < 0x199A ? NoHeight : PlaneHeightAt(c.Pos, n, c.RoadPoint);
    }

    /// <summary><c>n·(pos−p) − halfH</c>. [ref:0492]</summary>
    public static int PlaneDist(CarObj c, Vec3 n, Vec3 p) => Vec3.Dot(n, c.Pos - p) - c.HalfHeight;

    /// <summary>For parked cars (0x40): distance of the lowest box corner [ref:0493]
    /// <c>n·(pos−p) − |FX(right·n, halfW)| − |FX(up·n, halfH)| − |FX(fwd·n, halfL)|</c>.</summary>
    public static int HeightAboveRoad(CarObj c, Vec3 n, Vec3 p)
    {
        int a0 = Fx.Mul(Vec3.Dot(c.Orient.Row0, n), c.HalfWidth);
        int a1 = Fx.Mul(Vec3.Dot(c.Orient.Row1, n), c.HalfHeight);
        int a2 = Fx.Mul(Vec3.Dot(c.Orient.Row2, n), c.HalfLength);
        return Vec3.Dot(n, c.Pos - p) - Fx.Abs(a0) - Fx.Abs(a1) - Fx.Abs(a2);
    }

    // ------------------------------------------------------------ angular helpers

    /// <summary>Clamps each ω component to ±0x26666 (2.4 rev/s). [ref:0494]</summary>
    public static void ClampAngVel(CarObj c)
    {
        const int L = 0x26666;
        c.AngVel = new(Math.Clamp(c.AngVel.X, -L, L), Math.Clamp(c.AngVel.Y, -L, L), Math.Clamp(c.AngVel.Z, -L, L));
    }

    /// <summary>
    /// For non-parked cars on the ground: ω.x/ω.z set directly to tilt the car toward [ref:0495]
    /// the normal (no torques). <c>s = sign(up·n)</c>, <c>ex = up.x·s − n.x</c>, <c>ez = n.z − up.z·s</c>.
    /// </summary>
    public static void GroundImpact(CarObj c, Vec3 n, IRoadQuery road)
    {
        int s = c.UpDotN < 1 ? -1 : 1;
        var up = c.Orient.Row1;
        TiltToNormal(c, c.UpDotN, up.X * s - n.X, n.Z - up.Z * s, 0xFC28, true, road);
    }

    /// <summary>
    /// For parked cars (0x40): uses the car axis most parallel to n (up if strictly largest [ref:0496]
    /// else right if strictly largest, else forward); damping 0.95, no impact/ffb.
    /// </summary>
    public static void GroundContact(CarObj c, Vec3 n, IRoadQuery road)
    {
        int dr = Vec3.Dot(c.Orient.Row0, n), du = c.UpDotN, df = Vec3.Dot(c.Orient.Row2, n);
        int ar = Fx.Abs(dr), au = Fx.Abs(du), af = Fx.Abs(df);
        Vec3 axis; int m;
        if (ar < au && af < au) { axis = c.Orient.Row1; m = du; }
        else if (au < ar && af < ar) { axis = c.Orient.Row0; m = dr; }
        else { axis = c.Orient.Row2; m = df; }
        int s = m < 1 ? -1 : 1;
        TiltToNormal(c, m, axis.X * s - n.X, n.Z - axis.Z * s, 0xF333, false, road);
    }

    private static void TiltToNormal(CarObj c, int m, int ex, int ez, int damp, bool impact, IRoadQuery road)
    {
        int thr = (c.Flags & CarFlags.LocalHuman) != 0 ? 0x7AE : 0xA3D;
        if ((m > 0xE666 || m < -0xE666) && Fx.Abs(ex) <= thr && Fx.Abs(ez) <= thr)
        {
            c.AngVel.X = 0;
            c.AngVel.Z = 0;
            return;
        }
        if (Fx.Abs(ex) > 0x1999 || Fx.Abs(ez) > 0x1999)
        {
            c.Vel = Vec3.Scale(c.Vel, damp);
            if (impact && (Fx.Abs(ex) > 0x3333 || Fx.Abs(ez) > 0x3333))
            {
                c.Impact = Math.Min(Fx.Abs(c.Vel.X) + Fx.Abs(c.Vel.Z), 0x280000);
                c.ImpactType = 0x20003;
                road.OnHit(c);
            }
        }
        c.AngVel.X = Math.Clamp(ez, -0x4CCC, 0x4CCC);
        c.AngVel.Z = Math.Clamp(ex, -0x4CCC, 0x4CCC);
    }

    /// <summary>
    /// drive frame +0x188 (right/up=n/fwd) from the orientation and ground normal. [ref:0497]
    /// Non-humans nearly upright (k &gt; 0xE666) copy the orientation and replace up. Branch B is taken when
    /// k &lt; 0x8000 and |right·n| &gt; 0.5 compared as int vs double – i.e. any non-zero value (bug kept).
    /// </summary>
    public static void AlignToNormal(CarObj c, Vec3 n, int k)
    {
        var d = c.DriveFrame;
        if ((c.Flags & CarFlags.LocalHuman) == 0 && k > 0xE666)
        {
            d = c.Orient;
            d.Row1 = n;
            c.DriveFrame = d;
            return;
        }
        d.Row1 = n;
        if (k < 0x8000 && Fx.Abs(Vec3.Dot(c.Orient.Row0, n)) > 0)
        {
            d.Row0 = FxMath.Normalized(Vec3.Cross(n, c.Orient.Row2));
            d.Row2 = FxMath.Normalized(Vec3.Cross(d.Row0, n));
        }
        else
        {
            d.Row2 = FxMath.Normalized(Vec3.Cross(c.Orient.Row0, n));
            d.Row0 = FxMath.Normalized(Vec3.Cross(n, d.Row2));
        }
        c.DriveFrame = d;
    }

    // ------------------------------------------------------------ the step

    /// <summary>
    /// Per-car task (car+0x380, sim queue prio 0x1E): gravity × time scale, road vertical speed [ref:0498]
    /// smoothing (simtune 17/18), flight (d ≥ 0.6 m, extra gravity st[5]), landing (bounce using st[13..15], otherwise a plane collision) [ref:0499]
    /// on-ground tilt of ω toward the normal and snapping to the road.
    /// </summary>
    /// <param name="st">simtune set (29 ints) for the current car. [ref:0500]</param>
    /// <param name="raceTick">Race tick, used by <see cref="PhysLod.Timestep"/>. [ref:0501]</param>
    public static void Step(CarObj c, ReadOnlySpan<int> st, int raceTick, IRoadQuery? road = null)
    {
        road ??= FlatRoadQuery.Instance;
        if (c.Active == 0) return;
        var r = c.RoadFrame.Row1;
        if (c.Rail != 0)
        {
            c.Pos.Y = (c.Flags & CarFlags.Static) != 0 ? c.RoadPoint.Y : RoadHeightAtCar(c) + c.HalfHeight;
            c.AirTicks = 0;
            c.TookOff = 0;
            c.Vel.Y = 0;
            return;
        }
        if (!PhysLod.Timestep(1, 3, c.DistToPlayer, c.Id, raceTick, out var lod)) return;
        int rate = lod.RateHz, ticks = lod.Ticks;

        int gs = (c.Flags & CarFlags.LocalHuman) != 0 ? Fx.Mul(road.GravityScale(c), c.TimeScale) : c.TimeScale;
        c.F150 = 100 * Fx.One;
        c.Vel.Y -= Fx.Mul(ticks * GravityPerTick, gs);

        int h = RoadHeightAtCar(c);
        road.TunnelCeiling(c, h);
        int d = (c.Flags & CarFlags.Static) != 0 ? HeightAboveRoad(c, r, c.RoadPoint) : PlaneDist(c, r, c.RoadPoint);
        c.HeightAboveRoad = d;
        if (c.CrashSlice != 0 && c.Trk.RoadPoly == null) d = 100 * Fx.One;
        int dh = (h - c.PrevRoadHeight) * rate;
        if (c.CatchUp > 0x107AE) dh = Fx.Div(dh, gs);
        c.PrevRoadHeight = h;

        var nrm = r;
        if ((c.Flags & CarFlags.LocalHuman) != 0 && c.AirTicks == 0) road.FrontGroundProbe(c, ref nrm);
        c.UpDotN = Vec3.Dot(c.Orient.Row1, r);
        AlignToNormal(c, nrm, c.UpDotN);

        int ax = Fx.Abs(c.Vel.X), az = Fx.Abs(c.Vel.Z);
        c.SpeedApprox = ax > az ? ax + (az >> 2) : az + (ax >> 2);
        if (dh - c.RoadVySmoothed < -50 * Fx.One || dh < -120 * Fx.One)
        {
            c.AngVel.X = 0;
            c.AngVel.Z = 0;
            d += 0x4CCC8;
            c.HeightAboveRoad = d;
        }
        int lim = Math.Max(Fx.DivPow2(c.SpeedApprox, 1), 16 * Fx.One);
        if (dh - c.RoadVySmoothed > lim) { c.RoadVySmoothed = 0; dh = 0; }
        c.RoadVySmoothed += Fx.Mul(dh - c.RoadVySmoothed, dh > 0 ? st[StRiseSmoothing] : st[StFallSmoothing]);
        int vs = c.RoadVySmoothed;
        c.TookOff = 0;

        if (d >= AirborneHeight)
        {
            if (c.AirTicks == 0) c.TookOff = 1;
            c.AirTicks = (ushort)(c.AirTicks + ticks);
            c.Vel.Y -= Fx.Mul(Fx.Mul(ticks * GravityPerTick, st[StFlightGravity]), gs);
            return;
        }

        if (c.AirTicks == 0)
        {
            if ((c.Flags & CarFlags.Static) != 0) GroundContact(c, nrm, road); else GroundImpact(c, nrm, road);
        }
        else
        {
            Land(c, st, r, vs, ref d, road);
        }

        if (d <= SnapHeight)
        {
            c.HeightAboveRoad = 0;
            if ((c.Flags & CarFlags.Traffic) != 0) c.Pos.Y -= d;
            else c.Pos += new Vec3(Fx.Mul(-d, r.X), Fx.Mul(-d, r.Y), Fx.Mul(-d, r.Z));
            c.Vel.Y = vs;
        }
    }

    private static void Land(CarObj c, ReadOnlySpan<int> st, Vec3 r, int vs, ref int d, IRoadQuery road)
    {
        if ((c.Flags & CarFlags.LocalHuman) != 0) road.LandingReset(c);
        int vrel = vs - c.Vel.Y;
        if (c.UpDotN < st[StBounceThreshold] || c.AirTicks < 10)
        {
            road.CollPlane(c, r, c.RoadPoint);
            ClampAngVel(c);
            if (c.Impact > 5 * Fx.One && c.Impact < 20 * Fx.One) c.Impact = 20 * Fx.One;
        }
        else
        {
            int x = Math.Max(vrel - st[StBounceAbsorb], 0);
            int y = Fx.Mul(x, st[StBounceElasticity]);
            c.Vel.Y = vs < 0 ? vs + y : y;
            c.LandingSmoke = 5;
            c.AirTicks = 1;
            c.Impact = vrel * 2;
            if (c.Impact < 10 * Fx.One) c.Impact = 20 * Fx.One;
            c.HeightAboveRoad = 0xF5C;
            c.Pos.Y = c.Pos.Y - d + 0xF5C;
            d = 0xF5C;
            c.ImpactOther = null;
            c.ImpactType = Fx.One;
            c.LastHit = c.Pos;
        }

        if (Fx.Abs(c.AngVel.X) < 0x3333 && Fx.Abs(c.AngVel.Z) < 0x3333)
        {
            if (Fx.Abs(c.AngVel.Y) < 0x3333)
            {
                if (c.UpDotN > 0xF70B || c.UpDotN < -0xF70B) c.AirTicks = 0;      // on wheels / roof
                if (c.UpDotN < 0x8F5 && c.UpDotN > -0x8F5)                         // on a side / nose
                {
                    c.RightDotN = Vec3.Dot(c.Orient.Row0, r);
                    if (Fx.Abs(c.RightDotN) > 0xF70B) c.AirTicks = 0;
                    else if ((c.Flags & CarFlags.Static) != 0)
                    {
                        c.FwdDotN = Vec3.Dot(c.Orient.Row2, r);
                        if (Fx.Abs(c.FwdDotN) > 0xF70B) c.AirTicks = 0;
                    }
                }
            }
            else if (c.UpDotN > 0xE666)                                              // spinning on its wheels
            {
                c.F150 = c.AngVel.Y;
                c.AngVel.Y -= Fx.DivPow2(c.AngVel.Y, 5);
                c.Skid328 = 20 * Fx.One;
                c.Skid324 = 20 * Fx.One;
            }
        }
    }
}
