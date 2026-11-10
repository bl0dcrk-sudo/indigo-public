using NFS.Data;

namespace NFS.Desktop;

/// <summary>Game data directory choice (Android first run, also usable on the desktop): what counts as a valid directory and
/// how the on-screen browser walks the file system. Plain File IO only (the game reads files directly), no window, testable
/// with temporary directories.</summary>
public static class DataDirResolver
{
    /// <summary>The game directory for a path the user picked: the path itself when it holds GAMEDATA and FEDATA, else its
    /// <c>NFS2SE</c> subfolder (the user pointed at the parent of the installation); null when neither is a game directory.
    /// Names are matched case-insensitively (the Android file system is case-sensitive, the data is upper-case).</summary>
    public static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Directory.Exists(path)) return null;
            if (GameFiles.IsGameDir(path)) return Path.TrimEndingDirectorySeparator(path);
            if (GameFiles.ResolveIn(path, "NFS2SE") is { } sub && Directory.Exists(sub) && GameFiles.IsGameDir(sub))
                return Path.TrimEndingDirectorySeparator(sub);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Start points of the browser on Android: the app's own files dir (adb push target), the shared storage
    /// <c>/storage/emulated/0</c>, the volumes the platform reports (<paramref name="volumes"/>: SD cards, USB sticks – listing
    /// <c>/storage</c> itself is often denied) and every other volume under <c>/storage</c> – only those that exist.</summary>
    public static IReadOnlyList<string> DiscoverRoots(string? appDataDir, string storageDir = "/storage", IEnumerable<string>? volumes = null)
    {
        var roots = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !roots.Contains(p)) roots.Add(p); }
        Add(appDataDir);
        Add(Path.Combine(storageDir, "emulated", "0"));
        foreach (var v in volumes ?? []) Add(VolumeRoot(v));
        try
        {
            foreach (var d in Directory.EnumerateDirectories(storageDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var n = Path.GetFileName(d);
                if (n is "emulated" or "self") continue;
                Add(d);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return roots;
    }

    /// <summary>The volume root of an app-specific dir (<c>/storage/1234-ABCD/Android/data/pkg/files</c> → <c>/storage/1234-ABCD</c>);
    /// other paths unchanged.</summary>
    public static string VolumeRoot(string path)
    {
        int i = path.IndexOf("/Android/", StringComparison.Ordinal);
        return i > 0 ? path[..i] : Path.TrimEndingDirectorySeparator(path);
    }
}

/// <summary>Directory-only browser. <see cref="Current"/> = null shows the start points; otherwise the sub-directories of the
/// current folder (hidden entries starting with '.' are skipped; unreadable folders show as empty with <see cref="Error"/>).</summary>
public sealed class DirBrowser
{
    readonly IReadOnlyList<string> _roots;
    public string? Current { get; private set; }
    public IReadOnlyList<string> Entries { get; private set; } = [];
    public string? Error { get; private set; }

    public DirBrowser(IReadOnlyList<string> roots, string? start = null)
    {
        _roots = roots;
        if (start != null && Directory.Exists(start)) Open(start); else Open(null);
    }

    /// <summary>Display names of <see cref="Entries"/>.</summary>
    public IEnumerable<string> Names => Entries.Select(e => Current == null ? e : Path.GetFileName(e));

    /// <summary>True when the current folder is a game directory (or contains the <c>NFS2SE</c> folder of one).</summary>
    public bool IsValidHere => Current != null && DataDirResolver.Resolve(Current) != null;
    public string? ResolvedHere => Current == null ? null : DataDirResolver.Resolve(Current);

    public void Open(string? path)
    {
        Error = null;
        if (path == null) { Current = null; Entries = _roots; return; }
        try
        {
            var list = Directory.EnumerateDirectories(path)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).ToList();
            Current = path; Entries = list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Current = path; Entries = []; Error = e.Message;
        }
    }

    /// <summary>Enters the entry with this index of <see cref="Entries"/>.</summary>
    public void Enter(int index)
    {
        if ((uint)index < (uint)Entries.Count) Open(Entries[index]);
    }

    /// <summary>One level up; from a start point (or any folder not below a start point) back to the list of start points.
    /// Returns false when already at that list.</summary>
    public bool Up()
    {
        if (Current == null) return false;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Current));
        bool atRoot = _roots.Any(r => string.Equals(Path.TrimEndingDirectorySeparator(r), Path.TrimEndingDirectorySeparator(Current), StringComparison.Ordinal));
        if (atRoot || string.IsNullOrEmpty(parent) || !_roots.Any(r => IsUnder(Current, r))) { Open(null); return true; }
        Open(parent);
        return true;
    }

    static bool IsUnder(string path, string root) =>
        Path.TrimEndingDirectorySeparator(path).StartsWith(Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal);
}
