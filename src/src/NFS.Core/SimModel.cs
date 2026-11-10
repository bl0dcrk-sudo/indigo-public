using NFS.Data.Track;

namespace NFS.Core;

/// <summary>One axle (0x30 B on the stack of the original: front @sp+0, rear @sp+0x30; [ref:0544]
/// axle structure). Offsets in the comments are the offsets inside that structure.</summary>
public struct SimAxle
{
    public int Drive;      // +0x00 drive / brake force (before ×S.138)
    public int Load;       // +0x04 axle load
    public int Steer;      // +0x0C steering angle (front: from the wheel, rear: 0); angle/256 [ref:0545]
    public int Vx, Vy, Vz; // +0x10/+0x14/+0x18 tyre velocity = −16·v_local (± yaw term on x)
    public int Fy;         // +0x1C lateral force (out)
    public int F20;        // +0x20 (always 0)
    public int Fz;         // +0x24 longitudinal force (out)
    public bool Front;     // +0x28
    public int Skid;       // +0x2C skid (out) → car +0x324 (front) / +0x328 (rear)
}

/// <summary>Per-race inputs and "globals" of the SIM driving model: race config <c>cfg[i]</c> (car+0x21C) [ref:0546]
/// fields, bits, track slices and globals [ref:0547]
/// that the original writes in and reads in its subroutines. [ref:0548]</summary>
public sealed class SimEnv
{
    /// <summary>cfg+0x00 car model (0..14 player cars; 0xE = special, 5/7/8/9/0xA/0xD/0xE have ABS).</summary>
    public int Model;
    /// <summary>Per-car traits from <c>CarTraits</c> (set by RaceRunner); null = derive from <see cref="Model"/> (the vanilla rule).</summary>
    public bool? HasAbs, SkidAlways, SpecialRules0E, RearAeroBoost;
    /// <summary>cfg+0x1C / cfg+0x20 front / rear downforce set-up (1.0 = neutral).</summary>
    public int DownforceFront = Fx.One, DownforceRear = Fx.One;
    /// <summary>cfg+0x28 brake balance correction (added to carSpecs+0x100).</summary>
    public int BrakeBias;
    /// <summary><c>(&amp; 4) &amp;&amp; model &lt; 0xF</c>: player "boost" (aero drag /2, thrust ×2, no force cap). [ref:0549]</summary>
    public bool PlayerBoost;
    /// <summary><c>&amp; 8</c> (SLIP cheat): every surface uses grip class 9. [ref:0550]</summary>
    public bool SlipOption;
    /// <summary>row of the surface μ table (0 for this model). [ref:0551]</summary>
    public int ArcadeMode;
    /// <summary>Race tick, used for the ABS skid blink. [ref:0552]</summary>
    public int RaceTick;
    /// <summary>Track slices (XBID 15) for the road curvature term; null → no curvature. [ref:0553]</summary>
    public TrackSlice[]? Slices;

    // outputs [ref:0554]
    public int Throttle, Brake, SteerAbs;
    /// <summary>Gravity (0,−10,0) in the drive frame. [ref:0555]</summary>
    public Vec3 GravLocal;
    /// <summary>Grip μ for this step: surface μ plus road curvature term. [ref:0556]</summary>
    public int GripMu;
    /// <summary>Axle downforce multipliers (1.0 + aero). [ref:0557]</summary>
    public int DfFront = Fx.One, DfRear = Fx.One;
}

/// <summary>
/// The SIM driving model (<c>0</c>) is a [ref:0558]
/// force model (two axles, load transfer, friction circle), with [ref:0559]
/// the engine <see cref="SimEngine"/> [ref:0560]
/// the gearbox <see cref="DriveControls.AutoShift"/> and the controls filter [ref:0561]
/// <see cref="DriveControls.Filter"/>. Every operation follows the original (FX = rounded 16.16 product unless a [ref:0562]
/// comment says "no rounding"; C divisions truncate towards zero).
/// </summary>
public static class SimModel
{
    // [2][10] (read from the original): row 0 = sim, row 1 = arcade [ref:0563]
    private static readonly int[][] SurfaceMu =
    [
        [0x10000, 0xFD70, 0xFAE1, 0xF851, 0xF333, 0xE666, 0xD999, 0xCCCC, 0xC000, 0x9999],
        [0x10000, 0xFD70, 0xFAE1, 0xF851, 0xE666, 0xCCCC, 0xC000, 0xB333, 0xA666, 0x9999],
    ];

