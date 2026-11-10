using NFS.Game.FrontEnd;
using NFS.Game.Flow;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Key captured in the menu: PC scancode (set 1, as in the original game and KEYBOARD.TXT) and uppercase ASCII (0 = no character).</summary>
public readonly record struct KeyCapture(int Scan, int Ascii);

/// <param name="Capture">Key pressed newly in this frame (without Esc) – capturing on the controls screen.</param>
/// <param name="Chars">Characters (ASCII) pressed in this frame – menu codes (<see cref="MenuController.TypeChar"/>).</param>
/// <param name="CreditsSlow">Held "H" (0x48) – the credits screen then scrolls the list by +7 instead of −3. [ref:1068]</param>
/// <param name="CreditsFast">Held "P" (0x50) – the credits screen scrolls the list by −10.</param>
/// <param name="Split1">Split screen: player 1 keys from the split table (config +0x94); single-player driving uses <see cref="Drive"/>.</param>
/// <param name="Split2">Split screen: player 2 keys (keyboard only).</param>
/// <param name="PortOptions">Touch overlay GFX button (menu / pause): opens or closes the port options screen (<see cref="PortOptionsScreen"/>; keyboard: F7).</param>
/// <param name="Joysticks">Pad(s) as joysticks of the controls screen – capturing a button or axis (<see cref="MenuController.CaptureJoystick"/>).</param>
/// <param name="Typed">Characters for the player-name editor and modal windows (with letter case, space, punctuation; 8 = Backspace) –
/// <see cref="MenuController.NameKey"/>, <see cref="MenuController.DialogChar"/>.</param>
public readonly record struct DesktopInput(DriveKeys Drive, IReadOnlyList<MenuKey> Menu, bool Pause, IReadOnlyList<char>? Chars = null,
    KeyCapture? Capture = null, bool CreditsSlow = false, bool CreditsFast = false, DriveKeys? Split1 = null, DriveKeys? Split2 = null,
    IReadOnlyList<char>? Typed = null, bool OrbitCameraActivation = false, bool FixedCameraActivation = false, bool PortOptions = false,
    IReadOnlyList<JoyState>? Joysticks = null);

/// <summary>CPU-only desktop loop. The window and loop-review command use the same transitions.</summary>
public sealed class GameLoop
{
    readonly string? _dir;
    readonly bool _autopilot;
    readonly string? _settingsPath;
    ReplayStatusHud? _replayStatusHud;
    double _nameTicks;
    /// <summary>File loaded at startup: its unknown keys are written back to the file on save.</summary>
    UserSettings? _settings;
    public MenuScene Menu { get; }
    RaceDrive? _drive;
    /// <summary>User folder of HUDCON.DAT (next to settings.json); null without user settings (tests, autotests, headless).</summary>
    readonly string? _hudconDir;
    /// <summary>Folder of the HUDCON.DAT copy (null = persistence off).</summary>
    internal string? HudconDir => _hudconDir;
    public RaceDrive? Drive
    {
        get => _drive;
        private set
        {
            // However a race is left (restart, end, quit), its HUD choices are stored. [ref:1069]
            if (!ReferenceEquals(_drive, value)) SaveHudcon(_drive);
            _drive = value;
        }
    }

    void SaveHudcon(RaceDrive? drive)
    {
        if (_hudconDir != null && drive != null && HudConPersistence.ShouldSave(drive))
            HudConPersistence.Save(_hudconDir, drive.HudconSnapshot());
    }

    /// <summary>Host exit (window closed in a race): the same save as when the race ends.</summary>
    public void Shutdown() { SaveHudcon(_drive); _drive = null; }

    /// <summary>Folder for the new race's HUDCON.DAT copy; replays get null (no HUDCON copy is read or written). [ref:1070]</summary>
    internal string? HudconDirForNewRace()
    {
        SaveHudcon(_drive); // a restart builds the scene before the old race is dropped
        return Menu.Controller.PlaybackSource != null ? null : _hudconDir;
    }
    public bool ExitRequested { get; private set; }
    public PauseMenu PauseMenu { get; }

