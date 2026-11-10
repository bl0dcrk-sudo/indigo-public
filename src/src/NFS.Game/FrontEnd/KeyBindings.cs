using System.Text.Json.Serialization;
using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.FrontEnd;

namespace NFS.Game.FrontEnd;

/// <summary>Keyboard actions from the controls screen (action item 0xB,..); order = offsets in the config. [ref:1688]</summary>
public enum KeyAction { SteerLeft, SteerRight, Accelerate, Brake, LookBehind, CameraView, Handbrake, Horn, ShiftUp, ShiftDown }

/// <summary>Key bindings as in the original: 32-bit word at +0x6C..+0x90 (CONFIG.DAT, one per action): [ref:1689]
/// byte 0 = device (4 = keyboard from the controls screen, 3 = from KEYBOARD.TXT, 0 = none), byte 1 = scancode, byte 3 = ASCII
/// (upper case; 0 for keys without a character) – <c>(4, scan | ascii&lt;&lt;16)</c> = <c>4 (scan | ascii&lt;&lt;16) * 0x100</c>. [ref:1690]
/// Default values come from GAMEDATA/PREFS/PC/KEYBOARD.TXT (rows `C1_RIGHT = key,SC_RIGHT`; names `SC_*` from the original's table
/// 82 entries {name, scancode}); on a bad state the original calls its own routine. [ref:1691]
/// Key names on screen: draws TEXT 0x11D "keyboard" and the key name (TEXT 0x121..0x12F for [ref:1692]
/// space, Enter, Insert, Delete, Home, End, PgUp, PgDn, arrows, Backspace, Tab, Center; other keys = ASCII "%c").</summary>
public static class KeyBindings
{
    public const int FirstOffset = 0x6C, Stride = 4, Count = 10;
    public const int DeviceKeyboard = 4, DeviceKeyboardFile = 3;
    /// <summary>Scancode F5 (C1_TOGGLEHUD) – an action outside the controls screen, taken directly from KEYBOARD.TXT.</summary>
    public const int ScanF5 = 0x3F;
    public const string File = "GAMEDATA/PREFS/PC/KEYBOARD.TXT";

    /// <summary>KEYBOARD.TXT row names for successive actions.</summary>
    public static readonly string[] FileNames =
        ["C1_LEFT", "C1_RIGHT", "C1_GAS", "C1_BRAKE", "C1_LOOKBEHIND", "C1_CYCLECAMERA", "C1_HANDBRAKE", "C1_HORN", "C1_SHIFTUP", "C1_SHIFTDOWN"];

    /// <summary>Key names in settings.json.</summary>
    public static readonly string[] JsonNames =
        ["steerLeft", "steerRight", "accelerate", "brake", "lookBehind", "cameraView", "handbrake", "horn", "shiftUp", "shiftDown"];

    /// <summary>Built-in scancodes = the game's default KEYBOARD.TXT: left, right, up, down, B, C, space, H, A, Z.</summary>
    public static readonly int[] Builtin = [0x4B, 0x4D, 0x48, 0x50, 0x30, 0x2E, 0x39, 0x23, 0x1E, 0x2C];

    // ---- split screen (game type 1): second key table, 10 pairs {player 1, player 2} from +0x94 (stride 8) ----
    // (case 1) takes both players' keys from here (the +0x6C table is not used), the screen [ref:1693]
    //  draws two columns; duplicates are cleared across all 20 entries of the table. [ref:1694]
    public const int SplitFirstOffset = 0x94, SplitStride = 8, SplitPlayers = 2;
    public const string SplitFile = "GAMEDATA/PREFS/PC/2PLAYER.TXT";

    /// <summary>Built-in split scancodes (the same as CONFIG.DAT on the disc and 2PLAYER.TXT): [ref:1695]
    /// player 1 ← → ↑ ↓ PgDn PgUp Home End Insert Delete, player 2 S F E D Z R W T Q A (action order as <see cref="KeyAction"/>).</summary>
    public static readonly int[][] SplitBuiltin =
    [
        [0x4B, 0x4D, 0x48, 0x50, 0x51, 0x49, 0x47, 0x4F, 0x52, 0x53],
        [0x1F, 0x21, 0x12, 0x20, 0x2C, 0x13, 0x11, 0x14, 0x10, 0x1E],
    ];

    /// <summary>2PLAYER.TXT row names (`C1_*` player 1, `C2_*` player 2).</summary>
    public static string[] SplitFileNames(int player) =>
        [.. FileNames.Select(n => $"C{player + 1}_{n["C1_".Length..]}")];