    /// <summary>Surface μ for a grip class (table). [ref:0564]</summary>
    public static int SurfaceGripMu(int arcadeMode, int gripClass) => SurfaceMu[arcadeMode != 0 ? 1 : 0][gripClass];

    /// <summary>Cars with ABS: model ∈ {5, 7, 8, 9, 0xA, 0xD, 0xE}. [ref:0565]</summary>
    public static bool HasAbs(int model) => model is 5 or 7 or 8 or 9 or 0xA or 0xD or 0xE;

    private static int Trunc16(long p) => (int)(p >> 16);                       // 64-bit product, shifted right by 16, no rounding

    /// <summary><c>a = ang/256; ox = FX(cos,x) − FX(sin,z); oz = FX(sin,x) + FX(cos,z)</c>. [ref:0566]</summary>
    public static void Rotate2d(int x, int z, int ang, out int ox, out int oz)
    {
        int a = Fx.DivPow2(ang, 8);
        // Note: the cos interpolation term is (−sin)>>2 · f >> 21 (floor of the [ref:0567]
        // negated product), whereas the original subtracts the floor of the positive one; they differ by 1 LSB for some angles [ref:0568]
        FxMath.SinCosTab(a >> 6, out int s0, out int c0);
        int f = ((a & 63) * 0x6487E) >> 9;
        int s = s0 + (((c0 >> 2) * f) >> 21);
        int co = c0 + (((-s0 >> 2) * f) >> 21);
        ox = Fx.Mul(co, x) - Fx.Mul(s, z);
        oz = Fx.Mul(s, x) + Fx.Mul(co, z);
    }

