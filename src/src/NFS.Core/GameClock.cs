namespace NFS.Core;

/// <summary>
/// Race timer: runs at 128 Hz (<c>(0x80)</c>): [ref:0450]
/// <c>t128++; if t128 even: t64++; if t64 even: t32++ and SetEvent → input thread (32 Hz)</c>. [ref:0451]
/// Headless: call <see cref="Tick128"/> from a real-time or simulated clock.
/// </summary>
public sealed class GameClock
{
    public const int Hz = 128;

    public int Timer128 { get; private set; }   // [ref:0452]
    public int Timer64 { get; private set; }    // [ref:0453]
    public int Timer32 { get; private set; }    // [ref:0454]

    /// <summary>Raised on every 4th interrupt (the input thread's event).</summary>
    public event Action? InputTick;

    /// <summary>One 128 Hz interrupt; returns true when the 32 Hz input event fired.</summary>
    public bool Tick128()
    {
        Timer128++;
        if ((Timer128 & 1) != 0) return false;
        Timer64++;
        if ((Timer64 & 1) != 0) return false;
        Timer32++;
        InputTick?.Invoke();
        return true;
    }
}

/// <summary>
/// Sim queue, single-machine mode (0): per player a ring of 128 four-byte [ref:0455]
/// inputs (p·0x200) with occupancy flags (p·0x80), write index per player [ref:0456]
/// read index. <see cref="Prefill"/> puts 4 empty inputs and sets ticks-available to 3, so input [ref:0457]
/// sampled at 32 Hz tick n is consumed by the simulation at tick n+4 (125 ms) – recording and replay alike.
/// </summary>
public sealed class InputQueue
{
    public const int MaxPlayers = 8, Slots = 128, PrefillTicks = 4;

    private readonly uint[,] _data = new uint[MaxPlayers, Slots];
    private readonly bool[,] _busy = new bool[MaxPlayers, Slots];
    private readonly int[] _write = new int[MaxPlayers];
    private readonly uint[] _current = new uint[MaxPlayers];   // inputs of the current sim tick [ref:0458]

    public int NumPlayers { get; set; } = 1;                    // [ref:0459]
    public int ReadIndex { get; private set; }                  // [ref:0460]
    public int TicksAvailable { get; set; }                     // [ref:0461]
    public int WriteIndex(int player) => _write[player];

    /// <summary>In replay playback (<c>&gt; 1</c>) only ticks-available = 0. [ref:0462]</summary>
    public void Prefill(bool replayPlayback = false)
    {
        if (replayPlayback) { TicksAvailable = 0; return; }
        for (int p = 0; p < MaxPlayers; p++)
        {
            for (int s = 0; s < Slots; s++) { _data[p, s] = 0; _busy[p, s] = s < PrefillTicks; }
            _write[p] = PrefillTicks;
        }
        ReadIndex = 0;
        TicksAvailable = PrefillTicks - 1;
    }

    /// <summary>Net mode 0: stores into the player's write slot if it is free [ref:0463]
    /// then write index++ and ticks-available++. Returns false when the slot is still occupied.</summary>
    public bool Push(int player, uint input)
    {
        int slot = _write[player] & (Slots - 1);
        if (_busy[player, slot]) return false;
        _data[player, slot] = input;
        _busy[player, slot] = true;
        _write[player]++;
        TicksAvailable++;
        return true;
    }

    /// <summary>(called every 2nd 64 Hz step with tick32 = steps/2): copies slot [ref:0464]
    /// <c>tick32 &amp; 0x7F</c> of every player to the current inputs and frees it; read index++.</summary>
    public void Pop(int tick32)
    {
        int slot = tick32 & (Slots - 1);
        for (int p = 0; p < NumPlayers; p++) _current[p] = _data[p, slot];
        for (int p = 0; p < NumPlayers; p++) _busy[p, slot] = false;
        ReadIndex++;
    }

    /// <summary>Current input of the player for this simulation tick. [ref:0465]</summary>
    public uint Current(int player) => _current[player];
}

/// <summary>
/// Simulation stepping (without replay speed modes/pause): fixed 64 Hz steps [ref:0466]
/// caught up without limit, at most one render per iteration and only when a step was due.
/// <code>
/// target = 2*ticksAvail; if (target − prevTarget &lt; timer64 − prevTimer64) target++;
/// skipRender = stepsDone &gt; target;
/// while (stepsDone &lt;= target) { if (stepsDone even) pop(stepsDone&gt;&gt;1); stepsDone++; } [ref:0467]
/// </code>
/// (prevTimer64 starts at timer64 + 10000, prevTarget at 0.)
/// </summary>
public sealed class SimStepper
{
    private int _prevTarget;
    private int? _prevTimer;

    public int StepsDone { get; private set; }   // [ref:0468]
    public int Target { get; private set; }      // [ref:0469]

    /// <summary>One loop iteration; returns true when the original would render a frame.</summary>
    public bool Iterate(int timer64, int ticksAvailable, Action<int> popInputs, Action simTick)
    {
        int prevTimer = _prevTimer ?? timer64 + 10000;
        int target = ticksAvailable * 2;
        if (target - _prevTarget < timer64 - prevTimer) target++;
        _prevTarget = target;
        _prevTimer = timer64;
        Target = target;
        bool skipRender = target < StepsDone;
        while (StepsDone <= target)
        {
            if ((StepsDone & 1) == 0) popInputs(StepsDone >> 1);
            simTick();
            StepsDone++;
        }
        return !skipRender;
    }
}
