using NFS.Game.Race;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>The knockout series of the dedicated server (<see cref="KnockoutSeries.ForNetwork"/>): the same elimination and grid
/// logic as the local series, with 1..8 humans, no series-ending DNF and forfeits of humans that are gone.</summary>
public class NetworkKnockoutSeriesTests
{
    /// <summary>Results of a round of <paramref name="n"/> cars; <paramref name="order"/> lists the car indices from first to last.</summary>
    static RaceResults Results(int n, int[]? order = null, int[]? dnf = null)
    {
        order ??= Enumerable.Range(0, n).ToArray();
        return new RaceResults(order.Select((car, p) => new RacerResult(p + 1, car, $"CAR{car}", car < 3, false, 1000 + p, [], null,
            dnf?.Contains(car) == true ? 1 : 2)).ToArray());
    }

    [Fact]
    public void LocalSeriesStillRefusesMoreThanTwoHumans()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KnockoutSeries(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnockoutSeries.ForNetwork(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnockoutSeries.ForNetwork(0));
        Assert.Equal(8, KnockoutSeries.ForNetwork(8).Humans);
    }

    [Fact]
    public void EveryRoundEliminatesTheLastPlaceUntilOneEntrantIsLeft()
    {
        var ko = KnockoutSeries.ForNetwork(3);
        Assert.Equal(Enumerable.Range(0, 9), ko.RaceIdentities);
        for (int round = 0; round < 8; round++)
        {
            int n = ko.RaceIdentities.Count;
            Assert.Equal(9 - round, n);
            // The best car (index 0) wins, the highest index is last.
            ko.Complete(Results(n));
            int last = ko.RaceIdentities[n - 1];
            Assert.Equal([last], ko.Eliminated);
            Assert.Equal(round, ko.EliminatedRound(last));
            Assert.Equal(8 - round, ko.Entrants.Count);
            if (round < 7) { Assert.True(ko.CanContinue); ko.NextRace(); }
        }
        Assert.True(ko.Finished);
        Assert.Equal(0, ko.Winner);
        Assert.False(ko.CanContinue);
    }

    [Fact]
    public void CompleteCountsAResultOnlyOnce()
    {
        var ko = KnockoutSeries.ForNetwork(2);
        ko.Complete(Results(9, [8, 0, 1, 2, 3, 4, 5, 6, 7]));
        Assert.Equal([7], ko.Eliminated);
        ko.Complete(Results(9)); // a second delivery of any result changes nothing
        Assert.Equal([7], ko.Eliminated);
        Assert.Equal(8, ko.Entrants.Count);
    }

    [Fact]
    public void ADnfHumanIsEliminatedWithTheLastPlaceButAStragglerOnlyIsRankedLast()
    {
        var ko = KnockoutSeries.ForNetwork(3);
        // Human car 1 was gone from the round and is ranked last (place 9); car 6 is an AI car in second to last place.
        ko.Complete(Results(9, [0, 2, 3, 4, 5, 7, 8, 6, 1], dnf: [1, 6]), disconnectedCars: [1]);
        Assert.Equal([1], ko.Eliminated);
        Assert.False(ko.Aborted); // a DNF never ends a network series
        Assert.True(ko.HumanActive(0) && ko.HumanActive(2) && !ko.HumanActive(1));
        Assert.Contains(6, ko.Entrants); // a DNF AI car is not eliminated by the DNF, only the last place is
    }

    [Fact]
    public void SeveralGoneHumansAreAllEliminatedTogetherWithTheLastPlace()
    {
        var ko = KnockoutSeries.ForNetwork(4);
        ko.Complete(Results(9, [0, 1, 4, 5, 6, 7, 8, 3, 2], dnf: [2, 3]), disconnectedCars: [2, 3]);
        Assert.Equal([2, 3], ko.Eliminated.Order());
        Assert.Equal(7, ko.Entrants.Count);
        Assert.Equal(2, new[] { ko.EliminatedRound(2), ko.EliminatedRound(3) }.Count(r => r == 0));
        Assert.True(ko.CanContinue); // humans 0 and 1 are still in
    }

    [Fact]
    public void TheSeriesEndsWhenNoHumanIsLeft()
    {
        var ko = KnockoutSeries.ForNetwork(1);
        ko.Complete(Results(9, [1, 2, 3, 4, 5, 6, 7, 8, 0], dnf: [0]), disconnectedCars: [0]);
        Assert.True(ko.Finished);
        Assert.False(ko.CanContinue);
        Assert.Equal(-1, ko.Winner);
        Assert.Throws<InvalidOperationException>(ko.NextRace);
    }

    [Fact]
    public void AForfeitLeavesNoGapInTheGridAndTheOrderOfTheRestIsKept()
    {
        var ko = KnockoutSeries.ForNetwork(3);
        ko.Complete(Results(9)); // identity 8 out
        ko.Forfeit(1);           // human 1 is gone before the next round
        ko.NextRace();
        Assert.Equal([0, 2, 3, 4, 5, 6, 7], ko.RaceIdentities);
        // Rank 1 starts at the front (highest slot of 0..6), the last of the previous round at the back.
        Assert.Equal([6, 5, 4, 3, 2, 1, 0], ko.RaceIdentities.Select(ko.GridSlot));
        Assert.Equal(1, ko.EliminatedRound(1)); // it missed the round after the one that was played
        Assert.True(ko.HumanActive(0) && ko.HumanActive(2));
    }

    [Fact]
    public void ForfeitIsOnlyForNetworkSeriesAndOnlyForHumans()
    {
        Assert.Throws<InvalidOperationException>(() => new KnockoutSeries().Forfeit(0));
        var ko = KnockoutSeries.ForNetwork(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => ko.Forfeit(2));
    }

    [Fact]
    public void ResultsThatDoNotMatchTheEntrantsAreRefused()
    {
        var ko = KnockoutSeries.ForNetwork(2);
        Assert.Throws<ArgumentException>(() => ko.Complete(Results(8)));
        Assert.False(ko.ResultsApplied);
    }
}
