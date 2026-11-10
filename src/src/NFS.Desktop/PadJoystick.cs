using Microsoft.Xna.Framework.Input;
using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>The MonoGame pad as "joystick 1" of the controls screen: <see cref="JoyState"/> from a snapshot of <see cref="GamePadState"/> and
/// the joystick bindings from the menu config → driving keys. It does not poll the device, so it is testable with a fake state.
/// Buttons 0..9 = A, B, X, Y, LB, RB, Back, Start, left and right stick (letters "A".."J" on screen), axes 0..5 = left X, left Y,
/// right X, right Y, left and right trigger (0..255, centre 128; Y down gives a higher value than in DirectInput; triggers rest at 0).
/// The D-pad stays for menu navigation. A connected pad counts as a present joystick; without a pad the controls screen captures nothing.</summary>
public static class PadJoystick
{
    public static readonly Buttons[] ButtonOrder =
    [
        Buttons.A, Buttons.B, Buttons.X, Buttons.Y, Buttons.LeftShoulder, Buttons.RightShoulder,
        Buttons.Back, Buttons.Start, Buttons.LeftStick, Buttons.RightStick,
    ];

    public static void Update(JoyState state, GamePadState pad)
    {
        if (!pad.IsConnected) { state.Clear(); return; }
        state.Buttons = ButtonOrder.Length;
        state.AxisMask = (1 << JoyBindings.AxisCount) - 1;
        uint mask = 0;
        for (int i = 0; i < ButtonOrder.Length; i++)
            if (pad.IsButtonDown(ButtonOrder[i])) mask |= 1u << i;
        state.ButtonMask = mask;
        static int Stick(float v) => Math.Clamp((int)MathF.Round((v + 1f) * 127.5f), 0, 255);
        static int Trigger(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        state.Axes[0] = Stick(pad.ThumbSticks.Left.X);
        state.Axes[1] = Stick(-pad.ThumbSticks.Left.Y);
        state.Axes[2] = Stick(pad.ThumbSticks.Right.X);
        state.Axes[3] = Stick(-pad.ThumbSticks.Right.Y);
        state.Axes[4] = Trigger(pad.Triggers.Left);
        state.Axes[5] = Trigger(pad.Triggers.Right);
    }

    /// <summary>Adds the joystick bindings to the driving keys (action words in <see cref="KeyAction"/> order): buttons and axes
    /// above half range set the flags, and the throttle, brake and steering axes also set analog values (the larger of the two).
    /// The port's default pad buttons (InputMapping) stay – the binding adds to them.</summary>
    public static DriveKeys Apply(DriveKeys keys, IReadOnlyList<int> words, IReadOnlyList<JoyState> joysticks)
    {
        for (int i = 0; i < words.Count && i < KeyBindings.Count; i++)
        {
            int word = words[i];
            if (!JoyBindings.IsJoystick(word)) continue;
            float amount = JoyBindings.Amount(word, joysticks);
            if (amount <= 0) continue;
            bool on = amount > 0.5f, axis = JoyBindings.IsAxis(word);
            var action = (KeyAction)i;
            switch (action)
            {
                case KeyAction.SteerLeft or KeyAction.SteerRight:
                    bool left = action == KeyAction.SteerLeft;
                    int steer = (int)MathF.Round(amount * 127) * (left ? -1 : 1);
                    if (axis) { if (Math.Abs(steer) > Math.Abs(keys.AnalogSteer)) keys = keys with { AnalogSteer = (sbyte)steer }; }
                    else if (on) keys = left ? keys with { Left = true } : keys with { Right = true };
                    break;
                case KeyAction.Accelerate:
                    if (axis) keys = keys with { AnalogGas = Math.Max(keys.AnalogGas, (byte)Math.Round(amount * 255)) };
                    else if (on) keys = keys with { Up = true };
                    break;
                case KeyAction.Brake:
                    if (axis) keys = keys with { AnalogBrake = Math.Max(keys.AnalogBrake, (byte)Math.Round(amount * 255)) };
                    else if (on) keys = keys with { Down = true };
                    break;
                case KeyAction.LookBehind when on: keys = keys with { LookBehind = true }; break;
                case KeyAction.CameraView when on: keys = keys with { CycleCamera = true }; break;
                case KeyAction.Handbrake when on: keys = keys with { Handbrake = true }; break;
                case KeyAction.Horn when on: keys = keys with { Horn = true }; break;
                case KeyAction.ShiftUp when on: keys = keys with { GearUp = true }; break;
                case KeyAction.ShiftDown when on: keys = keys with { GearDown = true }; break;
            }
        }
        return keys;
    }
}
