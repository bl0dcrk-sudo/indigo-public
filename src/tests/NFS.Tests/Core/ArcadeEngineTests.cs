using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>on F355 (arcade derive, manual gearbox); expected values from the original's code [ref:3048]
/// (reference implementation of the original, same cases). [ref:3470]</summary>
public class ArcadeEngineTests
{
    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(true, false));

    // car: vz, throttleRamped, brakeRamped, rpm, gear, prevGear, speed, steer, yaw, gripClass, handbrake
    // env: thr, brk, curve;  expected: speed(+0x304), rpm, slip, lock(+0x2FC), skid324, skid328, brakeRamped, prevGear [ref:3049]
    public static TheoryData<string, int[], int[], int[]> Cases => new()
    {
        { "launch_full_gas", [0, 0xF8, 0, 3000, 2, 2, 0, 0, 0, 0, 0], [0x10000, 0, 0], [11023, 2990, 1, 0, 0, 2621440, 0, 2, 0] },
        { "accel_2nd_mid", [0x80000, 0xF8, 0, 3000, 2, 2, 0x80000, 0, 0, 0, 0], [0x10000, 0, 0], [546348, 2995, 0, 0, 0, 0, 0, 2, 0] },
        { "accel_3rd_wet", [0xF0000, 0xC0, 0, 4200, 4, 4, 0xF0000, 0, 0, 5, 0], [0xC000, 0, 0], [987971, 4000, 0, 0, 0, 0, 0, 4, 0] },
        { "cruise_p_ctrl", [0x100000, 0x80, 0, 4000, 4, 4, 0x100000, 0, 0, 0, 0], [0x8100, 0, 0], [1052600, 3800, 0, 0, 0, 0, 0, 4, 0] },
        { "coast_engine_brake", [0x140000, 0, 0, 5000, 4, 4, 0x140000, 0, 0, 0, 0], [0, 0, 0], [1305350, 4800, 0, 0, 0, 0, 0, 4, 0] },
        { "hard_brake", [0x1E0000, 0, 0xF8, 6000, 5, 5, 0x1E0000, 0, 0, 0, 0], [0, 0x10000, 0], [1914041, 5800, 0, 0, 0, 0, 248, 5, 65536] },
        { "brake_in_curve", [0x180000, 0, 0x60, 5000, 4, 4, 0x180000, 100, 0x800, 4, 0], [0, 0x6400, 0x30], [1550409, 4800, 3, 0, 0, 0, 96, 4, 25600] },
        { "overrev", [0x160000, 0xF8, 0, 9000, 2, 2, 0x160000, 10, 0, 0, 0], [0x10000, 0, 0], [1433052, 8800, 0, 0, 0, 0, 0, 2, 0] },
        { "neutral_rev", [0x50000, 0xF8, 0, 2000, 1, 3, 0x50000, 0, 0, 0, 0], [0x10000, 0, 0], [326611, 2600, 0, 0, 0, 0, 0, 3, 0] },
        { "handbrake", [0x120000, 0x40, 0, 5000, 3, 3, 0x120000, -50, 0, 0, 1], [0x4000, 0, 0], [1154659, 4800, -4, 2, 0, 2621440, 128, 3, 32768] },
        { "reverse_back", [-0x30000, 0xF8, 0, 2000, 0, 0, -0x30000, 0, 0, 0, 0], [0x10000, 0, 0], [-215426, 1800, 0, 0, 0, 0, 0, 0, 0] },
    };

    [GameTheory]
    [MemberData(nameof(Cases))]
    public void MatchesReference(string name, int[] car, int[] env, int[] expected)
    {
        var c = new CarObj
        {
            ThrottleRamped = (byte)car[1], BrakeRamped = (byte)car[2], Rpm = car[3], Gear = (byte)car[4], PrevGear = (byte)car[5],
            SignedSpeed = car[6], Steer = car[7], YawTarget = car[8], GripClass = car[9], Handbrake = (byte)car[10],
        };
        c.LocalVel.Z = car[0];
        var e = new ArcadeEnv { Throttle = env[0], Brake = env[1], RoadCurveAhead = env[2] };
        ArcadeModel.Engine(c, F355(), ref e);
        int[] got = [c.SignedSpeed, c.Rpm, c.SlipState, c.SlidingAxles, c.Skid324, c.Skid328, c.BrakeRamped, c.PrevGear, e.Brake];
        Assert.True(expected.SequenceEqual(got), $"{name}: got {string.Join(", ", got)}");
    }

    [GameFact]
    public void FullThrottle_SpeedsUpOverTicks_BrakeSlowsDown()
    {
        var sp = F355();
        var c = new CarObj { ThrottleRamped = 0xF8, Rpm = 3000, Gear = 3, PrevGear = 3, SignedSpeed = 0xA0000 };
        c.LocalVel.Z = 0xA0000;
        for (int i = 0; i < 32; i++)
        {
            var e = new ArcadeEnv { Throttle = 0x10000 };
            ArcadeModel.Engine(c, sp, ref e);
            Assert.True(c.SignedSpeed >= c.LocalVel.Z, $"tick {i}");
            c.LocalVel.Z = c.SignedSpeed;                       // the step sets v_loc ≈ target
        }
        int fast = c.SignedSpeed;
        Assert.True(fast > 0xA0000);
        c.ThrottleRamped = 0; c.BrakeRamped = 0xF8;
        var b = new ArcadeEnv { Brake = 0x10000 };
        ArcadeModel.Engine(c, sp, ref b);
        Assert.True(c.SignedSpeed < fast);
    }
}