    public static int SplitOffsetOf(int player, KeyAction a) => SplitFirstOffset + SplitStride * (int)a + 4 * player;
    public static int GetSplit(FeConfig c, int player, KeyAction a) => c.I32(SplitOffsetOf(player, a));
    public static void SetSplit(FeConfig c, int player, KeyAction a, int word) =>
        BitConverter.GetBytes(word).CopyTo(c.Raw, SplitOffsetOf(player, a));

    /// <summary>Player and action of the split entry at the config offset (null outside the table +0x94..+0xE3).</summary>
    public static (int Player, KeyAction Action)? SplitActionAt(int configOffset)
    {
        int rel = configOffset - SplitFirstOffset;
        if (rel < 0 || rel >= SplitStride * Count || rel % 4 != 0) return null;
        return (rel / 4 % 2, (KeyAction)(rel / SplitStride));
    }

    /// <summary>DONE on the screen is greyed out while any of the 20 entries is empty (zero). [ref:1696]</summary>
    public static bool AllAssignedSplit(FeConfig c) =>
        Enumerable.Range(0, SplitPlayers).All(p => Enumerable.Range(0, Count).All(i => IsAssigned(GetSplit(c, p, (KeyAction)i))));

    /// <summary>Default split scancodes from the text of 2PLAYER.TXT (`C1_*` / `C2_*`); a missing row = <see cref="SplitBuiltin"/>.</summary>
    public static int[][] ParseSplitFile(byte[]? data, IReadOnlyDictionary<string, int> names)
    {
        var result = SplitBuiltin.Select(a => (int[])a.Clone()).ToArray();
        if (data == null) return result;
        var text = PrefsText.Parse(data);
        for (int p = 0; p < SplitPlayers; p++)
        {
            var rows = SplitFileNames(p);
            for (int i = 0; i < Count; i++)
                if (text.Find(rows[i]) is { } e && e.Values.Length >= 2 && e.Values[0].Equals("key", StringComparison.OrdinalIgnoreCase)
                    && names.TryGetValue(e.Values[1], out int scan) && scan is > 0 and < 0x80) result[p][i] = scan;
        }
        return result;
    }

