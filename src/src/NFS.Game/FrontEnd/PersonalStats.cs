using System.Buffers.Binary;
using NFS.Core.Constants;

namespace NFS.Game.FrontEnd;

/// <summary>Personal stats entry (20 B, `FEDATA/PC/STATS/PRH`): s16 car (+0), s16 laps (+2),
/// s16 crashes (+4), 2 B padding, s32 lap time (+8), s32 total time (+0xC), s32 top speed (+0x10).
/// Decoded from the fields +4, +8, +0xC, +0x10 (the four chart types) and from .SSF files. [ref:1935]
/// (row "car, laps, crashes, lap time, total time, speed").</summary>
public readonly record struct PersonalStatRecord(short Car, short Laps, short Crashes, int LapTime, int TotalTime, int TopSpeed);

/// <summary>Personal stats file: 451 records × 20 B (450 = 15 cars × 30 entries; the last record is the file tail).</summary>
public sealed record PersonalStatsFile(IReadOnlyList<PersonalStatRecord> Records)
{
    public static PersonalStatsFile Parse(byte[] d)
    {
        var r = new PersonalStatRecord[d.Length / PersonalStats.RecordSize];
        for (int i = 0; i < r.Length; i++)
        {
            int o = PersonalStats.RecordSize * i;
            r[i] = new PersonalStatRecord(
                BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o)),
                BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o + 2)),
                BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 8)),
                BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 12)),
                BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 16)));
        }
        return new PersonalStatsFile(r);
    }
}

/// <summary>Personal stats from `FEDATA/PC/STATS/PRH` (frontend/pc/, screen). [ref:1936]
/// File name built from "%s%s%s" (directory, name from the table = prh01..prh08, extension) [ref:1937]
/// where the directory is `FEDATA/PC/STATS/PRH/`, the extension `.STF` for tracks 0..19 and `.SSF` for mirrored tracks 20..27
/// (mirrored track index = track − 20), and the "backwards" flag (config +0x1A) inserts "b" before the name (BPRH01.STF).
/// Entries: a block of 10 records at car number * 30 + laps category * 10 (2 → 0, 4 → 1, otherwise 2). [ref:1938]</summary>
public static class PersonalStats
{
    public const int RecordSize = 20;
    /// <summary>Block offset (car * 0x1E + laps category * 10) * 20 B. [ref:1939]</summary>
    public const int RecordsPerCar = 0x1E, RecordsPerLaps = 10;
    /// <summary>Number of menu items that select a value (track, laps, car, chart type). [ref:1940]</summary>
    public const int ViewCount = 4, CarCount = 12;

    /// <summary>Stats file path; track 0..7 (and mirrored 20..27 → the same index). [ref:1941]</summary>
    public static string FilePath(int track, bool backward)
    {
        int index = (uint)track <= 7 ? track : (uint)(track - 0x14) <= 7 ? track - 0x14 : 0;
        string name = TextConstants.PersonalStatNames[index];
        string ext = (uint)track < 0x14 ? ".STF" : ".SSF";
        return $"FEDATA/PC/STATS/PRH/{(backward ? "B" : "")}{name.ToUpperInvariant()}{ext}";
    }

    /// <summary>Reads the user's copy when it exists, otherwise the game data; [ref:1942]
    /// null when the file is in none of the directories (minimal installation).</summary>
    public static PersonalStatsFile? Load(int track, bool backward, string? gameDir = null, string? statsDir = null) =>
        UserStats.Read(statsDir, FilePath(track, backward), gameDir) is { } d ? PersonalStatsFile.Parse(d) : null;

    /// <summary>Laps category – 2 → 0, 4 → 1, otherwise (8) → 2. [ref:1943]</summary>
    public static int LapsCategory(int laps) => laps == 2 ? 0 : laps == 4 ? 1 : 2;

    /// <summary>Block of 10 entries for car <paramref name="car"/> and laps category (shorter when the file is truncated).</summary>
    public static IReadOnlyList<PersonalStatRecord> Block(PersonalStatsFile? file, int car, int laps)
    {
        if (file == null) return [];
        int first = (Math.Max(car, 0) * RecordsPerCar + LapsCategory(laps) * RecordsPerLaps);
        if (first >= file.Records.Count) return [];
        int count = Math.Min(RecordsPerLaps, file.Records.Count - first);
        return Enumerable.Range(first, count).Select(i => file.Records[i]).ToArray();
    }

    /// <summary>Number of entries = leading records of the block with a lap time ≠ 0 [ref:1944]
    /// (an empty block = no history; the original then draws only the grid).</summary>
    public static IReadOnlyList<PersonalStatRecord> Entries(IReadOnlyList<PersonalStatRecord> block)
    {
        int n = 0;
        while (n < block.Count && n < RecordsPerLaps && block[n].LapTime != 0) n++;
        return n == block.Count ? block : block.Take(n).ToArray();
    }

    /// <summary>Field drawn for the chart type – 0 crashes, 1 lap time, 2 total time [ref:1945]
    /// 3 top speed (default values = crashes, as in the original switch).</summary>
    public static int Value(in PersonalStatRecord record, int view) => view switch
    {
        1 => record.LapTime,
        2 => record.TotalTime,
        3 => record.TopSpeed,
        _ => record.Crashes,
    };

    /// <summary>Chart values for the selected type. [ref:1946]</summary>
    public static IReadOnlyList<int> Values(IReadOnlyList<PersonalStatRecord> entries, int view) =>
        entries.Select(r => Value(r, view)).ToArray();
}
