using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>One row of the port options screen.</summary>
internal enum PortOption { Graphics, Scale, Perspective, Filter, PixelFog, Ssaa, CarLight, Shadow, TrackShade, Range, Display, Wide, Smooth2D, BatterySaver, Rumble, Language, Back }

/// <summary>What a key did on the port options screen: <see cref="Changed"/> = <see cref="PortOptionsScreen.Settings"/> has a new value
/// (the host applies and saves it), <see cref="BatteryChanged"/> = only <see cref="PortOptionsScreen.BatterySaver"/> changed (frame cap, no graphics
/// rebuild: the host saves port.json and applies the cap, but does not call ApplyGraphics), <see cref="Close"/> = the screen should be closed.</summary>
internal readonly record struct PortOptionsResult(bool Changed, bool Close, bool BatteryChanged = false, bool RumbleChanged = false, bool LanguageChanged = false);

/// <summary>Model of the port-only options screen (the original menus have no room for them): the list of rows, the selection and the value changes,
/// producing new <see cref="GraphicsSettings"/>. No MonoGame types and no drawing (see <see cref="PortOptionsRenderer"/>), so it is testable
/// without a window. Up/Down move the selection (wrapping), Left/Right change the value of the row backwards/forwards (cyclic), Enter changes it
/// forwards (on BACK it closes), Esc closes.
/// <para>The Enhanced rows (all but GRAPHICS and BACK) stay editable in Vanilla – the values are remembered for the next switch to Enhanced –
/// but are shown dimmed (<see cref="IsActive"/> = false) because Vanilla ignores them.</para></summary>
internal sealed class PortOptionsScreen
{
    public static readonly PortOption[] Rows = Enum.GetValues<PortOption>();

    /// <param name="batterySaverAvailable">False when there is no port.json to keep the frame caps in (<c>_port == null</c>): the row then shows OFF,
    /// is dimmed and does not change.</param>
    public PortOptionsScreen(GraphicsSettings settings, bool batterySaver = false, bool batterySaverAvailable = true, bool rumble = true, bool rumbleAvailable = true, int language = 0, bool languageAvailable = true)
    {
        LanguageAvailable = languageAvailable; LanguageIndex = languageAvailable && language is >= 0 and < LanguageCount ? language : 0;
        RumbleAvailable = rumbleAvailable; Rumble = rumbleAvailable && rumble;
        Settings = settings; BatterySaverAvailable = batterySaverAvailable; BatterySaver = batterySaverAvailable && batterySaver;
    }

    public bool BatterySaverAvailable { get; }
    public bool RumbleAvailable { get; }
    public const int LanguageCount = 6;
    public bool LanguageAvailable { get; }
    /// <summary>LANGUAGE row (port.json <c>language</c>, 0..5 = ENG GER FRE SPA ITA SWE): the host saves it after <see cref="PortOptionsResult.LanguageChanged"/>;
    /// it applies from the next start (like the original, where the language comes from the installation), not to the running menu.</summary>
    public int LanguageIndex { get; set; }
    /// <summary>PAD RUMBLE row (port.json <c>rumble</c>, <see cref="PadRumble"/>); the host reads it after <see cref="PortOptionsResult.RumbleChanged"/>.</summary>
    public bool Rumble { get; set; }

    /// <summary>Whether F7 / the GFX button may open the screen: only in the game menu (not while typing a name, a dialog or capturing a key, not in the
    /// network menu) and in a race only from the pause menu (the simulation stands; a replay "pause" is not the pause menu).</summary>
    public static bool CanOpen(bool networkOpen, bool menuHost, bool raceActive, bool racePaused, bool replayPlayback, bool textEntry) =>
        menuHost && !networkOpen && !textEntry && (!raceActive || racePaused && !replayPlayback);

    /// <summary>Pad 1 Back (a new press): opens / closes the screen from the race pause menu, so a pad player can reach PAD RUMBLE without a keyboard.
    /// Back is the camera button in the race and the network chord (with Start) in the menu, so it counts only in the pause menu (the original's
    /// pause layout is untouched), not in a replay and not together with Start.</summary>
    public static bool PadToggles(bool backDown, bool backWasDown, bool startDown, bool racePaused, bool replayPlayback) =>
        backDown && !backWasDown && !startDown && racePaused && !replayPlayback;

    public GraphicsSettings Settings { get; set; }

    /// <summary>BATTERY SAVER row (frame caps, not a graphics setting): on = race 30 fps / menus 20 fps, off = the defaults 60 / 30
    /// (<see cref="FramePacing.SetBatterySaver"/>). Works in both graphics modes; the host reads it after <see cref="PortOptionsResult.Changed"/>.</summary>
    public bool BatterySaver { get; set; }
    public int Selected { get; private set; }
    public PortOption SelectedOption => Rows[Selected];

    /// <summary>False for a row the current mode ignores (Enhanced rows in Vanilla); GRAPHICS and BACK are always active, BATTERY SAVER whenever it is available.</summary>
    public bool IsActive(PortOption o) => o == PortOption.BatterySaver ? BatterySaverAvailable : o == PortOption.Rumble ? RumbleAvailable : o == PortOption.Language ? LanguageAvailable : o is PortOption.Graphics or PortOption.Back || Settings.Mode == GraphicsMode.Enhanced;

