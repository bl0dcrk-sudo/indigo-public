using NFS.Game.Flow;
using Xunit;

namespace NFS.Tests.Game;

public class GameStateMachineTests
{
    private static GameStateMachine InRace(bool replayFromMenu = false)
    {
        var m = new GameStateMachine();
        m.Booted();
        m.FrontEndDone(1, replayFromMenu);
        m.ArgsBuilt();
        m.RaceLoaded();
        return m;
    }

    [Fact]
    public void FullCycle_BackToMenu()
    {
        var seen = new List<GameState>();
        var m = new GameStateMachine();
        m.Changed += (_, to) => seen.Add(to);
        m.Booted();
        m.FrontEndDone(3);
        m.ArgsBuilt();
        m.RaceLoaded();
        Assert.True(m.RequestPause(0x140));
        m.PauseMenu(PauseChoice.Resume);
        m.RaceEnded();
        m.Unloaded();
        m.PostRaceDone(0);
        Assert.Equal([GameState.FrontEnd, GameState.BuildArgs, GameState.LoadRace, GameState.Race, GameState.Pause,
            GameState.Race, GameState.Unload, GameState.PostRace, GameState.FrontEnd], seen);
        Assert.Equal(3, m.MenuSelection);
    }

    [Fact]
    public void MenuExit_QuitsGame()
    {
        var m = new GameStateMachine();
        m.Booted();
        m.FrontEndDone(0);
        Assert.Equal(GameState.Exit, m.State);
    }

    [Fact]
    public void Pause_OnlyFromTick0x140()
    {
        var m = InRace();
        Assert.False(m.RequestPause(0x13F));
        Assert.Equal(GameState.Race, m.State);
        Assert.True(m.RequestPause(0x140));
        Assert.Equal(GameState.Pause, m.State);
    }

    [Fact]
    public void Restart_StaysInRace_WithoutReload()
    {
        var m = InRace();
        Assert.Equal(1, m.RaceStarts);
        m.RequestPause(0x400);
        m.PauseMenu(PauseChoice.Restart);
        Assert.Equal(GameState.Race, m.State);
        Assert.Equal(2, m.RaceStarts);
    }

    [Fact]
    public void EndRace_GoesToResults_QuitExits()
    {
        var m = InRace();
        m.RequestPause(0x400);
        m.PauseMenu(PauseChoice.EndRace);
        m.Unloaded();
        Assert.Equal(GameState.PostRace, m.State);
        m.PostRaceDone(1); // skipMenu: straight to the next race
        Assert.Equal(GameState.BuildArgs, m.State);

        var q = InRace();
        q.RequestPause(0x400);
        q.PauseMenu(PauseChoice.QuitGame);
        q.Unloaded();
        Assert.Equal(GameState.Exit, q.State);
    }

    [Fact]
    public void ReplayFromMenu_SkipsResults_AbortedLoadReturnsToMenu()
    {
        var m = InRace(replayFromMenu: true);
        m.RaceEnded();
        m.Unloaded();
        Assert.Equal(GameState.FrontEnd, m.State);
        Assert.False(m.ReplayFromMenu);

        var a = new GameStateMachine();
        a.Booted(); a.FrontEndDone(1); a.ArgsBuilt();
        a.RaceLoaded(aborted: true);
        Assert.Equal(GameState.FrontEnd, a.State);
    }

    [Fact]
    public void InvalidTransition_Throws()
    {
        var m = new GameStateMachine();
        Assert.Throws<InvalidOperationException>(() => m.RaceEnded());
        Assert.Throws<InvalidOperationException>(() => m.ArgsBuilt());
    }

    [Theory]
    [InlineData(0x1FF, null, RacePhase.Countdown)]
    [InlineData(0x200, null, RacePhase.Racing)]
    [InlineData(0x1000, 0x1000 - 0x140, RacePhase.Racing)]
    [InlineData(0x1000, 0x1000 - 0x141, RacePhase.Finished)]
    public void Phase(int tick, int? finish, RacePhase expected) =>
        Assert.Equal(expected, GameStateMachine.Phase(tick, finish));
}
