namespace NFS.Core;

/// <summary>
/// AI driving model after the original: the AI does not [ref:0056]
/// use the player's arcade/sim model but produces forces directly — in-control or out-of-control [ref:0057]
/// steering with TRAFCFG parameters <c>[91..101]</c> / <c>[102..112]</c>, longitudinal acceleration [ref:0058]
/// from the <c>&lt;model&gt;.dat</c> curve. [ref:0059]
/// Ported: the steering/force formulas, accel/brake and integration of v and ω. [ref:0060]
/// The caller supplies personality acceleration glue; the skill-dependent vmax cap remains separate.
/// Integration includes the slope force; the caller supplies LOD dt/ticks and beginner skill reduction. [ref:0061]
/// </summary>
public static class AiDriveModel
{
    // Checked after the overturned-car test: stop turning once the finished racer slows down. [ref:0062]
    public static bool FinishStop(CarObj c)
    {
        if ((c.Flags & CarFlags.Racer) == 0 || c.RaceState != 2 || Math.Abs((long)c.AiSpeed) >= 0x20000) return false;
        c.AngVel = default;
        ArcadeModel.AirDamping(c);
        return true;
    }
    /// <summary>TRAFCFG [91..101] / [102..112] (AI data files).</summary>
    public readonly record struct Params(int DlposToDlvel, int MaxDlvel, int DlvelToClacc, int MaxClacc, int DangleToDav,
        int MaxDav, int DavToAa, int MaxAa, int VelLimitRange, int LatVelLimitFactor, int AngVelLimitFactor)
    {
        public static Params From(ReadOnlySpan<int> v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10]);
        /// <summary>TRAFCFG.DAT defaults (in control): 5, 32, 4, 24, 1.25, 0.8, 5, 0.8, 20, 0.13, 0.013.</summary>
        public static readonly Params DefaultInControl = new(5 << 16, 32 << 16, 4 << 16, 24 << 16, 0x14000, 0xCCCC, 5 << 16,
            0xCCCC, 20 << 16, 0x2147, 0x0353);
        /// <summary>Defaults (out of control): 0, 16, 3, 24, 0.625, 0.8, 10, 0.8, 10, 0.32, 0.013.</summary>
        public static readonly Params DefaultOutOfControl = new(0, 16 << 16, 3 << 16, 24 << 16, 0xA000, 0xCCCC, 10 << 16,
            0xCCCC, 10 << 16, 0x51EB, 0x0353);
    }

    /// <summary>Output of one steering call: car-frame force/accel (+0x2A4..+0x2AC), road-frame force (+0x2C8..+0x2D0),
    /// angular accel (+0x298..+0x2A0, local), slip damping (+0x55C).</summary>
    public struct Forces
    {
        public Vec3 Car, Road, Ang;
        public int SlipDamping;
    }

    // clamp(x, ±m): first x >= m → m, then x <= −m → −m (order matters for m < 0).
    private static int Clamp(int x, int m) { if (x >= m) x = m; if (x <= -m) x = -m; return x; }

    /// <summary>15 m/s² for opponents (flag 8), else 12; ×2 in arcade; × glue when &gt; 1. [ref:0063]</summary>
    public static int BrakeDecel(CarObj c, bool opponent, bool arcade)
    {
        int d = opponent ? 15 << 16 : 12 << 16;
        if (arcade) d *= 2;
        return c.Glue > Fx.One ? (int)(((long)d * c.Glue) >> 16) : d;
    }

    /// <summary>(m/s², 16.16): curve <c>+0x408[min(|v|,0x6F)] &lt;&lt; 8</c>, 0 while shifting [ref:0064]
    /// ×1.3 when chasing (psycho); 0 above <paramref name="vmax"/> (also × glue). The caller applies the glue after the opponent/leading-human guards. [ref:0065]</summary>
    public static int Accel(CarObj c, int vmax = 0, int skillLevel = 1, int accelGlue = Fx.One)
    {
        if (c.ShiftCounter > 0) return 0;
        if (vmax > 0)                                          // speed cap, applied for car models < 0xF: [ref:0066]
        {
            if (Fx.Mul(vmax, c.Glue) < c.AiSpeed) return 0;
            if (vmax < c.AiSpeed) return 0;
        }
        int i = c.AiSpeed >> 16;
        if (i <= 0) i = -i;
        if (i >= 0x6F) i = 0x6F;
        int a = Fx.Mul(c.AccelCurve[i] << 8, accelGlue); // before beginner/psycho [ref:0067]
        if (skillLevel == 0 && c.Progress > 100) a = Fx.Mul(a, 0xC000); // beginner after start [ref:0068]
        if (c.PsychoTicks > 0) a = Fx.Mul(a, 0x14CCC);
        return a;
    }

    /// <summary>Out-of-control test: true unless the heading error is small, the car is not reversing and its speed is at least VelLimitRange − 3. [ref:0069]</summary>
    public static bool IsOutOfControl(CarObj c, in Params ooc) =>
        !(Math.Abs(c.HeadingError) < 0x74 && c.Direction != -1 && Math.Abs(c.AiSpeed) >= ooc.VelLimitRange - 3 * Fx.One);

    /// <summary><paramref name="dir"/> = −1 for wrong-way/reverse track. [ref:0070]</summary>
    public static Forces SteerInControl(CarObj c, in Params P, int dir, bool arcade, int vmax = 0, int skillLevel = 1, int accelGlue = Fx.One)
    {
        var f = new Forces();
        c.SlipDamping = 0;
        int dlat = c.LateralOffsetCur - c.SmoothedLaneOffset;
        int fs = Fx.Mul(c.LocalVel.Z, 0x62) + Fx.One;
        int latMax = Fx.Mul(c.MaxLatAccel, fs), k = Fx.Mul(P.DlvelToClacc, fs);
        if (c.Glue > Fx.One) { latMax = Fx.Mul(c.Glue, latMax); k = Fx.Mul(c.Glue, k); }
        int dav = Fx.Mul(0x80, P.DangleToDav) * -c.HeadingError;
        dav = Clamp(dav, P.MaxDav);
        int dlv = -Fx.Mul(dlat, P.DlposToDlvel);
        dlv = Clamp(dlv, P.MaxDlvel);
        dlv = Clamp(dlv, c.AiSpeedApprox);
        if (-P.VelLimitRange < c.AiSpeed && c.AiSpeed < P.VelLimitRange)
        {
            dlv = Clamp(dlv, Math.Abs(Fx.Mul(c.AiSpeedApprox, P.LatVelLimitFactor)));
            dav = Clamp(dav, Math.Abs(Fx.Mul(c.AiSpeedApprox, P.AngVelLimitFactor)));
        }
        int yaw = Clamp(-Fx.Mul(c.LocalAngVel.Y - dav, P.DavToAa), c.MaxYawAccel);
        int lat = Clamp(Fx.Mul(c.LateralSpeedAhead - dlv, k) * -dir, latMax);
        int lon;
        if (c.AiTargetSpeed == 0) lon = 0;
        else if (c.AiTargetSpeed * dir > c.AiSpeed * dir) { lon = Accel(c, vmax, skillLevel, accelGlue); c.BrakeRamped = 0; }
        else
        {
            lon = -BrakeDecel(c, (c.Flags & CarFlags.Opponent) != 0, arcade); c.BrakeRamped = 0xFF;
            if (c.LocalVel.Z < 0) lon = -lon;
        }
        int s = Math.Abs(c.LateralSpeedAhead) - SkidValue;
        c.Skid324 = c.Skid328 = s > 0 ? s : 0;
        if ((c.AiFlags & 0x1000) == 0) f.Road = new Vec3(lat * dir, 0, lon * dir); // road frame
        else f.Car = new Vec3(lat, 0, lon);                                          // wrong way: car frame
        f.Ang = new Vec3(0, yaw, 0);
        return f;
    }

    /// <summary>Lateral speed above which the car skids (TRAFCFG [90]). [ref:0071]</summary>
    public static int SkidValue { get; set; } = 11 << 16;

    /// <summary>Steering when out of control (slow / reversing / large heading error). [ref:0072]</summary>
    public static Forces SteerOutOfControl(CarObj c, in Params Q, int dir, bool arcade, int vmax = 0, int skillLevel = 1, int accelGlue = Fx.One)
    {
        var f = new Forces();
        c.SlipDamping = 0;
        int spd = Math.Abs(c.AiSpeed);
        int desS = c.Direction == -1 ? -c.AiTargetSpeed : c.AiTargetSpeed;
        int t = 0;
        if (c.AiSpeed <= -0x20000 || c.AiSpeed >= 0x20000)
            t = Math.Abs(Wrap(c.AiHeading - FxMath.Atan2(c.Vel.X, c.Vel.Z)));
        int g = Fx.One + CosQ(t + 0x100);                      // 1 − sin t
        if (spd > 0x120000) c.SlipDamping = Fx.One - Fx.Mul(0xCCC, Fx.One - g);
        int dav = Clamp(Fx.Mul(0x80, Q.DangleToDav) * -c.HeadingError, Q.MaxDav);
        int dlv = Clamp(Clamp(Fx.Mul(dav, 0xA0000), Q.MaxDlvel), c.AiSpeedApprox);
        if (-Q.VelLimitRange < c.AiSpeed && c.AiSpeed < Q.VelLimitRange)
        {
            dlv = Clamp(dlv, Math.Abs(Fx.Mul(c.AiSpeedApprox, Q.LatVelLimitFactor)));
            dav = Clamp(dav, Math.Abs(Fx.Mul(c.AiSpeedApprox, Q.AngVelLimitFactor)));
        }
        int yaw = -Fx.Mul(c.LocalAngVel.Y - dav, Q.DavToAa);
        if (spd > 0x120000) yaw /= 2;
        yaw = Clamp(yaw, c.MaxYawAccel);
        int lat = Clamp(-Fx.Mul(c.LocalVel.X - dlv, Q.DlvelToClacc), c.MaxLatAccel);
        int lon = 0;
        if (c.AiTargetSpeed != 0)
        {
            int w = c.LocalVel.Z * dir;
            if ((-Q.VelLimitRange < w && w <= 0 && w > desS) || (0 <= w && w < Q.VelLimitRange && w < desS))
            { lon = c.Direction * Accel(c, vmax, skillLevel, accelGlue); c.BrakeRamped = 0; }
            else if (spd > 0)
            {
                lon = -BrakeDecel(c, (c.Flags & CarFlags.Opponent) != 0, arcade); c.BrakeRamped = 0xFF;
                if (c.LocalVel.Z < 0) lon = -lon;
            }
        }
        lon = Fx.Mul(lon, g); lat = Fx.Mul(lat, g); yaw = Fx.Mul(yaw, Math.Max(g, 0x8000));
        c.Skid324 = c.Skid328 = spd > 0xF0000 ? spd : 0;
        if (spd < 0x20000) yaw = 0;
        f.Car = new Vec3(lat, 0, lon);
        f.Ang = new Vec3(0, yaw, 0);
        return f;
    }

    /// <summary>
    /// Per-tick update while reversing (+0x524 == −1): the reverse timer [ref:0073]
    /// (+0x528) is pushed back 0x18 ticks when the car is more than 10 m off the lane (+0x548) on the wrong side of the
    /// lateral target speed (+0x538); after +0x520 ticks the car goes forward again (+0x524 = 1); the target speed is
    /// clamped to ±20 m/s and the smoothed lane offset zeroed.
    /// </summary>
    public static void ReverseUpdate(CarObj c, int tick)
    {
        if (c.Direction != -1) return;
        if ((c.LateralOffsetCur < c.SmoothedLaneOffset - 0xA0000 && c.LateralSpeedAhead > 0)
            || (c.SmoothedLaneOffset + 0xA0000 < c.LateralOffsetCur && c.LateralSpeedAhead < 0))
            c.DirChangeTick = tick - 0x18;
        if (c.DirDuration < tick - c.DirChangeTick) { c.Direction = 1; c.DirChangeTick = tick; }
        if (c.AiTargetSpeed > 0x140000) c.AiTargetSpeed = 0x140000;
        if (c.AiTargetSpeed != -0x140000 && -c.AiTargetSpeed > 0x13FFFF) c.AiTargetSpeed = -0x140000;
        c.SmoothedLaneOffset = 0;
    }

    /// <summary>
    /// Toggles the direction (c, duration): reversing → forward once half the duration (+0x520/2) has passed [ref:0074]
    /// since +0x528; forward → reversing only when |+0x39C| &lt; 20 m/s and the same time has passed.
    /// </summary>
    public static void ReverseToggle(CarObj c, int duration, int tick)
    {
        if (c.Direction == -1 && c.DirDuration / 2 < tick - c.DirChangeTick)
        {
            c.Direction = 1; c.DirDuration = duration; c.DirChangeTick = tick;
            return;
        }
        if (c.Direction == 1 && c.DirDuration / 2 < tick - c.DirChangeTick)
        {
            if (Math.Abs(c.AiSpeed) > 0x13FFFF) return;
            c.Direction = -1; c.DirDuration = duration; c.DirChangeTick = tick;
        }
    }

    /// <summary>
    /// Off-road check: the lane (+0x500) is outside the roadway lanes [7 − hi, 6 + lo] (slice +0x1F nibbles) [ref:0075]
    /// for at most 4 LOD ticks (+0x54C accumulates <paramref name="lodTicks"/>) and the surface is steep (VROAD value ×2 ≤ 0xD999
    /// on a polygon off rails, else slice up.y·512 ≤ 0xD999) → true (back up).
    /// </summary>
    public static bool OffroadCheck(CarObj c, NFS.Data.Track.TrackSlice sl, int lodTicks)
    {
        bool onRoad = !(c.Lane < 7 - (sl.Lanes >> 4) || (sl.Lanes & 0xF) + 6 < c.Lane);
        if (onRoad) { c.OffRoadTicks = 0; return false; }
        c.OffRoadTicks += lodTicks;
        if (c.OffRoadTicks > 4) return false;
        if (c.Rail == 0 && c.Trk.RoadPoly != null)
        {
            int v = c.Trk.VRoad is short[] vr && vr.Length > 1 ? vr[1] : 0;
            if (0xD999 < v * 2) return false;
        }
        else if (0xD999 < sl.Up[1] * 0x200) return false;
        return true;
    }

    /// <summary>
    /// Gearbox and rpm (gear +0x2DA = AI gear index 0..6, rpm +0x2F0 – sound/HUD only; shifting blocks [ref:0076]
    /// via +0x3B8): outside a shift, shift up while <c>gs[g] &lt; |v|</c> (and gs[g+1] ≠ 0), down while [ref:0077]
    /// <c>|v| &lt; gs[g−1]</c>, each change restarts +0x3B8 = +0x3B4; target rpm [ref:0078]
    /// interpolates from base (0 in gear 0, else 3000) to +0x3BC by <c>(|v| − gs[g−1])·inv[g]</c>; during a shift 3000 and
    /// +0x3B8 −= LOD ticks; rpm moves 1/8 of the way (truncated).
    /// </summary>
    public static void GearboxRpm(CarObj c, int lodTicks = 1)
    {
        int target;
        if (c.ShiftCounter < 1)
        {
            int v = Math.Abs(c.AiSpeed);
            while (true)
            {
                bool up = c.GearSpeeds[c.Gear] < v;
                if (!up && !(v < UpshiftSpeed(c, c.Gear))) break;
                if (up)
                {
                    if (c.Gear + 1 >= 7 || c.GearSpeeds[c.Gear + 1] == 0) break;
                    c.Gear++;
                }
                else c.Gear--;
                c.ShiftCounter = c.ShiftTime;
            }
            int lo = UpshiftSpeed(c, c.Gear), b = c.Gear == 0 ? 0 : 0xBB80000;
            target = (Fx.Mul(Fx.Mul(v - lo, c.GearSpeedsInv[c.Gear]), c.MaxRpm - b) + b) >> 16;
        }
        else
        {
            target = 3000;
            c.ShiftCounter -= lodTicks;
        }
        if (c.Rpm < target) c.Rpm += (target - c.Rpm) / 8;
        else if (c.Rpm > target) c.Rpm -= (c.Rpm - target) / 8;
    }

    /// <summary>gs[g−1] (+0x3CC + 4g), 0 for g ≤ 0. [ref:0079]</summary>
    public static int UpshiftSpeed(CarObj c, int g) => g > 0 ? c.GearSpeeds[g - 1] : 0;

    /// <summary>COSQ(a) = cos from the quarter sine table without interpolation (a: 0x400 per turn).</summary>
    public static int CosQ(int a)
    {
        FxMath.SinCosTab(a & 0x3FF, out _, out int cos);
        return cos;
    }

    private static int Wrap(int a) { if (a < -0x200) a += 0x400; else if (a > 0x200) a -= 0x400; return a; }

    /// <summary>
    /// Δv = Mᵀ·F_car + Rᵀ·F_road (world), Δω = Mᵀ·A; v += Δv·dt, (slip damping per LOD [ref:0080]
    /// tick), ω += Δω·dt. Includes the slope force from ArcadeModel.ApplyGravitySlope. [ref:0081]
    /// </summary>
    public static void Integrate(CarObj c, in Forces f, int dt, int lodTicks = 1, bool arcade = true)
    {
        var M = c.Orient; var R = c.RoadFrame;
        // temporarily adds road F.z before the slope call, then subtracts it. [ref:0082]
        // The false argument disables the /8 factor; it still selects the uphill /4 reduction. [ref:0083]
        c.LocalForce = f.Car;
        c.LocalForce.Z += f.Road.Z;
        ArcadeModel.ApplyGravitySlope(c, false, arcade);
        c.LocalForce.Z -= f.Road.Z;
        Vec3 dv = MtMul(M, c.LocalForce) + MtMul(R, f.Road);
        Vec3 aw = MtMul(M, f.Ang);
        c.DeltaV = dv; c.DeltaW = aw;
        c.Vel += Vec3.Scale(dv, dt);
        if (c.SlipDamping != 0)
            for (int i = 0; i < lodTicks; i++) c.Vel = Vec3.Scale(c.Vel, c.SlipDamping);
        c.AngVel += Vec3.Scale(aw, dt);
        c.LocalForce.Z = 0; // clear the Z force at the end of the step [ref:0084]
    }

    /// <summary>
    /// Rail mode (+0x8C ≠ 0, car far from every human): no steering model; lateral speed +0x3CC [ref:0085]
    /// = (lane target 0x548 − lateral offset 0x3AC)·8; moves +0x39C toward [ref:0086]
    /// the target +0x398 by accel/brake · dt (clamped to the target); then v = right(0x118)·lat + orient forward
    /// (0xDC)·|+0x39C|, ω = 0, +0x2B8 = +0x3A0 and the orientation is the road frame
    /// (flipped when the car's wrong-way flag differs from the track-reverse flag). [ref:0087]
    /// </summary>
    public static void RailStep(CarObj c, int dt, bool trackReverse, bool arcade, int vmax = 0, int skillLevel = 1, int accelGlue = Fx.One)
    {
        c.RailLateralSpeed = (c.SmoothedLaneOffset - c.LateralOffsetCur) * 8; // lateral speed toward the lane target (+0x3CC) [ref:0088]
        bool wrongWay = (c.AiFlags & 0x1000) != 0;                           // byte +0x3A9 & 0x10
        int accel = Accel(c, vmax, skillLevel, accelGlue);
        int brake = BrakeDecel(c, (c.Flags & CarFlags.Opponent) != 0, arcade);
        // speed toward AiTargetSpeed: accelerate/brake along the track direction [ref:0089]
        if (wrongWay == trackReverse)
        {
            if (c.AiTargetSpeed < c.AiSpeed)
            {
                c.BrakeRamped = 0xFF;
                c.LocalForce.Z = brake;
                int v = c.AiSpeed - Fx.Mul(brake, dt);
                c.AiSpeed = c.AiTargetSpeed < v ? v : c.AiTargetSpeed;
            }
            else
            {
                c.LocalForce.Z = accel;
                c.AiSpeed += Fx.Mul(accel, dt);
                c.BrakeRamped = 0;
                if (c.AiTargetSpeed < c.AiSpeed) c.AiSpeed = c.AiTargetSpeed;
            }
        }
        else if (c.AiSpeed < c.AiTargetSpeed)
        {
            c.BrakeRamped = 0xFF;
            c.AiSpeed += Fx.Mul(brake, dt);
            c.LocalForce.Z = brake;
            if (c.AiTargetSpeed < c.AiSpeed) c.AiSpeed = c.AiTargetSpeed;
        }
        else
        {
            c.LocalForce.Z = accel;
            c.AiSpeed -= Fx.Mul(accel, dt);
            c.BrakeRamped = 0;
            if (c.AiSpeed <= c.AiTargetSpeed) c.AiSpeed = c.AiTargetSpeed;
        }
        // body: lateral speed along the road frame plus forward speed along the car's orientation [ref:0090]
        int lat = c.RailLateralSpeed, v39 = Math.Abs(c.AiSpeed);
        var R = c.RoadFrame; var M = c.Orient;
        c.Vel.X = Fx.Mul(lat, R.M00) + Fx.Mul(v39, M.M20);
        c.AngVel.X = 0;
        c.Vel.Y = Fx.Mul(lat, R.M01) + Fx.Mul(v39, M.M21);
        c.AngVel.Y = 0;
        c.Vel.Z = Fx.Mul(lat, R.M02) + Fx.Mul(v39, M.M22);
        c.LocalVel.Z = c.AiSpeedApprox;
        c.AngVel.Z = 0;
        // orientation: road frame, rotated 180° about the up axis when the wrong-way flag differs [ref:0091]
        if (wrongWay ^ trackReverse)
            c.Orient = new Mat3(-R.M00, -R.M01, -R.M02, R.M10, R.M11, R.M12, -R.M20, -R.M21, -R.M22);
        else c.Orient = R;
    }

    /// <summary>Mᵀ·v: each world component = sum of 3 FX in order x, y, z.</summary>
    private static Vec3 MtMul(in Mat3 m, Vec3 v) => new(
        Fx.Mul(m.M00, v.X) + Fx.Mul(m.M10, v.Y) + Fx.Mul(m.M20, v.Z),
        Fx.Mul(m.M01, v.X) + Fx.Mul(m.M11, v.Y) + Fx.Mul(m.M21, v.Z),
        Fx.Mul(m.M02, v.X) + Fx.Mul(m.M12, v.Y) + Fx.Mul(m.M22, v.Z));

    /// <summary>M·v (rows right/up/fwd).</summary>
    public static Vec3 MMul(in Mat3 m, Vec3 v) => new(
        Fx.Mul(m.M00, v.X) + Fx.Mul(m.M01, v.Y) + Fx.Mul(m.M02, v.Z),
        Fx.Mul(m.M10, v.X) + Fx.Mul(m.M11, v.Y) + Fx.Mul(m.M12, v.Z),
        Fx.Mul(m.M20, v.X) + Fx.Mul(m.M21, v.Y) + Fx.Mul(m.M22, v.Z));

    /// <summary>
    /// One AI step (heading part simplified; the target point is given [ref:0092]
    /// by the caller): local v/ω, heading error, lateral speed at the look-ahead slice, then in/out-of-control steering
    /// and integration. <paramref name="trackSpeed"/> = +0x39C (v · slice forward). Returns true when out of control.
    /// </summary>
    public static bool Step(CarObj c, Vec3 target, Vec3 rightAhead, in Params inCtl, in Params ooc, int dir, bool arcade, int dt, int trackSpeed, int vmax = 0, int skillLevel = 1, int lodTicks = 1, int accelGlue = Fx.One, bool psychoTarget = false)
    {
        c.LocalVel = MMul(c.Orient, c.Vel);
        c.LocalAngVel = MMul(c.Orient, c.AngVel);
        c.AiSpeed = trackSpeed;                                         // +0x39C: v along the track (sign = track direction)
        // +0x3A0 (AiSpeedApprox) is NOT recomputed here: it is written from the velocity at the START of [ref:0093]
        // the task (RaceRunner.StepCar); the wall hit in its prolog [ref:0094]
        // changes v before this point, but the steering limits keep using the pre-hit value.
        // while reversing (+0x524 != 1) the heading is that of the REAR of the car (forward row negated) [ref:0095]
        c.AiHeading = c.Direction != 1 ? FxMath.Atan2(-c.Orient.M20, -c.Orient.M22) : FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        if (!psychoTarget) c.ToTarget = target - c.Pos; // for a psycho chase, ToTarget was already supplied by the caller [ref:0096]
        c.TargetHeading = FxMath.Atan2(c.ToTarget.X, c.ToTarget.Z);
        c.HeadingError = Wrap(c.AiHeading - c.TargetHeading);
        c.LateralSpeedAhead = Fx.Mul(c.Vel.X, rightAhead.X) + Fx.Mul(c.Vel.Y, rightAhead.Y) + Fx.Mul(c.Vel.Z, rightAhead.Z);
        bool ooc_ = IsOutOfControl(c, ooc);
        var f = ooc_ ? SteerOutOfControl(c, ooc, dir, arcade, vmax, skillLevel, accelGlue) : SteerInControl(c, inCtl, dir, arcade, vmax, skillLevel, accelGlue);
        Integrate(c, f, dt, lodTicks, arcade);
        return ooc_;
    }
}
