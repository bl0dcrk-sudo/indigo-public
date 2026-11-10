namespace NFS.Core;

/// <summary>
/// Weather (race option 0x40 = bit 6, set by the RAIN cheat code). [ref:0714]
/// Zones per track (tracks 0, 5, 7 only). [ref:0715]
/// Intensity is 0..1.0 from the slice distance to the nearest zone, 0 in tunnels; it sets the streak level. [ref:0716]
/// Streak records: 40, updated once per [ref:0717]
/// rendered frame. Drawing and updating run in the render path of the original, never in the sim, so [ref:0718]
/// the physics is untouched. The windscreen drops (100 records, the last [ref:0719]
/// intensity*100 of them visible, are in <see cref="Drops"/>. [ref:0720]
/// Split screen (!= 0): <see cref="SecondView"/> keeps the intensity and type of the second [ref:0721]
/// player (per player); both views share the zones and the streak records, 20 records each [ref:0722]
/// (<see cref="VisibleStreaksSplit"/>, <see cref="SplitYRange"/>); no windscreen drops.
/// </summary>
public sealed class Weather
{
    public const int OptRain = 0x40;
    public const int StreakCount = 40;

    /// <summary>Zone record (12 B in the original): centre slice, half width in slices, type (1 = rain, 2/3 = other kinds). [ref:0723]</summary>
    public readonly record struct Zone(int Center, int HalfWidth, int Type);

    /// <summary>One 24-byte record: Y = +0 (top y), Speed = +2, then the line (X0,Y0)-(X1,Y1) at +0x0E..+0x14 [ref:0724]
    /// (render list cmd 0x11).</summary>
    public struct Streak { public short Y, Speed, X0, Y0, X1, Y1; }

    public const int Gray = 0x848484;   // (0xFF848484) [ref:0725]

    private readonly EaRandom _rng;
    private readonly List<Zone> _zones;
    public bool Enabled { get; }
    public int TrackId { get; }
    public IReadOnlyList<Zone> Zones => _zones;
    public Streak[] Streaks { get; }
    /// <summary>Intensity (16.16) and type of the (single) local player. [ref:0726]</summary>
    public int Intensity { get; private set; }
    public int Type { get; private set; }

    public const int DropTotal = 100;

    /// <summary>One windscreen drop (36-byte list record in the original: sprite cmd 0x14, x +8, y +0xC; age kept in a separate byte array). [ref:0727]
    /// <see cref="Large"/> = every 5th (shape mdrn instead of smrn).</summary>
    public struct Drop { public int X, Y; public byte Age; public bool Large; }

    public Drop[] Drops { get; } = new Drop[DropTotal];
    // y base, y range, x base; x range 500 (dash type 5 uses a lower, narrower area)
    private int _yBase, _yRange, _xBase;
    private const int XRange = 500;
    private readonly EaRandom _dropRng = new();   // unused (never drawn) when rain is off
    private const int SnowStep = 0x1E;   // snow movement step [ref:0728]

    public Weather(int gameOptions, int trackId, EaRandom rng, int dashType = -1)
    {
        Enabled = (gameOptions & OptRain) != 0;
        TrackId = trackId;
        _rng = rng;
        _zones = [];
        Streaks = new Streak[StreakCount];
        if (!Enabled) return;
        SetupZones();   // (the zone setup is its only part that touches state besides the allocation) [ref:0729]
        ResetStreaks(); // at race start [ref:0730]
        // own stream (clone of the weather stream at this point) so the streaks keep their sequence
        _dropRng.SetState(rng.State);
        InitDrops(dashType);
    }

    private Weather(Weather first)
    {
        Enabled = first.Enabled;
        TrackId = first.TrackId;
        _rng = first._rng;
        _zones = first._zones;
        Streaks = first.Streaks;
    }

    /// <summary>The second player's weather state in split screen (index 1 of the per-player intensity and type). [ref:0731]
    /// same zones, random stream and streak records as this one, own intensity and type (<see cref="Update"/>).</summary>
    public Weather SecondView() => new(this);

