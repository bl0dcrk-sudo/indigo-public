using System.Net;

namespace NFS.Net.Server;

/// <summary>
/// Parsed command line of the headless session server. Every administrative field is optional and overrides the value read
/// from <c>server-config.json</c>; game-flow flags of the former per-race server are accepted and ignored so existing launch
/// scripts keep working (the games themselves carry track, laps, cars and readiness).
/// </summary>
public sealed record SessionServerLaunch
{
    public bool Help { get; init; }
    public string? GameDir { get; init; }
    /// <summary>Directory of the data mods (<c>--mods DIR</c>); null = <c>mods/</c> next to the server exe (none = no mods).</summary>
    public string? ModsDir { get; init; }
    public string? ConfigPath { get; init; }
    public string? LogPath { get; init; }
    public string? PortFile { get; init; }
    /// <summary>Bound for unattended smoke runs; null = run until quit/Ctrl+C.</summary>
    public int? MaxRunSeconds { get; init; }
    public IPAddress? Bind { get; init; }
    public int? Port { get; init; }
    public int? MaxGames { get; init; }
    public int? MaxConnections { get; init; }
    public int? LobbyIdleTimeoutSeconds { get; init; }
    public int? GraceSeconds { get; init; }
    public int? RaceWaitSeconds { get; init; }
    public int? MaxRaceSteps { get; init; }
    /// <summary>Legacy per-race flags that were accepted and ignored (reported at startup).</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    /// <summary>Applies the command-line overrides on top of the file/default configuration.</summary>
    public SessionServerConfig Apply(SessionServerConfig current) => current with
    {
        Bind = Bind ?? current.Bind,
        Port = Port ?? current.Port,
        MaxGames = MaxGames ?? current.MaxGames,
        MaxConnections = MaxConnections ?? current.MaxConnections,
        LobbyIdleTimeoutSeconds = LobbyIdleTimeoutSeconds ?? current.LobbyIdleTimeoutSeconds,
        GraceSeconds = GraceSeconds ?? current.GraceSeconds,
        RaceWaitSeconds = RaceWaitSeconds ?? current.RaceWaitSeconds,
        MaxRaceSteps = MaxRaceSteps ?? current.MaxRaceSteps,
    };
}

/// <summary>Command line of <c>NFS.Server</c>. No arguments is a valid launch: the server listens immediately on
/// <c>0.0.0.0:27100</c> with the administrative file (when present) applied.</summary>
public static class SessionServerCommandLine
{
    public static readonly string Usage =
        "NFS.Server (no arguments: listen immediately on 0.0.0.0:27100)\n" +
        "NFS.Server --bind <IP> --port <N>   choose the address and port to listen on (default 0.0.0.0 27100)\n" +
        "NFS.Server [--game DIR] [--mods DIR] [--bind ADDR] [--port N] [--max-games N] [--max-connections N]\n" +
        "           [--lobby-timeout-seconds N] [--grace-seconds N] [--race-wait-seconds N] [--max-race-steps N]\n" +
        "           [--config FILE] [--log FILE] [--port-file FILE] [--max-run-seconds N] [--help]\n" +
        $"administrative defaults: {SessionServerConfigStore.DefaultPath}\n" +
        "legacy per-race flags (accepted and ignored): --track --laps --players --opponents --traffic --wait-seconds --interactive";

    /// <summary>Lines printed at startup before listening: where the server will listen, where that came from and how to change
    /// it (<c>--bind</c>/<c>--port</c> or the administrative file), so the operator sees the options without asking for help.</summary>
    public static IReadOnlyList<string> StartupHelp(SessionServerConfig config, string configPath, bool configFileUsed) =>
    [
        $"listening address: {SessionServerConfigStore.BindText(config.Bind)} port {config.Port} " +
            (configFileUsed ? $"(settings file {configPath} plus command line)" : "(defaults plus command line; no settings file)"),
        "to choose the address and port start with:  NFS.Server --bind <IP> --port <N>   (e.g. --bind 192.168.1.10 --port 27100)",
        $"the same settings can be kept in {configPath} (bind, port, maxGames, ...); command line flags win; --help lists all options",
    ];

