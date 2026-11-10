using System.Buffers.Binary;
using NFS.Data.Car;
using NFS.Game.Cars;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>SI to 16.16 adapter: hand-computed conversions and rejected values (openspec modding-api-cars 3.4).</summary>
public class SiPhysicsConverterTests
{
    // 30 / (pi * 0.3) = 31.83098862 rpm per (m/s) per unit of overall ratio; torque rpm 15*256, power rpm 22*256, redline 26*256
    static SiPhysics Duster => new()
    {
        MassKg = 1300, PowerKw = 110, PowerRpm = 5632, TorqueNm = 250, TorqueRpm = 3840, RedlineRpm = 6656,
        GearRatios = [3.7, 2.0, 1.35, 1.0, 0.8, 0.65], FinalDrive = 4.0, WheelRadiusM = 0.3, TopSpeedKmh = 180,
        Drive = CarDrive.Front, WheelbaseM = 2.67,
    };

    static SiFixed Convert(SiPhysics si)
    {
        Assert.True(SiPhysicsConverter.TryConvert(si, out var fx, out var err), err);
        return fx!;
    }

    static double D(int v) => v / 65536.0;

    [Fact]
    public void Converts_Scalars()
    {
        var fx = Convert(Duster);
        Assert.Equal(1300 * 65536, fx.Mass);                    // kg -> 16.16
        Assert.Equal(8, fx.NumGears);                           // reverse + neutral + 6
        Assert.Equal(6656, fx.Redline);                         // integer rpm
        Assert.Equal(50 * 65536, fx.MaxVelocity);               // 180 km/h = 50 m/s
        Assert.Equal(65536, fx.FrontDriveRatio);                // front drive
        Assert.Equal((int)Math.Round(2.67 * 65536), fx.WheelBase);
        Assert.Equal(65536, fx.GripScale);                      // tyreGrip default 1
        Assert.Null(fx.BrakeDeceleration);
    }

    [Fact]
    public void Converts_Drive()
    {
        Assert.Equal(0, Convert(Duster with { Drive = CarDrive.Rear }).FrontDriveRatio);
        Assert.Equal(0x8000, Convert(Duster with { Drive = CarDrive.All }).FrontDriveRatio);
    }

    [Fact]
    public void Converts_OptionalGripAndBrake()
    {
        var fx = Convert(Duster with { TyreGrip = 1.25, BrakeDecelerationMs2 = 12.5 });
        Assert.Equal(0x14000, fx.GripScale);
        Assert.Equal(12 * 65536 + 0x8000, fx.BrakeDeceleration);
    }

    [Fact]
    public void Converts_GearRatiosToRpmPerMetrePerSecond()
    {
        var v = Convert(Duster).VelocityToRpm;
        Assert.Equal(3.7 * 4 * 31.83098862, D(v[2]), 3);        // first gear
        Assert.Equal(2.0 * 4 * 31.83098862, D(v[3]), 3);
        Assert.Equal(0.65 * 4 * 31.83098862, D(v[7]), 3);       // sixth gear
        Assert.Equal(0, v[1]);                                  // neutral
        Assert.Equal(-1.1 * 3.7 * 4 * 31.83098862, D(v[0]), 3); // reverse default: 1.1 x first
        Assert.Equal(-5 * 4 * 31.83098862, D(Convert(Duster with { ReverseGearRatio = 5 }).VelocityToRpm[0]), 3);
    }

    [Fact]
    public void FewerGears_LeaveUnusedSlotsZero()
    {
        var fx = Convert(Duster with { GearRatios = [3.0, 1.5, 0.9, 0.7] });
        Assert.Equal(6, fx.NumGears);
        Assert.Equal(0, fx.VelocityToRpm[6]);
        Assert.Equal(0, fx.VelocityToRpm[7]);
        Assert.NotEqual(0, fx.VelocityToRpm[5]);
    }

    [Fact]
    public void Converts_TorqueCurve_ThroughThePeaks()
    {
        var t = Convert(Duster).TorqueCurve;
        double tpw = 110 * 30000 / (Math.PI * 5632);            // P / omega = 186.5 Nm
        Assert.Equal(41, t.Length);
        Assert.Equal(0.55 * 250, D(t[0]), 3);                   // start of the table
        Assert.Equal(250, D(t[15]), 3);                         // torque peak at 15 * 256 rpm
        Assert.Equal(tpw, D(t[22]), 3);                         // power peak at 22 * 256 rpm
        Assert.Equal(0.9 * tpw, D(t[26]), 3);                   // redline at 26 * 256 rpm
        Assert.Equal(0.3 * 0.9 * tpw, D(t[40]), 3);             // end of the table
        Assert.Equal(0.55 * 250 + (250 - 0.55 * 250) * 7 / 15, D(t[7]), 3);
        Assert.Equal(250, D(t.Max()), 3);
    }

