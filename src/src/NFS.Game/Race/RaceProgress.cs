using NFS.Core;

namespace NFS.Game.Race;

/// <summary>Live view of fields; no independent lap clock or counter. [ref:2144]</summary>
public sealed class LapState(CarObj car, int numLaps)
{
    public int LapsDone => car.LapsDone;
    public int LapStart => car.LapStartTick;
    public IReadOnlyList<int> LapTimes => car.LapTimes.Take(Math.Clamp(car.LapsDone, 0, 8)).ToArray();
    public int State => car.RaceState;
    public int RaceTime => State != 0 ? car.LapStartTick : 0;
    public int Position => car.DistanceSlices;
    public int ProgressTick => car.LastProgressTick;
    public int FinishTick => State == 2 && car.LapsDone == numLaps ? car.LastProgressTick : 0;
}

/// <summary>Facade over CarObj lap/ranking fields, plus the offline finish and abort logic. [ref:2145]</summary>
public sealed class RaceProgress
{
    /// <summary>Updates the lap count and progress of one car from its slice change. [ref:2146]
    /// The original uses fixed 50/500 slice windows, even for a different track length.</summary>
    public static void UpdateCarProgress(CarObj c, int slices, bool reverse)
    {
        int current = c.Trk.Slice, previous = c.PrevSlice;
        if (current != previous)
        {
            bool wrong = reverse ? current >= 0 && current <= 50 && previous > 500
                : current > 500 && previous == 0;
            bool crossed = reverse ? previous <= 50 && current > 500
                : current >= 0 && current <= 50 && previous > 500;
            if (wrong) c.LapOffset++;
            else if (crossed)
            {
                if (c.LapOffset > 0) c.LapOffset--;
                else c.Lap++;
            }
            c.PrevSlice = (ushort)current;
        }
        c.Progress = c.LapOffset > 0 ? (ushort)0
            : (ushort)(c.Lap * slices + (reverse ? slices - current : current));
    }

    public const int StartTick = 0x200;
    public const int FinishDelay = 0x140;
    private readonly CarObj[] _cars;
    private readonly bool[] _human;
    private readonly int[] _models;
    private readonly int _slices;
    public int NumLaps { get; }
    public IReadOnlyList<LapState> Cars { get; }
    public bool RaceOver { get; private set; }

    public RaceProgress(int nSlices, int numLaps, IReadOnlyList<CarObj> cars,
        IReadOnlyList<bool> human, IReadOnlyList<int> models)
    {
        _slices = nSlices; NumLaps = numLaps;
        _cars = cars.ToArray(); _human = human.ToArray(); _models = models.ToArray();
        Cars = _cars.Select(c => new LapState(c, numLaps)).ToArray();
    }

    /// <summary>Called on even ticks, after the sim tasks. [ref:2147]
    /// All non-observer humans must have finished strictly more than 0x140 ticks ago (offline).</summary>
    public void CheckFinish(int tick)
    {
        if (RaceOver) return;
        int humans = 0;
        for (int i = 0; i < _cars.Length; i++)
        {
            if (!_human[i] || _models[i] == 0x24) continue;
            humans++;
            if (_cars[i].RaceState != 2 || unchecked(_cars[i].LastProgressTick + FinishDelay) >= tick) return;
        }
        if (humans == 0) return;
        RaceOver = true;
        EndMark();
    }

    // (2), natural offline finish: humans already finished; estimate remaining opponents. [ref:2148]
    // The original's 32-bit product keeps only the low 32 bits; the clock is LAST PROGRESS, not the current race tick.
    private void EndMark()
    {
        for (int i = 0; i < _cars.Length; i++)
        {
            var c = _cars[i];
            if (_human[i])
            {
                if (_models[i] == 0x24)
                {
                    if (c.DistanceSlices < unchecked(_slices * NumLaps)) Estimate(c);
                    c.RaceState = 2;
                }
            }
            else if ((c.Flags & CarFlags.Racer) != 0 && c.RaceState != 2) // walks only humans and racing opponents (never traffic) [ref:2149]
            {
                Estimate(c);
                c.RaceState = 2;
            }
        }
    }

    /// <summary>(1) from the pause menu (choice 3): unfinished humans and racers get an [ref:2150]
    /// estimated time distance*(last progress-0x200)/progress, then everybody is marked aborted (state 1); in a
    /// tournament (race type 1) only the local human (never one who already finished) is aborted.</summary>
    public void AbortRace(int raceType)
    {
        int humanOrdinal = 0;
        for (int i = 0; i < _cars.Length; i++)
        {
            if (!_human[i]) continue;
            var c = _cars[i];
            bool local = humanOrdinal++ == 0;
            if (_models[i] == 0x24)
            {
                if (c.DistanceSlices < unchecked(_slices * NumLaps)) Estimate(c);
            }
            else if (c.RaceState != 2) Estimate(c);
            if (_models[i] == 0x24) { c.RaceState = 1; continue; }
            if (c.RaceState != 2 && (raceType != 1 || local)) c.RaceState = 1;
            if (raceType == 1 && (c.RaceState == 2 || !local)) continue;
            c.RaceState = 1;
        }
        for (int i = 0; i < _cars.Length; i++)
        {
            var c = _cars[i];
            if (_human[i] || (c.Flags & CarFlags.Racer) == 0) continue;
            if (c.RaceState != 2) Estimate(c);
            c.RaceState = 1;
        }
    }

    private void Estimate(CarObj c) => c.LapStartTick = c.DistanceSlices == 0 ? 0
        : unchecked(_slices * NumLaps * (c.LastProgressTick - StartTick)) / c.DistanceSlices;

    /// <summary>Uses the place assigned by RaceOrder. [ref:2151]</summary>
    public int[] Ranking() => Enumerable.Range(0, _cars.Length)
        .Where(i => _models[i] != 0x24 && (_cars[i].Flags & CarFlags.Racer) != 0)
        .OrderBy(i => _cars[i].RacePosition == 0 ? int.MaxValue : _cars[i].RacePosition).ToArray();
    public int PlaceOf(int i) => _cars[i].RacePosition;
}