    /// <summary>Flags that may appear without a value; every other flag needs the next argument.</summary>
    static readonly HashSet<string> FlagsWithoutValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "traffic", "interactive",
    };

    /// <summary>Legacy flags accepted (with their value, where the old parser took one) and ignored.</summary>
    static readonly HashSet<string> LegacyWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "track", "laps", "players", "opponents", "wait-seconds",
    };

    public static bool TryParse(string[] args, out SessionServerLaunch launch, out string error)
    {
        launch = new SessionServerLaunch();
        error = "";
        var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                error = $"unexpected argument '{arg}'";
                return false;
            }
            string key = arg[2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (FlagsWithoutValue.Contains(key))
            {
                opts[key] = null;
                continue;
            }
            if (!hasValue)
            {
                if (LegacyWithValue.Contains(key)) { opts[key] = null; continue; }
                error = $"flag --{key} needs a value";
                return false;
            }
            opts[key] = args[++i];
        }

        var ignored = new List<string>();
        foreach (var key in opts.Keys)
        {
            bool known = key is "help" or "game" or "mods" or "bind" or "port" or "max-games" or "max-connections" or
                "lobby-timeout-seconds" or "grace-seconds" or "race-wait-seconds" or "max-race-steps" or "config" or
                "log" or "port-file" or "max-run-seconds";
            bool legacy = key is "track" or "laps" or "players" or "opponents" or "traffic" or "wait-seconds" or "interactive";
            if (!known && !legacy)
            {
                error = $"unknown flag --{key}";
                return false;
            }
            if (legacy) ignored.Add("--" + key);
        }

        var result = new SessionServerLaunch { Help = opts.ContainsKey("help"), Ignored = ignored };
        string? Value(string key) => opts.TryGetValue(key, out var v) ? v : null;
        if (!TryInt(opts, "port", 0, SessionServerConfig.MaxPort, out int? port, out error)) return false;
        if (!TryInt(opts, "max-games", 1, Protocol.NetProtocol.MaxGameListEntries, out int? maxGames, out error)) return false;
        if (!TryInt(opts, "max-connections", SessionServerConfig.MinConnections, SessionServerConfig.MaxConnectionsLimit,
                out int? maxConnections, out error)) return false;
        if (!TryInt(opts, "lobby-timeout-seconds", SessionServerConfig.MinTimeoutSeconds, SessionServerConfig.MaxTimeoutSeconds,
                out int? lobbyTimeout, out error)) return false;
        if (!TryInt(opts, "grace-seconds", 0, SessionServerConfig.MaxTimeoutSeconds, out int? grace, out error)) return false;
        if (!TryInt(opts, "race-wait-seconds", SessionServerConfig.MinTimeoutSeconds, SessionServerConfig.MaxTimeoutSeconds,
                out int? raceWait, out error)) return false;
        if (!TryInt(opts, "max-race-steps", 0, int.MaxValue, out int? maxSteps, out error)) return false;
        if (!TryInt(opts, "max-run-seconds", 1, 86_400, out int? maxRun, out error)) return false;
        // Legacy numeric flags must still parse even though they are ignored, so a broken old script is reported.
        if (!TryInt(opts, "track", 0, 7, out _, out error)) return false;
        if (!TryInt(opts, "laps", 1, 8, out _, out error)) return false;
        if (!TryInt(opts, "players", 1, 8, out _, out error)) return false;
        if (!TryInt(opts, "opponents", 0, 2, out _, out error)) return false;
        if (!TryInt(opts, "wait-seconds", 1, 86_400, out _, out error)) return false;

        IPAddress? bind = null;
        if (Value("bind") is { } bindText)
        {
            if (!IPAddress.TryParse(bindText, out bind))
            {
                error = $"invalid bind address '{bindText}'";
                return false;
            }
        }
        launch = result with
        {
            GameDir = Value("game"), ModsDir = Value("mods"), ConfigPath = Value("config"), LogPath = Value("log"), PortFile = Value("port-file"),
            MaxRunSeconds = maxRun, Bind = bind, Port = port, MaxGames = maxGames, MaxConnections = maxConnections,
            LobbyIdleTimeoutSeconds = lobbyTimeout, GraceSeconds = grace, RaceWaitSeconds = raceWait, MaxRaceSteps = maxSteps,
        };
        return true;
    }

    static bool TryInt(Dictionary<string, string?> opts, string key, int min, int max, out int? value, out string error)
    {
        value = null;
        error = "";
        if (!opts.TryGetValue(key, out var text)) return true;
        if (text == null)
        {
            error = $"flag --{key} needs a value";
            return false;
        }
        if (!int.TryParse(text, out int parsed) || parsed < min || parsed > max)
        {
            error = $"--{key} must be an integer in {min}..{max}";
            return false;
        }
        value = parsed;
        return true;
    }
}
