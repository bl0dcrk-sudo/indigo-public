using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NFS.Data;
using NFS.Data.FrontEnd;

namespace NFS.Game.FrontEnd;

/// <summary>User settings (tasks 1c/1f): a readable `settings.json` in the user directory instead of writing
/// CONFIG.DAT into the game directory (the installation may be read-only and shared). The fields map to the CONFIG.DAT offsets
///  (see <see cref="FeConfig"/>), so the values are 1:1 with what the game uses. [ref:2020]
/// <para><b>Decision "saving next to CONFIG.DAT": we do not save.</b> The original writes CONFIG.DAT into the game directory,
/// the port deliberately does not touch game files – the installation may be read-only (disc, Program Files, a shared directory) [ref:2021]
/// and must stay untouched (game data is easy to tell apart from player data, with no mixing of two sources of truth).
/// The player's equivalent of CONFIG.DAT is this file; on the first start without it,
/// `GameLoop` writes the current menu configuration into it, so from then on changes go only here. The same reasons apply to
/// <see cref="UserStats"/> (`stats`), tournament saves (`saves`) and `port.json` – all in `%APPDATA%`.</para>
/// <para>No key = the field is not overwritten: <see cref="ApplyTo"/> changes only the fields that are set, so the file may be partial
/// and hand-edited, and a missing file keeps the configuration from the original game/`CONFIG.DAT`. Unknown keys are kept
/// on save (forward compatibility), and the save is atomic (temporary file + replace).</para></summary>
public sealed class UserSettings
{
    /// <summary>File schema version; a newer version is ignored (we do not guess future fields).</summary>
    public const int Schema = 1;
    public const string Folder = "NFS2SE-port";
    public const string FileName = "settings.json";

    /// <summary>%APPDATA%\NFS2SE-port\settings.json (on Windows `ApplicationData`; the port is a desktop app).</summary>
    public static string DefaultPath { get; } =
        DefaultPathFor(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static string DefaultPathFor(string appData) =>
        Path.Combine(string.IsNullOrWhiteSpace(appData) ? "." : appData, Folder, FileName);

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NewLine = "\n",   // platform-independent file (Environment.NewLine by default, CRLF on Windows)
    };

