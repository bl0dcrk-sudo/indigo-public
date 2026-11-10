using System.Net;
using System.Text.Json;

namespace NFS.Net.Server;

/// <summary>
/// Persistence of the last approved interactive server configuration (TASKS item 15). The file lives next to the server
/// executable (<see cref="AppContext.BaseDirectory"/>) so a portable package keeps its defaults; it is read as the defaults of
/// the configuration dialog and written only for an interactive (confirmed) run. Missing, unreadable, corrupt and out-of-range
/// files are ignored by the dialog (the built-in defaults stay) and never stop the server.
/// </summary>
public static class ServerConfigStore
{
    public const string FileName = "server-config.json";

    /// <summary>Default portable location: beside NFS.Server.exe (the application base directory of the package).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Current wire version: 1 = the bots field is the operator bot setting (-1 auto, 0..7 requested). Version 0
    /// (older files) stored the split-screen menu opponents (0..2) or an ignored 0 and is migrated on load.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Wire shape of the file; deliberately separate from the runtime record so validation is explicit.</summary>
    sealed class ConfigDto
    {
        public int Version { get; set; }
        public string? Mode { get; set; }
        public string? Bind { get; set; }
        public int Port { get; set; }
        public int Track { get; set; }
        public int Laps { get; set; }
        public int MinPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int Bots { get; set; }
        public bool Traffic { get; set; }
        public bool Backward { get; set; }
    }

    /// <summary>Loads and validates a saved configuration. Missing/corrupt/invalid files return false with an explanation;
    /// they are not an error condition of the server.</summary>
    public static bool TryLoad(string path, out ServerConsoleConfig? config, out string error)
    {
        config = null;
        try
        {
            if (!File.Exists(path)) { error = $"no saved configuration at {path}"; return false; }
            string json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<ConfigDto>(json, JsonOptions);
            if (dto == null) { error = "saved configuration is empty"; return false; }
            return TryConvert(dto, out config, out error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            error = $"cannot read {path}: {e.Message}";
            return false;
        }
    }

    /// <summary>Writes the configuration with a temporary file and an atomic replace in the same directory. A failed write
    /// returns false with the reason and leaves any previous file untouched.</summary>
    public static bool TrySave(string path, ServerConsoleConfig config, out string error)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (dir != null) Directory.CreateDirectory(dir);
            var dto = new ConfigDto
            {
                Version = CurrentVersion,
                Mode = config.Mode, Bind = BindText(config.Bind), Port = config.Port, Track = config.Track, Laps = config.Laps,
                MinPlayers = config.MinPlayers, MaxPlayers = config.MaxPlayers, Bots = config.Bots, Traffic = config.Traffic,
                Backward = config.Backward,
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

    static string BindText(IPAddress bind) => bind.Equals(IPAddress.Any) ? "0.0.0.0" : bind.ToString();

    /// <summary>Strict validation of a loaded file: every field must be in the range the dialog itself accepts.</summary>
    static bool TryConvert(ConfigDto dto, out ServerConsoleConfig? config, out string error)
    {
        config = null;
        string mode = (dto.Mode ?? "").Trim().ToLowerInvariant();
        if (mode != ServerConsole.ModeSingle)
        {
            error = $"unsupported mode '{dto.Mode}' (only '{ServerConsole.ModeSingle}' is available)";
            return false;
        }
        if (!ServerConsole.TryParseBind(dto.Bind ?? "", out IPAddress bind, out error)) return false;
        if (dto.Port is < 0 or > 65535) { error = "port must be in 0..65535"; return false; }
        if (dto.Track is < 0 or > 7) { error = "track must be in 0..7"; return false; }
        if (dto.Laps is < 1 or > 8) { error = "laps must be in 1..8"; return false; }
        if (dto.MinPlayers is < ServerRaceConfig.MinPlayers or > ServerRaceConfig.MaxPlayers)
        {
            error = $"min players must be in {ServerRaceConfig.MinPlayers}..{ServerRaceConfig.MaxPlayers}";
            return false;
        }
        if (dto.MaxPlayers is < ServerRaceConfig.MinPlayers or > ServerRaceConfig.MaxPlayers)
        {
            error = $"max players must be in {ServerRaceConfig.MinPlayers}..{ServerRaceConfig.MaxPlayers}";
            return false;
        }
        if (dto.MaxPlayers < dto.MinPlayers)
        {
            error = $"max players ({dto.MaxPlayers}) must not be below min players ({dto.MinPlayers})";
            return false;
        }
        int bots;
        if (dto.Version <= 0)
        {
            // Version 0 stored the menu's split-screen opponent option: 2 meant the full six-opponent lineup, anything else
            // meant no AI at all. In other capacities the field was always 0 and ignored (the lineup was always full).
            if (dto.Bots is < 0 or > 2) { error = "bots must be in 0..2 in a version 0 configuration"; return false; }
            bots = dto.MaxPlayers == 2 ? (dto.Bots == 2 ? ServerRaceConfig.AutoBots : 0) : ServerRaceConfig.AutoBots;
        }
        else
        {
            if (dto.Bots is < ServerRaceConfig.AutoBots or > ServerRaceConfig.MaxRacers - 1)
            {
                error = $"bots must be {ServerRaceConfig.AutoBots} (auto) or 0..{ServerRaceConfig.MaxRacers - 1}";
                return false;
            }
            bots = dto.Bots;
        }
        config = new ServerConsoleConfig(mode, bind, dto.Port, dto.Track, dto.Laps, dto.MinPlayers, dto.MaxPlayers, bots, dto.Traffic, dto.Backward);
        error = "";
        return true;
    }
}
