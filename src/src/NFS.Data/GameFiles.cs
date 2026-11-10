namespace NFS.Data;

/// <summary>Locates the original game data (never shipped with this project) and resolves paths
/// case-insensitively (the data uses upper-case names). The port runs from the data alone: a game directory is
/// recognised by its <c>GAMEDATA</c> and <c>FEDATA</c> folders, the executable is not needed.</summary>
public static class GameFiles
{
    public const string EnvVar = "NFS2SE_GAME_DIR";

    /// <summary>Search order: explicit path (--game-dir), %NFS2SE_GAME_DIR%, the exe directory, <c>NFS2SE</c>
    /// subfolder of the exe directory, then game_src/NFS2SE found by walking up from the exe directory. A candidate
    /// counts only when it holds the game data (<see cref="IsGameDir"/>).</summary>
    public static string FindGameDir(string? explicitDir = null) =>
        TryFindGameDir(explicitDir) ?? throw new DirectoryNotFoundException(
            $"Game data ({DataDirName}/ and {FeDirName}/ folders) not found. Pass the game directory or set {EnvVar}.");

    public static string? TryFindGameDir(string? explicitDir = null) =>
        FindIn(explicitDir, Environment.GetEnvironmentVariable(EnvVar), AppContext.BaseDirectory);

    /// <summary>Candidate directories in search order (full paths), for diagnostics when nothing is found.</summary>
    public static IReadOnlyList<string> SearchedPaths(string? explicitDir = null) =>
        Candidates(explicitDir, Environment.GetEnvironmentVariable(EnvVar), AppContext.BaseDirectory)
            .Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c))).ToList();

    /// <summary>The search of <see cref="TryFindGameDir"/> with its inputs passed in (testable without touching env vars).</summary>
    public static string? FindIn(string? explicitDir, string? envDir, string appBaseDir)
    {
        foreach (var c in Candidates(explicitDir, envDir, appBaseDir))
            if (IsGameDir(c)) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(c));
        return null;
    }

    public const string DataDirName = "GAMEDATA";
    public const string FeDirName = "FEDATA";

    /// <summary>True when <paramref name="dir"/> holds the game data the port reads: GAMEDATA (tracks, cars, audio) and
    /// FEDATA (front end). The game executable is not checked.</summary>
    public static bool IsGameDir(string dir) =>
        Directory.Exists(dir) && ResolveIn(dir, DataDirName) is { } g && Directory.Exists(g)
        && ResolveIn(dir, FeDirName) is { } f && Directory.Exists(f);

    private static IEnumerable<string> Candidates(string? explicitDir, string? envDir, string appBaseDir)
    {
        if (!string.IsNullOrEmpty(explicitDir)) yield return explicitDir;
        if (!string.IsNullOrEmpty(envDir)) yield return envDir;
        yield return appBaseDir;
        yield return Path.Combine(appBaseDir, "NFS2SE");
        for (var dir = new DirectoryInfo(appBaseDir); dir != null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "game_src", "NFS2SE");
    }

    /// <summary>Resolves a relative path ('/' or '\' separated) under <paramref name="root"/> ignoring case of each
    /// segment. Returns null when not found.</summary>
    public static string? ResolveIn(string root, string relative)
    {
        string cur = root;
        foreach (var seg in relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = Path.Combine(cur, seg);
            if (File.Exists(exact) || Directory.Exists(exact)) { cur = MatchCase(cur, seg) ?? exact; continue; }
            var found = MatchCase(cur, seg);
            if (found == null) return null;
            cur = found;
        }
        return cur;
    }

    private static string? MatchCase(string dir, string name)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var e in Directory.EnumerateFileSystemEntries(dir))
            if (string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>Full path of a game file (case-insensitive), or throws <see cref="FileNotFoundException"/>.</summary>
    public static string FullPath(string relative, string? gameDir = null) =>
        ResolveIn(FindGameDir(gameDir), relative) ?? throw new FileNotFoundException("game file not found", relative);

    public static bool Exists(string relative, string? gameDir = null) =>
        TryFindGameDir(gameDir) is { } root && ResolveIn(root, relative) is { } p && File.Exists(p);

    /// <summary>Raw bytes (no decompression). [ref:0946]</summary>
    public static byte[] ReadRaw(string relative, string? gameDir = null) => File.ReadAllBytes(FullPath(relative, gameDir));

    /// <summary>Bytes, unpacked when the header says EA-compressed. [ref:0947]</summary>
    public static byte[] Read(string relative, string? gameDir = null) => EaCompression.UnpackIfCompressed(ReadRaw(relative, gameDir));

    /// <summary>All files under the game dir (relative paths with '/'), optionally filtered by extension(s).</summary>
    public static IEnumerable<string> Enumerate(string? subDir = null, string? gameDir = null, params string[] extensions)
    {
        var root = FindGameDir(gameDir);
        var start = subDir == null ? root : ResolveIn(root, subDir);
        if (start == null || !Directory.Exists(start)) yield break;
        foreach (var f in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (extensions.Length > 0 && !extensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            yield return Path.GetRelativePath(root, f).Replace('\\', '/');
        }
    }
}