    [Fact]
    public void RoundsHalfAwayFromZero()
    {
        // 0.5 LSB = 1/131072 kg: 1300 + 1/131072 -> 1300 * 65536 + 0.5 rounds up
        var fx = Convert(Duster with { MassKg = 1300 + 0.5 / 65536 });
        Assert.Equal(1300 * 65536 + 1, fx.Mass);
    }

    public static IEnumerable<object[]> Rejected() =>
    [
        ["massKg", Duster with { MassKg = 0 }],
        ["massKg", Duster with { MassKg = 100000 }],
        ["massKg", Duster with { MassKg = double.NaN }],
        ["torqueNm", Duster with { TorqueNm = -5 }],
        ["torqueNm", Duster with { TorqueNm = 0 }],
        ["topSpeedKmh", Duster with { TopSpeedKmh = 2000 }],
        ["topSpeedKmh", Duster with { TopSpeedKmh = 0 }],
        ["topSpeedKmh", Duster with { TopSpeedKmh = 400 }],      // gearing cannot reach it
        ["redlineRpm", Duster with { RedlineRpm = 0 }],
        ["redlineRpm", Duster with { RedlineRpm = 20000 }],      // beyond the 10240 rpm torque table
        ["powerKw", Duster with { PowerKw = 0 }],
        ["powerKw", Duster with { PowerKw = 500 }],              // more than torque x rpm allows
        ["torqueRpm", Duster with { TorqueRpm = 6000 }],         // after the power peak
        ["powerRpm", Duster with { PowerRpm = 9000 }],           // above redline
        ["gearRatios", Duster with { GearRatios = [] }],
        ["gearRatios", Duster with { GearRatios = [3, 2.5, 2, 1.5, 1.2, 1, 0.8] }],
        ["gearRatios[1]", Duster with { GearRatios = [2.0, 2.0] }],
        ["gearRatios[0]", Duster with { GearRatios = [0, 1] }],
        ["finalDrive", Duster with { FinalDrive = 0 }],
        ["wheelRadiusM", Duster with { WheelRadiusM = 2 }],
        ["wheelbaseM", Duster with { WheelbaseM = 0 }],
        ["tyreGrip", Duster with { TyreGrip = 9 }],
        ["brakeDecelerationMs2", Duster with { BrakeDecelerationMs2 = -1 }],
        ["drive", Duster with { Drive = (CarDrive)9 }],
    ];

    [Theory, MemberData(nameof(Rejected))]
    public void RejectsOutOfRange_NamingTheField(string field, SiPhysics si)
    {
        Assert.False(SiPhysicsConverter.TryConvert(si, out var fx, out var error));
        Assert.Null(fx);
        Assert.Contains($"'{field}", error);
    }