    /// <summary>
    /// <paramref name="filter"/> (called in the middle of the step [ref:0569]
    /// after the local velocity, speed and yaw terms were computed from the NEW state; it may change gear/steering);
    /// <paramref name="autoShift"/> = when the race config says automatic. [ref:0570]
    /// Writes Δv/Δω (+0x28C/+0x298) and adds them to the velocity / angular velocity.
    /// </summary>
    public static void Step(CarObj c, DerivedCarSpecs sp, SimEnv e, Func<CarObj, DriveSignals> filter,
        Action<CarObj>? autoShift = null)
    {
        var S = sp.Source;
        c.LocalForce = default;                                                   // +0x2A4..0x2AC = 0
        c.GripClass = ArcadeModel.GripClassOf(c.SurfaceType, e.SlipOption);
        var M = c.DriveFrame;
        var v = c.Vel;
        var w = c.AngVel;
        c.LocalVel = new Vec3(Fx.Dot(v.X, v.Y, v.Z, M.M00, M.M01, M.M02), Fx.Dot(v.X, v.Y, v.Z, M.M10, M.M11, M.M12),
            Fx.Dot(v.X, v.Y, v.Z, M.M20, M.M21, M.M22));
        c.LocalAngVel = new Vec3(Fx.Dot(w.X, w.Y, w.Z, M.M00, M.M01, M.M02), Fx.Dot(w.X, w.Y, w.Z, M.M10, M.M11, M.M12),
            Fx.Dot(w.X, w.Y, w.Z, M.M20, M.M21, M.M22));
        var vl = c.LocalVel;
        c.SlipAngle = vl.Z < 0x50001 ? 0 : Fx.Div(vl.X, vl.Z);                    // slip angle only above 5 m/s
        int sp304 = FxMath.Hypot2(Fx.Abs(vl.X), Fx.Abs(vl.Z));
        c.SignedSpeed = vl.Z < 0 ? -sp304 : sp304;

        // tyre velocity = −16·v_local ((−v·32)/2, C division), yaw term from the WORLD yaw rate
        int hx = Fx.DivPow2(vl.X * -0x20, 1), hy = Fx.DivPow2(vl.Y * -0x20, 1), hz = Fx.DivPow2(vl.Z * -0x20, 1);
        int yawMul = Fx.Mul(c.AngVel.Y << 5, sp.YawArm);
        int yaw = -yawMul;
        if (c.SignedSpeed < 0x2CA45A) yaw = Fx.DivPow2(yaw, 1);
        else
        {
            int six = yawMul * -6;                                                // = 6·yaw
            if (c.SignedSpeed < 0x3C0000) yaw = Fx.DivPow2(six, 3);
            else if (c.SignedSpeed > 0x500000) yaw = Fx.DivPow2(six, 2);
        }
        e.GravLocal = new Vec3(ArcadeModel.GravSlope(M.M01), ArcadeModel.GravSlope(M.M11), ArcadeModel.GravSlope(M.M21));
        var F = new SimAxle { Vx = hx + yaw, Vy = hy, Vz = hz, Front = true };
        var R = new SimAxle { Vx = hx - yaw, Vy = hy, Vz = hz };

        var sig = filter(c);                                                      // (may change gear, steer) [ref:0571]
        e.Throttle = sig.Throttle; e.Brake = sig.Brake; e.SteerAbs = sig.SteerAbs;

        // front steering angle: wheel + lateral-g lean (FX(FX(gx, 0.19), 8.0)/65536), clamp ±127, × S.12C/128
        int lean = Fx.DivPow2(Fx.Mul(Fx.Mul(e.GravLocal.X, 0x30A3), 0x80000), 16);
        int st = Math.Clamp(c.Steer + lean, -0x7F, 0x7F);
        F.Steer = Fx.DivPow2(sp.MaxSteering * st, 7);
        if (vl.Z > 0x3C0000) F.Steer = Fx.Div(F.Steer, Fx.Mul(vl.Z, 0x444));      // ÷ (vl.z/60) above 60 m/s
        if (F.Steer != 0 && Fx.Abs(vl.Z) < 0x10000 && c.ThrottleRamped < 0x20 && c.Gear != 1)
        {
            // standing still with the wheel turned and no throttle: bleed velocity (×0.9)
            c.Vel = new Vec3(Fx.Mul(c.Vel.X, 0xE666), Fx.Mul(c.Vel.Y, 0xE666), Fx.Mul(c.Vel.Z, 0xE666));
            c.AngVel = new Vec3(Fx.Mul(c.AngVel.X, 0xE666), Fx.Mul(c.AngVel.Y, 0xE666), Fx.Mul(c.AngVel.Z, 0xE666));
        }

        var env = new SimEngineEnv(e.Throttle, e.DownforceFront, e.DownforceRear, e.PlayerBoost, e.GravLocal.Z);
        int eng = SimEngine.DriveForce(c, sp, env, out bool boostSpin, autoShift);
        c.SlipFront = boostSpin ? 1 : 0;                                          // +0x2F8
        F.Drive = Fx.Mul(eng, sp.FrontDriveRatio);
        R.Drive = eng - F.Drive;

        if ((c.AirTicks != 0 && S.SpinVelocityCap < c.F150) || c.ManualResetLock != 0)
        {
            c.Skid328 = 0;
            c.Skid324 = 0;
            return;
        }
        c.EdgeHit = 0;

        // brakes: force opposes the motion, capped by what stops the car within one 1/32 s step
        int br = Fx.Mul(e.Brake, S.MaxBrakingDeceleration);
        if (Fx.Abs(vl.Z) * 0x20 < br) br = Fx.Abs(vl.Z) << 5;
        if (vl.Z > 0) br = -br;
        int bf = Fx.Mul(br, e.BrakeBias + S.FrontBiasBrakeRatio);
        F.Drive += bf;
        R.Drive += br - bf;

        int weight = Fx.Mul(-e.GravLocal.Y, sp.GripMultiplier);
        GripFactors(c, sp, e);
        int load = Fx.Mul(weight, e.GripMu);
        if (load < 0) return;                                                     // (no final /4, /2 on this path)
        int loadFront = Fx.Mul(load, S.FrontGripBias);
        if (c.LoadTransfer < 0) c.LoadTransfer = Fx.DivPow2(c.LoadTransfer, 1);
        F.Load = Fx.Mul(e.DfFront, loadFront - c.LoadTransfer);
        R.Load = Fx.Mul(e.DfRear, c.LoadTransfer + (load - loadFront));
        c.SlidingAxles = 0;
        Tires(c, sp, e, ref F);
        Tires(c, sp, e, ref R);

        int fz = Fx.Mul(F.Fz + R.Fz, sp.InvGrip);
        c.LocalForce = new Vec3(F.Fy + R.Fy, 0, fz);
        c.LoadTransfer = Fx.Mul(fz, S.GTransferFactor) - Fx.Mul(e.GravLocal.Z, 0x1999);   // carSpecs +0x140 (file +0x144)
        int ty = Fx.Mul(F.Fy - R.Fy, sp.InvYawArm);                               // yaw torque from the axle side forces
        int k = -1;
        int vlz = vl.Z, wly = c.LocalAngVel.Y, slip = c.SlipAngle;
        if (vlz > 0 && Fx.Abs(slip) > 0xCCC)
        {
            if (c.SlipState != 0 && c.Handbrake == 0)
            {
                // sliding: damp the yaw torque that would increase the rotation beyond S.14C
                int d = Fx.Mul(Fx.Abs(vlz), 0x8000);
                d = 0x10000 - d < 0 ? 0 : 0x10000 - d;
                if ((S.SpinVelocityCap < wly && ty > 0) || (wly < -S.SpinVelocityCap && ty < 0)) ty = Fx.Mul(d, ty);
            }
            else if (vlz > 0x50000)
            {
                bool hard = (wly > 0 && c.Steer > 0x40 && c.Gear > 2) || (wly < 0 && c.Steer < -0x40 && c.Gear > 2);
                int q = 0;
                bool apply = true;
                if (hard) q = c.Handbrake != 0 ? Fx.DivPow2(S.SlideVelocityCap, 3)
                    : c.BrakeRamped != 0 ? S.SlideVelocityCap * 2 : Fx.DivPow2(S.SlideVelocityCap, 1);
                else if (c.Handbrake == 0) q = c.BrakeRamped != 0 ? S.SlideVelocityCap * 2 : S.SlideVelocityCap;
                else apply = false;                                               // handbrake without a hard turn: nothing
                if (apply)
                {
                    k = Math.Max(0x10000 - Fx.Mul(Fx.Abs(slip), q), 0x8000);     // bleed the yaw rate below
                    c.SlipState = 2;                                              // oversteer
                }
            }
        }

        ArcadeModel.ApplyGravitySlope(c, false, false);                           // (c, 0): sim adds F.z always [ref:0572]
        var Fl = c.LocalForce;
        c.DeltaV = Mat3.MulRow(Fl, M);                                            // Mᵀ·F (each product rounded)
        c.DeltaW = Mat3.MulRow(new Vec3(0, ty, 0), M);
        c.Vel = new Vec3(c.Vel.X + Fx.DivPow2(c.DeltaV.X, 5), c.Vel.Y + Fx.DivPow2(c.DeltaV.Y, 5), c.Vel.Z + Fx.DivPow2(c.DeltaV.Z, 5));
        c.AngVel = new Vec3(c.AngVel.X + Fx.DivPow2(c.DeltaW.X, 5), c.AngVel.Y + Fx.DivPow2(c.DeltaW.Y, 5), c.AngVel.Z + Fx.DivPow2(c.DeltaW.Z, 5));
        if (k > 0)                                                                // oversteer: bleed the yaw rate
        {
            var wl = Mat3.MulCol(M, c.AngVel);
            wl.Y = Fx.Mul(wl.Y, k);
            c.LocalAngVel = wl;
            c.AngVel = Mat3.MulRow(wl, M);
        }
        if (c.Gear == 1 && Fx.Abs(e.GravLocal.Z) <= 0x7FFF)                       // neutral on the flat: coasting drag
        {
            int m = Fx.Abs(vl.Z) < 0x140000 || Fx.Abs(c.Steer) > 0x20 ? 0xFD70 : 0xFF7C;
            c.Vel = new Vec3(Fx.Mul(m, c.Vel.X), Fx.Mul(m, c.Vel.Y), Fx.Mul(m, c.Vel.Z));
            c.AngVel = new Vec3(Fx.Mul(m, c.AngVel.X), Fx.Mul(m, c.AngVel.Y), Fx.Mul(m, c.AngVel.Z));
        }
        c.LocalForce.X = Fx.DivPow2(c.LocalForce.X, 2);                           // telemetry for other modules
        c.LocalForce.Z = Fx.DivPow2(c.LocalForce.Z, 1);
    }

