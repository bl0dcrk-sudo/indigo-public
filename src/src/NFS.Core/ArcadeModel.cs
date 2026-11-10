namespace NFS.Core;

/// <summary>Per-tick values read by the arcade model. [ref:0242]
/// Value ±0x200. [ref:0243]
/// <c>x</c> (gravity (0,−10,0) in the drive frame) and the race-config model <c>cfg+0</c>. [ref:0244]</summary>
public struct ArcadeEnv
{
    public int Throttle, Brake, SteerAbs;
    public int RoadCurveAhead;
    public int GravLocalX;
    public int Model;
    /// <summary><c>CarTraits.SpecialRules0E</c> (set by RaceRunner); null = <c>Model == ModelSpecial</c>.</summary>
    public bool? SpecialRules0E;
    /// <summary>Output: steering gain. [ref:0245]</summary>
    public int SteerGain;
    /// <summary>The cached steering term. For model 0x0E the original never writes it and reads [ref:0246]
    /// whatever an earlier call left there (bug). We keep it as explicit state: every non-0x0E call stores its
    /// <c>FX(gain · steerMul[A])</c> here, a 0x0E call reuses the last value (0 at start). Deterministic, not bit-exact
    /// with the real stack garbage (unknowable without a trace of the original).</summary>
    public int StaleSteerTerm;
    /// <summary>Mask <c>&amp; (player 0 ? 0x10 : 0x20)</c>: steering assist. [ref:0247]</summary>
    public bool SteerAssist;
    /// <summary>Race config +0x38 (front-end tag 0x21): driving aids. [ref:0248]
    /// Set when the driving aids are enabled. [ref:0249]</summary>
    public bool PlayerAssist;
    /// <summary>As <see cref="NFS.Data.Ai.AiSpeeds.Raw"/> (2 B per 2 slices; even byte = [ref:0250]
    /// target speed m/s) for the auto-drift part; null = speed file not loaded (the original [ref:0251]
    /// prints "AI_SPEED file not loaded" and skips that part).</summary>
    public byte[]? AiSpeeds;
    /// <summary>Track slices (XBID 15) for the steering assist; null → assist off.</summary>
    public NFS.Data.Track.TrackSlice[]? Slices;
    /// <summary>Track reverse flag (0/1). [ref:0252]</summary>
    public int TrackReverse;
    /// <summary>Track &lt; 9: runtime table[track·0x1C] (filled at runtime; [ref:0253]
    /// value unknown statically), else 1. Default 1 [hipoteza].</summary>
    public int LaneSide;
}

/// <summary>Arcade driving model ("target velocity" model). [ref:0254]
/// Sets target yaw rate +0x30C (checked against the original's code). [ref:0255]</summary>
public static class ArcadeModel
{
    public const int ModelSpecial = 0x0E;

    // steerMul[6], steerRate[6] (copied to the stack in the original) [ref:0256]
    private static readonly int[] SteerMul = [0x10000, 0x10000, 0x9999, 0xE666, 0x18000, 0x10000];
    private static readonly int[] SteerRate = [0x1999, 0x1999, 0x51E, 0xCCC, 0x1999, 0];

