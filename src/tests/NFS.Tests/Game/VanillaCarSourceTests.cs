using NFS.ModApi;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Core.Tables;
using NFS.Data;
using NFS.Data.Car;
using NFS.Game.Cars;
using NFS.Game.Race;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>VanillaCarSource: every field and trait of the 0x35 cars equals the old tables / original conditions.</summary>
public class VanillaCarSourceTests
{
    private static readonly IReadOnlyList<CarDefinition> Cars = [.. new VanillaCarSource().GetCars()];
    public static IEnumerable<object[]> AllCars() => Enumerable.Range(0, CarCatalog.VanillaCount).Select(i => new object[] { i });

    [Fact]
    public void Builds_0x35_Cars_AcceptedByCatalog()
    {
        var (catalog, diagnostics) = CarCatalog.Build([new VanillaCarSource()]);
        Assert.Empty(diagnostics);
        Assert.Equal(CarCatalog.VanillaCount, catalog.Count);
        for (int i = 0; i < CarCatalog.VanillaCount; i++)
        {
            Assert.Equal(i, Cars[i].LegacyIndex);
            Assert.True(catalog.TryGetByNumber(i, out var c));
            Assert.Equal(Cars[i].Key, c.Key);
        }
    }

    [Theory, MemberData(nameof(AllCars))]
    public void Identity_MatchesTables(int i)
    {
        var c = Cars[i];
        Assert.Equal(RaceConstants.CarNames[i], c.DisplayName);
        string name = RaceConstants.CarNames[i];
        bool shared = RaceConstants.CarNames.Count(n => n == name) > 1;
        Assert.Equal(shared ? $"nfs2se/{name}-{i:00}" : $"nfs2se/{name}", c.Key.Value);
        Assert.True(c.Key.IsVanilla);
        Assert.IsType<NoPhysics>(c.Physics); // table-only source: no files needed
    }

    [Theory, MemberData(nameof(AllCars))]
    public void Tables_MassVmaxBanks_MatchOldTables(int i)
    {
        var t = Cars[i].Traits;
        var info = GameTables.Default.CarInfo;
        int idx = i * GameTables.CarInfoStride + 1;
        Assert.Equal(idx < info.Length ? info[idx] : null, t.BodyMass);
        Assert.Equal(i < 15 ? GameTables.Default.ModelVmax[i] : 0, t.AiTopSpeed);
        Assert.Equal(1, t.BodyMassScale);
        if (i < 15)
            Assert.Equal((AudioConstants.PlayerBanks[i], AudioConstants.SplitBanks[i], AudioConstants.OpponentBanks[i]),
                (t.EngineBank.Player, t.EngineBank.Split, t.EngineBank.Opponent));
        else
        {
            Assert.Equal(AudioConstants.OpponentBanks[0], t.EngineBank.Opponent);
            Assert.StartsWith("S", t.EngineBank.Split);
            Assert.Equal(t.EngineBank.Player.ToUpperInvariant(), t.EngineBank.Split.ToUpperInvariant()[1..]);
        }
    }

    [Fact]
    public void EngineBanks_OfGeneratedCars_AreTheOriginalOnes()
    {
        string P(int i) => Cars[i].Traits.EngineBank.Player;
        Assert.Equal("TREX", P(43));
        Assert.Equal("CRATE", P(40)); Assert.Equal("CRATE", P(50));
        Assert.Equal("PUTT", P(18)); Assert.Equal("PUTT", P(29)); Assert.Equal("PUTT", P(36));
        foreach (int t in new[] { 20, 37, 21, 31, 33 }) Assert.Equal("TRUCK", P(t));
        Assert.Equal("LIMO", P(15));
        Assert.Equal("SLIMO", Cars[15].Traits.EngineBank.Split);
        Assert.Equal("STREX", Cars[43].Traits.EngineBank.Split);
    }

    [Theory, MemberData(nameof(AllCars))]
    public void Abs_AndSpecialRules_DeriveFromOriginalConditions(int i)
    {
        var t = Cars[i].Traits;
        Assert.Equal(SimModel.HasAbs(i), t.HasAbs);
        Assert.Equal(i == ArcadeModel.ModelSpecial, t.SpecialRules0E);
        Assert.Equal(i == 0 || i == 0xE, t.RearAeroBoost);   // SimModel.cs:388
        Assert.Equal(i == 7, t.SkidMarks);                    // SimModel.cs:269
    }

