namespace NFS.Core;

/// <summary>Current input of one player (following <c>car+0x370</c>): steering [ref:0522]
/// byte (signed), gas, brake, buttons. [ref:0523]</summary>
public readonly record struct PlayerInput(sbyte Steer, byte Gas, byte Brake, byte Buttons)
{
    public const byte BtnGearDown = 0x01, BtnGearUp = 0x02, BtnHorn = 0x04, BtnHandbrake = 0x08, BtnReset = 0x40;
}

/// <summary>
/// Local player driver task (prio 0x15): input → car+0x2D4 onward [ref:0524]
/// The AI pre-think calls (when CPU cars exist) and the input [ref:0525]
/// source (car+0x370: keyboard/replay/net) are outside; <see cref="Apply"/> receives the already read input.
/// </summary>
public static class PlayerDriver
{
    /// <summary>
    /// body after the input fetch: gas/brake <c>/8*8</c>, steering <c>(s8)/4*4</c> (C division, toward zero) [ref:0526]
    /// requested gear via <see cref="GearAuto"/> / <see cref="GearManual"/>, handbrake (button 8), horn (4).
    /// Returns true when button 0x40 asks for a reset (done by the caller). [ref:0527]
    /// </summary>
    /// <remarks>The gas division's high dividend word holds whatever the input function left in its upper 24 bits;
    /// it is called with that word = 0 and the value is used as unsigned here (0..248).</remarks>
    public static bool Apply(CarObj c, PlayerInput input, bool automatic, int numGears)
    {
        c.Throttle = (byte)(input.Gas / 8 * 8);
        c.Brake = (byte)(input.Brake / 8 * 8);
        c.SteerTarget = input.Steer / 4 * 4;
        c.RequestedGear = automatic ? GearAuto(c.Gear, input.Buttons) : GearManual(c.Gear, numGears, input.Buttons);
        c.Handbrake = (byte)((input.Buttons & PlayerInput.BtnHandbrake) != 0 ? 1 : 0);
        c.Horn = (byte)((input.Buttons & PlayerInput.BtnHorn) != 0 ? 1 : 0);
        return (input.Buttons & PlayerInput.BtnReset) != 0;
    }

    /// <summary>up (2): R→N, N→D(2); else down (1): N→R, ≥2→N. Gears 0 = R, 1 = N, 2.. = 1st.. [ref:0528]</summary>
    public static byte GearAuto(byte gear, byte buttons)
    {
        if ((buttons & PlayerInput.BtnGearUp) != 0)
            return gear switch { 0 => 1, 1 => 2, _ => gear };
        if ((buttons & PlayerInput.BtnGearDown) != 0)
        {
            if (gear == 1) return 0;
            if (gear > 1) return 1;
        }
        return gear;
    }

    /// <summary>up (2) +1 while &lt; n−1; else down (1) −1 while ≠ 0. [ref:0529]</summary>
    public static byte GearManual(byte gear, int numGears, byte buttons)
    {
        if ((buttons & PlayerInput.BtnGearUp) != 0)
            return gear < numGears - 1 ? (byte)(gear + 1) : gear;
        if ((buttons & PlayerInput.BtnGearDown) != 0 && gear != 0) return (byte)(gear - 1);
        return gear;
    }
}
