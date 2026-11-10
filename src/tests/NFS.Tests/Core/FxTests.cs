using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>16.16 primitives; expected values derived by hand from the original's arithmetic, as quoted in Fx.cs.</summary>
public class FxTests
{
    [Theory]
    [InlineData(0x18000, 0x18000, 0x24000)]          // 1.5·1.5 = 2.25 exactly
    [InlineData(1, 0x8000, 1)]                       // 0x8000 + 0x8000 → bit 16: half rounds up
    [InlineData(1, 0x7FFF, 0)]                       // bit 15 clear → down
    [InlineData(-1, 0x8000, 0)]                      // −0x8000 + 0x8000 = 0: half rounds toward +∞
    [InlineData(-1, 0x8001, -1)]                     // −0x8001 + 0x8000 = −1 → arithmetic shift → −1
    [InlineData(0x7FFF0000, 0x20000, -0x20000)]      // 32767·2 overflows: low 32 bits 0xFFFE0000
    [InlineData(-0x30000, 0x28000, -0x78000)]        // −3·2.5
    public void Mul_RoundsHalfUpAndWraps(int a, int b, int expected) => Assert.Equal(expected, Fx.Mul(a, b));

    [Fact]
    public void Mul_MatchesRegisterLevelIdiom()
    {
        // 64-bit product, shifted right by 16 with rounding via bit 15 — emulated step by step for pseudo-random operands
        var rnd = new Random(1);
        for (int i = 0; i < 10000; i++)
        {
            int a = rnd.Next(int.MinValue, int.MaxValue), b = rnd.Next(int.MinValue, int.MaxValue);
            long p = (long)a * b;
            uint eax = (uint)p, edx = (uint)(p >> 32);
            uint cf = (eax >> 15) & 1;
            int asm = (int)((edx << 16) + (eax >> 16) + cf);
            Assert.Equal(asm, Fx.Mul(a, b));
        }
    }

    [Theory]
    [InlineData(0x10000, 0x30000, 0x5555)]           // 1/3: 0x100000000/0x30000 = 0x5555.55 → trunc
    [InlineData(-0x10000, 0x30000, -0x5555)]         // truncation toward zero, not floor
    [InlineData(0x50000, 0x20000, 0x28000)]          // 5/2 = 2.5
    [InlineData(-7, 2, -0x38000)]                    // q=−3, r=−1: (−3<<16) + (−0x10000/2) = −3.5
    [InlineData(1, 0x10000, 1)]
    public void Div_TruncatesTowardZero(int a, int b, int expected) => Assert.Equal(expected, Fx.Div(a, b));

    [Fact]
    public void Div_EqualsLongDivisionWhenInRange()
    {
        var rnd = new Random(2);
        for (int i = 0; i < 10000; i++)
        {
            int a = rnd.Next(-0x7FFF_FFFF, 0x7FFF_FFFF) >> rnd.Next(0, 16);
            int b = rnd.Next(-0x7FFF_FFFF, 0x7FFF_FFFF) >> rnd.Next(0, 16);
            if (b == 0) continue;
            long q = ((long)a << 16) / b;
            if (q is > int.MaxValue or < int.MinValue) continue;
            Assert.Equal((int)q, Fx.Div(a, b));
        }
    }

    [Theory]
    [InlineData(-3, 1, -1, -2)]
    [InlineData(3, 1, 1, 1)]
    [InlineData(-0x10001, 16, -1, -2)]
    [InlineData(-64, 6, -1, -1)]                     // exact multiples agree
    [InlineData(-65, 6, -1, -2)]
    public void DivPow2_TruncatesWhileShrFloors(int x, int n, int div, int shr)
    {
        Assert.Equal(div, Fx.DivPow2(x, n));
        Assert.Equal(shr, Fx.Shr(x, n));
    }

    [Fact]
    public void DivPow2_MatchesSarShlSbbIdiom()
    {
        // bias negative values so the shift truncates toward zero, then shift by n
        var rnd = new Random(3);
        for (int i = 0; i < 10000; i++)
        {
            int x = rnd.Next(int.MinValue, int.MaxValue), n = rnd.Next(1, 31);
            uint edx = (uint)(x >> 31);
            uint cf = n == 0 ? 0 : (edx >> (32 - n)) & 1;
            edx <<= n;
            int eax = (int)((uint)x - edx - cf) >> n;
            Assert.Equal(eax, Fx.DivPow2(x, n));
            Assert.Equal(x / (1 << n), Fx.DivPow2(x, n));   // = C division
        }
    }

    [Fact]
    public void Abs_KeepsMinValue() => Assert.Equal(int.MinValue, Fx.Abs(int.MinValue));

    [Fact]
    public void Dot_IsSumOfThreeRoundedProducts()
    {
        // each FX(1, 0x8000) rounds up to 1 → 3 (an exact 64-bit sum would give 1.5 → 2 after one rounding)
        Assert.Equal(3, Fx.Dot(1, 1, 1, 0x8000, 0x8000, 0x8000));
        Assert.Equal(3, Vec3.Dot(new Vec3(1, 1, 1), new Vec3(0x8000, 0x8000, 0x8000)));
    }

    [Fact]
    public void Cross_OfAxesGivesThirdAxis()
    {
        var x = new Vec3(Fx.One, 0, 0); var y = new Vec3(0, Fx.One, 0);
        Assert.Equal(new Vec3(0, 0, Fx.One), Vec3.Cross(x, y));
        Assert.Equal(new Vec3(0, 0, -Fx.One), Vec3.Cross(y, x));
    }
}
