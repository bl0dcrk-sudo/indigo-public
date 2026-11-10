using NFS.Game.FrontEnd;
using NFS.Touch;

namespace NFS.Desktop;

/// <summary>Merges the touch controls into the frame's <see cref="DesktopInput"/> exactly as the keyboard / pad do: the same
/// <see cref="DriveKeys"/> fields (gas = Up, brake/reverse = Down, analog steering as the pad stick, gears, handbrake, horn,
/// reset, camera, look behind, HUD) and the same menu keys. Nothing here touches the simulation: the result goes through
/// <see cref="KeyboardDriver"/> like any other input. Stateful only for edge detection (pause, menu keys, system Back).</summary>
public sealed class TouchInput
{
    TouchButtons _prev;
    bool _backPrev;

    /// <param name="input">Keyboard / pad input of this frame.</param>
    /// <param name="touch">Controls from <see cref="TouchTracker"/> for the layout of <paramref name="mode"/>.</param>
    /// <param name="mode">Race: pause button and system Back = pause (a second one resumes); menu: on-screen pad and Back = Esc.</param>
    /// <param name="systemBack">Android Back button, level (held), see <see cref="PlatformBack"/>.</param>
    public DesktopInput Apply(DesktopInput input, TouchControls touch, TouchMode mode, bool systemBack)
    {
        var held = touch.Held;
        var prev = _prev;
        bool Edge(TouchButtons b) => (held & b) != 0 && (prev & b) == 0;
        bool back = systemBack && !_backPrev;
        _prev = held; _backPrev = systemBack;

        if (mode == TouchMode.Race)
        {
            var drive = MergeDrive(input.Drive, touch);
            var split1 = input.Split1 is { } s1 ? MergeDrive(s1, touch) : (DriveKeys?)null;
            return input with { Drive = drive, Split1 = split1, Pause = input.Pause || Edge(TouchButtons.Pause) || back };
        }
        if (mode == TouchMode.Menu)
        {
            var menu = new List<MenuKey>(input.Menu);
            void Add(TouchButtons b, MenuKey key) { if (Edge(b)) menu.Add(key); }
            Add(TouchButtons.MenuUp, MenuKey.Up); Add(TouchButtons.MenuDown, MenuKey.Down);
            Add(TouchButtons.MenuLeft, MenuKey.Left); Add(TouchButtons.MenuRight, MenuKey.Right);
            Add(TouchButtons.MenuOk, MenuKey.Enter);
            if (Edge(TouchButtons.MenuBack) || back) menu.Add(MenuKey.Escape);
            return input with { Menu = menu, PortOptions = input.PortOptions || Edge(TouchButtons.Options) };
        }
        return input;
    }

    static DriveKeys MergeDrive(DriveKeys k, TouchControls t) => k with
    {
        Up = k.Up || t.Has(TouchButtons.Gas),
        Down = k.Down || t.Has(TouchButtons.Brake),
        GearUp = k.GearUp || t.Has(TouchButtons.GearUp),
        GearDown = k.GearDown || t.Has(TouchButtons.GearDown),
        Reset = k.Reset || t.Has(TouchButtons.Reset),
        Horn = k.Horn || t.Has(TouchButtons.Horn),
        Handbrake = k.Handbrake || t.Has(TouchButtons.Handbrake),
        CycleCamera = k.CycleCamera || t.Has(TouchButtons.Camera),
        ToggleHud = k.ToggleHud || t.Has(TouchButtons.Hud),
        LookBehind = k.LookBehind || t.Has(TouchButtons.LookBehind),
        AnalogSteer = t.Steer != 0 ? t.SteerByte : k.AnalogSteer,
    };
}

/// <summary>System Back (Android) as a flag set by the activity and read by the host loop. The activity may run on another
/// thread than the game loop, hence the volatile flag; one request = one pressed frame (consumed when read).</summary>
public static class PlatformBack
{
    static volatile bool _requested;

    public static void Request() => _requested = true;

    /// <summary>True once per request; the request is cleared.</summary>
    public static bool Consume()
    {
        bool r = _requested;
        _requested = false;
        return r;
    }
}
