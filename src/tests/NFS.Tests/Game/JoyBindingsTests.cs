using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Joystick columns on the control screens [ref:3139] [ref:3488]
/// ): device words 1 (axis) and 2 (button), labels, capture on a synthetic input state [ref:3140]
/// displacing conflicts and saving to settings.json.</summary>
public class JoyBindingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("nfs-joy-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static FeConfig Empty() => new(new byte[FeConfig.Size]);

    static JoyState Pad(int buttons = 10, uint mask = 0, int axisMask = 0x3F, int center = 128)
    {
        var s = new JoyState { Buttons = buttons, ButtonMask = mask, AxisMask = axisMask };
        Array.Fill(s.Axes, center);
        return s;
    }

    static TextFile Text() => TextFile.Load("ENG", GameData.Dir);

    [Fact]
    public void ButtonWordLayout_IsDevice2_Button_Joystick()
    {
        int word = JoyBindings.ButtonWord(1, 2);
        Assert.Equal(0x01000202, word);
        Assert.True(JoyBindings.IsButton(word));
        Assert.Equal(1, JoyBindings.JoystickOf(word));
        Assert.Equal(2, JoyBindings.ButtonOf(word));
        Assert.True(KeyBindings.IsAssigned(word));
        Assert.False(KeyBindings.IsKey(word));
    }

    [GameFact]
    public void ButtonLabelsMatchTheOriginalFormats()
    {
        // “joystick” + %d (joystick + 1), “button” + %c (0x41 + button): TEXT 0x11E / 0x11F.
        var label = JoyBindings.Display(JoyBindings.ButtonWord(1, 2), Text())!.Value;
        Assert.Equal(new JoyLabel("joystick", "2", "button", "C"), label);
        Assert.Equal("A", JoyBindings.Display(JoyBindings.ButtonWord(0, 0), Text())!.Value.KindValue);
        Assert.Null(JoyBindings.Display(KeyBindings.Word(0x23, 'H'), Text()));
        Assert.Null(JoyBindings.Display(0, Text()));
    }

    [GameFact]
    public void AxisLabelsMatchTheOriginalFormats()
    {
        // “joystick” + %d (bits 28..31 + 1), “axis” + %d (byte 3 & 0xF, from 0): TEXT 0x11E / 0x120.
        int word = JoyBindings.AxisWord(Empty().Raw, 1, 3, positive: true, rest: 128);
        Assert.Equal(0x13, word >> 24 & 0xFF);
        Assert.Equal(new JoyLabel("joystick", "2", "axis", "3"), JoyBindings.Display(word, Text())!.Value);
    }

    [Fact]
    public void AxisWordRange_FollowsTheRestPositionLikeTheCapture()
    {
        var raw = Empty().Raw;
        // An axis resting low (< 0x40, e.g. a trigger) deflected positive: fixed 0x14EC; resting high (> 0xC0) deflected down: 0xEC14.
        Assert.Equal(((1 << 20 | 4 << 16 | 0x14EC) << 8) | 1, JoyBindings.AxisWord(raw, 1, 4, true, 0));
        Assert.Equal(((0 << 20 | 2 << 16 | 0xEC14) << 8) | 1, JoyBindings.AxisWord(raw, 0, 2, false, 255));
        // Axis centre: range from the calibration in the config (u16 A @ +0x166 + 2·axis, B @ +0x17E + 2·axis for positive movement).
        raw[0x166 + 2] = 0x98; raw[0x17E + 2] = 0xFF;
        raw[0x14E + 2] = 0x68; raw[0x136 + 2] = 0x00;
        int pos = JoyBindings.AxisWord(raw, 0, 1, true, 128), neg = JoyBindings.AxisWord(raw, 0, 1, false, 128);
        Assert.Equal(0x98FF01 | 1 << 24, pos);
        Assert.Equal(0x680001 | 1 << 24, neg);
        // The range gives a deflection of 0..1: positive 0x98→0xFF, negative 0x68→0.
        Assert.Equal(0f, JoyBindings.AxisAmount(pos, 0x98));
        Assert.Equal(1f, JoyBindings.AxisAmount(pos, 0xFF));
        Assert.Equal(0f, JoyBindings.AxisAmount(neg, 0x68));
        Assert.Equal(1f, JoyBindings.AxisAmount(neg, 0));
        Assert.True(JoyBindings.AxisAmount(pos, 0xCB) is > 0.4f and < 0.6f);
    }

    [Fact]
    public void Capture_WaitsForReleasedButtons_ThenTakesTheLowestPressedButton()
    {
        var capture = new JoyCapture();
        var pad = Pad(mask: 1u << 0);                                // Enter on A, pressed when entering capture
        var raw = Empty().Raw;
        Assert.Equal(0, capture.Poll([pad], raw));                   // loop: waits for release [ref:3141]
        Assert.False(capture.Armed);
        pad.ButtonMask = 0;
        Assert.Equal(0, capture.Poll([pad], raw));                   // spoczynek osi zapamietany
        Assert.True(capture.Armed);
        Assert.Equal(0, capture.Poll([pad], raw));
        pad.ButtonMask = 1u << 5 | 1u << 3;
        Assert.Equal(JoyBindings.ButtonWord(0, 3), capture.Poll([pad], raw));   // najnizszy bit
    }

    [Fact]
    public void Capture_IgnoresButtonsBeyondTheCountAndAbsentJoysticks()
    {
        var capture = new JoyCapture();
        var raw = Empty().Raw;
        var absent = new JoyState();
        var pad = Pad(buttons: 4);
        capture.Poll([absent, pad], raw);
        pad.ButtonMask = 1u << 7;                                    // beyond the button count
        Assert.Equal(0, capture.Poll([absent, pad], raw));
        pad.ButtonMask = 1u << 2;
        Assert.Equal(JoyBindings.ButtonWord(1, 2), capture.Poll([absent, pad], raw));   // second joystick: number 1
        Assert.Equal(0, new JoyCapture().Poll([absent], raw));
    }

    [Fact]
    public void Capture_TakesAnAxisOnlyAfterMovingMoreThan0x40FromRest()
    {
        var capture = new JoyCapture();
        var raw = Empty().Raw;
        var pad = Pad();
        capture.Poll([pad], raw);
        pad.Axes[0] = 128 + 0x40;                                    // exactly the threshold – too small
        Assert.Equal(0, capture.Poll([pad], raw));
        pad.Axes[0] = 128; pad.Axes[3] = 128 - 0x41;
        int word = capture.Poll([pad], raw);
        Assert.Equal(JoyBindings.AxisWord(raw, 0, 3, positive: false, rest: 128), word);
        Assert.True(JoyBindings.IsAxis(word));
        // an axis outside the axis mask is skipped
        var masked = Pad(axisMask: 0b000001);
        var c2 = new JoyCapture();
        c2.Poll([masked], raw);
        masked.Axes[2] = 255;
        Assert.Equal(0, c2.Poll([masked], raw));
        masked.Axes[0] = 255;
        Assert.Equal(JoyBindings.AxisWord(raw, 0, 0, true, 128), c2.Poll([masked], raw));
    }

    [Fact]
    public void Amount_ReadsButtonsAndAxesFromTheState()
    {
        var pad = Pad(mask: 1u << 1);
        var raw = Empty().Raw;
        Assert.Equal(1f, JoyBindings.Amount(JoyBindings.ButtonWord(0, 1), [pad]));
        Assert.Equal(0f, JoyBindings.Amount(JoyBindings.ButtonWord(0, 2), [pad]));
        Assert.Equal(0f, JoyBindings.Amount(JoyBindings.ButtonWord(1, 1), [pad]));        // there is no joystick 2
        pad.Axes[4] = 255;
        int trigger = JoyBindings.AxisWord(raw, 0, 4, true, 0);
        Assert.True(JoyBindings.IsActive(trigger, [pad]));
        Assert.False(JoyBindings.IsActive(trigger, [Pad(center: 0)]));
        Assert.False(JoyBindings.IsActive(trigger, [new JoyState()]));
        Assert.False(JoyBindings.IsActive(KeyBindings.Word(0x1E, 'A'), [pad]));           // a key is not a joystick
    }

    [Fact]
    public void AssigningAJoystickButton_ClearsTheSameWordElsewhere_AndKeepsDoneEnabled()
    {
        var c = Empty();
        KeyBindings.ApplyDefaults(c, KeyBindings.Builtin);
        int w = JoyBindings.ButtonWord(0, 2);
        KeyBindings.AssignWord(c, KeyAction.Horn, w);
        KeyBindings.AssignWord(c, KeyAction.Handbrake, w);                 // the same button displaces the previous action
        Assert.Equal(0, KeyBindings.Get(c, KeyAction.Horn));
        Assert.Equal(w, KeyBindings.Get(c, KeyAction.Handbrake));
        Assert.False(KeyBindings.AllAssigned(c));                           // an empty slot after displacement => DONE greyed out
        KeyBindings.AssignWord(c, KeyAction.Horn, KeyBindings.WordFor(0x23));
        Assert.True(KeyBindings.AllAssigned(c));                            // a joystick assignment counts as assigned
        // a button does not affect the keyboard or buttons with other numbers
        Assert.Equal(KeyBindings.WordFor(0x4B), KeyBindings.Get(c, KeyAction.SteerLeft));
        KeyBindings.AssignWord(c, KeyAction.Brake, JoyBindings.ButtonWord(0, 3));
        Assert.Equal(w, KeyBindings.Get(c, KeyAction.Handbrake));
    }

    [Fact]
    public void AssigningAnAxis_ClearsActionsOnTheOverlappingRangeOfTheSameAxis()
    {
        var raw = Empty().Raw;
        var c = Empty();
        int up = JoyBindings.AxisWord(raw, 0, 1, true, 0);                // 0x14..0xEC
        int down = JoyBindings.AxisWord(raw, 0, 1, false, 255);           // 0xEC..0x14 – the ranges touch
        int other = JoyBindings.AxisWord(raw, 0, 2, true, 0);
        KeyBindings.AssignWord(c, KeyAction.Accelerate, up);
        KeyBindings.AssignWord(c, KeyAction.SteerLeft, other);
        KeyBindings.AssignWord(c, KeyAction.Brake, down);                 // same axis, the ranges overlap [ref:3142]
        Assert.Equal(0, KeyBindings.Get(c, KeyAction.Accelerate));
        Assert.Equal(down, KeyBindings.Get(c, KeyAction.Brake));
        Assert.Equal(other, KeyBindings.Get(c, KeyAction.SteerLeft));     // the other axis stays
    }

    [Fact]
    public void SplitTable_AssignsJoystickWordsAcrossBothPlayers()
    {
        var c = Empty();
        KeyBindings.ApplySplitDefaults(c, KeyBindings.SplitBuiltin);
        int w = JoyBindings.ButtonWord(0, 0);
        KeyBindings.AssignSplitWord(c, 1, KeyAction.Horn, w);
        KeyBindings.AssignSplitWord(c, 0, KeyAction.Horn, w);
        Assert.Equal(0, KeyBindings.GetSplit(c, 1, KeyAction.Horn));      // displaced from player 2's column
        Assert.Equal(w, KeyBindings.GetSplit(c, 0, KeyAction.Horn));
        Assert.False(KeyBindings.AllAssignedSplit(c));
    }

    [Fact]
    public void JoystickBindings_SurviveTheSettingsFile_AndKeyboardEntriesStayTheSame()
    {
        var c = Empty();
        KeyBindings.ApplyDefaults(c, KeyBindings.Builtin);
        KeyBindings.ApplySplitDefaults(c, KeyBindings.SplitBuiltin);
        int button = JoyBindings.ButtonWord(0, 4), axis = JoyBindings.AxisWord(Empty().Raw, 0, 5, true, 0);
        KeyBindings.AssignWord(c, KeyAction.ShiftUp, button);
        KeyBindings.AssignWord(c, KeyAction.Accelerate, axis);
        KeyBindings.AssignSplitWord(c, 0, KeyAction.Horn, JoyBindings.ButtonWord(0, 9));
        var path = Path.Combine(_dir, "settings.json");
        UserSettings.From(c).Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"word\"", json);
        Assert.DoesNotContain("\"word\": 0", json);                       // keys are saved as before (no new field)
        var restored = Empty();
        UserSettings.Load(path).ApplyTo(restored);
        Assert.Equal(button, KeyBindings.Get(restored, KeyAction.ShiftUp));
        Assert.Equal(axis, KeyBindings.Get(restored, KeyAction.Accelerate));
        Assert.Equal(JoyBindings.ButtonWord(0, 9), KeyBindings.GetSplit(restored, 0, KeyAction.Horn));
        Assert.Equal(KeyBindings.WordFor(0x4B), KeyBindings.Get(restored, KeyAction.SteerLeft));
    }

    [Fact]
    public void ABrokenJoystickWord_IsIgnoredByTheSettingsFile()
    {
        var c = Empty();
        KeyBindings.ApplyDefaults(c, KeyBindings.Builtin);
        KeyBindings.ApplyEntries(c, new Dictionary<string, KeyBindingEntry?> { ["horn"] = new() { Word = 0x1234_5603 } });   // device 3 is not a joystick
        Assert.Equal(KeyBindings.WordFor(0x23), KeyBindings.Get(c, KeyAction.Horn));
    }
}