    /// <summary>The game window turns on the race loading screen (<see cref="LoadingScreen"/>) before the scene is loaded. Off by default –
    /// tests, autotests and headless tools get the race in the same frame as before.</summary>
    public bool LoadingScreens { get; set; }
    LoadingState? _loading;
    LoadingScreen? _loadingArt;
    byte _slideSequence;
    TitleScreen? _titleArt;
    bool _titleActive;

    /// <summary>Title screen is on screen now: any key dismisses it, the menu waits. [ref:1071]</summary>
    public bool TitleActive => _titleActive;
    /// <summary>Loading screen is on screen now (race session ready, scene not yet loaded).</summary>
    public bool Loading => _loading != null;

    /// <summary>Shows the title screen at startup (called once after the loop is created); no TITLE.QFS = no screen.</summary>
    public void ShowTitle()
    {
        try { _titleArt = new TitleScreen(_dir, Menu.Controller.Config.Language); _titleActive = true; }
        catch (Exception e) when (e is IOException or InvalidDataException) { _titleActive = false; }
    }

    /// <summary>Loading screen state: steps (car slide → track image with bar), each held for a fixed time and shown at least once.</summary>
    sealed class LoadingState
    {
        public int Car, Unlock, Variant, Track;
        public bool Slide, Orbit, Fixed, Shown;
        public double Elapsed;
        public double SlideEnd => Slide ? LoadingScreen.SlideSeconds : 0;
        public double Total => SlideEnd + LoadingScreen.TrackSeconds;
        public bool OnSlide => Slide && Elapsed < SlideEnd;
    }

    /// <summary>Why the settings file was ignored or not saved (for the host log); null = ok.
    /// GameLoop is shared with the headless CLI tools, so it does not log by itself – the host receives the message.</summary>
    public string? SettingsError { get; private set; }

    /// <param name="settingsPath">User settings file (task 1f); <c>null</c> = no loading and no saving
    /// (the default – how tests, autotests and headless tools call it). The game passes `UserSettings.DefaultPath` from the menu
    /// (`%APPDATA%\NFS2SE-port\settings.json`). When the file is missing, the constructor immediately writes the current
    /// menu configuration, filled in with fields from the installation's CONFIG.DAT (migration – from then on the player profile is the source of truth).</param>
    public GameLoop(string? gameDir = null, bool autopilot = false, string? settingsPath = null, string? hudconDir = null)
    {
        _dir = gameDir; _autopilot = autopilot; _settingsPath = settingsPath;
        _hudconDir = hudconDir ?? (settingsPath == null ? null : Path.GetDirectoryName(Path.GetFullPath(settingsPath)));
        PauseMenu = new PauseMenu(gameDir);
        Menu = new MenuScene(MenuController.Load(gameDir, language: PortLanguage.Current), gameDir);
        if (settingsPath != null)
        {
            _settings = UserSettings.Load(settingsPath);
            SettingsError = _settings.LoadError == null ? null : $"settings ignored: {_settings.LoadError}";
            _settings.ApplyTo(Menu.Controller.Config);
            // Migration on first start (task 1f): without a file we save the current menu configuration right away,
            // so that from then on the player profile is the source of truth, not the installation's values (a later change
            // of the defaults/CONFIG.DAT no longer changes the settings). On this one occasion the installation's CONFIG.DAT (if it exists
            // and has the original's size) carries over the profile fields – see UserSettings.FromConfigDat.
            // An existing file that is unreadable, corrupt or from a newer schema is left untouched – overwriting
            // would lose data that can still be recovered (the game starts on defaults + SettingsError).
            // A write error (e.g. a directory without permissions) also only goes to SettingsError; no exception leaves the constructor.
            if (!_settings.Loaded && _settings.LoadError == null)
            {
                UserSettings.FromConfigDat(gameDir)?.ApplyTo(Menu.Controller.Config);
                SaveSettings();
            }
        }
        if (settingsPath != null)
        {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
            Menu.Controller.PinLanguage();   // the profile never overrides the installed language [ref:1072]
            Menu.Controller.SavesDirectory = Path.Combine(baseDir, "saves");
            Menu.Controller.StatsDirectory = Path.Combine(baseDir, UserStats.Folder);
            Menu.Controller.ModRecordsPath = Path.Combine(baseDir, NFS.Game.FrontEnd.ModRecords.FileName);
            Menu.Controller.Cars = RaceLoader.DefaultCatalog(gameDir);
            _settings?.ApplyCars(Menu.Controller);   // mod cars of the saved selection (by key) now that the catalog is known
        }
        // The reward (unlock) goes to disk right away, not only on exit – a crash does not lose the reward.
        Menu.Controller.PersistRequested += SaveSettings;
        PauseMenu.SetVolumes(Menu.Controller.Config[0x5D], Menu.Controller.Config[0x5E]);
    }