    // a template with simple known values: mass 1000, flat 400 Nm, top speed 50 m/s, accel curve 100 up to 55 m/s then 0
    static (byte[] Specs, byte[] Ai) Template()
    {
        var s = new byte[CarSpecs.Size];
        var a = new byte[CarAiModel.Size];
        void W(byte[] d, int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o), v);
        W(s, 0, 1000 << 16); W(s, 8, 8); W(s, 0xF4, 50 << 16); W(s, 0x128, 0x7AE1); W(s, 0x138, 3 << 16); W(s, 0x13C, 0x41);
        for (int i = 0; i < 41; i++) W(s, 0x4C + 4 * i, 400 << 16);
        W(a, 0, 10); W(a, 4 * 65, 40 << 16); W(a, 4 * 66, 0xCCCD); W(a, 4 * 123, 60 << 16); W(a, 4 * 125, 77);
        for (int i = 0; i < 112; i++)
        {
            short v = (short)(i <= 55 ? 100 : 0);
            BinaryPrimitives.WriteInt16LittleEndian(a.AsSpan(4 * 9 + 2 * i), v);
            BinaryPrimitives.WriteInt16LittleEndian(a.AsSpan(4 * 67 + 2 * i), v);
        }
        return (s, a);
    }

    static int I(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
    static short S(byte[] d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o));

    [Fact]
    public void Apply_WritesMappedFields_KeepsTheRest()
    {
        var fx = Convert(Duster with { BrakeDecelerationMs2 = 14 });
        var (ts, ta) = Template();
        var (s, ai) = SiPhysicsConverter.Apply(fx, ts, ta);
        Assert.Equal(CarSpecs.Size, s.Length);
        Assert.Equal(CarAiModel.Size, ai.Length);
        Assert.Equal(fx.Mass, I(s, 0x00));
        Assert.Equal(8, I(s, 0x04));
        for (int g = 0; g < 8; g++) { Assert.Equal(fx.VelocityToRpm[g], I(s, 0x0C + 4 * g)); Assert.Equal(65536, I(s, 0x2C + 4 * g)); }
        for (int i = 0; i < 41; i++) Assert.Equal(fx.TorqueCurve[i], I(s, 0x4C + 4 * i));
        Assert.Equal(6656, I(s, 0xF0));
        Assert.Equal(50 << 16, I(s, 0xF4));
        Assert.Equal(65536, I(s, 0xF8));
        Assert.Equal(14 << 16, I(s, 0xFC));
        Assert.Equal(fx.WheelBase, I(s, 0x124));
        Assert.Equal(3 << 16, I(s, 0x138));                     // template grip x 1.0
        // from the template
        Assert.Equal(8, I(s, 0x08));
        Assert.Equal(0x7AE1, I(s, 0x128));
        Assert.Equal(0x41, I(s, 0x13C));
        Assert.Equal(77, I(ai, 4 * 125));
        Assert.Equal(0xCCCD, I(ai, 4 * 66));
        Assert.Equal(10, I(ai, 0));
    }

    [Fact]
    public void Apply_BrakeStaysFromTemplateWhenNotGiven()
    {
        var (ts, ta) = Template();
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0xFC), 0x1234);
        var (s, _) = SiPhysicsConverter.Apply(Convert(Duster), ts, ta);
        Assert.Equal(0x1234, I(s, 0xFC));
    }

    [Fact]
    public void Apply_GripScalesTemplateGrip()
    {
        var (ts, ta) = Template();
        var (s, ai) = SiPhysicsConverter.Apply(Convert(Duster with { TyreGrip = 1.5 }), ts, ta);
        Assert.Equal(4 * 65536 + 0x8000, I(s, 0x138));          // 3 x 1.5
        Assert.Equal(60 << 16, I(ai, 4 * 65));                  // 40 x 1.5
        Assert.Equal(90 << 16, I(ai, 4 * 123));
    }

    [Fact]
    public void Apply_AiGearSpeedsAndRpm()
    {
        var fx = Convert(Duster with { GearRatios = [3.0, 1.5, 0.9, 0.7] });
        var (ts, ta) = Template();
        var (_, ai) = SiPhysicsConverter.Apply(fx, ts, ta);
        Assert.Equal(6656 << 16, I(ai, 4));
        for (int k = 0; k < 4; k++)                              // speed at the rev limit in gear k+1
            Assert.Equal(6656.0 / D(fx.VelocityToRpm[k + 2]), D(I(ai, 4 * (2 + k))), 3);
        Assert.Equal(0, I(ai, 4 * 6));
        Assert.Equal(0, I(ai, 4 * 7));
        Assert.Equal(0, I(ai, 4 * 8));
    }

    [Fact]
    public void Apply_AccelCurve_SameTopSpeedAndPowerPerMass_IsUnchanged()
    {
        var fx = Convert(Duster);
        var (ts, ta) = Template();
        // template with the very same torque curve, mass and top speed -> factor 1 and no stretch
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0), fx.Mass);
        for (int i = 0; i < 41; i++) BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0x4C + 4 * i), fx.TorqueCurve[i]);
        var (_, ai) = SiPhysicsConverter.Apply(fx, ts, ta);
        for (int i = 0; i < 112; i++) { Assert.Equal(S(ta, 4 * 9 + 2 * i), S(ai, 4 * 9 + 2 * i)); Assert.Equal(S(ta, 4 * 67 + 2 * i), S(ai, 4 * 67 + 2 * i)); }
    }

    [Fact]
    public void Apply_AccelCurve_StretchedToTheNewTopSpeed()
    {
        var fx = Convert(Duster with { TopSpeedKmh = 360, GearRatios = [3.7, 2.0, 1.35, 1.0, 0.6, 0.4] });    // 100 m/s: twice the template's 50
        var (ts, ta) = Template();
        BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0), fx.Mass);
        for (int i = 0; i < 41; i++) BinaryPrimitives.WriteInt32LittleEndian(ts.AsSpan(0x4C + 4 * i), fx.TorqueCurve[i]);
        var (_, ai) = SiPhysicsConverter.Apply(fx, ts, ta);
        Assert.Equal(100, S(ai, 4 * 9 + 2 * 100));              // old bucket 50
        Assert.Equal(100, S(ai, 4 * 9 + 2 * 110));              // old bucket 55
        Assert.Equal(50, S(ai, 4 * 9 + 2 * 111));               // old bucket 55.5: halfway between 100 and 0
    }

    [Fact]
    public void Apply_AccelCurve_AmplitudeFollowsPowerPerMass_Limited()
    {
        var (ts, ta) = Template();                               // 400 Nm flat, 1000 kg
        var fx = Convert(Duster);
        var (_, ai) = SiPhysicsConverter.Apply(fx, ts, ta);
        double k = Math.Clamp(PowerPerMass(fx.TorqueCurve, 1300) / PowerPerMass(Enumerable.Repeat(400 << 16, 41).ToArray(), 1000), 0.25, 4);
        Assert.InRange(k, 0.25, 0.4);                            // 250 Nm @ 1300 kg vs 400 Nm @ 1000 kg: weaker, hits the 0.25 floor
        Assert.Equal((short)Math.Round(100 * k), S(ai, 4 * 9 + 2 * 10));
    }

    static double PowerPerMass(int[] torque, int massKg)
    {
        long best = 0;
        for (int i = 0; i < torque.Length; i++) best = Math.Max(best, (long)torque[i] * i);
        return best / (double)massKg / 65536.0;
    }

    [Fact]
    public void RaceClass_MatchesMenuLabel_AndTemplate()
    {
        // the menu labels McLaren F1 (0) / LGT1 (7) "class A" and F355 (2) "class C"
        Assert.Equal(CarRaceClass.A, VanillaCarSource.TraitsOf(0).RaceClass);
        Assert.Equal(CarRaceClass.A, VanillaCarSource.TraitsOf(7).RaceClass);
        Assert.Equal(CarRaceClass.C, VanillaCarSource.TraitsOf(2).RaceClass);
        Assert.Equal(CarRaceClass.B, VanillaCarSource.TraitsOf(11).RaceClass);
        Assert.Equal(7, SiPhysicsConverter.TemplateNumber(CarRaceClass.A));
        Assert.Equal(11, SiPhysicsConverter.TemplateNumber(CarRaceClass.B));
        Assert.Equal(2, SiPhysicsConverter.TemplateNumber(CarRaceClass.C));
        foreach (var i in new[] { 0, 2, 7, 11 })
            Assert.Equal(NFS.Core.Constants.FrontEndConstants.RatingTexts[i],
                NFS.Core.Constants.FrontEndConstants.RatingTexts[SiPhysicsConverter.TemplateNumber(VanillaCarSource.TraitsOf(i).RaceClass)]);
    }

    [GameFact]
    public void ToRaw_WithRealTemplates_ProducesParsableFiles_ForEveryClass()
    {
        var viv = NFS.Data.Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir);
        byte[]? Read(string n) => viv.Entries.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ? viv.Get(n) : null;
        foreach (var cls in new[] { CarRaceClass.A, CarRaceClass.B, CarRaceClass.C })
        {
            var raw = SiPhysicsConverter.ToRaw(Duster, cls, Read);
            var s = CarSpecs.Parse(raw.CarSpecs.ToArray());
            var a = CarAiModel.Parse(raw.CarAiModel.ToArray());
            Assert.Equal(1300 << 16, s.Mass);
            Assert.Equal(8, s.NumGears);
            Assert.Equal(112, a.SimAccelCurve.Length);
            Assert.Equal(6656 << 16, a.Unknown_1);
            Assert.True(s.TorqueCurve.All(t => t > 0));
            // the sim's own derivation accepts it
            var d = NFS.Core.CarSpecsDerive.Derive(s, new NFS.Core.DeriveOptions(false, false));
            Assert.True(d.MaxTorque > 0 && d.AeroDrag != 0);
        }
    }

    [Fact]
    public void ToRaw_Throws_WhenValueOutOfRange()
    {
        var e = Assert.Throws<InvalidDataException>(() => SiPhysicsConverter.ToRaw(Duster with { MassKg = 0 }, CarRaceClass.B, _ => null));
        Assert.Contains("massKg", e.Message);
    }
}
