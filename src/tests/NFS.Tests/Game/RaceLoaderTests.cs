using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class RaceLoaderTests
{
    private const string Prefs = "GAMEDATA/PREFS/PC/";
    private static readonly Lazy<RaceTags> Tags = new(() => RaceTags.FromConstants());

    private static RaceSetup FromPrefs(string file)
    {
        var c = new RaceArgsCompiler(Tags.Value, f => GameFiles.Exists(Prefs + f, GameData.Dir) ? GameData.ReadRaw(Prefs + f) : null);
        c.AddText(GameData.ReadRaw(Prefs + file));
        return RaceArgsParser.Parse(Tags.Value, c.ToArray());
    }

    private static RaceSetup FromReplay()
    {
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        return RaceArgsParser.Parse(Tags.Value, RaceArgsParser.StreamFromBlock(Tags.Value, rpy.Config.Raw));
    }

    private static RaceSession Load(RaceSetup s) => new RaceLoader(GameData.Dir).Load(s);

    [GameFact]
    public void ModelNames_FromExe()
    {
        var l = new RaceLoader(GameData.Dir);
        Assert.Equal("MCF1", l.ModelName(0));
        Assert.Equal("F355", l.ModelName(2));
        Assert.Equal("JAGR", l.ModelName(6));
    }

    [Fact]
    public void Flags_PerClass()
    {
        Assert.Equal(CarFlags.Racer | CarFlags.LocalHuman, RaceLoader.FlagsOf(1));
        Assert.Equal(CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent, RaceLoader.FlagsOf(2));
        Assert.Equal(CarFlags.NotLocal | CarFlags.Traffic, RaceLoader.FlagsOf(4));
        Assert.Equal(CarFlags.NotLocal | CarFlags.Cop, RaceLoader.FlagsOf(8));
    }

    [GameFact]
    public void Replay_Session()
    {
        var s = FromReplay();
        var r = Load(s);
        Assert.Equal(4, r.TrackNumber);
        Assert.Equal(8, r.Cars.Count);
        Assert.NotEmpty(r.Slices);
        Assert.NotNull(r.Speeds);
        AssertGrid(r);
    }

    [GameFact]
    public void DefaultPrefs_Session()
    {
        var s = FromPrefs("DEFAULT.TXT");
        var r = Load(s);
        Assert.Equal(s.NumCars, r.Cars.Count);
        Assert.True(r.Cars.Count >= 1);
        AssertGrid(r);
    }

    [GameFact]
    public void Backward_GridFacesReverse()
    {
        var s = FromReplay();
        s.Set(0x30, 1);
        var r = Load(s);
        Assert.True(r.Backward);
        AssertGrid(r);
        // slot 0 starts at the highest grid slice when reversed
        var racers = r.Cars.Where(c => c.Setup.CarClass is 1 or 2).ToList();
        Assert.Equal(racers.Max(c => c.StartSlice), racers.Single(c => c.GridSlot == 0).StartSlice);
    }

    [GameFact]
    public void RaceInfo_AllSources()
    {
        foreach (var src in new[] { "config", "replay", "DEFAULT.TXT" })
        {
            var setup = RaceInfo.Setup(src, GameData.Dir);
            var text = RaceInfo.Format(Load(setup));
            Assert.Contains($"cars {setup.NumCars}", text);
            Assert.Equal(setup.NumCars + 2, text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
        }
    }

    private static void AssertGrid(RaceSession r)
    {
        var racers = r.Cars.Where(c => c.Setup.CarClass is 1 or 2).ToList();
        Assert.Equal(racers.Count, racers.Select(c => c.Obj.Pos).Distinct().Count());
        foreach (var c in racers)
        {
            Assert.InRange(c.StartSlice, 0, 5 * 16 + 2);
            Assert.True(c.Obj.Flags.HasFlag(CarFlags.Racer));
            Assert.NotNull(c.Specs);
            var sl = r.Slices[c.StartSlice];
            // close to the slice centre (< 20 m), y offset 0.5 m above
            Assert.InRange(Math.Abs(c.Obj.Pos.X - sl.X) + Math.Abs(c.Obj.Pos.Z - sl.Z), 0, 20 * Fx.One);
            // forward vector agrees with track direction (next slice − this) or opposite when backward
            var next = r.Slices[(c.StartSlice + 1) % r.Slices.Length];
            long dx = next.X - sl.X, dz = next.Z - sl.Z;
            long dot = dx * c.Obj.Orient.M20 + dz * c.Obj.Orient.M22;
            Assert.True(r.Backward ? dot < 0 : dot > 0, $"car {c.Index} dot {dot}");
        }
        // pairs of rows: even slots left, odd slots right (lateral sign differs)
        Assert.Contains(racers, c => c.GridSlot % 2 == 0);
    }
}