    /// <summary>Saves the current menu configuration to the settings file (called when the game exits).</summary>
    public void SaveSettings()
    {
        if (_settingsPath is not { } path) return;
        // A file from a newer build (schema > Schema) is left untouched – overwriting would lose its fields.
        if (_settings is { SchemaVersion: > UserSettings.Schema })
        {
            SettingsError = $"settings not saved: schema {_settings.SchemaVersion} > {UserSettings.Schema}";
            return;
        }
        try
        {
            var current = UserSettings.From(Menu.Controller);
            if (_settings != null)
            {
                current.Extra = _settings.Extra;   // unknown keys survive the save
                foreach (var (name, entry) in _settings.KeyBindings ?? [])
                    current.KeyBindings!.TryAdd(name, entry);   // unknown actions too
                foreach (var (name, entry) in _settings.SplitKeyBindings ?? [])
                    current.SplitKeyBindings!.TryAdd(name, entry);
            }
            current.Save(path);
            SettingsError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SettingsError = $"settings not saved: {e.Message}";
        }
    }

    public void StartRace(RaceScene scene, bool orbitCameraActivation = false, bool fixedCameraActivation = false)
    {
        if (Menu.Controller.Status != MenuStatus.Race) Menu.Controller.AdoptRace(scene.Session);
        PauseMenu.SetVolumes(Menu.Controller.Config[0x5D], Menu.Controller.Config[0x5E]);
        Drive = new RaceDrive(scene, withAi: true,
            automatic: Menu.Controller.PlaybackSource != null ? null : _autopilot ? true : null,
            orbitCameraActivation: orbitCameraActivation,
            fixedCameraActivation: fixedCameraActivation, playbackSource: Menu.Controller.PlaybackSource)
        {
            // starts from HORIZONDISPLAY (CONFIG.DAT +0x132); [ref:1073]
            // afterwards only changed by the W key (0xE). [ref:1074]
            HorizonDisplay = Menu.Controller.Config[0x132] == 0,
        };
    }

    /// <summary>car slide (0), then the track image with the bar (2); [ref:1075]
    /// the scene is loaded in the frame after the last image shown.</summary>
    void BeginLoading(DesktopInput input)
    {
        var config = Menu.Controller.Config;
        _loadingArt = new LoadingScreen(_dir, config.Language, _slideSequence);
        int car = config.P1Car, unlock = config[0x1D];
        bool slide = LoadingScreen.HasSlide(car, unlock) && Menu.Controller.PlaybackSource == null;
        _loading = new LoadingState
        {
            Car = car, Unlock = unlock, Slide = slide, Variant = slide ? _loadingArt.NextVariant(car) : 1,
            Track = Menu.Controller.Session?.TrackNumber ?? config.Track,
            Orbit = input.OrbitCameraActivation, Fixed = input.FixedCameraActivation,
        };
        _slideSequence = _loadingArt.Sequence;
        Drive = null;
    }

    /// <summary>Keys of a one-view race: a split knockout with only player 2 left is driven with his keys (tags 0x23.. from +0x98).</summary>
    DriveKeys OneViewKeys(DesktopInput input) => Menu.Controller.SoleSplitHuman == 1 ? input.Split2 ?? input.Drive : input.Drive;

