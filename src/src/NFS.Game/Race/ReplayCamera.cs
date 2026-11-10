using NFS.Core;

namespace NFS.Game.Race;

/// <summary>Replay camera director (state machine and camera switching events). [ref:2316]
/// Only what is observable in the simulation is modelled: each camera change during a replay (state 2, replay playing) draws once from the [ref:2317]
/// shared random value (value &gt;&gt; 11 of the range → look-at field +0x50 = 0x547A / -0x5486 / 0). [ref:2318]
/// Cycle (idx 0..5, table = 4,6,7,5,6,1; entries 1 and 4 become 6 with an upright car or 2 when car+0x288 != 0): [ref:2319]
/// the current mode lasts 0x140 ticks (0x280 for modes 5/6), then the next index starts. For modes 5/6 the switch waits (flag set) [ref:2320]
/// until the camera itself moves: it jumps to another slice or sees a new station. [ref:2321]</summary>
public sealed class ReplayCamera
{
    /// <summary>Default mode cycle, as in the original executable. [ref:2322]</summary>
    public static readonly int[] DefaultModes = { 4, 6, 7, 5, 6, 1 };
    /// <summary>Speed thresholds per track speed index, as in the original executable. [ref:2323]</summary>
    public static readonly int[] TrackSpeeds = { 0x40000, 0xA0000, 0x140000, 0x280000, 0x3C0000, 0x500000, 0x700000, 0x8C0000 };
    /// <summary>Slice interval per speed index, as in the original executable. [ref:2324]</summary>
    public static readonly int[] TrackTicks = { 0x40, 0x20, 0x10, 8, 6, 4, 3, 2 };

    readonly WatcomRandom _rand;
    readonly int[] _modes = (int[])DefaultModes.Clone();
    int _idx;            // [ref:2325]
    bool _waiting;       // [ref:2326]
    int _lastChange;     // value -0x200 at the last change [ref:2327]
    int _tvStation = -1; // [ref:2328]
    int _counter, _trackIdx, _trackSlice; // 4C3218 / 4C3210 [ref:2329]
    int _sliceCount;

    /// <summary>Current camera mode (the argument of the last change). [ref:2330]</summary>
    public int Mode { get; private set; }
    /// <summary>Look-at field +0x50, set to 0x547A, -0x5486 or 0. [ref:2331]</summary>
    public int Field50 { get; private set; }
    /// <summary>Number of draws made by the camera. [ref:2332]</summary>
    public int Draws { get; private set; }
    /// <summary>Number of camera changes (calls) so far; presentation applies a new <see cref="Mode"/> when it grows. [ref:2333]</summary>
    public int Changes { get; private set; }
    /// <summary>Replay is playing (state 2); without it a camera change draws nothing. [ref:2334]</summary>
    public bool Playing { get; init; } = true;

    public ReplayCamera(WatcomRandom rand) { _rand = rand; Mode = _modes[0]; }

    /// <summary>Called on every tick once the race has started: advances the cycle timer. [ref:2335]</summary>
    public void Cycle(int tick, bool raceStarted, CarObj car, int sliceCount)
    {
        if (!raceStarted) return;
        _sliceCount = sliceCount;
        if (_waiting) return;
        int cur = _modes[_idx];
        int span = cur is 5 or 6 ? 0x280 : 0x140;
        if (span >= (tick - 0x200) - _lastChange) return;
        if (cur is 5 or 6) _waiting = true;
        if (car.WrongWayTicks == 0) { _modes[1] = 6; _modes[4] = 6; }
        else { _modes[1] = 2; _modes[4] = 2; _waiting = false; }
        _idx = (_idx + 1) % 6;
        if (!_waiting) Change(tick, car);
    }

    /// <summary>One camera update (mode 6, mode 5): may end the wait of modes 5/6. [ref:2336]</summary>
    public void Update(int tick, bool raceStarted, CarObj car, int sliceCount)
    {
        _sliceCount = sliceCount;
        if (sliceCount <= 0) return;
        if (Mode == 6) TrackUpdate(tick, raceStarted, car);
        else if (Mode == 5) TvUpdate(tick, car);
    }

    void TrackUpdate(int tick, bool raceStarted, CarObj car)
    {
        if (raceStarted) _counter++;
        if (_counter != TrackTicks[_trackIdx]) return;
        _counter = 0;
        _trackSlice = (_trackSlice + 1) % _sliceCount;
        int speed = car.AiSpeed, d = car.Trk.Slice - _trackSlice, r = speed - TrackSpeeds[_trackIdx];
        int jump = d >= 8 || (d >= 5 && r is >= 1 and <= 0x4FFFF) ? 1
            : d <= -7 || (d <= -5 && r is >= -0x4FFFF and <= -1) ? 2 : 0;
        if (jump == 0) return;
        _trackIdx = SpeedIndex(speed);
        int r2 = speed - TrackSpeeds[_trackIdx];
        int off = jump == 1 ? (r2 < 0x30000 ? 3 : r2 < 0xF0000 ? 5 : 7) : (r2 < -0x7FFFF ? -5 : -3);
        _trackSlice = ((car.Trk.Slice + off) % _sliceCount + _sliceCount) % _sliceCount;
        if (_waiting) Change(tick, car);
    }

    void TvUpdate(int tick, CarObj car)
    {
        int station = ((car.Trk.Slice % _sliceCount + _sliceCount) % _sliceCount + 15) % _sliceCount; // [ref:2337]
        station -= station % 30;
        if (station != _tvStation && _waiting && Playing) { Change(tick, car); return; } // return early, without updating _tvStation [ref:2338]
        _tvStation = station;
    }

    static int SpeedIndex(int speed)
    {
        int idx = 0;
        while (idx < 7 && TrackSpeeds[idx] < speed) idx++;
        return idx;
    }

    /// <summary>Draws the random look-at value and the new mode, then restarts the timer. [ref:2339]</summary>
    void Change(int tick, CarObj car)
    {
        if (Playing)
        {
            int r = _rand.Next();
            Draws++;
            int q = r >> 11;
            Field50 = q < 4 ? (q < 2 ? 0x547A : -0x5486) : 0;
        }
        Mode = _modes[_idx];
        Changes++;
        if (Mode == 6)
        {
            int speed = car.AiSpeed, i = 0;
            while (i < 6 && TrackSpeeds[i + 1] < speed) i++;
            int rem = speed - TrackSpeeds[i];
            int off = rem < 0 ? -3 : rem < 0x30000 ? 3 : rem < 0x140000 ? 5 : 7;
            _trackIdx = i;
            _trackSlice = ((car.Trk.Slice + off) % _sliceCount + _sliceCount) % _sliceCount;
            _counter = 0;
        }
        _waiting = false;
        _lastChange = tick - 0x200;
    }
}