    [JsonPropertyName("schema")] public int SchemaVersion { get; set; } = Schema;
    [JsonPropertyName("p1Car")] public int? P1Car { get; set; }                       // 0x1F (0 when a mod car is selected: see P1CarKey)
    // Mod cars (openspec modding-api-cars 4.4): selected by car key, because the in-memory number (0x35..) depends on the order of the mods.
    // The byte fields above then hold a valid original value, so a game without the mod (or an older one) starts with a normal car.
    [JsonPropertyName("p1CarKey")] public string? P1CarKey { get; set; }
    [JsonPropertyName("p2CarKey")] public string? P2CarKey { get; set; }
    [JsonPropertyName("opponentCarKey")] public string? OpponentCarKey { get; set; }
    /// <summary>Colour index (car.json "colors") per mod car key; the original config bytes have no room for it.</summary>
    [JsonPropertyName("p1ModColours")] public Dictionary<string, int>? P1ModColours { get; set; }
    [JsonPropertyName("p2ModColours")] public Dictionary<string, int>? P2ModColours { get; set; }
    [JsonPropertyName("p1Transmission")] public int? P1Transmission { get; set; }     // 0x20 (0 manual, 1 automatic; RaceRunner: Transmission == 1) [ref:2022]
    [JsonPropertyName("p1Colours")] public int[]? P1Colours { get; set; }             // 0x21, 16 B (colour per car)
    [JsonPropertyName("style")] public int? Style { get; set; }                       // 0x0B (0 sim, 1 wild, 2 arcade)
    [JsonPropertyName("skill")] public int? Skill { get; set; }                       // 0x0D
    [JsonPropertyName("catchUp")] public int? CatchUp { get; set; }                   // 0x0E
    [JsonPropertyName("track")] public int? Track { get; set; }                       // 0x10
    [JsonPropertyName("laps")] public int? Laps { get; set; }                         // 0x18
    [JsonPropertyName("lapsMenuIndex")] public int? LapsMenuIndex { get; set; }       // 0x19 (index of the laps list)
    [JsonPropertyName("backward")] public int? Backward { get; set; }                 // 0x1A
    [JsonPropertyName("mirrored")] public int? Mirrored { get; set; }                 // 0x1B
    [JsonPropertyName("opponentCar")] public int? OpponentCar { get; set; }           // 0x55 (0..11 car, 12..14 class)
    [JsonPropertyName("opponentCount")] public int? OpponentCount { get; set; }       // 0x56 (0 none, 1 one, 2 full field)
    [JsonPropertyName("trafficOff")] public int? TrafficOff { get; set; }             // 0x58
    [JsonPropertyName("units")] public int? Units { get; set; }                       // 0x5A
    [JsonPropertyName("language")] public int? Language { get; set; }                 // 0x5B
    [JsonPropertyName("volumeMusic")] public int? VolumeMusic { get; set; }           // 0x5D (0..127)
    [JsonPropertyName("volumeEffects")] public int? VolumeEffects { get; set; }       // 0x5E (0..127)
    [JsonPropertyName("unlocks1C")] public int? Unlocks1C { get; set; }               // 0x1C (bit0 = Monolithic)
    [JsonPropertyName("unlocks1D")] public int? Unlocks1D { get; set; }               // 0x1D (Bomber BFS/Tombstone/FZR)
    [JsonPropertyName("unlocks1E")] public int? Unlocks1E { get; set; }               // 0x1E (SLOT code: "slot car" options)
    /// <summary>Driving keys from the controls screen (config +0x6C..+0x90, see <see cref="KeyBindings"/>): action key →
    /// {scan, char}, scan 0 = unassigned. Saved to settings.json instead of KEYBOARD.TXT/CONFIG.DAT in the game directory.
    /// Missing key or bad entry = default key (from KEYBOARD.TXT); unknown actions are kept on save.</summary>
    [JsonPropertyName("keyBindings")] public Dictionary<string, KeyBindingEntry?>? KeyBindings { get; set; }
    /// <summary>Player 2 (split screen): car, gearbox and paint colours (config +0x3A, +0x3B, +0x3C[16]).</summary>
    [JsonPropertyName("p2Car")] public int? P2Car { get; set; }
    [JsonPropertyName("p2Transmission")] public int? P2Transmission { get; set; }
    [JsonPropertyName("p2Colours")] public int[]? P2Colours { get; set; }
    /// <summary>Player 1 and 2 names (config +0x1AC / +0x1B5, ≤ 8 characters): see <see cref="PlayerNames"/>; edited before a split-screen race starts.</summary>
    [JsonPropertyName("p1Name")] public string? P1Name { get; set; }
    [JsonPropertyName("p2Name")] public string? P2Name { get; set; }
    /// <summary>Split-screen keys (config +0x94..+0xE3, see <see cref="KeyBindings.SplitBuiltin"/>): key "p1.action" /
    /// "p2.action" → {scan, char}; missing key = default (2PLAYER.TXT).</summary>
    [JsonPropertyName("splitKeyBindings")] public Dictionary<string, KeyBindingEntry?>? SplitKeyBindings { get; set; }

    /// <summary>Keys unknown to this version of the port – kept so that an older build does not drop newer settings.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = new();

    /// <summary>Settings carry values (loaded from the file or taken from the configuration); without them <see cref="ApplyTo"/>
    /// overwrites nothing.</summary>
    [JsonIgnore] public bool Loaded { get; private set; }

    /// <summary>Why the file was ignored (corrupt JSON, newer schema, no access) – for the log.</summary>
    [JsonIgnore] public string? LoadError { get; private set; }