    public void Frame(double seconds, DesktopInput input)
    {
        if (ExitRequested) return;
        var controller = Menu.Controller;
        if (_titleActive)
        {
            // any key / button (0xD) ends the title; that key does not reach the menu. [ref:1076]
            if (input.Menu.Count > 0 || (input.Chars?.Count ?? 0) > 0 || (input.Typed?.Count ?? 0) > 0 || input.Capture != null) _titleActive = false;
            return;
        }
        if (_loading is { } loading)
        {
            loading.Elapsed += seconds;
            if (loading.Shown && loading.Elapsed >= loading.Total)
            {
                _loading = null;
                StartRace(new RaceScene(controller.Session!, _dir, hudconDir: HudconDirForNewRace()), loading.Orbit, loading.Fixed);
            }
            return;
        }
        if (controller.Status != MenuStatus.Race)
        {
            // menu clock: bars, title, panel slide and credits [ref:1077]
            // scrolls the list with the H (0x48, slower) and P (0x50, faster) keys. A key or character in this frame
            // pauses the animations for 0x14 ticks. [ref:1078] [ref:3387]
            bool keyPressed = input.Menu.Count > 0 || (input.Chars?.Count ?? 0) > 0 || input.Capture != null || (input.Typed?.Count ?? 0) > 0;
            Menu.Advance(seconds, input.CreditsSlow, input.CreditsFast, keyPressed);
            // A captured key (e.g. an arrow) must not navigate the menu in the same frame.
            bool captured = controller.Capturing && (input.Capture is { } cap && controller.CaptureKey(cap.Scan, cap.Ascii)
                || input.Joysticks is { } joys && controller.CaptureJoystick(joys));
            if (controller.NameEditing != null || controller.Dialog != null)
            {
                // Clock of the name editor and modal windows: returns the increment of the 128 Hz counter. [ref:1079] [ref:3388]
                _nameTicks += seconds * 128;
                int whole = (int)_nameTicks;
                _nameTicks -= whole;
                controller.AdvanceNameEditor(whole);
                controller.AdvanceDialog(whole);
                foreach (var ch in input.Typed ?? [])
                {
                    if (controller.Dialog != null) controller.DialogChar(ch);
                    else controller.NameKey(ch);
                }
            }
            foreach (var ch in captured ? [] : input.Chars ?? []) controller.TypeChar(ch);
            foreach (var key in captured ? [] : input.Menu) controller.HandleKey(key);
            if (controller.Status == MenuStatus.Race)
            {
                if (LoadingScreens) BeginLoading(input);
                else StartRace(new RaceScene(controller.Session!, _dir, hudconDir: HudconDirForNewRace()), input.OrbitCameraActivation, input.FixedCameraActivation);
            }
            else Drive = null;
            ExitRequested = controller.Status == MenuStatus.Exit;
            return;
        }
        var drive = Drive!;
        if (controller.PlaybackSource != null)
        {
            if (input.Menu.Contains(MenuKey.Escape))
            {
                controller.EndReplayPlayback(); Drive = null; return;
            }
            // Left/Right move over the strip's icons, Enter runs the selected one (pause, speed, eject, camera). [ref:1080]
            if (input.Menu.Contains(MenuKey.Left)) drive.ReplayPress(ReplayUiKey.Left);
            if (input.Menu.Contains(MenuKey.Right)) drive.ReplayPress(ReplayUiKey.Right);
            if (input.Menu.Contains(MenuKey.Enter)) drive.ReplayPress(ReplayUiKey.Select);
            if (input.Menu.Contains(MenuKey.Up))
            {
                var setup = drive.Scene.Session.Setup;
                var fresh = new RaceLoader(_dir).Load(setup);
                controller.RestartReplayPlayback(fresh);
                Drive = new RaceDrive(new RaceScene(controller.Session!, _dir, hudconDir: HudconDirForNewRace()), withAi: true,
                    playbackSource: controller.PlaybackSource)
                { HorizonDisplay = controller.Config[0x132] == 0 };
                return;
            }
            drive.Frame(seconds, input.Drive);
            if (drive.PlaybackEnded || drive.ReplayEjectRequested) // eject, same as Esc [ref:1081]
            {
                controller.EndReplayPlayback(); Drive = null;
            }
            return;
        }
        if (drive.Paused)
        {
            PauseChoice? choice = input.Pause ? PauseChoice.Resume : null;
            foreach (var key in input.Menu)
                choice ??= PauseMenu.Handle(key);
            controller.Config[0x5D] = (byte)PauseMenu.MusicVolume;
            controller.Config[0x5E] = (byte)PauseMenu.EffectsVolume;
            if (choice == null) return;
            controller.Flow.PauseMenu(choice.Value);
            switch (choice)
            {
                case PauseChoice.Resume:
                    if (drive.IsSplit) drive.Resume(input.Split1 ?? input.Drive, input.Split2 ?? default);
                    else drive.Resume(OneViewKeys(input));
                    break;
                case PauseChoice.Restart:
                    // A fresh session keeps the setup and resets all sim state. [ref:1082]
                    controller.RestartPausedRace();
                    StartRace(new RaceScene(controller.Session!, _dir, hudconDir: HudconDirForNewRace()), input.OrbitCameraActivation, input.FixedCameraActivation);
                    break;
                case PauseChoice.EndRace:
                    // ends the race: estimate unfinished times, abort entrants, show results. [ref:1083]
                    controller.CompleteRace(drive.Local.AbortRace(controller.Config.RaceType), drive.Local.BuildRecording());
                    Drive = null; break;
                case PauseChoice.QuitGame: ExitRequested = true; Drive = null; break;
            }
            return; // Never consume pause-menu time or buttons as a race step.
        }
        if (input.Pause && drive.Pause())
        {
            controller.Flow.RequestPause(drive.View.SimulationTick64);
            PauseMenu.Language = controller.Config.Language;
            PauseMenu.Open(controller.Config.RaceType);
            return;
        }
        if (_autopilot)
        {
            // Review frames use integral steps; no wall-clock dependence.
            drive.RunAutopilot(seconds);
        }
        else if (drive.IsSplit) drive.Frame(seconds, input.Split1 ?? input.Drive, input.Split2 ?? default);
        else drive.Frame(seconds, OneViewKeys(input));
        if (drive.Results is { } results) controller.CompleteRace(results, drive.Local.BuildRecording());
    }

