using System.Buffers.Binary;
using NFS.Core;
using NFS.Data.Car;
using NFS.ModApi;

namespace NFS.Game.Cars;

/// <summary>The SI values of a car converted to 16.16 and range checked (no template involved). Input of
/// <see cref="SiPhysicsConverter.Apply"/>.</summary>
public sealed record SiFixed(int Mass, int NumGears, int[] VelocityToRpm, int[] TorqueCurve, int Redline, int MaxVelocity,
    int FrontDriveRatio, int? BrakeDeceleration, int WheelBase, int GripScale);

/// <summary>
/// Adapter SI (<see cref="SiPhysics"/>) to the original 356 B CarSpecs / 524 B CarAiModel (openspec modding-api-cars D1, 3.4).
/// Stage 1 <see cref="TryConvert"/>: every SI value to 16.16 with a range check (the error names the car.json field).
/// Stage 2 <see cref="Apply"/>: writes those values over the bytes of a vanilla template car of the same class; every field not listed
/// there stays as in the template. Not to be confused with <c>NFS.Core.CarSpecsDerive</c> (the game's own, run later by the sim). [ref:1554]
/// Determinism: only IEEE + - * / and Math.Floor/Round are used (no transcendental functions), so every machine gets the same bytes.
/// </summary>
public static class SiPhysicsConverter
{
    // Allowed ranges: min/max over the 15 player cars of CARDATA.VIV (mass 1040..1480 kg, redline 6200..8600 rpm, top speed 74..123 m/s,
    // peak torque 358..990 Nm, wheelbase 2.40..2.90 m, brake 9.5..18 m/s2, rpm per m/s 61..407) widened by a generous margin.
    public const double MassMin = 300, MassMax = 6000;
    public const double RedlineMin = 2000, RedlineMax = 10240;           // 10240 = 40 * 256, the end of the torque table
    public const double TopSpeedKmhMin = 40, TopSpeedKmhMax = 600;
    public const double TorqueMin = 50, TorqueMax = 3000;
    public const double PowerMin = 10, PowerMax = 2000;
    public const double EngineRpmMin = 500;
    public const double WheelbaseMin = 1.5, WheelbaseMax = 4.5;
    public const double BrakeMin = 4, BrakeMax = 30;
    public const double GripMin = 0.5, GripMax = 1.6;
    public const double GearRatioMin = 0.3, GearRatioMax = 6, FinalDriveMin = 0.5, FinalDriveMax = 10;
    public const double WheelRadiusMin = 0.2, WheelRadiusMax = 0.6;
    public const double RpmPerMsMin = 15, RpmPerMsMax = 1200;
    public const int MaxForwardGears = 6;                                // CarSpecs has 8 slots: reverse + neutral + 6
    const int TorqueSteps = 41, TorqueStepRpm = 256;

    /// <summary>Vanilla car whose specs are the base of a mod car of the class: A = LGT1 (7), B = ISDE (11), C = F355 (2); D..G = F355 as well (the slowest of the original; the SI values overwrite its performance). Middle-of-the-range
    /// cars of their class with 8 gear slots; their drive layout does not matter (it is overwritten).</summary>
    public static int TemplateNumber(CarRaceClass c) => c switch { CarRaceClass.A => 7, CarRaceClass.B => 11, _ => 2 };

    /// <summary>Whole conversion for the race loader: reads the template of <paramref name="raceClass"/> through
    /// <paramref name="readCarFile"/> (CARDATA.VIV entry by name) and returns the 356 B / 524 B pair.</summary>
    /// <exception cref="InvalidDataException">Out-of-range value or missing template.</exception>
    public static RawPhysics ToRaw(SiPhysics si, CarRaceClass raceClass, Func<string, byte[]?> readCarFile)
    {
        if (!TryConvert(si, out var fx, out var error)) throw new InvalidDataException(error);
        int t = TemplateNumber(raceClass);
        string model = NFS.Core.Constants.RaceConstants.CarNames[t];
        var ts = readCarFile(Race.RaceLoader.SpecsFile(t, model, 0));
        var ta = readCarFile(Race.RaceLoader.AiModelFile(t, model));
        if (ts is not { Length: CarSpecs.Size } || ta is not { Length: CarAiModel.Size })
            throw new InvalidDataException($"template car {model} is not available");
        var (specs, ai) = Apply(fx!, ts, ta);
        return new RawPhysics(specs, ai);
    }

