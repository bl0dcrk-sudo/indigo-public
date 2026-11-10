using System.Text;
using System.Text.Json.Nodes;
using NFS.Core.Constants;
using NFS.Game.Cars;
using NFS.Game.Mods;
using NFS.ModApi;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>car.json parsing and ModCarSource (openspec modding-api-cars 3.4); temp dirs only.</summary>
public sealed class ModCarSourceTests : IDisposable
{
    const string ModId = "test.mod";
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-modcars-" + Guid.NewGuid().ToString("N"));
    readonly string _mod;

    public ModCarSourceTests()
    {
        _mod = Path.Combine(_root, ModId);
        Directory.CreateDirectory(_mod);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    static byte[] MinimalGlb()
    {
        var json = new List<byte>(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}}"));
        while (json.Count % 4 != 0) json.Add((byte)' ');
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + json.Count));
        w.Write((uint)json.Count); w.Write(0x4E4F534Au); w.Write(json.ToArray());
        return ms.ToArray();
    }

    static JsonObject Physics() => JsonNode.Parse("""
        { "massKg": 1300, "powerKw": 110, "powerRpm": 5632, "torqueNm": 250, "torqueRpm": 3840, "redlineRpm": 6656,
          "gearRatios": [3.7, 2.0, 1.35, 1.0, 0.8, 0.65], "finalDrive": 4.0, "wheelRadiusM": 0.3, "topSpeedKmh": 180,
          "drive": "front", "wheelbaseM": 2.67 }
        """)!.AsObject();

    static JsonObject Car(string id = "duster") => new()
    {
        ["schemaVersion"] = 1,
        ["id"] = id,
        ["names"] = new JsonObject { ["pl"] = "Duster PL", ["en"] = "Duster" },
        ["descriptions"] = new JsonObject { ["pl"] = "opis", ["en"] = "description" },
        ["manufacturer"] = "Dacia",
        ["year"] = 2010,
        ["class"] = "B",
        ["traits"] = new JsonObject { ["abs"] = true },
        ["engineBank"] = "mUsT",
        ["model"] = $"cars/{id}/{id}.glb",
        ["physics"] = Physics(),
    };

    /// <summary>Writes cars/&lt;id&gt;/car.json (and the glb unless the json has none / a missing one is wanted).</summary>
    string Write(JsonObject json, string? dir = null, bool glb = true)
    {
        string id = dir ?? json["id"]!.GetValue<string>();
        string d = Path.Combine(_mod, "cars", id);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "car.json"), json.ToJsonString());
        if (glb) File.WriteAllBytes(Path.Combine(d, id + ".glb"), MinimalGlb());
        return d;
    }

    static LoadedMod Loaded(string dir, string id = ModId, string[]? caps = null) =>
        new(new ModManifest(id, "1.0.0", "^1.0", ModKind.Data, caps ?? ["cars"]), dir);

    (List<CarDefinition> Cars, ModDiagnostics Diag) Load(params LoadedMod[] mods)
    {
        var diag = new ModDiagnostics();
        return ([.. new ModCarSource(mods.Length == 0 ? [Loaded(_mod)] : mods, diag).GetCars()], diag);
    }

    CarDefinition Parse(JsonObject json, out ModDiagnostics diag, string? dir = null)
    {
        Write(json, dir);
        var (cars, d) = Load();
        diag = d;
        return Assert.Single(cars);
    }

    string Error(JsonObject json, string? dir = null, bool glb = true)
    {
        Write(json, dir, glb);
        var (cars, diag) = Load();
        Assert.Empty(cars);
        return Assert.Single(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error).Message;
    }

    [Fact]
    public void HappyPath_SiPhysics()
    {
        var car = Parse(Car(), out var diag);
        Assert.Empty(diag.Entries);
        Assert.Equal("test.mod/duster", car.Key.Value);
        Assert.Equal("Duster", car.DisplayName);                  // en first
        Assert.Null(car.LegacyIndex);
        Assert.Equal("opis", car.Descriptions["pl"]);
        Assert.Equal("description", car.Descriptions["en"]);
        Assert.Equal("Dacia", car.Manufacturer);
        Assert.Equal(2010, car.Year);
        Assert.Equal(CarRaceClass.B, car.Traits.RaceClass);
        Assert.True(car.Traits.HasAbs);
        Assert.False(car.Traits.SkidMarks);
        Assert.Equal(new EngineBank("must", "smust", "omust"), car.Traits.EngineBank);
        Assert.Equal(1300 * 65536 / 100, car.Traits.BodyMass);
        Assert.Equal(50 * 65536, car.Traits.AiTopSpeed);
        Assert.EndsWith("duster.glb", car.ModelPath);
        var si = Assert.IsType<SiPhysics>(car.Physics);
        Assert.Equal(1300, si.MassKg);
        Assert.Equal(6, si.GearRatios.Count);
        Assert.Equal(CarDrive.Front, si.Drive);
        Assert.Equal(0.3, si.WheelRadiusM);
    }

    [Fact]
    public void DimensionsAndBodyMass_AreOptionalAndValidated()
    {
        var j = Car();
        Assert.Null(Parse(j, out _).Dimensions);
        j["dimensions"] = new JsonObject { ["lengthM"] = 4.34, ["widthM"] = 1.8, ["heightM"] = 1.69 };
        j["bodyMass"] = 13.0;
        var car = Parse(j, out _);
        Assert.Equal(new CarDimensions(4.34, 1.8, 1.69), car.Dimensions);
        Assert.Equal(13 * 65536, car.Traits.BodyMass);
        j["dimensions"] = new JsonObject { ["lengthM"] = 4.34, ["widthM"] = 1.8 };
        Assert.Contains("heightM", Error(j));
    }

    [Fact]
    public void NameFallsBackToPolish_AndDescriptionsAreOptional()
    {
        var j = Car();
        j["names"] = new JsonObject { ["pl"] = "Tylko PL" };
        j.Remove("descriptions");
        var car = Parse(j, out _);
        Assert.Equal("Tylko PL", car.DisplayName);
        Assert.Empty(car.Descriptions);
    }

    [Fact]
    public void HappyPath_RawPhysics()
    {
        var j = Car();
        j.Remove("physics");
        string d = Write(j);
        var specs = new byte[356];
        BitConverter.GetBytes(1200 << 16).CopyTo(specs, 0);
        BitConverter.GetBytes(60 << 16).CopyTo(specs, 0xF4);
        File.WriteAllBytes(Path.Combine(d, "p.dat"), specs);
        File.WriteAllBytes(Path.Combine(d, "ai.dat"), new byte[524]);
        j["raw"] = new JsonObject { ["specs"] = "cars/duster/p.dat", ["ai"] = "cars/duster/ai.dat" };
        File.WriteAllText(Path.Combine(d, "car.json"), j.ToJsonString());
        var (cars, diag) = Load();
        var car = Assert.Single(cars);
        Assert.Empty(diag.Entries);
        var raw = Assert.IsType<RawPhysics>(car.Physics);
        Assert.Equal(356, raw.CarSpecs.Length);
        Assert.Equal(1200 * 65536 / 100, car.Traits.BodyMass);
        Assert.Equal(60 << 16, car.Traits.AiTopSpeed);
    }

    [Fact]
    public void Raw_WrongSizeOrMissingFile_IsRejected()
    {
        var j = Car();
        j.Remove("physics");
        string d = Write(j);
        File.WriteAllBytes(Path.Combine(d, "p.dat"), new byte[100]);
        File.WriteAllBytes(Path.Combine(d, "ai.dat"), new byte[524]);
        j["raw"] = new JsonObject { ["specs"] = "cars/duster/p.dat", ["ai"] = "cars/duster/ai.dat" };
        File.WriteAllText(Path.Combine(d, "car.json"), j.ToJsonString());
        var (_, diag) = Load();
        Assert.Contains("raw.specs", Assert.Single(diag.Entries).Message);
        j["raw"]!["specs"] = "cars/duster/none.dat";
        File.WriteAllText(Path.Combine(d, "car.json"), j.ToJsonString());
        (_, diag) = Load();
        Assert.Contains("not found", Assert.Single(diag.Entries).Message);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("id")]
    [InlineData("names")]
    [InlineData("class")]
    [InlineData("engineBank")]
    [InlineData("model")]
    public void MissingRequiredField_NamesIt(string field)
    {
        var j = Car();
        j.Remove(field);
        Assert.Contains($"'{field}'", Error(j, dir: "duster"));
    }

    [Theory]
    [InlineData("massKg")]
    [InlineData("gearRatios")]
    [InlineData("drive")]
    [InlineData("topSpeedKmh")]
    public void MissingPhysicsField_NamesIt(string field)
    {
        var j = Car();
        j["physics"]!.AsObject().Remove(field);
        Assert.Contains($"'{field}'", Error(j));
    }

    [Fact]
    public void BothPhysicsAndRaw_IsRejected()
    {
        var j = Car();
        j["raw"] = new JsonObject { ["specs"] = "a", ["ai"] = "b" };
        Assert.Contains("not both", Error(j));
    }

    [Fact]
    public void NeitherPhysicsNorRaw_IsRejected()
    {
        var j = Car();
        j.Remove("physics");
        Assert.Contains("'physics'", Error(j));
    }

    [Fact]
    public void WrongSchemaVersion_IsRejected()
    {
        var j = Car();
        j["schemaVersion"] = 2;
        Assert.Contains("schemaVersion", Error(j));
    }

    [Fact]
    public void UnknownBank_IsRejected()
    {
        var j = Car();
        j["engineBank"] = "NOPE";
        Assert.Contains("engineBank", Error(j));
    }

    [Theory]
    [InlineData("MacF1", "sMacF1", "oMacF1")]
    [InlineData("futur", "sfutur", "ofutur")]
    public void Bank_MapsToSplitAndOpponentBanks(string bank, string split, string opp)
    {
        var j = Car();
        j["engineBank"] = bank;
        var car = Parse(j, out _);
        Assert.Equal(new EngineBank(bank, split, opp), car.Traits.EngineBank);
    }

    [Fact]
    public void EveryPlayerBank_IsAccepted()
    {
        for (int i = 0; i < AudioConstants.PlayerBanks.Count; i++)
        {
            var j = Car();
            j["engineBank"] = AudioConstants.PlayerBanks[i];
            var car = Parse(j, out _);
            Assert.Equal(AudioConstants.SplitBanks[i], car.Traits.EngineBank.Split);
            Assert.Equal(AudioConstants.OpponentBanks[i], car.Traits.EngineBank.Opponent);
            Directory.Delete(Path.Combine(_mod, "cars"), true);
        }
    }

    [Theory]
    [InlineData("../outside.glb")]
    [InlineData("/etc/x.glb")]
    [InlineData("C:/x.glb")]
    [InlineData("cars/../../x.glb")]
    public void ModelPathEscape_IsRejected(string path)
    {
        var j = Car();
        j["model"] = path;
        Assert.Contains("model", Error(j));
    }

    [Fact]
    public void ModelMissing_NotGlb_OrCorrupt_IsRejected()
    {
        var j = Car();
        Assert.Contains("not found", Error(j, glb: false));
        j["model"] = "cars/duster/duster.obj";
        Write(j);
        File.WriteAllText(Path.Combine(_mod, "cars", "duster", "duster.obj"), "v 0 0 0");
        var (_, diag) = Load();
        Assert.Contains(".glb", Assert.Single(diag.Entries).Message);
        j["model"] = "cars/duster/duster.glb";
        Write(j);
        File.WriteAllBytes(Path.Combine(_mod, "cars", "duster", "duster.glb"), [1, 2, 3, 4]);
        (_, diag) = Load();
        Assert.Contains("invalid glb", Assert.Single(diag.Entries).Message);
    }

    [Theory]
    [InlineData("A", CarRaceClass.A)]
    [InlineData("C", CarRaceClass.C)]
    [InlineData("D", CarRaceClass.D)]
    [InlineData("E", CarRaceClass.E)]
    [InlineData("F", CarRaceClass.F)]
    [InlineData("G", CarRaceClass.G)]
    public void ClassLetters_AToG_AreAccepted(string letter, CarRaceClass expected)
    {
        var j = Car();
        j["class"] = letter;
        var car = Parse(j, out var diag);
        Assert.Empty(diag.Entries);
        Assert.Equal(expected, car.Traits.RaceClass);
    }

    [Theory]
    [InlineData("H")]
    [InlineData("g")]
    public void UnknownClassLetter_IsRejectedWithADiagnostic(string letter)
    {
        var j = Car();
        j["class"] = letter;
        string message = Error(j);
        Assert.Contains("'class'", message);
        Assert.Contains("A, B, C, D, E, F or G", message);
    }

    [Fact]
    public void BadClass_BadJson_NotObject_AreRejected()
    {
        var j = Car();
        j["class"] = "S";
        Assert.Contains("'class'", Error(j));
        string d = Path.Combine(_mod, "cars", "duster");
        File.WriteAllText(Path.Combine(d, "car.json"), "{ nope");
        Assert.Contains("invalid JSON", Assert.Single(Load().Diag.Entries).Message);
        File.WriteAllText(Path.Combine(d, "car.json"), "[1]");
        Assert.Contains("object", Assert.Single(Load().Diag.Entries).Message);
    }

    [Fact]
    public void IdMustEqualDirectoryName()
    {
        var j = Car("other");
        Assert.Contains("directory name", Error(j, dir: "duster"));
    }

    [Theory]
    [InlineData("massKg", 0.0, "massKg")]
    [InlineData("massKg", 100000.0, "massKg")]
    [InlineData("torqueNm", -10.0, "torqueNm")]
    [InlineData("topSpeedKmh", 2000.0, "topSpeedKmh")]
    public void SiOutOfRange_RejectsTheCar_NamingTheField(string field, double value, string expected)
    {
        var j = Car();
        j["physics"]![field] = value;
        string msg = Error(j);
        Assert.Contains($"'{expected}'", msg);
        Assert.Contains("physics", msg);
    }

    [Fact]
    public void ZeroGears_RejectsTheCar()
    {
        var j = Car();
        j["physics"]!["gearRatios"] = new JsonArray();
        Assert.Contains("'gearRatios'", Error(j));
    }

    [Fact]
    public void UnknownFields_OnlyWarn()
    {
        var j = Car();
        j["colour"] = "red";
        j["traits"]!["turbo"] = true;
        var car = Parse(j, out var diag);
        Assert.NotNull(car);
        Assert.False(diag.HasErrors);
        Assert.Equal(2, diag.Entries.Count(e => e.Severity == ModDiagnosticSeverity.Warning));
    }

    [Fact]
    public void BadCarIsSkipped_OthersContinue()
    {
        var bad = Car("a-bad");
        bad["physics"]!["massKg"] = 0;
        Write(bad);
        Write(Car("b-good"));
        var (cars, diag) = Load();
        Assert.Equal("test.mod/b-good", Assert.Single(cars).Key.Value);
        Assert.Single(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error);
    }

    [Fact]
    public void ModWithoutCarsCapability_IsIgnored()
    {
        Write(Car());
        var (cars, diag) = Load(Loaded(_mod, caps: []));
        Assert.Empty(cars);
        Assert.Empty(diag.Entries);
    }

    [Fact]
    public void MoreThanMaxCars_StopsWithError()
    {
        for (int i = 0; i < ModPaths.MaxCarsPerMod + 1; i++)
        {
            string id = $"c{i:000}";
            string d = Path.Combine(_mod, "cars", id);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "car.json"), Car(id).ToJsonString());
            File.WriteAllBytes(Path.Combine(d, id + ".glb"), MinimalGlb());
        }
        var (cars, diag) = Load();
        Assert.Equal(ModPaths.MaxCarsPerMod, cars.Count);
        Assert.Contains("more than", Assert.Single(diag.Entries).Message);
    }

    [Fact]
    public void Catalog_RegistersModCarsAfterVanilla_FromNumber0x35()
    {
        Write(Car("a"));
        Write(Car("b"));
        var diag = new ModDiagnostics();
        var (catalog, catDiag) = CarCatalog.Build([new VanillaCarSource(), new ModCarSource([Loaded(_mod)], diag)]);
        Assert.Empty(catDiag);
        Assert.Equal(CarCatalog.VanillaCount + 2, catalog.Count);
        var a = CarKey.Parse("test.mod/a");
        var b = CarKey.Parse("test.mod/b");
        Assert.Equal(0x35, catalog.NumberOf(a));
        Assert.Equal(0x36, catalog.NumberOf(b));
        Assert.True(catalog.TryGetByNumber(0x35, out var car) && car.Key == a);
        for (int i = 0; i < CarCatalog.VanillaCount; i++)                  // vanilla untouched
        {
            Assert.True(catalog.TryGetByNumber(i, out var v));
            Assert.Equal(i, v.LegacyIndex);
            Assert.True(v.Key.IsVanilla);
        }
    }

    [Fact]
    public void DuplicateKeyAcrossModsIsImpossible_SameIdInTwoMods_GetsDistinctKeys()
    {
        string other = Path.Combine(_root, "other.mod");
        Directory.CreateDirectory(other);
        Write(Car());
        string d = Path.Combine(other, "cars", "duster");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "car.json"), Car().ToJsonString());
        File.WriteAllBytes(Path.Combine(d, "duster.glb"), MinimalGlb());
        var (catalog, catDiag) = CarCatalog.Build([new ModCarSource([Loaded(_mod), Loaded(other, "other.mod")], new ModDiagnostics())]);
        Assert.Equal(2, catalog.Count);
        Assert.Empty(catDiag);
    }
}
