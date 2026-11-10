namespace NFS.Desktop;

/// <summary>Command line of NFS.Desktop (no MonoGame types: testable without a window).</summary>
internal sealed record DesktopOptions
{
    public int Track { get; init; } = 0;
    public string? GameDir { get; init; }
    /// <summary><c>--mods DIR</c>: mods directory instead of <c>mods/</c> next to the exe (or the repo one).</summary>
    public string? ModsDir { get; init; }
    /// <summary>--autotest &lt;out_dir&gt;: render a few frames, save back-buffer PNG, exit 0.</summary>
    public string? AutotestDir { get; init; }
    public int AutotestFrames { get; init; } = 5;
    public int StartSlice { get; init; } = 0;
    public float Speed { get; init; } = 30f;     // slices per second
    public int Scale { get; init; } = 1;
    /// <summary><c>--window WxH</c>: start in a window of that size (implies <c>--windowed</c>, 320..7680 × 240..4320) instead of 640×480 × <see cref="Scale"/>;
    /// e.g. a 16:9 window for the panorama in an autotest. Alt+Enter back from full screen returns to the scaled window.</summary>
    public (int Width, int Height)? Window { get; init; }
    /// <summary>--race config|replay|PREFS.TXT: race grid view (RaceLoader) with chase camera behind slot 0.</summary>
    public string? Race { get; init; }
    public string? Server { get; init; }
    public string? Name { get; init; }
    public bool Remote => Server != null;
    /// <summary>--drive-autopilot (with --race + --autotest): drive <see cref="Seconds"/> of sim time with the test autopilot.</summary>
    public bool DriveAutopilot { get; init; }
    public double Seconds { get; init; } = 10;
    /// <summary><c>--perf</c>: log mean / p95 / max of render, texture upload and present times (<see cref="FrameTimings"/>) every 600 frames and at the end of an autotest.</summary>
    public bool Perf { get; init; }
    /// <summary><c>--gfx-notice</c> (with --autotest): show the F8 message ("GFX = …") from the first frame, so its look on the 2D layer can be checked in the screenshot.</summary>
    public bool GfxNotice { get; init; }
    /// <summary><c>--cam 0..3</c> (with --race): camera view after the start animation instead of HUDCON.DAT +0x1C (0 = cockpit); e.g. the cockpit in an autotest.</summary>
    public int? Cam { get; init; }
    public bool Help { get; init; }
    public bool Menu { get; init; } = true;
    /// <summary><c>--fullscreen</c> / <c>--windowed</c>; null = the remembered mode from <see cref="PortSettings"/>
    /// (full screen when nothing is saved – Steam Deck). Alt+Enter switches at run time.</summary>
    public bool? FullScreen { get; init; }
    /// <summary><c>--touch</c>: on-screen touch controls (always on in the Android build); on the desktop the mouse acts as one finger.</summary>
    public bool Touch { get; init; }
    /// <summary><c>--pick-data</c>: only the game data folder picker ("Using data dir" screen of the Android build); the mouse is one finger.</summary>
    public bool PickData { get; init; }
    /// <summary><c>--gfx enhanced|vanilla</c>: graphics mode of this run; null = the one remembered in [ref:3383]
    /// <see cref="PortSettings"/>, else <c>GraphicsModes.Default</c> (Enhanced). A comparison with the original (autotest, screenshots)
    /// must pass <c>--gfx vanilla</c> explicitly.</summary>
    public NFS.Render.GraphicsMode? Gfx { get; init; }
    /// <summary><c>--gfx-scale auto|1..4</c>: internal 3D scale k of Enhanced (0 = auto from the window size); null = the remembered one.</summary>
    public int? GfxScale { get; init; }
    /// <summary><c>--gfx-persp on|off</c>, <c>--gfx-filter nearest|bilinear|mip</c>, <c>--gfx-fog on|off</c>, <c>--gfx-ssaa auto|1..4</c>: Enhanced image quality
    /// items of this run (null = the remembered ones, else on / mip / on / auto).</summary>
    public bool? GfxPerspective { get; init; }
    public NFS.Render.TextureFilter? GfxFilter { get; init; }
    public bool? GfxFog { get; init; }
    public int? GfxSsaa { get; init; }
    /// <summary><c>--gfx-light on|off</c>, <c>--gfx-shadow on|off</c>, <c>--gfx-range normal|extended</c>, <c>--gfx-display sharp|fsr|crt</c>: Enhanced stage 3 items of this run
    /// (null = the remembered ones, else on / on / normal / sharp).</summary>
    public bool? GfxLight { get; init; }
    public bool? GfxShadow { get; init; }
    /// <summary><c>--gfx-trackshade on|off</c>: darkening of the track polygons from the TRK shade bits (null = the remembered one, else on).</summary>
    public bool? GfxTrackShade { get; init; }
    public bool? GfxRange { get; init; }
    public NFS.Render.DisplayFilterKind? GfxDisplay { get; init; }
    /// <summary><c>--gfx-wide on|off</c>: Hor+ panorama of the 3D scene (wider than 4:3 when the window is), off by default; null = the remembered one.</summary>
    public bool? GfxWide { get; init; }
    /// <summary><c>--gfx-smooth2d on|off</c>: menus / HUD magnified by MMPX in Enhanced (task 08j), on by default; null = the remembered one.</summary>
    public bool? GfxSmooth2D { get; init; }
    /// <summary>Whether this run reads/writes <c>port.json</c>: not in the autotest (deterministic, windowed).</summary>
    public bool UsesPortSettings => AutotestDir == null;