    /// <summary>Whether <see cref="Render(FrameBuffer, FrameBuffer, RenderOptions?)"/> draws a 3D scene now (a race) or a pure 2D screen (menu, exit).</summary>
    public bool ShowsScene => !ExitRequested && !_titleActive && _loading == null && Menu.Controller.Status == MenuStatus.Race;

    public void Render(FrameBuffer fb, RenderOptions? options = null) => Render(fb, fb, options);

    /// <summary>Frame into two layers (Enhanced, see <see cref="RaceDrive.Render(FrameBuffer, FrameBuffer, RenderOptions?)"/>): the race 3D scene into
    /// <paramref name="scene"/>, all 2D (HUD, replay icons, pause menu) into <paramref name="overlay"/>. Returns whether the 3D scene was drawn
    /// (false = menu / exit: the whole picture is in <paramref name="overlay"/>, opaque). With the same buffer for both it is the original frame.</summary>
    public bool Render(FrameBuffer scene, FrameBuffer overlay, RenderOptions? options = null)
    {
        if (ExitRequested) { overlay.Clear(0, 0, 0); return false; }
        if (_titleActive) { _titleArt!.Draw(overlay); return false; }
        if (_loading is { } loading)
        {
            var art = _loadingArt!;
            int step = Math.Clamp(1 + (int)((loading.Elapsed - loading.SlideEnd) / LoadingScreen.TrackSeconds * LoadingScreen.ProgressSteps), 1, LoadingScreen.ProgressSteps);
            bool drawn = loading.OnSlide && art.DrawSlide(overlay, loading.Car, loading.Unlock, loading.Variant) || art.DrawTrack(overlay, loading.Track, step);
            if (!drawn) overlay.Clear(0, 0, 0);
            if (!loading.OnSlide) loading.Shown = true;
            return false;
        }
        if (Menu.Controller.Status != MenuStatus.Race) { Menu.Render(overlay); return false; }
        options ??= new RenderOptions();
        // HORIZONDISPLAY (CONFIG.DAT +0x132) is set by RaceDrive at start; the I_HORIZON key toggles it. [ref:1084]
        options.HorizonDisplay = Drive!.HorizonDisplay;
        Drive!.Render(scene, overlay, options);
        if (Drive.PlaybackSource != null)
        {
            if (Drive.ReplayStrip is { } strip) (_replayStatusHud ??= new ReplayStatusHud(_dir)).Draw(overlay, strip);
        }
        if (Drive.Paused && Drive.PlaybackSource == null) PauseMenu.Render(overlay);
        return true;
    }
}

