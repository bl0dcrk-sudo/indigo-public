using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class TournamentTests
{
    static RaceResults Results(int humanPlace = 1, int humanState = 2)
    {
        var order = Enumerable.Range(1, 7).ToList(); order.Insert(humanPlace - 1, 0);
        return new RaceResults(order.Select((id, p) => new RacerResult(p + 1, id, $"CAR{id}", id == 0,
            id != 0, 1000 + p * 100, [], null, id == 0 ? humanState : 2)).ToArray());
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
    static MenuController Start(int track = 0)
    {
        var m = MenuController.Load(GameData.Dir);
        Open(m, "game setup"); Select(m, "race type"); m.HandleKey(MenuKey.Right); Open(m, "DONE");
        Open(m, "location"); Select(m, "track");
        for (int i = 0; i < track; i++) m.HandleKey(MenuKey.Right);
        Open(m, "DONE");
        Open(m, "RACE"); return m;
    }
    static void Summary(MenuController m) { Open(m, "continue"); Open(m, "continue"); Assert.True(m.TournamentSummary); }

    [Fact]
    public void PointsFollowSignedTimesRatherThanLiveRanks_AndAccumulateByIdentity()
    {
        var series = new TournamentSeries();
        var rows = Results().Racers.Select(r => r with { Place = 8 - r.CarIndex }).Reverse().ToArray();
        series.Complete(new RaceResults(rows));
        Assert.Equal([30, 25, 20, 16, 12, 8, 4, 0], series.Points);
        series.NextRace(1); series.Complete(Results(humanPlace: 8));
        Assert.Equal([30, 55, 45, 36, 28, 20, 12, 4], series.Points);
        Assert.Equal([1, 2, 3, 0, 4, 5, 6, 7], series.Standings);
        Assert.Equal(7, series.GridSlot(0)); Assert.Equal(0, series.GridSlot(1));
    }

    [Fact]
    public void NextGridUsesZeroBasedSlotsLikeTheTouLoad()
    {
        var series = new TournamentSeries();
        series.Complete(Results(humanPlace: 8));
        var slots = Enumerable.Range(0, 8).Select(series.GridSlot).ToArray();
        Assert.Equal(7, slots[0]);
        Assert.Equal(Enumerable.Range(0, 8), slots.Order());
    }

    [Fact]
    public void TracksAreChosenFromUncompletedPool_WithOriginalBitMapping()
    {
        var series = new TournamentSeries(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => series.SelectTrack(7));
        int[] tracks = [2, 6, 0, 4, 1, 5, 3];
        int[] masks = [0x10, 0x50, 0x51, 0x59, 0x5B, 0x7B, 0x7F];
        for (int stage = 0; stage < 7; stage++)
        {
            Assert.Equal(tracks[stage], series.Track); series.Complete(Results());
            Assert.Equal(masks[stage], series.CompletedMask); Assert.Equal(stage + 1, series.CompletedTracks);
            Assert.DoesNotContain(tracks[stage], series.AvailableTracks);
            Assert.Throws<ArgumentOutOfRangeException>(() => series.SelectTrack(tracks[stage]));
            if (stage < 6) series.NextRace(tracks[stage + 1]);
        }
        Assert.True(series.Finished); Assert.Empty(series.AvailableTracks);
        Assert.Throws<InvalidOperationException>(() => series.NextRace(0));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 1000)]
    [InlineData(1, -1000)]
    public void HumanDnfGetsNoPoints_AiEstimatedTimesStillScore(int state, int ticks)
    {
        var series = new TournamentSeries();
        var rows = Results(humanState: state).Racers.Select(r => r.CarIndex == 0 ? r with { RaceTicks = ticks } : r with { RaceState = 0 }).ToArray();
        series.Complete(new RaceResults(rows));
        Assert.Equal([0, 30, 25, 20, 16, 12, 8, 4], series.Points);
        Assert.Equal(1, series.CompletedMask); Assert.False(series.Finished);
    }

    [Fact]
    public void SignedTimeAndTiesUseStableIdentityOrder()
    {
        var series = new TournamentSeries();
        var rows = Results(humanPlace: 8).Racers.Select(r => r with { RaceTicks = -500 }).ToArray();
        series.Complete(new RaceResults(rows));
        Assert.Equal([30, 25, 20, 16, 12, 8, 4, 0], series.Points);
    }

    [Fact]
    public void EqualTotalsPreferHumanOverAi_AndAiReverseIdentity()
    {
        var series = new TournamentSeries();
        // First two competitors exchange first/second; live human rank is deliberately worse.
        series.Complete(Results()); series.NextRace(1); series.Complete(Results(humanPlace: 2));
        Assert.Equal(55, series.Points[0]); Assert.Equal(55, series.Points[1]); Assert.Equal(0, series.Standings[0]);
        var empty = new TournamentSeries();
        Assert.Equal([7, 0, 6, 5, 4, 3, 2, 1], empty.Standings);
    }

    [Fact]
    public void RepeatedResultsAndRestartRestoreScoresMaskAndGrid()
    {
        var series = new TournamentSeries();
        series.Complete(Results()); series.NextRace(3);
        var before = series.Points.ToArray();
        series.Complete(Results(humanPlace: 8)); series.Complete(Results());
        Assert.Equal(30, series.Points[0]); Assert.Equal(2, series.CompletedTracks);
        series.RestartStage();
        Assert.Equal(before, series.Points); Assert.Equal(1, series.CompletedMask); Assert.Equal(3, series.Track);
        Assert.Equal(0, series.GridSlot(0)); Assert.False(series.ResultsApplied);
        series.Complete(Results()); Assert.Equal(60, series.Points[0]); Assert.Equal(5, series.CompletedMask);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    [InlineData(8, 0)]
    public void PodiumAwardsEachBonusLevelOnlyOnce_WhenFinalCloses(int place, int expected)
    {
        var series = new TournamentSeries(); var config = new FeConfig(new byte[FeConfig.Size]);
        config[0x1C] = 0x81;
        for (int stage = 0; stage < 7; stage++)
        {
            series.Complete(Results(place));
            Assert.Equal(0, config[0x1D]);
            if (stage < 6) { Assert.False(series.CloseFinal(config)); series.NextRace(stage + 1); }
        }
        Assert.Equal(expected != 0, series.CloseFinal(config));
        Assert.Equal(expected, config[0x1D]); Assert.False(series.CloseFinal(config));
        Assert.Equal(0x81, config[0x1C]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FinalDnfBlocksRewardDespiteOverallWin(int finalState)
    {
        var series = new TournamentSeries(); var config = new FeConfig(new byte[FeConfig.Size]);
        for (int i = 0; i < 6; i++) { series.Complete(Results()); series.NextRace(i + 1); }
        series.Complete(Results(humanState: finalState));
        Assert.Equal(0, series.Standings[0]); Assert.False(series.CloseFinal(config)); Assert.Equal(0, config[0x1D]);
    }

    [Fact]
    public void LesserPodiumPreservesExistingUnlockLevel()
    {
        var series = new TournamentSeries(); var config = new FeConfig(new byte[FeConfig.Size]); config[0x1D] = 3;
        for (int i = 0; i < 7; i++) { series.Complete(Results(3)); if (i < 6) series.NextRace(i + 1); }
        Assert.False(series.CloseFinal(config)); Assert.Equal(3, config[0x1D]);
    }

    [GameFact]
    public void MenuSummaryChoosesOnlyRemainingTracks_AndRestartRollsBackStage()
    {
        var m = Start(2); var initial = m.Session!.Setup;
        m.CompleteRace(Results()); Summary(m);
        for (int i = 0; i < 14; i++)
        {
            Assert.Contains(m.NextTournamentTrack, m.Tournament!.AvailableTracks);
            Assert.NotEqual(2, m.NextTournamentTrack); m.HandleKey(MenuKey.Right);
        }
        m.HandleKey(MenuKey.Down); m.HandleKey(MenuKey.Enter);
        Assert.False(m.Tournament!.ResultsApplied); Assert.Equal(0, m.Tournament.CompletedMask);
        Assert.Equal(initial.Block, m.Session!.Setup.Block);
        m.CompleteRace(Results()); Summary(m);
        int next = m.NextTournamentTrack; m.HandleKey(MenuKey.Enter);
        Assert.Equal(next, m.Tournament.Track); Assert.Equal(8, m.Session!.Cars.Count);
        Assert.Equal(Enumerable.Range(0, 8), Enumerable.Range(0, 8).Select(m.Session.Setup.GridSlot));
    }

    [GameFact]
    public void ReturnToMenuAbandonsSeries_AndNextTournamentStartsClean()
    {
        var m = Start(); m.CompleteRace(Results()); Summary(m); m.HandleKey(MenuKey.Escape);
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Null(m.Tournament); Assert.Equal(0, m.Config.RaceType);
        Assert.Equal(0, m.Config[0x1D]); m.Config.RaceType = 1; m.StartRace();
        Assert.Equal(0, m.Tournament!.CompletedTracks); Assert.All(m.Tournament.Points, p => Assert.Equal(0, p));
    }

    [GameFact]
    public void MenuFinalGrantsCarsInCurrentConfig_AndSeriesEnds()
    {
        var m = Start();
        for (int i = 0; i < 7; i++)
        {
            m.CompleteRace(Results()); Summary(m);
            Assert.Equal(i == 6 ? "tournament-final" : "tournament-standings", m.CurrentScreen.Id);
            Assert.Equal(0, m.Config[0x1D]); m.HandleKey(MenuKey.Enter);
        }
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Null(m.Tournament); Assert.Equal(3, m.Config[0x1D]);
        Open(m, "player one");
        Assert.Equal(15, m.CurrentScreen.Items.Single(i => i.ConfigOffset == 0x1F).ValueTexts!.Count);
    }

    [GameFact]
    public void MenuTournamentStartsSelectedPoolTrack_EightCarsFourLapsStockTuning()
    {
        var m = Start(4); var session = m.Session!;
        Assert.Equal(MenuStatus.Race, m.Status); Assert.NotNull(m.Tournament);
        Assert.Equal(3, session.TrackNumber); Assert.Equal(8, session.Cars.Count);
        Assert.Equal(4, session.Setup.NumLaps); Assert.Equal(1, session.Setup.RaceType); Assert.Equal(1, session.Setup.Skill);
        Assert.Equal(7, session.Setup.NumOpponentCars); Assert.DoesNotContain(session.Setup.Cars, c => c.CarClass == 4);
        Assert.Equal(Enumerable.Range(0, 8), Enumerable.Range(0, 8).Select(session.Setup.GridSlot));
        int rotation = m.Config.TournamentOrder;
        Assert.InRange(rotation, 0, 10);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (i + rotation) % 11), session.Setup.Cars.Skip(1).Select(c => c.Personality));
        for (int i = 0; i < 7; i++) Assert.Equal(NFS.Core.Constants.FrontEndConstants.TournamentOpponentsC[i], session.Setup.Car(i + 1).CarType);
    }

    [GameFact]
    public void SeriesUsesOriginalOpponentClassTables_AndWrapsPersonalityModuloEleven()
    {
        var builder = new FeRaceArgsBuilder();
        foreach (int player in new[] { 0, 6, 2 })
        {
            var table = player switch { 0 => NFS.Core.Constants.FrontEndConstants.TournamentOpponentsC, 6 => NFS.Core.Constants.FrontEndConstants.TournamentOpponentsB, _ => NFS.Core.Constants.FrontEndConstants.TournamentOpponentsA };
            var config = new FeConfig(new byte[FeConfig.Size]);
            config.RaceType = 1; config.Style = 1; config.P1Car = (byte)player; config[4] = 9;
            var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), builder.Build(config, tournament: new TournamentSeries()));
            Assert.Equal([9, 10, 0, 1, 2, 3, 4], setup.Cars.Skip(1).Select(c => c.Personality));
            for (int i = 0; i < 7; i++) Assert.Equal(table[i], setup.Car(i + 1).CarType);
        }
    }
}