    public static int[][] LoadSplitDefaults(string? gameDir)
    {
        byte[]? data = null;
        try { if (GameFiles.Exists(SplitFile, gameDir)) data = GameFiles.ReadRaw(SplitFile, gameDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ParseSplitFile(data, ScancodeNames());
    }

    public static void ApplySplitDefaults(FeConfig c, int[][] scans)
    {
        for (int p = 0; p < SplitPlayers; p++)
            for (int i = 0; i < Count; i++) SetSplit(c, p, (KeyAction)i, WordFor(scans[p][i]));
    }

    /// <summary>For the split table: if none of the 20 entries is a key, insert the defaults. [ref:1697]</summary>
    public static void EnsureSplitDefaults(FeConfig c, int[][] scans)
    {
        bool any = Enumerable.Range(0, SplitPlayers).Any(p => Enumerable.Range(0, Count).Any(i => IsKey(GetSplit(c, p, (KeyAction)i))));
        if (!any) ApplySplitDefaults(c, scans);
    }

    /// <summary>settings.json entry for a word: key = {scan, char}, joystick = {word} (devices 1/2), otherwise empty.</summary>
    static KeyBindingEntry Entry(int w) =>
        IsKey(w) ? new KeyBindingEntry { Scan = Scan(w), Char = Ascii(w) }
        : JoyBindings.IsJoystick(w) ? new KeyBindingEntry { Word = w } : new KeyBindingEntry();

    /// <summary>Word from a settings.json entry: a valid joystick word, a key (scan from <see cref="Assignable"/>), 0 = none; null = entry to skip.</summary>
    static int? Staged(KeyBindingEntry e)
    {
        if (e.Word != 0) return JoyBindings.IsJoystick(e.Word) ? e.Word : null;
        if (e.Scan == 0) return 0;
        if (!Assignable(e.Scan)) return null;
        return Word(e.Scan, Upper(e.Char is >= 0 and < 0x80 ? e.Char : 0));
    }

    /// <summary>To settings.json: key "p1.action" / "p2.action" (names as <see cref="JsonNames"/>) → {scan, char}; scan 0 = none.</summary>
    public static Dictionary<string, KeyBindingEntry> ToSplitEntries(FeConfig c)
    {
        var r = new Dictionary<string, KeyBindingEntry>();
        for (int p = 0; p < SplitPlayers; p++)
            for (int i = 0; i < Count; i++)
            {
                int w = GetSplit(c, p, (KeyAction)i);
                r[$"p{p + 1}.{JsonNames[i]}"] = Entry(w);
            }
        return r;
    }

    /// <summary>As <see cref="ApplyEntries"/> for the split table (displacing duplicates across all 20 entries).</summary>
    public static void ApplySplitEntries(FeConfig c, IReadOnlyDictionary<string, KeyBindingEntry?> entries)
    {
        var staged = new Dictionary<(int, int), int>();
        for (int p = 0; p < SplitPlayers; p++)
            for (int i = 0; i < Count; i++)
            {
                if (!entries.TryGetValue($"p{p + 1}.{JsonNames[i]}", out var e) || e == null) continue;
                if (Staged(e) is { } word) staged[(p, i)] = word;
            }
        var explicitWords = staged.Values.Where(w => w != 0).ToHashSet();
        for (int p = 0; p < SplitPlayers; p++)
            for (int i = 0; i < Count; i++)
            {
                var a = (KeyAction)i;
                if (staged.TryGetValue((p, i), out int w)) SetSplit(c, p, a, w);
                else if (explicitWords.Contains(GetSplit(c, p, a))) SetSplit(c, p, a, 0);
            }
    }

    /// <summary>Assignment on the screen (as <see cref="Assign"/>, but displaces the same key across all 20 entries). [ref:1698]</summary>
    public static bool AssignSplit(FeConfig c, int player, KeyAction action, int scan, int ascii)
    {
        if (!Assignable(scan)) return false;
        AssignSplitWord(c, player, action, Word(scan, Upper(ascii & 0xFF)));
        return true;
    }

    /// <summary>As <see cref="AssignWord"/> for the split table (conflicts across all 20 entries).</summary>
    public static void AssignSplitWord(FeConfig c, int player, KeyAction action, int word)
    {
        for (int p = 0; p < SplitPlayers; p++)
            for (int i = 0; i < Count; i++)
                if (Conflicts(GetSplit(c, p, (KeyAction)i), word)) SetSplit(c, p, (KeyAction)i, 0);
        SetSplit(c, player, action, word);
    }

    public static int OffsetOf(KeyAction a) => FirstOffset + Stride * (int)a;
    public static KeyAction? ActionAt(int configOffset) =>
        configOffset >= FirstOffset && configOffset < FirstOffset + Stride * Count && (configOffset - FirstOffset) % Stride == 0
            ? (KeyAction)((configOffset - FirstOffset) / Stride) : null;

    public static int Word(int scan, int ascii) => DeviceKeyboard + ((scan | ascii << 16) << 8);
    public static int Device(int word) => word & 0xFF;
    public static int Scan(int word) => word >> 8 & 0xFF;
    public static int Ascii(int word) => word >> 24 & 0xFF;
    public static bool IsKey(int word) => Device(word) is DeviceKeyboard or DeviceKeyboardFile;
    /// <summary>Word assigned to an action: keyboard or joystick (devices 1/2, <see cref="JoyBindings"/>); checks only "non-zero". [ref:1699]</summary>
    public static bool IsAssigned(int word) => IsKey(word) || JoyBindings.IsJoystick(word);

    public static int Get(FeConfig c, KeyAction a) => c.I32(OffsetOf(a));
    public static void Set(FeConfig c, KeyAction a, int word) => BitConverter.GetBytes(word).CopyTo(c.Raw, OffsetOf(a));
    public static bool AllAssigned(FeConfig c) => Enumerable.Range(0, Count).All(i => IsAssigned(Get(c, (KeyAction)i)));

    /// <summary>ASCII character of a key on the US layout (as from the BIOS keyboard buffer); 0 = key without a character.</summary>
    public static int AsciiOf(int scan) => scan switch
    {
        >= 0x02 and <= 0x0A => '1' + scan - 0x02, 0x0B => '0', 0x0C => '-', 0x0D => '=',
        >= 0x10 and <= 0x19 => "QWERTYUIOP"[scan - 0x10], 0x1A => '[', 0x1B => ']',
        >= 0x1E and <= 0x26 => "ASDFGHJKL"[scan - 0x1E], 0x27 => ';', 0x28 => '\'', 0x29 => '`', 0x2B => '\\',
        0x39 => ' ', 0x0F => '\t', 0x0E => '\b', 0x1C => '\r',
        >= 0x2C and <= 0x32 => "ZXCVBNM"[scan - 0x2C], 0x33 => ',', 0x34 => '.', 0x35 => '/',
        _ => 0
    };

    /// <summary>ASCII toupper from the original (only 'a'..'z' → −0x20); the key name is [ref:1700]
    /// `sprintf("%c", toupper(ascii))` (format), and a captured key is stored in upper case. [ref:1701]</summary>
    public static int Upper(int c) => c is >= 'a' and <= 'z' ? c - 0x20 : c;

    /// <summary>Table of `SC_*` names → scancode from the original. [ref:1702]</summary>
    public static IReadOnlyDictionary<string, int> ScancodeNames()
    {
        var r = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, scan) in TextConstants.ScancodeNames) r[name] = scan;
        return r;
    }