    /// <summary>in split screen (!= 0): after the same streak level test (types 1 and 2, k = trunc(intensity*8/65536) [ref:0732]
    /// &gt; 0) always 20 records, player 1 from index 0, player 2 from index 0x14 (the 40 records are halved, whatever the level).</summary>
    public (int First, int Count) VisibleStreaksSplit(int player)
    {
        if (!Enabled || Type is not (1 or 2) || (int)((long)Intensity * 8 >> 16) == 0) return (0, 0);
        return (player == 0 ? 0 : 0x14, 0x14);
    }

    /// <summary>in split screen: y range 0..0xB4 (player 1) and 0xF0..0x1A4 (player 2) of the whole screen. [ref:0733]</summary>
    public static (int YMin, int YMax) SplitYRange(int player) => player == 0 ? (0, 0xB4) : (0xF0, 0x1A4);

    /// <summary>Drop area by dash type (5: x 150.., y 0x5F..+185; else x 100.., y 0x50..+200, x range [ref:0734]
    /// 500), per drop in order x = rand % 500 + xBase, y = rand % yRange + yBase, age = 0xB4 - rand % 100.</summary>
    private void InitDrops(int dashType)
    {
        if (dashType == 5) { _yBase = 0x5F; _yRange = 0xB9; _xBase = 0x96; }
        else { _yBase = 0x50; _yRange = 200; _xBase = 100; }
        for (int i = 0; i < DropTotal; i++)
        {
            Drops[i].Large = i % 5 == 0;
            Drops[i].X = (int)(_dropRng.Next() % XRange) + _xBase;
            Drops[i].Y = (int)(_dropRng.Next() % (uint)_yRange) + _yBase;
            Drops[i].Age = unchecked((byte)(0xB4 - _dropRng.Next() % 100));
        }
    }

    /// <summary>the last trunc(intensity*100/65536) records are visible (First = 100 - Count). [ref:0735]</summary>
    public (int First, int Count) VisibleDrops()
    {
        if (!Enabled) return (0, 0);
        int n = (int)((long)Intensity * DropTotal >> 16);
        return n == 0 ? (0, 0) : (DropTotal - n, n);
    }

    /// <summary>
    /// Called only with weather on, in the cockpit view (dash active) and without split screen; [ref:0736]
    /// rain (types 1, 3) and snow (types 2, 4) step the drops, not while paused. <paramref name="speed"/> [ref:0737]
    /// (m/s 16.16, negative = 0). Moves the visible drops and returns them; (0, 0) when nothing is drawn.
    /// </summary>
    public (int First, int Count) UpdateDrops(int speed, bool paused = false)
    {
        var (first, count) = VisibleDrops();
        if (count == 0) return (0, 0);
        if (paused) return (first, count);
        int mph = Fx.Mul(Math.Max(speed, 0), 0x23CA5) / 65536;
        bool rain = Type is 1 or 3;
        if (rain || Type is 2 or 4) StepDrops(first, mph, rain);
        return (first, count);
    }

    private void StepDrops(int first, int mph, bool rain)
    {
        int slow = Math.Max(7 - mph / 0x28, 2);
        int limit = rain ? 0xB4 : 100;
        int i = first;
        while (i < DropTotal)
        {
            ref var d = ref Drops[i];
            while (d.Age > limit)
            {
                d.X = (int)(_dropRng.Next() % XRange) + _xBase;
                d.Y = (int)(_dropRng.Next() % (uint)_yRange) + _yBase;
                uint r = _dropRng.Next() % 100;
                d.Age = rain ? unchecked((byte)(0xB4 - r)) : (byte)(100 - r);
                if (++i > DropTotal - 1) return;
                d = ref Drops[i];
            }
            d.Age++;
            if (rain)
            {
                if (mph > 0x50 && d.Age % slow == 0)
                {
                    d.Y -= 1;
                    if (d.X < _yRange) d.X -= 1;
                    if (XRange < d.X) d.X += 1;
                }
                if (mph < 10 && d.Age % 8 == 0) d.Y += 1;
            }
            else if (mph > 0x3C && d.Age % slow == 0)
            {
                d.Y -= SnowStep;
                if (d.X < _yRange) d.X -= SnowStep;
                if (XRange < d.X) d.X += SnowStep;
            }
            i++;
        }
    }

