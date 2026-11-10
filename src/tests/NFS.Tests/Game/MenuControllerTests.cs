using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class MenuControllerTests
{
    private static MenuController Menu() => MenuController.Load(GameData.Dir);
    private static void Keys(MenuController menu, params MenuKey[] keys)
    { foreach (var key in keys) menu.HandleKey(key); }
    private static void Select(MenuController menu, string text)
    {
        for (int i = 0; i < menu.CurrentScreen.Items.Count; i++)
        {
            if (menu.CurrentScreen.Items[menu.SelectedIndex].Text == text) return;
            menu.HandleKey(MenuKey.Down);
        }
        throw new Exception($"Cannot select {text} on {menu.CurrentScreen.Id}");
    }
    private static void Open(MenuController menu, string text)
    { Select(menu, text); menu.HandleKey(MenuKey.Enter); }
    private static void Done(MenuController menu) => Open(menu, "DONE");

    [GameFact]
    public void Defaults_MatchOriginalHandler()
    {
        var m = Menu(); var setup = m.BuildSetup();
        Assert.Equal("main", m.CurrentScreen.Id);
        Assert.Equal(0, m.Config.Track);
        Assert.Equal(1, m.Config.Style); // wild [ref:3152]
        Assert.Equal(4, m.Config.Laps); // order 4,8,2 [ref:3153]
        Assert.Equal(1, m.Config.P1Transmission);
        Assert.Equal(12, m.Config.OpponentCar);
        Assert.Equal(2, m.Config.OpponentCount);
        Assert.Equal(0, m.Config.CatchUp); // single player [ref:3154]
        Assert.Equal(8, setup.NumCars);
        Assert.Equal(4, setup.NumLaps);
        Assert.Equal(0, setup.Car(0).CarType);
    }

    [GameFact]
    public void NetworkSetupFreezesFullMenuConfigurationWithoutChangingTheMenu()
    {
        var m = Menu();
        m.Config.Style = 0;
        m.Config.Backward = 1;
        m.Config.Mirrored = 1;
        m.Config.P1Car = 2;
        m.Config.P1Transmission = 1;
        m.Config.Raw[0x21] = 5;
        m.Config.Track = 6;
        m.Config.Laps = 3;
        var expected = m.BuildSetup();
        var before = m.Config.ToBytes();
        var setup = m.BuildNetworkSetup("Alice");
        Assert.Equal(2, setup.NumPlayerCars);
        Assert.Equal(0, setup.RaceType);
        Assert.Equal(0, setup.Style);
        Assert.True(setup.Backward);
        Assert.True(setup.Mirrored);
        Assert.Equal(expected.Track, setup.Track);
        Assert.Equal(expected.NumLaps, setup.NumLaps);
        Assert.Equal(expected.Car(0).CarType, setup.Car(0).CarType);
        Assert.Equal(expected.Car(0).Transmission, setup.Car(0).Transmission);
        Assert.Equal(expected.Car(0).Colour, setup.Car(0).Colour);
        Assert.Equal("Alice", NFS.Game.Race.RaceLoader.CarName(setup, 0));
        var shortNameSetup = m.BuildNetworkSetup("Al");
        Assert.Equal("Al", NFS.Game.Race.RaceLoader.CarName(shortNameSetup, 0));
        Assert.All(shortNameSetup.Block.AsSpan(0x78 + 0x34 + 2, 10).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(before, m.Config.ToBytes());
    }

    [GameFact]
    public void Arrows_WrapAndSkipPlayerTwo()
    {
        var m = Menu();
        Keys(m, MenuKey.Up);
        Assert.Equal("exit", m.CurrentScreen.Items[m.SelectedIndex].Text);
        Keys(m, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down);
        Assert.Equal("opponents", m.CurrentScreen.Items[m.SelectedIndex].Text);
        Assert.True((m.CurrentScreen.Items.Single(i => i.Text == "player two").Flags & 1) != 0);
    }

    [GameFact]
    public void LocationOptions_WrapAndCompile()
    {
        var m = Menu(); Open(m, "location");
        Keys(m, MenuKey.Left);
        Assert.Equal(6, m.Config.Track); // seven unlocked tracks from the original's table
        Keys(m, MenuKey.Right, MenuKey.Right, MenuKey.Down, MenuKey.Left);
        Assert.Equal(1, m.Config.Track);
        Assert.Equal(2, m.Config.Laps);
        Done(m);
        Assert.Equal(2, m.BuildSetup().Track);
        Assert.Equal(2, m.BuildSetup().NumLaps);
    }

    [GameFact]
    public void EscapeAndCancel_RestoreButDoneKeeps()
    {
        var m = Menu(); Open(m, "location"); Keys(m, MenuKey.Right, MenuKey.Escape);
        Assert.Equal(0, m.Config.Track);
        Open(m, "location"); Keys(m, MenuKey.Right); Open(m, "CANCEL");
        Assert.Equal(0, m.Config.Track);
        Open(m, "location"); Keys(m, MenuKey.Right); Done(m);
        Assert.Equal(1, m.Config.Track);
    }

    [GameFact]
    public void CarTransmissionDirectionAndDifficulty_CompileFromKeys()
    {
        var m = Menu(); Open(m, "game setup"); Select(m, "backwards"); Keys(m, MenuKey.Right);
        Select(m, "mirrored"); Keys(m, MenuKey.Right); Done(m);
        Open(m, "player one"); Keys(m, MenuKey.Right, MenuKey.Down, MenuKey.Left); Done(m);
        Open(m, "opponents"); Select(m, "skill level"); Keys(m, MenuKey.Right); Done(m);
        var setup = m.BuildSetup();
        Assert.True(setup.Backward); Assert.True(setup.Mirrored);
        Assert.Equal(1, setup.Car(0).CarType);
        Assert.Equal(0, m.Config.P1Transmission);
        Assert.Equal(1, setup.Skill);
    }

    [GameFact]
    public void Color_WrapsWithinOriginalCarRange()
    {
        var m = Menu(); Open(m, "player one"); Select(m, "color");
        byte initial = m.Config.P1Colour(0);
        Keys(m, MenuKey.Left, MenuKey.Right);
        Assert.Equal(initial, m.Config.P1Colour(0));
        Done(m); Assert.Equal(initial, m.BuildSetup().Car(0).Colour);
    }

    [GameFact]
    public void NumberNoneOneFull_ControlsOpponentAndTraffic()
    {
        var m = Menu(); Open(m, "location"); Keys(m, MenuKey.Right); Done(m);
        Open(m, "opponents"); Select(m, "number"); Keys(m, MenuKey.Left);
        Assert.Equal(1, m.Config.OpponentCount);
        Assert.Equal(0, m.Config.OpponentCar);
        Assert.Equal(1, m.Config.TrafficOff);
        Keys(m, MenuKey.Left);
        Assert.Equal(0, m.Config.OpponentCount); Assert.Equal(15, m.Config.OpponentCar);
        Assert.True((m.CurrentScreen.Items.Single(i => i.Text == "skill level").Flags & 1) != 0);
        Keys(m, MenuKey.Left);
        Assert.Equal(2, m.Config.OpponentCount); Assert.Equal(0, m.Config.OpponentCar);
    }

    [GameFact]
    public void SimStyleStart_BuildsSimulationRace()
    {
        var m = Menu(); Open(m, "game setup"); Select(m, "style"); Keys(m, MenuKey.Left); Done(m);
        Assert.Equal(0, m.Config.Style);
        Open(m, "RACE");
        Assert.Equal(MenuStatus.Race, m.Status);
        Assert.Equal(0, m.Session!.Setup.Style);   // 0 [ref:3155]
    }

    [GameFact]
    public void ReplayPicker_CurrentSavedCancelAndMissingFilesAreSafe()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nfs2se-replay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            byte[] valid = GameData.ReadRaw("REPLAY.RPY");
            File.WriteAllBytes(Path.Combine(dir, "saved.rpy"), valid);
            MenuController OpenPicker()
            {
                var menu = Menu(); menu.SavesDirectory = dir;
                menu.StartRace();
                var latestRecorder = new ReplayRecorder(menu.Session!.Setup);
                latestRecorder.Record(default);
                menu.CompleteRace(RaceResults.Capture(new RaceRunner(menu.Session, withAi: true)), latestRecorder.Build(2));
                menu.ReturnToMenu(); // current replay must remain available from the profile menu
                Assert.True(menu.HasCurrentReplay);
                // Follow the front-end's actual screen targets (the initial selection can skip disabled rows).
                for (int i = 0; menu.CurrentScreen.Items[menu.SelectedIndex].Target != "options" && i < 12; i++) menu.HandleKey(MenuKey.Down);
                Assert.Equal("options", menu.CurrentScreen.Items[menu.SelectedIndex].Target);
                menu.HandleKey(MenuKey.Enter);
                for (int i = 0; menu.CurrentScreen.Items[menu.SelectedIndex].Target != "load-save" && i < 12; i++) menu.HandleKey(MenuKey.Down);
                Assert.Equal("load-save", menu.CurrentScreen.Items[menu.SelectedIndex].Target);
                menu.HandleKey(MenuKey.Enter);
                for (int i = 0; menu.SelectedIndex != 4 && i < 12; i++) menu.HandleKey(MenuKey.Down);
                Assert.Equal(4, menu.SelectedIndex); // view replay; row 0 is disabled without a series
                menu.HandleKey(MenuKey.Enter);
                Assert.Equal("load-save", menu.CurrentScreen.Id);
                Assert.Equal(DialogKind.List, menu.Dialog!.Kind);
                return menu;
            }

            var cancel = OpenPicker();
            Assert.True(cancel.Dialog!.Items.Count == 2, string.Join(" | ", cancel.Dialog.Items)); // current plus saved file
            cancel.HandleKey(MenuKey.Escape);
            Assert.Null(cancel.PlaybackSource);
            Assert.Equal(MenuStatus.Menu, cancel.Status);

            var current = OpenPicker();
            current.HandleKey(MenuKey.Enter);
            Assert.NotNull(current.PlaybackSource);
            Assert.Equal(2, current.PlaybackSource!.RaceTicks64); // just-finished in-memory replay
            Assert.Equal(MenuStatus.Race, current.Status);
            current.EndReplayPlayback();
            Assert.Equal(MenuStatus.Menu, current.Status);

            var saved = OpenPicker();
            saved.HandleKey(MenuKey.Down); saved.HandleKey(MenuKey.Enter);
            Assert.NotNull(saved.PlaybackSource);
            Assert.Equal(4, saved.Session!.TrackNumber);
            saved.EndReplayPlayback();

            var missing = OpenPicker();
            File.Delete(Path.Combine(dir, "saved.rpy"));
            missing.HandleKey(MenuKey.Down); missing.HandleKey(MenuKey.Enter);
            Assert.Null(missing.PlaybackSource);
            Assert.Equal(MenuStatus.Menu, missing.Status);
            Assert.Contains("could not be loaded", missing.LastMessage);

            File.WriteAllBytes(Path.Combine(dir, "broken.rpy"), [0, 1, 2]);
            var broken = OpenPicker();
            broken.HandleKey(MenuKey.Down); broken.HandleKey(MenuKey.Enter);
            Assert.Null(broken.PlaybackSource);
            Assert.Equal(MenuStatus.Menu, broken.Status);
            Assert.Contains("could not be loaded", broken.LastMessage);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [GameFact]
    public void ResultsReplay_RestoresResultsAndTournamentWithoutChangingSettings()
    {
        var menu = Menu();
        menu.Config.RaceType = 1;
        menu.StartRace();
        var tournament = Assert.IsType<TournamentSeries>(menu.Tournament);
        var result = new RaceResults(Array.AsReadOnly(Enumerable.Range(0, 8).Select(i =>
            new RacerResult(i + 1, i, RaceLoader.CarName(menu.Session!.Setup, i), i == 0, false, 1000 + i,
                Array.Empty<int>(), null)).ToArray()));
        var recorded = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        menu.CompleteRace(result, recorded);
        var completedResults = menu.Results;
        var completedSession = menu.Session;
        var config = menu.Config.ToBytes();
        int completedMask = tournament.CompletedMask;

        Keys(menu, MenuKey.Down, MenuKey.Down, MenuKey.Enter); // results action 2 = replay
        Assert.Equal(MenuStatus.Race, menu.Status);
        Assert.True(menu.ResultsReplay);
        Assert.Same(tournament, menu.Tournament);
        Assert.Equal(config, menu.Config.ToBytes());
        menu.EndReplayPlayback();

        Assert.Equal(MenuStatus.Results, menu.Status);
        Assert.Same(completedSession, menu.Session);
        Assert.Same(completedResults, menu.Results);
        Assert.Same(tournament, menu.Tournament);
        Assert.Equal(completedMask, tournament.CompletedMask);
        Assert.Equal(config, menu.Config.ToBytes());
        Assert.False(menu.ResultsReplay);
    }

    [GameFact]
    public void SplitSingleRace_StartsWithTwoHumans()
    {
        var m = Menu(); m.Config.GameType = 1;
        Open(m, "RACE");
        Assert.Equal(MenuStatus.Menu, m.Status);   // first the name editor for players 1 and 2 (PlayerNamesTests)
        Keys(m, MenuKey.Enter, MenuKey.Enter);
        Assert.Equal(MenuStatus.Race, m.Status);
        Assert.NotNull(m.Session);
        Assert.Equal(2, m.Session!.Setup.NumPlayerCars);
        Assert.Equal(1, m.Session.Setup.CommMode);
    }

    [GameFact]
    public void UnsupportedStarts_LeaveMenuAndReportReason()
    {
        foreach (var (game, race) in new[] { (2, 0), (4, 0) })
        {
            var m = Menu(); m.Config.GameType = (byte)game; m.Config.RaceType = (byte)race;
            m.StartRace();
            Assert.Equal(MenuStatus.Menu, m.Status); Assert.Null(m.Session); Assert.NotNull(m.LastMessage);
        }
    }

    [GameFact]
    public void SavedUnlocks_UseOriginalExpandedTrackAndCarLists()
    {
        var m = Menu(); m.Config[0x1C] = 1; m.Config[0x1D] = 3;
        Open(m, "location"); Keys(m, MenuKey.Left);
        Assert.Equal(7, m.Config.Track); Done(m);
        Open(m, "player one"); Keys(m, MenuKey.Left);
        Assert.True(m.Config.P1Car > 11);
        Keys(m, MenuKey.Right); Assert.Equal(0, m.Config.P1Car);
    }

    [GameFact]
    public void AdoptedRace_ResultsEscapeReturnsToMenu()
    {
        var m = Menu(); var session = new RaceLoader(GameData.Dir).Load(m.BuildSetup());
        m.AdoptRace(session);
        m.CompleteRace(RaceResults.Capture(new RaceRunner(session, withAi: true)));
        Keys(m, MenuKey.Escape);
        Assert.Equal(MenuStatus.Menu, m.Status);
        Assert.Equal(NFS.Game.Flow.GameState.FrontEnd, m.Flow.State);
    }

    [GameFact]
    public void AdoptedRace_RestartPreservesTrackAndCreatesNewObjects()
    {
        var m = Menu(); m.Config.Track = 2;
        var session = new RaceLoader(GameData.Dir).Load(m.BuildSetup());
        var host = Menu(); host.AdoptRace(session);
        host.CompleteRace(RaceResults.Capture(new RaceRunner(session, withAi: true)));
        Open(host, "restart");
        Assert.Equal(4, host.Session!.TrackNumber);
        Assert.NotSame(session.Cars[0].Obj, host.Session.Cars[0].Obj);
        Assert.Equal(session.Setup.Block, host.Session.Setup.Block);
    }

    [GameFact]
    public void NestedCancel_RestoresCarSettingsThenParentChoices()
    {
        var m = Menu(); Open(m, "game setup"); Select(m, "style"); Keys(m, MenuKey.Left); Done(m);
        Open(m, "player one"); Keys(m, MenuKey.Right);
        Open(m, "settings"); Keys(m, MenuKey.Right, MenuKey.Escape);
        Assert.Equal(1, m.Config.P1Car);
        Assert.Equal(0, m.Config[0x33]);
        Keys(m, MenuKey.Escape);
        Assert.Equal(0, m.Config.P1Car);
    }

    [GameFact]
    public void SplitOptions_EnableCatchupAndPlayerTwoSkipSingleOpponent()
    {
        var m = Menu(); Open(m, "game setup"); Keys(m, MenuKey.Right);
        Assert.Equal(1, m.Config.CatchUp); Done(m);
        Open(m, "player two"); Done(m);
        Open(m, "opponents"); Select(m, "number"); Keys(m, MenuKey.Left);
        Assert.Equal(0, m.Config.OpponentCount);
        Keys(m, MenuKey.Right); Assert.Equal(2, m.Config.OpponentCount);
    }

    [GameFact]
    public void InvalidSavedConfig_FallsBackToOriginalDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "nfs-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "GAMEDATA"));
        Directory.CreateDirectory(Path.Combine(root, "FEDATA", "PC", "TEXT"));
        Directory.CreateDirectory(Path.Combine(root, "FEDATA", "PC", "CONFIG"));
        try
        {
            File.Copy(Path.Combine(GameData.Dir!, "FEDATA", "PC", "TEXT", "TEXT.ENG"), Path.Combine(root, "FEDATA", "PC", "TEXT", "TEXT.ENG"));
            File.WriteAllBytes(Path.Combine(root, "FEDATA", "PC", "CONFIG", "CONFIG.DAT"), [1, 2, 3]);
            var m = MenuController.Load(root, useSavedConfig: true);
            Assert.Equal(4, m.Config.Laps); Assert.Equal(1, m.Config.Style);
            Assert.Equal(2, m.Config.OpponentCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [GameFact]
    public void MenuRaceTicksResultsMenu_RestartRetainsChoices()
    {
        var m = Menu(); Open(m, "location"); Keys(m, MenuKey.Right); Done(m);
        Open(m, "RACE");
        Assert.Equal(MenuStatus.Race, m.Status); Assert.Equal(2, m.Session!.TrackNumber);
        var runner = new RaceRunner(m.Session, withAi: true);
        for (int i = 0; i < 6; i++) runner.Step(new PlayerInput());
        Assert.True(runner.Tick > 0);
        m.CompleteRace(RaceResults.Capture(runner));
        Assert.Equal(MenuStatus.Results, m.Status); Assert.Equal(8, m.Results!.Racers.Count);
        Open(m, "restart");
        Assert.Equal(MenuStatus.Race, m.Status); Assert.Equal(2, m.Session!.TrackNumber);
        m.CompleteRace(RaceResults.Capture(new RaceRunner(m.Session, withAi: true)));
        Open(m, "continue"); Open(m, "continue");
        Assert.Equal(MenuStatus.Menu, m.Status); Assert.Equal("main", m.CurrentScreen.Id);
        Assert.Equal(1, m.Config.Track); Assert.Null(m.Results);
    }
}
