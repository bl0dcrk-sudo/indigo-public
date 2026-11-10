namespace NFS.Core;

/// <summary>Inputs of the engine step: setup downforce <c>cfg+0x1C/+0x20</c> (1.0 = neutral) [ref:0538]
/// player boost (<c>&amp; 4</c> and model &lt; 0xF) and local gravity <c>z</c>. [ref:0539]</summary>
public readonly record struct SimEngineEnv(int Throttle, int DownforceFront = Fx.One, int DownforceRear = Fx.One,
    bool PlayerBoost = false, int GravLocalZ = 0);

/// <summary>
/// Engine part of the SIM driving model: engine rpm from the wheel [ref:0540]
/// speed (vl.z · S.0C[gear]), torque from the 41-point curve (S.4C, 256 rpm steps), wheelspin, engine braking and aero
/// drag; returns the drive force along local z (checked against the original). [ref:0541]
/// The step itself, the tyre model and the grip factors are <see cref="SimModel"/>.
/// </summary>
public static class SimEngine
{
    public static int TorqueAt(DerivedCarSpecs sp, int idx) => sp.Source.TorqueCurve[Math.Clamp(idx, 0, 40)];   // [ref:0542]

    /// <summary>Updates c.Rpm, c.Handbrake (over-rev ×1.5), c.SlipState/+0x2F8 (wheelspin); [ref:0543]
    /// <paramref name="wheelspinBoost"/> = car+0x2F8. Auto shifting is the caller's job (as in DriveControls.AutoShift).</summary>
    public static int DriveForce(CarObj c, DerivedCarSpecs sp, in SimEngineEnv e, out bool wheelspinBoost,
        Action<CarObj>? autoShift = null)
    {
        int F0 = sp.Redline;
        bool over = false;
        wheelspinBoost = false;
        if (F0 < c.Rpm) { over = true; if (F0 / 2 + F0 < c.Rpm) c.Handbrake = 1; }
        int k = Fx.DivPow2(e.DownforceFront + e.DownforceRear - 0x20000, 3) + Fx.One;
        int vi = Fx.DivPow2(Fx.Mul(c.LocalVel.Z, k), 16);
        int aero = Fx.Mul(sp.AeroDrag, vi * vi * vi);
        if (e.PlayerBoost) aero /= 2;
        int tgt = Fx.Mul(F0, e.Throttle);

        if (c.Gear == 1 || c.GearChangeCounter != 0 || c.AirTicks != 0)                 // neutral / shifting / airborne
        {
            if (c.Rpm < tgt && c.GearChangeCounter == 0) c.Rpm = Math.Min(c.Rpm + 500, tgt);
            else if (c.GearChangeCounter == 0) c.Rpm = Math.Max(c.Rpm - 200, tgt);
            else c.Rpm = Math.Max(c.Rpm - 50, 0);
            c.SlipState = 0;
            return -aero;
        }
        autoShift?.Invoke(c);
        int g = c.Gear;
        int wr = Fx.DivPow2(Fx.Mul(c.LocalVel.Z, sp.VelocityToRpm[g]), 16);
        int tq = over ? sp.Source.TorqueCurve[Fx.DivPow2(F0, 8)] : TorqueAt(sp, Fx.DivPow2(c.Rpm, 8));
        int F = Fx.Mul(tq, sp.GearThrust[g]);
        if (!e.PlayerBoost) F = Math.Min(F, g == 2 ? 0xA0000 : 0x80000);
        int d = tgt - wr;
        if (Fx.Abs(d) < 125 && tgt < F0 - 300) d = 0;
        c.SlipState = 0;
        int diff = c.Rpm - wr;
        bool spin = (diff > 1250 && c.GearChangeCounter == 0 && g <= 4)
            || (g > 1 && c.SignedSpeed < -0x1999 && c.ThrottleRamped > 0x40)
            || (g == 0 && c.SignedSpeed > 0x1999 && c.ThrottleRamped > 0x40);
        if (spin)                                                                       // wheelspin
        {
            wheelspinBoost = e.PlayerBoost;
            F = Fx.Mul(F, e.Throttle + Fx.One);
            c.SlipState = 1;
            int st = g == 2 ? 5 : g == 3 ? 25 : 75;
            c.Rpm -= Math.Min(st, diff);
            c.Rpm = Math.Min(c.Rpm, tgt);
        }
        else if (d < 0)                                                                 // engine braking
        {
            F = -Fx.Mul(F, sp.Source.GasOffFactor);                                   // carSpecs +0x144 = file +0x140 (see CarSpecs)
            if ((e.GravLocalZ > 0 && g > 1 && F < 0 && g < 3) || (e.GravLocalZ < 0 && g == 0 && F > 0)) F /= 2;
            int lim = Fx.DivPow2((int)(((long)(sp.GearSpeedFactor[g] << 3) * 0x28000000) >> 16), 16);
            c.Rpm += Math.Min(-diff, lim);
            c.Rpm = Math.Max(c.Rpm, tgt);
        }
        else if (d == 0) { c.Rpm = wr; F = aero; }
        else                                                                            // accelerating
        {
            if (diff > 200) c.Rpm -= 200; else if (diff < -300) c.Rpm += 200; else c.Rpm = wr;
            c.Rpm = Math.Min(c.Rpm, tgt);
            F = Fx.Mul(F, e.Throttle);
            F = Fx.Mul(F, Math.Min(Fx.Mul(Fx.Abs(c.SlipAngle), 0x8000) + Fx.One, 0x28000));
        }
        if (c.Rpm < 0)
        {
            int t = -(c.LocalVel.Z * 32);
            if (!((F > 0 && t > 0 && F > t) || (F < 0 && t < 0 && F < t))) { c.Rpm = 0; return t - aero; }
        }
        return F - aero;
    }
}
