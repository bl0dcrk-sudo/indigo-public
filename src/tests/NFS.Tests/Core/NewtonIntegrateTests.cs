using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>
/// integrators. Expected values: [ref:3478]
/// (emulation of the original's arithmetic, own sin, rot and orthonormalize code from the original tables). [ref:3075]
/// </summary>
public class NewtonIntegrateTests
{
    private static CarObj Car(Vec3 pos, Vec3 vel) => new() { Active = 1, Pos = pos, Vel = vel };

    [Theory]
    [InlineData(0x10000, -0x20000, 0x7FFF0000, 0x12345, -0x12345, -0x3F, 0x1048D, -0x2048D, 0x7FFF0000)]
    [InlineData(0, 0, 0, -1, -64, -65, 0, -1, -1)]   // C division: -1/64 = 0, -65/64 = -1 (not floor)
    public void Pos_AddsVelocityOver64Truncated(int px, int py, int pz, int vx, int vy, int vz, int ex, int ey, int ez)
    {
        var c = Car(new(px, py, pz), new(vx, vy, vz));
        NewtonIntegrate.Pos(c);
        Assert.Equal(new Vec3(ex, ey, ez), c.Pos);
    }

    [Fact]
    public void Pos_InactiveCarDoesNotMove()
    {
        var c = Car(new(1, 2, 3), new(0x10000, 0x10000, 0x10000));
        c.Active = 0;
        NewtonIntegrate.Pos(c);
        Assert.Equal(new Vec3(1, 2, 3), c.Pos);
    }

    [Theory]
    [InlineData(0x10000, 0x1048D, -0x1FF, 0x3FFFB)]
    [InlineData(0x11EB8, 0x10518, -0x23D, 0x47ADB)]
    [InlineData(0xE666, 0x10418, -0x1CC, 0x39993)]
    public void PosScaled_UsesCatchUpAndCopiesTimeScale(int k, int ex, int ey, int ez)
    {
        var c = Car(new(0x10000, 0, -5), new(0x12345, -0x7FFF, 0x1000000));
        c.CatchUp = k;
        NewtonIntegrate.PosScaled(c);
        Assert.Equal(new Vec3(ex, ey, ez), c.Pos);
        Assert.Equal(k, c.TimeScale);
    }

    private static Mat3 M(params int[] v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);

