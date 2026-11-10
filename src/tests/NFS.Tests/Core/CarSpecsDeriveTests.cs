using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>on real p&lt;car&gt;.dat; expected values: [ref:3474]
/// (emulation of the original).</summary>
public class CarSpecsDeriveTests
{
    private static CarSpecs Load(string car) => CarSpecs.Parse(GameData.ReadRaw($"GAMEDATA/SIM/CARDATA/P{car}.DAT"));

    private static readonly DeriveOptions Arcade = new(Arcade: true, PlayerBoost: false);
    private static readonly DeriveOptions SimBoost = new(Arcade: false, PlayerBoost: true, FinalDrive: 0x11000, SteerScale: 0xC000);

    [GameFact]
    public void F355_Arcade()
    {
        var d = CarSpecsDerive.Derive(Load("F355"), Arcade);
        Assert.Equal((7, 23461888, 0, 196608), (d.InvRedline, d.MaxTorque, d.FrontDriveRatio, d.GripMultiplier));
        Assert.Equal((22119, 504320, 8516, 21845, 1179648), (d.AeroDrag, d.YawArm, d.InvYawArm, d.InvGrip, d.MaxSteering));
        Assert.Equal([-22388407, 0, 24520294, 17254318, 12862749, 10148249, 8271296, 6742042], d.VelocityToRpm);
        Assert.Equal([-191, 655, 175, 248, 333, 423, 519, 637], d.GearSpeedFactor);
        Assert.Equal([-1655, 0, 1703, 1135, 674, 532, 562, 573], d.GearThrust);
        Assert.Equal([-2614, 762, 2853, 2013, 1499, 1180, 962, 784], d.GearRpmFactor);
    }

    [GameFact]
    public void F355_SimWithSetupAndBoost()
    {
        var d = CarSpecsDerive.Derive(Load("F355"), SimBoost);
        Assert.Equal((7, 23461888, 29491, 245760), (d.InvRedline, d.MaxTorque, d.FrontDriveRatio, d.GripMultiplier));
        Assert.Equal((42960, 504320, 8516, 17476, 884736), (d.AeroDrag, d.YawArm, d.InvYawArm, d.InvGrip, d.MaxSteering));
        Assert.Equal([-21071441, 0, 23077923, 16239358, 12106116, 9551293, 7784749, 6345451], d.VelocityToRpm);
        Assert.Equal([-203, 655, 186, 264, 354, 449, 551, 676], d.GearSpeedFactor);
        Assert.Equal([-3114, 0, 3208, 2136, 1270, 1002, 1058, 1078], d.GearThrust);
        Assert.Equal([-2460, 762, 2685, 1891, 1410, 1112, 906, 738], d.GearRpmFactor);
    }

    [GameTheory]
    [InlineData("CALA", 8, 19694, 518656, 20480, new[] { -162, 655, 181, 283, 398, 506, 605, 720 }, new[] { -1570, 0, 1667, 842, 678, 565, 525, 507 }, new[] { -3511, 868, 3142, 2010, 1429, 1124, 940, 790 })]
    [InlineData("FF50", 7, 21223, 522752, 20480, new[] { -238, 655, 224, 312, 393, 473, 563, 716 }, new[] { -1465, 0, 1787, 1226, 931, 736, 619, 511 }, new[] { -2098, 762, 2229, 1600, 1270, 1055, 887, 697 })]
    [InlineData("GT90", 10, 16416, 596992, 21845, new[] { -262, 655, 234, 360, 495, 749, 1057 }, new[] { -844, 0, 1008, 573, 536, 394, 300 }, new[] { -2561, 1024, 2867, 1864, 1355, 895, 634 })]
    public void OtherCars_Arcade(string car, int invRedline, int aero, int yawArm, int invGrip, int[] speed, int[] thrust, int[] rpm)
    {
        var s = Load(car);
        var d = CarSpecsDerive.Derive(s, Arcade);
        Assert.Equal((invRedline, aero, yawArm, invGrip), (d.InvRedline, d.AeroDrag, d.YawArm, d.InvGrip));
        Assert.Equal(speed, d.GearSpeedFactor.Take(s.NumGears));
        Assert.Equal(thrust, d.GearThrust.Take(s.NumGears));
        Assert.Equal(rpm, d.GearRpmFactor.Take(s.NumGears));
    }

    [GameFact]
    public void ToDriveControlSpecs_CopiesDerivedGearTables()
    {
        var s = Load("F355");
        var dc = CarSpecsDerive.Derive(s, Arcade).ToDriveControlSpecs(s);
        Assert.Equal((8, 8, 8600), (dc.NumGears, dc.GearShiftDelay, dc.Redline));
        Assert.Equal(175, dc.GearSpeedFactor[2]);
        Assert.Equal(2853, dc.GearRpmFactor[2]);
        Assert.Equal(s.BrakeIncreasingCurve, dc.BrakeIncreasing);
    }

    [Fact]
    public void ZeroRatio_GivesSpeedFactor0x28F()
    {
        var torque = new int[41];
        torque[10] = 300 << 16;
        var s = new CarSpecs(1000 << 16, 3, 4, [-0x100000, 0, 0x100000, 0, 0, 0, 0, 0], [0x10000, 0x10000, 0x10000, 0, 0, 0, 0, 0],
            torque, 6000, 50 << 16, 0, 0, 0, new byte[8], new byte[8], new byte[8], new byte[8], 2 << 16, 0, 18 << 16, 16, 32,
            3 << 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var d = CarSpecsDerive.Derive(s, Arcade);
        Assert.Equal(0x28F, d.GearSpeedFactor[1]);
        Assert.Equal(0x1000, d.GearSpeedFactor[2]);                 // 1/16
        Assert.Equal(Fx.Div(Fx.Div(0x100000, 1000 << 16), 10 << 16), d.GearThrust[2]);
        Assert.Equal(300 << 16, d.MaxTorque);
        Assert.Equal(0x10000 / 6000, d.InvRedline);
    }
}
