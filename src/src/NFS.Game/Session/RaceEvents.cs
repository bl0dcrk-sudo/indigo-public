using NFS.Core;
using NFS.Game.Race;

namespace NFS.Game.Session;

/// <summary>One-time race occurrence (countdown beep, impact, horn request, finish). <see cref="EventId"/> is unique and increasing
/// per race; <see cref="SnapshotSequence"/> = the frame published at the end of the step that produced it. Durable consequences are
/// also present in <see cref="RaceViewFrame"/> (e.g. <see cref="RaceViewFrame.Results"/>), so a lost event never loses state.</summary>
public abstract record RaceEvent(int RaceId, long EventId, long SnapshotSequence, int SimulationTick64);

/// <summary>countdown sample 0..3 ("3", "2", "1", "GO") at tick 0x140/0x180/0x1C0/0x200. [ref:2415]</summary>
public sealed record CountdownBeepEvent(int RaceId, long EventId, long SnapshotSequence, int SimulationTick64, int Sample)
    : RaceEvent(RaceId, EventId, SnapshotSequence, SimulationTick64);

/// <summary>An impact strong enough to be heard (+0x168 &gt; 0xA0000, shake idle), as read before the impact state is [ref:2416]
/// cleared: +0x16C ImpactType, +0x168 Impact, +0x2CC UpDotN, LastHit position. [ref:2417]</summary>
public sealed record CarImpactEvent(int RaceId, long EventId, long SnapshotSequence, int SimulationTick64,
    int CarId, int ImpactType, int Impact, int UpDotN, Vec3 Position)
    : RaceEvent(RaceId, EventId, SnapshotSequence, SimulationTick64);

/// <summary>Horn pattern (psycho / traffic horn): pattern count, length and gap in sound frames. [ref:2418]</summary>
public sealed record HornRequestEvent(int RaceId, long EventId, long SnapshotSequence, int SimulationTick64,
    int CarId, int Count, int Length, int Gap)
    : RaceEvent(RaceId, EventId, SnapshotSequence, SimulationTick64);

/// <summary>The host captured the final result (emitted exactly once).</summary>
public sealed record RaceFinishedEvent(int RaceId, long EventId, long SnapshotSequence, int SimulationTick64, RaceResults Results)
    : RaceEvent(RaceId, EventId, SnapshotSequence, SimulationTick64);

/// <summary>
/// Client-side join of the event channel with the frame channel: drops duplicates and events of another race, and hands out the
/// events of each presented frame in <see cref="RaceEvent.EventId"/> order. No ordering between the two channels is assumed.
/// Memory is bounded (stage 5a.3): at most <paramref name="maxTracked"/> event ids are remembered above a floor; an id at or below
/// the floor counts as already seen. With <paramref name="expiryTicks64"/> a one-shot sound event older than that many 64 Hz ticks
/// behind the presented frame is dropped instead of being played late; <see cref="RaceFinishedEvent"/> never expires.
/// </summary>
public sealed class RaceEventInbox(int raceId, int maxTracked = RaceEventInbox.DefaultMaxTracked, int? expiryTicks64 = null)
{
    public const int DefaultMaxTracked = 1024;

    readonly SortedDictionary<long, RaceEvent> _pending = [];
    readonly SortedSet<long> _seen = [];
    long _floor;

    /// <summary>Events dropped as duplicates, as belonging to another race or as older than the remembered window.</summary>
    public int Rejected { get; private set; }
    /// <summary>Events dropped by <c>expiryTicks64</c> instead of being handed out.</summary>
    public int Expired { get; private set; }
    /// <summary>Remembered ids (bounded by <c>maxTracked</c>) plus undelivered events (diagnostics).</summary>
    public int Tracked => _seen.Count;
    public int Pending => _pending.Count;

    public void Accept(IEnumerable<RaceEvent> events)
    {
        foreach (var e in events)
        {
            if (e.RaceId != raceId || e.EventId <= _floor || !_seen.Add(e.EventId))
            {
                Rejected++;
                continue;
            }
            _pending[e.EventId] = e;
            while (_seen.Count > maxTracked)
            {
                long min = _seen.Min;
                _seen.Remove(min);
                _floor = min;
                // an undelivered event below the new floor is kept only when it must never be lost (the result)
                if (_pending.TryGetValue(min, out var old) && old is not RaceFinishedEvent) { _pending.Remove(min); Expired++; }
            }
        }
    }

    /// <summary>Events belonging to frames up to and including <paramref name="snapshotSequence"/>, oldest first.
    /// <paramref name="simulationTick64"/> = tick of the presented frame (used only with an expiry).</summary>
    public IReadOnlyList<RaceEvent> TakeThrough(long snapshotSequence, int? simulationTick64 = null, int? notAfterTick64 = null)
    {
        List<RaceEvent>? taken = null;
        List<long>? expired = null;
        foreach (var e in _pending.Values)
        {
            if (e.SnapshotSequence > snapshotSequence) continue;
            if (notAfterTick64 is { } limit && e.SimulationTick64 > limit) continue;
            if (expiryTicks64 is { } exp && simulationTick64 is { } now && e is not RaceFinishedEvent && now - e.SimulationTick64 > exp)
            {
                (expired ??= []).Add(e.EventId);
                continue;
            }
            (taken ??= []).Add(e);
        }
        if (expired != null)
            foreach (var id in expired) { _pending.Remove(id); Expired++; }
        if (taken == null) return [];
        foreach (var e in taken) _pending.Remove(e.EventId);
        return taken;
    }
}