    // half width = rand % 100 + 60; if the range is too narrow, centred with half (hi-lo)/2. [ref:0738]
    private Zone RandomZone(int lo, int hi, int type)
    {
        int half = (int)(_rng.Next() % 100) + 0x3C;
        if (half * 2 > hi - lo) return new Zone((hi + lo) / 2, (hi - lo) / 2, type);
        int span = hi - half * 2 - lo;
        int centre = half + lo + (span == 0 ? 0 : (int)(_rng.Next() % (uint)span));
        return new Zone(centre, half, type);
    }

    // the same call order as in the original [ref:0739]
    private void SetupZones()
    {
        switch (TrackId)
        {
            case 0:
                _zones.Add((_rng.Next() & 1) != 0 ? RandomZone(0x32, 0x1C2, 1) : RandomZone(0x2BC, 0x3E8, 1));
                break;
            case 5:
                _zones.Add(new Zone(0x269, 2, 3));
                _zones.Add(new Zone(0x274, 5, 3));
                uint r = _rng.Next() % 3;
                _zones.Add(r == 0 ? RandomZone(0x32, 0x1F4, 1) : r == 1 ? RandomZone(0x2D5, 0x352, 1) : RandomZone(0x3C0, 0x5AA, 1));
                break;
            case 7:
                _zones.Add(RandomZone(0xC8, 0x17C, 1));
                if ((_rng.Next() & 1) != 0) _zones.Add(RandomZone(0x226, 0x460, 2));
                else { _zones.Add(RandomZone(0x4B0, 0x6D6, 2)); _zones.Add(new Zone(0x6F0, 2, 2)); }
                break;
        }
    }

    /// <summary>the zone whose centre is closest to <paramref name="slice"/> (|d| &lt; 10000, first [ref:0740]
    /// wins ties); no zones (other tracks) returns all zeros.</summary>
    public Zone FindZone(int slice)
    {
        if (_zones.Count == 0) return default;
        int best = 10000, idx = 0;
        for (int i = 0; i < _zones.Count; i++)
        {
            int d = Math.Abs(_zones[i].Center - slice);
            if (d < best) { best = d; idx = i; }
        }
        return _zones[idx];
    }

    /// <summary>
    /// for the player car at <paramref name="slice"/> of <paramref name="numSlices"/>: [ref:0741]
    /// intensity = (half - d) / half in 16.16 (d = circular distance to the zone centre, capped at 1.0), 0 inside a
    /// tunnel (<paramref name="surroundings"/> nibble 2 on either side) or outside the zone; type = zone type when
    /// intensity is not 0. [ref:0742]
    /// </summary>
    public void Update(int slice, int numSlices, byte surroundings)
    {
        int intensity = 0;
        if (!Enabled) { Intensity = 0; Type = 0; return; }
        var z = FindZone(slice);
        if (z.HalfWidth != 0)
        {
            int d = numSlices - z.HalfWidth < z.Center && slice < z.HalfWidth
                ? numSlices + slice - z.Center
                : Math.Abs(slice - z.Center);
            bool tunnel = surroundings >> 4 == 2 || (surroundings & 0xF) == 2;
            if (!tunnel && d < z.HalfWidth)
                intensity = Math.Min((int)(((long)(z.HalfWidth - d) << 16) / z.HalfWidth), 0x10000);
        }
        Intensity = intensity;
        Type = intensity != 0 ? z.Type : 0;
    }

