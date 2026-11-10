using Microsoft.Xna.Framework.Input;

namespace NFS.Desktop;

/// <summary>Default gamepad controls in split screen (port policy, as for pad 1 in single-player driving): the pad is overlaid on the player's keys.
/// Pad 1 = "joystick 1" = player 1, pad 2 (<c>PlayerIndex.Two</c>) = "joystick 2" = player 2. The original has no default pad bindings –
/// the device is chosen by the joystick number in the binding word (<see cref="NFS.Game.FrontEnd.JoyBindings.JoystickOf"/>); the port adds
/// these default buttons [hypothesis: the original CONFIG.DAT does not bind pads to players, only the bindings made on screen]. [ref:1537]
/// Without polling the device: testable with a fake <see cref="GamePadState"/>.</summary>
public static class SplitPadMap
{
    /// <summary>Overlays the pad state onto the player's keys: buttons are OR-ed, analog axes come from the pad; a disconnected pad changes nothing.</summary>
    public static DriveKeys Overlay(DriveKeys keys, GamePadState pad)
    {
        if (!pad.IsConnected) return keys;
        bool Button(Buttons b) => pad.IsButtonDown(b);
        return keys with
        {
            GearUp = keys.GearUp || Button(Buttons.RightShoulder),
            GearDown = keys.GearDown || Button(Buttons.LeftShoulder),
            Reset = keys.Reset || Button(Buttons.Y),
            Horn = keys.Horn || Button(Buttons.LeftStick),
            Handbrake = keys.Handbrake || Button(Buttons.X),
            AnalogSteer = (sbyte)Math.Clamp((int)MathF.Round(pad.ThumbSticks.Left.X * 127), -127, 127),
            AnalogGas = (byte)Math.Clamp((int)MathF.Round(pad.Triggers.Right * 255), 0, 255),
            AnalogBrake = (byte)Math.Clamp((int)MathF.Round(pad.Triggers.Left * 255), 0, 255),
            CycleCamera = keys.CycleCamera || Button(Buttons.Back),
            LookBehind = keys.LookBehind || Button(Buttons.RightStick),
        };
    }
}