    /// <summary>SI to 16.16 with range checks. On failure <paramref name="error"/> is "field 'name': ..." (car.json spelling).</summary>
    public static bool TryConvert(SiPhysics si, out SiFixed? result, out string? error)
    {
        result = null;
        string? e = null;
        double mass = Check("massKg", si.MassKg, MassMin, MassMax, ref e);
        double redline = Check("redlineRpm", si.RedlineRpm, RedlineMin, RedlineMax, ref e);
        double topKmh = Check("topSpeedKmh", si.TopSpeedKmh, TopSpeedKmhMin, TopSpeedKmhMax, ref e);
        double wheelBase = Check("wheelbaseM", si.WheelbaseM, WheelbaseMin, WheelbaseMax, ref e);
        double power = Check("powerKw", si.PowerKw, PowerMin, PowerMax, ref e);
        double powerRpm = Check("powerRpm", si.PowerRpm, EngineRpmMin, redline, ref e);
        double torque = Check("torqueNm", si.TorqueNm, TorqueMin, TorqueMax, ref e);
        double torqueRpm = Check("torqueRpm", si.TorqueRpm, EngineRpmMin, powerRpm, ref e);   // the torque peak is not above the power peak
        double fin = Check("finalDrive", si.FinalDrive, FinalDriveMin, FinalDriveMax, ref e);
        double radius = Check("wheelRadiusM", si.WheelRadiusM, WheelRadiusMin, WheelRadiusMax, ref e);
        double grip = Check("tyreGrip", si.TyreGrip ?? 1.0, GripMin, GripMax, ref e);
        double brake = si.BrakeDecelerationMs2 is { } b ? Check("brakeDecelerationMs2", b, BrakeMin, BrakeMax, ref e) : 0;
        if (e is null && si.Drive is not (CarDrive.Rear or CarDrive.Front or CarDrive.All)) e = "field 'drive': unknown value";

        var ratios = si.GearRatios ?? [];
        if (e is null && (ratios.Count < 1 || ratios.Count > MaxForwardGears))
            e = $"field 'gearRatios': {ratios.Count} gears, must be 1..{MaxForwardGears}";
        var v2r = new double[8];
        if (e is null)
        {
            double k = fin * 30 / (Math.PI * radius);                  // rpm per (m/s) = ratio * final * 60 / (2 pi r)
            for (int g = 0; g < ratios.Count && e is null; g++)
            {
                double r = Check($"gearRatios[{g}]", ratios[g], GearRatioMin, GearRatioMax, ref e);
                if (e is null && g > 0 && r >= ratios[g - 1]) e = $"field 'gearRatios[{g}]': {r} must be below the previous ratio {ratios[g - 1]}";
                v2r[g + 2] = r * k;
                Check($"gearRatios[{g}] (rpm per m/s)", v2r[g + 2], RpmPerMsMin, RpmPerMsMax, ref e);
            }
            double rev = si.ReverseGearRatio == 0 ? 1.1 * ratios[0] : si.ReverseGearRatio;
            Check("reverseGearRatio", rev, GearRatioMin, GearRatioMax, ref e);
            v2r[0] = -rev * k;
            // the top gear must be able to reach the declared top speed before the rev limiter
            double reach = redline / v2r[ratios.Count + 1] * 3.6;      // km/h at the limiter in top gear
            if (e is null && reach < 0.9 * topKmh)
                e = $"field 'topSpeedKmh': {topKmh} km/h is not reachable, the top gear at {redline} rpm gives {reach:0.#} km/h";
        }
        int[] curve = e is null ? BuildTorqueCurve(torque, torqueRpm, power, powerRpm, redline, ref e) : [];
        if (e is null)
        {
            var v2rFx = new int[8];
            for (int i = 0; i < 8; i++) v2rFx[i] = Fix($"gearRatios (slot {i})", v2r[i], ref e);
            int mFx = Fix("massKg", mass, ref e), vFx = Fix("topSpeedKmh", topKmh / 3.6, ref e);
            int wFx = Fix("wheelbaseM", wheelBase, ref e), gFx = Fix("tyreGrip", grip, ref e);
            int? brFx = si.BrakeDecelerationMs2 is null ? null : Fix("brakeDecelerationMs2", brake, ref e);
            if (e is null)
            {
                result = new SiFixed(mFx, ratios.Count + 2, v2rFx, curve, (int)Math.Round(redline, MidpointRounding.AwayFromZero), vFx,
                    si.Drive switch { CarDrive.Front => Fx.One, CarDrive.All => Fx.One / 2, _ => 0 }, brFx, wFx, gFx);
            }
        }
        error = e;
        return e is null;
    }

