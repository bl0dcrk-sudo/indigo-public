using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Joystick columns on the control screens: "joystick N" + "axis M" / "button X" instead of "keyboard" + key names [ref:2754]
/// capturing a gamepad button/axis in the game loop (synthetic input state) and driving with the assigned pad.
/// PNG preview: <c>NFS_CONTROLS11_DIR</c> (usually out/gfx_check/controls11).</summary>
public class JoyControlsScreenTests
{
    static uint TextColour(int index)
    {
        uint c = NFS.Core.Constants.RenderConstants.FrontEndTextColours[index];
        return (c >> 16 & 255) * 15 >> 4 << 16 | (c >> 8 & 255) * 15 >> 4 << 8 | (c & 255) * 15 >> 4;
    }

    static readonly MenuKey[] Path =
    [
        MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter,
        MenuKey.Down, MenuKey.Down, MenuKey.Enter, MenuKey.Enter
    ];

    static FrameBuffer Render(MenuController menu)
    {
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        return fb;
    }

    static int Count(FrameBuffer fb, int x0, int y0, int x1, int y1, uint rgb)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (fb.Get(x, y) == rgb) n++;
        return n;
    }

    static void Dump(string name, FrameBuffer fb)
    {
        if (Environment.GetEnvironmentVariable("NFS_CONTROLS11_DIR") is not { } dir) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), fb.EncodePng());
    }

    static MenuController OpenSetup()
    {
        var menu = MenuController.Load();
        foreach (var key in Path) menu.HandleKey(key);
        Assert.Equal("controls-setup", menu.CurrentScreen.Id);
        return menu;
    }

    [GameDataFact]
    public void ButtonBinding_DrawsJoystickNumberThenButtonLetter_InPlaceOfTheKeyboardColumn()
    {
        var menu = OpenSetup();
        var text = TextFile.Load("ENG");
        var item = menu.CurrentScreen.Items[0];
        var e = text.Entries[item.TextId];
        int valueX = e.X + (e.FontSize < 18 ? 0xD : 0x13);
        int gap = e.FontSize == 18 ? 10 : 6, second = valueX + (e.FontSize == 18 ? 0x50 : 0x42);
        var font = new FrontEndFont().Font(e.FontSize);
        var keyboard = Render(menu);
        Dump("controls_keyboard", keyboard);

        KeyBindings.Set(menu.Config, KeyAction.SteerLeft, JoyBindings.ButtonWord(1, 2));    // "joystick 2 button C"
        var label = menu.JoyText(item)!.Value;
        Assert.Equal(new JoyLabel("joystick", "2", "button", "C"), label);
        var joy = Render(menu);
        Dump("controls_joystick_button", joy);
        uint hi = TextColour(e.HighlightColour);
        // "joystick" in the value column, the number after it, "button" at offset 0x42 and the letter after it - all in the colour of the selected item.
        Assert.True(Count(joy, valueX, e.Y, valueX + font.Measure("joystick"), e.Y + e.FontSize, hi) > 0);
        int numberX = valueX + font.Measure("joystick") + gap;
        Assert.True(Count(joy, numberX, e.Y, numberX + font.Measure("2"), e.Y + e.FontSize, hi) > 0);
        Assert.True(Count(joy, second, e.Y, second + font.Measure("button"), e.Y + e.FontSize, hi) > 0);
        int letterX = second + font.Measure("button") + gap;
        Assert.True(Count(joy, letterX, e.Y, letterX + font.Measure("C"), e.Y + e.FontSize, hi) > 0);
        // "keyboard" and "left" from the previous frame disappear: the value column draws only the joystick texts.
        Assert.NotEqual(Count(keyboard, valueX, e.Y, second + 60, e.Y + e.FontSize, hi), Count(joy, valueX, e.Y, second + 60, e.Y + e.FontSize, hi));
        // the item label did not move
        for (int y = e.Y; y < e.Y + e.FontSize; y++)
            for (int x = 0; x < valueX; x++) Assert.Equal(keyboard.Get(x, y), joy.Get(x, y));
    }

    [GameDataFact]
    public void AxisBinding_DrawsAxisNumber_AndAnEmptyEntryStaysUnderscores()
    {
        var menu = OpenSetup();
        var text = TextFile.Load("ENG");
        var item = menu.CurrentScreen.Items[2];                                         // accelerate
        var e = text.Entries[item.TextId];
        int valueX = e.X + (e.FontSize < 18 ? 0xD : 0x13);
        int gap = e.FontSize == 18 ? 10 : 6, second = valueX + (e.FontSize == 18 ? 0x50 : 0x42);
        var font = new FrontEndFont().Font(e.FontSize);
        KeyBindings.Set(menu.Config, KeyAction.Accelerate, JoyBindings.AxisWord(menu.Config.Raw, 0, 5, true, 0));   // "joystick 1 axis 5"
        Assert.Equal(new JoyLabel("joystick", "1", "axis", "5"), menu.JoyText(item)!.Value);
        var fb = Render(menu);
        Dump("controls_joystick_axis", fb);
        uint colour = TextColour(e.Colour);                                              // item 2 is not selected
        int numberX = second + font.Measure("axis") + gap;
        Assert.True(Count(fb, second, e.Y, second + font.Measure("axis"), e.Y + e.FontSize, colour) > 0);
        Assert.True(Count(fb, numberX, e.Y, numberX + font.Measure("5"), e.Y + e.FontSize, colour) > 0);
        KeyBindings.Set(menu.Config, KeyAction.Accelerate, 0);
        Assert.Null(menu.JoyText(item));                                                 // no assignment: only "________"
    }

    [GameDataFact]
    public void SplitScreen_DrawsJoystickColumnForPlayerOne_AndKeyboardForPlayerTwo()
    {
        var menu = MenuController.Load();
        menu.Config.GameType = 1;
        foreach (var target in new[] { "options", "controls", "controls-setup" })
        {
            int index = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == target).i;
            for (int guard = 0; menu.SelectedIndex != index && guard < 80; guard++) menu.HandleKey(MenuKey.Down);
            menu.HandleKey(MenuKey.Enter);
        }
        Assert.Equal(MenuController.SplitControlsScreen, menu.CurrentScreen.Id);
        var text = TextFile.Load("ENG");
        var item = menu.CurrentScreen.Items[0];
        var e = text.Entries[item.TextId];
        int valueX = e.X + (e.FontSize < 18 ? 0xD : 0x13);
        var font = new FrontEndFont().Font(e.FontSize);
        KeyBindings.SetSplit(menu.Config, 0, KeyAction.SteerLeft, JoyBindings.ButtonWord(0, 0));   // "joystick 1 button A"
        var fb = Render(menu);
        Dump("controls_split_joystick", fb);
        Assert.Equal(new JoyLabel("joystick", "1", "button", "A"), menu.JoyText(item, 0)!.Value);
        Assert.Null(menu.JoyText(item, 1));                                                          // player 2: keyboard
        Assert.Equal("S", menu.KeyText(item, 1).Name);
        Assert.True(Count(fb, valueX, e.Y, valueX + font.Measure("joystick"), e.Y + e.FontSize, TextColour(e.HighlightColour)) > 0);
        int x2 = valueX + 0x8A + (e.FontSize == 18 ? 0x50 : 0x42);
        Assert.True(Count(fb, x2, e.Y, x2 + font.Measure("S"), e.Y + e.FontSize, TextColour(e.Colour)) > 0);
    }

    static DesktopInput Frame(params MenuKey[] keys) => new(default, keys, false);
    static DesktopInput WithPad(JoyState pad, params MenuKey[] keys) => new(default, keys, false, Joysticks: [pad]);

    static void OpenControls(GameLoop loop)
    {
        var m = loop.Menu.Controller;
        foreach (var key in Path) loop.Frame(0, Frame(key));
        Assert.Equal("controls-setup", m.CurrentScreen.Id);
    }

    [GameDataFact]
    public void PadCapture_InTheLoop_WaitsForReleaseThenAssignsTheButton_WithoutNavigatingTheMenu()
    {
        var loop = new GameLoop();
        OpenControls(loop);
        var m = loop.Menu.Controller;
        var pad = new JoyState { Buttons = 10, AxisMask = 0x3F };
        Array.Fill(pad.Axes, 128);
        pad.ButtonMask = 1;                                             // the pad's A button (Enter) is pressed at the moment capture starts
        loop.Frame(0, WithPad(pad, MenuKey.Enter));
        Assert.True(m.Capturing);
        loop.Frame(0, WithPad(pad));                                    // still pressed - capture waits
        Assert.True(m.Capturing);
        pad.ButtonMask = 0;
        loop.Frame(0, WithPad(pad));
        Assert.True(m.Capturing);
        int before = m.SelectedIndex;
        pad.ButtonMask = 1u << 4;                                       // LB = button E; the menu gets the down arrow in this frame
        loop.Frame(0, WithPad(pad, MenuKey.Down));
        Assert.False(m.Capturing);
        Assert.Equal(before, m.SelectedIndex);
        Assert.Equal(JoyBindings.ButtonWord(0, 4), KeyBindings.Get(m.Config, KeyAction.SteerLeft));
        Assert.Equal(new JoyLabel("joystick", "1", "button", "E"), m.JoyText(m.CurrentScreen.Items[0])!.Value);
    }

    [GameDataFact]
    public void PadCapture_TakesAStickAxis_AndEscCancels()
    {
        var loop = new GameLoop();
        OpenControls(loop);
        var m = loop.Menu.Controller;
        int old = KeyBindings.Get(m.Config, KeyAction.SteerLeft);
        var pad = new JoyState { Buttons = 10, AxisMask = 0x3F };
        Array.Fill(pad.Axes, 128);
        loop.Frame(0, WithPad(pad, MenuKey.Enter));
        loop.Frame(0, WithPad(pad));
        loop.Frame(0, WithPad(pad, MenuKey.Escape));                    // Esc restores the previous assignment
        Assert.False(m.Capturing);
        Assert.Equal(old, KeyBindings.Get(m.Config, KeyAction.SteerLeft));
        loop.Frame(0, WithPad(pad, MenuKey.Enter));
        loop.Frame(0, WithPad(pad));                                    // axis rest position remembered
        pad.Axes[0] = 20;                                               // left stick to the left
        loop.Frame(0, WithPad(pad));
        Assert.False(m.Capturing);
        int word = KeyBindings.Get(m.Config, KeyAction.SteerLeft);
        Assert.True(JoyBindings.IsAxis(word));
        Assert.Equal(new JoyLabel("joystick", "1", "axis", "0"), m.JoyText(m.CurrentScreen.Items[0])!.Value);
    }

    [GameDataFact]
    public void NoPadConnected_CapturesNothing_AndKeyboardCaptureStillWorks()
    {
        var loop = new GameLoop();
        OpenControls(loop);
        var m = loop.Menu.Controller;
        var none = new JoyState();
        loop.Frame(0, WithPad(none, MenuKey.Enter));
        for (int i = 0; i < 3; i++) loop.Frame(0, WithPad(none));
        Assert.True(m.Capturing);
        loop.Frame(0, new DesktopInput(default, [], false, null, KeyScan.Capture(Keys.W), Joysticks: [none]));
        Assert.False(m.Capturing);
        Assert.Equal(0x11, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.SteerLeft)));
        Assert.Null(m.JoyText(m.CurrentScreen.Items[0]));
    }

    static GamePadState PadState(Buttons buttons = 0, Vector2? left = null, float leftTrigger = 0, float rightTrigger = 0) =>
        new(new GamePadThumbSticks(left ?? Vector2.Zero, Vector2.Zero), new GamePadTriggers(leftTrigger, rightTrigger),
            new GamePadButtons(buttons), new GamePadDPad());

    [Fact]
    public void PadJoystick_MapsGamePadToButtonsAndAxes()
    {
        var joy = new JoyState();
        PadJoystick.Update(joy, default);                                 // no pad = no joystick
        Assert.False(joy.Present);
        PadJoystick.Update(joy, PadState(Buttons.A | Buttons.RightStick, new Vector2(-1, 1), 0, 1));
        Assert.True(joy.Present);
        Assert.Equal(1u | 1u << 9, joy.ButtonMask);
        Assert.Equal((0, 0), (joy.Axes[0], joy.Axes[1]));                 // left = 0, up = 0 (DirectInput)
        Assert.Equal((0, 255), (joy.Axes[4], joy.Axes[5]));
        PadJoystick.Update(joy, PadState());
        Assert.Equal(128, joy.Axes[0]);
    }

    [GameDataFact]
    public void AssignedPadControls_DriveTheCar_InAdditionToTheDefaultPadButtons()
    {
        var menu = MenuController.Load(NFS.Data.GameFiles.TryFindGameDir());
        var raw = menu.Config.Raw;
        KeyBindings.Set(menu.Config, KeyAction.ShiftUp, JoyBindings.ButtonWord(0, 1));                  // B
        KeyBindings.Set(menu.Config, KeyAction.Accelerate, JoyBindings.AxisWord(raw, 0, 5, true, 0));   // right trigger
        KeyBindings.Set(menu.Config, KeyAction.SteerLeft, JoyBindings.AxisWord(raw, 0, 0, false, 128)); // left stick to the left
        var input = new InputMapping();
        input.SetJoystickBindings(menu);
        var idle = input.Read(default, PadState()).Drive;
        Assert.False(idle.GearUp); Assert.Equal(0, idle.AnalogGas); Assert.Equal(0, idle.AnalogSteer);
        var drive = input.Read(default, PadState(Buttons.B, new Vector2(-1, 0), 0, 1)).Drive;
        Assert.True(drive.GearUp);
        Assert.Equal(255, drive.AnalogGas);                              // trigger 0..1 = range 0x14..0xEC
        Assert.True(drive.AnalogSteer < -100);
        Assert.False(drive.Left);                                        // the axis gives analog steering, not a key flag
        var half = input.Read(default, PadState(0, null, 0, 0.5f)).Drive;
        Assert.InRange((int)half.AnalogGas, 110, 145);
    }
}
