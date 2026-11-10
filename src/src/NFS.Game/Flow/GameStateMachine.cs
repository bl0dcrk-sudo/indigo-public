namespace NFS.Game.Flow;

/// <summary>Top-level states of the program flow: [ref:1598]
/// Boot → FrontEnd → BuildArgs → LoadRace → [Race ↔ Pause] (restart = inner loop) → Unload → PostRace → FrontEnd.</summary>
public enum GameState { Boot, FrontEnd, BuildArgs, LoadRace, Race, Pause, Unload, PostRace, Exit }

/// <summary>Choices: 1 resume, 2 restart, 3 end race (leads to results), 4 quit game. [ref:1599]</summary>
public enum PauseChoice { Resume = 1, Restart = 2, EndRace = 3, QuitGame = 4 }

/// <summary>Race phase from the 64 Hz race tick. [ref:1600]</summary>
public enum RacePhase { Countdown, Racing, Finished }

/// <summary>Pure (no I/O) model of the program flow; the host performs the work of each state and reports the
/// outcome through the methods below. Invalid transitions throw <see cref="InvalidOperationException"/>.</summary>
public sealed class GameStateMachine
{
    /// <summary>pause allowed from tick 0x140. [ref:1601]</summary>
    public const int PauseFromTick = 0x140;
    /// <summary>Start of the race (lights) at tick 0x200.</summary>
    public const int StartTick = 0x200;
    /// <summary>Race is over 0x140 ticks (5 s at 64 Hz) after the last human finished. [ref:1602]</summary>
    public const int FinishDelay = 0x140;

    public GameState State { get; private set; } = GameState.Boot;
    /// <summary>selection passed back on the next menu entry (starts at 1). [ref:1603]</summary>
    public int MenuSelection { get; private set; } = 1;
    /// <summary>1: replay started from the Load menu → no results screen. [ref:1604]</summary>
    public bool ReplayFromMenu { get; private set; }
    /// <summary>Number of passes of the current load (1 = first start, +1 per restart). [ref:1605]</summary>
    public int RaceStarts { get; private set; }
    public bool QuitRequested { get; private set; }

    public event Action<GameState, GameState>? Changed;

    private void Go(GameState to)
    {
        var from = State;
        State = to;
        Changed?.Invoke(from, to);
    }

    private void Expect(GameState s, string what)
    {
        if (State != s) throw new InvalidOperationException($"{what} not valid in state {State}");
    }

    /// <summary>Boot initialization done. [ref:1606]</summary>
    public void Booted() { Expect(GameState.Boot, nameof(Booted)); Go(GameState.FrontEnd); }

    /// <summary>Front end returned: 0 = quit game, otherwise start race. [ref:1607]</summary>
    public void FrontEndDone(int selection, bool replayFromMenu = false)
    {
        Expect(GameState.FrontEnd, nameof(FrontEndDone));
        if (selection == 0) { Go(GameState.Exit); return; }
        MenuSelection = selection;
        ReplayFromMenu = replayFromMenu;
        Go(GameState.BuildArgs);
    }

    /// <summary>Argument building finished. [ref:1608]</summary>
    public void ArgsBuilt() { Expect(GameState.BuildArgs, nameof(ArgsBuilt)); Go(GameState.LoadRace); }

    /// <summary>Race loaded; <paramref name="aborted"/> (network load aborted) → menu. [ref:1609]</summary>
    public void RaceLoaded(bool aborted = false)
    {
        Expect(GameState.LoadRace, nameof(RaceLoaded));
        if (aborted) { Go(GameState.FrontEnd); return; }
        RaceStarts = 1; // first pass of the restart loop [ref:1610]
        Go(GameState.Race);
    }

    /// <summary>Pause key; ignored (returns false) before tick 0x140. [ref:1611]</summary>
    public bool RequestPause(int raceTick)
    {
        Expect(GameState.Race, nameof(RequestPause));
        if (raceTick < PauseFromTick) return false;
        Go(GameState.Pause);
        return true;
    }

    public void PauseMenu(PauseChoice choice)
    {
        Expect(GameState.Pause, nameof(PauseMenu));
        switch (choice)
        {
            case PauseChoice.Resume: Go(GameState.Race); break;
            case PauseChoice.Restart: RaceStarts++; Go(GameState.Race); break; // no reload [ref:1612]
            case PauseChoice.EndRace: Go(GameState.Unload); break;
            case PauseChoice.QuitGame: QuitRequested = true; Go(GameState.Unload); break; // [hipoteza] exit after unload
            default: throw new ArgumentOutOfRangeException(nameof(choice));
        }
    }

    /// <summary>Race returned: race over, main exit, network abort or replay end. [ref:1613]</summary>
    public void RaceEnded() { Expect(GameState.Race, nameof(RaceEnded)); Go(GameState.Unload); }

    /// <summary>Unload done. A replay started from the Load menu skips the results. [ref:1614]</summary>
    public void Unloaded()
    {
        Expect(GameState.Unload, nameof(Unloaded));
        if (QuitRequested) Go(GameState.Exit);
        else if (ReplayFromMenu) { ReplayFromMenu = false; Go(GameState.FrontEnd); }
        else Go(GameState.PostRace);
    }

    /// <summary>1 = straight into the next race/replay (skip menu), else back to menu. [ref:1615]</summary>
    public void PostRaceDone(int result)
    {
        Expect(GameState.PostRace, nameof(PostRaceDone));
        Go(result == 1 ? GameState.BuildArgs : GameState.FrontEnd);
    }

    /// <summary>Race phase: Countdown before tick 0x200, Finished once 0x140 ticks passed since the last human
    /// finished (<paramref name="lastHumanFinishTick"/> null = not all humans finished).</summary>
    public static RacePhase Phase(int raceTick, int? lastHumanFinishTick) =>
        raceTick < StartTick ? RacePhase.Countdown
        : lastHumanFinishTick is { } f && raceTick - f > FinishDelay ? RacePhase.Finished
        : RacePhase.Racing;
}
