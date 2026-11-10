using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.Mods;
using NFS.Game.Race;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Mods;

/// <summary>
/// openspec modding-api-cars 4.5: a whole headless race on track 0 with the repo mod car <c>indigo.duster/duster</c> as the human (test autopilot,
/// automatic gearbox) and as an AI opponent (the port's AiDriver), plus the engine bank the race audio picks for it. One lap (the replay's eight
/// would only make the test slow); the race ends when <see cref="RaceProgress.RaceOver"/> is set.
/// </summary>
public sealed class ModRaceTests(ITestOutputHelper output)
{
    const string DusterKey = "indigo.duster/duster";
    const int MaxTicks = 32 * 600;               // 10 min of sim time; a lap takes far less
    static readonly string RepoMods = Path.GetFullPath(Path.Combine(GameData.ProjectRoot, "..", "mods"));

    static CarCatalog Catalog()
    {
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(RepoMods, diag);
        var (catalog, problems) = CarCatalog.Build([VanillaCarSource.FromGameDir(GameData.Dir), new ModCarSource(mods, diag)]);
        Assert.Empty(problems);
        Assert.DoesNotContain(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
        return catalog;
    }

    /// <summary>The replay's race on track 0, one lap; the Duster takes the human slot or the first AI slot.</summary>
    static (RaceSetup Setup, int Slot, int Number) Setup(CarCatalog catalog, bool asHuman)
    {
        var tags = RaceTags.FromConstants();
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var s = RaceArgsParser.Parse(tags, RaceArgsParser.StreamFromBlock(tags, rpy.Config.Raw));
        s.Set(0x24, 0);                                                    // track 0
        s.Set(0x04, 1);                                                    // one lap
        int slot = Enumerable.Range(0, s.NumCars).First(i => s.Car(i).CarClass == (asHuman ? 1 : 2));
        int number = catalog.NumberOf(CarKey.Parse(DusterKey));
        s.Set(0x78 + slot * NFS.Data.FrontEnd.RaceConfig.CarRecordSize, number);
        return (s, slot, number);
    }

    /// <summary>Simple test autopilot (as RaceRunnerTests / the CLI drive-race): steer to the slice heading and back to the centre line.</summary>
    internal static PlayerInput Autopilot(RaceRunner run)
    {
        var c = run.Car;
        int dir = run.Session.Backward ? -1 : 1;
        var sl = run.Session.Slices[c.Trk.Slice];
        int want = FxMath.Atan2(sl.Forward[0] * dir, sl.Forward[2] * dir);
        int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        int err = ((want - have + 0x200) & 0x3FF) - 0x200;
        int lat = c.LateralOffset * dir;
        return new PlayerInput((sbyte)Math.Clamp(err * 2 - lat / 0x4000, -127, 127), 200, 0, 0);
    }

    static double Distance(Vec3 a, Vec3 b) => Math.Sqrt(Math.Pow((double)a.X - b.X, 2) + Math.Pow((double)a.Y - b.Y, 2) + Math.Pow((double)a.Z - b.Z, 2)) / 65536.0;   // metres (16.16)

    [GameFact]
    public void Duster_AsHuman_FinishesTheLapOnTrack0()
    {
        var catalog = Catalog();
        var (setup, slot, number) = Setup(catalog, asHuman: true);
        var runner = new RaceRunner(new RaceLoader(GameData.Dir, catalog).Load(setup), slot, automatic: true, withAi: true, countdown: true);
        Assert.Equal(0, runner.Session.TrackNumber);
        Assert.Equal(number, runner.Player.Setup.CarType);
        var prev = runner.Car.Pos;
        double path = 0;
        int t = 0;
        while (!runner.Laps.RaceOver && t < MaxTicks)
        {
            runner.Step(Autopilot(runner)); t++;
            path += Distance(prev, runner.Car.Pos); prev = runner.Car.Pos;
        }
        output.WriteLine($"human Duster: ticks {t}, state {runner.Laps.Cars[slot].State}, laps {runner.Laps.Cars[slot].LapsDone}, " +
            $"path {path:F0} m, edge hits {runner.EdgeHits}, wall hits {runner.WallHits}, resets {runner.ManualResets}");
        Assert.True(runner.Laps.RaceOver, $"the race did not end within {MaxTicks} ticks");
        Assert.Equal(2, runner.Laps.Cars[slot].State);                      // the Duster crossed the finish line
        Assert.True(runner.Laps.Cars[slot].LapsDone >= 1);
        Assert.True(path > 1000, $"the Duster must really drive a lap (path {path:F0} m)");
    }

    [GameFact]
    public void Duster_AsAi_FinishesTheLapOnTrack0()
    {
        var catalog = Catalog();
        var (setup, slot, number) = Setup(catalog, asHuman: false);
        var runner = new RaceRunner(new RaceLoader(GameData.Dir, catalog).Load(setup), 0, automatic: true, withAi: true, countdown: true);
        Assert.Equal(0, runner.Session.TrackNumber);
        var duster = runner.Cars[slot];
        Assert.Equal(number, duster.Race.Setup.CarType);
        Assert.NotNull(duster.Ai);                                           // driven by the port's AiDriver
        var prev = duster.Obj.Pos;
        double path = 0;                                                       // metres driven (a lap ends where it began, so the end point says nothing)
        int t = 0;
        while (!runner.Laps.RaceOver && t < MaxTicks)
        {
            runner.Step(Autopilot(runner)); t++;
            path += Distance(prev, duster.Obj.Pos); prev = duster.Obj.Pos;
        }
        output.WriteLine($"AI Duster: ticks {t}, state {runner.Laps.Cars[slot].State}, laps {runner.Laps.Cars[slot].LapsDone}, path {path:F0} m");
        Assert.True(runner.Laps.RaceOver, $"the race did not end within {MaxTicks} ticks");
        Assert.Equal(2, runner.Laps.Cars[slot].State);                      // the AI Duster crossed the finish line
        Assert.True(path > 1000, $"the AI Duster must really drive a lap (path {path:F0} m)");
    }

    [GameFact]
    public void Duster_GetsItsOwnEngineBank_InTheRaceAudio()
    {
        var catalog = Catalog();
        var duster = catalog.Get(CarKey.Parse(DusterKey));
        Assert.Equal("must", duster.Traits.EngineBank!.Player);
        Assert.Contains("must", NFS.Core.Constants.AudioConstants.PlayerBanks);

        // as the human: the player engine voices (channels 0 and 2) play the bank of the car.json
        var (setup, slot, _) = Setup(catalog, asHuman: true);
        var runner = new RaceRunner(new RaceLoader(GameData.Dir, catalog).Load(setup), slot, automatic: true, withAi: true, countdown: true);
        using var audio = new RaceAudio(runner, GameData.Dir);
        var seen = new HashSet<string>();
        for (int t = 0; t < 32 * 20; t++)
        {
            runner.Step(Autopilot(runner));
            foreach (var v in audio.Voices) if (v.Channel is 0 or 2) seen.Add(v.Bank);
        }
        output.WriteLine("player engine banks: " + string.Join(",", seen));
        Assert.Equal(["must"], seen.Select(b => b.ToLowerInvariant()).Distinct().ToArray());
    }
}
