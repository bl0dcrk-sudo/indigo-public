namespace NFS.Data.Car;

/// <summary><c>p&lt;name&gt;.dat</c> (356 B) → CarSpecs, read sequentially. [ref:0968]
/// Values are LE int 16.16 ("Fixed") unless named *Int / byte arrays. Names from P*.DES.</summary>
public sealed record CarSpecs(
    int Mass,                      // +0x00 Fixed
    int NumGears,                  // +0x04 Integer (reverse + neutral + forward)
    int GearShiftDelay,            // +0x08 Integer (ticks)
    int[] VelocityToRpm,           // +0x0C Fixed[8]
    int[] GearEfficiency,          // +0x2C Fixed[8]
    int[] TorqueCurve,             // +0x4C Fixed[41], 256 rpm steps
    int Redline,                   // +0xF0 Integer rpm
    int MaxVelocity,               // +0xF4 Fixed m/s
    int FrontDriveRatio,           // +0xF8 Fixed
    int MaxBrakingDeceleration,    // +0xFC Fixed
    int FrontBiasBrakeRatio,       // +0x100 Fixed
    byte[] GasIncreasingCurve,     // +0x104 u8[8]
    byte[] GasDecreasingCurve,     // +0x10C u8[8]
    byte[] BrakeIncreasingCurve,   // +0x114 u8[8]
    byte[] BrakeDecreasingCurve,   // +0x11C u8[8]
    int WheelBase,                 // +0x124 Fixed
    int FrontGripBias,             // +0x128 Fixed
    int MaxSteeringAcceleration,   // +0x12C Fixed
    int TurnInRamp,                // +0x130 Integer
    int TurnOutRamp,               // +0x134 Integer
    int LateralAccelerationGripMultiplier, // +0x138 Fixed
    int AerodynamicDownforceMultiplier,    // +0x13C Fixed
    // NB: reads the file sequentially but stores these two SWAPPED: file +0x140 → memory +0x144 [ref:0969]
    // (engine braking in), file +0x144 → memory +0x140. [ref:0970]
    // Offsets here are file offsets; physics code must use GasOffFactor where the original reads carSpecs +0x144.
    int GasOffFactor,              // +0x140 Fixed (in memory at carSpecs +0x144)
    int GTransferFactor,           // +0x144 Fixed (in memory at carSpecs +0x140)
    int SlideMultiplier,           // +0x148 Fixed
    int SpinVelocityCap,           // +0x14C Fixed
    int SlideVelocityCap,          // +0x150 Fixed
    int SlideAssistanceFactor,     // +0x154 Integer (angle units 0x400)
    int PushFactor,                // +0x158 Integer
    int LowTurnFactor,             // +0x15C Fixed
    int HighTurnFactor)            // +0x160 Fixed
{
    public const int Size = 356;

    public static CarSpecs Parse(byte[] d)
    {
        Bin.Require(d.Length >= Size, $"carSpecs: {d.Length} < {Size} B");
        int I(int o) => Bin.S32(d, o);
        byte[] B8(int o) => d.AsSpan(o, 8).ToArray();
        return new CarSpecs(I(0), I(4), I(8), Bin.S32Array(d, 0x0C, 8), Bin.S32Array(d, 0x2C, 8), Bin.S32Array(d, 0x4C, 41),
            I(0xF0), I(0xF4), I(0xF8), I(0xFC), I(0x100), B8(0x104), B8(0x10C), B8(0x114), B8(0x11C),
            I(0x124), I(0x128), I(0x12C), I(0x130), I(0x134), I(0x138), I(0x13C), I(0x140), I(0x144), I(0x148), I(0x14C),
            I(0x150), I(0x154), I(0x158), I(0x15C), I(0x160));
    }

    public static double Fx(int v) => v / 65536.0;
}

/// <summary><c>&lt;model&gt;.dat</c> (524 B = 131 int, CARDATA.VIV) — AI driving parameters. [ref:0971]
/// Accel curves: 56 int = 112 × s16 each (sim, arcade).</summary>
public sealed record CarAiModel(
    int GearShiftTime,          // [0]   → car+0x3B4
    int Unknown_1,              // [1]   → car+0x3BC [prawd. max rpm]
    int[] GearSpeeds,           // [2..8] → car+0x3D0[7] gear speed thresholds
    short[] SimAccelCurve,      // [9..64] 112 × s16 → car+0x408
    int SimMaxLateralAccel,     // [65]  → car+0x550
    int SimMaxYawAccel,         // [66]  → car+0x554
    short[] ArcadeAccelCurve,   // [67..122]
    int ArcadeMaxLateralAccel,  // [123]
    int ArcadeMaxYawAccel,      // [124]
    int Unknown_125,            // [125] → car+0x4E8
    int Unknown_126,            // [126] → car+0x4F0
    int Unknown_127,            // [127] → car+0x4F4
    int Unknown_128,            // [128] → car+0x4F8 = FXDIV(x, (+0x4F0 − +0x4F4) << 16)
    int TrafficSpeedMultiplier, // [129] → car+0x560
    int Unknown_130)            // [130] → car+0x15C
{
    public const int Size = 524;

    public static CarAiModel Parse(byte[] d)
    {
        Bin.Require(d.Length >= Size, $"model dat: {d.Length} < {Size} B");
        int I(int i) => Bin.S32(d, 4 * i);
        return new CarAiModel(I(0), I(1), Bin.S32Array(d, 8, 7), Bin.S16Array(d, 4 * 9, 112), I(65), I(66),
            Bin.S16Array(d, 4 * 67, 112), I(123), I(124), I(125), I(126), I(127), I(128), I(129), I(130));
    }

    public short[] AccelCurve(bool arcade) => arcade ? ArcadeAccelCurve : SimAccelCurve;
}

/// <summary>simtune.dat (232 B): 2 sets (sim, arcade) × 29 int. In the file each set is 21 values [ref:0972]
/// then 4 interleaved pairs (slice distance, speed boost multiplier); in memory (0x74 B/set) [ref:0973]
/// [0..20] as read, [21..24] distances, [25..28] multipliers. <see cref="Values"/> uses the memory layout.</summary>
public sealed record SimTuneSet(int[] Values)
{
    public const int Count = 29;
    public int this[int i] => Values[i];
    public int TelepoleLaunchValue => Values[0];
    public ReadOnlySpan<int> GlueDistances => Values.AsSpan(21, 4);
    public ReadOnlySpan<int> GlueMultipliers => Values.AsSpan(25, 4);
}

public sealed record SimTune(SimTuneSet Sim, SimTuneSet Arcade)
{
    public const int Size = 2 * SimTuneSet.Count * 4;

    public SimTuneSet For(bool arcade) => arcade ? Arcade : Sim;

    public static SimTune Parse(byte[] d)
    {
        Bin.Require(d.Length >= Size, $"simtune: {d.Length} < {Size} B");
        var sets = new SimTuneSet[2];
        int p = 0;
        for (int s = 0; s < 2; s++)
        {
            var v = new int[SimTuneSet.Count];
            for (int i = 0; i < 21; i++, p += 4) v[i] = Bin.S32(d, p);
            for (int k = 0; k < 4; k++, p += 8) { v[21 + k] = Bin.S32(d, p); v[25 + k] = Bin.S32(d, p + 4); }
            sets[s] = new SimTuneSet(v);
        }
        return new SimTune(sets[0], sets[1]);
    }
}