    /// <summary>Writes c+0x30C/+0x368 (target yaw rate), +0x364, +0x324, may zero +0x2B8; [ref:0257]
    /// sets <see cref="ArcadeEnv.SteerGain"/>.</summary>
    public static void Steer(CarObj c, DerivedCarSpecs sp, ref ArcadeEnv e)
    {
        var src = sp.Source;
        int vz = c.LocalVel.Z, avz = Fx.Abs(vz);
        int s = c.SlipState, a = Fx.Abs(s);

        int k = Fx.DivPow2(sp.MaxSteering, 6);
        if (e.SpecialRules0E ?? e.Model == ModelSpecial) k = Fx.Mul(k, 0x18000);
        if (avz < 0x50000) k = Fx.Mul(Fx.Mul(avz, 0x3333), k);
        else if (vz > 0x140000)
            k = Fx.Div(k, Fx.Mul(avz, c.ThrottleRamped < 0x80 ? src.LowTurnFactor : src.HighTurnFactor) + 0x10000);
        if (c.SlidingAxles == 1 || c.SlidingAxles == 3) k = Fx.DivPow2(k, 2);

        int rc = e.RoadCurveAhead, g1, g2;
        if ((rc > 0x40 && s == 3) || (rc < -0x40 && s == -3))
        {
            int arc = Fx.Abs(rc), m;
            if (arc > 0x80) m = Fx.DivPow2((arc - src.SlideAssistanceFactor) * 0x10000, 9) + 0x10000;
            else m = Math.Clamp(Fx.DivPow2((arc - src.SlideAssistanceFactor / 2) * 0x10000, 7) + 0x8000, 0xC000, 0x10000);
            g1 = Fx.Mul(k, m);
            // +0x198 = +0x188[4] (3rd gear speed factor); the product is truncated (no rounding)
            int vtop = (int)(((long)sp.GearSpeedFactor[4] * (sp.Redline << 16)) >> 16);
            g2 = Math.Clamp(Fx.Div(avz, vtop), 0x8000, 0x10000);
        }
        else { g1 = k; g2 = 0xC000; }
        e.SteerGain = Fx.Mul(g1, g2);

        int lean = Fx.DivPow2((int)(((long)Fx.Mul(e.GravLocalX, 0x30A3) * 0x100000) >> 16), 16);
        int st = Math.Clamp(c.Steer + lean, -127, 127);
        int y = Fx.DivPow2(k, 7) * st;
        if (vz < 0) y = -y;
        c.FrontGrip = Fx.Abs(y);
        if (c.GripClass > 2 && Fx.Abs(c.Steer) > 0x40 && avz > 0x50000)
        {
            if (c.Skid324 < 0x140000) c.Skid324 = Math.Min(c.Skid324 + 0x20000, 0x280000);
            c.FrontGrip = Fx.Mul(e.SteerAbs, c.FrontGrip);
        }

        if (a > 1 && a != 5)
        {
            int w;
            if (a == 4) w = c.Gear == 0 || c.SignedSpeed > 0x9FFFF ? 0x10000 : Math.Max(Fx.Mul(c.SignedSpeed, 0x1999), 0);
            else w = Math.Min(c.Gear == 1 ? e.Brake : e.Throttle + e.Brake, 0x10000);
            if (!(e.SpecialRules0E ?? e.Model == ModelSpecial)) e.StaleSteerTerm = Fx.Mul(e.SteerGain, SteerMul[a]);
            int sf = (st > 0 && s > 0) || (st < 0 && s < 0) ? e.SteerAbs : 0;
            int t = Fx.Mul(w, e.StaleSteerTerm);
            if (a == 2) t = Fx.Mul(sf, t);
            if (s < 1) t = -t;
            int tgt = y + t;
            int lim = SteerRate[a];
            if ((s == 2 && c.SteerTarget < 1) || (s == -2 && c.SteerTarget >= 0)) lim /= 2;
            int d = tgt - c.YawTarget;
            y = d > lim ? c.YawTarget + lim : d < -lim ? c.YawTarget - lim : tgt;
        }

        if (avz < 0x10000 && c.ThrottleRamped > 0x32 && c.Gear != 1)      // spin on the spot (donut)
        {
            if (c.LocalForce.Z < -0x51E) { y = c.Steer << 8; c.LocalVel.Z = 0; }
            else if (c.LocalForce.Z > 0x51E) { y = -c.Steer << 8; c.LocalVel.Z = 0; }
        }
        if ((e.SpecialRules0E ?? e.Model == ModelSpecial) && c.Steer == 0) y = 0;
        c.FrontGripDemand = y;
        c.YawTarget = y;
    }

    /// <summary>
    /// Branch "no contact" (+0x100 = UpDotN &lt; 0x199A: the car is tilted away from the road, e.g. [ref:0258]
    /// tumbling/on its roof). Instead of the normal step, it zeroes the slip target +0x310 [ref:0259]
    /// skid levels +0x324/+0x328 and SlipState +0x2F4, drops Rpm by 500 (to 0 below 0x1F5) and – when the body is lower
    /// than 0x8000 above the road (+0x15C), it damps the velocity/yaw rate (<see cref="AirDamping"/>). [ref:0260]
    /// Returns true when the branch was taken (the caller must then skip the normal drive step).
    /// </summary>
    public static bool NoContactDrive(CarObj c)
    {
        if (c.UpDotN >= 0x199A) return false;
        c.SlipAngle = 0;
        c.Skid324 = 0;
        c.Skid328 = 0;
        c.SlipState = 0;
        c.Rpm = c.Rpm < 0x1F5 ? 0 : c.Rpm - 500;
        if (c.HeightAboveRoad > 0x7FFF) return true;
        AirDamping(c);
        return true;
    }

    /// <summary>v *= 0.94 (0xF0A3); with UpDotN ≤ 0x3332 also ω.y *= 0.98 (0xFAE1). [ref:0261]</summary>
    public static void AirDamping(CarObj c)
    {
        c.Vel = new Vec3(Fx.Mul(c.Vel.X, 0xF0A3), Fx.Mul(c.Vel.Y, 0xF0A3), Fx.Mul(c.Vel.Z, 0xF0A3));
        if (c.UpDotN > 0x3332) return;
        c.AngVel.Y = Fx.Mul(c.AngVel.Y, 0xFAE1);
    }

    // u8[20] (surface → grip class; = all 9 with game option 8) [ref:0262]
    private static readonly byte[] SurfaceGripClass = [0, 0, 2, 2, 1, 2, 2, 1, 2, 2, 0, 1, 1, 2, 0, 2, 0, 0, 0, 0];

