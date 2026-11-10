using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;

namespace NFS.Desktop.Tests;

/// <summary>Saving/loading a tournament `.tou` in the menu and immediate saving of unlocks. Temporary directories only. [ref:3463]</summary>
public class TournamentSaveLoopTests
{
    static DesktopInput Input(MenuKey key) => new(default, [key], false);
    static void Open(GameLoop loop, string text)
    {
        var m = loop.Menu.Controller;
        for (int i = 0; i < m.CurrentScreen.Items.Count; i++)
        {
            if (m.CurrentScreen.Items[m.SelectedIndex].Text == text)
            { loop.Frame(0, Input(MenuKey.Enter)); return; }
            loop.Frame(0, Input(MenuKey.Down));
        }
        throw new Exception($"Missing menu item {text}");
    }
    static RaceResults HumanWins() => new(Enumerable.Range(0, 8).Select(i => new RacerResult(i + 1, i,
        $"RACER {i + 1}", i == 0, i != 0, 6000 + i * 300, [], null)).ToArray());

    static GameLoop NewLoop(string dir) => new(null, false, Path.Combine(dir, "settings.json"));
    static string TempDir() => Directory.CreateTempSubdirectory("nfs-tou-loop-").FullName;
    static readonly int[] TrackBits = [1, 2, 16, 4, 8, 32, 64];

    /// <summary>Menu, tournament, first race completed, totals table.</summary>
    static GameLoop ToSummary(string dir)
    {
        var loop = NewLoop(dir); loop.Menu.Controller.Config.RaceType = 1;
        Open(loop, "RACE");
        loop.Menu.Controller.CompleteRace(HumanWins());
        Open(loop, "continue"); Open(loop, "continue");
        Assert.True(loop.Menu.Controller.TournamentSummary);
        return loop;
    }

    static void PlayRemainingRacesAndFinish(GameLoop loop)
    {
        var m = loop.Menu.Controller;
        for (int race = 1; race < 7; race++)
        {
            loop.Frame(0, Input(MenuKey.Enter));                                    // NEXT RACE
            m.CompleteRace(HumanWins());
            Open(loop, "continue"); Open(loop, "continue");
        }
        Assert.True(m.Tournament!.Finished);
        loop.Frame(0, Input(MenuKey.Enter));                                        // FINISH
    }

    [GameDataFact]
    public void ReverseTournamentLoadsEveryNextRaceFromTheWinnersGrid()
    {
        // carries the 1-based rank into the next grid; reversed, slot 8 used to land before the track start. [ref:2959]
        var dir = TempDir();
        try
        {
            var loop = NewLoop(dir); var c = loop.Menu.Controller.Config;
            c.RaceType = 1; c.Backward = 1;
            Open(loop, "RACE");
            loop.Menu.Controller.CompleteRace(HumanWins());
            Open(loop, "continue"); Open(loop, "continue");
            Assert.Equal(7, loop.Menu.Controller.Tournament!.GridSlot(7));
            PlayRemainingRacesAndFinish(loop);
        }
        finally { Directory.Delete(dir, true); }
    }

    static void Type(GameLoop loop, string text) => loop.Frame(0, new DesktopInput(default, [], false, Typed: text.ToCharArray()));

