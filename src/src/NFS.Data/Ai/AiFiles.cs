namespace NFS.Data.Ai;

/// <summary>One AI personality (228 B). Fixed = int 16.16. [ref:0950]</summary>
public sealed record Personality(
    byte Id,                     // +0 'a'+i
    byte[] Unknown_0x01,         // +1 3 B
    int[] SpeedGlue,             // +0x04 Fixed[21], index = (Δprogress/15)+10
    int[] AccGlue,               // +0x58 Fixed[21]
    int SpeedFactorAdjust,       // +0xAC
    int HandlingAdjust,          // +0xB0 [ref:0951]
    int BlockLookBehind,         // +0xB4 int (×4 m)
    int BlockLookAhead,          // +0xB8 Fixed m
    int MaxLaneChangeSpeed,      // +0xBC Fixed
    int LaneChangeSpeedFactor,   // +0xC0 Fixed
    int LookAhead,               // +0xC4 int slices
    int[] LaneSlack,             // +0xC8 Fixed[4] m
    int NewSlackProbability,     // +0xD8 int
    int PsychoTriggerCount,      // +0xDC int
    int PsychoTime)              // +0xE0 int ticks
{
    public const int Size = 0xE4;
    public char Letter => (char)Id;

    internal static Personality Read(byte[] d, int o) => new(
        d[o], d.AsSpan(o + 1, 3).ToArray(), Bin.S32Array(d, o + 4, 21), Bin.S32Array(d, o + 0x58, 21),
        Bin.S32(d, o + 0xAC), Bin.S32(d, o + 0xB0), Bin.S32(d, o + 0xB4), Bin.S32(d, o + 0xB8), Bin.S32(d, o + 0xBC),
        Bin.S32(d, o + 0xC0), Bin.S32(d, o + 0xC4), Bin.S32Array(d, o + 0xC8, 4), Bin.S32(d, o + 0xD8), Bin.S32(d, o + 0xDC),
        Bin.S32(d, o + 0xE0));
}

/// <summary>GAMEDATA/SIM/AI/PRSONAL.DAT (3012 B): u8 grid[4][12] personality letters per start slot (rows by game type: [ref:0952]
/// 0 KO, 1 tournament, 2 single race 1 opponent, 3 single race many), then 13 × 228 B personalities.</summary>
public sealed record Prsonal(byte[][] Grid, IReadOnlyList<Personality> Personalities)
{
    public const int Size = 48 + 13 * Personality.Size;

    public static Prsonal Parse(byte[] d)
    {
        Bin.Require(d.Length >= Size, $"PRSONAL: {d.Length} < {Size} B");
        var grid = new byte[4][];
        for (int r = 0; r < 4; r++) grid[r] = d.AsSpan(12 * r, 12).ToArray();
        var p = new Personality[13];
        for (int i = 0; i < 13; i++) p[i] = Personality.Read(d, 48 + Personality.Size * i);
        return new Prsonal(grid, p);
    }

    /// <summary>Looks up a personality by letter 'a'..'m'. [ref:0953]</summary>
    public Personality? ByLetter(char c) => Personalities.FirstOrDefault(p => p.Letter == c);
}

/// <summary>GAMEDATA/SIM/AI/TRAFCFG.DAT (452 B = 113 int LE). Fixed = int 16.16. [ref:0954]
/// Glue factors: 3 groups (single / tournament / knockout) × 24 int = 12 (speed, acc) pairs:
/// Overall, Laps{2,4,8}, Mode{sim,arcade}, Skill{beg,exp}, Traffic{none,some}, Opp{1,many}.</summary>
public sealed record TrafCfg(int[] Values)
{
    public const int Count = 113;
    public const int Size = Count * 4;

    public int OppDesiredAhead => Values[0];            // [0] (dead in code)
    public int Unknown_TrafficDensity => Values[1];     // [1] "NOT USED"
    public int NumTrafficCars => Values[2];             // [2]
    public ReadOnlySpan<int> TrafficSpeedFactors => Values.AsSpan(3, 4);  // [3..6]
    public int TrafficBaseSpeedKmh => Values[7];        // [7] Fixed km/h → ×0x471C
    public ReadOnlySpan<int> TrafficLaneOffsets => Values.AsSpan(8, 4);   // [8..11]
    /// <summary>[12..83]: group 0..2 × 24.</summary>
    public ReadOnlySpan<int> GlueGroup(int group) => Values.AsSpan(12 + 24 * group, 24);
    public int LatVelLookahead => Values[84];
    public int MinLookahead => Values[85];
    public int SimMaxLookahead => Values[86];
    public int SimLookaheadFactor => Values[87];
    public int ArcadeMaxLookahead => Values[88];
    public int ArcadeLookaheadFactor => Values[89];
    public int SkidValue => Values[90];
    /// <summary>Values [91..101], 11 entries. [ref:0955]</summary>
    public ReadOnlySpan<int> InControl => Values.AsSpan(91, 11);
    /// <summary>Values [102..112], 11 entries. [ref:0956]</summary>
    public ReadOnlySpan<int> OutOfControl => Values.AsSpan(102, 11);

    public static TrafCfg Parse(byte[] d)
    {
        Bin.Require(d.Length >= Size, $"TRAFCFG: {d.Length} < {Size} B");
        return new TrafCfg(Bin.S32Array(d, 0, Count));
    }
}

/// <summary>AI speed/line files in GAMEDATA/TRACKS/SPEEDS: TRnn[B].QPS/.QAS (sim and arcade; [ref:0957]
/// unpacked = .PSP/.ASP: 2 B per 2 slices, even byte = target speed m/s, odd byte = 1 unused) and .QPL/.QAL
/// (unpacked.PLN/.ALN: 1 B per slice = preferred lane 4..16, 0 = none). Missing file → 50 m/s. [ref:0958]</summary>
public sealed record AiSpeeds(byte[] Raw)
{
    public const byte DefaultSpeed = 0x32;
    /// <summary>Target speed in m/s, read at byte <c>[(slice/2)*2]</c>. [ref:0959]</summary>
    public int SpeedAt(int slice) => Raw[(slice / 2) * 2];
    public int NumSlices => Raw.Length;

    public static AiSpeeds Parse(byte[] data) => new(EaCompression.UnpackIfCompressed(data));
}

public sealed record AiLine(byte[] Raw)
{
    public int LaneAt(int slice) => Raw[slice];
    public static AiLine Parse(byte[] data) => new(EaCompression.UnpackIfCompressed(data));
}

/// <summary>Parked car (24 B): int model, int slice, int x, y, z (16.16), int heading (0x10000 = 360°).</summary>
public readonly record struct StaticCar(int Model, int Slice, int X, int Y, int Z, int Heading);

/// <summary>GAMEDATA/TRACKS/DATA/PC/SMnn.DAT: int count, count × StaticCar. [ref:0960]
/// count is clamped to at run time. [ref:0961]</summary>
public sealed record StaticCars(IReadOnlyList<StaticCar> Cars)
{
    public static StaticCars Parse(byte[] d)
    {
        int n = Bin.S32(d, 0);
        Bin.Require(n >= 0 && d.Length >= 4 + 24 * n, "SMnn.DAT truncated");
        var r = new StaticCar[n];
        for (int i = 0; i < n; i++)
        {
            int o = 4 + 24 * i;
            r[i] = new StaticCar(Bin.S32(d, o), Bin.S32(d, o + 4), Bin.S32(d, o + 8), Bin.S32(d, o + 12), Bin.S32(d, o + 16), Bin.S32(d, o + 20));
        }
        return new StaticCars(r);
    }
}