    /// <summary>Loads the settings; no file = empty (overwrites nothing), corrupt or newer file = empty + <see cref="LoadError"/>.</summary>
    public static UserSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new UserSettings();
        try
        {
            var parsed = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Options) ?? new UserSettings();
            if (parsed.SchemaVersion > Schema)
            {
                parsed.LoadError = $"schema {parsed.SchemaVersion} > {Schema}";
                return parsed;
            }
            parsed.SchemaVersion = Schema;
            parsed.Loaded = true;
            return parsed;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UserSettings { LoadError = e.Message };
        }
    }

    /// <summary>Atomic save: a temporary file in the same directory, then a replace (an interruption does not leave half a file).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        SchemaVersion = Schema;
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options) + "\n", new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Menu state → settings: <see cref="From(FeConfig)"/> plus the mod cars (by key; the byte fields get a valid original car).</summary>
    public static UserSettings From(MenuController menu)
    {
        var s = From(menu.Config);
        if (menu.ModCarOf(menu.Config.P1Car) is { } p1) { s.P1Car = 0; s.P1CarKey = p1.Key.Value; }
        if (menu.ModCarOf(menu.Config.P2Car) is { } p2) { s.P2Car = 0; s.P2CarKey = p2.Key.Value; }
        if (menu.ModCarOf(menu.Config.OpponentCar) is { } op) { s.OpponentCar = 12; s.OpponentCarKey = op.Key.Value; }
        s.P1ModColours = menu.P1ModColours.Count > 0 ? new(menu.P1ModColours) : null;
        s.P2ModColours = menu.P2ModColours.Count > 0 ? new(menu.P2ModColours) : null;
        return s;
    }

    /// <summary>Puts the mod cars of the file (by key) into the menu once its catalog is known; a key of a mod that is gone is ignored, so the
    /// original car from the byte field stays. Call after <see cref="ApplyTo"/> and after <c>MenuController.Cars</c> was set.</summary>
    public void ApplyCars(MenuController menu)
    {
        if (!Loaded) return;
        int? Resolve(string? text) => text != null && NFS.ModApi.CarKey.TryParse(text, out var key) && menu.Cars.TryGet(key, out var car) && car.LegacyIndex is null
            ? menu.Cars.NumberOf(key) : null;
        if (Resolve(P1CarKey) is { } a) menu.Config.P1Car = (byte)a;
        if (Resolve(P2CarKey) is { } b) menu.Config.P2Car = (byte)b;
        if (Resolve(OpponentCarKey) is { } c) menu.Config.OpponentCar = (byte)c;
        foreach (var (key, index) in P1ModColours ?? []) menu.P1ModColours[key] = index;
        foreach (var (key, index) in P2ModColours ?? []) menu.P2ModColours[key] = index;
    }

    /// <summary>Game configuration state → settings (for saving).</summary>
    public static UserSettings From(FeConfig c) => new()
    {
        P1Car = c.P1Car,
        P1Transmission = c.P1Transmission,
        P1Colours = c.P1Colours.ToArray().Select(b => (int)b).ToArray(),
        Style = c.Style,
        Skill = c.Skill,
        CatchUp = c.CatchUp,
        Track = c.Track,
        Laps = c.Laps,
        LapsMenuIndex = c.LapsMenuIndex,
        Backward = c.Backward,
        Mirrored = c.Mirrored,
        OpponentCar = c.OpponentCar,
        OpponentCount = c.OpponentCount,
        TrafficOff = c.TrafficOff,
        Units = c.Units,
        Language = c.Language,
        VolumeMusic = c[0x5D],
        VolumeEffects = c[0x5E],
        Unlocks1C = c[0x1C],
        Unlocks1D = c[0x1D],
        Unlocks1E = c[0x1E],
        KeyBindings = FrontEnd.KeyBindings.ToEntries(c).ToDictionary(p => p.Key, p => (KeyBindingEntry?)p.Value),
        P2Car = c.P2Car,
        P2Transmission = c.P2Transmission,
        P2Colours = c.P2Colours.ToArray().Select(b => (int)b).ToArray(),
        P1Name = c.ProfileName(0),
        P2Name = c.ProfileName(1),
        SplitKeyBindings = FrontEnd.KeyBindings.ToSplitEntries(c).ToDictionary(p => p.Key, p => (KeyBindingEntry?)p.Value),
        Loaded = true,
    };

    /// <summary>Migration from the original (task 1f, first start without a settings file): profile fields from
    /// `FEDATA/PC/CONFIG/CONFIG.DAT` of the installation; null when the file is missing, has a size other than the original, or cannot be read.
    /// The game/race type is not carried over (From does not include it, as with the reset). [ref:2023]
    /// Keys with no keyboard assignment (joystick, devices 1/2/3, empty) are skipped, so they do not unbind
    /// the port's default keys.</summary>
    public static UserSettings? FromConfigDat(string? gameDir)
    {
        byte[] bytes;
        try
        {
            if (!GameFiles.Exists(FeConfig.FilePath, gameDir)) return null;
            bytes = GameFiles.ReadRaw(FeConfig.FilePath, gameDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (bytes.Length != FeConfig.Size) return null;
        var s = From(FeConfig.Parse(bytes));
        s.KeyBindings = s.KeyBindings!.Where(p => p.Value is { Scan: not 0 }).ToDictionary(p => p.Key, p => p.Value);
        s.SplitKeyBindings = s.SplitKeyBindings!.Where(p => p.Value is { Scan: not 0 }).ToDictionary(p => p.Key, p => p.Value);
        return s;
    }

    /// <summary>Overwrites the configuration only with the fields present in the file. Volumes as in the original: 0..127.</summary>
    public void ApplyTo(FeConfig c)
    {
        if (!Loaded) return;
        if (P1Car is { } p1) c.P1Car = Byte(p1);
        if (P1Transmission is { } tr) c.P1Transmission = Byte(tr);
        if (P1Colours is { } col)
            for (int i = 0; i < 16 && i < col.Length; i++) c.Raw[0x21 + i] = Byte(col[i]);
        if (Style is { } st) c.Style = Byte(st);
        if (Skill is { } sk) c.Skill = Byte(sk);
        if (CatchUp is { } cu) c.CatchUp = Byte(cu);
        if (Track is { } tk) c.Track = (ushort)Math.Clamp(tk, 0, ushort.MaxValue);
        if (Laps is { } lp) c.Laps = Byte(lp);
        if (LapsMenuIndex is { } li) c.LapsMenuIndex = Byte(li);
        if (Backward is { } bw) c.Backward = Byte(bw);
        if (Mirrored is { } mi) c.Mirrored = Byte(mi);
        if (OpponentCar is { } oc) c.OpponentCar = Byte(oc);
        if (OpponentCount is { } on) c.OpponentCount = Byte(on);
        if (TrafficOff is { } to) c.TrafficOff = Byte(to);
        if (Units is { } un) c.Raw[0x5A] = Byte(un);
        if (Language is { } lg) c.Language = Byte(lg);
        if (VolumeMusic is { } vm) c[0x5D] = Volume(vm);
        if (VolumeEffects is { } ve) c[0x5E] = Volume(ve);
        if (Unlocks1C is { } u1) c[0x1C] = Byte(u1);
        if (Unlocks1D is { } u2) c[0x1D] = Byte(u2);
        if (Unlocks1E is { } u3) c[0x1E] = Byte(u3);
        if (KeyBindings is { } kb) FrontEnd.KeyBindings.ApplyEntries(c, kb);
        if (P2Car is { } p2) c.P2Car = Byte(p2);
        if (P2Transmission is { } t2) c.Raw[0x3B] = Byte(t2);
        if (P2Colours is { } col2)
            for (int i = 0; i < 16 && i < col2.Length; i++) c.Raw[0x3C + i] = Byte(col2[i]);
        if (P1Name is { Length: > 0 } n1) c.SetName(FeConfig.ProfileNameOffset(0), n1);
        if (P2Name is { Length: > 0 } n2) c.SetName(FeConfig.ProfileNameOffset(1), n2);
        if (SplitKeyBindings is { } skb) FrontEnd.KeyBindings.ApplySplitEntries(c, skb);
    }

    static byte Byte(int v) => (byte)Math.Clamp(v, 0, 255);
    static byte Volume(int v) => (byte)Math.Clamp(v, 0, 127);   // slider 0..127, as in the original [ref:2024]
}