    /// <summary>Whether the run reads and writes the user's settings (`%APPDATA%\NFS2SE-port\settings.json`).
    /// Only the game started from the menu (no arguments / `--menu`): `--track`, `--race` and `--autotest` must be repeatable and
    /// must not touch the file in the profile (the headless tools call GameLoop the same way, without a settings path).</summary>
    public bool UsesUserSettings => Menu && AutotestDir == null;

    /// <summary>HUDCON.DAT copy in the profile folder: every windowed race, also <c>--race</c> and <c>--server</c>; [ref:1065]
    /// <c>--autotest</c> stays repeatable and does not touch the profile.</summary>
    public bool UsesHudcon => AutotestDir == null;

    public const string Usage =
        "NFS.Desktop [--menu] [--track N] [--game-dir DIR] [--mods DIR] [--slice S] [--speed SLICES_PER_S] [--scale K]\n" +
        "            [--windowed | --fullscreen | --window WxH] [--touch] [--pick-data]  (default: last used mode, first run full screen; Alt+Enter toggles)\n" +
        "            [--race config|replay|PREFS.TXT] [--server HOST[:PORT] --name NAME]\n" +
        "            [--autotest OUT_DIR] [--frames N]\n" +
        "            [--drive-autopilot] [--seconds N] [--perf] [--gfx-notice] [--cam 0..3]\n" +
        "keys: Up/Down speed, Left/Right previous/next track, Space pause, Esc quit\n" +
        "race: Up gas, Down brake, Left/Right steer, A/Z gear up/down, H horn, R reset, Space handbrake, Esc pause\n" +
        "pad: RT gas, LT brake, left stick steer, RB/LB gears, Y reset, X handbrake, LS horn, Start pause\n" +
        "menu/pause pad: D-pad select/change, A accept, B back/resume; pause Esc/Start resume\n" +
        "default/menu: arrows select/change, Enter accept, Esc cancel/back; results Enter/Esc menu";

