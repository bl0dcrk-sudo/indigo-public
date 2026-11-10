using NFS.Data.FrontEnd;

namespace NFS.Game.FrontEnd;

/// <summary>State of one joystick/gamepad for the controls screen (the original's per-device state): [ref:1678]
/// number of buttons (0 = no device, config +0x196 + 2·joystick), button mask, axis mask (config +0x19A + 2·joystick) and axes 0..255
/// (centre ~128; the original reduces 16.16 to 0..255 via <c>(v / 256)</c>, negative = 0). Mutable object – the host uses one
/// instance per pad so that a frame does not allocate.</summary>
public sealed class JoyState
{
    public int Buttons;
    public uint ButtonMask;
    public int AxisMask;
    public readonly int[] Axes = new int[JoyBindings.AxisCount];

    public bool Present => Buttons > 0;
    public void Clear() { Buttons = 0; ButtonMask = 0; AxisMask = 0; Array.Clear(Axes); }
}

/// <summary>Labels of the joystick column: "joystick" + number, then "axis"/"button" + number/letter. [ref:1679]</summary>
public readonly record struct JoyLabel(string Joystick, string JoystickNumber, string Kind, string KindValue);

/// <summary>Joystick bindings on the controls screens (devices 1 and 2 of the binding word; 4 = keyboard, see <see cref="KeyBindings"/>).
/// Axis word (device 1): byte 0 = 1, byte 1 = end of the axis response range, byte 2 = start [ref:1680]
/// (start &lt; end = positive direction), byte 3 = joystick number in bits 4..7 and axis (0..5) [ref:1681]
/// in bits 0..3. Button word (device 2): byte 0 = 2, byte 1 = button number, byte 3 = joystick number. On screen:
/// "joystick N axis M" (N = joystick + 1, M = axis from 0) or "joystick N button X" (X = "A" + button).</summary>
public static class JoyBindings
{
    public const int DeviceAxis = 1, DeviceButton = 2, JoystickCount = 2, AxisCount = 6;
    /// <summary>Axis deflection threshold relative to the rest position during capture (±0x40 of 0..255). [ref:1682]</summary>
    public const int AxisThreshold = 0x40;
    /// <summary>Default calibration from CONFIG.DAT on the disc (u16[12] starting at +0x136 = 0, +0x14E = 0x68, +0x166 = 0x98, +0x17E = 0xFF; 6 axes per joystick).</summary>
    const int DefaultCentreLow = 0x68, DefaultCentreHigh = 0x98;

    public static bool IsAxis(int word) => (word & 0xFF) == DeviceAxis;
    public static bool IsButton(int word) => (word & 0xFF) == DeviceButton;
    public static bool IsJoystick(int word) => IsAxis(word) || IsButton(word);

    public static int ButtonWord(int joystick, int button) => joystick << 24 | (button & 0xFF) << 8 | DeviceButton;

    /// <summary>Joystick number (0-based) from an axis word (bits 28..31) or a button word (byte 3).</summary>
    public static int JoystickOf(int word) => IsAxis(word) ? (int)((uint)word >> 28) : (sbyte)(word >> 24);
    public static int AxisOf(int word) => word >> 24 & 0xF;
    public static int ButtonOf(int word) => word >> 8 & 0xFF;

    /// <summary>Axis word from capture: positive deflection with the axis resting low (&lt; 0x40) = range 0x14..0xEC [ref:1683]
    /// negative with the axis resting high (&gt; 0xC0) = 0xEC..0x14, otherwise the range from the calibration in the config (A·0x100 + B: positive
    /// A = u16 @ +0x166 + 12·joystick + 2·axis and B = u16 @ +0x17E…, negative A = u16 @ +0x14E… and B = u16 @ +0x136…).</summary>
    public static int AxisWord(byte[] config, int joystick, int axis, bool positive, int rest)
    {
        int x = joystick << 20 | axis << 16;
        int index = 12 * joystick + 2 * axis;
        ushort U16(int o) => (ushort)(config[o] | config[o + 1] << 8);
        if (positive)
        {
            int start = U16(0x166 + index), end = U16(0x17E + index);
            if (start == end) (start, end) = (DefaultCentreHigh, 0xFF);   // port: config without calibration (CONFIG.DAT 0x98 / 0xFF)
            x += rest < 0x40 ? 0x14EC : (start << 8) + end;
        }
        else
        {
            int start = U16(0x14E + index), end = U16(0x136 + index);
            if (start == end) (start, end) = (DefaultCentreLow, 0);       // port: config without calibration (CONFIG.DAT 0x68 / 0)
            x += rest > 0xC0 ? 0xEC14 : (start << 8) + end;
        }
        return x << 8 | DeviceAxis;
    }

