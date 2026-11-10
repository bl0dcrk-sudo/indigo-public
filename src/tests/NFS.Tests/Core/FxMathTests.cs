using NFS.Core;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

/// <summary>
/// Bit-exact fxmath. "ref" values come from an independent emulation of the original's arithmetic [ref:3475]
/// (sincos, atan2, normalize/length, [ref:3071]
/// orthonormalize) reading the same original tables. Table-free functions use hand-computed values. [ref:3072]
/// </summary>
public class FxMathTests
{
    // ------------------------------------------------------------ sin / cos (table of the original)

    [Theory]
    [InlineData(0, 0, 0x10000)]
    [InlineData(0x40, 0x192, 0xFFFE)]            // exact table entries [1] = 402 and [255]
    [InlineData(0x123, 0x723, 0xFFE7)]           // ref (interpolated)
    [InlineData(0x1000, 0x61F7, 0xEC83)]         // 22.5°
    [InlineData(0x2000, 0xB504, 0xB504)]         // 45°
    [InlineData(0x4000, 0x10000, 0)]             // 90°: quadrant 1, j = 0
    [InlineData(0x5555, 0xDDB4, -0x7FFD)]        // ref
    [InlineData(0x8000, 0, -0x10000)]
    [InlineData(-0x1234, -0x6E9C, 0xE6DF)]       // ref: negative angle via arithmetic shift
    [InlineData(0xC321, -0xFF3F, 0x13A4)]        // ref
    public void SinCos_MatchesAsm(int a, int sin, int cos)
    {
        FxMath.SinCos(a, out int s, out int c);
        Assert.Equal((sin, cos), (s, c));
    }

    [Fact]
    public void SinCos_IsCloseToMathAndExactOnTableGrid()
    {
        for (int a = -0x10000; a <= 0x20000; a += 7)
        {
            FxMath.SinCos(a, out int s, out int c);
            double r = a * Math.PI * 2 / 0x10000;
            Assert.InRange(s - Math.Sin(r) * 0x10000, -4, 4);
            Assert.InRange(c - Math.Cos(r) * 0x10000, -4, 4);
        }
        for (int a = 0; a < 0x10000; a += 0x40)
        {
            // on the 1024-step grid there is no interpolation: quarter-turn shift and odd/even symmetry are exact
            Assert.Equal(FxMath.Cos(a), FxMath.Sin(a + 0x4000));
            Assert.Equal(-FxMath.Sin(a), FxMath.Sin(-a));
            Assert.Equal(FxMath.Cos(a), FxMath.Cos(-a));
            Assert.Equal(FxMath.Sin(a), FxMath.Sin(a + 0x10000));
        }
    }

    [Fact]
    public void SinCosTab_UsesTenBitIndex()
    {
        var t = GameTables.FromConstants();
        FxMath.SinCosTab(0x100, out int s, out int c);
        Assert.Equal((0x10000, 0), (s, c));
        FxMath.SinCosTab(0x301, out s, out c);          // quadrant 3, j = 1: (−T[255], T[1])
        Assert.Equal((-t.SinQuarter[255], t.SinQuarter[1]), (s, c));
    }

    // ------------------------------------------------------------ atan2

    [Theory]
    [InlineData(0, 0, 0x80)]                     // (0,0) → 45° (equal-magnitude path)
    [InlineData(1, 0, 0x100)]                    // +x = 90°
    [InlineData(0, 1, 0)]                        // +z = 0
    [InlineData(-1, 1, -0x80)]
    [InlineData(1, -1, 0x180)]
    [InlineData(0x10000, 0x8000, 0xB4)]          // atan(2) = 63.43° → 180.4
    [InlineData(-0x30000, -0x12345, -0x13B)]     // ref
    [InlineData(0x1234, -0x56789, 0x1FE)]        // ref
    [InlineData(-5, 7, -0x65)]                   // ref
    public void Atan2_MatchesAsm(int x, int z, int expected)
    {
        Assert.Equal(expected, FxMath.Atan2(x, z));
    }

