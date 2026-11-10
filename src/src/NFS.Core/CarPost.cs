using NFS.Core.Tables;
using NFS.Data.Track;

namespace NFS.Core;

/// <summary>What it needs from the race: simtune of the current, the slice chain [ref:0344]
/// and (locator) for the crash reset. [ref:0345]</summary>
public sealed class CarPostEnv
{
    public required int[] SimTune { get; init; }
    /// <summary>Mode: 0 sim, 1 arcade. [ref:0346]</summary>
    public int ArcadeMode { get; init; } = 1;
    public required TrackSlice[] Slices { get; init; }
    public bool Reverse { get; init; }
    /// <summary>Track update callback for a car. [ref:0347]</summary>
    public required Action<CarObj> TrackUpdate { get; init; }
    public GameTables Tables { get; init; } = GameTables.Default;
    /// <summary>Random source for the impact body shake (shared). [ref:0348]</summary>
    public WatcomRandom Rand { get; init; } = new();
    public int SkillLevel { get; init; }
    public Func<CarObj, NFS.Data.Ai.Personality?>? PersonalityOf { get; init; }
}

/// <summary>
/// Post-step for the car (sim queue prio 0x32, after the collision tasks): the physics parts [ref:0349]
/// (task "prio 0x32" in the original, checked against the original), including the AI reaction to hits. [ref:0350]
/// Not ported: smoke/spark/skid particles, sounds and body lean (+0x664..+0x67C, render only). [ref:0351]
/// </summary>
public static class CarPost
{
    // simtune (mode·0x74) [ref:0352]
    public const int StCrashImpact = 0, StJumpRollAttenuate = 1, StJumpRollTurn = 2, StJumpPitchDamp = 3,
        StJumpPitchAccel = 4, StCrashTime = 6, StCrashTimeGas = 7, StResetCooldown = 8, StResetSlices = 9,
        StCrashSpinSettle = 10, StResetHeight = 11, StResetSpeed = 12;

    public static void Update(CarObj c, CarPostEnv env, int raceTick)
    {
        var st = env.SimTune;
        c.SoundEventCount = 0;
        if (c.ManualResetLock != 0) c.ManualResetLock--;
        if (c.Rail != 0 || c.Active == 0)
        {
            if (c.AutoResetCounter < 1 && c.CrashSlice == 0) return;
            c.AutoResetCounter = 0;
            CrashReset(c, env, false);
            return;
        }
        var personality = env.PersonalityOf?.Invoke(c);
        AiReactions.OnHit(c, env.SkillLevel, personality?.PsychoTriggerCount ?? 0, personality?.PsychoTime ?? 0, raceTick); // [ref:0353]
        TakeoffSpin(c, st);
        if (c.DistToPlayer < 0x460000) SlopeSlide(c, env.Tables);
        if (c.DistToPlayer < 0x640000) SmokeFx(c);
        ImpactProcess(c, env, raceTick);
    }

    /// <summary>For cars &lt; 100 m: the landing smoke counter +0x90 (set to 5 [ref:0354]
    /// on a hard landing) counts down once per sim tick. The particles it spawns while +0xBC &gt; 20 [ref:0355]
    /// are rendering only and not ported. [ref:0356]</summary>
    public static void SmokeFx(CarObj c)
    {
        if (c.LandingSmoke > 0) c.LandingSmoke--;
    }