    /// <summary>Axis deflection 0..1 according to the range from the word (start→end); 0 outside the range. Value 0..255 as from <see cref="JoyState.Axes"/>.</summary>
    public static float AxisAmount(int word, int value)
    {
        int end = word >> 8 & 0xFF, start = word >> 16 & 0xFF;
        if (start == end) return value >= end ? 1 : 0;
        float t = (value - start) / (float)(end - start);
        return Math.Clamp(t, 0f, 1f);
    }

    /// <summary>Whether the binding is active in the current state (button pressed, or axis past half of its range).</summary>
    public static bool IsActive(int word, IReadOnlyList<JoyState> joysticks) => Amount(word, joysticks) > 0.5f;

    /// <summary>0..1: 1/0 for a button, deflection for an axis; no device = 0.</summary>
    public static float Amount(int word, IReadOnlyList<JoyState> joysticks)
    {
        if (!IsJoystick(word)) return 0;
        int j = JoystickOf(word);
        if ((uint)j >= (uint)joysticks.Count || !joysticks[j].Present) return 0;
        var s = joysticks[j];
        if (IsButton(word))
        {
            int b = ButtonOf(word);
            return b < s.Buttons && b < 32 && (s.ButtonMask >> b & 1) != 0 ? 1 : 0;
        }
        int a = AxisOf(word);
        return a < AxisCount && (s.AxisMask >> a & 1) != 0 ? AxisAmount(word, s.Axes[a]) : 0;
    }

    /// <summary>Labels by device: device 1 = "joystick N" + "axis M" (TEXT 0x11E/0x120, M = byte 3 &amp; 0xF from 0) [ref:1684]
    /// device 2 = "joystick N" + "button X" (TEXT 0x11E/0x11F, X = "A" + button number, <c>sprintf("%c", n + 0x41)</c>);
    /// N = joystick number + 1 (<c>sprintf("%d")</c>). Other words → null.</summary>
    public static JoyLabel? Display(int word, TextFile text)
    {
        string T(int id) => id < text.Entries.Count ? text[id] : "";
        if (IsAxis(word)) return new JoyLabel(T(0x11E), (JoystickOf(word) + 1).ToString(), T(0x120), AxisOf(word).ToString());
        if (IsButton(word)) return new JoyLabel(T(0x11E), (JoystickOf(word) + 1).ToString(), T(0x11F), ((char)(ButtonOf(word) + 0x41 & 0xFF)).ToString());
        return null;
    }
}

/// <summary>Capture of a joystick button/axis on the controls screen. [ref:1685]
/// First all buttons must be released (loop); only then is the axis rest position remembered [ref:1686]
/// ; then on each frame: the lowest pressed button (joystick 0, then 1) or the first axis deflected [ref:1687]
/// by more than 0x40 from rest. The object is independent of the host – testable with a synthetic input state.</summary>
public sealed class JoyCapture
{
    readonly int[][] _rest = [new int[JoyBindings.AxisCount], new int[JoyBindings.AxisCount]];
    bool _armed;

    public bool Armed => _armed;

    /// <summary>One frame of capture; returns the binding word or 0 (nothing yet / waiting for the buttons to be released).</summary>
    public int Poll(IReadOnlyList<JoyState> joysticks, byte[] config)
    {
        int count = Math.Min(joysticks.Count, JoyBindings.JoystickCount);
        if (!_armed)
        {
            for (int j = 0; j < count; j++)
                if (joysticks[j].Present && joysticks[j].ButtonMask != 0) return 0;
            for (int j = 0; j < count; j++) Array.Copy(joysticks[j].Axes, _rest[j], JoyBindings.AxisCount);
            _armed = true;
            return 0;
        }
        for (int j = 0; j < count; j++)
        {
            var s = joysticks[j];
            if (!s.Present) continue;
            for (int b = 0; b < s.Buttons && b < 32; b++)
                if ((s.ButtonMask >> b & 1) != 0) return JoyBindings.ButtonWord(j, b);
            for (int a = 0; a < JoyBindings.AxisCount; a++)
            {
                if ((s.AxisMask >> a & 1) == 0) continue;
                int delta = s.Axes[a] - _rest[j][a];
                if (delta > JoyBindings.AxisThreshold) return JoyBindings.AxisWord(config, j, a, true, _rest[j][a]);
                if (delta < -JoyBindings.AxisThreshold) return JoyBindings.AxisWord(config, j, a, false, _rest[j][a]);
            }
        }
        return 0;
    }
}
