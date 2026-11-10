using NFS.Core;
using NFS.Game.Race;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Race order (values from the original's formulas). [ref:3216]</summary>
public class RacerLapTaskTests
{
    private const int N = 1863;
    private static readonly int[] Vmax = Enumerable.Repeat(0x500000, 15).ToArray();

    private static CarObj Car(int id, CarFlags flags = CarFlags.Racer | CarFlags.Opponent)
    {
        var c = new CarObj { PlayerIndex = id, Flags = flags };
        RacerLapTask.StatsReset(c);
        return c;
    }

    private static void Run(CarObj c, int tick, int model = 0, int laps = 3, bool reverse = false, WatcomRandom? rand = null, RaceOrder? order = null)
        => RacerLapTask.Run(c, model, tick, laps, N, reverse, Vmax, rand ?? new WatcomRandom(), order ?? new RaceOrder(1));

    [Fact]
    public void StatsReset_StartsLapClockAtGreenLight()
    {
        var c = Car(0);
        c.LapTimes[2] = 5; c.RacePosition = 3;
        RacerLapTask.StatsReset(c);
        Assert.Equal(0x200, c.LapStartTick);
        Assert.Equal(0, c.LapsDone);
        Assert.Equal(0, c.LapTimes[2]);
        Assert.Equal(0, c.RacePosition);
    }

    [Theory]
    [InlineData(false, 100, 100)]            // forward: (laps − offset)·n + slice
    [InlineData(true, 100, N - 100 - N)]     // reverse, offset 1 (as after): (0 − 1)·n (n − slice) [ref:3217]
    public void SliceChange_UpdatesDistanceAndProgressTick(bool reverse, int slice, int expected)
    {
        var c = Car(0);
        c.LapOffset = reverse ? 1 : 0;
        c.Trk.Slice = slice;
        Run(c, 700, reverse: reverse);
        Assert.Equal(expected, c.DistanceSlices);
        Assert.Equal(700, c.LastProgressTick);
        Assert.Equal(slice, c.LastSlice);
        Run(c, 702, reverse: reverse);            // same slice: nothing changes
        Assert.Equal(700, c.LastProgressTick);
    }

    [Fact]
    public void LapChange_RecordsLapTimeAndRestartsClock()
    {
        var c = Car(0);
        c.Lap = 1;
        Run(c, 0x200 + 5000);
        Assert.Equal(1, c.LapsDone);
        Assert.Equal(5000, c.LapTimes[0]);
        Assert.Equal(0x200 + 5000, c.LapStartTick);
        c.Lap = 2;
        Run(c, 0x200 + 5000 + 4321);
        Assert.Equal(4321, c.LapTimes[1]);
        Assert.Equal(0, c.RaceState);
    }

    [Fact]
    public void FinalLap_FinishesRaceWithRaceTimeAndFrozenDistance()
    {
        var c = Car(0);
        c.LapsDone = 2; c.LapStartTick = 9000;
        c.Lap = 3;
        Run(c, 12000, laps: 3);
        Assert.Equal(2, c.RaceState);
        Assert.Equal(3000, c.LapTimes[2]);
        Assert.Equal(12000 - 0x200, c.LapStartTick);          // +0x234 −= 0x200: total race time
        Assert.Equal(12000, c.LastProgressTick);
        Assert.Equal(4 * N, c.DistanceSlices);                 // (laps + 1)·nSlices
        c.Trk.Slice = 5;                                        // finished cars no longer advance
        Run(c, 12010, laps: 3);
        Assert.Equal(4 * N, c.DistanceSlices);
        Assert.Equal(12000, c.LastProgressTick);
    }

    [Fact]
    public void Observer_NeverFinishesAndIsNotRanked()
    {
        var c = Car(0);
        var order = new RaceOrder(2);
        c.LapsDone = 2; c.Lap = 3; c.LapStartTick = 100;
        Run(c, 500, model: 0x24, laps: 3, order: order);
        Assert.Equal(0, c.RaceState);
        Assert.Equal(0, order.PositionOf(c));
    }

    [Fact]
    public void MaxSpeed_TracksPeakPerLap_OpponentsDrawAtTheModelCap()
    {
        var c = Car(0, CarFlags.Racer | CarFlags.LocalHuman);
        c.LocalVel.Z = 0x300000;
        Run(c, 600);
        c.LocalVel.Z = 0x2F0000;
        Run(c, 602);
        Assert.Equal(0x300000, c.LapMaxSpeed[0]);

        var ai = Car(1);
        ai.LocalVel.Z = 0x300000;                // below the cap: one draw (the cap test), stored as is
        var rnd = new WatcomRandom();
        Run(ai, 600, rand: rnd);
        Assert.Equal(0x300000, ai.LapMaxSpeed[0]);
        var one = new WatcomRandom(); one.Next();
        Assert.Equal(one.State, rnd.State);

        ai.LocalVel.Z = 0x7FFFFF;                // above the cap: two draws, stored = vmax − 3·rand()
        var expect = new WatcomRandom(); expect.Next();   // the draw of the previous call
        int r1 = expect.Next(), r2 = expect.Next();
        Assert.True(Vmax[0] - 3 * r1 < 0x7FFFFF);
        Run(ai, 602, rand: rnd);
        Assert.Equal(Vmax[0] - 3 * r2, ai.LapMaxSpeed[0]);
        Assert.Equal(expect.State, rnd.State);
    }

    [Fact]
    public void RaceOrder_SortsByDistance_TieBrokenByEarlierProgressTick()
    {
        var order = new RaceOrder(4);
        var a = Car(10); a.DistanceSlices = 50; a.LastProgressTick = 900;
        var b = Car(11); b.DistanceSlices = 80; b.LastProgressTick = 950;
        var c = Car(12); c.DistanceSlices = 50; c.LastProgressTick = 800;   // same distance as a, reached earlier
        var d = Car(13); d.DistanceSlices = -30; d.LastProgressTick = 100;
        foreach (var x in new[] { a, b, c, d }) order.Insert(x);
        Assert.Equal([1, 2, 3, 4], [order.PositionOf(b), order.PositionOf(c), order.PositionOf(a), order.PositionOf(d)]);
        order.Reset();
        Assert.Equal(0, order.PositionOf(a));
    }

    [Fact]
    public void BodyShakeStart_UsesImpactForAmplitudeAndTwoRandomSigns()
    {
        var c = new CarObj { Impact = 0x180000 };
        var rnd = new WatcomRandom();
        var probe = new WatcomRandom();
        bool neg0 = probe.Next() % 0x100 > 0x80, neg1 = probe.Next() % 0x100 > 0x80;
        CarPost.BodyShakeStart(c, 0x15, 0x23, rnd);
        Assert.Equal(0x15, c.ShakePhase0);
        Assert.Equal(0x23, c.ShakePhase1);
        int amp = (0x180000 - 0xA0000) * 2;
        Assert.Equal(neg0 ? -amp : amp, c.ShakeAmp0);
        Assert.Equal(neg1 ? -c.ShakeAmp0 : c.ShakeAmp0, c.ShakeAmp1);
        c.Impact = 0x900000;                       // amplitude is clamped to 0x2D0000
        CarPost.BodyShakeStart(c, 1, 2, rnd);
        Assert.Equal(0x2D0000, Math.Abs(c.ShakeAmp0));
    }
}