    [Theory]
    [InlineData(0, 0, 0x2000)]
    [InlineData(1, 0, 0x4000)]
    [InlineData(0, 1, 0)]
    [InlineData(0x10000, 0x8000, 0x2D1C)]        // 63.43° · 0x10000/360 = 11548
    [InlineData(-0x30000, -0x12345, -0x4EC5)]    // ref
    [InlineData(0x1234, -0x56789, 0x7F77)]       // ref
    [InlineData(-5, 7, -0x1945)]                 // ref
    public void Atan2_16_MatchesAsm(int x, int y, int expected)
    {
        Assert.Equal(expected, FxMath.Atan2_16(x, y));
    }

    [Fact]
    public void Atan2_RangeSymmetryAndAccuracy()
    {
        var rnd = new Random(5);
        for (int i = 0; i < 20000; i++)
        {
            int x = rnd.Next(-0x1000000, 0x1000000) >> rnd.Next(0, 20);
            int z = rnd.Next(-0x1000000, 0x1000000) >> rnd.Next(0, 20);
            int a10 = FxMath.Atan2(x, z), a16 = FxMath.Atan2_16(x, z);
            Assert.InRange(a10, -0x200, 0x200);
            Assert.InRange(a16, -0x8000, 0x8000);
            if (x != 0)                                   // x = 0, z < 0 gives +0x200 / +0x8000 for both
            {
                Assert.Equal(-a10, FxMath.Atan2(-x, z));  // mirror in x is exact
                Assert.Equal(-a16, FxMath.Atan2_16(-x, z));
            }
            if (x == 0 && z == 0) continue;
            double r = Math.Atan2(x, z);
            Assert.InRange(Wrap(a10 - r * 0x200 / Math.PI, 0x400), -1.0, 1.0);
            Assert.InRange(Wrap(a16 - r * 0x8000 / Math.PI, 0x10000), -2.0, 2.0);
        }
    }

    private static double Wrap(double d, int full) => d - full * Math.Round(d / full);

    [Fact]
    public void Atan2_InvertsSinCos()
    {
        for (int a = -0x8000 + 0x40; a < 0x8000; a += 0x40)
        {
            FxMath.SinCos(a, out int s, out int c);
            Assert.InRange(FxMath.Atan2_16(s, c) - a, -2, 2);
            Assert.InRange(FxMath.Atan2(s, c) - (a >> 6), -1, 1);
        }
    }

    [Theory]
    [InlineData(0x10, -0x10, 0x20)]
    [InlineData(0x1F0, -0x1F0, -0x20)]       // d = 0x3E0 > 0x200 → d − 0x400
    [InlineData(-0x1F0, 0x1F0, 0x20)]        // d = b − a = 0x3E0 ≥ 0x201 → 0x400 − d
    [InlineData(-0x10, 0x10, -0x20)]
    [InlineData(0x300, 0x100, 0x200)]        // same sign: plain difference
    public void AngleDiff_WrapsOnlyAcrossSign(int a, int b, int expected) => Assert.Equal(expected, FxMath.AngleDiff(a, b));