    /// <summary>Game option 8 = all 9: surface type → grip class 0..9 (types ≥ 20 → 9). [ref:0263]</summary>
    internal static int GripClassOf(int surfaceType, bool gameOption8) =>
        gameOption8 || (uint)surfaceType >= 20 ? 9 : SurfaceGripClass[surfaceType];

    internal static int GravSlope(int a) => (int)(((long)a * -0xA0000) >> 16);   // FX without rounding

    /// <summary>For (c, arcade=1): adds the slope component of gravity to F.z (+0x2AC). [ref:0264]</summary>
    public static void ApplyGravitySlope(CarObj c, bool arcade, bool? arcadeMode = null)
    {
        if ((c.Flags & (CarFlags)0x50) != 0 || c.BrakeRamped >= 10) return;
        int g = GravSlope(c.Orient.M21);                                         // forward.y (+0xE0)
        if (arcade) g = Fx.DivPow2(g, 3);
        g = Fx.Mul(g, c.TimeScale);
        int t = Fx.Mul(c.LocalForce.Z, g);
        if (t > 0x1999 || !(arcadeMode ?? arcade)) c.LocalForce.Z += g;         // the mode and the /8 argument are separate [ref:0265]
        else if (t < -0x1999) c.LocalForce.Z += Fx.DivPow2(g, 2);
    }

    /// <summary>
    /// Without the optional parts; the caller must first run (signals in [ref:0266]
    /// <paramref name="e"/>) and (<see cref="ArcadeEnv.RoadCurveAhead"/>) beforehand. [ref:0267]
    /// Not included (TODO): options 0x10/0x20. [ref:0268]
    /// The plain path is always taken. [ref:0269]
    /// Writes Δv/Δω (+0x28C/+0x298) and adds them to Vel/AngVel like the original.
    /// </summary>
    public static void Step(CarObj c, DerivedCarSpecs sp, ref ArcadeEnv e, bool raceStarted, bool gameOption8 = false,
        Action<CarObj>? autoShift = null)
    {
        c.GripClass = GripClassOf(c.SurfaceType, gameOption8);   // option 8 gives 9 [ref:0270]
        if (c.SlipState == 0 && c.SlidingAxles == 0)
        {
            c.Skid324 = Math.Max(c.Skid324 - 0x40000, 0);
            c.Skid328 = Math.Max(c.Skid328 - 0x40000, 0);
        }
        var M = c.DriveFrame;
        var v = c.Vel;
        var vl = new Vec3(Fx.Dot(v.X, v.Y, v.Z, M.M00, M.M01, M.M02), Fx.Dot(v.X, v.Y, v.Z, M.M10, M.M11, M.M12),
            Fx.Dot(v.X, v.Y, v.Z, M.M20, M.M21, M.M22));
        c.LocalVel = vl;
        c.SignedSpeed = FxMath.Hypot2(Fx.Abs(vl.X), Fx.Abs(vl.Z));
        if (vl.Z < 0) c.SignedSpeed = -c.SignedSpeed;
        e.GravLocalX = GravSlope(M.M01);                                         // (x; y/z unused here) [ref:0271]
        c.LocalForce = new Vec3(-vl.X, -vl.Y, -vl.Z);
        int ty = -c.AngVel.Y;

        Engine(c, sp, ref e, autoShift);
        if (e.PlayerAssist) PlayerAssist(c, sp, ref e);   // 125 — before the airborne early-out [ref:0272]
        if (c.AirTicks != 0 || c.ManualResetLock != 0)
        {
            c.LocalForce.X = 0; c.Heading = 0; c.Skid324 = 0; c.Skid328 = 0; c.LocalForce.Z = 0;
            return;
        }
        c.EdgeHit = 0;
        if (e.SteerAssist && e.Slices != null && c.WrongWayTicks == 0) SteerAssist(c, sp, ref e);   // options 0x10/0x20 [ref:0273]
        else Steer(c, sp, ref e);
        LateralSlide(c, sp, ref e);
        int s = c.SignedSpeed;
        if (s < 0xF0000 && s > 0 && c.Handbrake == 0) c.SlipAngle = Math.Clamp(c.SlipAngle, -s, s);
        c.LocalForce.Y = 0;
        ty += c.YawTarget;
        c.LocalForce.Z += c.SignedSpeed;
        c.LocalForce.X += c.SlipAngle;
        DebugTrace?.Invoke($"step target={c.SignedSpeed} vlz={vl.Z} Fz(before grav)={c.LocalForce.Z} gravX={e.GravLocalX}");
        ApplyGravitySlope(c, true);
        DebugTrace?.Invoke($"step Fz(after grav)={c.LocalForce.Z} Fx={c.LocalForce.X}");

        var F = c.LocalForce;                                                    // Δv = Mᵀ·F (each product rounded)
        c.DeltaV = new Vec3(Fx.Mul(F.X, M.M00) + TruncMul(F.Y, M.M10) + Fx.Mul(F.Z, M.M20),
            Fx.Mul(F.X, M.M01) + TruncMul(F.Y, M.M11) + Fx.Mul(F.Z, M.M21),
            Fx.Mul(F.X, M.M02) + TruncMul(F.Y, M.M12) + Fx.Mul(F.Z, M.M22));
        c.DeltaW = new Vec3(Fx.Mul(ty, M.M10), Fx.Mul(ty, M.M11), Fx.Mul(ty, M.M12));
        c.AngVel = new Vec3(c.AngVel.X + c.DeltaW.X, c.AngVel.Y + c.DeltaW.Y, c.AngVel.Z + c.DeltaW.Z);
        c.Vel = new Vec3(c.Vel.X + c.DeltaV.X, c.Vel.Y + c.DeltaV.Y, c.Vel.Z + c.DeltaV.Z);

        if (c.Gear == 1 && Fx.Abs(vl.Z) < 0x140000)                              // neutral: 0.995 damping
        {
            c.Vel = new Vec3(Fx.Mul(c.Vel.X, 0xFEB8), Fx.Mul(c.Vel.Y, 0xFEB8), Fx.Mul(c.Vel.Z, 0xFEB8));
            c.AngVel = new Vec3(Fx.Mul(c.AngVel.X, 0xFEB8), Fx.Mul(c.AngVel.Y, 0xFEB8), Fx.Mul(c.AngVel.Z, 0xFEB8));
        }
        c.Heading = 0;
        c.LocalForce.X <<= 3;
        c.LocalForce.Z <<= 3;
        int ax = Fx.Abs(c.LocalVel.X), az = Fx.Abs(c.LocalVel.Z);
        c.SignedSpeed = az < ax ? ax + (az >> 2) : az + (ax >> 2);
        if ((Fx.Abs(c.SignedSpeed) < 0x3333 && c.ThrottleRamped < 0x40) || !raceStarted) { c.Skid328 = 0; c.Skid324 = 0; }
    }

