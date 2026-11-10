using System.Diagnostics;
using System.Text;
using NFS.Game.Mods;
using NFS.ModApi;
using Xunit;

namespace NFS.Tests.Mods;

public sealed class ModLoaderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-modloader-" + Guid.NewGuid().ToString("N"));
    readonly List<string> _links = [];

    public ModLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var l in _links) { try { Directory.Delete(l); } catch (Exception) { } } // non-recursive: never through a junction
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    static string ManifestJson(string id) =>
        "{ \"id\": \"" + id + "\", \"version\": \"1.0.0\", \"api\": \"^1.0\", \"kind\": \"data\", \"capabilities\": [\"cars\"] }";

    /// <summary>Writes <c>modsDir/dirName/version/mod.json</c>; returns the version directory.</summary>
    static string MakeMod(string modsDir, string dirName, string json, string version = "1.0.0")
    {
        string d = Path.Combine(modsDir, dirName, version);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "mod.json"), json);
        return d;
    }

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

    bool TryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
                    { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p!.WaitForExit();
            }
            else Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception) { return false; }
        if (!Directory.Exists(link)) return false;
        _links.Add(link);
        return true;
    }

    [Fact]
    public void ValidMod_IsLoaded()
    {
        MakeMod(_root, "test.valid", ManifestJson("test.valid"));
        var diag = new ModDiagnostics();
        var m = Assert.Single(ModLoader.Load(_root, diag));
        Assert.Equal("test.valid", m.Manifest.Id);
        Assert.Equal(Path.Combine(_root, "test.valid", "1.0.0"), m.Dir);
        Assert.Empty(diag.Entries);
    }

    static string VersionedJson(string id, string version) => ManifestJson(id).Replace("\"1.0.0\"", "\"" + version + "\"");

    [Fact]
    public void TwoVersions_NewestIsLoaded()
    {
        MakeMod(_root, "test.multi", VersionedJson("test.multi", "1.0.0"), "1.0.0");
        MakeMod(_root, "test.multi", VersionedJson("test.multi", "1.2.0"), "1.2.0");
        var diag = new ModDiagnostics();
        var m = Assert.Single(ModLoader.Load(_root, diag));
        Assert.Equal("1.2.0", m.Manifest.Version);
        Assert.Equal(Path.Combine(_root, "test.multi", "1.2.0"), m.Dir);
        Assert.Empty(diag.Entries);
    }

    [Fact]
    public void Versions_AreOrderedNumericallyNotLexically()
    {
        MakeMod(_root, "test.sem", VersionedJson("test.sem", "1.9.0"), "1.9.0");
        MakeMod(_root, "test.sem", VersionedJson("test.sem", "1.10.0"), "1.10.0");
        MakeMod(_root, "test.sem", VersionedJson("test.sem", "1.2.0"), "1.2.0");
        Assert.Equal("1.10.0", Assert.Single(ModLoader.Load(_root, new ModDiagnostics())).Manifest.Version);
        Assert.True(ModLoader.CompareVersions("1.10.0", "1.9.0") > 0);
        Assert.True(ModLoader.CompareVersions("2.0.0", "10.0.0") < 0);
        Assert.Equal(0, ModLoader.CompareVersions("1.0.0", "1.0.0"));
    }

    [Fact]
    public void VersionDirDifferentFromManifestVersion_IsRejected_OtherVersionStillLoads()
    {
        MakeMod(_root, "test.ver", VersionedJson("test.ver", "1.0.0"), "1.0.0");
        MakeMod(_root, "test.ver", VersionedJson("test.ver", "3.0.0"), "2.0.0");   // directory says 2.0.0, manifest 3.0.0
        var diag = new ModDiagnostics();
        Assert.Equal("1.0.0", Assert.Single(ModLoader.Load(_root, diag)).Manifest.Version);
        var e = Assert.Single(diag.Entries);
        Assert.Equal(ModDiagnosticSeverity.Error, e.Severity);
        Assert.Contains("must equal the manifest version", e.Message);
    }

    [Fact]
    public void OldLayout_ManifestDirectlyInIdDir_IsRejectedWithDiagnostic()
    {
        string d = Path.Combine(_root, "test.old");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "mod.json"), ManifestJson("test.old"));
        var diag = new ModDiagnostics();
        Assert.Empty(ModLoader.Load(_root, diag));
        var e = Assert.Single(diag.Entries);
        Assert.Equal(ModDiagnosticSeverity.Error, e.Severity);
        Assert.Contains("old layout", e.Message);
    }

    [Fact]
    public void MissingModsDir_IsEmptyWithoutDiagnostics()
    {
        var diag = new ModDiagnostics();
        Assert.Empty(ModLoader.Load(Path.Combine(_root, "nope"), diag));
        Assert.Empty(diag.Entries);
    }

    [Fact]
    public void DirWithoutManifest_IsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "just.a.dir"));
        var diag = new ModDiagnostics();
        Assert.Empty(ModLoader.Load(_root, diag));
        Assert.Empty(diag.Entries);
    }

    [Fact]
    public void BadJson_GivesErrorAndOtherModsStillLoad()
    {
        MakeMod(_root, "test.bad", "{ not json");
        MakeMod(_root, "test.good", ManifestJson("test.good"));
        var diag = new ModDiagnostics();
        Assert.Equal("test.good", Assert.Single(ModLoader.Load(_root, diag)).Manifest.Id);
        var e = Assert.Single(diag.Entries);
        Assert.Equal(ModDiagnosticSeverity.Error, e.Severity);
        Assert.Equal("test.bad/1.0.0", e.ModId);
    }

    [Fact]
    public void DirNameDifferentFromId_IsSkipped()
    {
        MakeMod(_root, "other.name", ManifestJson("test.mismatch"));
        var diag = new ModDiagnostics();
        Assert.Empty(ModLoader.Load(_root, diag));
        Assert.Contains("must equal the mod id", Assert.Single(diag.Entries).Message);
    }

    [Fact]
    public void SecondDirClaimingAnExistingId_IsSkipped()
    {
        MakeMod(_root, "test.one", ManifestJson("test.one"));
        MakeMod(_root, "test.two", ManifestJson("test.one"));
        var diag = new ModDiagnostics();
        Assert.Equal("test.one", Assert.Single(ModLoader.Load(_root, diag)).Manifest.Id);
        Assert.Equal(ModDiagnosticSeverity.Error, Assert.Single(diag.Entries).Severity);
    }

    [Fact]
    public void ModDirThatIsALink_IsSkippedWithWarning()
    {
        string target = Path.GetDirectoryName(MakeMod(Path.Combine(_root, "elsewhere"), "test.linked", ManifestJson("test.linked")))!;
        string mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(mods);
        if (!TryLink(Path.Combine(mods, "test.linked"), target)) return;
        var diag = new ModDiagnostics();
        Assert.Empty(ModLoader.Load(mods, diag));
        Assert.Equal(ModDiagnosticSeverity.Warning, Assert.Single(diag.Entries).Severity);
    }

    [Theory]
    [InlineData("../x.glb")]
    [InlineData("a/../../x.glb")]
    [InlineData("a\\..\\..\\x.glb")]
    [InlineData("/etc/x.glb")]
    [InlineData("\\x.glb")]
    [InlineData("C:\\x.glb")]
    [InlineData("")]
    public void TryResolveFile_RejectsOutsidePaths(string rel)
    {
        string mod = MakeMod(_root, "test.paths", ManifestJson("test.paths"));
        Assert.False(ModPaths.TryResolveFile(mod, rel, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void TryResolveFile_RejectsAbsolutePathInsideTheMod()
    {
        string mod = MakeMod(_root, "test.abs", ManifestJson("test.abs"));
        Assert.False(ModPaths.TryResolveFile(mod, Path.Combine(mod, "mod.json"), out _, out _));
    }

    [Fact]
    public void TryResolveFile_AcceptsRelativeFile()
    {
        string mod = MakeMod(_root, "test.ok", ManifestJson("test.ok"));
        Directory.CreateDirectory(Path.Combine(mod, "cars"));
        File.WriteAllBytes(Path.Combine(mod, "cars", "a.glb"), MinimalGlb());
        Assert.True(ModPaths.TryResolveFile(mod, "cars/a.glb", out var full, out var err), err);
        Assert.Equal(Path.Combine(mod, "cars", "a.glb"), full);
        Assert.True(ModPaths.TryResolveFile(mod, "cars/missing.glb", out _, out _)); // existence is the caller's business
    }

    [Fact]
    public void TryResolveFile_RejectsJunctionOutOfTheMod()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "x.glb"), MinimalGlb());
        string mod = MakeMod(_root, "test.link", ManifestJson("test.link"));
        if (!TryLink(Path.Combine(mod, "cars"), outside)) return;
        Assert.False(ModPaths.TryResolveFile(mod, "cars/x.glb", out _, out var err));
        Assert.Contains("link", err);
    }

    [Fact]
    public void TryResolveFile_RejectsTooBigFile()
    {
        string mod = MakeMod(_root, "test.big", ManifestJson("test.big"));
        using (var f = File.Create(Path.Combine(mod, "big.bin"))) f.SetLength(ModPaths.MaxFileBytes + 1);
        Assert.False(ModPaths.TryResolveFile(mod, "big.bin", out _, out var err));
        Assert.Contains("limit", err);
    }

    [Fact]
    public void Glb_Valid_Passes_Truncated_Fails()
    {
        byte[] glb = MinimalGlb();
        string good = Path.Combine(_root, "good.glb"), cut = Path.Combine(_root, "cut.glb"), junk = Path.Combine(_root, "junk.glb");
        File.WriteAllBytes(good, glb);
        File.WriteAllBytes(cut, glb.Take(glb.Length - 7).ToArray());
        File.WriteAllBytes(junk, [1, 2, 3]);
        Assert.True(ModPaths.TryValidateGlb(good, out var e0), e0);
        Assert.False(ModPaths.TryValidateGlb(cut, out var e1));
        Assert.Contains("truncated", e1);
        Assert.False(ModPaths.TryValidateGlb(junk, out _));
        Assert.False(ModPaths.TryValidateGlb(Path.Combine(_root, "missing.glb"), out var e2));
        Assert.NotNull(e2);
    }

    [Fact]
    public void ResolveModsDir_OverrideWins()
    {
        string o = Path.Combine(_root, "custom");
        Assert.Equal(o, ModPaths.ResolveModsDir(o, _root));
    }

    [Fact]
    public void ResolveModsDir_NextToExe()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        Assert.Equal(Path.Combine(_root, "mods"), ModPaths.ResolveModsDir(null, _root));
    }

    [Fact]
    public void ResolveModsDir_WalksUpToRepo()
    {
        string repo = Path.Combine(_root, "repo");
        string exe = Path.Combine(repo, "src", "src", "NFS.Desktop", "bin", "Release");
        Directory.CreateDirectory(exe);
        File.WriteAllText(Path.Combine(repo, "src", "NFS2SE.sln"), "");
        Directory.CreateDirectory(Path.Combine(repo, "mods"));
        Assert.Equal(Path.Combine(repo, "mods"), ModPaths.ResolveModsDir(null, exe));
    }

    [Fact]
    public void ResolveModsDir_NothingFound_ReturnsNextToExeAndLoaderIsEmpty()
    {
        string exe = Path.Combine(_root, "bin");
        Directory.CreateDirectory(exe);
        string dir = ModPaths.ResolveModsDir(null, exe);
        Assert.Equal(Path.Combine(exe, "mods"), dir);
        Assert.Empty(ModLoader.Load(dir, new ModDiagnostics()));
    }

    [Fact]
    public void Session_CollectsModsAndDiagnostics_AndNeverThrows()
    {
        string mods = Path.Combine(_root, "m");
        MakeMod(mods, "test.fine", ManifestJson("test.fine"));
        MakeMod(mods, "test.broken", "[]");
        var s = ModSession.Create(mods, _root);
        Assert.Single(s.Mods);
        Assert.Single(s.Diagnostics.Entries);
        Assert.Empty(ModSession.Create("\0bad|path", _root).Mods);
    }

    [Fact]
    public void RepoMods_LoadWithoutErrors()
    {
        string dir = ModPaths.ResolveModsDir(null, AppContext.BaseDirectory);
        if (!File.Exists(Path.Combine(dir, "indigo.duster", "1.0.0", "mod.json"))) return; // not running from the repo
        var diag = new ModDiagnostics();
        Assert.Contains(ModLoader.Load(dir, diag), m => m.Manifest.Id == "indigo.duster");
        Assert.False(diag.HasErrors);
    }
}
