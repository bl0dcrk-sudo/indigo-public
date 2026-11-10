using System.Net;
using System.Text.Json;

namespace NFS.Net.Server;

/// <summary>
/// Administrative configuration of the headless session server (file <c>server-config.json</c>): where it listens and how
/// many games/connections and how long it keeps them. Game flow (track, laps, cars, readiness, starting) belongs to the
/// clients and their games and is deliberately absent. A missing or invalid file never stops the server: the built-in
/// defaults stay in effect and the reason is logged.
/// </summary>
public sealed record SessionServerConfig
{
    public const string FileName = "server-config.json";
    /// <summary>No-argument start listens on every interface, the historical dedicated-server port.</summary>
    public const int DefaultPort = 27100;
    public const int MinPort = 0, MaxPort = 65535;
    public const int MinTimeoutSeconds = 1, MaxTimeoutSeconds = 86_400;
    public const int MinConnections = 1, MaxConnectionsLimit = 4096;

    public IPAddress Bind { get; init; } = IPAddress.Any;
    public int Port { get; init; } = DefaultPort;
    /// <summary>Games that may exist at the same time (also bounds the games list).</summary>
    public int MaxGames { get; init; } = 16;
    public int MaxConnections { get; init; } = 256;
    /// <summary>An idle lobby (no join/ready/option change) is closed after this many seconds; a running race is never cut.</summary>
    public int LobbyIdleTimeoutSeconds { get; init; } = 900;
    /// <summary>Reconnect reservation window (reserved for the reconnect stage; kept in the administrative file now).</summary>
    public int GraceSeconds { get; init; } = 30;
    /// <summary>How long a composed round may wait for its humans to report ready.</summary>
    public int RaceWaitSeconds { get; init; } = 120;
    /// <summary>Time the stragglers of a round get after the first human finisher (0 = unlimited); then the round ends and the rest is DNF.</summary>
    public int StragglerSeconds { get; init; } = 120;
    /// <summary>Diagnostic bound for smoke runs (0 = no step limit).</summary>
    public int MaxRaceSteps { get; init; }

    public SessionServerOptions ToServerOptions() => new()
    {
        MaxGames = MaxGames,
        MaxConnections = MaxConnections,
        LobbyIdleTimeoutMicros = LobbyIdleTimeoutSeconds * 1_000_000L,
        GraceMicros = GraceSeconds * 1_000_000L,
        RaceWaitTimeoutMicros = RaceWaitSeconds * 1_000_000L,
        StragglerLimitMicros = StragglerSeconds * 1_000_000L,
        MaxRaceSteps = MaxRaceSteps,
    };

    /// <summary>Checks every administrative field; returns a readable description on failure.</summary>
    public bool Validate(out string error)
    {
        if (Bind.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6))
        {
            error = "bind must be an IPv4 or IPv6 address";
            return false;
        }
        if (Port is < MinPort or > MaxPort) { error = $"port must be in {MinPort}..{MaxPort}"; return false; }
        if (MaxGames is < 1 or > Protocol.NetProtocol.MaxGameListEntries)
        {
            error = $"max games must be in 1..{Protocol.NetProtocol.MaxGameListEntries}";
            return false;
        }
        if (MaxConnections is < MinConnections or > MaxConnectionsLimit) { error = $"max connections must be in {MinConnections}..{MaxConnectionsLimit}"; return false; }
        if (LobbyIdleTimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            error = $"lobby timeout must be in {MinTimeoutSeconds}..{MaxTimeoutSeconds} seconds";
            return false;
        }
        if (GraceSeconds is < 0 or > MaxTimeoutSeconds) { error = $"grace must be in 0..{MaxTimeoutSeconds} seconds"; return false; }
        if (RaceWaitSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            error = $"race wait must be in {MinTimeoutSeconds}..{MaxTimeoutSeconds} seconds";
            return false;
        }
        if (StragglerSeconds is < 0 or > MaxTimeoutSeconds) { error = $"straggler limit must be in 0..{MaxTimeoutSeconds} seconds"; return false; }
        if (MaxRaceSteps < 0) { error = "max race steps must not be negative"; return false; }
        error = "";
        return true;
    }

    public override string ToString() => $"{SessionServerConfigStore.BindText(Bind)}:{Port}";
}

