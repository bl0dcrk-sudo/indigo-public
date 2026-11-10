using Microsoft.Xna.Framework.Input;
using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>PC scancodes (set 1) ↔ MonoGame keys. Navigation key scancodes (0x47..0x53) are as in the original (arrows = 0x48/0x50/0x4B/0x4D);
/// the numeric keypad cannot be bound (in the original it shares these scancodes with the navigation keys).</summary>
public static class KeyScan
{
    static readonly (int Scan, Keys Key)[] Table =
    [
        (0x01, Keys.Escape), (0x02, Keys.D1), (0x03, Keys.D2), (0x04, Keys.D3), (0x05, Keys.D4), (0x06, Keys.D5), (0x07, Keys.D6),
        (0x08, Keys.D7), (0x09, Keys.D8), (0x0A, Keys.D9), (0x0B, Keys.D0), (0x0C, Keys.OemMinus), (0x0D, Keys.OemPlus),
        (0x0E, Keys.Back), (0x0F, Keys.Tab), (0x10, Keys.Q), (0x11, Keys.W), (0x12, Keys.E), (0x13, Keys.R), (0x14, Keys.T),
        (0x15, Keys.Y), (0x16, Keys.U), (0x17, Keys.I), (0x18, Keys.O), (0x19, Keys.P), (0x1A, Keys.OemOpenBrackets),
        (0x1B, Keys.OemCloseBrackets), (0x1C, Keys.Enter), (0x1D, Keys.LeftControl), (0x1E, Keys.A), (0x1F, Keys.S), (0x20, Keys.D),
        (0x21, Keys.F), (0x22, Keys.G), (0x23, Keys.H), (0x24, Keys.J), (0x25, Keys.K), (0x26, Keys.L), (0x27, Keys.OemSemicolon),
        (0x28, Keys.OemQuotes), (0x29, Keys.OemTilde), (0x2A, Keys.LeftShift), (0x2B, Keys.OemPipe), (0x2C, Keys.Z), (0x2D, Keys.X),
        (0x2E, Keys.C), (0x2F, Keys.V), (0x30, Keys.B), (0x31, Keys.N), (0x32, Keys.M), (0x33, Keys.OemComma), (0x34, Keys.OemPeriod),
        (0x35, Keys.OemQuestion), (0x36, Keys.RightShift), (0x38, Keys.LeftAlt), (0x39, Keys.Space), (0x3A, Keys.CapsLock),
        (0x3B, Keys.F1), (0x3C, Keys.F2), (0x3D, Keys.F3), (0x3E, Keys.F4), (0x3F, Keys.F5), (0x40, Keys.F6), (0x41, Keys.F7),
        (0x42, Keys.F8), (0x43, Keys.F9), (0x44, Keys.F10), (0x45, Keys.NumLock), (0x46, Keys.Scroll),
        (0x47, Keys.Home), (0x48, Keys.Up), (0x49, Keys.PageUp), (0x4B, Keys.Left), (0x4D, Keys.Right), (0x4F, Keys.End),
        (0x50, Keys.Down), (0x51, Keys.PageDown), (0x52, Keys.Insert), (0x53, Keys.Delete), (0x57, Keys.F11), (0x58, Keys.F12),
    ];

    static readonly Dictionary<int, Keys> ToKey = Table.ToDictionary(t => t.Scan, t => t.Key);
    static readonly Dictionary<Keys, int> ToScan = Table.ToDictionary(t => t.Key, t => t.Scan);

    /// <summary>MonoGame key for a scancode; <see cref="Keys.None"/> = none (scancode 0 or unknown).</summary>
    public static Keys KeyOf(int scan) => ToKey.GetValueOrDefault(scan, Keys.None);

    /// <summary>Captured key (scancode + ASCII), or null when it is not a key from the table.</summary>
    public static KeyCapture? Capture(Keys key) =>
        ToScan.TryGetValue(key, out int scan) ? new KeyCapture(scan, KeyBindings.AsciiOf(scan)) : null;

    /// <summary>Keys to capture in this frame: newly pressed, without Esc (Esc ends capturing in the menu).</summary>
    public static KeyCapture? FirstPressed(KeyboardState now, KeyboardState before)
    {
        foreach (var (scan, key) in Table)
            if (key != Keys.Escape && now.IsKeyDown(key) && !before.IsKeyDown(key)) return Capture(key);
        return null;
    }
}