    /// <summary>Default scancodes (action → scan, plus F5 for the HUD) from the text of KEYBOARD.TXT: rows `C1_x = key,SC_y`.
    /// A missing or unparseable row = the value from <see cref="Builtin"/> (the game's default file).</summary>
    public static (int[] Scans, int HudScan) ParseFile(byte[]? data, IReadOnlyDictionary<string, int> names)
    {
        var scans = (int[])Builtin.Clone();
        int hud = ScanF5;
        if (data == null) return (scans, hud);
        var text = PrefsText.Parse(data);
        static bool Key(PrefsEntry e, IReadOnlyDictionary<string, int> n, out int scan)
        {
            scan = 0;
            return e.Values.Length >= 2 && e.Values[0].Equals("key", StringComparison.OrdinalIgnoreCase)
                && n.TryGetValue(e.Values[1], out scan) && scan is > 0 and < 0x80;
        }
        for (int i = 0; i < Count; i++)
            if (text.Find(FileNames[i]) is { } e && Key(e, names, out int s)) scans[i] = s;
        if (text.Find("C1_TOGGLEHUD") is { } h && Key(h, names, out int hs)) hud = hs;
        return (scans, hud);
    }

    /// <summary>Word from the controls screen for a scancode from the file (ASCII as on a US keyboard, 0 for arrows/space).</summary>
    public static int WordFor(int scan) => scan == 0 ? 0 : Word(scan, AsciiOf(scan));

