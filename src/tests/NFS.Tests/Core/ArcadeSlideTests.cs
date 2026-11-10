using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>(target lateral speed +0x310) on F355; expected values from the original's code [ref:3055]
/// (reference implementation of the original, same cases). [ref:3471]</summary>
public class ArcadeSlideTests
{
    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(true, false));

    // car: speed(+0x304), vz, yaw(+0x30C), throttleRamped, rpm, slip, lateral(+0x310), gripClass, frontGrip(+0x364)
    // env: thr, brk, gravX;  expected: lateral, slip, yaw, skid324, skid328, frontGrip
    public static TheoryData<string, int[], int[], int[]> Cases => new()
    {
        { "grip_cruise", [0x180000, 0x180000, 0x400, 0x80, 5000, 0, 0, 0, 0], [0x8000, 0, 0], [0, 0, 1024, 0, 0, 0] },
        { "push_lift", [0x200000, 0x200000, 0x6000, 0x10, 5000, 0, 0, 0, 0x2000], [0x1000, 0, 0], [0, 5, 24576, 13107, 13107, 4096] },
        { "oversteer2", [0x100000, 0x100000, 0x3000, 0xF8, 6000, 2, -0x2000, 0, 0], [0x10000, 0, 0], [-27852, 2, 12288, 131072, 131072, 0] },
        { "drift3", [0x180000, 0x180000, 0x2000, 0xC0, 6000, 3, -0x8000, 0, 0], [0xC000, 0, 0], [-52428, 3, 8192, 131072, 131072, 0] },
        { "drift3_countersteer", [0x180000, 0x180000, -0x200, 0xC0, 6000, 3, -0x8000, 1, 0], [0x8000, 0, 0], [0, 3, 0, 0, 131072, 0] },
        { "drift3_slow", [0xA0000, 0xA0000, 0x2000, 0xC0, 2500, -3, 0x8000, 0, 0], [0xC000, 0, 0], [19662, 0, 8192, 0, 131072, 0] },
        { "spin4_lean", [0x30000, 0x30000, -0x1000, 0, 4000, -4, 0x1000, 0, 0], [0, 0x10000, -0x20000], [31458, -4, -4096, 131072, 131072, 0] },
    };

    [GameTheory]
    [MemberData(nameof(Cases))]
    public void MatchesReference(string name, int[] car, int[] env, int[] expected)
    {
        var c = new CarObj
        {
            SignedSpeed = car[0], YawTarget = car[2], ThrottleRamped = (byte)car[3], Rpm = car[4], SlipState = car[5],
            SlipAngle = car[6], GripClass = car[7], FrontGrip = car[8], Gear = 2,
        };
        c.LocalVel.Z = car[1];
        var e = new ArcadeEnv { Throttle = env[0], Brake = env[1], GravLocalX = env[2] };
        ArcadeModel.LateralSlide(c, F355(), ref e);
        int[] got = [c.SlipAngle, c.SlipState, c.YawTarget, c.Skid324, c.Skid328, c.FrontGrip];
        Assert.True(expected.SequenceEqual(got), $"{name}: got {string.Join(", ", got)}");
    }
}
