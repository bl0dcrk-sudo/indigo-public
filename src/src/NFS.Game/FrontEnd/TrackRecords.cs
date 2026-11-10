using NFS.ModApi;
using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.Race;

namespace NFS.Game.FrontEnd;

/// <summary>Track records from FEDATA/PC/STATS (frontend/common). [ref:1999]
/// Path: (table of names for ids 0..7 and 20..27; out of range → entry 0 = "oval") [ref:2000]
/// and "%s%s%s" with the extension ".stf". Read: (31 × 20 B). [ref:2001]
/// Lap record = entry 0; [ref:2002]
/// total record = entry 1 for 2 laps, 0xB for 4, 0x15 for the rest. [ref:2003]
/// The time in the file is in ticks of 1/64 s (as LapTimes in CarObj) – it is used as-is. [ref:2004]</summary>
public static class TrackRecords
{
    /// <summary>QFS name of the track description (`FEDATA/PC/ART/TRACK/&lt;name&gt;INFO.QFS`). [ref:2005]</summary>
    public static string InfoArt(int track) => TextConstants.TrackInfoArt[Math.Clamp(track, 0, 7)] + "info";

    /// <summary>QFS name of the records screen (`FEDATA/PC/ART/TRACK/&lt;name&gt;REC.QFS`). [ref:2006]</summary>
    public static string RecordsArt(int track) => TextConstants.TrackRecordsArt[Math.Clamp(track, 0, 7)] + "rec";

    /// <summary>Index of the first .STF entry for a number of laps (2 → 1, 4 → 0xB, otherwise 0x15). [ref:2007]
    /// Rows 0..9 of the records screen are entries RowOffset + row.</summary>
    public static int RowOffset(int laps) => laps == 2 ? 1 : laps == 4 ? 0xB : 0x15;

    /// <summary>Car name from the field <see cref="StatRecord.Car"/>. 0x12 and 0x24 → "Jacques" [ref:2008]
    /// (0xC), above 0xF → TEXT 0x6D, otherwise TEXT 0x5D + number (0x5D McLaren F1 … 0x68 Isdera 112i). [ref:2009]</summary>
    public static string CarName(TextFile text, int car) =>
        car is 0x12 or 0x24 ? TextConstants.SpecialDriverName
        : car > 0xF ? text[0x6D]
        : text[0x5D + Math.Clamp(car, 0, 0xF)];

    /// <summary>Record difficulty label – 2 → 0x1F "arc", 1 → 0x1B "wild", otherwise 0x1E "sim". [ref:2010]</summary>
    public static int SkillText(int skill) => skill == 2 ? 0x1F : skill == 1 ? 0x1B : 0x1E;

    /// <summary>Name from the .STF without padding (the FFN font has no tab glyph). [ref:2011]</summary>
    public static string RecName(StatRecord rec) => rec.Name.TrimEnd();

    /// <summary>id 0..7 → the same name, 20..27 (mirrored track) → id − 20, otherwise 0. [ref:2012]</summary>
    public static int NameIndex(int trackId) =>
        (uint)trackId <= 7 ? trackId : (uint)(trackId - 0x14) <= 7 ? trackId - 0x14 : 0;

    public static string FilePath(int trackId) =>
        $"FEDATA/PC/STATS/{TextConstants.TrackStatNames[NameIndex(trackId)].ToUpperInvariant()}.STF";

    /// <summary>With value 1 ("clear records"): zeroes the `.STF` – 31 entries × 20 B with an empty name [ref:2013] [ref:3421]
    /// (""), car/time/difficulty 0 (620 bytes of zero). [ref:2014]</summary>
    public const int ClearRecordCount = 31;
    public static byte[] ClearedBytes() => new byte[ClearRecordCount * StatsFile.RecordSize];

    /// <summary>(track, 1): writes the cleared `.STF` to the user's directory [ref:2015]
    /// (<see cref="UserStats"/>); the game directory is left untouched.</summary>
    public static void Clear(int trackId, string statsDir) =>
        UserStats.Write(statsDir, FilePath(trackId), ClearedBytes());

    /// <summary>Reads the user's copy when it exists, otherwise the game data; [ref:2016]
    /// null when the file is in none of the directories (drive / minimal installation).</summary>
    public static StatsFile? Load(int trackId, string? gameDir = null, string? statsDir = null) =>
        UserStats.Read(statsDir, FilePath(trackId), gameDir) is { } d ? StatsFile.Parse(d) : null;

    /// <summary>Lap record (entry 0); null when there is no entry or the time = 0 (the game then hides the line).</summary>
    public static StatRecord? Lap(int trackId, string? gameDir = null, string? statsDir = null) =>
        Entry(trackId, 0, gameDir, statsDir);

    /// <summary>Total record for a number of laps (2 → 1, 4 → 0xB, otherwise 0x15). [ref:2017]</summary>
    public static StatRecord? Total(int trackId, int laps, string? gameDir = null, string? statsDir = null) =>
        Entry(trackId, laps == 2 ? 1 : laps == 4 ? 0xB : 0x15, gameDir, statsDir);

    static StatRecord? Entry(int trackId, int index, string? gameDir, string? statsDir)
    {
        if (Load(trackId, gameDir, statsDir) is not { } file || index >= file.Records.Count) return null;
        var rec = file.Records[index];
        return rec.Time != 0 ? rec : null;
    }

    /// <summary>stattool: ("%s     %s     %s     %s", name, time, difficulty, car). The name in the file has [ref:2018]
    /// tab padding, and the FFN font has no tab glyph (it skips it) – so we trim it. [ref:2019]</summary>
    public static string Text(TextFile text, StatRecord rec, Func<int, string> time)
    {
        string skill = text[rec.Skill == 2 ? 0x1F : rec.Skill == 1 ? 0x1B : 0x1E];
        string car = (uint)rec.Car < 12 && CarCatalog.VanillaTables.TryGetByLegacyIndex(rec.Car, out var def) ? def.DisplayName : ""; // rec.Car = original index
        return $"{rec.Name.TrimEnd()}     {time(rec.Time)}     {skill}     {car}";
    }
}
