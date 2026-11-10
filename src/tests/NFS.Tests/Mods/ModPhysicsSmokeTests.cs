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
/// openspec modding-api-cars 3.6, "dymny" (physics smoke) test. <c>Fixtures/smoke.si</c> is a test mod with one SI-physics car (a Dacia
/// Duster: class G (F355 template), 1200 kg, 84 kW, 156 Nm, 5 gears); <c>Fixtures/smoke.broken</c> is the same car with <c>massKg: 0</c>.
/// The glb is a relative copy of mods/indigo.duster and is copied into the temp mod at test time (never checked in).
/// Task 4.1: the repo <c>mods/</c> dir (indigo.duster) is loaded as well, so the real Duster is smoke-tested together with the fixture.
/// </summary>
public sealed class ModPhysicsSmokeTests : IDisposable
{
    const string SiModId = "smoke.si";
    const string BrokenModId = "smoke.broken";
    const int Ticks = 3000;                       // 3000 sim ticks = about 94 s of 32 Hz sim time
    // Sanity bounds in 16.16 units. A car at 180 km/h (50 m/s) moves about 1.6 m per 32 Hz tick, i.e. about 1<<17 units. The per-tick
    // bound 1<<26 (1024 m) is about 500 times that, so it only trips on a teleport or an overflow-sized jump; a saturated value would
    // already be caught by the int.MinValue/int.MaxValue check. The absolute bound (16384 m) is a generous ceiling, not a track measure.
    const int MaxTickDelta = 1 << 26;
    const int MaxAbs = 1 << 30;
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-smoke-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    static string FixtureDir(string modId) => Path.Combine(GameData.ProjectRoot, "tests", "NFS.Tests", "Mods", "Fixtures", modId);

    /// <summary>Temp copy of a fixture mod: mod.json, cars/duster/car.json and the glb of mods/indigo.duster next to it.</summary>
    string BuildMod(string modId)
    {
        string mod = Path.Combine(_root, modId, "1.0.0");
        string car = Path.Combine(mod, "cars", "duster");
        Directory.CreateDirectory(car);
        File.Copy(Path.Combine(FixtureDir(modId), "mod.json"), Path.Combine(mod, "mod.json"));
        File.Copy(Path.Combine(FixtureDir(modId), "cars", "duster", "car.json"), Path.Combine(car, "car.json"));
        File.Copy(Path.Combine(GameData.ProjectRoot, "..", "mods", "indigo.duster", "1.0.0", "cars", "duster", "duster.glb"), Path.Combine(car, "duster.glb"));
        return mod;
    }

    static string RepoMods => Path.GetFullPath(Path.Combine(GameData.ProjectRoot, "..", "mods"));

    CarCatalog BuildCatalog()
    {
        BuildMod(SiModId);
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(_root, diag).Concat(ModLoader.Load(RepoMods, diag)).ToList();
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        var (catalog, problems) = CarCatalog.Build([VanillaCarSource.FromGameDir(GameData.Dir), new ModCarSource(mods, diag)]);
        Assert.Empty(problems);
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        return catalog;
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

    [GameFact]
    public void ModCars_RunWithoutOverflowOrExceptions()
    {
        var catalog = BuildCatalog();
        var modNumbers = Enumerable.Range(CarCatalog.VanillaCount, catalog.Cars.Count - CarCatalog.VanillaCount).ToList();
        Assert.NotEmpty(modNumbers);                                      // the smoke mod must be in the catalog
        Assert.Contains(catalog.Cars, c => c.Key.Value == "indigo.duster/duster");   // and the repo mod

        // "Straight-line" driving = constant full throttle, no steering, no buttons (the test does not verify that the road is straight).
        var gas = new PlayerInput(Steer: 0, Gas: 255, Brake: 0, Buttons: 0);
        foreach (int number in modNumbers)
        {
            string name = catalog.Cars[number].Key.Value;
            var runner = new RaceRunner(new RaceLoader(GameData.Dir, catalog).Load(Setup(number)), 0, automatic: true, withAi: true, countdown: true);
            var prev = runner.Car.Pos;
            long path = 0;
            for (int t = 0; t < Ticks; t++)
            {
                runner.Step(gas);
                var car = runner.Car;
                Assert.Equal(number, runner.Player.Setup.CarType);      // the human really drives the mod car
                foreach (var (label, v) in Components(car))
                {
                    Assert.True(v != int.MinValue && v != int.MaxValue, $"{name} tick {t + 1}: {label} saturated at {v}");
                    Assert.True(Math.Abs((long)v) < MaxAbs, $"{name} tick {t + 1}: {label} = {v} beyond the sane bound");
                }
                long dx = (long)car.Pos.X - prev.X, dy = (long)car.Pos.Y - prev.Y, dz = (long)car.Pos.Z - prev.Z;
                Assert.True(Math.Abs(dx) < MaxTickDelta && Math.Abs(dy) < MaxTickDelta && Math.Abs(dz) < MaxTickDelta,
                    $"{name} tick {t + 1}: position jumped by ({dx},{dy},{dz}) in one tick");
                path += Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                prev = car.Pos;
            }
            // automatic gearbox: the replay human has a manual one and would stay in neutral. 3000 ticks of full gas: F355 ~2500 m, LGT1 ~3100 m, SI Duster ~2300 m
            Assert.True(path > (1000L << 16), $"{name}: the car must really drive, at least 1000 m (path {path >> 16} m)");
        }
    }

    [ModFact]
    public void RepoDuster_IsRegisteredWithExpectedTraits()
    {
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(RepoMods, diag);
        var cars = new ModCarSource(mods, diag).GetCars().ToList();
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        var duster = Assert.Single(cars, c => c.Key.Value == "indigo.duster/duster");
        Assert.Equal("Duster", duster.DisplayName);
        Assert.Equal(CarRaceClass.G, duster.Traits.RaceClass);
        Assert.Equal(2, SiPhysicsConverter.TemplateNumber(duster.Traits.RaceClass));   // class G rides on the F355 template
        Assert.True(duster.Traits.HasAbs);
        Assert.Equal("must", duster.Traits.EngineBank!.Player);
        Assert.Equal(CarDrive.Front, Assert.IsType<SiPhysics>(duster.Physics).Drive);
        Assert.NotNull(duster.Dimensions);
        Assert.NotNull(duster.ModelPath);
        // the converter writes the front share (+0xF8) = 1.0 for a front-drive car
        Assert.True(SiPhysicsConverter.TryConvert((SiPhysics)duster.Physics, out var fx, out _));
        Assert.Equal(Fx.One, fx!.FrontDriveRatio);
    }

    static IEnumerable<(string Label, int Value)> Components(CarObj car)
    {
        yield return ("Pos.X", car.Pos.X);
        yield return ("Pos.Y", car.Pos.Y);
        yield return ("Pos.Z", car.Pos.Z);
        yield return ("Vel.X", car.Vel.X);
        yield return ("Vel.Y", car.Vel.Y);
        yield return ("Vel.Z", car.Vel.Z);
    }

    [ModFact]
    public void BrokenCar_IsRejectedWithDiagnosticNamingTheField()
    {
        BuildMod(BrokenModId);
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(_root, diag);
        var cars = new ModCarSource(mods, diag).GetCars().ToList();
        Assert.Empty(cars);
        var error = Assert.Single(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        Assert.Contains("massKg", error.Message);
    }
}
