using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Series with two humans (split screen): knockout with a mask of the active humans, as in statchk case 1 [ref:3241]
/// and tournament without a mask. One player stays as before (KnockoutTests, TournamentTests). [ref:3242]</summary>
public class SplitSeriesTests
{
    /// <summary>Race results for n cars (the humans are the first <paramref name="humans"/> cars); <paramref name="order"/> = car indices from the winner.</summary>
    static RaceResults Results(int n, int humans, int[]? order = null, int dnf = -1)
    {
        order ??= Enumerable.Range(0, n).ToArray();
        return new RaceResults(order.Select((car, p) => new RacerResult(p + 1, car, $"CAR{car}", car < humans,
            car >= humans, 1000 + 100 * p, [], null, car == dnf ? 1 : 2)).ToArray());
    }
    /// <summary>Results in which the last place is taken by car <paramref name="loser"/>.</summary>
    static RaceResults LastIs(int n, int humans, int loser, int dnf = -1)
        => Results(n, humans, Enumerable.Range(0, n).Where(i => i != loser).Append(loser).ToArray(), dnf);

    // ---- knockout: mask of active humans ----

    [Fact]
    public void BothHumansRaceTheFirstRound_WithNineEntrants()
    {
        var ko = new KnockoutSeries(2);
        Assert.Equal(2, ko.Humans);
        Assert.True(ko.HumanActive(0)); Assert.True(ko.HumanActive(1));
        Assert.Equal(Enumerable.Range(0, 9), ko.RaceIdentities);
        Assert.Equal(Enumerable.Range(0, 9), ko.RaceIdentities.Select(ko.GridSlot));
        Assert.False(new KnockoutSeries().HumanActive(1));   // one player: only human 0
    }

