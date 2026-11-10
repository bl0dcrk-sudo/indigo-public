using NFS.ModApi;

namespace NFS.Game.Mods;

/// <summary>A mod accepted by <see cref="ModLoader"/>.</summary>
public sealed record LoadedMod(ModManifest Manifest, string Dir);

/// <summary>Scans <c>modsDir/&lt;id&gt;/&lt;version&gt;/mod.json</c> (openspec modding-api-cars D5, 5.3). Never throws on bad data: every problem becomes a
/// <see cref="ModDiagnostics"/> entry and that mod version is skipped. The id directory must equal the mod id, the version directory must equal
/// the manifest <c>version</c> (ordinal). Several versions of one mod may lie side by side; the highest by semver (numeric, 1.10.0 &gt; 1.9.0) is loaded.
/// The old layout <c>mods/&lt;id&gt;/mod.json</c> is rejected with a diagnostic.</summary>
public static class ModLoader
{
    public const string ManifestFile = "mod.json";

    public static IReadOnlyList<LoadedMod> Load(string modsDir, ModDiagnostics diag)
    {
        var result = new List<LoadedMod>();
        try
        {
            if (!Directory.Exists(modsDir)) return result;
            var dirs = Directory.GetDirectories(modsDir);
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith('.')) continue;   // ".incoming": mods being received from a server (ModStaging)
                try { LoadId(dir, name, result, diag); }
                catch (Exception e) { diag.Error(name, $"cannot load mod: {e.Message}"); }
            }
        }
        catch (Exception e)
        {
            diag.Error("", $"cannot scan mods directory '{modsDir}': {e.Message}");
        }
        return result;
    }

    static void LoadId(string idDir, string id, List<LoadedMod> result, ModDiagnostics diag)
    {
        if ((File.GetAttributes(idDir) & FileAttributes.ReparsePoint) != 0)
        {
            diag.Warning(id, "mod directory is a link, skipped");
            return;
        }
        if (File.Exists(Path.Combine(idDir, ManifestFile)))
        {
            diag.Error(id, $"old layout: {ManifestFile} directly in the mod directory; move it to '{id}/<version>/' (the directory name must equal the manifest version)");
        }
        LoadedMod? best = null;
        var versions = Directory.GetDirectories(idDir);
        Array.Sort(versions, StringComparer.Ordinal);
        foreach (var vdir in versions)
        {
            string vname = Path.GetFileName(vdir);
            string label = id + "/" + vname;
            LoadedMod? m;
            try { m = LoadOne(vdir, id, vname, label, diag); }
            catch (Exception e) { diag.Error(label, $"cannot load mod: {e.Message}"); continue; }
            if (m != null && (best == null || CompareVersions(m.Manifest.Version, best.Manifest.Version) > 0)) best = m;
        }
        if (best != null) result.Add(best);
    }

    /// <summary>Loads exactly <c>modsDir/id/version</c> (not the newest version), with the same checks as <see cref="Load"/>; null plus
    /// a diagnostic when it is missing or invalid.</summary>
    public static LoadedMod? LoadExact(string modsDir, string id, string version, ModDiagnostics diag)
    {
        string label = id + "/" + version;
        try
        {
            if (!ModPaths.IsSafeRelativeName(id, out _) || !ModPaths.IsSafeRelativeName(version, out _)) { diag.Error(label, "invalid mod id or version"); return null; }
            string idDir = Path.Combine(modsDir, id), dir = Path.Combine(idDir, version);
            if (!Directory.Exists(dir)) { diag.Error(label, "mod version is not installed"); return null; }
            if ((File.GetAttributes(idDir) & FileAttributes.ReparsePoint) != 0) { diag.Warning(id, "mod directory is a link, skipped"); return null; }
            return Validate(dir, id, version, diag);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            diag.Error(label, $"cannot load mod: {e.Message}");
            return null;
        }
    }

    /// <summary>Validates the mod in <paramref name="dir"/> as if it lay in <c>mods/id/version</c> (the directory name itself is not
    /// checked): manifest, id and version equal to the arguments, no links. Used for a mod received from a server before it is installed.</summary>
    public static LoadedMod? Validate(string dir, string id, string version, ModDiagnostics diag)
    {
        string label = id + "/" + version;
        try { return LoadOne(dir, id, version, label, diag); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            diag.Error(label, $"cannot load mod: {e.Message}");
            return null;
        }
    }

    static LoadedMod? LoadOne(string dir, string id, string versionDirName, string label, ModDiagnostics diag)
    {
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
        {
            diag.Warning(label, "version directory is a link, skipped");
            return null;
        }
        string manifestPath = Path.Combine(dir, ManifestFile);
        if (!File.Exists(manifestPath)) return null; // not a mod version
        if (!ModPaths.TryResolveFile(dir, ManifestFile, out manifestPath, out var pathError))
        {
            diag.Error(label, pathError!);
            return null;
        }
        if (!ModManifestParser.TryParse(File.ReadAllText(manifestPath), out var manifest, out var error))
        {
            diag.Error(label, $"{ManifestFile}: {error}");
            return null;
        }
        if (!string.Equals(manifest!.Id, id, StringComparison.Ordinal))
        {
            diag.Error(label, $"directory name must equal the mod id '{manifest.Id}'");
            return null;
        }
        if (!string.Equals(manifest.Version, versionDirName, StringComparison.Ordinal))
        {
            diag.Error(label, $"version directory name '{versionDirName}' must equal the manifest version '{manifest.Version}'");
            return null;
        }
        return new LoadedMod(manifest, dir);
    }

    /// <summary>Numeric semver order of two <c>MAJOR.MINOR.PATCH</c> strings (already validated by the manifest parser).</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? System.Numerics.BigInteger.Parse(pa[i], System.Globalization.CultureInfo.InvariantCulture) : 0;
            var y = i < pb.Length ? System.Numerics.BigInteger.Parse(pb[i], System.Globalization.CultureInfo.InvariantCulture) : 0;
            int c = x.CompareTo(y);
            if (c != 0) return c;
        }
        return 0;
    }
}