    /// <summary>Reads the defaults from the game (KEYBOARD.TXT; no file = built-in).</summary>
    public static (int[] Scans, int HudScan) LoadDefaults(string? gameDir)
    {
        byte[]? data = null;
        try { if (GameFiles.Exists(File, gameDir)) data = GameFiles.ReadRaw(File, gameDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ParseFile(data, ScancodeNames());
    }

    /// <summary>Sets all actions to the defaults (action 0x13 "reset"). [ref:1703]</summary>
    public static void ApplyDefaults(FeConfig c, int[] scans)
    {
        for (int i = 0; i < Count; i++) Set(c, (KeyAction)i, WordFor(scans[i]));
    }

    /// <summary>If no entry is a key (config without bindings), inserts the defaults. [ref:1704]</summary>
    public static void EnsureDefaults(FeConfig c, int[] scans)
    {
        if (!Enumerable.Range(0, Count).Any(i => IsKey(Get(c, (KeyAction)i)))) ApplyDefaults(c, scans);
    }

    /// <summary>Scancodes that cannot be assigned on the controls screen: F1..F10 (0x3B..0x44) and Enter. [ref:1705]</summary>
    public static bool Assignable(int scan) => scan is > 0 and < 0x80 && scan is not (>= 0x3B and <= 0x44) && scan != 0x1C;

    /// <summary>Assignment from a captured key: the same key displaces another action [ref:1706]
    /// (its entry = 0, "________"). Returns false when the key is rejected.</summary>
    public static bool Assign(FeConfig c, KeyAction action, int scan, int ascii)
    {
        if (!Assignable(scan)) return false;
        AssignWord(c, action, Word(scan, Upper(ascii & 0xFF)));
        return true;
    }

    /// <summary>Assignment of any word (a key or a joystick from capture) [ref:1707]
    /// displaces conflicting entries (<see cref="Conflicts"/>) and stores the word in the action.</summary>
    public static void AssignWord(FeConfig c, KeyAction action, int word)
    {
        for (int i = 0; i < Count; i++)
            if (Conflicts(Get(c, (KeyAction)i), word)) Set(c, (KeyAction)i, 0);
        Set(c, action, word);
    }

    /// <summary>Rule: device 3 – equal ignoring byte 3; otherwise an identical word; for an axis (device 1) also the same [ref:1708]
    /// axis of the same joystick when the response ranges touch (byte 2 = byte 2, byte 1 = byte 1, byte 1 = byte 2 or byte 2 = byte 1 of the word).</summary>
    public static bool Conflicts(int existing, int word)
    {
        if (word == 0) return false;
        if ((word & 0xFF) == DeviceKeyboardFile) return (existing & 0xFFFFFF) == (word & 0xFFFFFF);
        if (existing == word) return true;
        if ((word & 0xFF) != JoyBindings.DeviceAxis || ((uint)existing & 0xFF0000FF) != ((uint)word & 0xFF0000FF)) return false;
        int b1 = word & 0xFF00, b2 = word & 0xFF0000;
        return (existing & 0xFF0000) == b2 || (existing & 0xFF00) == b1 || (existing & 0xFF00) == b2 >> 8 || (existing & 0xFF0000) == b1 << 8;
    }

    /// <summary>Key text as on the screen: (device label, name). No binding = (null, "________" TEXT 0x133).
    ///  draws the name only for device 4 (keyboard from the controls screen); devices 1/2/3 give the column [ref:1709]
    /// joystick/axis + `sprintf("%d"/"%c")` (TEXT 0x11E/0x11F/0x120) – they are not in the port's config (always 4), so
    /// the port draws the key name for them as for 4; device 0 and ≥5 are "________".</summary>
    public static (string? Device, string Name) Display(int word, TextFile text)
    {
        string T(int id) => id < text.Entries.Count ? text[id] : "";
        if (!IsKey(word)) return (null, T(0x133));
        int id = Scan(word) switch
        {
            0x39 => 0x121, 0x1C => 0x122, 0x52 => 0x123, 0x53 => 0x124, 0x47 => 0x125, 0x4F => 0x126, 0x49 => 0x127,
            0x51 => 0x128, 0x48 => 0x129, 0x50 => 0x12A, 0x4B => 0x12B, 0x4D => 0x12C, 0x0E => 0x12D, 0x0F => 0x12E,
            0x4C => 0x12F, _ => 0
        };
        if (id != 0) return (T(0x11D), T(id));
        int a = Ascii(word);
        return (T(0x11D), a == 0 ? "" : ((char)Upper(a)).ToString());
    }

    /// <summary>To settings.json: action key → {scan, char}; scan 0 = no binding.</summary>
    public static Dictionary<string, KeyBindingEntry> ToEntries(FeConfig c)
    {
        var r = new Dictionary<string, KeyBindingEntry>();
        for (int i = 0; i < Count; i++)
        {
            int w = Get(c, (KeyAction)i);
            r[JsonNames[i]] = Entry(w);
        }
        return r;
    }

    /// <summary>From settings.json to the config: only valid entries (scan from <see cref="Assignable"/> or 0 = none); bad and missing
    /// entries keep the default key. An explicitly set key displaces the same default key of another action.</summary>
    public static void ApplyEntries(FeConfig c, IReadOnlyDictionary<string, KeyBindingEntry?> entries)
    {
        var staged = new Dictionary<KeyAction, int>();
        for (int i = 0; i < Count; i++)
        {
            if (!entries.TryGetValue(JsonNames[i], out var e) || e == null) continue;
            if (Staged(e) is { } word) staged[(KeyAction)i] = word;
        }
        var explicitWords = staged.Values.Where(w => w != 0).ToHashSet();
        for (int i = 0; i < Count; i++)
        {
            var a = (KeyAction)i;
            if (staged.TryGetValue(a, out int w)) Set(c, a, w);
            else if (explicitWords.Contains(Get(c, a))) Set(c, a, 0);
        }
    }
}

/// <summary>Key entry in settings.json: PC scancode (set 1) and ASCII character (upper case, 0 = key without a character).</summary>
public sealed class KeyBindingEntry
{
    [JsonPropertyName("scan")] public int Scan { get; set; }
    [JsonPropertyName("char")] public int Char { get; set; }
    /// <summary>Joystick/gamepad binding (device word 1 = axis or 2 = button, <see cref="JoyBindings"/>); 0 = keyboard/none.
    /// The key is written only for a joystick, so old settings files stay unchanged.</summary>
    [JsonPropertyName("word"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Word { get; set; }
}