    [Fact]
    public void OneHumanEliminated_SeriesContinuesForTheOther_AndRacesAsCompactIdentities()
    {
        var ko = new KnockoutSeries(2);
        ko.Complete(LastIs(9, 2, loser: 0));
        Assert.Equal(0, ko.EliminatedIdentity);
        Assert.False(ko.HumanActive(0)); Assert.True(ko.HumanActive(1));
        Assert.True(ko.CanContinue);
        ko.NextRace();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], ko.RaceIdentities);   // human 1 is now car 0 of the race
        // statchk case 1: compact index → identity = the n-th active mask entry [ref:3243]
        ko.Complete(LastIs(8, 1, loser: 3));
        Assert.Equal(4, ko.EliminatedIdentity);
        Assert.True(ko.HumanActive(1));
    }

    [Fact]
    public void SecondHumanLast_LeavesTheFirstHumanRacing()
    {
        var ko = new KnockoutSeries(2);
        ko.Complete(LastIs(9, 2, loser: 1));
        Assert.True(ko.HumanActive(0)); Assert.False(ko.HumanActive(1)); Assert.True(ko.CanContinue);
        ko.NextRace();
        Assert.Equal([0, 2, 3, 4, 5, 6, 7, 8], ko.RaceIdentities);
        ko.Complete(LastIs(8, 1, loser: 0));   // human 0 (car 0) last → nobody is driving any more
        Assert.False(ko.AnyHumanActive); Assert.False(ko.CanContinue);
        Assert.Throws<InvalidOperationException>(() => ko.NextRace());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void AnyDnfEndsTheSplitSeries(int dnf)
    {
        var ko = new KnockoutSeries(2);
        ko.Complete(LastIs(9, 2, loser: 8, dnf: dnf));
        Assert.True(ko.Aborted); Assert.False(ko.AnyHumanActive); Assert.False(ko.CanContinue);
    }

    [Fact]
    public void RestartRestoresTheEliminatedHuman()
    {
        var ko = new KnockoutSeries(2);
        ko.Complete(LastIs(9, 2, loser: 0));
        ko.RestartStage();
        Assert.True(ko.HumanActive(0)); Assert.Equal(9, ko.Entrants.Count);
        ko.Complete(LastIs(9, 2, loser: 4));
        Assert.True(ko.HumanActive(0)); Assert.Equal(4, ko.EliminatedIdentity);
    }

    [Fact]
    public void SplitFinal_IsWonByTheFirstCarOfTheRace_AndGrantsTheTrackOnce()
    {
        static KnockoutSeries ToFinal()
        {
            var k = new KnockoutSeries(2);
            for (int stage = 0; stage < 7; stage++) { int n = 9 - stage; k.Complete(LastIs(n, 2, loser: n - 1)); k.NextRace(); }
            return k;
        }
        var ko = ToFinal();
        Assert.Equal([0, 1], ko.RaceIdentities);
        ko.Complete(Results(2, 2, [1, 0]));   // car 1 wins; car 0 (decides the prize) is second [ref:3244]
        Assert.False(ko.Won);

        ko = ToFinal();
        ko.Complete(Results(2, 2, [0, 1]));
        Assert.True(ko.Won);
        var cfg = new FeConfig(new byte[FeConfig.Size]);
        Assert.True(ko.CloseFinal(cfg)); Assert.False(ko.CloseFinal(cfg)); Assert.Equal(1, cfg[0x1C]);
    }

    // ---- tournament: both humans drive every round ----

    [Fact]
    public void Tournament_PointsGoToBothHumans_AndRejectsOneHumanResults()
    {
        var series = new TournamentSeries(0, humans: 2);
        Assert.Equal(2, series.Humans);
        Assert.Throws<ArgumentException>(() => series.Complete(Results(8, 1)));   // one human in a two-human series
        series.Complete(Results(8, 2, [3, 0, 1, 2, 4, 5, 6, 7]));
        Assert.Equal([25, 20, 16, 30, 12, 8, 4, 0], series.Points);
        Assert.Equal(Enumerable.Range(0, 8), Enumerable.Range(0, 8).Select(series.GridSlot).Order());
        Assert.Equal(1, series.GridSlot(0)); Assert.Equal(0, series.GridSlot(3));
    }

    [Fact]
    public void Tournament_DnfHumanGetsNoPoints_AndOtherHumanKeepsHis()
    {
        var series = new TournamentSeries(0, humans: 2);
        var rows = Results(8, 2).Racers.Select(r => r.CarIndex == 1 ? r with { RaceState = 1, Estimated = true } : r).ToArray();
        series.Complete(new RaceResults(rows));
        Assert.Equal(0, series.Points[1]);
        Assert.True(series.Points[0] > 0);
        Assert.Equal(1, series.CompletedTracks);
    }

    [Fact]
    public void Tournament_ATieOfTwoHumansPutsTheBetterRaceRankHigher()
    {
        // with equal points a human ranks above the AI; with two humans, the one with the better place in the last race ranks higher [ref:3245]
        var series = new TournamentSeries(0, humans: 2);
        series.Complete(Results(8, 2, [0, 1, 2, 3, 4, 5, 6, 7]));   // 0: 30, 1: 25, AI 2: 20
        series.NextRace(1);
        series.Complete(Results(8, 2, [2, 1, 0, 3, 4, 5, 6, 7]));   // 1: +25 = 50, 0: +20 = 50, AI 2: +30 = 50
        Assert.Equal(series.Points[0], series.Points[1]);
        Assert.Equal(series.Points[0], series.Points[2]);
        var standings = series.Standings.ToList();
        Assert.True(standings.IndexOf(1) < standings.IndexOf(0));   // human 1 was second, human 0 third in the last race
        Assert.True(standings.IndexOf(0) < standings.IndexOf(2));   // the AI loses the tie to the human
    }

    [Fact]
    public void OneHumanTournament_KeepsTheOriginalTieRule()
    {
        var series = new TournamentSeries(0);
        series.Complete(Results(8, 1, [1, 0, 2, 3, 4, 5, 6, 7]));   // human 0: 25, AI 1: 30
        series.NextRace(1);
        series.Complete(Results(8, 1, [0, 1, 2, 3, 4, 5, 6, 7]));   // human 0: +30 = 55, AI 1: +25 = 55
        var standings = series.Standings.ToList();
        Assert.True(standings.IndexOf(0) < standings.IndexOf(1));
    }

    [Fact]
    public void RestoredTournament_KeepsTheHumanCountOfTheSavedConfig()
    {
        var f = new TouFile { TrackCount = 1, Mask = 1 };
        Assert.Equal(2, TournamentSeries.Restore(f, 1, humans: 2).Humans);
        Assert.Equal(1, TournamentSeries.Restore(f, 1).Humans);
    }

    // ---- menu and builder (game data) ----

    static MenuController SplitMenu(int raceType, Action<FeConfig>? edit = null)
    {
        var m = MenuController.Load(GameData.Dir);
        m.Config.GameType = 1; m.Config.RaceType = (byte)raceType;
        edit?.Invoke(m.Config);
        return m;
    }

    /// <summary>Results → lap screen → series summary.</summary>
    static void ToSummary(MenuController m) { m.HandleKey(MenuKey.Enter); m.HandleKey(MenuKey.Enter); }

    [GameFact]
    public void SplitKnockout_StartsWithTwoHumansAndSevenOpponents()
    {
        var m = SplitMenu(2);
        m.StartRace();
        Assert.Equal(MenuStatus.Race, m.Status);
        Assert.Equal(2, m.Knockout!.Humans);
        var setup = m.Session!.Setup;
        Assert.Equal(9, m.Session.Cars.Count); Assert.Equal(2, setup.NumPlayerCars); Assert.Equal(7, setup.NumOpponentCars);
        Assert.Equal(1, setup.CommMode);
        Assert.Equal(1, setup.Car(0).CarClass); Assert.Equal(1, setup.Car(1).CarClass); Assert.Equal(2, setup.Car(2).CarClass);
        Assert.Null(m.SoleSplitHuman);
    }

    [GameFact]
    public void SplitTournament_StartsWithTwoHumansAndSixOpponents()
    {
        var m = SplitMenu(1);
        m.StartRace();
        Assert.Equal(MenuStatus.Race, m.Status);
        Assert.Equal(2, m.Tournament!.Humans);
        var setup = m.Session!.Setup;
        Assert.Equal(8, m.Session.Cars.Count); Assert.Equal(2, setup.NumPlayerCars); Assert.Equal(6, setup.NumOpponentCars);
        Assert.Equal(1, setup.CommMode);
    }

    [GameFact]
    public void HumanZeroEliminated_NextRoundIsAOneViewRaceDrivenWithPlayerTwoKeys()
    {
        var m = SplitMenu(2);
        KeyBindings.ApplySplitDefaults(m.Config, KeyBindings.SplitBuiltin);
        m.StartRace();
        m.CompleteRace(LastIs(9, 2, loser: 0));
        ToSummary(m);
        Assert.True(m.KnockoutSummary); Assert.True(m.Knockout!.CanContinue);
        Assert.Equal("NEXT RACE", m.KnockoutActions[0]);
        m.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuStatus.Race, m.Status);
        var setup = m.Session!.Setup;
        Assert.Equal(8, m.Session.Cars.Count); Assert.Equal(1, setup.NumPlayerCars); Assert.Equal(7, setup.NumOpponentCars);
        Assert.Equal(0, setup.CommMode);
        Assert.Equal(1, setup.Car(0).CarClass); Assert.Equal(2, setup.Car(1).CarClass);
        Assert.Equal(1, m.SoleSplitHuman);
        // reverse grid of the survivors by last place (compact count 8, slots 0..7): car 0 = identity 1 won, so it starts last
        Assert.Equal(Enumerable.Range(0, 8), Enumerable.Range(0, 8).Select(setup.GridSlot).Order());
        Assert.Equal(7, setup.GridSlot(0));
        // player 2's keys in player 1's tags (index = 1), without player 2's tags [ref:3246] [ref:3492]
        Assert.Equal(m.Config.SplitKey(1, 0), setup.KeyBindings[0x23 - 0x23]);
        Assert.Equal(m.Config.SplitKey(1, 1), setup.KeyBindings[0x24 - 0x23]);
        Assert.Equal(0, setup.KeyBindings[0x2F - 0x23]);
    }

    [GameFact]
    public void HumanOneEliminated_PlayerOneKeepsHisKeysAndTheRaceIsOneView()
    {
        var m = SplitMenu(2);
        KeyBindings.ApplySplitDefaults(m.Config, KeyBindings.SplitBuiltin);
        m.StartRace();
        m.CompleteRace(LastIs(9, 2, loser: 1));
        ToSummary(m);
        m.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuStatus.Race, m.Status);
        var setup = m.Session!.Setup;
        Assert.Equal(0, setup.CommMode); Assert.Equal(1, setup.NumPlayerCars);
        Assert.Equal(0, m.SoleSplitHuman);
        Assert.Equal(m.Config.SplitKey(0, 0), setup.KeyBindings[0x23 - 0x23]);
    }

    [GameFact]
    public void SplitKnockout_BothHumansOut_ReturnsToTheMenu()
    {
        var m = SplitMenu(2);
        m.StartRace();
        m.CompleteRace(LastIs(9, 2, loser: 0)); ToSummary(m); m.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuStatus.Race, m.Status);
        m.CompleteRace(LastIs(8, 1, loser: 0)); ToSummary(m);
        Assert.Equal("FINISH", m.KnockoutActions[0]);
        m.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Null(m.Knockout);
    }

    [GameFact]
    public void SplitTournament_GridAndPointsCarryOverToTheNextRound()
    {
        var m = SplitMenu(1);
        m.StartRace();
        m.CompleteRace(Results(8, 2, [4, 1, 0, 2, 3, 5, 6, 7]));
        var series = m.Tournament!;
        Assert.Equal(30, series.Points[4]); Assert.Equal(25, series.Points[1]); Assert.Equal(20, series.Points[0]);
        ToSummary(m);
        Assert.True(m.TournamentSummary);
        m.HandleKey(MenuKey.Enter);   // NEXT RACE
        Assert.Equal(MenuStatus.Race, m.Status);
        Assert.Equal(2, m.Session!.Setup.NumPlayerCars); Assert.Equal(1, m.Session.Setup.CommMode);
        for (int i = 0; i < 8; i++) Assert.Equal(series.GridSlot(i), m.Session.Setup.GridSlot(i));
    }

    [GameFact]
    public void SwitchingTheGameTypeAbandonsATournamentInProgress()
    {
        var m = SplitMenu(1);
        m.StartRace();
        Assert.Equal(2, m.Tournament!.Humans);
        m.CompleteRace(Results(8, 2)); ToSummary(m); m.ReturnToMenu();
        Assert.Null(m.Tournament);
        m.Config.GameType = 0; m.Config.RaceType = 1; m.StartRace();
        Assert.Equal(1, m.Tournament!.Humans); Assert.Equal(1, m.Session!.Setup.NumPlayerCars);
    }

    [GameFact]
    public void SeriesOpponentsFollowTheClassOfEitherHuman_WithTheIndexShiftedByOne()
    {
        // P1 class C (0), P2 class B (6) → table B, index i + 1
        foreach (int raceType in new[] { 1, 2 })
        {
            var m = SplitMenu(raceType, c => { c.P1Car = 0; c.P2Car = 6; });
            m.StartRace();
            var table = raceType == 2 ? NFS.Core.Constants.FrontEndConstants.KnockoutOpponentsB : NFS.Core.Constants.FrontEndConstants.TournamentOpponentsB;
            for (int i = 0; i < 6; i++)
                Assert.Equal(table[i + 1], m.Session!.Setup.Car(2 + i).CarType);
        }
        // P1 class B, P2 class A → A wins
        var a = SplitMenu(2, c => { c.P1Car = 6; c.P2Car = 2; });
        a.StartRace();
        Assert.Equal(NFS.Core.Constants.FrontEndConstants.KnockoutOpponentsA[1], a.Session!.Setup.Car(2).CarType);
        // one player: no shift (regression)
        var single = MenuController.Load(GameData.Dir); single.Config.RaceType = 2; single.Config.P1Car = 2; single.StartRace();
        Assert.Equal(NFS.Core.Constants.FrontEndConstants.KnockoutOpponentsA[0], single.Session!.Setup.Car(1).CarType);
    }
}