    /// <summary>
    /// Forces of one axle. Brake/drive lock (wheelspin limit or lock-up → <see cref="TireCurve"/>) [ref:0573]
    /// slip angle → lateral force (front: linear up to 16°·ld/12, rear: up to 2.0·ld/2), friction circle
    /// (<see cref="WheelSlip"/>), rotation back by the steering angle. Writes car +0x2FC, +0x324/+0x328, +0x364/+0x368.
    /// </summary>
    public static void Tires(CarObj c, DerivedCarSpecs sp, SimEnv e, ref SimAxle t)
    {
        var S = sp.Source;
        bool locked = false;
        t.Skid = 0;
        if (t.Steer != 0) { Rotate2d(t.Vx, t.Vz, t.Steer, out int nx, out int nz); t.Vx = nx; t.Vz = nz; }
        int f = t.Drive;
        if (f < 0 && t.Vz < 0)
        {
            if (c.ThrottleRamped <= 0x40 || c.Gear != 0) { f = Math.Max(f, t.Vz); locked = true; }
        }
        else if (f > 0 && t.Vz > 0 && (c.ThrottleRamped <= 0x40 || c.Gear < 2))
        {
            f = Math.Min(f, t.Vz);
            locked = true;
        }
        f = Fx.Mul(f, sp.GripMultiplier);
        t.Drive = f;
        int vlz = c.LocalVel.Z;
        bool abs = e.HasAbs ?? HasAbs(e.Model);
        bool slide = false;
        if (locked && Fx.Abs(f) > t.Load)
        {
            if (c.Handbrake != 0) slide = true;
            else if (!abs && vlz <= 0x280000 && c.BrakeRamped >= 0xEC && (Fx.Abs(vlz) >= 0x50000 || c.SlipState != 0)) slide = true;
            else
            {
                if (t.Load < f) f = t.Load;
                else if (-f != t.Load && f <= -t.Load) f = -t.Load;
                t.Drive = f;
                if (abs && vlz < 0x280000) t.Skid = e.RaceTick % 4 == 0 || (e.SkidAlways ?? e.Model == 7) ? 0x30000 : 0;
            }
        }
        else if (c.Handbrake != 0 && !t.Front && Fx.Abs(vlz) > 0x8000) slide = true;

        if (slide)
        {
            c.SlidingAxles += t.Front ? 1 : 2;
            TireCurve(ref t);
        }
        else
        {
            int a = 0;
            if (t.Vz != 0)
            {
                a = FxMath.Atan2_16(t.Vx, Fx.DivPow2(-t.Vz, 1));
                if (t.Vz > 0) a = t.Vx > 0 ? 0x8000 - a : t.Vx < 0 ? -0x8000 - a : a;
                a <<= 8;
            }
            if (t.Front)
            {
                int ld = Fx.Mul(t.Load, e.DfFront);
                int fy = Fx.Mul(Fx.Mul(Math.Min(Fx.Abs(a), 0x100000), ld), 0x1555);
                if (a < 0) fy = -fy;
                t.Fy = fy + Fx.DivPow2(e.GravLocal.X, 1);
                if (Fx.Abs(t.Vx) + Fx.Abs(t.Vz) < 0x200000)                      // low speed: never more than the tyre speed
                    t.Fy = t.Vx <= 0 ? -Math.Min(Fx.Abs(fy), Fx.Abs(t.Vx)) : Math.Min(Fx.Abs(fy), t.Vx);
            }
            else
            {
                int ld = Fx.Mul(t.Load, e.DfRear);
                int kk = Math.Max(Fx.Mul(vlz, 0xA3D), 0x8000);
                int fy = Fx.DivPow2(Fx.Mul(Math.Min(Math.Max(Fx.Abs(a), kk), 0x20000), ld), 1);
                if (a < 0) fy = -fy;
                fy += Fx.DivPow2(e.GravLocal.X, 1);
                t.Fy = t.Vx <= 0 ? -Math.Min(Fx.Abs(fy), Fx.Abs(t.Vx)) : Math.Min(Fx.Abs(fy), t.Vx);
            }
            t.Fz = t.Drive;
            t.F20 = 0;
            WheelSlip(c, sp, e, ref t);
            if (t.Steer != 0) { Rotate2d(t.Fy, t.Fz, -t.Steer, out int ny, out int nz); t.Fy = ny; t.Fz = nz; }
        }
        if (t.Front) c.Skid324 = t.Skid; else c.Skid328 = t.Skid;
    }