    static (int, int)? ParseWindow(string s)
    {
        var p = s.Split('x', 'X');
        return p.Length == 2 && int.TryParse(p[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int w)
            && int.TryParse(p[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int h)
            && w is >= 320 and <= 7680 && h is >= 240 and <= 4320 ? (w, h) : null;
    }

    public static DesktopOptions Parse(IReadOnlyList<string> args)
    {
        var o = new DesktopOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{a}: missing value");
            int NextInt() => int.TryParse(Next(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : throw new ArgumentException($"{a}: not an integer");
            o = a switch
            {
                "--track" or "-t" => o with { Track = NextInt(), Menu = false },
                "--menu" => o with { Menu = true, Race = null },
                "--game-dir" => o with { GameDir = Next() },
                "--mods" => o with { ModsDir = Next() },
                "--race" => o with { Race = Next(), Menu = false },
                "--server" => o with { Server = Next(), Menu = false },
                "--name" => o with { Name = Next() },
                "--autotest" => o with { AutotestDir = Next() },
                "--frames" => o with { AutotestFrames = Math.Max(1, NextInt()) },
                "--slice" => o with { StartSlice = NextInt() },
                "--speed" => o with { Speed = float.TryParse(Next(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : throw new ArgumentException($"{a}: not a number") },
                "--scale" => o with { Scale = Math.Clamp(NextInt(), 1, 4) },
                "--gfx" => o with { Gfx = NFS.Render.GraphicsModes.Parse(Next()) ?? throw new ArgumentException($"{a}: expected enhanced or vanilla") },
                "--gfx-scale" => o with { GfxScale = GraphicsSettings.ParseScale(Next()) ?? throw new ArgumentException($"{a}: expected auto or 1..4") },
                "--gfx-persp" => o with { GfxPerspective = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-filter" => o with { GfxFilter = GraphicsSettings.ParseFilter(Next()) ?? throw new ArgumentException($"{a}: expected nearest, bilinear or mip") },
                "--gfx-fog" => o with { GfxFog = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-ssaa" => o with { GfxSsaa = GraphicsSettings.ParseScale(Next()) ?? throw new ArgumentException($"{a}: expected auto or 1..4") },
                "--gfx-light" => o with { GfxLight = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-shadow" => o with { GfxShadow = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-trackshade" => o with { GfxTrackShade = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-range" => o with { GfxRange = GraphicsSettings.ParseRange(Next()) ?? throw new ArgumentException($"{a}: expected normal or extended") },
                "--gfx-wide" => o with { GfxWide = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-smooth2d" => o with { GfxSmooth2D = GraphicsSettings.ParseSwitch(Next()) ?? throw new ArgumentException($"{a}: expected on or off") },
                "--gfx-display" => o with { GfxDisplay = NFS.Render.DisplayFilterKinds.Parse(Next()) ?? throw new ArgumentException($"{a}: expected sharp, fsr or crt") },
                "--windowed" => o with { FullScreen = false },
                "--window" => o with { Window = ParseWindow(Next()) ?? throw new ArgumentException($"{a}: expected WxH, 320..7680 x 240..4320"), FullScreen = false },
                "--fullscreen" => o with { FullScreen = true },
                "--touch" => o with { Touch = true },
                "--pick-data" => o with { PickData = true },
                "--drive-autopilot" => o with { DriveAutopilot = true },
                "--perf" => o with { Perf = true },
                "--gfx-notice" => o with { GfxNotice = true },
                "--cam" => o with { Cam = NextInt() is var c and >= 0 and <= 3 ? c : throw new ArgumentException($"{a}: expected 0..3") },
                "--seconds" => o with { Seconds = double.TryParse(Next(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double sec) && sec >= 0 ? sec : throw new ArgumentException($"{a}: not a number") },
                "--help" or "-h" or "/?" => o with { Help = true },
                _ => throw new ArgumentException($"unknown argument: {a}"),
            };
        }
        if (o.Track < 0 || o.Track > 99) throw new ArgumentException("--track: 0..99");
        if (o.Remote && string.IsNullOrWhiteSpace(o.Name)) throw new ArgumentException("--server requires --name NAME.");
        if (o.Remote && (o.Race != null || o.AutotestDir != null || o.DriveAutopilot))
            throw new ArgumentException("--server cannot be combined with --race, --autotest, or --drive-autopilot.");
        return o;
    }
}
