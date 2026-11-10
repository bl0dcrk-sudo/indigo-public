using Microsoft.Xna.Framework.Input;
using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>Port host bindings, deliberately independent of the original hardware drivers.
/// Keyboard driving bindings follow PREFS/PC/KEYBOARD.TXT; pad bindings are the port's policy.
/// No polling here: supplied MonoGame snapshots can be tested without a device/window.</summary>
public sealed class InputMapping
{
    KeyboardState _keyboard;
    GamePadState _pad;
    readonly JoyState _joy = new();
    readonly JoyState _joy2 = new();
    readonly JoyState[] _joysticks, _allJoysticks;
    readonly int[] _joyWords = new int[KeyBindings.Count], _splitJoyWords = new int[KeyBindings.Count], _split2JoyWords = new int[KeyBindings.Count];
    /// <summary>Shift + cyfra 0..9 w ukladzie US.</summary>
    static readonly char[] ShiftedDigits = [')', '!', '@', '#', '$', '%', '^', '&', '*', '('];
    /// <summary>Typed character keys (without letters and digits): the plain and Shift character, US layout.</summary>
    static readonly (Keys Key, char Plain, char Shifted)[] TypedKeys =
    [
        (Keys.Space, ' ', ' '), (Keys.Back, (char)8, (char)8), (Keys.OemMinus, '-', '_'), (Keys.OemPlus, '=', '+'),
        (Keys.OemPeriod, '.', '>'), (Keys.OemComma, ',', '<'), (Keys.OemQuotes, '\'', '"'), (Keys.OemSemicolon, ';', ':'),
        (Keys.OemQuestion, '/', '?'), (Keys.OemOpenBrackets, '[', '{'), (Keys.OemCloseBrackets, ']', '}'),
        (Keys.OemPipe, '\\', '|'), (Keys.OemBackslash, '\\', '|'), (Keys.OemTilde, '`', '~'),
        (Keys.Multiply, '*', '*'), (Keys.Add, '+', '+'), (Keys.Subtract, '-', '-'), (Keys.Divide, '/', '/'), (Keys.Decimal, '.', '.'),
    ];
    public InputMapping() { _joysticks = [_joy]; _allJoysticks = [_joy, _joy2]; }
    /// <summary>Joystick bindings from the controls screens (action words; single-player driving and player 1 in split screen),
    /// added to the default pad buttons – see <see cref="PadJoystick.Apply"/>. No allocation (called every frame).</summary>
    public void SetJoystickBindings(MenuController menu)
    {
        for (int i = 0; i < KeyBindings.Count; i++)
        {
            _joyWords[i] = KeyBindings.Get(menu.Config, (KeyAction)i);
            _splitJoyWords[i] = KeyBindings.GetSplit(menu.Config, 0, (KeyAction)i);
            _split2JoyWords[i] = KeyBindings.GetSplit(menu.Config, 1, (KeyAction)i);
        }
    }
    /// <summary>Driving keys (from the controls screen; default as in KEYBOARD.TXT). Menu navigation and codes are fixed.</summary>
    public DriveKeyMap Bindings { get; set; } = DriveKeyMap.Default;
    /// <summary>Split screen keys (two players from the +0x94 table); single-player driving uses <see cref="Bindings"/>.</summary>
    public SplitKeyMap SplitBindings { get; set; } = SplitKeyMap.Default;
    /// <summary>Samples keyboard scan-code bits 0x36 (RightShift) and 0x2E (C) at race initialization. [ref:1091]</summary>
    public static bool OrbitCameraActivation(KeyboardState keyboard)
        => keyboard.IsKeyDown(Keys.RightShift) && keyboard.IsKeyDown(Keys.C);
    /// <summary>Samples keyboard scan-code bits 0x36 (RightShift) and 0x30 (B) at race initialization: fixed camera mode 8. [ref:1092]</summary>
    public static bool FixedCameraActivation(KeyboardState keyboard)
        => keyboard.IsKeyDown(Keys.RightShift) && keyboard.IsKeyDown(Keys.B);
    /// <summary>The second pad (<c>PlayerIndex.Two</c>) = "joystick 2": controls only player 2 in split screen (the port's default buttons and player 2's bindings to joystick 2);
    /// it does not affect player 1, single-player driving or the menu. Without pad 2 (the default state) the behaviour is unchanged.</summary>
    public DesktopInput Read(KeyboardState keyboard, GamePadState pad, GamePadState pad2 = default)
    {
        bool Key(Keys k) => keyboard.IsKeyDown(k);
        bool Button(Buttons b) => pad.IsConnected && pad.IsButtonDown(b);
        bool Edge(Keys k, Buttons b) => (Key(k) && !_keyboard.IsKeyDown(k)) ||
            (Button(b) && (!_pad.IsConnected || !_pad.IsButtonDown(b)));
        var menu = new List<MenuKey>();
        foreach (var (key, button, command) in new[] {
            (Keys.Up, Buttons.DPadUp, MenuKey.Up), (Keys.Down, Buttons.DPadDown, MenuKey.Down),
            (Keys.Left, Buttons.DPadLeft, MenuKey.Left), (Keys.Right, Buttons.DPadRight, MenuKey.Right),
            (Keys.Enter, Buttons.A, MenuKey.Enter), (Keys.Escape, Buttons.B, MenuKey.Escape) })
            if (Edge(key, button)) menu.Add(command);
        var b = Bindings;
        bool Bound(Keys k) => k != Keys.None && Key(k);
        var drive = new DriveKeys(Bound(b.Gas), Bound(b.Brake), Bound(b.Left), Bound(b.Right),
            Bound(b.ShiftUp) || Button(Buttons.RightShoulder), Bound(b.ShiftDown) || Button(Buttons.LeftShoulder),
            Key(Keys.R) || Button(Buttons.Y), Bound(b.Horn) || Button(Buttons.LeftStick),
            Bound(b.Handbrake) || Button(Buttons.X),
            pad.IsConnected ? (sbyte)Math.Clamp((int)MathF.Round(pad.ThumbSticks.Left.X * 127), -127, 127) : (sbyte)0,
            pad.IsConnected ? (byte)Math.Clamp((int)MathF.Round(pad.Triggers.Right * 255), 0, 255) : (byte)0,
            pad.IsConnected ? (byte)Math.Clamp((int)MathF.Round(pad.Triggers.Left * 255), 0, 255) : (byte)0,
            Bound(b.Camera) || Button(Buttons.Back), Bound(b.ToggleHud),
            Bound(b.LookBehind) || Button(Buttons.RightStick), // KEYBOARD.TXT: C1_LOOKBEHIND=SC_B (held).
            Bound(b.HorizonToggle), // I_HORIZON: the original has no default key, port = F9. [ref:1093]
            Key(Keys.Q), Key(Keys.E), Key(Keys.W)); // network race spectator (previous / next car, leader); driving does not read them.
        // Split screen: player 1 (split table keys + pad) and player 2 (keyboard only); car reset F11 / F12.
        var sb = SplitBindings;
        DriveKeys Split(DriveKeyMap m, Keys reset, bool usePad) => new(Bound(m.Gas), Bound(m.Brake), Bound(m.Left), Bound(m.Right),
            Bound(m.ShiftUp) || usePad && Button(Buttons.RightShoulder), Bound(m.ShiftDown) || usePad && Button(Buttons.LeftShoulder),
            Key(reset) || usePad && Button(Buttons.Y), Bound(m.Horn) || usePad && Button(Buttons.LeftStick),
            Bound(m.Handbrake) || usePad && Button(Buttons.X),
            usePad && pad.IsConnected ? (sbyte)Math.Clamp((int)MathF.Round(pad.ThumbSticks.Left.X * 127), -127, 127) : (sbyte)0,
            usePad && pad.IsConnected ? (byte)Math.Clamp((int)MathF.Round(pad.Triggers.Right * 255), 0, 255) : (byte)0,
            usePad && pad.IsConnected ? (byte)Math.Clamp((int)MathF.Round(pad.Triggers.Left * 255), 0, 255) : (byte)0,
            Bound(m.Camera) || usePad && Button(Buttons.Back), Bound(m.ToggleHud), Bound(m.LookBehind) || usePad && Button(Buttons.RightStick), false);
        PadJoystick.Update(_joy, pad);
        PadJoystick.Update(_joy2, pad2);
        drive = PadJoystick.Apply(drive, _joyWords, _joysticks);
        var split1 = PadJoystick.Apply(Split(sb.Player1, sb.Reset1, true), _splitJoyWords, _allJoysticks);
        var split2 = PadJoystick.Apply(SplitPadMap.Overlay(Split(sb.Player2, sb.Reset2, false), pad2), _split2JoyWords, _allJoysticks);
        bool pause = Edge(Keys.Escape, Buttons.Start);
        // Menu codes: letters and digits (top digit row and numeric keypad) as ASCII characters, on the rising edge. [ref:1094]
        var chars = new List<char>();
        for (Keys k = Keys.A; k <= Keys.Z; k++) if (Key(k) && !_keyboard.IsKeyDown(k)) chars.Add((char)('A' + (k - Keys.A)));
        for (Keys k = Keys.D0; k <= Keys.D9; k++) if (Key(k) && !_keyboard.IsKeyDown(k)) chars.Add((char)('0' + (k - Keys.D0)));
        for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++) if (Key(k) && !_keyboard.IsKeyDown(k)) chars.Add((char)('0' + (k - Keys.NumPad0)));
        // Player name editor, chat and text fields: letters respecting Shift/CapsLock (lowercase by default), digits [ref:1095]
        // and US-layout punctuation (for a Polish programmer) – Shift gives the symbol (Shift+0 = ')'), CapsLock does not change it –
        // space and Backspace (8); the rest only on the rising edge. Consumers filter the characters themselves (e.g. PlayerNames.Accepts).
        var typed = new List<char>();
        bool shift = Key(Keys.LeftShift) || Key(Keys.RightShift);
        bool upper = shift != keyboard.CapsLock;
        for (Keys k = Keys.A; k <= Keys.Z; k++) if (Key(k) && !_keyboard.IsKeyDown(k)) typed.Add((char)((upper ? 'A' : 'a') + (k - Keys.A)));
        for (Keys k = Keys.D0; k <= Keys.D9; k++) if (Key(k) && !_keyboard.IsKeyDown(k)) typed.Add(shift ? ShiftedDigits[k - Keys.D0] : (char)('0' + (k - Keys.D0)));
        for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++) if (Key(k) && !_keyboard.IsKeyDown(k)) typed.Add((char)('0' + (k - Keys.NumPad0)));
        foreach (var (k, plain, shifted) in TypedKeys)
            if (Key(k) && !_keyboard.IsKeyDown(k)) typed.Add(shift ? shifted : plain);
        var capture = KeyScan.FirstPressed(keyboard, _keyboard);
        _keyboard = keyboard; _pad = pad;
        return new DesktopInput(drive, menu, pause, chars, capture, Key(Keys.H), Key(Keys.P), split1, split2, typed,
            OrbitCameraActivation(keyboard), FixedCameraActivation(keyboard), Joysticks: _allJoysticks);
    }
}