    [Theory, MemberData(nameof(AllCars))]
    public void TrafficPaint_MatchesCarModel(int i)
        => Assert.Equal(CarModel.PaintVariants(i, 3).Length, Cars[i].Traits.TrafficPaintVariants);

    [Theory, MemberData(nameof(AllCars))]
    public void Cockpit_MatchesRenderCode(int i)
    {
        var k = Cars[i].Traits.Cockpit;
        bool dash = NFS.Render.Cockpit.HasDashboard(i);
        Assert.Equal(dash ? $"DSH{i:00}.QFS" : null, k.DashFile);
        Assert.Equal(!dash ? CockpitKind.None : i == 4 ? CockpitKind.Digital : CockpitKind.Dash, k.Kind);
        Assert.Equal(dash && i == 5, k.Tinted);
        Assert.Equal(dash && i == 0, k.LeftMirror);
        Assert.Equal(dash && i is 3 or 4, k.NoBitOverlays);
        Assert.Equal(dash && i == 7 ? "DSH07HD.FSH" : null, k.ExtraFile);
        // digital dash has no mirror (Cockpit.InitialMirror): the same car the Digital kind marks
        if (dash) Assert.Equal(k.Kind != CockpitKind.Digital, NFS.Render.Cockpit.InitialMirror(i, []));
    }

    /// <summary>The traits predict exactly which cars CarGeo.ApplyWheelShift moves (a real .geo changes only where the trait is set).</summary>
    [GameFact]
    public void WheelShift_MatchesCarGeo()
    {
        var viv = Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir);
        bool front4 = false, rearAny = false;
        for (int i = 0; i < CarCatalog.VanillaCount; i++)
        {
            string geoName = $"{RaceConstants.CarNames[i]}.geo";
            if (!viv.Entries.Any(e => e.Name.Equals(geoName, StringComparison.OrdinalIgnoreCase))) continue; // RaceLoader: geo = null
            var data = viv.Get(geoName);
            var plain = CarGeo.Parse(data);
            var shifted = CarGeo.Parse(data);
            shifted.ApplyWheelShift(i);
            bool Changed(int p) => !plain.Parts[p].Vertices.SequenceEqual(shifted.Parts[p].Vertices);
            bool front = Changed(0xC) || Changed(0xE), rear = Changed(0x10) || Changed(0x12);
            var t = Cars[i].Traits;
            if (front) Assert.True(t.WheelShift, $"car {i}");
            if (rear) Assert.True(t.RearWheelShift, $"car {i}");
            if (t.WheelShift) Assert.True(front, $"car {i} front"); // car 4 has front wheels with polygons
            front4 |= front && i == 4; rearAny |= rear;
        }
        Assert.True(front4);
        Assert.True(rearAny);
    }

    [GameFact]
    public void PhysicsBytes_EqualCarDataViv_AndWhatRaceLoaderParses()
    {
        var viv = Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir);
        byte[]? File(string n) => viv.Entries.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ? viv.Get(n) : null;
        var cars = new VanillaCarSource(File).GetCars().ToArray();
        Assert.Equal(CarCatalog.VanillaCount, cars.Length);
        int raw = 0;
        for (int i = 0; i < cars.Length; i++)
        {
            string model = RaceConstants.CarNames[i];
            var specs = File(RaceLoader.SpecsFile(i, model, 0));
            var ai = File(RaceLoader.AiModelFile(i, model));
            if (specs is { Length: CarSpecs.Size } && ai is { Length: CarAiModel.Size })
            {
                var p = Assert.IsType<RawPhysics>(cars[i].Physics);
                Assert.True(specs.AsSpan().SequenceEqual(p.CarSpecs.Span), $"specs of car {i}");
                Assert.True(ai.AsSpan().SequenceEqual(p.CarAiModel.Span), $"ai of car {i}");
                raw++;
            }
            else Assert.IsType<NoPhysics>(cars[i].Physics);
        }
        Assert.True(raw >= 15, $"only {raw} cars with raw physics");
    }
}
