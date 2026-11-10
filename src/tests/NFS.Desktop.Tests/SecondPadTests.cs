using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NFS.Desktop;
using NFS.Game.FrontEnd;

namespace NFS.Desktop.Tests;

/// <summary>Second pad (PlayerIndex.Two) = "joystick 2" = player 2 in split screen. No window; synthetic GamePadState.</summary>
public class SecondPadTests
{
    static GamePadState Pad(Buttons buttons = 0, Vector2? left = null, float lt = 0, float rt = 0)
        => new(new GamePadThumbSticks(left ?? Vector2.Zero, Vector2.Zero), new GamePadTriggers(lt, rt), new GamePadButtons(buttons), new GamePadDPad());

    [Fact]
    public void Pad2_DrivesPlayerTwoOnly()
    {
        var read = new InputMapping().Read(default, default, Pad(Buttons.X | Buttons.RightShoulder, new Vector2(-1, 0), 0.5f, 1));
        var p2 = read.Split2!.Value;
        Assert.Equal(255, p2.AnalogGas);
        Assert.InRange((int)p2.AnalogBrake, 120, 135);
        Assert.True(p2.AnalogSteer < -100);
        Assert.True(p2.Handbrake); Assert.True(p2.GearUp);
        Assert.Equal(default, read.Split1!.Value);
        Assert.Equal(default, read.Drive);
        Assert.Empty(read.Menu);
        Assert.False(read.Pause);
    }

    [Fact]
    public void Pad2_ButtonsDoNotNavigateMenuOrPause()
    {
        var read = new InputMapping().Read(default, default, Pad(Buttons.A | Buttons.B | Buttons.Start | Buttons.Back));
        Assert.Empty(read.Menu);
        Assert.False(read.Pause);
        Assert.True(read.Split2!.Value.CycleCamera);   // Back = player 2's camera
        Assert.False(read.Split1!.Value.CycleCamera);
    }

    [Fact]
    public void Pad1_StillDrivesPlayerOneAndMenu_NotPlayerTwo()
    {
        var read = new InputMapping().Read(default, Pad(Buttons.A | Buttons.Y, new Vector2(1, 0), 0, 1));
        Assert.Contains(MenuKey.Enter, read.Menu);
        Assert.Equal(255, read.Drive.AnalogGas);
        Assert.Equal(255, read.Split1!.Value.AnalogGas);
        Assert.True(read.Split1.Value.Reset);
        Assert.Equal(default, read.Split2!.Value);
    }

    [Fact]
    public void NoPad2_IsAsBefore()
    {
        var keys = new KeyboardState(Keys.E, Keys.F);
        var pad = Pad(Buttons.X, new Vector2(0.5f, 0), 0, 1);
        var a = new InputMapping().Read(keys, pad);
        var b = new InputMapping().Read(keys, pad, Pad());                  // connected, but at rest
        var c = new InputMapping().Read(keys, pad, default);
        Assert.Equal(a.Split2, c.Split2);
        Assert.True(a.Split2!.Value.Up && a.Split2.Value.Right);            // player 2's keyboard works
        Assert.Equal(0, a.Split2.Value.AnalogGas);
        Assert.Equal(a.Split2, b.Split2);
        Assert.Equal(a.Split1, c.Split1);
    }

    [GameDataFact]
    public void PlayerTwoJoystickWords_ReadJoystick2_AndPlayerOneWordsMayToo()
    {
        var menu = MenuController.Load(NFS.Data.GameFiles.TryFindGameDir());
        KeyBindings.SetSplit(menu.Config, 1, KeyAction.ShiftUp, JoyBindings.ButtonWord(1, 1));   // "joystick 2 button B"
        KeyBindings.SetSplit(menu.Config, 1, KeyAction.Horn, JoyBindings.ButtonWord(0, 1));      // "joystick 1 button B"
        var input = new InputMapping();
        input.SetJoystickBindings(menu);
        var onPad2 = input.Read(default, default, Pad(Buttons.B));
        Assert.True(onPad2.Split2!.Value.GearUp);
        Assert.False(onPad2.Split2.Value.Horn);
        var onPad1 = input.Read(default, Pad(Buttons.B), default);
        Assert.True(onPad1.Split2!.Value.Horn);                // the joystick number in the word selects the device, not the player
        Assert.False(onPad1.Split2.Value.GearUp);
    }

    static MenuController OpenSplitControlsOnColumn2()
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
        menu.Config[0x08] = 1;                                   // player 2's column
        menu.HandleKey(MenuKey.Up);
        while (menu.SelectedIndex != 0) menu.HandleKey(MenuKey.Up);
        return menu;
    }

    [GameDataFact]
    public void CaptureOnPlayerTwoScreen_TakesPad2_AndThenPad2DrivesPlayerTwo()
    {
        var menu = OpenSplitControlsOnColumn2();
        Assert.Equal(1, menu.SplitColumn);
        var input = new InputMapping();
        menu.HandleKey(MenuKey.Enter);
        Assert.True(menu.Capturing);
        Assert.False(menu.CaptureJoystick(input.Read(default, default, Pad()).Joysticks!));           // armed (at rest)
        Assert.True(menu.Capturing);
        Assert.True(menu.CaptureJoystick(input.Read(default, default, Pad(Buttons.B)).Joysticks!));
        Assert.False(menu.Capturing);
        var action = menu.CurrentScreen.Items[0];
        int word = KeyBindings.GetSplit(menu.Config, 1, KeyAction.SteerLeft);
        Assert.Equal(JoyBindings.ButtonWord(1, 1), word);                                              // "joystick 2 button B"
        Assert.Equal(new JoyLabel("joystick", "2", "button", "B"), menu.JoyText(action, 1)!.Value);
        input.SetJoystickBindings(menu);
        Assert.True(input.Read(default, default, Pad(Buttons.B)).Split2!.Value.Left);
        var p1 = input.Read(default, Pad(Buttons.B), default);
        Assert.False(p1.Split2!.Value.Left);
        Assert.False(p1.Split1!.Value.Left);
    }

    [GameDataFact]
    public void CaptureOnPlayerTwoScreen_Pad1StillWorks_AndNoPad2BreaksNothing()
    {
        var menu = OpenSplitControlsOnColumn2();
        var input = new InputMapping();
        menu.HandleKey(MenuKey.Enter);
        Assert.False(menu.CaptureJoystick(input.Read(default, Pad(), default).Joysticks!));
        Assert.True(menu.CaptureJoystick(input.Read(default, Pad(Buttons.X), default).Joysticks!));
        Assert.Equal(JoyBindings.ButtonWord(0, 2), KeyBindings.GetSplit(menu.Config, 1, KeyAction.SteerLeft));   // joystick 1 button C
        menu.HandleKey(MenuKey.Enter);
        for (int i = 0; i < 3; i++) Assert.False(menu.CaptureJoystick(input.Read(default, default, default).Joysticks!));
        Assert.True(menu.Capturing);                                                                   // no pads: nothing was assigned
    }

    [Fact]
    public void Overlay_DisconnectedPad_ChangesNothing()
    {
        var keys = new DriveKeys(true, false, false, true, false, false, false);
        Assert.Equal(keys, SplitPadMap.Overlay(keys, default));
    }
}
