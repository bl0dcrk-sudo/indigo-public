using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class TournamentLoopTests
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
    static GameLoop Start()
    {
        var loop = new GameLoop(); loop.Menu.Controller.Config.RaceType = 1;
        Open(loop, "RACE"); return loop;
    }

    [GameDataFact]
    public void TournamentSummaryRenders_AndSelectedRemainingTrackStartsNewDrive()
    {
        var loop = Start(); var previous = loop.Drive!;
        Assert.Equal(8, previous.Scene.Session.Cars.Count); Assert.Equal(4, previous.Scene.Session.Setup.NumLaps);
        var results = new RaceResults(Enumerable.Range(0, 8).Select(i => new RacerResult(i + 1, i,
            $"RACER {i + 1}", i == 0, i != 0, 6000 + i * 300, [], null)).ToArray());
        loop.Menu.Controller.CompleteRace(results); Open(loop, "continue"); Open(loop, "continue");
        Assert.True(loop.Menu.Controller.TournamentSummary);
        loop.Frame(0, Input(MenuKey.Right));
        int track = loop.Menu.Controller.NextTournamentTrack;
        Assert.Contains(track, loop.Menu.Controller.Tournament!.AvailableTracks);
        var fb = new FrameBuffer(); loop.Render(fb);
        if (Environment.GetEnvironmentVariable("NFS_TOURNAMENT_REVIEW_DIR") is { Length: > 0 } review)
        { Directory.CreateDirectory(review); fb.SavePng(Path.Combine(review, "tournament-summary.png")); }
        loop.Frame(0, Input(MenuKey.Enter));
        Assert.NotSame(previous, loop.Drive); Assert.Equal(track, loop.Menu.Controller.Tournament.Track);
        Assert.Equal(8, loop.Drive!.Runner.Cars.Count); Assert.Equal(4, loop.Drive.Scene.Session.Setup.NumLaps);
    }

    [GameDataFact]
    public void PauseEndRaceShowsAbortedResultsAndKeepsTournamentWithoutPoints()
    {
        var loop = Start();
        for (int i = 0; i < 160; i++) loop.Frame(1.0 / 32, new(default, [], false));
        loop.Frame(0, new(default, [], true)); Assert.True(loop.Drive!.Paused);
        for (int i = 0; i < 3; i++) loop.Frame(0, Input(MenuKey.Down)); // restart greyed out in a tournament
        loop.Frame(0, Input(MenuKey.Enter)); loop.Frame(0, Input(MenuKey.Left)); loop.Frame(0, Input(MenuKey.Enter));
        Assert.Equal(MenuStatus.Results, loop.Menu.Controller.Status); Assert.NotNull(loop.Menu.Controller.Tournament);
        Assert.Equal(1, loop.Menu.Controller.Results!.Racers.Single(r => r.Human).RaceState);
        Assert.Equal(0, loop.Menu.Controller.Tournament!.Points[0]); // DNF human gets no points
        Assert.Null(loop.Drive); Assert.Equal(0, loop.Menu.Controller.Config[0x1D]);
    }
}
