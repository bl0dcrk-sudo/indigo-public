using NFS.Core;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

/// <summary>queues, ordering, timer/input queue and stepping. [ref:3087]</summary>
public class SchedulerTests
{
    private static Action<object?> Log(List<string> log, string name) => arg => log.Add($"{name}{arg}");

    [Fact]
    public void Add_SortsByPriority_EqualInInsertionOrder()
    {
        var log = new List<string>();
        var q = new TaskQueue(16);
        // per car, in car order: physics/drive share prio 0x1E → drive first, then newton [ref:3088]
        for (int car = 0; car < 2; car++)
        {
            q.Add(Log(log, "track"), car, TaskPrio.TrackUpdate);
            q.Add(Log(log, "drv"), car, TaskPrio.Driver);
            q.Add(Log(log, "drive"), car, TaskPrio.Drive);
            q.Add(Log(log, "newton"), car, TaskPrio.Physics);
            q.Add(Log(log, "coll"), car, TaskPrio.Collisions);
            q.Add(Log(log, "post"), car, TaskPrio.PostUpdate);
        }
        q.Run();
        Assert.Equal(["track0", "track1", "drv0", "drv1", "drive0", "newton0", "drive1", "newton1",
                      "coll0", "coll1", "post0", "post1"], log);
        Assert.Equal(12, q.Count);
    }

    [Fact]
    public void Remove_ShiftsDown_AndNotFoundDropsLastTask()
    {
        var log = new List<string>();
        var q = new TaskQueue(8);
        var a = Log(log, "a"); var b = Log(log, "b"); var c = Log(log, "c");
        q.Add(a, 1, 5); q.Add(b, 1, 6); q.Add(c, 1, 7);
        Assert.True(q.Remove(b, 1));
        q.Run();
        Assert.Equal(["a1", "c1"], log);
        // original quirk: no match → Count-- anyway, the last entry disappears [ref:3089]
        Assert.False(q.Remove(b, 2));
        Assert.Equal(1, q.Count);
        log.Clear(); q.Run();
        Assert.Equal(["a1"], log);
        // re-adding after removals behaves like a clean queue
        q.Add(c, 9, 0); q.Add(b, 9, 5);
        log.Clear(); q.Run();
        Assert.Equal(["c9", "a1", "b9"], log);
        Assert.False(new TaskQueue(2).Remove(a, null));   // empty → no-op
    }

    [Fact]
    public void Run_SeesTasksAddedDuringRun()
    {
        var log = new List<string>();
        var q = new TaskQueue(8);
        q.Add(_ => { log.Add("first"); q.Add(Log(log, "late"), null, 9); }, null, 1);
        q.Run();
        Assert.Equal(["first", "late"], log);   // Count is re-read every iteration
    }

    [Fact]
    public void RaceTicker_SimOnEvenTicksBeforeFrame()
    {
        var log = new List<string>();
        var t = new RaceTicker();
        t.Sim.Add(_ => log.Add($"sim{t.RaceTick}"), null, 6);
        t.Frame.Add(_ => log.Add($"frame{t.RaceTick}"), null, 0x1E);
        t.OnSoundFrame = () => log.Add($"snd{t.RaceTick}");
        for (int i = 0; i < 4; i++) t.Tick();
        Assert.Equal(["sim0", "frame0", "frame1", "snd2", "sim2", "frame2", "frame3", "snd4"], log);
    }

    [Fact]
    public void RaceTicker_CountdownAndStart()
    {
        var stages = new List<(int tick, int stage)>();
        var t = new RaceTicker();
        t.OnCountdown = s => stages.Add((t.RaceTick, s));
        while (t.RaceTick <= 0x200) t.Tick();
        Assert.Equal([(0x140, 3), (0x180, 2), (0x1C0, 1), (0x200, 0)], stages);
        Assert.True(t.RaceStarted);
    }

    [Fact]
    public void GameClock_DividesBy2And4()
    {
        var c = new GameClock();
        int events = 0;
        c.InputTick += () => events++;
        var fired = Enumerable.Range(0, 16).Select(_ => c.Tick128()).ToArray();
        Assert.Equal((16, 8, 4), (c.Timer128, c.Timer64, c.Timer32));
        Assert.Equal(4, events);
        Assert.Equal([3, 7, 11, 15], fired.Select((f, i) => (f, i)).Where(p => p.f).Select(p => p.i));
    }

