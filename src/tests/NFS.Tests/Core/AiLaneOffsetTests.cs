using NFS.Core;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>AI random lane slack +0x3A4 and their [ref:3029]
/// use of the AI LCG (*..EC). Tick-exact agreement with the original is checked by the private trace tests. [ref:3030]</summary>
public class AiLaneOffsetTests
{
    private const int Slices = 1863;
    private static readonly int[] Slack = [13107, 26214, -13107, -26214];
    private static readonly int[] Traffic = [1, 2, 3, 4];

    private static CarObj Car(CarFlags flags, int index, int slice, int rate = 0)
    {
        var c = new CarObj { Flags = flags, PlayerIndex = index, OffsetChangeRate = rate };
        c.Trk.Slice = slice;
        return c;
    }

    private static bool Drew(CarObj c, bool reverse, int prob = 20, int seed = -23536)
    {
        var rng = new AiRandom(seed);
        uint before = rng.State;
        AiLaneOffset.MaybeChange(c, rng, Slices, reverse, Slack, prob, Traffic);
        return rng.State != before;
    }

    [Theory]
    [InlineData(false, 2, 12, true)]    // forward: s = slice + 2·index = 16
    [InlineData(false, 2, 13, false)]
    [InlineData(true, 2, 20, true)]     // reverse: s = slice − 2·index = 16
    [InlineData(true, 2, 12, false)]
    [InlineData(true, 7, 7, true)]      // reverse, wrapped: 7 − 14 + 1863 = 1856 = 116·16
    [InlineData(true, 7, 6, false)]
    [InlineData(false, 7, 1849, true)]  // forward, wrapped: 1849 + 14 − 1863 = 0
    [InlineData(false, 7, 1850, false)]
    public void Opponent_DrawsOnlyOnItsCheckpointSlices(bool reverse, int index, int slice, bool expected)
    {
        Assert.Equal(expected, Drew(Car(CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent, index, slice), reverse));
    }

    [Fact]
    public void LocalHuman_NeverDraws()
    {
        for (int slice = 0; slice < 64; slice++)
            Assert.False(Drew(Car(CarFlags.Racer | CarFlags.LocalHuman, 0, slice), false));
    }

    [Fact]
    public void Traffic_DrawsOnEvenCheckpoints_WithItsOwnRateAndTable()
    {
        Assert.True(Drew(Car(CarFlags.NotLocal | CarFlags.Traffic, 3, 10, rate: 2), false));   // 10 + 6 = 16
        Assert.False(Drew(Car(CarFlags.NotLocal | CarFlags.Traffic, 3, 11, rate: 2), false));
        // rate 2 → about every second draw picks, and only from the traffic table
        var rng = new AiRandom(12345);
        int picks = 0;
        for (int i = 0; i < 400; i++)
        {
            var c = Car(CarFlags.NotLocal | CarFlags.Traffic, 0, 0, rate: 2);
            if (AiLaneOffset.MaybeChange(c, rng, Slices, false, Slack, 0, Traffic))
            {
                picks++;
                Assert.Contains(c.LaneRandomOffset, Traffic);
            }
        }
        Assert.InRange(picks, 150, 250);
    }

    [Fact]
    public void PickCondition_IsMid16TimesProbabilityShiftedEqualsOne_ThenSecondDrawSelectsSlack()
    {
        // Reference written from the original's low-level code: draw 1 decides, draw 2 indexes the personality slack. [ref:3031]
        var rng = new AiRandom(-23536);
        var reference = new AiRandom(-23536);
        int picks = 0;
        for (int i = 0; i < 2000; i++)
        {
            var c = Car(CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent, 0, 0);
            bool picked = AiLaneOffset.MaybeChange(c, rng, Slices, false, Slack, 20, Traffic);
            bool expected = (((reference.Next() & 0xFFFF00) >> 8) * 20 >> 16) == 1;
            Assert.Equal(expected, picked);
            if (!expected) { Assert.Equal(0, c.LaneRandomOffset); continue; }
            picks++;
            Assert.Equal(Slack[(int)((reference.Next() & 0xFFFF00) >> 8) & 3], c.LaneRandomOffset);
        }
        Assert.Equal(reference.State, rng.State);
        Assert.InRange(picks, 50, 150); // ≈ 1/20 of the draws
    }

    [Fact]
    public void ProbabilityZeroOrOne_NeverPicksButStillConsumesTheDraw()
    {
        foreach (int prob in new[] { 0, 1 })
        {
            var rng = new AiRandom(777);
            var reference = new AiRandom(777);
            for (int i = 0; i < 500; i++)
            {
                var c = Car(CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent, 0, 0);
                Assert.False(AiLaneOffset.MaybeChange(c, rng, Slices, false, Slack, prob, Traffic));
                reference.Next();
                Assert.Equal(reference.State, rng.State);
            }
        }
    }
}