    /// <summary>SAVE on the totals table → name field → type the name → Enter → message „tournament saved” → Enter closes. [ref:2960]</summary>
    static void SaveFromSummary(GameLoop loop, string name = "abc")
    {
        while (loop.Menu.Controller.SelectedIndex != 3) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, Input(MenuKey.Enter));
        Type(loop, name);
        loop.Frame(0, Input(MenuKey.Enter));
        loop.Frame(0, Input(MenuKey.Enter));
        Assert.Null(loop.Menu.Controller.Dialog);
    }

    static void OpenLoadSave(GameLoop loop) { Open(loop, "options"); Open(loop, "load/save"); }

    [GameDataFact]
    public void ManualSaveFromSummaryWritesTouInUserDirectoryAndNotAutomatically()
    {
        var dir = TempDir();
        try
        {
            var loop = ToSummary(dir);
            Assert.False(Directory.Exists(Path.Combine(dir, "saves")));      // the original does not save on its own after a race
            for (int i = 0; i < 3; i++) loop.Frame(0, Input(MenuKey.Down));
            loop.Frame(0, Input(MenuKey.Enter));                                     // SAVE → „save tournament as” (TEXT 0x2E1) [ref:2961]
            var m = loop.Menu.Controller;
            Assert.Equal(DialogKind.Text, m.Dialog!.Kind); Assert.Equal(0x2E1, m.Dialog.TextId);
            Assert.Equal("name", m.Dialog.Entry);                                  // starting buffer = TEXT 0x2F8
            Type(loop, "abc");
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.Message, m.Dialog!.Kind); Assert.Equal(0x2ED, m.Dialog.TextId);   // „tournament saved”
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Null(m.Dialog);
            var file = TouFile.Parse(File.ReadAllBytes(Path.Combine(dir, "saves", "abc.tou")));    // `%s%s.tou`
            var t = m.Tournament!;
            Assert.Equal(1, file.TrackCount); Assert.Equal(t.CompletedMask, file.Mask);
            Assert.Equal(t.Points, file.Points.Select(p => (int)p).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void SaveOverwriteAsksAndNoKeepsTheOldFileEscapeOrEmptyNameWritesNothing()
    {
        var dir = TempDir();
        try
        {
            var loop = ToSummary(dir);
            var m = loop.Menu.Controller;
            string path = Path.Combine(dir, "saves", "abc.tou");
            SaveFromSummary(loop);
            var before = File.ReadAllBytes(path);
            PlayOneMoreRace(loop);                                                   // the series changed: a new save would differ from the old one
            for (int i = 0; i < 3; i++) loop.Frame(0, Input(MenuKey.Down));
            loop.Frame(0, Input(MenuKey.Enter)); Type(loop, "abc"); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.Confirm, m.Dialog!.Kind); Assert.Equal(0x2F5, m.Dialog.TextId);   // „file exists. Overwrite?”
            Assert.False(m.Dialog.Yes);
            loop.Frame(0, Input(MenuKey.Enter));                                     // default is „no”
            Assert.Null(m.Dialog); Assert.Equal(before, File.ReadAllBytes(path));

            loop.Frame(0, Input(MenuKey.Enter)); Type(loop, "abc"); loop.Frame(0, Input(MenuKey.Enter));
            Type(loop, "y");                                                         // English „yes” letter
            Assert.Equal(DialogKind.Message, m.Dialog!.Kind); Assert.Equal(0x2ED, m.Dialog.TextId);
            Assert.NotEqual(before, File.ReadAllBytes(path));
            loop.Frame(0, Input(MenuKey.Enter));

            loop.Frame(0, Input(MenuKey.Enter)); Type(loop, "x"); loop.Frame(0, Input(MenuKey.Escape));   // Esc
            Assert.Null(m.Dialog); Assert.False(File.Exists(Path.Combine(dir, "saves", "x.tou")));
            loop.Frame(0, Input(MenuKey.Enter)); Type(loop, "\b"); loop.Frame(0, Input(MenuKey.Enter));   // empty name
            Assert.Null(m.Dialog); Assert.Equal(["abc"], Directory.GetFiles(Path.Combine(dir, "saves")).Select(Path.GetFileNameWithoutExtension));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Next completed race of the series from the totals table (NEXT RACE → result → table).</summary>
    static void PlayOneMoreRace(GameLoop loop)
    {
        while (loop.Menu.Controller.SelectedIndex != 0) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, Input(MenuKey.Enter));
        loop.Menu.Controller.CompleteRace(HumanWins());
        Open(loop, "continue"); Open(loop, "continue");
        Assert.True(loop.Menu.Controller.TournamentSummary);
    }

    [GameDataFact]
    public void LoadRestoresSeriesAndTheNextRaceUsesAnUncompletedTrack()
    {
        var dir = TempDir();
        try
        {
            var first = ToSummary(dir); SaveFromSummary(first);
            var saved = first.Menu.Controller.Tournament!;

            var loop = NewLoop(dir);
            Assert.Null(loop.Menu.Controller.Tournament);
            OpenLoadSave(loop);
            loop.Frame(0, Input(MenuKey.Enter));                             // load tournament (first active entry)
            var m = loop.Menu.Controller;
            Assert.Equal(DialogKind.List, m.Dialog!.Kind); Assert.Equal(["abc"], m.Dialog.Items);   // list `*.tou` [ref:2962]
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(0x17C, m.Dialog!.TextId);                           // „tournament loaded”, then exit the screen [ref:2963]
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal("main", m.CurrentScreen.Id);
            Assert.NotNull(m.Tournament); Assert.Equal(1, m.Config.RaceType);
            Assert.Equal(saved.Points, m.Tournament.Points);
            Assert.Equal(saved.CompletedMask, m.Tournament.CompletedMask);
            Open(loop, "RACE");
            Assert.NotNull(loop.Drive);
            Assert.Equal(0, saved.CompletedMask & TrackBits[m.Tournament.Track]);   // track not yet completed
            Assert.Equal(m.Tournament.Track, m.Config.Track);
            Assert.Equal(8, loop.Drive.Runner.Cars.Count);
            m.CompleteRace(HumanWins());                                     // the loaded series keeps scoring points
            Assert.Equal(2, m.Tournament.CompletedTracks);
            Assert.Equal(saved.Points[0] + 30, m.Tournament.Points[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void MissingOrCorruptSaveKeepsTheGameRunning()
    {
        var dir = TempDir();
        try
        {
            var loop = NewLoop(dir);
            OpenLoadSave(loop);
            Assert.NotEqual(0, loop.Menu.Controller.CurrentScreen.Items[0].Flags & 1);  // save tournament disabled without a series
            loop.Frame(0, Input(MenuKey.Enter));                                        // load: no files = empty list
            var m = loop.Menu.Controller;
            Assert.Equal(DialogKind.List, m.Dialog!.Kind); Assert.Empty(m.Dialog.Items); Assert.Equal(-1, m.Dialog.Selected);
            loop.Frame(0, Input(MenuKey.Enter));                                        // Enter on „cancel”
            Assert.Null(m.Dialog); Assert.Null(m.Tournament);
            Directory.CreateDirectory(Path.Combine(dir, "saves"));
            File.WriteAllBytes(Path.Combine(dir, "saves", "broken.tou"), [1, 2, 3]);    // corrupted
            loop.Frame(0, Input(MenuKey.Enter)); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.Message, m.Dialog!.Kind); Assert.Equal(0x2F0, m.Dialog.TextId);   // „load failed”
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Null(m.Tournament); Assert.Equal(0, m.Config.RaceType);
            Assert.Equal("load-save", m.CurrentScreen.Id);                          // stays on the Load/Save screen
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void LoadWhileASeriesIsRunningAsksFirstAndListsFilesSortedByName()
    {
        var dir = TempDir();
        try
        {
            var first = ToSummary(dir); SaveFromSummary(first, "zeta"); SaveFromSummary(first, "alpha");
            var m = first.Menu.Controller;
            m.ReturnToMenu();                                                           // the port loses the series when returning to the menu
            Assert.Null(m.Tournament);
            var loop = NewLoop(dir); OpenLoadSave(loop);
            loop.Frame(0, Input(MenuKey.Enter));                                        // no series: list right away
            var list = loop.Menu.Controller.Dialog!;
            Assert.Equal(["alpha", "zeta"], list.Items);
            loop.Frame(0, Input(MenuKey.Down)); loop.Frame(0, Input(MenuKey.Enter));    // zeta
            loop.Frame(0, Input(MenuKey.Enter));                                        // message 0x17C
            var m2 = loop.Menu.Controller;
            Assert.NotNull(m2.Tournament);

            OpenLoadSave(loop);                                                         // series in progress → „quit the current tournament?”
            Assert.Equal(DialogKind.Confirm, Advance(loop, m2, "load tournament").Kind);
            Assert.Equal(0x2E9, m2.Dialog!.TextId); Assert.False(m2.Dialog.Yes);
            loop.Frame(0, Input(MenuKey.Enter));                                        // „no”: no list
            Assert.Null(m2.Dialog);
            Advance(loop, m2, "load tournament"); loop.Frame(0, Input(MenuKey.Left)); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.List, m2.Dialog!.Kind);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Selects an entry and Enter; returns the open window.</summary>
    static FrontEndDialog Advance(GameLoop loop, MenuController m, string item)
    {
        while (m.CurrentScreen.Items[m.SelectedIndex].Text != item) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, Input(MenuKey.Enter));
        return m.Dialog!;
    }

    [GameDataFact]
    public void DeleteTournamentPicksAFileConfirmsAndAnnouncesIt()
    {
        var dir = TempDir();
        try
        {
            var first = ToSummary(dir); SaveFromSummary(first, "one"); SaveFromSummary(first, "two");
            string one = Path.Combine(dir, "saves", "one.tou"), two = Path.Combine(dir, "saves", "two.tou");

            var loop = NewLoop(dir); OpenLoadSave(loop);
            var m = loop.Menu.Controller;
            Assert.Equal(DialogKind.List, Advance(loop, m, "delete tournament").Kind);
            loop.Frame(0, Input(MenuKey.Down));                                         // two
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.Confirm, m.Dialog!.Kind); Assert.Equal(0x2EB, m.Dialog.TextId);   // „delete saved game from disk?”
            loop.Frame(0, Input(MenuKey.Enter));                                        // default is „no”
            Assert.True(File.Exists(two));
            Advance(loop, m, "delete tournament"); loop.Frame(0, Input(MenuKey.Down)); loop.Frame(0, Input(MenuKey.Enter));
            Type(loop, "Y");
            Assert.Equal(0x2EC, m.Dialog!.TextId);                                      // „tournament file deleted”
            Assert.False(File.Exists(two)); Assert.True(File.Exists(one));
            loop.Frame(0, Input(MenuKey.Enter));
            Advance(loop, m, "delete tournament"); loop.Frame(0, Input(MenuKey.Escape)); // Esc on the list: nothing happens
            Assert.Null(m.Dialog); Assert.True(File.Exists(one));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ReplaySaveAfterResultsWritesTheNewRecordingInsteadOfInstallationReplay()
    {
        var dir = TempDir();
        try
        {
            var loop = NewLoop(dir);
            var m = loop.Menu.Controller;
            m.StartRace();
            var recorder = new ReplayRecorder(m.Session!.Setup);
            recorder.Record(new NFS.Core.PlayerInput(-7, 247, 0, 0));
            recorder.Record(default);
            var recorded = recorder.Build(4);
            var installed = NFS.Data.GameFiles.ReadRaw("REPLAY.RPY");
            m.CompleteRace(HumanWins(), recorded);
            m.ReturnToMenu();
            OpenLoadSave(loop);
            Advance(loop, m, "save replay");
            Type(loop, "newlap"); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(0x2EE, m.Dialog!.TextId);
            var saved = File.ReadAllBytes(Path.Combine(dir, "saves", "newlap.rpy"));
            Assert.Equal(recorded.RawData, saved);
            Assert.Equal(4, Replay.Parse(saved).RaceTicks);
            Assert.Equal(installed, NFS.Data.GameFiles.ReadRaw("REPLAY.RPY"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ReplaySaveCopiesTheCurrentReplayAndDeleteRemovesIt()
    {
        var dir = TempDir();
        try
        {
            var loop = NewLoop(dir); OpenLoadSave(loop);
            var m = loop.Menu.Controller;
            Assert.Equal(0, m.CurrentScreen.Items[3].Flags & 1);                        // replay.rpy from the disc exists → „save replay” active
            Assert.Equal(DialogKind.Text, Advance(loop, m, "save replay").Kind);
            Assert.Equal(0x2E2, m.Dialog!.TextId);                                      // „save replay as”
            Type(loop, "lap1"); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(0x2EE, m.Dialog!.TextId);                                      // „replay saved”
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(NFS.Data.GameFiles.ReadRaw("REPLAY.RPY"), File.ReadAllBytes(Path.Combine(dir, "saves", "lap1.rpy")));

            Advance(loop, m, "view replay");                                            // „current replay” + the saved one
            Assert.Equal(["current replay", "lap1"], m.Dialog!.Items);
            loop.Frame(0, Input(MenuKey.Escape));
            Assert.Equal(DialogKind.List, Advance(loop, m, "delete replay").Kind);
            Assert.Equal(["lap1"], m.Dialog!.Items);
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(0x2F1, m.Dialog!.TextId);                                      // „delete replay file from disk?”
            loop.Frame(0, Input(MenuKey.Right)); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(0x2F2, m.Dialog!.TextId);                                      // „replay deleted”
            Assert.False(File.Exists(Path.Combine(dir, "saves", "lap1.rpy")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void TournamentPodiumUnlockIsPersistedImmediatelyAndSurvivesANewLoop()
    {
        var dir = TempDir();
        try
        {
            var loop = ToSummary(dir);
            var settings = Path.Combine(dir, "settings.json");
            Assert.True(File.Exists(settings));                                         // migration on first start
            Assert.NotEqual(3, UserSettings.Load(settings).Unlocks1D);                  // reward not yet granted
            PlayRemainingRacesAndFinish(loop);                                          // reward on completing the finale
            Assert.Equal(3, loop.Menu.Controller.Config[0x1D]);
            Assert.Equal(3, UserSettings.Load(settings).Unlocks1D);                     // no SaveSettings on exit
            Assert.Equal(3, NewLoop(dir).Menu.Controller.Config[0x1D]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void UnlockPersistenceKeepsUnknownKeysAndNewerSchemaFiles()
    {
        var dir = TempDir();
        try
        {
            var settings = Path.Combine(dir, "settings.json");
            File.WriteAllText(settings, """{ "schema": 1, "futureKey": 42 }""");
            PlayRemainingRacesAndFinish(ToSummary(dir));
            Assert.Contains("futureKey", File.ReadAllText(settings));
            Assert.Equal(3, UserSettings.Load(settings).Unlocks1D);

            var newer = """{ "schema": 99, "unlocks1D": 1 }""";
            File.WriteAllText(settings, newer);
            PlayRemainingRacesAndFinish(ToSummary(dir));
            Assert.Equal(newer, File.ReadAllText(settings));                            // newer schema left untouched
        }
        finally { Directory.Delete(dir, true); }
    }
}