/// <summary>resume/restart, two 0..127 volumes (step 3) [ref:1085]
/// end race/quit confirmations (default NO; Left/Right toggle); Esc resumes even inside confirmation.
/// Restart is greyed out and skipped by navigation unless race type is 0 (table). [ref:1086]
/// Graphics from GAMEDATA/PAUSE/Pause??.QFS; without game data a text panel is used. [ref:1087]</summary>
public sealed class PauseMenu
{
    readonly string? _dir;
    PauseArt? _art;
    public PauseMenu(string? gameDir = null) { _dir = gameDir; }
    public int Selected { get; private set; }
    public int MusicVolume { get; private set; } = 127;
    public int EffectsVolume { get; private set; } = 127;
    public bool Confirming { get; private set; }
    public bool ConfirmYes { get; private set; }
    public bool RestartEnabled { get; private set; } = true;
    /// <summary>Race type (2 = knockout, caption "QUIT KNOCKOUT?"). [ref:1088]</summary>
    public int RaceType { get; private set; }
    public int Language { get; set; }
    public void SetVolumes(int music, int effects)
    {
        MusicVolume = Math.Clamp(music, 0, 127);
        EffectsVolume = Math.Clamp(effects, 0, 127);
    }
    public void Open(int raceType = 0)
    {
        Selected = 0; Confirming = false; ConfirmYes = false;
        RaceType = raceType; RestartEnabled = raceType == 0; _art = null;
    }

    int Step(int delta)
    {
        int n = Selected;
        do n = (n + 6 + delta) % 6; while (n == 1 && !RestartEnabled);
        return n;
    }

    public PauseChoice? Handle(MenuKey key)
    {
        if (key == MenuKey.Escape) return PauseChoice.Resume;
        if (Confirming)
        {
            if (key is MenuKey.Left or MenuKey.Right) ConfirmYes = !ConfirmYes;
            if (key == MenuKey.Enter)
            {
                Confirming = false;
                if (ConfirmYes) return Selected == 4 ? PauseChoice.EndRace : PauseChoice.QuitGame;
            }
            return null;
        }
        if (key == MenuKey.Up) Selected = Step(-1);
        if (key == MenuKey.Down) Selected = Step(1);
        if (key is MenuKey.Left or MenuKey.Right)
        {
            int delta = key == MenuKey.Left ? -3 : 3;
            if (Selected == 2) MusicVolume = Math.Clamp(MusicVolume + delta, 0, 127);
            if (Selected == 3) EffectsVolume = Math.Clamp(EffectsVolume + delta, 0, 127);
        }
        if (key == MenuKey.Enter)
        {
            if (Selected == 0) return PauseChoice.Resume;
            if (Selected == 1) return PauseChoice.Restart;
            if (Selected >= 4) { Confirming = true; ConfirmYes = false; }
        }
        return null;
    }

    public void Render(FrameBuffer fb)
    {
        _art ??= PauseArt.TryLoad(_dir, Language);
        if (_art != null) { _art.Draw(fb, this); return; }
        // Fallback without game data.
        PopupBox.Panel(fb, 100, 110, 540, 365, 0x101820, 0x101820);
        HudOverlay.DrawText(fb, 130, 125, "PAUSED", 0xFFFF00);
        string[] rows = ["RESUME", "RESTART", $"MUSIC {MusicVolume} (NO CD MUSIC)",
            $"EFFECTS {EffectsVolume}", "RETURN TO MENU", "QUIT GAME"];
        for (int i = 0; i < rows.Length; i++)
            HudOverlay.DrawText(fb, 130, 167 + i * 25, (i == Selected ? "= " : "  ") + rows[i],
                i == Selected ? 0xFFFF00u : 0xFFFFFFu, 2);
        if (Confirming)
            HudOverlay.DrawText(fb, 130, 330, "CONFIRM " + (ConfirmYes ? "= YES   NO" : "YES   = NO"), 0xFFFF00, 2);
        HudOverlay.DrawText(fb, 110, 350, "ARROWS/D-PAD  ENTER/A OK  ESC/B/START RESUME", scale: 2);
    }
}
