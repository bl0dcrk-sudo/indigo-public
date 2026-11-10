namespace NFS.Core;

/// <summary>carSpecs fields read by <see cref="DriveControls.Filter"/> (p&lt;car&gt;.dat offsets; S.188 is derived elsewhere
/// passed in until that is ported). [ref:0387]</summary>
public sealed class DriveControlSpecs
{
    public int NumGears;                         // +0x04 (R + N + forward)
    public int GearShiftDelay;                   // +0x08 ticks
    public int Redline;                          // +0xF0 rpm
    public byte[] BrakeIncreasing = new byte[8]; // +0x114 ramp step per (brake >> 5)
    public byte[] BrakeDecreasing = new byte[8]; // +0x11C
    public int TurnInRamp;                       // +0x130
    public int TurnOutRamp;                      // +0x134
    public int[] GearSpeedFactor = new int[10];  // +0x188[gear] (derived): speed at redline = FX'(f, redline << 16)
    public int[] GearRpmFactor = new int[10];    // +0x168[gear] (derived): rpm = redline * FX(speed, f) / 65536
}

/// <summary>Race options used by the filter: <c>[idx]+0 == 1</c> (automatic) and car+0x21C cfg +0x10/+0x14/+0x18. [ref:0388]</summary>
public readonly record struct DriveOptions(bool Automatic, bool SteerRamp, bool SlowThrottle, bool BrakeRamp);

/// <summary>Signals written by the filter: throttle, brake, absolute steering (16.16, ≤ 1.0) [ref:0389]
/// ; <see cref="GearChanged"/> = the shift sound (-1,7,…) was requested. [ref:0390]</summary>
public readonly record struct DriveSignals(int Throttle, int Brake, int SteerAbs, bool GearChanged);

/// <summary>
/// Common to sim and arcade, called from the drive task: throttle/brake ramps [ref:0391]
/// gear change, steering ramp (checked against the original). [ref:0392]
/// </summary>
public static class DriveControls
{
    /// <summary>Automatic: before this race tick the requested gear is forced to D (2).</summary>
    public const int AutoDriveUntilTick = 0x23C;

    public static DriveSignals Filter(CarObj c, DriveControlSpecs s, DriveOptions o, bool raceStarted, int raceTick)
    {
        bool gearChanged = false;
        if (c.RaceState == 2)                                   // finished
        {
            c.ThrottleRamped = 0;
            c.BrakeRamped = 0xFF;
            c.Steer = 0;
        }
        else
        {
            int step = o.SlowThrottle ? 0x10 : 0x18;
            int dt = c.Throttle - c.ThrottleRamped;
            c.ThrottleRamped = (byte)(c.ThrottleRamped + Math.Clamp(dt, -step, step));

            if (!o.BrakeRamp) c.BrakeRamped = c.Brake;
            else
            {
                int db = c.Brake - c.BrakeRamped, i = c.BrakeRamped >> 5;
                c.BrakeRamped = (byte)(db < 0
                    ? c.BrakeRamped - Math.Min(-db, s.BrakeDecreasing[i])
                    : c.BrakeRamped + Math.Min(db, s.BrakeIncreasing[i]));
            }

            if (c.GearChangeCounter != 0) c.GearChangeCounter--;
            if (raceStarted) gearChanged = ChangeGear(c, s, o, raceTick);

            if (!o.SteerRamp) c.Steer = c.SteerTarget;
            else if ((c.SteerTarget >= 0 && c.Steer < 0) || (c.SteerTarget <= 0 && c.Steer > 0))
                c.Steer += c.Steer > 0 ? -Math.Min(c.Steer, s.TurnOutRamp) : Math.Min(-c.Steer, s.TurnOutRamp); // back to 0
            else
                c.Steer += Math.Clamp(c.SteerTarget - c.Steer, -s.TurnInRamp, s.TurnInRamp);
        }
        if (!raceStarted) c.BrakeRamped = 0xFF;
        if (c.EdgeHit == 2) c.ThrottleRamped = 0;

        int thr = Math.Min((c.ThrottleRamped + 1) * 0x10000 / 0xF8, 0x10000);
        int brk = Math.Min((c.BrakeRamped + 1) * 0x10000 / 0xF8, 0x10000);
        int steerAbs = Fx.Abs(c.Steer + 1) * 0x10000 / 128;
        return new DriveSignals(thr, brk, steerAbs, gearChanged);
    }

    private static bool ChangeGear(CarObj c, DriveControlSpecs s, DriveOptions o, int raceTick)
    {
        if (o.Automatic && raceTick < AutoDriveUntilTick) c.RequestedGear = 2;
        if (c.Gear == c.RequestedGear) return false;
        byte g;
        if (o.Automatic)
        {
            if (c.RequestedGear < 2) g = c.RequestedGear;
            else if (c.RequestedGear == 2 && c.Gear <= 1)
            {
                // D from N/R: highest gear whose redline speed is below the current forward speed (no rounding)
                g = 2;
                long redline = (long)(s.Redline << 16);
                for (int i = 2; i < s.NumGears; i++)
                    if ((int)((s.GearSpeedFactor[i] * redline) >> 16) < c.LocalVel.Z) g = (byte)i;
            }
            else return false;
        }
        else g = c.RequestedGear;                               // (manual only) [ref:0393]
        c.PrevGear = c.Gear;
        c.Gear = g;
        c.GearChangeCounter = (byte)s.GearShiftDelay;
        return true;
    }

    /// <summary>
    /// Sim automatic, only in a forward gear ≥ 2: [ref:0394]
    /// <c>r(g) = (redline * FX(car+0x304, S.168[g])) / 65536</c>; full throttle (&gt; 0xE666) or over-revving →
    /// up when <c>r(gear) &gt; redline−500</c> (not in top gear), else down two / one gear when that gear's rpm is
    /// below redline−1500; otherwise only the one-gear downshift is considered.
    /// Returns true when a gear change (and its sound(-1,7,…)) happened. [ref:0395]
    /// </summary>
    public static bool AutoShift(CarObj c, DriveControlSpecs s, int drvThrottle)
    {
        int gear = c.Gear;
        if (gear < 2) return false;
        int up = gear < s.NumGears - 1 ? gear + 1 : gear;
        int dn = gear > 2 ? gear - 1 : gear;
        int dn2 = gear > 3 ? gear - 2 : gear;
        int Rpm(int g) => Fx.DivPow2(s.Redline * Fx.Mul(c.SignedSpeed, s.GearRpmFactor[g]), 16);
        int r = Rpm(gear), rDn = Rpm(dn), rDn2 = Rpm(dn2);
        int low = s.Redline - 1500;
        if (drvThrottle > 0xE666 || Fx.Abs(r) > s.Redline)
        {
            if (r > s.Redline - 500 && gear < s.NumGears - 1)
                return up != gear && Shift(c, s, up);
            if (rDn2 < low && dn2 != gear) return Shift(c, s, dn2);
        }
        return rDn < low && dn != gear && Shift(c, s, dn);
    }

    private static bool Shift(CarObj c, DriveControlSpecs s, int g)
    {
        c.PrevGear = c.Gear;
        c.Gear = (byte)g;
        c.GearChangeCounter = (byte)s.GearShiftDelay;
        return true;
    }
}