    /// <summary>
    /// in the step the car left the ground (+0x8E) and not crashed [ref:0357]
    /// (+0x17C = 0): ω in car axes l = (ω·right, ω·up, ω·fwd) (+0x2BC..+0x2C4), pitch l.x ×st[3], roll l.z ×st[1];
    /// local humans also get <c>+FX(Fz/2, st[4])</c> pitch and <c>−FX(Fx/2, st[2])</c> roll from the drive forces
    /// (+0x2AC/+0x2A4, /2 truncating); then ω = Mᵀ·l. Every product a rounded FX as in the original.
    /// </summary>
    public static void TakeoffSpin(CarObj c, ReadOnlySpan<int> st)
    {
        if (c.TookOff == 0 || c.CrashSlice != 0) return;
        var m = c.Orient;
        var w = c.AngVel;
        int lx = Fx.Mul(w.X, m.M00) + Fx.Mul(w.Y, m.M01) + Fx.Mul(w.Z, m.M02);
        int ly = Fx.Mul(w.X, m.M10) + Fx.Mul(w.Y, m.M11) + Fx.Mul(w.Z, m.M12);
        int lz = Fx.Mul(w.X, m.M20) + Fx.Mul(w.Y, m.M21) + Fx.Mul(w.Z, m.M22);
        lx = Fx.Mul(lx, st[StJumpPitchDamp]);
        lz = Fx.Mul(lz, st[StJumpRollAttenuate]);
        if ((c.Flags & CarFlags.LocalHuman) != 0)
        {
            lx += Fx.Mul(c.LocalForce.Z / 2, st[StJumpPitchAccel]);
            lz -= Fx.Mul(c.LocalForce.X / 2, st[StJumpRollTurn]);
        }
        c.LocalAngVel = new Vec3(lx, ly, lz);
        c.AngVel = new Vec3(
            Fx.Mul(lx, m.M00) + Fx.Mul(ly, m.M10) + Fx.Mul(lz, m.M20),
            Fx.Mul(lx, m.M01) + Fx.Mul(ly, m.M11) + Fx.Mul(lz, m.M21),
            Fx.Mul(lx, m.M02) + Fx.Mul(ly, m.M12) + Fx.Mul(lz, m.M22));
    }

    /// <summary>Not traffic, slide surface, road normal [ref:0358]
    /// y &lt; 0.8 and +0x15C ≤ 0x7AE → v.xz += roadUp.xz (raw), v.y −= 1.3.</summary>
    public static void SlopeSlide(CarObj c, GameTables t)
    {
        if ((c.Flags & CarFlags.Traffic) != 0) return;
        if ((uint)c.SurfaceType >= (uint)t.SurfSlide.Length || t.SurfSlide[c.SurfaceType] == 0) return;
        if (c.RoadFrame.M11 >= 0xCCCC || c.HeightAboveRoad > 0x7AE) return;
        c.Vel = new Vec3(c.Vel.X + c.RoadFrame.M10, c.Vel.Y - 0x14CCC, c.Vel.Z + c.RoadFrame.M12);
    }

    /// <summary>
    /// For a landing of a human and for a crash: [ref:0359]
    /// +0x2B0/+0x2B8 = v·drive right / v·drive forward, slip/skid/state cleared; arcade with forward speed &gt; 0.1
    /// and |vx/vz| &gt; 0.1 → state ∓3 (vx &lt; 0 → +3), +0x310 = vx/4, skid +2.0 (≤ 40.0, only below 20.0).
    /// The rpm branch for neutral is unreachable (<c>CMP DL,DL; JZ</c>). [ref:0360]
    /// </summary>
    public static void DriveLandingReset(CarObj c, int arcadeMode)
    {
        var v = c.Vel; var f = c.DriveFrame;
        int vx = Fx.Mul(v.X, f.M00) + Fx.Mul(v.Y, f.M01) + Fx.Mul(v.Z, f.M02);
        int vz = Fx.Mul(v.X, f.M20) + Fx.Mul(v.Y, f.M21) + Fx.Mul(v.Z, f.M22);
        c.LocalVel = new Vec3(vx, c.LocalVel.Y, vz);
        c.SlipAngle = 0; c.Skid324 = 0; c.Skid328 = 0; c.SlipState = 0;
        if (vz <= 0x1999 || arcadeMode != 1) return;
        int r = Fx.Div(vx, vz);
        if (r < 1) r = -r;
        if (r <= 0x1999) return;
        c.SlipState = vx < 0 ? 3 : -3;
        c.SlipAngle = vx / 4;
        if (c.Skid324 < 0x140000) c.Skid324 = Math.Min(c.Skid324 + 0x20000, 0x280000);
        if (c.Skid328 < 0x140000) c.Skid328 = Math.Min(c.Skid328 + 0x20000, 0x280000);
    }