    static int[] BuildTorqueCurve(double tpk, double rpmT, double kw, double rpmP, double redline, ref string? e)
    {
        double tpw = kw * 30000 / (Math.PI * rpmP);                    // P / omega, Nm
        if (tpw > tpk * 1.01) { e = $"field 'powerKw': {kw} kW at {rpmP} rpm needs {tpw:0.#} Nm, above torqueNm {tpk}"; return []; }
        if (rpmP == rpmT && tpw < tpk * 0.99) { e = $"field 'powerKw': {kw} kW at the torque peak {rpmT} rpm gives {tpw:0.#} Nm, not torqueNm {tpk}"; return []; }
        double tred = redline > rpmP ? 0.9 * tpw : tpw;
        var pts = new List<(double X, double Y)> { (0, 0.55 * tpk), (rpmT, tpk) };
        if (rpmP > rpmT) pts.Add((rpmP, tpw));
        if (redline > pts[^1].X) pts.Add((redline, tred));
        double xEnd = (TorqueSteps - 1) * TorqueStepRpm;
        if (xEnd > pts[^1].X) pts.Add((xEnd, 0.3 * tred));
        var curve = new int[TorqueSteps];
        for (int i = 0; i < TorqueSteps; i++)
        {
            double x = i * TorqueStepRpm, y = pts[^1].Y;
            for (int k = 1; k < pts.Count; k++)
                if (x <= pts[k].X)
                {
                    var (x0, y0) = pts[k - 1]; var (x1, y1) = pts[k];
                    y = x1 == x0 ? y1 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);
                    break;
                }
            curve[i] = Fix($"torqueCurve[{i}]", y, ref e);
        }
        return curve;
    }

    /// <summary>Writes <paramref name="fx"/> over copies of the template files and derives the AI model. Fields written:
    /// specs +0x00 mass, +0x04 gear count, +0x0C rpm per m/s[8], +0x2C gear efficiency (1.0 for every used gear: the template's values
    /// compensate its own ratios), +0x4C torque[41], +0xF0 redline, +0xF4 top speed, +0xF8 front drive share (rear 0, front 1, all 0.5),
    /// +0xFC braking (only if given), +0x124 wheelbase, +0x138 grip multiplier (template x tyreGrip). AI (524 B): [1] = redline;
    /// [2..8] = speed at the rev limit per forward gear (redline / rpm per m/s, 0 for unused); accel curves (sim and arcade, 112 x s16, one
    /// bucket per m/s) are resampled so the template's top speed lands on the new one and multiplied by (power/mass) / (template power/mass),
    /// limited to 0.25..4; lateral accelerations x tyreGrip. Everything else stays from the template.</summary>
    public static (byte[] Specs, byte[] Ai) Apply(SiFixed fx, byte[] templateSpecs, byte[] templateAi)
    {
        if (templateSpecs.Length != CarSpecs.Size) throw new ArgumentException("template CarSpecs must be 356 B", nameof(templateSpecs));
        if (templateAi.Length != CarAiModel.Size) throw new ArgumentException("template CarAiModel must be 524 B", nameof(templateAi));
        var tSpecs = CarSpecs.Parse(templateSpecs);
        var tAi = CarAiModel.Parse(templateAi);
        var s = (byte[])templateSpecs.Clone();
        var ai = (byte[])templateAi.Clone();
        static void W(byte[] d, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o), v);
        W(s, 0x00, fx.Mass);
        W(s, 0x04, fx.NumGears);
        for (int i = 0; i < 8; i++) { W(s, 0x0C + 4 * i, fx.VelocityToRpm[i]); W(s, 0x2C + 4 * i, i < fx.NumGears ? Fx.One : 0); }
        for (int i = 0; i < TorqueSteps; i++) W(s, 0x4C + 4 * i, fx.TorqueCurve[i]);
        W(s, 0xF0, fx.Redline);
        W(s, 0xF4, fx.MaxVelocity);
        W(s, 0xF8, fx.FrontDriveRatio);
        if (fx.BrakeDeceleration is { } br) W(s, 0xFC, br);
        W(s, 0x124, fx.WheelBase);
        W(s, 0x138, Scale(tSpecs.LateralAccelerationGripMultiplier, fx.GripScale));

        W(ai, 4 * 1, fx.Redline << 16);
        for (int k = 0; k < 7; k++)
        {
            int g = k + 2;
            W(ai, 4 * (2 + k), g < fx.NumGears ? (int)(((long)fx.Redline << 32) / fx.VelocityToRpm[g]) : 0);
        }
        static double PowerPerMass(int[] torque, int mass)
        {
            long best = 0;
            for (int i = 0; i < torque.Length; i++) best = Math.Max(best, (long)torque[i] * i);   // torque x rpm / 256
            return best / (mass / 65536.0);
        }
        double amp = Math.Clamp(PowerPerMass(fx.TorqueCurve, fx.Mass) / PowerPerMass(tSpecs.TorqueCurve, tSpecs.Mass), 0.25, 4.0);
        double stretch = tSpecs.MaxVelocity / (double)fx.MaxVelocity;
        PutCurve(ai, 4 * 9, tAi.SimAccelCurve, stretch, amp);
        PutCurve(ai, 4 * 67, tAi.ArcadeAccelCurve, stretch, amp);
        W(ai, 4 * 65, Scale(tAi.SimMaxLateralAccel, fx.GripScale));
        W(ai, 4 * 123, Scale(tAi.ArcadeMaxLateralAccel, fx.GripScale));
        return (s, ai);
    }

    static int Scale(int v, int factor) => (int)(((long)v * factor) >> 16);

    static void PutCurve(byte[] ai, int offset, short[] t, double stretch, double amp)
    {
        for (int i = 0; i < 112; i++)
        {
            double pos = i * stretch;
            int lo = (int)Math.Floor(pos);
            double v = lo > 111 ? 0 : lo == 111 ? t[111] : t[lo] + (t[lo + 1] - t[lo]) * (pos - lo);
            BinaryPrimitives.WriteInt16LittleEndian(ai.AsSpan(offset + 2 * i), (short)Math.Clamp(Math.Round(v * amp), 0, short.MaxValue));
        }
    }

    /// <summary>Round to 16.16 (half away from zero); error when it does not fit int32.</summary>
    static int Fix(string field, double v, ref string? error)
    {
        if (error is not null) return 0;
        double scaled = Math.Round(v * 65536, MidpointRounding.AwayFromZero);
        if (double.IsNaN(scaled) || scaled > int.MaxValue || scaled < int.MinValue) { error = $"field '{field}': {v} does not fit 16.16"; return 0; }
        return (int)scaled;
    }

    /// <summary>Range check; the first error wins (later checks are skipped). Returns the value.</summary>
    static double Check(string field, double v, double min, double max, ref string? error)
    {
        if (error is null && (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max))
            error = $"field '{field}': {v:0.###} is outside {min:0.###}..{max:0.###}";
        return v;
    }
}