    /// <summary>(without the pass-through of the second parameter): lateral lane target from slice bytes [ref:0274]
    /// +0x1F (lanes nibbles) and +0x20/+0x21. Side 1: unsigned ((b1F &amp; 0xF)·b21·0x8000)/2; else signed
    /// ((b1F &gt;&gt; 4)·b20·−0x8000)/2 (division, toward zero).</summary>
    public static int LaneTargetOffset(NFS.Data.Track.TrackSlice s, int laneSide)
    {
        if (laneSide == 1) return (int)((uint)(s.Lanes & 0xF) * s.HalfWidthRight * 0x8000u / 2);
        return (s.Lanes >> 4) * s.HalfWidthLeft * -0x8000 / 2;
    }

    /// <summary>(1:1 with the original): target lateral offset +0x394 = lane offset [ref:0275]
    /// steer·10/0x7C m; when |offset error| ≤ 3.5 m and +0x2B8 ≥ 0: yaw target +0x30C = (FX(FX(|v|,0x1999), spec.12C/64)/128)·
    /// clamp(curvature(slice+1,+2) + heading error + lane error, ±0x7F), +0x368 = 0; otherwise the plain path. [ref:0276]
    /// Only called with car+0x288 == 0, so "forward" (0 &lt; +0x288) == (TrackReverse == 0). [ref:0277]</summary>
    public static void SteerAssist(CarObj c, DerivedCarSpecs sp, ref ArcadeEnv e)
    {
        var sl = e.Slices!;
        int n = sl.Length, i0 = c.Trk.Slice;
        bool fwd = (0 < c.WrongWayTicks ? 1 : 0) == e.TrackReverse;
        int st = fwd ? c.Steer : -c.Steer;
        int tgt = LaneTargetOffset(sl[i0], e.LaneSide) + st * 0xA0000 / 0x7C;
        int err = c.LateralOffsetCur - tgt;
        c.LaneTargetOffset = tgt;
        int lane = Fx.DivPow2(err, 16);
        if (Fx.Abs(err) >= 0x38001 || c.LocalVel.Z < 0) { Steer(c, sp, ref e); return; }
        int i1, i2;
        if (fwd) { i1 = i0 + 1; if (i1 >= n) i1 -= n; i2 = i0 + 2; if (i2 >= n) i2 -= n; }
        else { i1 = i0 - 1; if (i1 < 0) i1 += n; i2 = i0 - 2; if (i2 < 0) i2 += n; }
        var s0 = sl[i0]; var s1 = sl[i1]; var s2 = sl[i2];
        int a1 = FxMath.Atan2(s1.X - s0.X, s1.Z - s0.Z);
        int a2 = FxMath.Atan2(s2.X - s1.X, s2.Z - s1.Z);
        int curv = FxMath.AngleDiff(a2, a1);
        if (Fx.Abs(curv) > 0x1E) curv /= 2;
        int head = FxMath.AngleDiff(a1, c.Heading);
        if (Fx.Abs(head) > 100) head /= 2;
        int gain = Fx.Mul(Fx.Mul(Fx.Abs(c.LocalVel.Z), 0x1999), sp.MaxSteering / 64);
        if (fwd) lane = -lane;
        int sum = Math.Clamp(curv + head + lane, -0x7F, 0x7F);
        c.FrontGripDemand = 0;
        c.YawTarget = gain / 128 * sum;
    }

