using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.Mods;
using NFS.Game.Race;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>
/// openspec modding-api-cars 3.5, "clone" test: a test mod (checked-in mod.json + car.json under Fixtures/clone.f355, no EA data)
/// holds the F355 (number 2) in the <c>raw</c> form under the key <c>clone.f355/f355clone</c>. The 356 B specs and 524 B AI files
/// are cut from CARDATA.VIV of the game dir at test time into a temp copy of the mod (never checked in); the glb is a relative
/// copy of mods/indigo.duster. A headless replay race (REPLAY.RPY: sim, 8 cars, AI, replay inputs) is run with the human car
/// being the original and then the clone (lockstep, same replay input); every tick the whole CarObj state (<see cref="CarDump"/>) of every car must
/// be equal except <see cref="BodyMassSlots"/>. NB: car.json "names.en" must be "F355": the loader finds the .geo of the car in CARDATA.VIV by DisplayName.
/// </summary>
public sealed class CloneModTests : IDisposable
{
    const string ModId = "clone.f355";
    const int Original = 2;                       // F355
    const int Ticks = 4000;
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-clone-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    static string TestsMods => Path.Combine(GameData.ProjectRoot, "tests", "NFS.Tests", "Mods", "Fixtures", ModId);

    CarCatalog BuildCatalog()
    {
        string mod = Path.Combine(_root, ModId, "1.0.0");
        string car = Path.Combine(mod, "cars", "f355clone");
        Directory.CreateDirectory(car);
        File.Copy(Path.Combine(TestsMods, "mod.json"), Path.Combine(mod, "mod.json"));
        File.Copy(Path.Combine(TestsMods, "cars", "f355clone", "car.json"), Path.Combine(car, "car.json"));
        File.Copy(Path.Combine(GameData.ProjectRoot, "..", "mods", "indigo.duster", "1.0.0", "cars", "duster", "duster.glb"), Path.Combine(car, "f355clone.glb"));
        var viv = Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir);
        string model = RaceConstants_Name(Original);
        File.WriteAllBytes(Path.Combine(car, "specs.dat"), viv.Get(RaceLoader.SpecsFile(Original, model, 0)));
        File.WriteAllBytes(Path.Combine(car, "ai.dat"), viv.Get(RaceLoader.AiModelFile(Original, model)));

        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(_root, diag);
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        var (catalog, problems) = CarCatalog.Build([VanillaCarSource.FromGameDir(GameData.Dir), new ModCarSource(mods, diag)]);
        Assert.Empty(problems);
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        return catalog;
    }

    static string RaceConstants_Name(int i) => NFS.Core.Constants.RaceConstants.CarNames[i];

    static RaceSetup Setup(int humanCarType)
    {
        var tags = RaceTags.FromConstants();
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var s = RaceArgsParser.Parse(tags, RaceArgsParser.StreamFromBlock(tags, rpy.Config.Raw));
        int human = s.Cars.Select((c, i) => (c, i)).First(x => x.c.CarClass == 1).i;
        s.Set(0x78 + human * RaceConfig.CarRecordSize, humanCarType);
        return s;
    }

    /// <summary>Known exception (design.md, "Exceptions (2.8)", c): a car.json <c>raw</c> car takes BodyMass from CarSpecs.Mass/100 (F355: 13.53),
    /// the original from the table (13.96). The sim itself reads CarSpecs, so the 4000-tick state is identical anyway. [ref:3274]</summary>
    static readonly HashSet<string> BodyMassSlots = ["Mass", "InvMass", "Inertia", "InvInertia"];

    static RaceRunner NewRunner(CarCatalog catalog, int humanCarType) =>
        new(new RaceLoader(GameData.Dir, catalog).Load(Setup(humanCarType)), 0, automatic: null, withAi: true, countdown: true);

    [GameFact]
    public void Clone_RunsTheSameRaceAsTheOriginal()
    {
        var catalog = BuildCatalog();
        Assert.True(CarKey.TryParse($"{ModId}/f355clone", out var key));
        int clone = catalog.NumberOf(key);
        Assert.Equal(CarCatalog.VanillaCount, clone);
        Assert.Equal(CarKey.Vanilla("F355"), catalog.Cars[Original].Key);

        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var input = new ReplayInputSource(rpy);
        var orig = NewRunner(catalog, Original);
        var cl = NewRunner(catalog, clone);
        var firstDiff = new SortedDictionary<string, string>();             // "carN.slot" -> first tick and values
        var moved = new HashSet<long>();
        for (int t = 0; t < Ticks; t++)
        {
            var inp = input.ForRaceTick(orig.Tick);
            orig.Step(inp);
            cl.Step(inp);
            Assert.Equal(orig.Cars.Count, cl.Cars.Count);
            for (int c = 0; c < orig.Cars.Count; c++)
                foreach (var slot in CarDump.Slots)
                {
                    long x = slot.Get(orig.Cars[c].Obj), y = slot.Get(cl.Cars[c].Obj);
                    if (c == 0 && slot.Name == "Pos.X") moved.Add(x);
                    if (x != y && !BodyMassSlots.Contains(slot.Name)) firstDiff.TryAdd($"car{c}.{slot.Name}", $"tick {t + 1}: {x} vs {y}");
                }
        }
        Assert.True(moved.Count > Ticks / 4, "the human car must actually move");
        Assert.True(firstDiff.Count == 0, $"{firstDiff.Count} slots diverge: " + string.Join("; ", firstDiff.Take(40).Select(kv => kv.Key + " @ " + kv.Value)));
    }
}