    /// <summary>
    /// ("podrzut", a toss): ω scaled by K0, kicked about the drive right (K1, sign = bit 2 of [ref:0361]
    /// ) and forward (K2, sign = bit 3), v += drive up · (K3 + min(|speed − 44|/64, 0xE666)); +0x14C++; [ref:0362]
    /// Table index 1 = arcade and not static; every /256 truncates (arithmetic shift with rounding fix). [ref:0363]
    /// </summary>
    public static void Launch(CarObj c, int arcadeMode, int raceTick, GameTables t)
    {
        int s1 = (raceTick & 4) != 0 ? -1 : 1, s2 = (raceTick & 8) != 0 ? -1 : 1;
        int m = arcadeMode != 0 && (c.Flags & CarFlags.Static) == 0 ? 1 : 0;
        var k = t.LaunchK;
        var f = c.DriveFrame;
        int k0 = k[m] / 256;
        var w = new Vec3(c.AngVel.X * k0 / 256, c.AngVel.Y * k0 / 256, c.AngVel.Z * k0 / 256);
        int a = k[2 + m] * s1 / 256;
        w = new Vec3(w.X + f.M00 * a / 256, w.Y + f.M01 * a / 256, w.Z + f.M02 * a / 256);
        int b = k[4 + m] * s2 / 256;
        w = new Vec3(w.X + f.M20 * b / 256, w.Y + f.M21 * b / 256, w.Z + f.M22 * b / 256);
        c.AngVel = w;
        int sp = Math.Abs(c.SpeedApprox - 0x2C0000) / 64;
        if (sp >= 0xE667) sp = 0xE666;
        int u = (k[6 + m] + sp) / 256;
        c.Vel = new Vec3(c.Vel.X + f.M10 * u / 256, c.Vel.Y + f.M11 * u / 256, c.Vel.Z + f.M12 * u / 256);
        c.AirTicks++;
        Newton.ClampAngVel(c);
    }

    private static int CrashTimer(CarObj c, CarPostEnv env) =>
        c.CrashSlice != 0 ? 0x100
        : env.ArcadeMode == 1 && (c.Flags & CarFlags.Opponent) != 0 ? 3 * env.SimTune[StCrashTime]
        : env.SimTune[StCrashTime];

