using NFS.Core;

namespace NFS.Game.Race;

/// <summary>
/// Ranking table (12 × 16 B {car id +0x1E8, distance +0x220, tick +0x224, human flag}). [ref:2305]
/// Rebuilt from scratch on every 64 Hz tick: clears it, then inserts each racer (distance descending, [ref:2306]
/// equal distance → earlier last-progress tick first) and turns the index into +0x278. [ref:2307]
/// </summary>
public sealed class RaceOrder
{
    public const int Capacity = 12;
    private readonly int[] _id = new int[Capacity], _dist = new int[Capacity], _tick = new int[Capacity];
    private readonly int _count;

    public RaceOrder(int racers) { _count = racers; Reset(); }

    /// <summary>Initial value of an entry: id −1, distance 0xFFFE7961, tick 0. [ref:2308]</summary>
    public void Reset()
    {
        for (int i = 0; i < Capacity; i++) { _id[i] = -1; _dist[i] = unchecked((int)0xFFFE7961); _tick[i] = 0; }
    }

    /// <summary>The insertion loop at the end of the ranking pass. [ref:2309]</summary>
    public void Insert(CarObj c)
    {
        for (int j = 0; j < _count; j++)
        {
            if (_dist[j] < c.DistanceSlices || (_dist[j] == c.DistanceSlices && c.LastProgressTick < _tick[j]))
            {
                for (int k = _count - 2; k >= j; k--) { _id[k + 1] = _id[k]; _dist[k + 1] = _dist[k]; _tick[k + 1] = _tick[k]; }
                _id[j] = c.PlayerIndex; _dist[j] = c.DistanceSlices; _tick[j] = c.LastProgressTick;
                return;
            }
        }
    }

    /// <summary>distance of the first ranked entry that has a tick (+8 ≠ 0) and whose car is not an [ref:2310]
    /// observer; false when there is none (the original then reads past the table).</summary>
    public bool TryLeaderDistance(Func<int, bool> isObserver, out int distance)
    {
        for (int i = 0; i < _count; i++)
            if (_tick[i] != 0 && _id[i] >= 0 && !isObserver(_id[i])) { distance = _dist[i]; return true; }
        distance = 0;
        return false;
    }

    /// <summary>1-based index of the last entry with the car's id (0 when absent). [ref:2311]</summary>
    public int PositionOf(CarObj c)
    {
        int pos = 0;
        for (int i = 0; i < _count; i++) if (_id[i] == c.PlayerIndex) pos = i + 1;
        return pos;
    }
}

/// <summary>Frame-queue priority 0x19; racers only, every 64 Hz tick. [ref:2312]</summary>
public static class RacerLapTask
{
    /// <summary>Zeroes +0x220..+0x280 and sets +0x234 = 0x200. [ref:2313]</summary>
    public static void StatsReset(CarObj c)
    {
        c.DistanceSlices = 0; c.LastProgressTick = 0; c.LastSlice = 0; c.LapsDone = 0; c.LapStartTick = 0x200;
        Array.Clear(c.LapTimes); Array.Clear(c.LapMaxSpeed);
        c.CrashCount = 0; c.RaceState = 0; c.RacePosition = 0;
    }

    /// <param name="model">car model index (**+0x21C); 0x24 = observer, excluded from the ranking.</param>
    /// <param name="modelVmax">Per-model speed cap (15 entries). [ref:2314]</param>
    /// <param name="aiTopSpeed">CarTraits.AiTopSpeed of this car; null = <paramref name="modelVmax"/>[model].</param>
    /// <param name="rand">Random source, used only for opponents at the model's speed cap. [ref:2315]</param>
    public static void Run(CarObj c, int model, int tick, int numLaps, int nSlices, bool reverse,
        IReadOnlyList<int> modelVmax, WatcomRandom rand, RaceOrder order, int? aiTopSpeed = null)
    {
        int speed = c.LocalVel.Z; // +0x2B8
        if (c.LapsDone < 8 && c.LapMaxSpeed[c.LapsDone] < speed)
        {
            int stored = speed;
            if ((((byte)c.Flags) & 8) != 0 && model < 0xF)   // opponent below the 15 model caps (net mode < 2)
            {
                int vmax = aiTopSpeed ?? modelVmax[model];
                int cap = vmax + rand.Next() * -3;
                if (cap < speed) stored = vmax + rand.Next() * -3;
            }
            c.LapMaxSpeed[c.LapsDone] = stored;
        }
        if (c.Lap != c.LapsDone && c.RaceState != 2)
        {
            c.LapTimes[c.LapsDone & 7] = tick - c.LapStartTick;      // lap index bounded by the 8-entry table in the original layout
            c.LapStartTick = tick;
            c.LapsDone = c.Lap;
            if (c.Lap == numLaps && model != 0x24)
            {
                c.LastProgressTick = tick;
                c.RaceState = 2;
                c.LapStartTick -= 0x200;
                c.DistanceSlices = (c.LapsDone + 1) * nSlices;
            }
        }
        if (c.Trk.Slice != c.LastSlice)
        {
            c.LastSlice = c.Trk.Slice;
            if (c.RaceState != 2)
            {
                c.LastProgressTick = tick;
                c.DistanceSlices = (c.LapsDone - c.LapOffset) * nSlices + (reverse ? nSlices - c.LastSlice : c.LastSlice);
            }
        }
        if (model != 0x24) order.Insert(c);
    }
}
