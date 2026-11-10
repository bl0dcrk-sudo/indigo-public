using System.Text.Json;
using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.FrontEnd;
using NFS.Game.Mods;
using NFS.Game.Race;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>openspec modding-api-cars 4.3 (D8): <see cref="GameDataMode"/>, records of modded races in <c>records_mods.json</c> (the original
/// <c>.STF</c> stay byte for byte), mods header of port replays. Uses the repo mod <c>mods/indigo.duster</c> and temp dirs only.</summary>
public sealed class ModDataModeTests : IDisposable
{
    const string DusterKey = "indigo.duster/duster";
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-datamode-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    static string RepoMods => Path.GetFullPath(Path.Combine(GameData.ProjectRoot, "..", "mods"));

    static (CarCatalog Catalog, IReadOnlyList<LoadedMod> Mods) Duster()
    {
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(RepoMods, diag);
        var (catalog, problems) = CarCatalog.Build([VanillaCarSource.FromGameDir(GameData.Dir), new ModCarSource(mods, diag)]);
        Assert.Empty(problems);
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        return (catalog, mods);
    }

    static RaceSetup Setup(int humanCarType)
    {
        var tags = RaceTags.FromConstants();
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var s = RaceArgsParser.Parse(tags, RaceArgsParser.StreamFromBlock(tags, rpy.Config.Raw));
        int human = s.Cars.Select((c, i) => (c, i)).First(x => x.c.CarClass == 1).i;
        s.Set(0x78 + human * RaceConfig.CarRecordSize, humanCarType);
        return s;
    }

    static Replay Record(RaceSetup setup, CarCatalog? catalog, IReadOnlyList<LoadedMod>? mods)
    {
        var rec = new ReplayRecorder(setup, 0, catalog, mods);
        for (int i = 0; i < 100; i++) rec.Record(new PlayerInput(Steer: 0, Gas: 255, Brake: 0, Buttons: 0));
        return rec.Build(200);
    }

    /// <summary>Copy of the mod dir under the same id with an edited manifest or an extra file.</summary>
    LoadedMod ModifiedCopy(string tag, Func<string, string>? manifest, string? extraFile, string dirVersion = "1.0.0")
    {
        string dir = Path.Combine(_root, tag, "indigo.duster", dirVersion);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoMods, "indigo.duster", "1.0.0"), "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dir, Path.GetRelativePath(Path.Combine(RepoMods, "indigo.duster", "1.0.0"), f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        if (manifest != null) File.WriteAllText(Path.Combine(dir, "mod.json"), manifest(File.ReadAllText(Path.Combine(dir, "mod.json"))));
        if (extraFile != null) File.WriteAllText(Path.Combine(dir, extraFile), "x");
        var loaded = ModLoader.Load(Path.Combine(_root, tag), new ModDiagnostics());
        return Assert.Single(loaded);
    }

    [Fact]
    public void GameDataMode_FollowsParticipatingCars()
    {
        Assert.Equal(GameDataMode.Original, GameDataModes.Of([0, 5, 0x34]));
        Assert.Equal(GameDataMode.Modded, GameDataModes.Of([0, 0x35]));
        Assert.Equal(GameDataMode.Original, GameDataModes.Of([]));
    }

    // ---- records -----------------------------------------------------------------------------------------------------

    static string[] StfFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.STF", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray() : [];

    static Dictionary<string, byte[]> Snapshot(string dir) =>
        StfFiles(dir).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes);

    static void AssertSame(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (k, v) in before) Assert.True(v.AsSpan().SequenceEqual(after[k]), $".STF changed: {k}");
    }

    static RaceResults Results(int humanCar, int opponentCar, int raceTicks = 1000, int laps = 2) => new([
        new RacerResult(1, 0, "", true, false, raceTicks, [.. Enumerable.Repeat(raceTicks / laps, laps)], raceTicks / laps, CarType: humanCar),
        new RacerResult(2, 1, "", false, false, raceTicks + 500, [400, 400], 400, CarType: opponentCar)]);

    (MenuController Menu, string Stats, string ModFile) Menu(CarCatalog catalog)
    {
        // temp copy of the user-side stats dir seeded with the original .STF, so that the byte comparison is meaningful for the file a record would hit
        string stats = Path.Combine(_root, "stats");
        string src = Path.Combine(GameData.Dir!, "FEDATA", "PC", "STATS");
        foreach (var f in Directory.GetFiles(src, "*.STF", SearchOption.AllDirectories))
        {
            string to = Path.Combine(stats, "FEDATA", "PC", "STATS", Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        var m = MenuController.Load(GameData.Dir);
        m.StatsDirectory = stats;
        m.ModRecordsPath = Path.Combine(_root, "records_mods.json");
        m.Cars = catalog;
        m.Config.GameType = 0; m.Config.RaceType = 0; m.Config.Track = 0;
        m.StartRace();
        return (m, stats, m.ModRecordsPath);
    }

    static void FinishRecords(MenuController m)
    {
        m.UpdateRecords();
        m.EndVictory();
        m.UpdateRecords();
        if (m.NameEditing != null) m.HandleKey(MenuKey.Enter);
        m.HandleKey(MenuKey.Enter);                 // "lap times"
        m.UpdateRecords();
        if (m.NameEditing != null) m.HandleKey(MenuKey.Enter);
    }

    [GameFact]
    public void DusterRace_WritesModRecords_AndLeavesStfBytewiseUntouched()
    {
        var (catalog, _) = Duster();
        int duster = catalog.NumberOf(CarKey.Parse(DusterKey));
        var (m, stats, modFile) = Menu(catalog);
        string gameStats = Path.Combine(GameData.Dir!, "FEDATA", "PC", "STATS");
        var gameBefore = Snapshot(gameStats);
        var userBefore = Snapshot(stats);
        Assert.NotEmpty(gameBefore);
        int laps = m.Session!.Setup.NumLaps;

        m.CompleteRace(Results(duster, 0, 1000, laps));
        FinishRecords(m);

        AssertSame(gameBefore, Snapshot(gameStats));
        AssertSame(userBefore, Snapshot(stats));
        Assert.True(File.Exists(modFile));
        using var doc = JsonDocument.Parse(File.ReadAllText(modFile));
        Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
        var recs = doc.RootElement.GetProperty("records").EnumerateArray().ToList();
        var total = Assert.Single(recs, r => r.GetProperty("kind").GetString() == "total");
        Assert.Equal(DusterKey, total.GetProperty("car").GetString());
        Assert.Equal(1000, total.GetProperty("ticks").GetInt32());
        Assert.Equal(laps, total.GetProperty("laps").GetInt32());
        var lap = Assert.Single(recs, r => r.GetProperty("kind").GetString() == "lap");
        Assert.Equal(1000 / laps, lap.GetProperty("ticks").GetInt32());
        // the file loads back
        Assert.Equal(2, ModRecords.Load(modFile).Records.Count);
    }

    [GameFact]
    public void ModdedRace_WithVanillaHuman_AlsoGoesToModRecords()
    {
        var (catalog, _) = Duster();
        int duster = catalog.NumberOf(CarKey.Parse(DusterKey));
        var (m, stats, modFile) = Menu(catalog);
        var userBefore = Snapshot(stats);
        m.CompleteRace(Results(0, duster, 1000, m.Session!.Setup.NumLaps));   // F355 human, Duster as opponent: race is Modded
        FinishRecords(m);
        AssertSame(userBefore, Snapshot(stats));
        Assert.True(catalog.TryGetByNumber(0, out var f355));
        Assert.Contains(ModRecords.Load(modFile).Records, r => r.Car == f355.Key.Value);
    }

    [GameFact]
    public void VanillaRace_StillWritesStf_AndNoModRecords()
    {
        var (catalog, _) = Duster();
        var (m, stats, modFile) = Menu(catalog);
        m.CompleteRace(Results(0, 1, 1000, m.Session!.Setup.NumLaps));
        FinishRecords(m);
        Assert.False(File.Exists(modFile));
        Assert.Equal(1000, TrackRecords.Load(0, GameData.Dir, stats)!.Records[TrackRecords.RowOffset(m.Session.Setup.NumLaps)].Time);
    }

    [Fact]
    public void ModRecords_RanksTrimsAndSavesAtomically()
    {
        string path = Path.Combine(_root, "r", "records_mods.json");
        var r = ModRecords.Empty();
        for (int i = 0; i < 12; i++) r.InsertTotal(new ModRecord(1, 2, ModRecords.Total, "a/b", "n" + i, 1000 + 10 * (11 - i), 0));
        Assert.Equal(10, r.Records.Count);
        Assert.Equal(1000, r.BestTotal(1, 2, "a/b")!.Ticks);
        Assert.False(r.TotalQualifies(1, 2, "a/b", 5000));
        Assert.True(r.TotalQualifies(1, 2, "other/car", 5000));          // keyed per car
        Assert.True(r.Save(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(10, ModRecords.Load(path).Records.Count);
        // a newer schema is not overwritten
        File.WriteAllText(path, "{\"schema\":99,\"records\":[]}");
        var newer = ModRecords.Load(path);
        Assert.Empty(newer.Records);
        Assert.False(newer.Save(path));
        Assert.Contains("99", File.ReadAllText(path));
    }

    // ---- replays -----------------------------------------------------------------------------------------------------

    [GameFact]
    public void Replay_ModsHeader_RoundtripAndStillParsesAsOriginalFormat()
    {
        var (catalog, mods) = Duster();
        int duster = catalog.NumberOf(CarKey.Parse(DusterKey));
        var plain = Record(Setup(duster), null, null);
        var replay = Record(Setup(duster), catalog, mods);

        Assert.Null(ModsReplayHeader.Read(plain));
        var header = ModsReplayHeader.Read(replay)!;
        Assert.Equal(ModsReplayHeader.CurrentVersion, header.Version);
        var mod = Assert.Single(header.Mods);
        Assert.Equal("indigo.duster", mod.Id);
        Assert.Equal("1.0.0", mod.Version);
        Assert.Equal(64, mod.Hash.Length);
        var car = Assert.Single(header.Cars);
        Assert.Equal((DusterKey, duster), (car.Key, car.Number));

        // the original-format part is identical; the header is only a trailer behind the end marker
        Assert.True(replay.RawData.AsSpan(0, plain.RawData.Length).SequenceEqual(plain.RawData));
        Assert.True(replay.RawData.Length > plain.RawData.Length);
        var reparsed = Replay.Parse(replay.RawData);
        Assert.Equal(replay.Packets.Count, reparsed.Packets.Count);
        Assert.True(header.ToTrailer().AsSpan().SequenceEqual(ModsReplayHeader.Read(reparsed)!.ToTrailer()));

        ModsReplayHeader.Validate(replay, catalog, mods);   // same set: accepted
    }

    [GameFact]
    public void Replay_WithOtherModSet_IsRejectedWithClearError()
    {
        var (catalog, mods) = Duster();
        int duster = catalog.NumberOf(CarKey.Parse(DusterKey));
        var replay = Record(Setup(duster), catalog, mods);

        var missing = Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(replay, catalog, []));
        Assert.Contains("indigo.duster", missing.Message);
        Assert.Contains("not installed", missing.Message);

        var otherVersion = ModifiedCopy("ver", t => t.Replace("\"1.0.0\"", "\"1.1.0\""), null, "1.1.0");
        var ver = Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(replay, catalog, [otherVersion]));
        Assert.Contains("1.0.0", ver.Message);
        Assert.Contains("1.1.0", ver.Message);

        var otherContent = ModifiedCopy("content", null, "extra.txt");
        var content = Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(replay, catalog, [otherContent]));
        Assert.Contains("content differs", content.Message);

        // the same mod loaded as a different car number (vanilla-only catalog does not know the key at all)
        Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(replay, CarCatalog.VanillaTables, mods));

        // the menu refuses the replay before building a race
        var m = MenuController.Load(GameData.Dir);
        m.Cars = catalog; m.LoadedMods = [];
        Assert.Throws<ReplayModsMismatchException>(() => m.BeginReplayPlayback(replay));
        Assert.Null(m.PlaybackSource);
    }

    [GameFact]
    public void Replay_WithoutHeader_UsingModCar_IsRejected_NewerHeaderToo()
    {
        var (catalog, mods) = Duster();
        int duster = catalog.NumberOf(CarKey.Parse(DusterKey));
        var noHeader = Record(Setup(duster), null, null);
        Assert.Contains("no mods header", Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(noHeader, catalog, mods)).Message);

        var good = ModsReplayHeader.Read(Record(Setup(duster), catalog, mods))!;
        var newer = Replay.Parse(new ModsReplayHeader(99, good.Mods, good.Cars).AppendTo(noHeader.RawData));
        Assert.Contains("not supported", Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(newer, catalog, mods)).Message);

        var cut = Replay.Parse([.. noHeader.RawData, .. new ModsReplayHeader(1, good.Mods, good.Cars).ToTrailer()[..20]]);
        Assert.Throws<ReplayModsMismatchException>(() => ModsReplayHeader.Validate(cut, catalog, mods));
    }

    [GameFact]
    public void VanillaReplay_HasNoHeader_IsByteIdentical_AndStillLoads()
    {
        var (catalog, mods) = Duster();
        var setup = Setup(0);
        var a = Record(setup, null, null);
        var b = Record(setup, catalog, mods);          // catalog with mods loaded, but no mod car in this race: Original mode
        Assert.True(a.RawData.AsSpan().SequenceEqual(b.RawData));
        Assert.Null(ModsReplayHeader.Read(b));
        ModsReplayHeader.Validate(b, catalog, []);     // needs no mod at all
        ModsReplayHeader.Validate(b, CarCatalog.VanillaTables, []);

        // the original REPLAY.RPY of the disc: no header, accepted, not modified
        var original = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        Assert.Null(ModsReplayHeader.Read(original));
        ModsReplayHeader.Validate(original, CarCatalog.VanillaTables, []);
    }
}
