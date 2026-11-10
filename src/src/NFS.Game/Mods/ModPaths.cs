namespace NFS.Game.Mods;

/// <summary>Locating the mods directory and resolving files referenced by a mod (openspec modding-api-cars D5).</summary>
public static class ModPaths
{
    /// <summary>Largest file a mod may reference.</summary>
    public const long MaxFileBytes = 16L << 20;

    /// <summary>Most files and bytes of one mod that are transferred from a server (openspec modding-api-cars 5.4; local mods are not
    /// limited in total).</summary>
    public const int MaxFilesPerMod = 256;
    public const long MaxModBytes = 64L << 20;

    /// <summary>Most cars one mod may declare (checked where cars are enumerated).</summary>
    public const int MaxCarsPerMod = 64;

    /// <summary>Mods directory: <paramref name="overrideDir"/> (<c>--mods</c>) if given; else <c>mods/</c> next to the exe if it exists;
    /// else, when running from the repo, <c>mods/</c> of the nearest ancestor holding <c>src/NFS2SE.sln</c>; else <c>exeDir/mods</c> (may not exist).</summary>
    public static string ResolveModsDir(string? overrideDir, string exeDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir)) return Path.GetFullPath(overrideDir);
        string next = Path.GetFullPath(Path.Combine(exeDir, "mods"));
        if (Directory.Exists(next)) return next;
        try
        {
            for (var d = new DirectoryInfo(Path.GetFullPath(exeDir)); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "src", "NFS2SE.sln")))
                {
                    string repo = Path.Combine(d.FullName, "mods");
                    return Directory.Exists(repo) ? repo : next;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return next;
    }

    /// <summary>Resolves <paramref name="relative"/> inside <paramref name="modDir"/>. Rejects absolute paths, <c>..</c> segments, paths that leave
    /// the mod after normalisation, any symlink / junction on the way, and (when the file exists) files over <see cref="MaxFileBytes"/>.</summary>
    public static bool TryResolveFile(string modDir, string relative, out string full, out string? error)
    {
        full = "";
        error = null;
        if (string.IsNullOrWhiteSpace(relative)) { error = "empty path"; return false; }
        if (relative.IndexOf('\0') >= 0) { error = $"invalid path '{relative}'"; return false; }
        if (Path.IsPathRooted(relative) || relative[0] is '/' or '\\' || relative.Contains(':'))
        {
            error = $"path '{relative}' must be relative to the mod directory";
            return false;
        }
        var parts = relative.Split('/', '\\');
        if (parts.Contains(".."))
        {
            error = $"path '{relative}' must not contain '..'";
            return false;
        }
        try
        {
            string root = Path.GetFullPath(modDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = $"path '{relative}' leaves the mod directory";
                return false;
            }
            string cur = root;
            foreach (var seg in candidate[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                cur = Path.Combine(cur, seg);
                if (!File.Exists(cur) && !Directory.Exists(cur)) break;
                if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"path '{relative}' passes through a link ('{seg}'), which is not allowed";
                    return false;
                }
            }
            if (File.Exists(candidate))
            {
                long len = new FileInfo(candidate).Length;
                if (len > MaxFileBytes) { error = $"file '{relative}' is {len} B, over the {MaxFileBytes} B limit"; return false; }
            }
            full = candidate;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"cannot resolve '{relative}': {e.Message}";
            return false;
        }
    }

    /// <summary>Strict check of a relative file name that arrives from outside (network transfer, 5.4): <c>/</c> separated segments of
    /// <c>[A-Za-z0-9._-]</c>, none empty, none starting or ending with a dot, none a Windows device name, at most 8 segments, 128 characters.
    /// Rejects absolute paths, <c>..</c>, backslashes and drive letters.</summary>
    public static bool IsSafeRelativeName(string relative, out string? error)
    {
        error = null;
        if (relative.Length is 0 or > 128) { error = "path length must be 1..128"; return false; }
        var segments = relative.Split('/');
        if (segments.Length > 8) { error = $"path '{relative}' is too deep"; return false; }
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg.Length > 64 || seg[0] == '.' || seg[^1] == '.'
                || !seg.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            { error = $"path '{relative}' has an invalid segment"; return false; }
            string stem = seg.Contains('.') ? seg[..seg.IndexOf('.')] : seg;
            if (DeviceNames.Contains(stem.ToUpperInvariant())) { error = $"path '{relative}' uses a reserved name"; return false; }
        }
        return true;
    }

    static readonly HashSet<string> DeviceNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Checks that <paramref name="file"/> (already resolved) is a glb the game's glTF reader (<c>GltfModel</c>, NFS.Data) accepts.
    /// Truncation, bad magic, unsupported features: all become an error message instead of an exception.</summary>
    public static bool TryValidateGlb(string file, out string? error)
    {
        error = null;
        try
        {
            NFS.Data.Gltf.GltfModel.Load(file);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException
                                     or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        {
            error = $"invalid glb '{Path.GetFileName(file)}': {e.Message}";
            return false;
        }
    }
}