    public static TheoryData<bool, int, int, int, int, Mat3, int, int> RotCases => new()
    {
        // w = (0.5, -1.625, 0.1875) rev/s: accumulator exceeds 0x1000 quickly -> frequent orthonormalisation
        { true, 0x8000, -0x1A000, 0x3000, 0, M(0xFCB3, 0x4A7, 0x28AA, -0x6B4, 0xFF9C, 0xC66, -0x2860, -0xD4D, 0xFC71), 15, 0x940 },
        { true, 0x8000, -0x1A000, 0x3000, 1, M(0xF2EB, 0x720, 0x5077, -0xF44, 0xFE74, 0x178A, -0x4F52, -0x1B22, 0xF1E0), 16, 0 },
        { true, 0x8000, -0x1A000, 0x3000, 15, M(-0xBAFC, -0x7B12, 0x7C2E, -0x9876, 0xCBC9, -0x1B99, -0x5594, -0x5E1F, -0xDE26), 16, 0 },
        { true, 0x8000, -0x1A000, 0x3000, 39, M(0xE8D7, 0x78B, 0x6A1F, -0x15F0, 0xFD45, 0x1E21, -0x6819, -0x247D, 0xE701), 16, 0 },
        { false, 0x8000, -0x1A000, 0x3000, 0, M(0xF2E9, 0x8F2, 0x504D, -0x113E, 0xFE4D, 0x17D3, -0x4EF0, -0x1C04, 0xF1E8), 32, 0 },
        { false, 0x8000, -0x1A000, 0x3000, 1, M(0xCD21, 0x896, 0x98E8, -0x28D6, 0xF968, 0x28C5, -0x939C, -0x390F, 0xC939), 32, 0 },
        { false, 0x8000, -0x1A000, 0x3000, 15, M(0xAB03, -0x3984, -0xB59B, 0x3A4, 0xF4FE, -0x4A2B, 0xBE76, 0x2EF6, 0xA47A), 32, 0 },
        { false, 0x8000, -0x1A000, 0x3000, 39, M(0xA28A, 0x21C, 0xC5C3, -0x3D56, 0xF3E7, 0x2FCD, -0xBC06, -0x4DBA, 0x9B5F), 32, 0 },
        // w = (1/16, -9/64, 3/64): period counter drives orthonormalisation (step 15 at 64 Hz)
        { true, 0x1000, -0x2400, 0xC00, 0, M(0xFFFA, 0x12D, 0x389, -0x133, 0xFFFE, 0x192, -0x387, -0x196, 0xFFF8), 15, 0x100 },
        { true, 0x1000, -0x2400, 0xC00, 1, M(0xFFE7, 0x254, 0x714, -0x26C, 0xFFF9, 0x320, -0x70C, -0x330, 0xFFE2), 14, 0x200 },
        { true, 0x1000, -0x2400, 0xC00, 15, M(0xF91E, 0x1009, 0x38D6, -0x1597, 0xFE0F, 0x16E6, -0x36F8, -0x1B12, 0xF88B), 16, 0 },
        { true, 0x1000, -0x2400, 0xC00, 39, M(0xD61C, 0x1B93, 0x89B2, -0x3D48, 0xF432, 0x2E58, -0x7E60, -0x47B1, 0xD2BB), 8, 0x800 },
        { false, 0x1000, -0x2400, 0xC00, 0, M(0xFFE5, 0x25B, 0x712, -0x271, 0xFFF8, 0x324, -0x70B, -0x335, 0xFFE3), 31, 0x200 },
        { false, 0x1000, -0x2400, 0xC00, 1, M(0xFF92, 0x49F, 0xE29, -0x4F8, 0xFFE0, 0x637, -0xE0C, -0x67B, 0xFF8A), 30, 0x400 },
        { false, 0x1000, -0x2400, 0xC00, 15, M(0xE4D5, 0x19CE, 0x6FE5, -0x2FBA, 0xF83E, 0x285D, -0x6878, -0x38F4, 0xE2BA), 25, 0xE00 },
        { false, 0x1000, -0x2400, 0xC00, 39, M(0x6857, 0xB23, 0xE989, -0x8578, 0xD4C7, 0x317E, -0xBFF7, -0x8DE9, 0x5C8D), 28, 0x800 },
    };

    [Theory]
    [MemberData(nameof(RotCases))]
    public void Rot_MatchesAsmEmulation(bool rot64, int wx, int wy, int wz, int step, Mat3 expected, int cnt, int accum)
    {
        var c = new CarObj
        {
            Active = 1, Orient = Mat3.Identity, AngVel = new(wx, wy, wz),
            OrthoCount = rot64 ? NewtonIntegrate.OrthoPeriod64 : NewtonIntegrate.OrthoPeriod32,
        };
        for (int i = 0; i <= step; i++)
            if (rot64) NewtonIntegrate.Rot64(c); else NewtonIntegrate.Rot32(c);
        Assert.Equal(expected, c.Orient);
        Assert.Equal(cnt, c.OrthoCount);
        Assert.Equal(accum, c.RotAccum);
    }

    [Fact]
    public void Rot64_SmallAnglesOnlyAccumulate()
    {
        // ω/64 = (0xC, 0x13, -0xD): below all thresholds -> matrix untouched, counter untouched, accum += 0x2C
        var c = new CarObj { Active = 1, Orient = Mat3.Identity, AngVel = new(0x300, 0x4C0, -0x340), OrthoCount = 5, RotAccum = 7 };
        NewtonIntegrate.Rot64(c);
        Assert.Equal(Mat3.Identity, c.Orient);
        Assert.Equal(5, c.OrthoCount);
        Assert.Equal(51, c.RotAccum);
    }
}
