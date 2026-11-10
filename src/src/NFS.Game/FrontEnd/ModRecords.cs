using System.Text.Json;
using System.Text.Json.Serialization;

namespace NFS.Game.FrontEnd;

/// <summary>One record of <c>records_mods.json</c>. <see cref="Kind"/> is <c>total</c> (race time for <see cref="Laps"/> laps) or <c>lap</c>
/// (best lap, <see cref="Laps"/> = 0). Time in 1/64 s ticks like the .STF files.</summary>
public sealed record ModRecord(
    [property: JsonPropertyName("track")] int Track,
    [property: JsonPropertyName("laps")] int Laps,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("car")] string Car,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ticks")] int Ticks,
    [property: JsonPropertyName("skill")] int Skill);

/// <summary>Records of races with mod cars (GameDataMode.Modded, D8), kept apart from the original <c>.STF</c> files in
/// <c>%APPDATA%\NFS2SE-port\records_mods.json</c>, keyed by track and car key (<c>indigo.duster/duster</c>). Per (track, laps, car, kind)
/// the best <see cref="Rows"/> times for <c>total</c> and the single best for <c>lap</c>. Writes are atomic (temp file + replace);
/// a file with a newer schema is left alone (read as empty, never overwritten by <see cref="Save"/>).</summary>
public sealed class ModRecords
{
    public const string FileName = "records_mods.json";
    public const int Schema = 1;
    public const int Rows = 10;
    public const string Total = "total", Lap = "lap";

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NewLine = "\n" };

    sealed record FileModel(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("records")] List<ModRecord>? Records);

    readonly List<ModRecord> _records;
    readonly bool _readOnly;

    ModRecords(List<ModRecord> records, bool readOnly) { _records = records; _readOnly = readOnly; }

    public IReadOnlyList<ModRecord> Records => _records;

    /// <summary>Empty set (no file yet).</summary>
    public static ModRecords Empty() => new([], false);

    /// <summary>Loads the file; a missing, damaged or newer-schema file gives an empty set (the latter two are never overwritten).</summary>
    public static ModRecords Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty();
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Options);
            if (m == null) return new([], true);
            if (m.Schema != Schema) return new([], true);
            return new(m.Records ?? [], false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new([], true); }
    }

    /// <summary>Atomic write. Returns false when the file belongs to a newer/unreadable version and was left alone.</summary>
    public bool Save(string path)
    {
        if (_readOnly) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel(Schema, _records), Options) + "\n");
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    IEnumerable<ModRecord> Of(int track, int laps, string kind, string car)
        => _records.Where(r => r.Track == track && r.Laps == laps && r.Kind == kind && r.Car == car).OrderBy(r => r.Ticks);

    /// <summary>Same rule as the original top 10: a time qualifies when the list is not full, or it is not worse than the last one.</summary>
    public bool TotalQualifies(int track, int laps, string car, int ticks)
    {
        if (ticks <= 0) return false;
        var list = Of(track, laps, Total, car).ToList();
        return list.Count < Rows || ticks <= list[^1].Ticks;
    }

    /// <summary>Rank 1.. a time would get (ties go after existing equal times).</summary>
    public int RankOf(int track, int laps, string car, int ticks) => Of(track, laps, Total, car).Count(r => r.Ticks <= ticks) + 1;

    /// <summary>Inserts a total time (after existing equal times) and trims to <see cref="Rows"/>. Returns the rank 1..10 (0 = cut off).</summary>
    public int InsertTotal(ModRecord record)
    {
        var list = Of(record.Track, record.Laps, Total, record.Car).ToList();
        int index = list.FindIndex(r => r.Ticks > record.Ticks);
        if (index < 0) index = list.Count;
        list.Insert(index, record);
        _records.RemoveAll(r => r.Track == record.Track && r.Laps == record.Laps && r.Kind == Total && r.Car == record.Car);
        _records.AddRange(list.Take(Rows));
        return index < Rows ? index + 1 : 0;
    }

    public bool LapQualifies(int track, string car, int ticks)
        => ticks > 0 && (Of(track, 0, Lap, car).FirstOrDefault() is not { } best || ticks <= best.Ticks);

    public void SetLap(ModRecord record)
    {
        _records.RemoveAll(r => r.Track == record.Track && r.Kind == Lap && r.Car == record.Car);
        _records.Add(record with { Laps = 0, Kind = Lap });
    }

    public ModRecord? BestTotal(int track, int laps, string car) => Of(track, laps, Total, car).FirstOrDefault();
    public ModRecord? BestLap(int track, string car) => Of(track, 0, Lap, car).FirstOrDefault();
}
