using NFS.Data.Car;

namespace NFS.Core;

/// <summary>Race/setup inputs: <c>&amp; 4</c> together with [ref:0382]
/// <c>cfg+0 (model) &lt; 0xF</c> (player "boost": thrust ×2, 45 % front drive, grip ×1.25), and the setup fields
/// <c>cfg+0x2C</c> (final drive, 0 = none) / <c>cfg+0x24</c> (steering scale) — both ignored in arcade.</summary>
public readonly record struct DeriveOptions(bool Arcade, bool PlayerBoost, int FinalDrive = 0, int SteerScale = Fx.One);

/// <summary>carSpecs fields changed or appended by the derivation below (offsets in the in-memory carSpecs). [ref:0383]</summary>
public sealed class DerivedCarSpecs
{
    public CarSpecs Source = null!;              // file fields not changed by the derive step
    public int NumGears;                         // +0x04
    public int Redline;                          // +0xF0
    public int InvRedline;                       // +0x164 = 0x10000 / redline (integer division)
    public int MaxTorque;                        // +0x1D8 = max(torque curve)
    public int[] VelocityToRpm = new int[8];     // +0x0C[g] (divided by the final drive in sim)
    public int[] GearSpeedFactor = new int[8];   // +0x188[g] = FXDIV(1.0, +0x0C[g]) (0x28F when the ratio is 0)
    public int[] GearThrust = new int[8];        // +0x1A8[g] = FX(FXDIV(FXDIV(+0x0C, mass), 10.0), efficiency)
    public int[] GearRpmFactor = new int[8];     // +0x168[g] = FXDIV(1.0, (+0x188 * (redline << 16)) >> 16)
    public int FrontDriveRatio;                  // +0xF8 (0x7333 with boost)
    public int GripMultiplier;                   // +0x138 (×1.25 with boost)
    public int AeroDrag;                         // +0x1C8 = FXDIV(force at v_max, vi³)
    public int YawArm;                           // +0x1CC = FX(wheelbase/2, 0x648) << 8
    public int InvYawArm;                        // +0x1D0 = FXDIV(1.0, +0x1CC)
    public int InvGrip;                          // +0x1D4 = FXDIV(1.0, +0x138)
    public int MaxSteering;                      // +0x12C (×cfg.24 in sim)

    /// <summary>Fills the <see cref="DriveControlSpecs"/> fields used by DriveControls.Filter (S.168/S.188 per gear). [ref:0384]</summary>
    public DriveControlSpecs ToDriveControlSpecs(CarSpecs s)
    {
        var d = new DriveControlSpecs
        {
            NumGears = s.NumGears, GearShiftDelay = s.GearShiftDelay, Redline = s.Redline,
            BrakeIncreasing = (byte[])s.BrakeIncreasingCurve.Clone(), BrakeDecreasing = (byte[])s.BrakeDecreasingCurve.Clone(),
            TurnInRamp = s.TurnInRamp, TurnOutRamp = s.TurnOutRamp,
        };
        for (int g = 0; g < NumGears && g < 8; g++)
        {
            d.GearSpeedFactor[g] = GearSpeedFactor[g];
            d.GearRpmFactor[g] = GearRpmFactor[g];
        }
        return d;
    }
}

/// <summary>Derived carSpecs fields (checked against the original). [ref:0385]</summary>
public static class CarSpecsDerive
{
    public static DerivedCarSpecs Derive(CarSpecs s, DeriveOptions o)
    {
        var d = new DerivedCarSpecs
        {
            Source = s, NumGears = s.NumGears, Redline = s.Redline,
            InvRedline = 0x10000 / s.Redline,
            MaxTorque = 0, FrontDriveRatio = s.FrontDriveRatio, GripMultiplier = s.LateralAccelerationGripMultiplier,
            MaxSteering = s.MaxSteeringAcceleration,
        };
        foreach (int t in s.TorqueCurve) if (d.MaxTorque < t) d.MaxTorque = t;   // starts at 0 (signed compare)
        Array.Copy(s.VelocityToRpm, d.VelocityToRpm, 8);

        for (int g = 0; g < s.NumGears; g++)
        {
            if (!o.Arcade && o.FinalDrive != 0) d.VelocityToRpm[g] = Fx.Div(d.VelocityToRpm[g], o.FinalDrive);
            int r = d.VelocityToRpm[g];
            d.GearSpeedFactor[g] = r == 0 ? 0x28F : Fx.Div(Fx.One, r);
            int th = Fx.Div(Fx.Div(r, s.Mass), 10 * Fx.One);
            th = Fx.Mul(th, s.GearEfficiency[g]);
            if (o.PlayerBoost) th *= 2;
            d.GearThrust[g] = th;
            int k = (int)(((long)d.GearSpeedFactor[g] * (s.Redline << 16)) >> 16);   // no rounding here
            d.GearRpmFactor[g] = Fx.Div(Fx.One, k);
        }
        if (o.PlayerBoost)
        {
            d.FrontDriveRatio = 0x7333;
            d.GripMultiplier = Fx.Mul(d.GripMultiplier, 0x14000);
        }

        int top = s.NumGears - 1;
        int i = Fx.DivPow2(Fx.DivPow2(Fx.Mul(s.MaxVelocity, d.VelocityToRpm[top]), 16), 8);
        int fmax = Fx.Mul(s.TorqueCurve[i], d.GearThrust[top]);
        int vi = Fx.DivPow2(s.MaxVelocity, 16);
        d.AeroDrag = Fx.Div(fmax, vi * vi * vi);

        d.YawArm = Fx.Mul(s.WheelBase / 2, 0x648) << 8;
        d.InvYawArm = Fx.Div(Fx.One, d.YawArm);
        d.InvGrip = Fx.Div(Fx.One, d.GripMultiplier);
        if (!o.Arcade) d.MaxSteering = Fx.Mul(d.MaxSteering, o.SteerScale);
        // = 0: caller's global, not modelled here. [ref:0386]
        return d;
    }
}
