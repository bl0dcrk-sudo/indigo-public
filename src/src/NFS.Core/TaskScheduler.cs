namespace NFS.Core;

/// <summary>
/// Task queue. [ref:0602]
/// entries {prio, fn, arg} kept sorted by ascending priority by insertion; equal priorities run in insertion order.
/// The race uses two queues of capacity 0x90: the sim queue (32 Hz) [ref:0603]
/// and the frame queue (64 Hz), see <see cref="RaceTicker"/>. [ref:0604]
/// </summary>
public sealed class TaskQueue
{
    /// <summary>Priority of an empty slot. [ref:0605]</summary>
    public const int EmptyPrio = 0x7FFE;

    /// <summary>Capacity used for both race queues. [ref:0606]</summary>
    public const int RaceCapacity = 0x90;

    public readonly record struct Entry(int Prio, Action<object?>? Fn, object? Arg);

    private readonly Entry[] _e;

    public int Capacity { get; }
    public int Count { get; private set; }

    public TaskQueue(int capacity = RaceCapacity)
    {
        Capacity = capacity;
        _e = new Entry[capacity + 1];   // the original allocates cap*16+0x18 bytes: one spare slot (written by remove)
        for (int i = 0; i < _e.Length; i++) _e[i] = new Entry(EmptyPrio, null, null);
    }

    public Entry this[int i] => i >= 0 && i < Count ? _e[i] : throw new ArgumentOutOfRangeException(nameof(i));

    /// <summary>Count++, then the first slot whose prio is strictly greater than [ref:0607]
    /// <paramref name="prio"/> (empty slots have 0x7FFE) receives the task, later entries shift up.</summary>
    public void Add(Action<object?> fn, object? arg, int prio)
    {
        if (Count >= Capacity) throw new InvalidOperationException("task queue full");   // original: no check
        Count++;
        int pos = 0;
        for (int i = 0; i < Count; i++)
        {
            pos = i;
            if (prio < _e[i].Prio)
            {
                for (int k = Count - 1; k > i; k--) _e[k] = _e[k - 1];
                break;
            }
        }
        _e[pos] = new Entry(prio, fn, arg);
    }

    /// <summary>
    /// removes the first entry with the same (fn, arg) and shifts the rest down. [ref:0608]
    /// Original quirk kept: when nothing matches, Count is still decremented (the last task is dropped).
    /// Returns whether a match was found. No-op when the queue is empty.
    /// </summary>
    public bool Remove(Action<object?> fn, object? arg)
    {
        if (Count == 0) return false;
        bool found = false;
        for (int i = 0; i < Count; i++)
        {
            if (!Equals(_e[i].Fn, fn) || !Equals(_e[i].Arg, arg)) continue;
            for (int k = i; k < Count - 1; k++) _e[k] = _e[k + 1];
            found = true;
            break;
        }
        _e[Count] = _e[Count] with { Prio = EmptyPrio };   // writes the slot past the end, as the original
        Count--;
        _e[Count] = new Entry(EmptyPrio, null, null);      // original leaves a stale copy here; never observable
        return found;
    }

    /// <summary>calls fn(arg) for every non-null entry in order (Count re-read each step). [ref:0609]</summary>
    public void Run()
    {
        for (int i = 0; i < Count; i++)
        {
            var e = _e[i];
            e.Fn?.Invoke(e.Arg);
        }
    }
}

/// <summary>
/// One 64 Hz simulation step. On even ticks the 32 Hz sim queue runs <b>before</b> the [ref:0610]
/// frame queue of the same tick. Hooks stand in for systems not implemented yet (sound, ranking, …).
/// </summary>
public sealed class RaceTicker
{
    public const int StartTick = 0x200;          // set when tick reaches 0x200 (8 s) [ref:0611]
    public static readonly int[] LightTicks = [0x140, 0x180, 0x1C0];

    public TaskQueue Sim { get; } = new();       // (32 Hz) [ref:0612]
    public TaskQueue Frame { get; } = new();     // (64 Hz) [ref:0613]

    /// <summary>(64 Hz). [ref:0614]</summary>
    public int RaceTick { get; set; }
    public bool RaceStarted { get; set; }        // [ref:0615]
    private int _lightStage;                     // last countdown sample played (3,2,1,0) [ref:0616]

    /// <summary>Countdown sound: stage 3 at ≥0x140, 2 at ≥0x180, 1 at ≥0x1C0, 0 at start (index 0..3). [ref:0617]</summary>
    public Action<int>? OnCountdown;
    public Action? OnPreTick;                    // [ref:0618]
    public Action? OnBeforeSim;                  // (even ticks, before the sim queue) [ref:0619]
    public Action? OnAfterSim;                   // [ref:0620]
    public Action? OnSoundFrame;                 // (when the incremented tick is even) [ref:0621]
    public Action? OnRankAndGlue;                // (every tick, last) [ref:0622]

    public void Tick()
    {
        if (!RaceStarted)
        {
            if (RaceTick >= StartTick)
            {
                RaceStarted = true;
                if (_lightStage != 0) { OnCountdown?.Invoke(0); _lightStage = 0; }
            }
            else if (RaceTick >= LightTicks[2]) { if (_lightStage != 1) { OnCountdown?.Invoke(1); _lightStage = 1; } }
            else if (RaceTick >= LightTicks[1]) { if (_lightStage != 2) { OnCountdown?.Invoke(2); _lightStage = 2; } }
            else if (RaceTick >= LightTicks[0]) { if (_lightStage != 3) { OnCountdown?.Invoke(3); _lightStage = 3; } }
        }
        OnPreTick?.Invoke();
        if ((RaceTick & 1) == 0)
        {
            OnBeforeSim?.Invoke();
            Sim.Run();
            OnAfterSim?.Invoke();
        }
        Frame.Run();
        RaceTick++;
        if ((RaceTick & 1) == 0) OnSoundFrame?.Invoke();
        OnRankAndGlue?.Invoke();
    }
}

/// <summary>Task priorities used by the race (the task order of the original's table). [ref:0623]</summary>
public static class TaskPrio
{
    public const int TrackUpdate = 6;     // sim: +0x36C [ref:0624]
    public const int Driver = 0x15;       // sim: +0x374 [ref:0625]
    public const int LapTask = 0x19;      // frame: +0x378, flag 1 [ref:0626]
    public const int Drive = 0x1E;        // sim: +0x37C [ref:0627]
    public const int Physics = 0x1E;      // sim: +0x380; frame: +0x384 [ref:0628]
    public const int Collisions = 0x28;   // sim: +0x38C [ref:0629]
    public const int PostUpdate = 0x32;   // sim: +0x390 [ref:0630]
}
