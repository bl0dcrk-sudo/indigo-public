using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NFS.Game.Mods;

/// <summary>SHA-256 (hex) over a mod directory: every file in ordinal order of its relative path ('/' separators), path and content.
/// Cached per directory for the session (mods are frozen after startup).</summary>
public static class ModFingerprint
{
    static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Of(LoadedMod mod) => Cache.GetOrAdd(Path.GetFullPath(mod.Dir), Compute);

    /// <summary>The same hash of any directory, not cached (a mod being received is hashed before it has a place in <c>mods/</c>).</summary>
    public static string OfDirectory(string dir) => Compute(Path.GetFullPath(dir));

    static string Compute(string dir)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var rel in files)
        {
            h.AppendData(Encoding.UTF8.GetBytes(rel + "\n"));
            h.AppendData(File.ReadAllBytes(Path.Combine(dir, rel)));
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }
}
