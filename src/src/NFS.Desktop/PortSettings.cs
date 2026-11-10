using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NFS.Desktop;

/// <summary>Port-only (non-vanilla) host settings, kept apart from the original's CONFIG mirror
/// (<c>settings.json</c>, <see cref="NFS.Game.FrontEnd.UserSettings"/>): <c>%APPDATA%\NFS2SE-port\port.json</c>, plain JSON.
/// Today: the window mode (Alt+Enter), remembered between runs. Missing/broken file = defaults (full screen);
/// unknown keys survive a save; atomic write (temp file + move).</summary>
public sealed class PortSettings
{
    public const string FileName = "port.json";

    public static string DefaultPath { get; } = Path.Combine(
        Path.GetDirectoryName(NFS.Game.FrontEnd.UserSettings.DefaultPath)!, FileName);

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NewLine = "\n",
    };

    /// <summary>Last used window mode; null = never saved (start full screen).</summary>
    [JsonPropertyName("fullScreen")] public bool? FullScreen { get; set; }

    /// <summary>Game data directory chosen by the user (Android first run: the folder picked on the "Using data dir" screen;
    /// holds GAMEDATA and FEDATA). Null = never chosen.</summary>
    [JsonPropertyName("gameDir")] public string? GameDir { get; set; }
    /// <summary>Frame cap in a race / track view (frames per second, 10..240); null = default 60, 0 = no cap (vsync only).</summary>
    [JsonPropertyName("maxFps")] public int? MaxFps { get; set; }
    /// <summary>Frame cap in the menus and the network lobby; null = default 30, 0 = no cap.</summary>
    [JsonPropertyName("menuFps")] public int? MenuFps { get; set; }
    /// <summary>Graphics mode, <c>"enhanced"</c> or <c>"vanilla"</c>; null/unknown = never saved = Enhanced. [ref:3403]</summary>
    [JsonPropertyName("graphicsMode")] public string? GraphicsModeText { get; set; }
    /// <summary>Internal 3D scale of Enhanced: 1..4, 0 or null = automatic from the window size.</summary>
    [JsonPropertyName("internalScale")] public int? InternalScaleSetting { get; set; }

    /// <summary>Enhanced image quality items (null = default on): perspective-correct textures, filter <c>nearest|bilinear|mip</c>, per-pixel fog,
    /// SSAA factor (0/null = auto, 1 = off, 2..4).</summary>
    [JsonPropertyName("gfxPerspective")] public bool? GfxPerspective { get; set; }
    [JsonPropertyName("gfxFilter")] public string? GfxFilter { get; set; }
    [JsonPropertyName("gfxPixelFog")] public bool? GfxPixelFog { get; set; }
    [JsonPropertyName("gfxSsaa")] public int? GfxSsaa { get; set; }
    /// <summary>Stage 3: Gouraud car light, car shadow (null = default on), view range <c>normal|extended</c> (default normal), display filter <c>sharp|fsr|crt</c> (default sharp).</summary>
    [JsonPropertyName("gfxLight")] public bool? GfxLight { get; set; }
    [JsonPropertyName("gfxShadow")] public bool? GfxShadow { get; set; }
    /// <summary>Track polygon shading (null = default on).</summary>
    [JsonPropertyName("gfxTrackShade")] public bool? GfxTrackShade { get; set; }
    [JsonPropertyName("gfxRange")] public string? GfxRange { get; set; }
    [JsonPropertyName("gfxDisplay")] public string? GfxDisplay { get; set; }
    [JsonPropertyName("gfxWide")] public bool? GfxWide { get; set; }
    /// <summary>2D layer magnified by MMPX in Enhanced (null = default on).</summary>
    [JsonPropertyName("gfxSmooth2d")] public bool? GfxSmooth2D { get; set; }

    /// <summary>Menu language, the port's stand-in for the original's install.win language line [ref:1328]
    /// read <c>english|german|french|spanish|italian|swedish</c> there; the original menu has no language item. [ref:1329]
    /// Written as that name; a name, a TEXT code (<c>GER</c>) or a digit 0..5 is accepted when read. Null/unknown = English.</summary>
    [JsonPropertyName("language")] public string? LanguageText { get; set; }
    [JsonIgnore] public int LanguageIndex => NFS.Data.FrontEnd.TextFile.ParseLanguage(LanguageText) ?? 0;
    /// <summary>Stores <paramref name="index"/> (0..5, else English) under <c>language</c>.</summary>
    public void SetLanguage(int index) => LanguageText = NFS.Data.FrontEnd.TextFile.LanguageNames[index is >= 0 and < 6 ? index : 0];

    /// <summary>Pad vibration in a race (<see cref="PadRumble"/>); null = default on.</summary>
    [JsonPropertyName("rumble")] public bool? Rumble { get; set; }
    [JsonIgnore] public bool RumbleOn => Rumble ?? true;

    /// <summary>Remembered graphics mode; a missing or unknown value = <see cref="NFS.Render.GraphicsModes.Default"/> (Enhanced).</summary>
    [JsonIgnore] public NFS.Render.GraphicsMode Graphics => NFS.Render.GraphicsModes.Parse(GraphicsModeText) ?? NFS.Render.GraphicsModes.Default;

    /// <summary>Network nick typed on the Connect screen; null = never saved (the menu falls back to the profile name).</summary>
    [JsonPropertyName("netNick")] public string? NetNick { get; set; }
    /// <summary>Server host and UDP port last used for a connection; null = never saved.</summary>
    [JsonPropertyName("netHost")] public string? NetHost { get; set; }
    [JsonPropertyName("netPort")] public int? NetPort { get; set; }

    /// <summary>Remembers the Connect-screen entries. Blank nick/host and out-of-range ports are not stored (the previous value stays).</summary>
    public void RememberNetwork(string nick, string host, int port)
    {
        if (nick.Trim().Length > 0) NetNick = nick.Trim();
        if (host.Trim().Length > 0) NetHost = host.Trim();
        if (port is >= 1 and <= 65535) NetPort = port;
    }

    /// <summary>The nick and the server were saved by an earlier connect (not the very first run): the Connect screen then starts on CONNECT.</summary>
    [JsonIgnore] public bool HasSavedNetwork => !string.IsNullOrWhiteSpace(NetNick) && !string.IsNullOrWhiteSpace(NetHost);

    /// <summary>Initial Connect-screen entries: the remembered nick/host/port, else <paramref name="fallbackNick"/> and the defaults.</summary>
    public (string Nick, string Host, string Port) NetworkDefaults(string fallbackNick, string defaultHost, int defaultPort) =>
        (string.IsNullOrWhiteSpace(NetNick) ? fallbackNick : NetNick,
         string.IsNullOrWhiteSpace(NetHost) ? defaultHost : NetHost,
         (NetPort is >= 1 and <= 65535 ? NetPort.Value : defaultPort).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Look of the network menu screens, <c>"original"</c> or <c>"classic"</c> (experimental, no menu item: edit this file);
    /// null/unknown = never saved = Original.</summary>
    [JsonPropertyName("netMenuLook")] public string? NetMenuLookText { get; set; }
    [JsonIgnore] public NetMenuLook NetMenuLook
    {
        get => NetMenuLooks.Parse(NetMenuLookText) ?? NetMenuLook.Original;
        set => NetMenuLookText = NetMenuLooks.Name(value);
    }

    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = new();

    [JsonIgnore] public string? Error { get; private set; }

    public static PortSettings Load(string path)
    {
        if (!File.Exists(path)) return new PortSettings();
        try { return JsonSerializer.Deserialize<PortSettings>(File.ReadAllText(path), Options) ?? new PortSettings(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { return new PortSettings { Error = $"port settings ignored: {e.Message}" }; }
    }

    /// <summary>Writes the file; returns null or the reason it was not saved (for the host log).</summary>
    public string? Save(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full + ".tmp", JsonSerializer.Serialize(this, Options) + "\n", new UTF8Encoding(false));
            File.Move(full + ".tmp", full, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return $"port settings not saved: {e.Message}"; }
    }
}
