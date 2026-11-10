using NFS.Game.Cars;
using NFS.ModApi;

namespace NFS.Game.Mods;

/// <summary>One file of a <see cref="ModPack"/>: relative name (<c>/</c> separated) and size.</summary>
public sealed record ModPackFile(string Path, long Size);

/// <summary>The files of an installed mod as a server offers them for download (openspec modding-api-cars 5.4): every file of the mod
/// directory in the order of <see cref="ModFingerprint"/> (ordinal by relative path). A mod that breaks the transfer limits
/// (<see cref="ModPaths.MaxFilesPerMod"/>, <see cref="ModPaths.MaxModBytes"/>, <see cref="ModPaths.MaxFileBytes"/>, unsafe names, links)
/// has no pack: <see cref="TryCreate"/> says why.</summary>
public sealed class ModPack
{
    public LoadedMod Mod { get; }
    public IReadOnlyList<ModPackFile> Files { get; }
    public long TotalBytes { get; }

    ModPack(LoadedMod mod, List<ModPackFile> files, long total) { Mod = mod; Files = files; TotalBytes = total; }

    public static bool TryCreate(LoadedMod mod, out ModPack? pack, out string? error)
    {
        pack = null;
        error = null;
        try
        {
            string root = Path.GetFullPath(mod.Dir);
            var files = new List<ModPackFile>();
            long total = 0;
            var rels = new List<string>();
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) { error = $"'{Path.GetRelativePath(root, f)}' is a link"; return false; }
                rels.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
            }
            foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { error = $"'{Path.GetRelativePath(root, d)}' is a link"; return false; }
            rels.Sort(StringComparer.Ordinal);
            if (rels.Count > ModPaths.MaxFilesPerMod) { error = $"{rels.Count} files, over the limit of {ModPaths.MaxFilesPerMod}"; return false; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in rels)
            {
                if (!ModPaths.IsSafeRelativeName(rel, out error)) return false;
                if (!seen.Add(rel)) { error = $"'{rel}' repeats under another letter case"; return false; }
                long size = new FileInfo(Path.Combine(root, rel)).Length;
                if (size > ModPaths.MaxFileBytes) { error = $"file '{rel}' is {size} B, over the {ModPaths.MaxFileBytes} B limit"; return false; }
                total += size;
                files.Add(new ModPackFile(rel, size));
            }
            if (total > ModPaths.MaxModBytes) { error = $"{total} B, over the limit of {ModPaths.MaxModBytes} B"; return false; }
            if (files.Count == 0) { error = "no files"; return false; }
            pack = new ModPack(mod, files, total);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }
}

/// <summary>Receives one mod version from outside (a server) into a temporary directory <c>mods/.incoming/&lt;random&gt;</c> and installs
/// it with one atomic directory move to <c>mods/&lt;id&gt;/&lt;version&gt;/</c> (openspec modding-api-cars 5.4). The data is untrusted: names
/// are checked with <see cref="ModPaths.IsSafeRelativeName"/>, sizes against the declared ones and the limits of <see cref="ModPaths"/>, and
/// <see cref="Commit"/> accepts only a mod whose <see cref="ModFingerprint"/> equals the expected hash and that passes the validation of a
/// local mod (manifest, id/version, cars: glb, textures, triangle budgets). An installed version is never overwritten.</summary>
public sealed class ModStaging : IDisposable
{
    readonly string _modsDir, _id, _version, _dir;
    readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    FileStream? _file;
    string? _currentName;
    long _currentSize, _currentWritten, _total;
    int _count;
    bool _done;

    public string Id => _id;
    public string Version => _version;
    public long TotalBytes => _total;

    public ModStaging(string modsDir, string id, string version)
    {
        if (!ModPaths.IsSafeRelativeName(id, out var e1) || !ModPaths.IsSafeRelativeName(version, out e1) || id.Contains('/') || version.Contains('/'))
            throw new ArgumentException($"invalid mod id or version: {e1}");
        _modsDir = Path.GetFullPath(modsDir);
        _id = id;
        _version = version;
        _dir = Path.Combine(_modsDir, ".incoming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Starts the next file; throws <see cref="InvalidDataException"/> for a bad name, a repeated name or a limit.</summary>
    public void BeginFile(string path, long size)
    {
        if (_file != null) throw new InvalidDataException("a file is still open");
        if (!ModPaths.IsSafeRelativeName(path, out var error)) throw new InvalidDataException(error);
        if (size < 0 || size > ModPaths.MaxFileBytes) throw new InvalidDataException($"file '{path}' size {size} B is over the {ModPaths.MaxFileBytes} B limit");
        if (_count + 1 > ModPaths.MaxFilesPerMod) throw new InvalidDataException($"more than {ModPaths.MaxFilesPerMod} files");
        if (_total + size > ModPaths.MaxModBytes) throw new InvalidDataException($"more than {ModPaths.MaxModBytes} B");
        if (!_names.Add(path)) throw new InvalidDataException($"file '{path}' repeated");
        string full = Path.GetFullPath(Path.Combine(_dir, path));
        if (!full.StartsWith(_dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"path '{path}' leaves the mod directory");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _currentName = path;
        _currentSize = size;
        _currentWritten = 0;
        _count++;
        _total += size;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_file == null) throw new InvalidDataException("no file is open");
        if (_currentWritten + data.Length > _currentSize) throw new InvalidDataException($"file '{_currentName}' is longer than declared");
        _file.Write(data);
        _currentWritten += data.Length;
    }

    public bool FileInProgress => _file != null;
    public bool FileComplete => _file != null && _currentWritten == _currentSize;

    public void EndFile()
    {
        if (_file == null) throw new InvalidDataException("no file is open");
        if (_currentWritten != _currentSize) throw new InvalidDataException($"file '{_currentName}' is shorter than declared");
        _file.Dispose();
        _file = null;
    }

    /// <summary>Validates and installs the received mod. Returns null on success, else the reason (nothing is installed; the temporary
    /// directory is removed either way).</summary>
    public string? Commit(string expectedHashHex, int expectedFiles, long expectedBytes)
    {
        try
        {
            if (_file != null) return "a file is incomplete";
            if (_count != expectedFiles || _total != expectedBytes) return $"received {_count} files / {_total} B, expected {expectedFiles} / {expectedBytes} B";
            if (!string.Equals(ModFingerprint.OfDirectory(_dir), expectedHashHex, StringComparison.OrdinalIgnoreCase)) return "content hash differs from the announced one";
            var diag = new ModDiagnostics();
            var mod = ModLoader.Validate(_dir, _id, _version, diag);
            if (mod != null)
            {
                foreach (var _ in new ModCarSource(new[] { mod }, diag).GetCars()) { }   // car.json, models, textures, budgets
            }
            if (mod == null || diag.HasErrors)
                return "invalid mod: " + string.Join("; ", diag.Entries.Where(d => d.Severity == ModDiagnosticSeverity.Error).Select(d => d.Message).DefaultIfEmpty("no manifest"));
            string idDir = Path.Combine(_modsDir, _id), target = Path.Combine(idDir, _version);
            if (Directory.Exists(target) || File.Exists(target)) return $"'{_id}/{_version}' is already installed (an installed version is never overwritten)";
            Directory.CreateDirectory(idDir);
            Directory.Move(_dir, target);
            _done = true;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"cannot install: {e.Message}";
        }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        if (!_done)
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try
        {
            string incoming = Path.Combine(_modsDir, ".incoming");
            if (Directory.Exists(incoming) && !Directory.EnumerateFileSystemEntries(incoming).Any()) Directory.Delete(incoming);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