    /// <summary>("driving aids", called when <see cref="ArcadeEnv.PlayerAssist"/> [ref:0278]
    /// ≠ 0) — 1:1 with the original (every product rounded FX unless noted below): [ref:0279]
    /// <list type="number">
    /// <item><b>Landing alignment, every car</b> (lowered onto the road while airborne: +0x15C height &lt; 1 m,
    ///   +0x14C ≠ 0, +0xAC (= Vel.y) &lt; 0, ground normal +0x198 &gt; 0.5 and carUp(+0xD0)·roadUp(+0x124) &gt; 0):
    ///   ω (+0xE8) <b>is overwritten</b> (not added to) with Mᵀ·((M·carUp) × (M·roadUp)) — mathematically
    ///   carUp × roadUp, the car turns towards the road normal as it touches down.</item>
    /// <item><b>Player auto-drift</b> (flags 0x04, on the ground, AI speed table loaded): when the forward speed
    ///   +0x2B8 reaches <c>(FX([(slice/2)*2] &lt;&lt; 16, 0.9) × (sp+0xF4 ÷ 65536)) ÷ 0x68</c>, SlipState [ref:0280]
    ///   (+0x2F4) is forced to ±3 by the sign of the yaw target (+0x30C ≥ 0x51E → +3, ≤ −0x51E → −3); a drift
    ///   already at ±3 with |carUp·roadUp| &gt; 0xD999 is cleared first.</item>
    /// </list>
    /// <paramref name="sp"/>+0xF4 = <c>carSpecs.MaxVelocity</c>; the 32-bit product and the /0x68 are
    /// truncating as in the original.</summary>
    public static void PlayerAssist(CarObj c, DerivedCarSpecs sp, ref ArcadeEnv e)
    {
        var M = c.DriveFrame;
        if (c.HeightAboveRoad < Fx.One && c.AirTicks != 0 && c.Vel.Y < 0 && M.M11 > 0x8000 &&
            Vec3.Dot(c.RoadFrame.Row1, c.Orient.Row1) > 0)
        {
            var u = Mat3.MulCol(M, c.Orient.Row1);        // M·carUp  (+0xD0..+0xD8)
            var n = Mat3.MulCol(M, c.RoadFrame.Row1);     // M·roadUp (+0x124..+0x12C)
            c.AngVel = Mat3.MulRow(Vec3.Cross(u, n), M);  // ω := Mᵀ·(u × n)
        }
        if ((c.Flags & CarFlags.LocalHuman) == 0) return;
        if (e.AiSpeeds == null) return;
        if (c.AirTicks != 0) return;
        int i = c.Trk.Slice / 2 * 2;                      // 1 byte per 2 slices, even byte = m/s [ref:0281]
        if (i >= e.AiSpeeds.Length) return;               // (the original allocates 2 B of slack here)
        int tgt = (int)((long)Fx.Mul(e.AiSpeeds[i] << 16, 0xE666) * Fx.DivPow2(sp.Source.MaxVelocity, 16)) / 0x68;
        int d = Vec3.Dot(c.RoadFrame.Row1, c.Orient.Row1);
        if (d <= 0) d = -d;                               // the original recomputes the dot and negates it
        if (d > 0xD999 && Fx.Abs(c.SlipState) == 3) { c.SlipState = 0; return; }
        if (tgt > c.LocalVel.Z) return;
        if (c.YawTarget <= -0x51E) c.SlipState = -3;
        else if (c.YawTarget >= 0x51E) c.SlipState = 3;
    }

    // slideRate[6], slideMul[6] [ref:0282]
    private static readonly int[] SlideRate = [0x1999, 0x10000, 0x4CCC, 0x4CCC, 0x8000, 0];
    private static readonly int[] SlideMul = [0, 0x10000, 0x28000, 0x30000, 0x40000, 0x10000];