    [Fact]
    public void InputQueue_DelaysInputByFourTicks()
    {
        var q = new InputQueue { NumPlayers = 2 };
        q.Prefill();
        Assert.Equal(3, q.TicksAvailable);
        // input sampled at 32 Hz ticks 0.. lands in slots 4..
        for (uint i = 0; i < 10; i++) { Assert.True(q.Push(0, 100 + i)); Assert.True(q.Push(1, 200 + i)); }
        Assert.Equal(3 + 20, q.TicksAvailable);
        var seen = new List<uint>();
        for (int tick = 0; tick < 8; tick++) { q.Pop(tick); seen.Add(q.Current(0)); }
        Assert.Equal([0u, 0, 0, 0, 100, 101, 102, 103], seen);
        Assert.Equal(203u, q.Current(1));
        Assert.Equal(8, q.ReadIndex);
    }

    [Fact]
    public void InputQueue_RefusesOccupiedSlot()
    {
        var q = new InputQueue();
        q.Prefill();
        for (int i = 0; i < InputQueue.Slots - InputQueue.PrefillTicks; i++) Assert.True(q.Push(0, 1));
        Assert.False(q.Push(0, 2));      // wrapped onto prefill slot 0, not consumed yet
        q.Pop(0);
        Assert.True(q.Push(0, 3));
    }

    [Fact]
    public void InputQueue_ReplayPrefillOnlyResetsAvailability()
    {
        var q = new InputQueue();
        q.Prefill();
        q.Prefill(replayPlayback: true);
        Assert.Equal(0, q.TicksAvailable);
    }

    [Fact]
    public void SimStepper_RunsTwoStepsPerInputTick()
    {
        var q = new InputQueue();
        q.Prefill();
        var s = new SimStepper();
        var pops = new List<int>();
        int ticks = 0;
        // first iteration: 3 inputs available → target 6 → steps 0..6, pops 0..3
        Assert.True(s.Iterate(0, q.TicksAvailable, t => { pops.Add(t); q.Pop(t); }, () => ticks++));
        Assert.Equal(7, ticks);
        Assert.Equal([0, 1, 2, 3], pops);
        // nothing new and the 64 Hz timer did not move → no step, no render
        Assert.False(s.Iterate(0, q.TicksAvailable, t => pops.Add(t), () => ticks++));
        Assert.Equal(7, ticks);
        // timer advanced by one 64 Hz tick → one extra step allowed (target+1 = 7)
        Assert.True(s.Iterate(1, q.TicksAvailable, t => pops.Add(t), () => ticks++));
        Assert.Equal(8, ticks);
        // one more input → target 8 → step 8 pops tick32 4
        q.Push(0, 0x55);
        Assert.True(s.Iterate(1, q.TicksAvailable, t => { pops.Add(t); q.Pop(t); }, () => ticks++));
        Assert.Equal(9, ticks);
        Assert.Equal(4, pops[^1]);
        Assert.Equal(0x55u, q.Current(0));
    }

    [Fact]
    public void PhysLod_RowThreeByDistance()
    {
        var t = GameTables.FromConstants();
        Assert.True(PhysLod.Timestep(1, 3, 0, 5, 100, out var st, t));                       // player / near: every tick
        Assert.Equal(new PhysLod.Step(32, 1, 0x800), st);
        PhysLod.Timestep(1, 3, 64 * 0x10000, 0, 100, out st, t);                               // 64 m → level 5
        Assert.Equal(new PhysLod.Step(16, 2, 0x1000), st);
        PhysLod.Timestep(1, 3, 1000 * 0x10000, 0, 100, out st, t);                             // far → level 4, band clamped to 19
        Assert.Equal(new PhysLod.Step(8, 4, 0x2000), st);
        // level 4 (mask 3): runs when ((tick/2 + id) & 3) == 3
        var runs = Enumerable.Range(0, 16).Where(k => PhysLod.Timestep(1, 3, 200 * 0x10000, 2, k, out _, t)).ToArray();
        Assert.Equal([0, 2, 3, 10, 11], runs);     // tick 0: mask forced to 0 → always; then tick/2 ∈ {1, 5}
    }
}