    /// <summary>
    /// (physics only): an impact (+0x160, set by the collisions) above st[0] that is not [ref:0364]
    /// a static car hit by nothing is a crash — landing reset, +0x27C++, near the ground ω.x/ω.z += k·ω.y (k 2 in
    /// arcade) and sets the crash flag; skid cleared, reset timer +0x578 armed; impact fields cleared. The [ref:0365]
    /// timer counts down (a human upright on its wheels does not count), settles ω, and at 0 (or below st[7] with the
    /// throttle &gt; 200). A car lying wrong (up·roadUp &lt; 0.75, off road, crashed) re-arms it. [ref:0366]
    /// </summary>
    public static void ImpactProcess(CarObj c, CarPostEnv env, int raceTick)
    {
        var st = env.SimTune;
        if (c.Crashing != 0 && c.AirTicks == 0) c.Crashing = 0;   // (+ spark/smoke fx)
        if (c.Impact != 0)
        {
            if (((c.Flags & CarFlags.Static) == 0 || c.ImpactOther != null) && c.Impact > st[StCrashImpact])
            {
                DriveLandingReset(c, env.ArcadeMode);
                c.CrashCount++;
                if (c.HeightAboveRoad < Fx.One)
                {
                    int k = env.ArcadeMode != 0 && (c.Flags & CarFlags.Static) == 0 ? 2 : 1;
                    c.AngVel.Z += k * c.AngVel.Y;
                    c.AngVel.X += k * c.AngVel.Y;
                    Launch(c, env.ArcadeMode, raceTick, env.Tables);
                }
                c.Crashing = 1;
                c.Skid324 = 0; c.Skid328 = 0;
                if (c.AutoResetCounter < 1) c.AutoResetCounter = CrashTimer(c, env);
            }
            // tail: a hard impact (> 0xA0000) while the body shake is idle (both phases < 0x1F) starts it [ref:0367]
            // each start draws from the shared random source [ref:0368]
            if (c.Impact > 0xA0000 && Math.Max(c.ShakePhase0, c.ShakePhase1) < 0x1F)
            {
                int type = c.ImpactType & unchecked((int)0xEFFF0000);
                int r = env.Rand.Next() % 8 + 0x10;
                if (type == 0x40000 || type == 0x20000) BodyShakeStart(c, r, 0x23, env.Rand);   // (phase0, phase1) as in the original
                else BodyShakeStart(c, 0x23, r, env.Rand);
            }
            c.ImpactOther = null; c.ImpactType = 0; c.Impact = 0;
        }
        if (c.AutoResetCounter > 0)
        {
            if (c.UpDotN < 0x1999 || c.CrashSlice != 0 || (c.Flags & CarFlags.LocalHuman) == 0) c.AutoResetCounter--;
            if (c.HeightAboveRoad < 0x4CCC && c.UpDotN > 0x8000)
            {
                if (Math.Abs(c.AngVel.X) + Math.Abs(c.AngVel.Z) < st[StCrashSpinSettle] && c.CrashSlice == 0)
                {
                    c.AngVel.X -= c.AngVel.X / 4;
                    c.AutoResetCounter -= 8;
                    c.AngVel.Z -= c.AngVel.Z / 4;
                }
            }
            else if (c.HeightAboveRoad < 5 * Fx.One && c.CrashSlice == 0)
            {
                c.AngVel = new Vec3(c.AngVel.X - c.AngVel.X / 32, c.AngVel.Y - c.AngVel.Y / 32, c.AngVel.Z - c.AngVel.Z / 32);
            }
            if (Math.Abs(c.Vel.Y) + c.SpeedApprox < Fx.One && c.CrashSlice == 0) c.AutoResetCounter -= 8;
            if (c.AutoResetCounter < 1 || (c.AutoResetCounter < st[StCrashTimeGas] && c.Throttle > 200 && c.CrashSlice == 0))
            {
                if ((c.Flags & CarFlags.Traffic) == 0) CrashReset(c, env, false);
                else c.Crashing = 0;
                c.AutoResetCounter = 0;
            }
        }
        if (c.ShakePhase0 > 0) c.ShakePhase0--;   // shake phases count down [ref:0369]
        if (c.ShakePhase1 > 0) c.ShakePhase1--;
        if (c.AutoResetCounter < 1)
        {
            int u = Vec3.Dot(c.RoadFrame.Row1, c.Orient.Row1);
            if (u > 0xBFFF && c.SurfaceType != 0 && c.SurfaceType != 0xE && c.CrashSlice == 0) return;
            c.AutoResetCounter = CrashTimer(c, env);
            if (c.AirTicks != 0)
            {
                c.AngVel.Z += 0x51E;
                c.AngVel.X += 0x28F;
            }
        }
    }

    /// <summary>(c, phase0, phase1): +0x580 = phase0, amplitude +0x57C = min((impact − 0xA0000)·2 [ref:0370]
    /// 0x2D0000) with a random sign (rand % 0x100 &gt; 0x80 → negative), +0x588 = phase1, +0x584 = +0x57C with a second random sign.</summary>
    public static void BodyShakeStart(CarObj c, int phase0, int phase1, WatcomRandom rand)
    {
        c.ShakePhase0 = phase0;
        int amp = (c.Impact - 0xA0000) * 2;
        if (amp > 0x2D0000) amp = 0x2D0000;
        c.ShakeAmp0 = amp;
        if (rand.Next() % 0x100 > 0x80) c.ShakeAmp0 = -c.ShakeAmp0;
        c.ShakePhase1 = phase1;
        c.ShakeAmp1 = c.ShakeAmp0;
        if (rand.Next() % 0x100 > 0x80) c.ShakeAmp1 = -c.ShakeAmp1;
    }

    /// <summary>On-road test: surface ≠ 0/0xE, no wall bit 0x80, on a road polygon. [ref:0371]</summary>
    private static bool OnRoad(CarObj c) =>
        c.SurfaceType != 0 && c.SurfaceType != 0xE && (c.SurfaceByte & 0x80) == 0 && c.Trk.RoadPoly != null;

