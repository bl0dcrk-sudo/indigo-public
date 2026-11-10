using System.Text;
using System.Text.Json.Nodes;
using NFS.Game.Cars;
using NFS.Game.Mods;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>car.json "colors" and "paintMaterials" (openspec modding-api-cars 3.4, 4.2): parsing and validation, temp dirs only, plus the repo duster.</summary>
public sealed class CarColorsTests : IDisposable
{
    const string ModId = "test.colors";
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-carcolors-" + Guid.NewGuid().ToString("N"));
    readonly string _mod;

    public CarColorsTests()
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

    static JsonObject Car() => new()
    {
        ["schemaVersion"] = 1,
        ["id"] = "duster",
        ["names"] = new JsonObject { ["pl"] = "Duster", ["en"] = "Duster" },
        ["manufacturer"] = "Dacia",
        ["year"] = 2010,
        ["class"] = "B",
        ["engineBank"] = "mUsT",
        ["model"] = "cars/duster/duster.glb",
        ["physics"] = Physics(),
    };

    static JsonObject Color(string en, string pl, string rgb) => new()
    {
        ["names"] = new JsonObject { ["pl"] = pl, ["en"] = en },
        ["rgb"] = rgb,
    };

    void Write(JsonObject json)
    {
        string d = Path.Combine(_mod, "cars", "duster");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "car.json"), json.ToJsonString());
        File.WriteAllBytes(Path.Combine(d, "duster.glb"), MinimalGlb());
    }

    (List<CarDefinition> Cars, ModDiagnostics Diag) Load(string dir)
    {
        var diag = new ModDiagnostics();
        var mod = new LoadedMod(new ModManifest(ModId, "1.0.0", "^1.0", ModKind.Data, ["cars"]), dir);
        return ([.. new ModCarSource([mod], diag).GetCars()], diag);
    }

    string Error(JsonObject json)
    {
        Write(json);
        var (cars, diag) = Load(_mod);
        Assert.Empty(cars);
        return Assert.Single(diag.Entries, e => e.Severity == ModDiagnosticSeverity.Error).Message;
    }

    CarDefinition Parse(JsonObject json)
    {
        Write(json);
        var (cars, diag) = Load(_mod);
        Assert.Empty(diag.Entries);
        return Assert.Single(cars);
    }

    [Fact]
    public void Colors_ParsedWithNamesAndRgb()
    {
        var j = Car();
        j["colors"] = new JsonArray(Color("Atacama Orange", "Pomaranczowy", "#C8691E"), Color("Pearl Black", "Czarny", "#16171A"));
        var car = Parse(j);
        Assert.Equal(2, car.Colors.Count);
        var first = car.Colors[0];
        Assert.Equal((byte)0xC8, first.R);
        Assert.Equal((byte)0x69, first.G);
        Assert.Equal((byte)0x1E, first.B);
        Assert.Equal("Atacama Orange", first.Names["en"]);
        Assert.Equal("Pomaranczowy", first.Names["pl"]);
        Assert.Equal("Atacama Orange", first.Name("en"));
        Assert.Equal("Pomaranczowy", first.Name("pl"));
        var second = car.Colors[1];
        Assert.Equal((byte)0x16, second.R);
        Assert.Equal((byte)0x17, second.G);
        Assert.Equal((byte)0x1A, second.B);
    }

    [Fact]
    public void Colors_RgbIsCaseInsensitiveHex()
    {
        var j = Car();
        j["colors"] = new JsonArray(Color("Lower", "Dolny", "#abcdef"));
        var c = Assert.Single(Parse(j).Colors);
        Assert.Equal((0xAB, 0xCD, 0xEF), (c.R, c.G, c.B));
    }

    [Fact]
    public void Colors_AreOptional()
    {
        var car = Parse(Car());
        Assert.Empty(car.Colors);
        Assert.Empty(car.PaintMaterials);
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("red")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("C8691E")]
    public void Colors_BadRgb_ErrorNamesTheField(string rgb)
    {
        var j = Car();
        j["colors"] = new JsonArray(Color("Bad", "Zly", rgb));
        var msg = Error(j);
        Assert.Contains("colors[].rgb", msg);
        Assert.Contains(rgb, msg);
    }

    [Fact]
    public void Colors_MoreThan16_IsAnError()
    {
        var j = Car();
        var arr = new JsonArray();
        for (int i = 0; i < 17; i++) arr.Add(Color("C" + i, "K" + i, "#102030"));
        j["colors"] = arr;
        Assert.Contains("'colors'", Error(j));
    }

    [Fact]
    public void Colors_Exactly16_IsAccepted()
    {
        var j = Car();
        var arr = new JsonArray();
        for (int i = 0; i < 16; i++) arr.Add(Color("C" + i, "K" + i, "#102030"));
        j["colors"] = arr;
        Assert.Equal(16, Parse(j).Colors.Count);
    }

    [Fact]
    public void PaintMaterials_Parsed()
    {
        var j = Car();
        j["paintMaterials"] = new JsonArray("UV_body", "UV_trim");
        Assert.Equal(["UV_body", "UV_trim"], Parse(j).PaintMaterials);
    }

    [Fact]
    public void PaintMaterials_NonStringOrEmpty_IsAnError()
    {
        var j = Car();
        j["paintMaterials"] = new JsonArray(7);
        Assert.Contains("'paintMaterials'", Error(j));
        j["paintMaterials"] = new JsonArray("");
        Assert.Contains("'paintMaterials'", Error(j));
    }

    [Fact]
    public void PaintMaterials_MoreThan8_IsAnError()
    {
        var j = Car();
        var arr = new JsonArray();
        for (int i = 0; i < 9; i++) arr.Add("UV_" + i);
        j["paintMaterials"] = arr;
        Assert.Contains("'paintMaterials'", Error(j));
    }

    [ModFact]
    public void RepoDuster_HasSixColoursAndUvBodyPaint()
    {
        string? repoMods = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && repoMods == null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "mods", "indigo.duster", "1.0.0");
            if (File.Exists(Path.Combine(p, "cars", "duster", "car.json"))) repoMods = p;
        }
        Assert.True(repoMods != null, "mods/indigo.duster/1.0.0/cars/duster/car.json not found above " + AppContext.BaseDirectory);
        var (cars, diag) = Load(repoMods!);
        Assert.Empty(diag.Entries);
        var duster = Assert.Single(cars);
        Assert.Equal(5, duster.Colors.Count);
        Assert.Equal(["UV_body"], duster.PaintMaterials);
        Assert.Equal("Atacama Orange", duster.Colors[0].Name("en"));
        Assert.Equal("Fiery Red", duster.Colors[4].Name("en"));
        Assert.Equal((byte)0xA8, duster.Colors[4].R);
    }
}
