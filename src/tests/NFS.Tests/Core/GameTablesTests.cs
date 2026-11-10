using NFS.Core;
using NFS.Core.Constants;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

public class GameTablesTests
{
    [Fact]
    public void SinTable_KnownEntries()
    {
        var t = GameTables.FromConstants().SinQuarter;
        Assert.Equal(257, t.Length);
        Assert.Equal((0, 402, 0x10000), (t[0], t[1], t[256]));          // [ref:3068]
        for (int i = 1; i <= 256; i++) Assert.True(t[i] > t[i - 1]);
        for (int i = 0; i <= 256; i++) Assert.InRange(t[i] - Math.Sin(i * Math.PI / 512) * 65536, -1.5, 1.5);   // not pure round/floor [ref:3069]
    }

    [Fact]
    public void AtanTables_EqualTheFormula()
    {
        var t = GameTables.FromConstants();
        Assert.Equal(MathConstants.GenerateAtan8(), t.Atan8);
        Assert.Equal(MathConstants.GenerateAtan16(), t.Atan16);
        Assert.Equal(0x80, t.Atan8[256]);
        Assert.Equal(0x2000, t.Atan16[256]);
    }

    [Fact]
    public void SqrtAndOrthoConstants()
    {
        var t = GameTables.FromConstants();
        Assert.Equal((0x6A09E6u, 0x96u), (t.SqrtMul[0], t.SqrtMul[31]));   // [ref:3070]
        Assert.Equal((0x98u, 0x6B8A88u), (t.SqrtAdd[0], t.SqrtAdd[31]));
        Assert.Equal([-0x2000, 0x1800, -0x1400], t.OrthoCoeffs);            // −1/8, 3/32, −5/64
    }

    [Fact]
    public void LodTables()
    {
        var t = GameTables.FromConstants();
        Assert.Equal([0, 1, 2, 4, 8, 16, 32], t.LodRateHz);
        Assert.Equal([32, 32, 16, 8, 4, 2, 1], t.LodTicks);
        Assert.Equal([0, 0x10000, 0x8000, 0x4000, 0x2000, 0x1000, 0x800], t.LodDt);
        Assert.Equal([63, 31, 15, 7, 3, 1, 0], t.LodMask);
        var row3 = t.LodRows.AsSpan(3 * 20, 20).ToArray();
        Assert.Equal(new byte[] { 6, 6, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 }, row3);
    }

}