    /// <summary><b>Target lateral speed</b> +0x310 (drift) [ref:0283]
    /// understeer "push" (slip state 5), counter-steer handling, skid levels.</summary>
    public static void LateralSlide(CarObj c, DerivedCarSpecs sp, ref ArcadeEnv e)
    {
        var src = sp.Source;
        int eTerm = 0;
        bool flag = false;
        if ((e.SpecialRules0E ?? e.Model == ModelSpecial) && Fx.Abs(c.SlipState) is 5 or 1 or 4) c.SlipState = 0;
        bool push;
        if (Fx.Abs(c.SlipState) == 3 && !(Fx.Abs(c.SignedSpeed) > 0xEFFFF && c.Rpm > 2999)) { c.SlipState = 0; push = true; }
        else push = c.SlipState == 0 || c.SlipState == 5;
        if (push)                                                                 // understeer "push"
        {
            int t16 = Fx.DivPow2(c.SignedSpeed, 16);
            int q = Fx.Abs(Fx.DivPow2(c.YawTarget, 9) * t16 * t16);
            eTerm = q;
            c.SlipState = 0;
            if (c.ThrottleRamped < 0x40)
            {
                eTerm = 0x10000 - Math.Max(q - src.PushFactor, 0);
                if (eTerm < 0x9999)
                {
                    c.Skid324 = Math.Min(c.Skid324 + 0x3333, 0x50000);
                    c.SlipState = 5;
                    c.FrontGrip /= 2;
                }
                if (eTerm < 0x8000) c.Skid328 = Math.Min(c.Skid328 + 0x3333, 0x50000);
            }
        }

        int lim = SlideRate[Fx.Abs(c.SlipState)];
        if ((Fx.Abs(c.SignedSpeed) < 0x50000 && c.ThrottleRamped < 0x20) || c.SlipState == 0 || c.ThrottleRamped == 0) lim *= 2;
        if ((c.YawTarget > -0x51E && c.SlipState == -3) || (c.YawTarget < 0x51E && c.SlipState == 3))   // counter-steer
        {
            flag = true;
            if (c.GripClass < 3)
            {
                int baseRate = SlideRate[Fx.Abs(c.SlipState)];
                lim = e.Throttle < 0xC001 ? Math.Min(lim * 4, baseRate * 4) : Math.Min(lim * 3, baseRate * 3);
            }
            if ((c.SlipState == -3 && c.SlipAngle < 0x4CCC) || (c.SlipState == 3 && c.SlipAngle > -0x4CCC)) { c.SlipState = 0; c.SlipAngle = 0; }
            else c.YawTarget = 0;
        }

        int t = 0, a = Fx.Abs(c.SlipState);
        if (a >= 2 && a != 5)
        {
            eTerm = Fx.Mul(c.YawTarget, Fx.Mul(src.SlideMultiplier, SlideMul[a]));
            t = -Fx.Mul(eTerm, c.SignedSpeed);
            if (Fx.Abs(c.SignedSpeed) < 0x50000) t /= 2;
            if (e.Throttle + e.Brake < 0x1999) lim *= 2;
            if (Fx.Abs(c.LocalVel.Z) < 0x50000) t = Fx.Mul(Fx.Abs(c.LocalVel.Z), 0x3333);
        }
        t += Fx.Mul(Fx.Abs(t), Fx.Mul(e.GravLocalX, 0x1999));
        int d = t - c.SlipAngle;
        c.SlipAngle = d > lim ? c.SlipAngle + lim : d < -lim ? c.SlipAngle - lim : c.SlipAngle + d;

        a = Fx.Abs(c.SlipState);
        if ((Fx.Abs(c.SlipAngle) < 0x3333 && !flag) || c.EdgeHit != 0)
        {
            if (a != 5 && c.SlipState != 1) c.SlipState = 0;
            c.SlipAngle = 0;
            return;
        }
        if ((a == 2 && e.Throttle < 0x8000) || (a > 2 && c.Rpm < 3000))
        {
            if (a != 5) c.SlipState = 0;
            return;
        }
        c.Skid328 = Bump(c.Skid328, 0x20000, 0x140000);
        if ((Fx.Abs(eTerm) > 0x4000 && a != 2) || Fx.Abs(eTerm) > 0x8000) c.Skid324 = Bump(c.Skid324, 0x20000, 0x140000);
    }

    // [arcade][grip class] (arcade row) [ref:0284]
    private static readonly int[] GripArcade = [0x10000, 0xFD70, 0xFAE1, 0xF851, 0xE666, 0xCCCC, 0xC000, 0xB333, 0xA666, 0x9999];

    /// <summary>Diagnostics hook (replay-compare --probe): internal values of the engine step.</summary>
    public static Action<string>? DebugTrace;

    private static int Torque(DerivedCarSpecs sp, int rpm) =>                 // (rpm/256) [ref:0285]
        sp.Source.TorqueCurve[Math.Clamp(Fx.DivPow2(rpm, 8), 0, 40)];

    private static int TruncMul(int a, int b) => (int)(((long)a * b) >> 16);  // 64-bit product, shifted right by 16, no rounding

