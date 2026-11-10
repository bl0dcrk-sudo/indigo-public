using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class RaceProgressTests
{
    static CarObj Car(int id) => new() { PlayerIndex = id, Flags = CarFlags.Racer };
    static RaceProgress Progress(CarObj[] cars, bool[] humans, int[]? models = null, int laps = 2)
        => new(1000, laps, cars, humans, models ?? new int[cars.Length]);

    [Fact]
    public void FacadeImmediatelyReflectsAuthoritativeLapFieldsAndRanking()
    {
        var a = Car(0); var b = Car(1);
        var p = Progress([a, b], [true, false]);
        a.LapsDone = 1; a.LapTimes[0] = 4000; a.LapStartTick = 4512;
        a.DistanceSlices = 1100; a.RacePosition = 2; b.RacePosition = 1;
        Assert.Equal(1, p.Cars[0].LapsDone);
        Assert.Equal([4000], p.Cars[0].LapTimes);
        Assert.Equal(1100, p.Cars[0].Position);
        Assert.Equal([1, 0], p.Ranking());
        a.RacePosition = 1; b.RacePosition = 2;
        Assert.Equal([0, 1], p.Ranking());
        a.RaceState = 2; a.LapsDone = 2; a.LapStartTick = 9000; a.LastProgressTick = 9512;
        Assert.Equal(9000, p.Cars[0].RaceTime);
        Assert.Equal(9512, p.Cars[0].FinishTick);
    }

    [Fact]
    public void FinishWaitsStrictlyMoreThanFiveSecondsForEveryHuman_ThenEstimatesAiFromLastProgress()
    {
        var a = Car(0); var b = Car(1); var ai = Car(2);
        var p = Progress([a, b, ai], [true, true, false]);
        a.RaceState = 2; a.LastProgressTick = 1000; a.LapStartTick = 488;
        p.CheckFinish(2000); Assert.False(p.RaceOver);
        b.RaceState = 2; b.LastProgressTick = 1200; b.LapStartTick = 688;
        ai.DistanceSlices = 1000; ai.LastProgressTick = 1112;
        p.CheckFinish(1520); Assert.False(p.RaceOver);
        p.CheckFinish(1522); Assert.True(p.RaceOver);
        Assert.Equal(2, ai.RaceState);
        Assert.Equal(1200, ai.LapStartTick); // 2000 * (1112 - 512) / 1000, NOT elapsed wall time
        Assert.Equal(0, p.Cars[2].FinishTick); // estimated finish is not a crossed finish line
        Assert.Equal(488, a.LapStartTick);
        Assert.Equal(688, b.LapStartTick);
        p.CheckFinish(2000); Assert.Equal(1200, ai.LapStartTick);
    }

    [Fact]
    public void ObserversDoNotBlockFinish_ButAllObserversOrNoHumansNeverEndRace()
    {
        var human = Car(0); var observer = Car(1);
        human.RaceState = 2; human.LastProgressTick = 1000;
        var p = Progress([human, observer], [true, true], [0, 0x24]);
        p.CheckFinish(1322);
        Assert.True(p.RaceOver);
        Assert.Equal(2, observer.RaceState);
        Assert.Equal([0], p.Ranking());
        var only = Progress([Car(0)], [true], [0x24]); only.CheckFinish(10000);
        Assert.False(only.RaceOver);
        var none = Progress([Car(0)], [false]); none.CheckFinish(10000);
        Assert.False(none.RaceOver);
    }

    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(1, 2000000, -295991296)] // unchecked low-32-bit product, then signed division
    public void EndEstimatePreservesZeroDistanceAndWatcomOverflow(int distance, int lastTick, int expected)
    {
        var h = Car(0); h.RaceState = 2; h.LastProgressTick = 1000;
        var ai = Car(1); ai.DistanceSlices = distance; ai.LastProgressTick = lastTick;
        var p = Progress([h, ai], [true, false]); p.CheckFinish(1322);
        Assert.Equal(expected, ai.LapStartTick);
    }

    [Fact]
    public void CrossingBackwardAndForwardDoesNotCountLapTwice()
    {
        var c = Car(0); c.PrevSlice = 1800; c.Trk.Slice = 0;
        RaceProgress.UpdateCarProgress(c, 1863, false); Assert.Equal(1, c.Lap);
        c.Trk.Slice = 1862; RaceProgress.UpdateCarProgress(c, 1863, false);
        c.Trk.Slice = 0; RaceProgress.UpdateCarProgress(c, 1863, false);
        Assert.Equal(1, c.Lap); Assert.Equal(0, c.LapOffset);
    }

    [GameFact]
    public void ConfiguredFinalLapFlowsThroughRunnerIntoHudStateAndRaceEnd()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), withAi: true, countdown: false);
        Assert.Equal(run.Session.Setup.NumLaps, run.Laps.NumLaps);
        run.SetLaps(1);
        for (int i = 0; i < 100; i++) run.Step(default);
        run.Car.Lap = 1;
        run.Step(default);
        Assert.Equal(2, run.Car.RaceState);
        Assert.Equal(1, run.Laps.Cars[0].LapsDone);
        Assert.Equal(run.Car.LapStartTick, run.Laps.Cars[0].RaceTime);
        while (run.Tick <= run.Car.LastProgressTick + RaceProgress.FinishDelay) run.Step(default);
        Assert.False(run.Laps.RaceOver);
        run.Step(default); Assert.True(run.Laps.RaceOver);
        var snapshot = RaceResults.Capture(run);
        Assert.Equal(8, snapshot.Racers.Count);
        Assert.Contains(snapshot.Racers, r => r.Human && !r.Estimated && r.LapTicks.Count == 1);
        var result = snapshot.Racers.Single(r => r.Human);
        Assert.Equal(result.LapTicks.Min(), result.BestLapTicks);
        int saved = result.LapTicks[0];
        run.Car.LapTimes[0] = 999;
        Assert.Equal(saved, result.LapTicks[0]);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(0x120, "")]
    [InlineData(0x121, "READY")]
    [InlineData(0x140, "3")]
    [InlineData(0x17F, "3")]
    [InlineData(0x180, "2")]
    [InlineData(0x1BF, "2")]
    [InlineData(0x1C0, "1")]
    [InlineData(0x1FF, "1")]
    [InlineData(0x200, "GO")]
    [InlineData(0x21F, "GO")]
    [InlineData(0x220, "")]
    public void CountdownFollowsOriginalLightStagesAndVisibilityWindow(int tick, string text)
        => Assert.Equal(text, RacePresentation.CountdownText(tick));
}