    // ------------------------------------------------------------ sqrt / normalize

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 0x102)]
    [InlineData(0x10000u, 0x10220)]              // highest bit 16: 0x6A0A + 0x9816 (piecewise, ≈1.0083)
    [InlineData(0x40000u, 0x20440)]
    [InlineData(0x90000u, 0x2FF98)]
    [InlineData(0x12345678u, 0x442C32)]
    [InlineData(0xFFFFFFFFu, 0x1018A87)]
    public void SqrtTab_MatchesAsm(uint x, int expected)
    {
        Assert.Equal(expected, FxMath.SqrtTab(x));
    }

    [Fact]
    public void SqrtTab_IsCloseToSqrt()
    {
        for (uint x = 0x100; x < 0xF0000000u; x += x / 7 + 1)
        {
            double exact = Math.Sqrt(x / 65536.0) * 65536;
            Assert.InRange(FxMath.SqrtTab(x) / exact, 0.99, 1.02);
        }
    }

    [Theory]
    [InlineData(0x30000, 0x40000, 0, 0x9AAD, 0xCE3C, 0)]               // ref (|v| ≈ 5.03 with the table sqrt)
    [InlineData(0x1234, -0x5678, 0x9ABC, 0x1A10, -0x7BD0, 0xDD8F)]     // ref
    [InlineData(0, 0, 0, 0, 0, 0)]                                     // len 0 → unchanged
    public void Normalize_MatchesAsm(int x, int y, int z, int ex, int ey, int ez)
    {
        var v = new Vec3(x, y, z);
        FxMath.Normalize(ref v);
        Assert.Equal(new Vec3(ex, ey, ez), v);
    }

    [Theory]
    [InlineData(0x30000, 0x40000, 0, 0x9AAE, 0xCE3E, 0)]                         // ref (divides instead of ×1/len)
    [InlineData(0x7FFFFFFF, -0x40000000, 0x12345678, 0xE490, -0x7248, 0x2081)]   // ref: pre-scaled by halving
    [InlineData(0x1234, -0x5678, 0x9ABC, 0x1A10, -0x7BCF, 0xDD8E)]               // ref
    public void NormalizeBig_MatchesAsm(int x, int y, int z, int ex, int ey, int ez)
    {
        var v = new Vec3(x, y, z);
        FxMath.NormalizeBig(ref v);
        Assert.Equal(new Vec3(ex, ey, ez), v);
    }

    [Theory]
    [InlineData(0x30000, 0x40000, 0, 0x4F70A)]                     // ref
    [InlineData(0x1000000, 0, 0, 0)]                               // quirk: FX(256.0,256.0) = 2^32 wraps to 0 → l2 = 0
    [InlineData(0x7FFFFFFF, -0x40000000, 0x12345678, -0x70A28A00)] // ref: len << k overflows like the original
    public void LengthBig_MatchesAsm(int x, int y, int z, int expected)
    {
        Assert.Equal(expected, FxMath.LengthBig(new Vec3(x, y, z)));
    }

    // ------------------------------------------------------------ table-free helpers

    [Theory]
    [InlineData(0, 0x10000, 0x10000)]
    [InlineData(0x10000, 0x10000, 0x15FBE)]   // lo == hi → branch 3: hi + lo·(¼+1/16+1/32+…+1/32768) = 0x10000 + 0x5FBE
    [InlineData(0x30000, 0x40000, 0x51F3A)]   // lo ≥ ¾hi → branch 3 (hand sum of 12 shifts = 0x11F3A)
    [InlineData(0x40000, 0x30000, 0x51F3A)]   // argument order irrelevant
    [InlineData(0x10000, 0x80000, 0x81687)]   // lo < hi/4: >>4 0x1000, >>6 0x400, >>7 0x200, >>9 0x80, >>14 4, >>15 2, >>16 1
    public void Hypot2_ShiftSums(int a, int b, int expected) => Assert.Equal(expected, FxMath.Hypot2(a, b));

    [Fact]
    public void Hypot2_IsWithinFewPercentOfHypot()
    {
        for (int a = 0; a < 0x400000; a += 0x1357)
            for (int b = 1; b < 0x400000; b += 0x2468)
            {
                double h = Math.Sqrt((double)a * a + (double)b * b);
                Assert.InRange(FxMath.Hypot2(a, b) / h, 0.96, 1.03);   // coarse: lo == hi gives 1.374 for √2
            }
    }

    [Fact]
    public void DistXzApprox_MaxPlusQuarterMin()
    {
        Assert.Equal(0x34000, FxMath.DistXzApprox(new Vec3(0x30000, 99, 0x10000), Vec3.Zero));
        Assert.Equal(0x34000, FxMath.DistXzApprox(new Vec3(-0x10000, 0, -0x30000), Vec3.Zero));
        Assert.Equal(0x14000, FxMath.SpeedApproxXz(new Vec3(-0x10000, 0x500000, 0x10000)));   // tie → z + x/4
    }

    // ------------------------------------------------------------ matrices

    [Fact]
    public void RotationMatrices_HaveOriginalLayout()
    {
        FxMath.SinCos(0x1000, out int s, out int c);
        Assert.Equal(new Mat3(Fx.One, 0, 0, 0, c, s, 0, -s, c), FxMath.RotX(0x1000));
        Assert.Equal(new Mat3(c, 0, -s, 0, Fx.One, 0, s, 0, c), FxMath.RotY(0x1000));
        Assert.Equal(new Mat3(c, s, 0, -s, c, 0, 0, 0, Fx.One), FxMath.RotZ(0x1000));
        Assert.Equal(Mat3.Identity, FxMath.RotY(0));
    }

    [Fact]
    public void Mat3Mul_SumsRoundedProducts()
    {
        var a = new Mat3(1, 1, 1, 0, 0, 0, 0, 0, 0);
        var b = new Mat3(0x8000, 0, 0, 0x8000, 0, 0, 0x8000, 0, 0);
        Assert.Equal(3, Mat3.Mul(a, b).M00);
        var m = new Mat3(1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.Equal(m, Mat3.Mul(m, Mat3.Identity));
        Assert.Equal(new Mat3(1, 4, 7, 2, 5, 8, 3, 6, 9), m.Transpose());
    }

    [Fact]
    public void Orthonormalize_MatchesAsm()
    {
        // Reference: RotY(0x1234) · perturbed matrix [ref:3073]
        var m = Mat3.Mul(FxMath.RotY(0x1234), new Mat3(0x10100, 0x200, -0x300, 0x100, 0xFF00, 0x80, 0x300, -0x50, 0x10080));
        Assert.Equal(new Mat3(0xE67B, 0x1F1, -0x7187, 0x100, 0xFF00, 0x80, 0x71BF, 0x95, 0xE607), m);
        FxMath.Orthonormalize(ref m);
        Assert.Equal(new Mat3(0xE5DB, 0x10B, -0x715F, -0x6, 0xFFB0, 0x72, 0x7172, 0x13, 0xE5B6), m);
    }

    [Fact]
    public void Orthonormalize_ImprovesOrthonormality()
    {
        var m = Mat3.Mul(FxMath.RotX(0x777), FxMath.RotZ(-0x2345));
        m.M01 += 0x400; m.M12 -= 0x300; m.M20 += 0x200;
        static double Err(Mat3 x)
        {
            var p = Mat3.Sub(Mat3.Mul(x.Transpose(), x), Mat3.Identity);
            double e = 0;
            for (int i = 0; i < 9; i++) e = Math.Max(e, Math.Abs(p[i]));
            return e;
        }
        double before = Err(m);
        FxMath.Orthonormalize(ref m);
        // coefficients are ¼ of the (I+E)^(-1/2) series → first-order error ×¾ per iteration, ×(¾)^4 ≈ 0.32 per call
        Assert.True(Err(m) < before * 0.4, $"{Err(m)} vs {before}");
        for (int i = 0; i < 12; i++) FxMath.Orthonormalize(ref m);   // repeated calls (every 16/32 steps) converge
        Assert.True(Err(m) <= 0x10, $"{Err(m)}");
    }

    [Fact]
    public void RotateMatrix_ThresholdsAndAccumulator()
    {
        int acc = 0;
        // |ay| = 0x10 ≤ 0x13, |ax| < 0xE, |az| ≤ 0xD: nothing "changed", result still RotY(ay)
        Assert.False(FxMath.RotateMatrix(out var r, 5, 0x10, -0xD, out bool re, 0x1000, ref acc));
        Assert.Equal(FxMath.RotY(0x10), r);
        Assert.Equal(5 + 0x10 + 0xD, acc);
        Assert.False(re);
        Assert.True(FxMath.RotateMatrix(out r, 0, 0x14, 0, out _, 0x1000, ref acc));            // |ay| > 0x13
        Assert.True(FxMath.RotateMatrix(out r, -0xE, 0, 0, out _, 0x1000, ref acc));            // RotX applied
        Assert.Equal(Mat3.Mul(FxMath.RotX(-0xE), FxMath.RotY(0)), r);
        Assert.True(FxMath.RotateMatrix(out r, 0x20, 0x30, 0xE, out _, 0x1000, ref acc));       // X·Y·Z
        Assert.Equal(Mat3.Mul(Mat3.Mul(FxMath.RotX(0x20), FxMath.RotY(0x30)), FxMath.RotZ(0xE)), r);
        acc = 0x1000;
        FxMath.RotateMatrix(out _, 0, 0, 0, out re, 0x1000, ref acc);
        Assert.False(re);                                                                        // not > threshold
        FxMath.RotateMatrix(out _, 0, 1, 0, out re, 0x1000, ref acc);
        Assert.True(re);
    }
}