    private static int WheelRpm(CarObj c, DerivedCarSpecs sp, int gear) =>
        Fx.DivPow2(sp.Redline * Fx.Mul(c.LocalVel.Z, sp.GearRpmFactor[gear]), 16);

    private static int Bump(int v, int add, int below) => v < below ? Math.Min(v + add, 0x280000) : v;

    /// <summary>rpm (+0x2F0), slip state, brake lock (+0x2FC), skid levels and the [ref:0286]
    /// <b>target longitudinal speed</b> +0x304. May override <see cref="ArcadeEnv.Brake"/> and +0x2D8. [ref:0287]
    /// <paramref name="autoShift"/> = when the race config says automatic (caller decides). [ref:0288]</summary>
    public static void Engine(CarObj c, DerivedCarSpecs sp, ref ArcadeEnv e, Action<CarObj>? autoShift = null)
    {
        var src = sp.Source;
        int grip = GripArcade[c.GripClass];
        int F0 = sp.Redline, dW = 0, wr, facc = 0;
        bool free = false;
        int B = src.MaxBrakingDeceleration * 2, brakeF = B;
        if (c.SignedSpeed < 0xA0000) brakeF = Fx.Mul(B, 0xC000);
        if (Fx.Abs(c.SlipState) > 2) brakeF /= 2;
        int D = Fx.Mul(B, 0xCCCC);
        int tgt = Fx.DivPow2(TruncMul(e.Throttle, F0 << 16), 16);

        if (c.Gear == 1 || c.AirTicks != 0)
        {
            wr = WheelRpm(c, sp, c.PrevGear);
            c.Rpm = tgt > c.Rpm ? Math.Min(c.Rpm + 600, tgt) : Math.Max(c.Rpm - 300, tgt);
            if (wr > 0) dW = -100; else if (wr < 0) dW = 100;
            free = true;
            if (c.SlipState == 1) c.SlipState = 0;
        }
        else
        {
            autoShift?.Invoke(c);
            int g = c.Gear;
            wr = WheelRpm(c, sp, g);
            if (wr < 0)
            {
                c.BrakeRamped = 0xFF; e.Brake = 0x10000;
                c.Skid328 = Bump(c.Skid328, 0x40000, 0x1E0000);
                free = true;
                if (g == 0 && c.LocalVel.Z > 0xA0000) { if (c.Steer > 16) c.SlipState = 4; else if (c.Steer < -16) c.SlipState = -4; }
            }
            if ((g == 2 || g == 0) && c.PrevGear == 1) { c.Rpm = tgt; c.PrevGear = (byte)g; }
            int diff = c.Rpm - wr;
            if (c.SlipState == 1) c.SlipState = 0;
            if (diff > 1500 && g <= 4 && e.Throttle > 0x8000 && Fx.Abs(c.SlipState) != 4 && c.EdgeHit == 0)
            {
                c.SlipState = 1;                                               // wheelspin at launch
                if (g == 2 && c.SignedSpeed > 0x50000) { if (c.Steer > 32) c.SlipState = 2; else if (c.Steer < -32) c.SlipState = -2; }
                int k = Fx.DivPow2((0x100 - c.ThrottleRamped) * 20, 8);
                c.Skid328 = 0x280000;
                k += g == 3 ? 0x37 : g == 4 ? 0x55 : 10;
                c.Rpm -= Fx.DivPow2(TruncMul(k << 16, grip), 16);
            }
            else if (diff > 200) c.Rpm -= 200;
            else if (diff < -200) c.Rpm += 200;
            else c.Rpm = Fx.Abs(wr);

            int F = 0;
            if (F0 < c.Rpm && g < sp.NumGears - 1)
            {
                if (!free)                                                     // over-rev
                {
                    F = -Fx.Mul(src.TorqueCurve[Fx.DivPow2(F0, 8)], sp.GearThrust[g]) / 2;
                    int lim = Fx.DivPow2(TruncMul(grip, 0x400000), 16);
                    if (lim < c.Steer) c.SlipState = 3; else if (c.Steer < -lim) c.SlipState = -3;
                }
            }
            else if (c.GearChangeCounter != 0) F = 0;
            else if (c.ThrottleRamped == 0)                                    // coasting: engine braking
            {
                int m = Fx.Mul(Fx.Abs(c.LocalVel.Z), 0x1999);
                int T = Fx.Mul(Torque(sp, c.Rpm), sp.GearThrust[g]);
                int eb = Fx.Mul(T, src.GasOffFactor);                         // carSpecs +0x144 = file +0x140 (see CarSpecs)
                bool against = (c.LocalVel.Z > 0 && g == 0) || (c.LocalVel.Z < 0 && g >= 2);
                F = Fx.Mul(m, against ? eb : -eb);
            }
            else if (Fx.Abs(tgt - wr) < 250)                                   // near target: P controller
                F = Fx.Mul(TruncMul((tgt - wr) * 0x10000, sp.InvRedline), TruncMul(sp.GearSpeedFactor[g], F0 << 16));
            else if (wr < tgt)                                                 // accelerating
            {
                int T = Fx.Mul(Torque(sp, c.Rpm), sp.GearThrust[g]);
                F = Fx.Mul(e.Throttle, T * 3 / 2);
                if (c.SlipState == 0 && grip < e.Throttle && c.GripClass > 2 && g < 4)
                {
                    c.SlipState = c.Steer > 0x40 ? 2 : c.Steer < -0x40 ? -2 : 1;
                    c.Skid328 = Bump(c.Skid328, 0x20000, 0x140000);
                }
                int a = Fx.Abs(c.SlipState);
                if (a == 3) F = Fx.Mul(F, Math.Min(Fx.Mul(e.Throttle, Fx.Abs(c.YawTarget * 2)) + 0x10000, 0x30000));
                else if (a == 1 || a == 2) F = Fx.Mul(F, 0x8000);
                F = Fx.Mul(F, grip);
            }
            else if (tgt < wr) F = -Fx.Mul(Fx.Mul(Torque(sp, c.Rpm), sp.GearThrust[g]), src.GasOffFactor); // carSpecs +0x144
            if (c.Handbrake != 0) F /= 2;
            dW = Fx.DivPow2(F0 * Fx.Mul(F, sp.GearRpmFactor[g]), 16);
            facc = F;
        }

        c.SlidingAxles = 0;                                                    // brakes [ref:0289]
        if (c.BrakeRamped != 0 || c.Handbrake != 0)
        {
            if (c.Handbrake != 0)
            {
                if (c.Steer > 0) c.SlipState = 4; else if (c.Steer < 0) c.SlipState = -4;
                c.BrakeRamped = 0x80; e.Brake = 0x8000;
            }
            else if (c.BrakeRamped > 0x20 && c.EdgeHit == 0)
            {
                int lim = Fx.DivPow2(TruncMul(grip, 0x400000), 16);
                if (c.YawTarget > 0 && lim < c.Steer && e.RoadCurveAhead > 0x19) c.SlipState = 3;
                else if (c.YawTarget < 0 && c.Steer < -lim && e.RoadCurveAhead < -0x19) c.SlipState = -3;
            }
            int bf = Fx.Mul(e.Brake, brakeF);
            if (c.LocalVel.Z >= 0) bf = -bf;
            facc += bf;
            bool ok = c.GripClass > 2 || c.Gear < 4;
            if (facc < -Fx.Mul(grip, D + 0x20000) && ok && Fx.Abs(c.SignedSpeed) > 0x3333)
            {
                c.SlidingAxles += 1;
                c.Skid324 = Bump(c.Skid324, 0x40000, 0x1E0000);
            }
            if ((facc < -Fx.Mul(grip, D) || c.Handbrake != 0) && ok && Fx.Abs(c.SignedSpeed) > 0x3333)
            {
                if (c.SlipState != 1) c.SlidingAxles += 2;
                if (c.Handbrake != 0) c.Skid328 = 0x280000; else c.Skid328 = Bump(c.Skid328, 0x40000, 0x1E0000);
            }
            bf = Fx.Mul(bf, grip);
            if (c.SlidingAxles == 3 && c.GripClass > 2) bf /= 2;
            int gb = c.Gear == 1 ? c.PrevGear : c.Gear;
            dW += Fx.DivPow2(F0 * Fx.Mul(bf, sp.GearRpmFactor[gb]), 16);
            if (dW < 0 && wr >= 0) { if (wr == 0) wr = 1; free = false; }
            else if (dW > 0 && wr <= 0) { if (wr == 0) wr = -1; free = false; }
        }

        DebugTrace?.Invoke($"engine g={c.Gear} prev={c.PrevGear} wr={wr} tgt={tgt} rpm={c.Rpm} dW={dW} facc={facc} free={free} slip={c.SlipState} thr={e.Throttle} gcc={c.GearChangeCounter} vlz={c.LocalVel.Z}");
        int step = Fx.DivPow2(dW, 5), old = wr;                                // wheel-rpm integration [ref:0290]
        wr += step;
        if (!free)
        {
            if (step < 0 && old > 0 && wr < 0) wr = 0;
            else if (step > 0 && old < 0 && wr > 0) wr = 0;
            else if (step < 0 && old == 0) wr = 0;
        }
        wr = step > 0 ? Math.Min(wr, F0) : Math.Max(wr, -F0);
        int r = Fx.Div(wr << 16, F0 << 16);
        int gs = c.Gear == 1 ? c.PrevGear : c.Gear;
        c.SignedSpeed = Fx.Mul(TruncMul(sp.GearSpeedFactor[gs], F0 << 16), r);
    }
}