/// <summary>Reads and writes the administrative <c>server-config.json</c>. The same file may still carry the fields of the old
/// per-race console configuration (mode, track, laps, players…); they are ignored here. Fields absent from an older file fall
/// back to the built-in defaults, which keeps an existing file usable after the server became a game host.</summary>
public static class SessionServerConfigStore
{
    public const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, SessionServerConfig.FileName);

    public static string BindText(IPAddress bind) => bind.Equals(IPAddress.Any) ? "0.0.0.0" : bind.ToString();

    sealed class Dto
    {
        public int Version { get; set; }
        public string? Bind { get; set; }
        public int? Port { get; set; }
        public int? MaxGames { get; set; }
        public int? MaxConnections { get; set; }
        public int? LobbyIdleTimeoutSeconds { get; set; }
        public int? GraceSeconds { get; set; }
        public int? RaceWaitSeconds { get; set; }
        public int? MaxRaceSteps { get; set; }
        public int? StragglerSeconds { get; set; }
        // Fields of the former per-race configuration; accepted and ignored.
        public string? Mode { get; set; }
        public int? Track { get; set; }
        public int? Laps { get; set; }
        public int? MinPlayers { get; set; }
        public int? MaxPlayers { get; set; }
        public int? Bots { get; set; }
        public bool? Traffic { get; set; }
        public bool? Backward { get; set; }
    }

    public static bool TryLoad(string path, out SessionServerConfig? config, out string error)
    {
        config = null;
        try
        {
            if (!File.Exists(path)) { error = $"no configuration at {path}"; return false; }
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), JsonOptions);
            if (dto == null) { error = "configuration is empty"; return false; }
            var defaults = new SessionServerConfig();
            IPAddress bind = defaults.Bind;
            if (dto.Bind is { } bindText && !IPAddress.TryParse(bindText, out bind!))
            {
                error = $"invalid bind address '{bindText}'";
                return false;
            }
            var candidate = defaults with
            {
                Bind = bind,
                Port = dto.Port ?? defaults.Port,
                MaxGames = dto.MaxGames ?? defaults.MaxGames,
                MaxConnections = dto.MaxConnections ?? defaults.MaxConnections,
                LobbyIdleTimeoutSeconds = dto.LobbyIdleTimeoutSeconds ?? defaults.LobbyIdleTimeoutSeconds,
                GraceSeconds = dto.GraceSeconds ?? defaults.GraceSeconds,
                RaceWaitSeconds = dto.RaceWaitSeconds ?? defaults.RaceWaitSeconds,
                MaxRaceSteps = dto.MaxRaceSteps ?? defaults.MaxRaceSteps,
                StragglerSeconds = dto.StragglerSeconds ?? defaults.StragglerSeconds,
            };
            if (!candidate.Validate(out error)) return false;
            config = candidate;
            error = "";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            error = $"cannot read {path}: {e.Message}";
            return false;
        }
    }

    /// <summary>Writes the administrative fields with a temporary file and an atomic replace. Never used by the no-argument
    /// start; it exists for administrators and tests.</summary>
    public static bool TrySave(string path, SessionServerConfig config, out string error)
    {
        try
        {
            if (!config.Validate(out error)) return false;
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (dir != null) Directory.CreateDirectory(dir);
            var dto = new Dto
            {
                Version = CurrentVersion, Bind = BindText(config.Bind), Port = config.Port, MaxGames = config.MaxGames,
                MaxConnections = config.MaxConnections, LobbyIdleTimeoutSeconds = config.LobbyIdleTimeoutSeconds,
                GraceSeconds = config.GraceSeconds, RaceWaitSeconds = config.RaceWaitSeconds, MaxRaceSteps = config.MaxRaceSteps,
                StragglerSeconds = config.StragglerSeconds,
            };
            string json = JsonSerializer.Serialize(dto, JsonOptions) + Environment.NewLine;
            string temp = $"{full}.{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            error = "";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = $"cannot write {path}: {e.Message}";
            return false;
        }
    }
}