    /// <summary>Friction circle: limits (Fy, Fz) to the axle's grip [ref:0574]
    /// <c>L = FX(load, df)</c>; writes car +0x364 (front: L) and +0x368 (front: ±demand).</summary>
    public static void WheelSlip(CarObj c, DerivedCarSpecs sp, SimEnv e, ref SimAxle t)
    {
        if (e.SpecialRules0E ?? e.Model == 0xE) return;
        int ay = Fx.Abs(t.Fy), az = Fx.Abs(t.Fz);
        int mag = (az < ay ? ay : az) + ((az < ay ? az : ay) >> 2);              // max + min/4 (arithmetic shift)
        int ex = ay < 0x70001 ? (ay >> 2) + 0x70000 : ay + 0x1C000;
        int L = Fx.Mul(t.Load, t.Front ? e.DfFront : e.DfRear);
        if (t.Front)
        {
            c.FrontGripDemand = t.Fy < 1 ? -mag : mag;
            c.FrontGrip = L;
        }
        if (mag <= L) return;
        t.Skid = Fx.Abs(c.LocalVel.Z) > 0x50000 || c.SlipState != 0 ? mag - L : 0;
        bool spin = false;
        if ((c.Gear == 2 || c.Gear == 0) && c.ThrottleRamped != 0 && c.SlipState == 0 && !t.Front && ex < L)
        {
            spin = true;                                                          // rear wheelspin: less skid, keep Fz
            t.Skid = Fx.DivPow2(t.Skid, 3);
        }
        int d = mag - L;
        int dd, ll;
        if (c.GripClass < 3) { dd = Fx.DivPow2(d, 3); ll = Fx.DivPow2(L, 3); }
        else { dd = Fx.DivPow2(d, 2); ll = Fx.DivPow2(L, 2); }
        int r = ll <= dd ? ll : dd;
        int s = Fx.Div(L - r, mag);
        t.Fy = Fx.Mul(t.Fy, s);
        if (spin && c.GripClass < 3) return;
        t.Fz = Fx.Mul(t.Fz, s);
    }

