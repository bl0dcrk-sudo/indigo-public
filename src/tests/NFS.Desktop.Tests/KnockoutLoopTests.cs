using NFS.Core;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class KnockoutLoopTests
{
    static DesktopInput Input(MenuKey key) => new(default, [key], false);
    static void Select(GameLoop loop, string text)
    {
        var m = loop.Menu.Controller;
        for (int i = 0; i < m.CurrentScreen.Items.Count; i++)
        {
            if (m.CurrentScreen.Items[m.SelectedIndex].Text == text) return;
            loop.Frame(0, Input(MenuKey.Down));
        }
        throw new Exception($"Missing menu item {text}");
    }
    static void Open(GameLoop loop, string text) { Select(loop, text); loop.Frame(0, Input(MenuKey.Enter)); }
    static GameLoop Start()
    {
        var loop = new GameLoop(); Open(loop, "game setup"); Select(loop, "race type");
        loop.Frame(0, Input(MenuKey.Right)); loop.Frame(0, Input(MenuKey.Right)); Open(loop, "DONE");
        Open(loop, "RACE"); return loop;
    }

    /// <summary>Race-1 results: the human (car 0) wins, car 8 finishes last and is eliminated.</summary>
    static RaceResults HumanWins() => new(Enumerable.Range(0, 9).Select(i =>
        new RacerResult(i + 1, i, $"RACER {i + 1}", i == 0, i != 0, 6000 + i * 300, [], null)).ToArray());

    // Review artifact is opt-in so test runs never write to the game installation.
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_KO_REVIEW_DIR") is { Length: > 0 } dir)
        { Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name)); }
    }

    [GameDataFact]
    public void MenuKnockoutStartsFirstSessionAndDesktopDrive()
    {
        var loop = Start(); var session = loop.Drive!.Scene.Session;
        Assert.Equal(MenuStatus.Race, loop.Menu.Controller.Status);
        Assert.Equal(0, session.TrackNumber); Assert.Equal(9, session.Cars.Count);
        Assert.Equal(2, session.Setup.NumLaps); Assert.Equal(2, session.Setup.RaceType);
        Assert.Equal(9, loop.Drive.Runner.Cars.Count);
        loop.Render(new FrameBuffer());
    }

    [GameDataFact]
    public void NextRaceKeepsDrivingTheHumanCarNotGridSlotZero()
    {
        // KO grid = entrants − previous place, so the race-1 winner starts race 2 from the front [ref:2758]
        // grid slot 0 is the car that finished last. The player is (class 1), not slot 0. [ref:2759]
        var loop = Start();
        Review(Render(loop), "knockout-race1-start.png");   // race 1: the human is last, cars ahead of the camera
        loop.Menu.Controller.CompleteRace(HumanWins());
        Open(loop, "continue"); Open(loop, "continue");
        loop.Frame(0, Input(MenuKey.Enter));
        var scene = loop.Drive!.Scene; var cars = scene.Session.Cars;
        var player = RaceScene.PlayerCar(cars)!;
        Assert.Equal(0, player.Index);                    // the human stays first in the compacted lineup [ref:2760]
        Assert.Equal(cars.Count - 1, player.GridSlot);    // winner → front slot
        Assert.True((player.Obj.Flags & CarFlags.LocalHuman) != 0);
        var last = cars.Single(c => c.GridSlot == 0);
        Assert.Equal(7, last.Index);                      // the car that finished last in race 1
        Assert.NotSame(player, last);
        Assert.Equal(1, cars.Count(c => (c.Obj.Flags & CarFlags.LocalHuman) != 0)); // no AI took the human flag
        Assert.True(ReferenceEquals(loop.Drive.Car, player.Obj), "the drive simulates the human car");
        Assert.True(scene.PlayerCamera().ToView(RaceScene.ToWorld(player.Obj.Pos)).Z > 0, "the camera follows it");
        Review(Render(loop), "knockout-race2-start.png");  // race 2: pole — no cars ahead of the camera
    }

    static FrameBuffer Render(GameLoop loop) { var fb = new FrameBuffer(); loop.Render(fb); return fb; }

    [GameDataFact]
    public void PauseAbortBeforeGoShowsDnfAndEndsSeriesWithoutReward()
    {
        var loop = Start();
        for (int i = 0; i < 160; i++) loop.Frame(1.0 / 32, new(default, [], false));
        Assert.Equal(0x140, loop.Drive!.Runner.Tick);
        // (1) also marks cars that have already finished as DNF in KO. [ref:2761]
        loop.Drive.Runner.Cars[0].Obj.RaceState = 2;
        loop.Drive.Runner.Cars[1].Obj.RaceState = 2;
        loop.Frame(0, new(default, [], true));
        for (int i = 0; i < 3; i++) loop.Frame(0, Input(MenuKey.Down)); // restart is skipped outside a single race
        loop.Frame(0, Input(MenuKey.Enter)); loop.Frame(0, Input(MenuKey.Left)); loop.Frame(0, Input(MenuKey.Enter));
        Assert.Equal(MenuStatus.Results, loop.Menu.Controller.Status);
        Assert.All(loop.Menu.Controller.Results!.Racers, r => Assert.Equal(1, r.RaceState));
        Assert.True(loop.Menu.Controller.Knockout!.Aborted);
        Open(loop, "continue"); Open(loop, "continue");
        Assert.True(loop.Menu.Controller.KnockoutSummary);
        loop.Frame(0, Input(MenuKey.Enter));
        Assert.Equal(MenuStatus.Menu, loop.Menu.Controller.Status);
        Assert.Null(loop.Menu.Controller.Knockout); Assert.Equal(0, loop.Menu.Controller.Config[0x1C]);
    }

    [GameDataFact]
    public void SummaryRendersAndNextRaceCreatesNewDrive()
    {
        var loop = Start(); var previous = loop.Drive!;
        loop.Menu.Controller.CompleteRace(HumanWins());
        Open(loop, "continue"); Open(loop, "continue");
        var fb = Render(loop);
        Review(fb, "knockout-summary.png");
        Assert.True(loop.Menu.Controller.KnockoutSummary);
        loop.Frame(0, Input(MenuKey.Enter));
        Assert.NotSame(previous, loop.Drive); Assert.Equal(2, loop.Drive!.Scene.Session.TrackNumber);
        Assert.Equal(8, loop.Drive.Runner.Cars.Count); Assert.Equal(2, loop.Drive.Scene.Session.Setup.NumLaps);
    }
}