/// <summary>Driving keys from the menu bindings (config +0x6C..+0x90) and the HUD from KEYBOARD.TXT; defaults as in KEYBOARD.TXT.</summary>
public sealed record DriveKeyMap(Keys Left, Keys Right, Keys Gas, Keys Brake, Keys LookBehind, Keys Camera, Keys Handbrake,
    Keys Horn, Keys ShiftUp, Keys ShiftDown, Keys ToggleHud, Keys HorizonToggle)
{
    /// <summary>I_HORIZON key (tag 0x49, 0xE): the original has no default key for it – KEYBOARD.TXT [ref:1096]
    /// lists only C1_*, and exposes tags 0x3B..0x44 (MAINEXIT, I_UP..I_GAMMA). Port [ref:1097]
    /// takes F9, continuing the sequence from DEFAULT.TXT (I_DETAIL=F5, I_SCREENSIZE=F6, I_INTERLACE=F7).</summary>
    public const Keys HorizonKey = Keys.F9;

    public static DriveKeyMap Default { get; } = From(KeyBindings.Builtin, KeyBindings.ScanF5);

    static DriveKeyMap From(int[] scans, int hud) => new(KeyScan.KeyOf(scans[0]), KeyScan.KeyOf(scans[1]), KeyScan.KeyOf(scans[2]),
        KeyScan.KeyOf(scans[3]), KeyScan.KeyOf(scans[4]), KeyScan.KeyOf(scans[5]), KeyScan.KeyOf(scans[6]), KeyScan.KeyOf(scans[7]),
        KeyScan.KeyOf(scans[8]), KeyScan.KeyOf(scans[9]), KeyScan.KeyOf(hud), HorizonKey);

    /// <summary>Bindings from the menu config; an empty entry = action without a key.</summary>
    public static DriveKeyMap From(MenuController menu)
    {
        var scans = new int[KeyBindings.Count];
        for (int i = 0; i < scans.Length; i++)
        {
            int w = KeyBindings.Get(menu.Config, (KeyAction)i);
            scans[i] = KeyBindings.IsKey(w) ? KeyBindings.Scan(w) : 0;
        }
        return From(scans, menu.ToggleHudScan);
    }
}

/// <summary>Split screen keys (config +0x94..+0xE3: player 1 and player 2, default 2PLAYER.TXT) plus keys outside the controls screen:
/// car reset F11 (player 1, tag 0x28) and F12 (player 2, tag 0x34); player 1 HUD toggle [ref:1098]
/// C1_TOGGLEHUD from KEYBOARD.TXT (F5); player 2 – the original does not define I_MAP2, port decision: F6.</summary>
public sealed record SplitKeyMap(DriveKeyMap Player1, DriveKeyMap Player2, Keys Reset1, Keys Reset2)
{
    public const Keys ResetKey1 = Keys.F11, ResetKey2 = Keys.F12, ToggleHudKey2 = Keys.F6;

    public static SplitKeyMap Default { get; } = From(KeyBindings.SplitBuiltin, KeyBindings.ScanF5);

    static SplitKeyMap From(int[][] scans, int hud)
    {
        DriveKeyMap Map(int[] s, Keys toggle) => new(KeyScan.KeyOf(s[0]), KeyScan.KeyOf(s[1]), KeyScan.KeyOf(s[2]), KeyScan.KeyOf(s[3]),
            KeyScan.KeyOf(s[4]), KeyScan.KeyOf(s[5]), KeyScan.KeyOf(s[6]), KeyScan.KeyOf(s[7]), KeyScan.KeyOf(s[8]), KeyScan.KeyOf(s[9]),
            toggle, DriveKeyMap.HorizonKey);
        return new(Map(scans[0], KeyScan.KeyOf(hud)), Map(scans[1], ToggleHudKey2), ResetKey1, ResetKey2);
    }

    /// <summary>Bindings from the menu config (split table); an empty entry = action without a key.</summary>
    public static SplitKeyMap From(MenuController menu)
    {
        var scans = new int[KeyBindings.SplitPlayers][];
        for (int p = 0; p < scans.Length; p++)
        {
            scans[p] = new int[KeyBindings.Count];
            for (int i = 0; i < KeyBindings.Count; i++)
            {
                int w = KeyBindings.GetSplit(menu.Config, p, (KeyAction)i);
                scans[p][i] = KeyBindings.IsKey(w) ? KeyBindings.Scan(w) : 0;
            }
        }
        return From(scans, menu.ToggleHudScan);
    }
}
