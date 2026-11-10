using NFS.ModApi;

namespace NFS.Game.Mods;

/// <summary>Mods found at startup; frozen for the rest of the run (D5). <see cref="Start"/> never throws.</summary>
public sealed class ModSession
{
    static bool _started;

    public static ModSession Current { get; private set; } = new("", [], new ModDiagnostics());

    public string ModsDir { get; }
    public IReadOnlyList<LoadedMod> Mods { get; }
    public ModDiagnostics Diagnostics { get; }

    ModSession(string modsDir, IReadOnlyList<LoadedMod> mods, ModDiagnostics diag) { ModsDir = modsDir; Mods = mods; Diagnostics = diag; }

    /// <summary>Resolves the mods dir, loads mods, logs every mod and diagnostic through <paramref name="log"/> and stores the result in
    /// <see cref="Current"/>. Only the first call loads; later calls return the already frozen session.</summary>
    public static ModSession Start(string? overrideDir, string exeDir, Action<string> log)
    {
        if (_started) return Current;
        _started = true;
        var s = Create(overrideDir, exeDir);
        try
        {
            log($"mods: dir '{s.ModsDir}', {s.Mods.Count} loaded");
            foreach (var m in s.Mods) log($"mod loaded: {m.Manifest.Id} {m.Manifest.Version} ({m.Dir})");
            foreach (var d in s.Diagnostics.Entries) log($"mod {d.Severity.ToString().ToLowerInvariant()}: [{d.ModId}] {d.Message}");
        }
        catch (Exception) { /* logging must not stop the game */ }
        Current = s;
        return s;
    }

    static ModSession? _startup;

    /// <summary>A session of exactly the given versions of <paramref name="modsDir"/> (not the newest ones), for a network game that plays
    /// the server's set (5.4). A version that is missing or invalid is a diagnostic error and is left out. Never throws.</summary>
    public static ModSession CreateExact(string modsDir, IEnumerable<(string Id, string Version)> wanted)
    {
        var diag = new ModDiagnostics();
        var mods = new List<LoadedMod>();
        foreach (var (id, version) in wanted)
            if (ModLoader.LoadExact(modsDir, id, version, diag) is { } m) mods.Add(m);
        return new ModSession(modsDir, mods, diag);
    }

    /// <summary>Makes <paramref name="session"/> the current one for the duration of a network game (the mods stay frozen while a game
    /// runs); <see cref="RestoreStartup"/> brings the set of the start back.</summary>
    public static void UseForNetwork(ModSession session)
    {
        _startup ??= Current;
        Current = session;
    }

    public static void RestoreStartup()
    {
        if (_startup == null) return;
        Current = _startup;
        _startup = null;
    }

    /// <summary>Loads without storing or freezing (tests, tools). Never throws.</summary>
    public static ModSession Create(string? overrideDir, string exeDir)
    {
        var diag = new ModDiagnostics();
        string dir = "";
        IReadOnlyList<LoadedMod> mods = [];
        try
        {
            dir = ModPaths.ResolveModsDir(overrideDir, exeDir);
            mods = ModLoader.Load(dir, diag);
        }
        catch (Exception e) { diag.Error("", $"mod loading failed: {e.Message}"); }
        return new ModSession(dir, mods, diag);
    }
}