    /// <summary>Returns whether the reset was accepted (FFB nop is host-only). [ref:0372]</summary>
    public static bool ManualReset(CarObj c, CarPostEnv env, bool raceStarted)
    {
        if (c.ManualResetLock != 0 || c.RaceState == 2 || !raceStarted
            || (c.Flags & CarFlags.LocalHuman) == 0 || c.CrashSlice != 0) return false;
        c.Crashing = 0;
        int step = env.SimTune[StResetSlices] * (env.Reverse ? -1 : 1);
        int s = c.Trk.Slice + step;
        if (step < 0) { if (s < 0) s += env.Slices.Length; }
        else if (s >= env.Slices.Length) s -= env.Slices.Length;
        var t = env.Slices[s];
        int lw = (t.Lanes >> 4) * t.HalfWidthLeft * 0x8000;
        int rw = (t.Lanes & 15) * t.HalfWidthRight * 0x8000;
        int x = (rw - lw) / 2;
        void Place() => NewtonPlace.PlaceOnSlice(c, env.Slices, s, new Vec3(x, 0, 0), env.Reverse, env.TrackUpdate);
        Place();
        bool ok = false;
        for (int i = 0; i < 21 && !(ok = OnRoad(c)); i++)
        {
            // increments only the high word (wraps exactly like ADD x, 0x10000). [ref:0373]
            x = unchecked(x + (lw <= rw ? 0x10000 : -0x10000));
            Place();
        }
        if (!ok) { x = 0; Place(); }
        int h = env.SimTune[StResetHeight];
        var cur = env.Slices[c.Trk.Slice];
        if (cur.Surroundings >> 4 == 2 || (cur.Surroundings & 15) == 2) h >>= 1; // [ref:0374]
        c.Pos.Y += h;
        c.Vel = default;
        ResetMotion(c, env.SimTune);
        c.CollisionCooldown = c.ManualResetLock = 0x30;
        return true;
    }

