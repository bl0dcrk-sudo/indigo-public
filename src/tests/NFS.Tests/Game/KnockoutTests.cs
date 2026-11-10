using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class KnockoutTests
{
    static RaceResults Results(int n, int loser = -1, int dnf = -1)
    {
        if (loser < 0) loser = n - 1;
        var order = Enumerable.Range(0, n).Where(i => i != loser).Append(loser).ToArray();
        return new RaceResults(order.Select((i, p) => new RacerResult(p + 1, i, $"CAR{i}", i == 0,
            i != 0, 1000 + p, [], null, i == dnf ? 1 : 2)).ToArray());
    }

    static void Select(MenuController m, string text)
    {
        for (int i = 0; i < m.CurrentScreen.Items.Count; i++)
        {
            if (m.CurrentScreen.Items[m.SelectedIndex].Text == text) return;
            m.HandleKey(MenuKey.Down);
        }
        throw new Exception($"Missing menu item {text}");
    }
    static void Open(MenuController m, string text) { Select(m, text); m.HandleKey(MenuKey.Enter); }
    static MenuController Start()
    {
        var m = MenuController.Load(GameData.Dir);
        Open(m, "game setup"); Select(m, "race type");
        m.HandleKey(MenuKey.Right); m.HandleKey(MenuKey.Right); Open(m, "DONE");
        Open(m, "RACE"); return m;
    }
    static void Summary(MenuController m) { Open(m, "continue"); Open(m, "continue"); Assert.True(m.KnockoutSummary); }

    [Fact]
    public void CompressedIndicesKeepIdentity_AndReverseLastPlaces()
    {
        var ko = new KnockoutSeries();
        Assert.Equal(Enumerable.Range(0, 9), ko.RaceIdentities.Select(ko.GridSlot));
        ko.Complete(Results(9, loser: 2));
        Assert.Equal(2, ko.EliminatedIdentity);
        ko.NextRace();
        Assert.Equal([0, 1, 3, 4, 5, 6, 7, 8], ko.RaceIdentities);
        Assert.Equal([7, 6, 5, 4, 3, 2, 1, 0], ko.RaceIdentities.Select(ko.GridSlot));
        ko.Complete(Results(8, loser: 2));
        Assert.Equal(3, ko.EliminatedIdentity); // compact car 2 now means original racer 3
        Assert.DoesNotContain(3, ko.Entrants);
        Assert.DoesNotContain(2, ko.Entrants);
    }

    [Fact]
    public void ReverseGridUsesFinishingPlacesRatherThanCarIndices()
    {
        var ko = new KnockoutSeries();
        int[] places = [4, 2, 9, 7, 1, 8, 3, 6, 5];
        ko.Complete(new RaceResults(Enumerable.Range(0, 9).Select(i =>
            new RacerResult(places[i], i, "CAR", i == 0, false, 1000, [], null)).ToArray()));
        ko.NextRace();
        Assert.Equal([0, 1, 3, 4, 5, 6, 7, 8], ko.RaceIdentities);
        Assert.Equal([4, 6, 1, 7, 0, 5, 2, 3], ko.RaceIdentities.Select(ko.GridSlot));
    }

    [Fact]
    public void HumanLastEndsSeriesImmediately()
    {
        var ko = new KnockoutSeries(); ko.Complete(Results(9, loser: 0));
        Assert.Equal(0, ko.EliminatedIdentity); Assert.False(ko.CanContinue); Assert.False(ko.Won);
        Assert.Throws<InvalidOperationException>(() => ko.NextRace());
        Assert.False(ko.CloseFinal(new FeConfig(new byte[FeConfig.Size])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AnyDnfEndsSinglePlayerSeries(int dnf)
    {
        var ko = new KnockoutSeries(); ko.Complete(Results(9, dnf: dnf));
        Assert.True(ko.Aborted); Assert.Empty(ko.Entrants); Assert.False(ko.CanContinue); Assert.False(ko.Won);
    }

    [Fact]
    public void RepeatedResultsAndRestartDoNotEliminateTwice()
    {
        var ko = new KnockoutSeries();
        ko.Complete(Results(9, loser: 3)); ko.Complete(Results(9, loser: 5));
        Assert.Equal(8, ko.Entrants.Count); Assert.Equal(3, ko.EliminatedIdentity);
        ko.RestartStage(); Assert.Equal(9, ko.Entrants.Count); Assert.Equal(0, ko.Stage);
        ko.Complete(Results(9, loser: 5));
        Assert.Contains(3, ko.Entrants); Assert.DoesNotContain(5, ko.Entrants);
        Assert.Equal(8, ko.Entrants.Count);
    }

    [Fact]
    public void FullSeriesGrantsOnlyTrackBitOnce_WhenFinalIsClosed()
    {
        var config = new FeConfig(new byte[FeConfig.Size]); config[0x1C] = 0x80;
        var ko = new KnockoutSeries();
        for (int stage = 0; stage < 8; stage++)
        {
            ko.Complete(Results(9 - stage));
            if (stage < 7) { Assert.False(ko.CloseFinal(config)); ko.NextRace(); }
        }
        Assert.True(ko.Won); Assert.False(ko.CanContinue); Assert.Equal(0x80, config[0x1C]);
        Assert.True(ko.CloseFinal(config)); Assert.False(ko.CloseFinal(config));
        Assert.Equal(0x81, FeConfig.Parse(config.ToBytes())[0x1C]); Assert.Equal(0, config[0x1D]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void FinalRequiresFinishedHumanWinner(int humanState)
    {
        var ko = new KnockoutSeries();
        for (int i = 0; i < 7; i++) { ko.Complete(Results(9 - i)); ko.NextRace(); }
        var rows = Results(2).Racers.Select(r => r.CarIndex == 0 ? r with { RaceState = humanState } : r).ToArray();
        ko.Complete(new RaceResults(rows));
        Assert.False(ko.Won); Assert.False(ko.CloseFinal(new FeConfig(new byte[FeConfig.Size])));
    }

    [Fact]
    public void FinalLossGivesNoTrackOrCarReward()
    {
        var ko = new KnockoutSeries(); var config = new FeConfig(new byte[FeConfig.Size]);
        for (int i = 0; i < 7; i++) { ko.Complete(Results(9 - i)); ko.NextRace(); }
        ko.Complete(Results(2, loser: 0));
        Assert.False(ko.Won); Assert.False(ko.CanContinue); Assert.False(ko.CloseFinal(config));
        Assert.Equal(0, config[0x1C]); Assert.Equal(0, config[0x1D]);
    }

    [GameFact]
    public void MenuFullSeriesLoadsEightTracksAndShrinkingSessions_WithStableCarsAndGrid()
    {
        var m = Start();
        int[] tracks = [0, 2, 4, 5, 3, 6, 7, 8]; // next race increments menu track [ref:3150]
        var initial = m.Session!.Setup.Cars.ToArray();
        for (int stage = 0; stage < 8; stage++)
        {
            var session = m.Session!; int n = 9 - stage;
            Assert.Equal(tracks[stage], session.TrackNumber); Assert.Equal(n, session.Cars.Count);
            Assert.Equal(2, session.Setup.NumLaps); Assert.Equal(2, session.Setup.RaceType);
            Assert.Equal(1, session.Setup.Skill); Assert.Equal(n - 1, session.Setup.NumOpponentCars);
            Assert.DoesNotContain(session.Cars, c => c.Setup.CarClass == 4);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(initial[i], session.Setup.Car(i));
                Assert.Equal(stage == 0 ? i : n - 1 - i, session.Setup.GridSlot(i));
            }
            m.CompleteRace(Results(n)); Summary(m);
            Assert.Equal(stage == 7 ? "knockout-final" : "knockout-standings", m.CurrentScreen.Id);
            m.HandleKey(MenuKey.Enter);
        }
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Null(m.Knockout);
        Assert.Equal(0, m.Config.RaceType); Assert.Equal(7, m.Config.Track);
        Assert.Equal(1, m.Config[0x1C] & 1); Assert.Equal(0, m.Config[0x1D]);
        m.ReturnToMenu(); Assert.Equal(1, m.Config[0x1C] & 1);
        Open(m, "location"); Assert.Equal(8, m.CurrentScreen.Items.Single(i => i.Text == "track").ValueTexts!.Count);
    }

    [GameFact]
    public void ResultRestartRestoresEntrantsAndPauseRestartKeepsStage()
    {
        var m = Start(); var initial = m.Session!.Setup.Cars.ToArray();
        m.CompleteRace(Results(9, loser: 2)); Summary(m);
        m.HandleKey(MenuKey.Down); m.HandleKey(MenuKey.Enter); // custom summary restart
        Assert.Equal(9, m.Session!.Cars.Count); Assert.Equal(9, m.Knockout!.Entrants.Count);
        m.CompleteRace(Results(9, loser: 4)); Summary(m); m.HandleKey(MenuKey.Enter);
        var setup = m.Session!.Setup; Assert.Equal(1, m.Knockout.Stage);
        Assert.Equal([0, 1, 2, 3, 5, 6, 7, 8], m.Knockout.RaceIdentities);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(initial[m.Knockout.RaceIdentities[i]], setup.Car(i));
            Assert.Equal(7 - i, setup.GridSlot(i));
        }
        m.Flow.RequestPause(0x140); m.Flow.PauseMenu(NFS.Game.Flow.PauseChoice.Restart); m.RestartPausedRace();
        Assert.Equal(setup.Block, m.Session!.Setup.Block); Assert.Equal(8, m.Knockout.Entrants.Count);
        Assert.Contains(2, m.Knockout.Entrants); Assert.DoesNotContain(4, m.Knockout.Entrants);
    }

    [GameFact]
    public void RestartFromFirstResultsScreenRestoresTheSameRound()
    {
        var m = Start(); var setup = m.Session!.Setup;
        m.CompleteRace(Results(9, loser: 3)); Open(m, "restart");
        Assert.Equal(setup.Block, m.Session!.Setup.Block);
        Assert.Equal(9, m.Knockout!.Entrants.Count); Assert.False(m.Knockout.ResultsApplied);
        m.CompleteRace(Results(9, loser: 5)); Summary(m);
        Assert.Equal(8, m.Knockout.Entrants.Count); Assert.Contains(3, m.Knockout.Entrants);
        Assert.DoesNotContain(5, m.Knockout.Entrants);
    }

    [GameFact]
    public void ReturnToMenuAbandonsStateWithoutAward_AndNewSeriesStartsAtNine()
    {
        var m = Start(); m.CompleteRace(Results(9)); Summary(m);
        m.HandleKey(MenuKey.Escape);
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Null(m.Knockout);
        Assert.Equal(0, m.Config[0x1C]); Assert.Equal(0, m.Config.RaceType);
        m.Config.RaceType = 2; m.StartRace();
        Assert.Equal(9, m.Session!.Cars.Count); Assert.Equal(0, m.Knockout!.Stage);
    }

    [GameFact]
    public void HumanEliminationRoutesToEndInsteadOfNextRace()
    {
        var m = Start(); m.CompleteRace(Results(9, loser: 0)); Summary(m);
        Assert.Equal("FINISH", m.KnockoutActions[0]);
        m.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Equal(0, m.Config[0x1C]);
    }

    [GameFact]
    public void CaptureRetainsDnfDistinctFromEstimatedOpponentFinish()
    {
        var m = Start(); var runner = new RaceRunner(m.Session!, withAi: true);
        runner.Cars[0].Obj.RaceState = 1;
        runner.Cars[1].Obj.RaceState = 2;
        var result = RaceResults.Capture(runner);
        Assert.Equal(1, result.Racers.Single(r => r.CarIndex == 0).RaceState);
        Assert.Equal(2, result.Racers.Single(r => r.CarIndex == 1).RaceState);
        Assert.True(result.Racers.Single(r => r.CarIndex == 1).Estimated);
    }

}