    public static string Label(PortOption o) => o switch
    {
        PortOption.Graphics => "GRAPHICS", PortOption.Scale => "SCALE", PortOption.Perspective => "PERSPECTIVE", PortOption.Filter => "FILTER",
        PortOption.PixelFog => "PIXEL FOG", PortOption.Ssaa => "SSAA", PortOption.CarLight => "CAR LIGHT", PortOption.Shadow => "SHADOW", PortOption.TrackShade => "TRACK SHADE",
        PortOption.Range => "RANGE", PortOption.Display => "DISPLAY", PortOption.Wide => "WIDE VIEW", PortOption.Smooth2D => "2D SMOOTH", PortOption.BatterySaver => "BATTERY SAVER", PortOption.Rumble => "PAD RUMBLE", PortOption.Language => "LANGUAGE", _ => "BACK",
    };

    /// <summary>Value text of a row ("" for BACK).</summary>
    public string Value(PortOption o) => o switch
    {
        PortOption.Graphics => Settings.Mode == GraphicsMode.Vanilla ? "VANILLA" : "ENHANCED",
        PortOption.Scale => Settings.Scale == 0 ? "AUTO" : Settings.Scale.ToString(),
        PortOption.Perspective => OnOff(Settings.Perspective),
        PortOption.Filter => Settings.Filter switch { TextureFilter.Nearest => "NEAREST", TextureFilter.Bilinear => "BILINEAR", _ => "MIP" },
        PortOption.PixelFog => OnOff(Settings.PixelFog),
        PortOption.Ssaa => Settings.Ssaa switch { 0 => "AUTO", 1 => "OFF", var s => s + "X" },
        PortOption.CarLight => OnOff(Settings.CarLight),
        PortOption.Shadow => OnOff(Settings.CarShadow),
        PortOption.TrackShade => OnOff(Settings.TrackShade),
        PortOption.Range => Settings.ExtendedRange ? "EXTENDED" : "NORMAL",
        PortOption.Display => Settings.Display.Name().ToUpperInvariant(),
        PortOption.Wide => OnOff(Settings.Wide),
        PortOption.Smooth2D => OnOff(Settings.Smooth2D),
        PortOption.BatterySaver => OnOff(BatterySaver),
        PortOption.Rumble => OnOff(Rumble),
        PortOption.Language => NFS.Data.FrontEnd.TextFile.CodeFor(LanguageIndex),
        _ => "",
    };

    static string OnOff(bool b) => b ? "ON" : "OFF";

    public PortOptionsResult Handle(MenuKey key)
    {
        switch (key)
        {
            case MenuKey.Up: Selected = (Selected + Rows.Length - 1) % Rows.Length; return default;
            case MenuKey.Down: Selected = (Selected + 1) % Rows.Length; return default;
            case MenuKey.Escape: return new PortOptionsResult(false, true);
            case MenuKey.Left: return Change(-1);
            case MenuKey.Right: return Change(+1);
            case MenuKey.Enter: return SelectedOption == PortOption.Back ? new PortOptionsResult(false, true) : Change(+1);
            default: return default;
        }
    }

    PortOptionsResult Change(int dir)
    {
        var g = Settings;
        switch (SelectedOption)
        {
            case PortOption.Back: return default;
            case PortOption.Graphics: g = g.Toggled(); break;
            case PortOption.Scale: g = g with { Scale = Wrap(g.Scale, dir, InternalScale.Max + 1) }; break;
            case PortOption.Perspective: g = g with { Perspective = !g.Perspective }; break;
            case PortOption.Filter: g = g with { Filter = (TextureFilter)Wrap((int)g.Filter, dir, 3) }; break;
            case PortOption.PixelFog: g = g with { PixelFog = !g.PixelFog }; break;
            case PortOption.Ssaa: g = g with { Ssaa = Wrap(g.Ssaa, dir, InternalScale.Max + 1) }; break;   // AUTO(0), OFF(1), 2..Max
            case PortOption.CarLight: g = g with { CarLight = !g.CarLight }; break;
            case PortOption.Shadow: g = g with { CarShadow = !g.CarShadow }; break;
            case PortOption.TrackShade: g = g with { TrackShade = !g.TrackShade }; break;
            case PortOption.Range: g = g with { ExtendedRange = !g.ExtendedRange }; break;
            case PortOption.Wide: g = g with { Wide = !g.Wide }; break;
            case PortOption.Smooth2D: g = g with { Smooth2D = !g.Smooth2D }; break;
            case PortOption.BatterySaver:
                if (!BatterySaverAvailable) return default;
                BatterySaver = !BatterySaver;
                return new PortOptionsResult(false, false, BatteryChanged: true);
            case PortOption.Rumble:
                if (!RumbleAvailable) return default;
                Rumble = !Rumble;
                return new PortOptionsResult(false, false, RumbleChanged: true);
            case PortOption.Language:
                if (!LanguageAvailable) return default;
                LanguageIndex = Wrap(LanguageIndex, dir, LanguageCount);
                return new PortOptionsResult(false, false, LanguageChanged: true);
            case PortOption.Display: g = g with { Display = dir > 0 ? g.Display.Next() : g.Display.Next().Next() }; break;
        }
        Settings = g;
        return new PortOptionsResult(true, false);
    }

    static int Wrap(int value, int dir, int count) => ((value + dir) % count + count) % count;
}