    /// <summary>
    /// (c, force): unless forced, a car standing well (up·roadUp &gt; 0.75, ω.x/ω.z &lt; 1 [ref:0375]
    /// on road, not crashed, +0x14C &lt; 6, low) is left alone. Otherwise placed st[9] slices on (crash slice or
    /// current), arcade: lateral x = (Rw − Lw)/2 then ±1 m steps toward the wider side until on the road (21 tries,
    /// else the centre); raised by st[11] (half in a tunnel); v = forward · FX(st[12], |slice+30 forward · road
    /// forward|) (256 twice, truncating). The sim-model branch (grid of 8 slices, lane by car index, 31 steps) is ported. [ref:0376]
    /// </summary>
    public static void CrashReset(CarObj c, CarPostEnv env, bool force)
    {
        var st = env.SimTune;
        var sl = env.Slices;
        int n = sl.Length, dir = env.Reverse ? -1 : 1;
        c.Crashing = 0;
        if (!force)
        {
            int u = Vec3.Dot(c.RoadFrame.Row1, c.Orient.Row1);
            if (u > 0xC000 && c.AngVel.X < Fx.One && c.AngVel.Z < Fx.One && c.SurfaceType != 0xE && c.SurfaceType != 0
                && c.CrashSlice == 0 && c.AirTicks < 6 && c.HeightAboveRoad < 0x9999)
            {
                c.Crashing = 0;
                return;
            }
        }
        int s = c.CrashSlice != 0 ? c.CrashSlice : c.Trk.Slice;
        c.CrashSlice = 0;
        int step = st[StResetSlices] * dir;
        s += step;
        if (step < 0) { if (s < 0) s += n; }
        else if (s >= n) s -= n;
        void Place(int x) => NewtonPlace.PlaceOnSlice(c, sl, s, new Vec3(x, 0, 0), env.Reverse, env.TrackUpdate);
        bool ok = false;
        if (env.ArcadeMode != 0)
        {
            var t = sl[s];
            uint lw = (uint)(t.Lanes >> 4) * t.HalfWidthLeft * 0x8000u, rw = (uint)(t.Lanes & 0xF) * t.HalfWidthRight * 0x8000u;
            int x = unchecked((int)(rw - lw)) / 2;
            Place(x);
            // check → step → place, 21 times; the last placement is not checked (falls through to the centre)
            for (int i = 0; i < 0x15 && !(ok = OnRoad(c)); i++)
            {
                x += lw <= rw ? 0x10000 : -0x10000;
                Place(x);
            }
        }
        else
        {
            // sim (0): the slice is snapped to a grid of 8 and the car takes a lane by its index: humans ahead (+8), others [ref:0377]
            // behind (−8), plus (index/2) slices; even index → left side of the road, odd → right; 31 one-metre steps towards the
            // middle until the car is on the road, else the centre.
            int idx = c.PlayerIndex;
            int g8 = (c.Flags & CarFlags.LocalHuman) != 0 ? 8 : -8;
            s = (s / 8 * 8 + g8 * dir + idx / 2 * dir) % n;
            if (s < 0) s = 0;
            var t = sl[s];
            int hw = Fx.Mul(c.HalfWidth, 0x18000);
            int x, stepX;
            if (idx / 2 * 2 == idx) { x = unchecked(hw - t.HalfWidthLeft * 0x8000 * (t.Lanes >> 4)); stepX = 1; }
            else { x = unchecked(t.HalfWidthRight * 0x8000 * (t.Lanes & 0xF) - hw); stepX = -1; }
            Place(x);
            for (int i = 0; i < 0x1F && !(ok = OnRoad(c)); i++)
            {
                x = unchecked(x + stepX * 0x10000);
                Place(x);
            }
        }
        if (!ok) Place(0);
        if (!force)
        {
            int h = st[StResetHeight];
            var cur = sl[c.Trk.Slice];
            if (cur.Surroundings >> 4 == 2 || (cur.Surroundings & 0xF) == 2) h >>= 1;   // [ref:0378]
            c.Pos.Y += h;
        }
        int s2 = c.Trk.Slice + 30 * dir;
        if (30 * dir < 0) { if (s2 < 0) s2 += n; }
        else if (s2 >= n) s2 -= n;
        var fw = sl[s2].Forward;
        var rf = c.RoadFrame;
        int kk = Fx.Mul(fw[0] << 9, rf.M20) + Fx.Mul(fw[1] << 9, rf.M21) + Fx.Mul(fw[2] << 9, rf.M22);
        if (kk < 1) kk = -kk;
        int m = Fx.Mul(st[StResetSpeed], kk) / 256;
        var o = c.Orient;
        c.Vel = new Vec3(o.M20 * m / 256, o.M21 * m / 256, o.M22 * m / 256);
        ResetMotion(c, st);
    }

    /// <summary>ω, local ω, forces, +0x2E8 = 0, +0x14C = 1, impact, +0x158, landing smoke [ref:0379]
    /// took-off, crash flags and body lean cleared, +0x16C = st[8] (collision immunity). [ref:0380]</summary>
    public static void ResetMotion(CarObj c, ReadOnlySpan<int> st)
    {
        c.AngVel = default;
        c.LocalAngVel = default;
        c.LocalForce = default;
        c.ManualResetLock = 0;
        c.AirTicks = 1;
        c.Impact = 0;
        c.ImpactOther = null;
        c.RoadVySmoothed = 0;
        c.LandingSmoke = 0;
        c.TookOff = 0;
        c.Crashing = 0;
        c.CrashSlice = 0;
        c.Unk660 = 0; c.BodyRoll = 0; c.BodyPitch = 0; c.SmoothFz = 0; c.SmoothFx = 0;
        c.CollisionCooldown = st[StResetCooldown];
        DrvStateReset(c);
    }

    /// <summary>gear and requested gear = 2 (1st), Δv/Δω, local v/ω, slip state/angle [ref:0381]
    /// skid, edge hit cleared.</summary>
    public static void DrvStateReset(CarObj c)
    {
        c.Gear = 2;
        c.RequestedGear = 2;
        c.DeltaV = default;
        c.DeltaW = default;
        c.LocalVel = default;
        c.LocalAngVel = default;
        c.SlipState = 0;
        c.SlipAngle = 0;
        c.Skid324 = 0;
        c.Skid328 = 0;
        c.EdgeHit = 0;
    }
}