    /// <summary>streak level k = trunc(intensity*8 / 65536) (0..8); k*5 streaks, from index (8-k)*5. [ref:0743]
    /// Only types 1 and 2 draw streaks (type 3 = none).</summary>
    public (int First, int Count) VisibleStreaks()
    {
        if (!Enabled || Type is not (1 or 2)) return (0, 0);
        int k = (int)((long)Intensity * 8 >> 16);
        if (k == 0) return (0, 0);
        int first = (8 - k) * 0x28 / 8;
        return (first, 0x28 - first);
    }

    /// <summary>Streak length (7*camX/65536 + 7)/2 + 2, using the camera +0x28 component (<paramref name="camX"/> [ref:0744]
    /// 16.16); clamp 2..9 for type 1, 2..5 otherwise; 2 without weather.</summary>
    public static int StreakLen(int type, int camX)
    {
        if (type == 0) return 2;
        int v = (int)((long)camX * 7 / 65536);          // truncation toward zero
        int len = (v + 7) / 2 + 2;
        return type == 1 ? Math.Clamp(len, 2, 9) : Math.Clamp(len, 2, 5);
    }

    /// <summary>Random y (rand % 480) and speed ((rand &amp; 7) + 2) for the 40 records. [ref:0745]</summary>
    public void ResetStreaks()
    {
        for (int i = 0; i < StreakCount; i++)
        {
            Streaks[i] = default;
            Streaks[i].Y = (short)(_rng.Next() % 0x1E0);
            Streaks[i].Speed = (short)((_rng.Next() & 7) + 2);
        }
    }

    /// <summary>
    /// One call per drawn frame, single view (y range yMin..yMax = 0..0x168): moves the [ref:0746]
    /// visible records down by 3*speed, respawns them at the top (rand), and rebuilds their lines. Inputs from the camera
    /// of the player: <paramref name="speed"/> = player car +0x2B8 (m/s 16.16; below 50 mph the x positions are evenly
    /// spread, above they are random), <paramref name="camX"/> / <paramref name="camZ"/> = camera +0x28 / +0x30 (16.16)
    /// [hypothesis: components of the camera right vector].
    /// </summary>
    public void UpdateStreaks(int first, int count, int speed, int camX, int camZ, int yMin = 0, int yMax = 0x168)
    {
        if (!Enabled || count <= 0 || Type == 0) return;
        int mph = Fx.Mul(speed, 0x23CA5) / 65536;
        int slant = Fx.Mul(camZ, 0x4000);
        int len = StreakLen(Type, camX);
        int jitter = Math.Max((int)((long)slant * 0x1E0 / 65536), 0);
        bool rain = Type == 1;
        int spacing = (yMax - yMin) / (len * (rain ? 8 : 4) - len) + 1;
        for (int i = 0; i < count; i++)
        {
            ref var s = ref Streaks[first + i];
            int t = rain ? (len - 1) * spacing * s.Speed : s.Speed * ((len - 1) * spacing) * 2;
            int k = rain ? 9 : 5;
            if ((k - len * 2) * spacing + yMin + t < s.Y)
            {
                s.Y = (short)(yMin + (_rng.Next() & 0x1F));
                s.Speed = (short)(rain ? (_rng.Next() & 7) + 2 : (_rng.Next() & 1) + 2);
            }
            short xBase = mph < 0x32 ? (short)((short)(0x280 / count) * (short)i) : (short)(_rng.Next() % 0x280);
            short j = (short)jitter;
            if (rain)
            {
                s.Y0 = s.Y;
                s.Y1 = (short)(s.Speed / 2 + s.Y0);
                s.X0 = (short)((int)((long)s.Y0 * slant / 65536) + xBase - j);
                s.X1 = (short)((int)((long)s.Y1 * slant / 65536) + xBase - j);
            }
            else
            {
                s.Y0 = (short)(s.Y - (short)(_rng.Next() & 1));
                s.Y1 = (short)(s.Y0 + s.Speed);
                s.X0 = (short)((int)((long)s.Y0 * slant / 65536) + xBase - j - 1);
                s.X1 = (short)(s.X0 + (int)((long)slant * 2 / 65536));
            }
            s.Y = (short)(s.Y + s.Speed * 3);
        }
    }
}
