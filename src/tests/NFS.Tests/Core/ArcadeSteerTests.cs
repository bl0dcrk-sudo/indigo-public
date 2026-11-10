using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>on F355 (arcade derive); expected values from the original's code [ref:3056]
/// (emulation of the original, same cases). [ref:3472]</summary>
public class ArcadeSteerTests
{
    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(true, false));

    // name, vz, throttleRamped, steer, steerTarget, gripClass, slip, axles, yaw0, speed, gear, forceZ,
    // env: thr, brk, steerAbs, curve, gravX, model, stale;  expected: yaw, 364, 324, vz after, gain, stale
    public static TheoryData<string, int[], int[], int[]> Cases => new()
    {
        { "cruise_left", [0x1E0000, 0xF8, -60, 0, 0, 0, 0, 0, 0, 2, 0], [0x10000, 0, -0x7800, 0, 0, 0, 0], [-4680, 4680, 0, 1966080, 7513, 0] },
        { "slow_right", [0x30000, 0x40, 100, 0, 4, 0, 0, 0, 0, 2, 0], [0x4000, 0, 0xC800, 0, 0, 0, 0], [8600, 8600, 0, 196608, 8294, 0] },
        { "fast_hard_lean", [0x280000, 0x20, 120, 0, 5, 0, 1, 0, 0, 2, 0], [0x2000, 0x8000, 0xF000, 0, 0x8000, 0, 0], [2178, 2042, 131072, 2621440, 1763, 0] },
        { "drift3_curve", [0x1C0000, 0xC0, 90, 0, 1, 3, 0, 0x2000, 0x1C0000, 2, 0], [0xC000, 0, 0xB400, 0x60, 0, 0, 0], [10551, 7200, 0, 1835008, 4965, 4468] },
        { "drift3_sharp", [0x200000, 0xC0, 110, 0, 1, 3, 0, 0, 0x200000, 2, 0], [0xC000, 0, 0xDC00, 0xA0, 0, 0, 0], [3276, 8250, 0, 2097152, 7258, 6532] },
        { "oversteer2", [0x80000, 0xF8, 80, 0, 3, 2, 0, 0x1000, 0x80000, 2, 0], [0x10000, 0, 0xA000, 0, 0, 0, 0], [4751, 7200, 131072, 524288, 13824, 8294] },
        { "spin4_reverse", [-0x60000, 0, -40, 0, 0, -4, 0, -0x800, -0x60000, 0, 0], [0, 0x10000, -0x5000, 0, 0, 0, 0], [-8601, 5760, 0, -393216, 13824, 20736] },
        { "donut", [0x8000, 0x80, 50, 0, 0, 0, 0, 0, 0, 2, -0x8000], [0x8000, 0, 0x6400, 0, 0, 0, 0], [12800, 700, 0, 0, 1382, 0] },
        { "model0E_stale", [0x80000, 0xF8, 80, 0, 3, 3, 0, 0, 0x80000, 2, 0], [0x10000, 0, 0xA000, 0, 0, 0xE, 0x3000], [3276, 10800, 131072, 524288, 20736, 12288] },
    };

    [GameTheory]
    [MemberData(nameof(Cases))]
    public void MatchesReference(string name, int[] car, int[] env, int[] expected)
    {
        var c = new CarObj
        {
            ThrottleRamped = (byte)car[1], Steer = car[2], SteerTarget = car[3], GripClass = car[4], SlipState = car[5],
            SlidingAxles = car[6], YawTarget = car[7], SignedSpeed = car[8], Gear = (byte)car[9],
        };
        c.LocalVel.Z = car[0];
        c.LocalForce.Z = car[10];
        var e = new ArcadeEnv
        {
            Throttle = env[0], Brake = env[1], SteerAbs = env[2], RoadCurveAhead = env[3], GravLocalX = env[4], Model = env[5],
            StaleSteerTerm = env[6],
        };
        ArcadeModel.Steer(c, F355(), ref e);
        Assert.True(expected.SequenceEqual([c.YawTarget, c.FrontGrip, c.Skid324, c.LocalVel.Z, e.SteerGain, e.StaleSteerTerm]),
            $"{name}: got {c.YawTarget},{c.FrontGrip},{c.Skid324},{c.LocalVel.Z},{e.SteerGain},{e.StaleSteerTerm}");
        Assert.Equal(c.YawTarget, c.FrontGripDemand);
    }

    [GameFact]
    public void Model0E_ZeroSteer_ZeroYaw()
    {
        var c = new CarObj { ThrottleRamped = 0xF8, Steer = 0, SlipState = 3, Gear = 2, SignedSpeed = 0x80000 };
        c.LocalVel.Z = 0x80000;
        var e = new ArcadeEnv { Throttle = 0x10000, Model = ArcadeModel.ModelSpecial, StaleSteerTerm = 0x7FFF };
        ArcadeModel.Steer(c, F355(), ref e);
        Assert.Equal(0, c.YawTarget);
        Assert.Equal(0x7FFF, e.StaleSteerTerm);                 // not overwritten by a 0x0E call
    }
}
