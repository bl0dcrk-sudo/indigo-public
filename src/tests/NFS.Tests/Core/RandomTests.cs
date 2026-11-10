using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>Sequences computed independently by (register-level emulation of the original's code [ref:3084] [ref:3480]
/// and the inlined LCG). [ref:3085]</summary>
public class RandomTests
{
    [Fact]
    public void EaRandom_Seed0()
    {
        var r = new EaRandom(0);
        uint[] expected = [0x559A51ED, 0x274EA7F5, 0xE827F7E0, 0x76B65632, 0x7F1B3FEB, 0xEAE8D5EE, 0x5101186F, 0x6575225A];
        Assert.Equal(expected, expected.Select(_ => r.Next()).ToArray());
    }

    [Fact]
    public void EaRandom_Seed0x1234()
    {
        var r = new EaRandom(0x1234);
        uint[] expected = [0x559ABF25, 0x27502639, 0xE82BF340, 0x76BF4BCA, 0x7F2D2B1B, 0xEB09AFC6, 0x5139694F, 0x65D0A5C6];
        Assert.Equal(expected, expected.Select(_ => r.Next()).ToArray());
    }

    [Fact]
    public void EaRandom_NegativeShortSeedIsSignExtended()
    {
        var r = new EaRandom((short)-1);   // (s16)time() may be negative
        uint[] expected = [0x559A51E7, 0x274EA7E0, 0xE827F7A8, 0x76B655B4, 0x7F1B3EEF, 0xEAE8D420, 0x51011557, 0x65751D53];
        Assert.Equal(expected, expected.Select(_ => r.Next()).ToArray());
    }

    [Fact]
    public void EaRandom_CounterCarryIncrementsResult()
    {
        // hand: S = [5,0,0,0,0,0xFFFFFFFF]: S4 = S5+S4 = 0xFFFFFFFF (c0), S3 = S2 = S1 = 0xFFFFFFFF, r = 0xFFFFFFFF+5 = 4;
        // counter: ++S5 = 0, ++S4 = 0, ++S3 = 0, ++S2 = 0, ++S1 = 0 → carry out → increment → r = 5 [ref:3086]
        var a = new EaRandom();
        a.SetState([5, 0, 0, 0, 0, 0xFFFFFFFF]);
        Assert.Equal(5u, a.Next());
        Assert.Equal([5u, 0, 0, 0, 0, 0], a.State);
        // without the full carry: S5 = 0xFFFFFFFE → r = 0xFFFFFFFE + 5 = 3, counter S5 → 0xFFFFFFFF
        var b = new EaRandom();
        b.SetState([5, 0, 0, 0, 0, 0xFFFFFFFE]);
        Assert.Equal(3u, b.Next());
        Assert.Equal([3u, 0xFFFFFFFE, 0xFFFFFFFE, 0xFFFFFFFE, 0xFFFFFFFE, 0xFFFFFFFF], b.State);
    }

    [Fact]
    public void AiRandom_Seed12345()
    {
        var r = new AiRandom(12345);
        Assert.Equal(0x291D28EBu, r.Last);
        Assert.Equal(0x28EBu, r.State);
        Assert.Equal(12345u, r.Multiplier);
        uint[] expected = [0x7B52C53, 0x8596E7B, 0x14CFA963, 0x1FE8470B, 0xD61E173, 0x2A77C29B];
        Assert.Equal(expected, expected.Select(_ => r.Next()).ToArray());
        Assert.Equal(0x2A77C29Bu, r.Last);
    }

    [Fact]
    public void AiRandom_EvenSeedBecomesOdd()
    {
        var r = new AiRandom(2);   // mul = 3, x = 6 → 7
        Assert.Equal(3u, r.Multiplier);
        Assert.Equal(0xB367u, r.Last);
        uint[] expected = [0x21A35, 0x4E9F, 0xEBDD, 0x2C397, 0x24AC5, 0xE04F];
        Assert.Equal(expected, expected.Select(_ => r.Next()).ToArray());
    }

    [Fact]
    public void AiRandom_Mid16()
    {
        var a = new AiRandom(12345); var b = new AiRandom(12345);
        Assert.Equal((int)((a.Next() & 0xFFFF00) >> 8), b.NextMid16());
    }

    [Fact]
    public void WatcomRandom_DefaultSeed()
    {
        var r = new WatcomRandom();
        int[] expected = [16838, 5758, 10113, 17515, 31051];   // s = s·0x41C64E6D + 0x3039, (s>>16) & 0x7FFF from s = 1
        Assert.Equal(expected, expected.Select(_ => r.Next()).ToArray());
    }
}