    /// <summary>tyre sliding (lock-up / handbrake): force opposite to the tyre velocity [ref:0575]
    /// magnitude ≈ 7/8·load (q in 8.8 format; for |v| ≤ 0x100 q = |vx| as in the original).</summary>
    public static void TireCurve(ref SimAxle t)
    {
        int ax = Fx.Abs(t.Vx), az = Fx.Abs(t.Vz);
        int mag = az < ax ? ax + (az >> 2) : (ax >> 2) + az;
        int load = t.Load;
        int k = load - Fx.DivPow2(load, 3);
        t.Skid = load < mag ? mag - load : 0;
        int q = ax;
        if (Fx.Abs(mag) > 0x100) q = Fx.DivPow2(Fx.Div(k, mag), 8);
        t.Fy = Fx.DivPow2(t.Vx * q, 8);
        t.Fz = Fx.DivPow2(t.Vz * q, 8);
    }

    /// <summary>
    /// Sets grip μ = surface μ + road curvature term (change of the slice pitch [ref:0576]
    /// between the previous slice and this one; dot products WITHOUT rounding), and the axle downforce multipliers
    /// <c>DfFront</c> / <c>DfRear</c> = 1.0 + speed·aero (above 5 m/s; rear ×2 for models 0/0xE above 36 m/s under braking, else ×1.5). [ref:0577]
    /// </summary>
    public static void GripFactors(CarObj c, DerivedCarSpecs sp, SimEnv e)
    {
        var S = sp.Source;
        int ang = 0;
        if (e.Slices is { Length: > 0 } sl)
        {
            int i = c.Trk.Slice, p = i - 1 < 0 ? i - 1 + sl.Length : i - 1;
            var fi = sl[i].Forward; var fp = sl[p].Forward; var up = sl[p].Up;
            static int D(sbyte a, sbyte b) => Trunc16((long)(a << 9) * (b << 9));
            int cs = D(fp[0], fi[0]) + D(fp[1], fi[1]) + D(fp[2], fi[2]);
            int sn = D(up[0], fi[0]) + D(up[1], fi[1]) + D(up[2], fi[2]);
            ang = FxMath.Atan2(sn, cs);
        }
        int vlz = c.LocalVel.Z;
        int kv = Math.Min(Fx.Mul(Fx.Abs(vlz), 0xA3D), 0x20000);
        e.GripMu = SurfaceMu[e.ArcadeMode != 0 ? 1 : 0][c.GripClass] + Fx.Mul(kv, Trunc16((long)(ang << 17) * 0x28F));
        e.DfFront = e.DfRear = Fx.One;
        if (vlz > 0x50000)
        {
            e.DfFront = Fx.Mul(vlz, Fx.Mul(S.AerodynamicDownforceMultiplier, e.DownforceFront)) + Fx.One;
            int rear = Fx.Mul(e.DownforceRear, S.AerodynamicDownforceMultiplier);
            int r = (e.RearAeroBoost ?? (e.Model == 0 || e.Model == 0xE)) && vlz > 0x240000 && c.BrakeRamped != 0 ? rear * 2 : Fx.DivPow2(rear * 3, 1);
            e.DfRear = Fx.Mul(vlz, r) + Fx.One;
        }
    }
}
