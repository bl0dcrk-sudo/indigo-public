using System.Text.Json.Nodes;
using NFS.ModApi;
using NFS.Game.Mods;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>mod.json parser: shape, required fields, semver, API range compatibility, kind, capabilities, id rules.</summary>
public class ModManifestParserTests
{
    private static JsonObject Valid() => new()
    {
        ["id"] = "indigo.duster",
        ["version"] = "1.0.0",
        ["api"] = "^1.0",
        ["kind"] = "data",
        ["capabilities"] = new JsonArray("cars"),
    };

    private static bool Parse(JsonObject obj, out string error)
    {
        var ok = ModManifestParser.TryParse(obj.ToJsonString(), out _, out var err);
        error = err ?? string.Empty;
        return ok;
    }

    private static string ErrorOf(JsonObject obj)
    {
        Assert.False(Parse(obj, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        return error;
    }

    [Fact]
    public void ValidManifest_Parses()
    {
        const string json = """{"id":"indigo.duster","version":"1.0.0","api":"^1.0","kind":"data","capabilities":["cars"]}""";

        Assert.True(ModManifestParser.TryParse(json, out var manifest, out var error));
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("indigo.duster", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("^1.0", manifest.ApiRange);
        Assert.Equal(ModKind.Data, manifest.Kind);
        Assert.Equal(new[] { "cars" }, manifest.Capabilities);
    }

    [Fact]
    public void UnknownTopLevelFields_AreIgnored()
    {
        var obj = Valid();
        obj["author"] = "someone";
        obj["extra"] = new JsonObject { ["x"] = 1 };

        Assert.True(Parse(obj, out var error), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void InvalidJson_Rejected(string json)
    {
        Assert.False(ModManifestParser.TryParse(json, out var manifest, out var error));
        Assert.Null(manifest);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("api")]
    [InlineData("kind")]
    [InlineData("capabilities")]
    public void MissingField_Rejected_NamingField(string field)
    {
        var obj = Valid();
        obj.Remove(field);

        var error = ErrorOf(obj);
        Assert.Contains($"'{field}'", error);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("a.b.c")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void BadVersion_Rejected(string version)
    {
        var obj = Valid();
        obj["version"] = version;

        Assert.Contains("'version'", ErrorOf(obj));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("^2.0")]
    [InlineData("^1.5")]
    [InlineData("^1")]
    [InlineData("~1.0")]
    [InlineData("^a.b")]
    public void IncompatibleOrMalformedApi_Rejected(string api)
    {
        var obj = Valid();
        obj["api"] = api;

        Assert.Contains("'api'", ErrorOf(obj));
    }

    [Fact]
    public void IncompatibleApi_ErrorMentionsBothVersions()
    {
        var obj = Valid();
        obj["api"] = "^1.5";

        var error = ErrorOf(obj);
        Assert.Contains("^1.5", error);
        Assert.Contains(ModApiInfo.Version, error);
    }

    [Fact]
    public void CompatibleApi_Accepted()
    {
        var obj = Valid();
        obj["api"] = "^1.0";

        Assert.True(Parse(obj, out var error), error);
    }

    [Theory]
    [InlineData("^1.0", "1.0.0", true)]
    [InlineData("^1.0", "1.5.0", true)]
    [InlineData("^1.5", "1.0.0", false)]
    [InlineData("^1.0", "2.0.0", false)]
    [InlineData("^2.0", "1.0.0", false)]
    [InlineData("^1.0", "1.0", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("^x.0", "1.0.0", false)]
    public void IsApiCompatible_Rules(string range, string apiVersion, bool expected)
    {
        Assert.Equal(expected, ModManifestParser.IsApiCompatible(range, apiVersion));
    }

    [Fact]
    public void KindCode_Rejected_AsNotSupported()
    {
        var obj = Valid();
        obj["kind"] = "code";

        Assert.Contains("code mods are not supported", ErrorOf(obj));
    }

    [Theory]
    [InlineData("mod")]
    [InlineData("DATA")]
    [InlineData("")]
    public void UnknownKind_Rejected_NamingField(string kind)
    {
        var obj = Valid();
        obj["kind"] = kind;

        Assert.Contains("'kind'", ErrorOf(obj));
    }

    [Theory]
    [InlineData("vehicles")]
    [InlineData("Cars")]
    [InlineData("tracks")]
    public void UnknownCapability_Rejected(string capability)
    {
        var obj = Valid();
        obj["capabilities"] = new JsonArray(capability);

        var error = ErrorOf(obj);
        Assert.Contains("'capabilities'", error);
        Assert.Contains(capability, error);
    }

    [Fact]
    public void EmptyCapabilities_Rejected()
    {
        var obj = Valid();
        obj["capabilities"] = new JsonArray();

        Assert.Contains("'capabilities'", ErrorOf(obj));
    }

    [Fact]
    public void NonStringCapability_Rejected()
    {
        var obj = Valid();
        obj["capabilities"] = new JsonArray(1);

        Assert.Contains("'capabilities'", ErrorOf(obj));
    }

    [Theory]
    [InlineData("nfs2se.cars")]
    [InlineData("nfs2se-x.cars")]
    [InlineData("Indigo.Duster")]
    [InlineData("indigo duster")]
    [InlineData("indigo.duster!")]
    [InlineData("indigo")]
    [InlineData("ab")]
    [InlineData("indigo.")]
    public void BadId_Rejected_NamingField(string id)
    {
        var obj = Valid();
        obj["id"] = id;

        Assert.Contains("'id'", ErrorOf(obj));
    }

    [Fact]
    public void IdLongerThan64Chars_Rejected()
    {
        var obj = Valid();
        obj["id"] = "indigo." + new string('a', 60);

        Assert.Contains("'id'", ErrorOf(obj));
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("indigo.duster")]
    [InlineData("a_b.c-d")]
    [InlineData("author9.car.set2")]
    public void GoodId_Accepted(string id)
    {
        var obj = Valid();
        obj["id"] = id;

        Assert.True(Parse(obj, out var error), error);
    }
}
