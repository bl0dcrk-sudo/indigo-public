using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.ModApi;

namespace NFS.Game.Mods;

/// <summary>A replay needs a mod set that is not the one loaded now (missing mod, other version or content, other car numbering).</summary>
public sealed class ReplayModsMismatchException(string message) : IOException(message);

public sealed record ReplayModEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("hash")] string Hash);

public sealed record ReplayCarEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("number")] int Number);

/// <summary>Mods header of a port replay (D8): mods that own cars of the race (id, version, content hash) and the car keys with the in-memory
/// numbers stored in the race config. It is a trailer after the end marker of the original replay stream: <c>"NFSMODS1"</c>, u32 LE length,
/// UTF-8 JSON (<c>v</c> = 1). Everything the original parser reads is unchanged, so replays without it (all Original-mode replays, old port
/// replays) stay byte-identical and load as before. Original-mode replays never get it.</summary>
public sealed record ModsReplayHeader(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("mods")] IReadOnlyList<ReplayModEntry> Mods,
    [property: JsonPropertyName("cars")] IReadOnlyList<ReplayCarEntry> Cars)
{
    public const int CurrentVersion = 1;
    static readonly byte[] Magic = "NFSMODS1"u8.ToArray();

    /// <summary>Header for the given car numbers of a race; null in <see cref="GameDataMode.Original"/> (no mod car).</summary>
    public static ModsReplayHeader? For(CarCatalog catalog, IEnumerable<int> carNumbers, IReadOnlyList<LoadedMod> mods)
    {
        var numbers = carNumbers.Where(n => n >= CarCatalog.VanillaCount).Distinct().Order().ToList();
        if (numbers.Count == 0) return null;
        var cars = new List<ReplayCarEntry>();
        var used = new List<ReplayModEntry>();
        foreach (int n in numbers)
        {
            if (!catalog.TryGetByNumber(n, out var def)) throw new InvalidOperationException($"mod car number {n} is not in the catalog");
            cars.Add(new ReplayCarEntry(def.Key.Value, n));
            string modId = ModIdOf(def.Key.Value);
            if (used.Any(u => u.Id == modId)) continue;
            var mod = mods.FirstOrDefault(m => m.Manifest.Id == modId)
                ?? throw new InvalidOperationException($"mod '{modId}' of car '{def.Key.Value}' is not loaded");
            used.Add(new ReplayModEntry(modId, mod.Manifest.Version, ModFingerprint.Of(mod)));
        }
        return new ModsReplayHeader(CurrentVersion, used, cars);
    }

    static string ModIdOf(string carKey) { int i = carKey.IndexOf('/'); return i < 0 ? carKey : carKey[..i]; }

    public byte[] ToTrailer()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        var b = new byte[Magic.Length + 4 + json.Length];
        Magic.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(Magic.Length), json.Length);
        json.CopyTo(b, Magic.Length + 4);
        return b;
    }

    /// <summary>Appends the trailer after the stream end marker (<c>data</c> must end with it).</summary>
    public byte[] AppendTo(byte[] data) => [.. data, .. ToTrailer()];

    /// <summary>Header of a replay; null when it has none (Original mode / old port replay / unknown trailing bytes).
    /// Throws <see cref="ReplayModsMismatchException"/> for a damaged header or one written by a newer port.</summary>
    public static ModsReplayHeader? Read(Replay replay)
    {
        var d = replay.RawData;
        int p = replay.StreamEnd + 1;
        if (d.Length < p + Magic.Length + 4 || !d.AsSpan(p, Magic.Length).SequenceEqual(Magic)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + Magic.Length));
        int start = p + Magic.Length + 4;
        if (len < 0 || start + len > d.Length) throw new ReplayModsMismatchException("replay mods header is truncated");
        ModsReplayHeader? h;
        try { h = JsonSerializer.Deserialize<ModsReplayHeader>(d.AsSpan(start, len)); }
        catch (JsonException e) { throw new ReplayModsMismatchException("replay mods header is damaged: " + e.Message); }
        if (h is null || h.Mods is null || h.Cars is null) throw new ReplayModsMismatchException("replay mods header is damaged");
        if (h.Version != CurrentVersion) throw new ReplayModsMismatchException($"replay mods header version {h.Version} is not supported (this port reads {CurrentVersion})");
        return h;
    }

    /// <summary>Checks the replay against the loaded mods and catalog. A replay without header must not use mod car numbers.
    /// Throws <see cref="ReplayModsMismatchException"/> with the reason (names the mod).</summary>
    public static void Validate(Replay replay, CarCatalog catalog, IReadOnlyList<LoadedMod> mods)
    {
        var header = Read(replay);
        var used = Enumerable.Range(0, replay.Config.NumCars).Select(i => replay.Config.Car(i).CarType)
            .Where(n => n >= CarCatalog.VanillaCount).Distinct().ToList();
        if (header is null)
        {
            if (used.Count > 0) throw new ReplayModsMismatchException($"replay uses mod car number {used[0]} but has no mods header");
            return;
        }
        foreach (var m in header.Mods)
        {
            var have = mods.FirstOrDefault(x => x.Manifest.Id == m.Id);
            if (have is null) throw new ReplayModsMismatchException($"replay needs mod '{m.Id}' {m.Version}, which is not installed");
            if (have.Manifest.Version != m.Version)
                throw new ReplayModsMismatchException($"replay needs mod '{m.Id}' {m.Version}, but version {have.Manifest.Version} is installed");
            if (!string.Equals(ModFingerprint.Of(have), m.Hash, StringComparison.OrdinalIgnoreCase))
                throw new ReplayModsMismatchException($"replay needs mod '{m.Id}' {m.Version}, but its content differs from the recorded one");
        }
        foreach (var c in header.Cars)
        {
            if (!CarKey.TryParse(c.Key, out var key) || !catalog.TryGet(key, out _))
                throw new ReplayModsMismatchException($"replay uses car '{c.Key}', which is not available");
            int now = catalog.NumberOf(key);
            if (now != c.Number)
                throw new ReplayModsMismatchException($"replay recorded car '{c.Key}' as number {c.Number}, now it is {now} (different mod set or order)");
        }
        foreach (int n in used)
            if (header.Cars.All(c => c.Number != n)) throw new ReplayModsMismatchException($"replay uses car number {n} that its mods header does not list");
    }
}
