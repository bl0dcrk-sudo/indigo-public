using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>on F355 (sim derive, no setup); expected values from the original's code [ref:3090]
/// (reference implementation of the original, same cases). [ref:3481]</summary>
public class SimEngineTests
{
    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(false, false));

    // car: gear, rpm, vz, speed(+0x304), throttleRamped, lateral(+0x310); env: thr, gravZ; expected: force, rpm, slip, handbrake
    public static TheoryData<string, int[], int[], int[]> Cases => new()
    {
        { "neutral_rev", [1, 1000, 0, 0, 0, 0], [0x10000, 0], [0, 1500, 0, 0] },
        { "launch_spin", [2, 4000, 0x10000, 0x10000, 0xF8, 0], [0x10000, 0], [1058040, 3995, 1, 0] },
        { "accel_3rd", [3, 5000, 0xF0000, 0xF0000, 0xF8, 0x4000], [0x10000, 0], [414433, 4800, 0, 0] },
        { "accel_overrev", [2, 9000, 0x170000, 0x170000, 0xF8, 0], [0x10000, 0], [-190367, 8605, 0, 0] },
        { "engine_brake_downhill", [2, 5000, 0xC0000, 0xC0000, 0, 0], [0, 0x8000], [-97575, 4489, 0, 0] },
        { "standstill_spin_4th", [4, 3990, 0, 0, 0, 0], [0x7700, 0], [306696, 3915, 1, 0] },
        { "fast_aero_6th", [7, 7000, 0x480000, 0x480000, 0xF8, 0], [0x10000, 0], [79159, 7200, 0, 0] },
    };

    [GameTheory]
    [MemberData(nameof(Cases))]
    public void MatchesReference(string name, int[] car, int[] env, int[] expected)
    {
        var c = new CarObj
        {
            Gear = (byte)car[0], Rpm = car[1], SignedSpeed = car[3], ThrottleRamped = (byte)car[4], SlipAngle = car[5],
        };
        c.LocalVel.Z = car[2];
        int f = SimEngine.DriveForce(c, F355(), new SimEngineEnv(env[0], GravLocalZ: env[1]), out _);
        int[] got = [f, c.Rpm, c.SlipState, c.Handbrake];
        Assert.True(expected.SequenceEqual(got), $"{name}: got {string.Join(", ", got)}");
    }

    [GameFact]
    public void OverRevBeyond150Percent_SetsRearLock()
    {
        var c = new CarObj { Gear = 2, Rpm = 8600 * 3 / 2 + 1 };
        SimEngine.DriveForce(c, F355(), new SimEngineEnv(0), out _);
        Assert.Equal(1, c.Handbrake);
    }
}
