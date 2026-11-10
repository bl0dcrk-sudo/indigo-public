using Vector3 = System.Numerics.Vector3;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Net.Sockets;
using System.Net;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Game.Flow;
using NFS.Net.Protocol;
using NFS.Render;
using NFS.Game.FrontEnd;
using NFS.Touch;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRect = Microsoft.Xna.Framework.Rectangle;

namespace NFS.Desktop;

/// <summary>
/// MonoGame host: every frame NFS.Render.TrackRenderer → FrameBuffer (RGBA 640×480) → Texture2D → SpriteBatch,
/// scaled to the window with aspect-preserving letterbox. Fly-through camera along the COL slices.
/// Graphics modes: <b>Vanilla</b> = the picture above (one 640×480 buffer, point sampling); <b>Enhanced</b> = the 3D scene in [ref:3386]
/// 640k×480k and the 2D layer (HUD, menu…) in 640×480 with an alpha mask, both drawn into an integer-scaled render target and shown with a
/// bilinear filter (sharp bilinear: nearest to a whole multiple of the window, then bilinear).
/// </summary>
internal sealed class Game1 : Microsoft.Xna.Framework.Game
{
    const int W = 640, H = 480;
    readonly DesktopOptions _opt;
    readonly GraphicsDeviceManager _gdm;
    readonly FrameTargets _targets = new();
    readonly DisplayFilterRunner _filterRunner = new();   // display filter buffers reused frame to frame (08i)
    readonly GfxNotice _notice = new();
    /// <summary>The port options screen (F7 / touch GFX) while it is open; null otherwise.</summary>
    PortOptionsScreen? _options;
    /// <summary>Pad vibration (port.json <c>rumble</c>, PORT OPTIONS "PAD RUMBLE"); the sink is the real pad.</summary>
    readonly PadRumble _rumble = new(new GamePadRumbleSink());
    bool _rumbleOn = true;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary><c>--perf</c> timings (null = off); <see cref="_perfLast"/> = timestamp of the previous Draw (frame-to-frame time).</summary>
    FrameTimings? _perf;
    long _perfLast;
    GraphicsSettings _gfx = new(GraphicsModes.Default, 0);
    readonly RenderOptions _render = new();
    SpriteBatch _sb = null!;
    Texture2D _tex = null!;            // the 2D layer (640×480; in Vanilla the whole picture)
    Texture2D? _wideTex;               // panorama: the 2D layer centred in the wide frame
    Texture2D? _sceneTex;              // Enhanced: the 3D scene (640k×480k)
    Texture2D? _smoothTex;             // Enhanced, 2D SMOOTH: the 2D layer magnified 2× by MMPX (task 08j)
    RenderTarget2D? _rt;               // Enhanced: composed picture at a whole multiple of the scene size
    Texture2D? _displayTex;            // Enhanced + CPU display filter (fsr / crt): the filtered picture
    TrackRenderer _track = null!;
    FlyThrough _fly = null!;
    MenuScene? _menu;
    RaceScene? _race;
    RaceDrive? _drive;
    NFS.Game.Race.RaceAudio? _raceAudio;
    PcmStream? _pcm;
    MenuAudio? _menuAudio;
    bool _menuAudioFailed;
    PcmStream? _menuPcm;
    KeyboardState _prevKeys;
    readonly InputMapping _input = new();
    /// <summary>On-screen controls (Android always, desktop with --touch); null = keyboard / pad only.</summary>
    TouchHost? _touch;
    /// <summary>On-screen keyboard follows the text fields (state of the previous frame); a no-op without a platform hook.</summary>
    readonly SoftKeyboard _softKeyboard = new();
    GameLoop _loop = null!;
    /// <summary>The network menu (connect, games, lobby, race, summary) while it is open; null in the game menu and local modes.</summary>
    NetworkMenuHost? _net;
    bool _networkChordHeld;
    bool _padBackHeld;
    string? _lastNick, _lastHost, _lastPort;
    int _frames;
    readonly FrameLimiter _limiter = new();

    public int ExitCode { get; private set; }

    public Game1(DesktopOptions opt)
    {
        _opt = opt;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = W * opt.Scale,
            PreferredBackBufferHeight = H * opt.Scale,
            SynchronizeWithVerticalRetrace = opt.AutotestDir == null,
        };
        Window.AllowUserResizing = true;
        Window.Title = WindowTitle;
        IsFixedTimeStep = false;
        if (opt.UsesPortSettings)
        {
            _port = PortSettings.Load(PortSettings.DefaultPath);
            if (_port.Error != null) HostLog.Info(_port.Error);
            _rumbleOn = _port.RumbleOn;
            PortLanguage.Current = _port.LanguageIndex;
        }
        _gfx = GraphicsSettings.Resolve(opt, _port);
        HostLog.Info($"graphics: {_gfx.Mode.Name()}, internal scale {(_gfx.Scale == 0 ? "auto" : _gfx.Scale.ToString())}");
    }

    readonly PortSettings? _port;

    protected override void Initialize()
    {
        _gdm.HardwareModeSwitch = false;   // borderless desktop-size full screen, no display mode change (Wine/Proton)
        var (full, w, h) = WindowMode.Initial(_opt, _port, DisplayW, DisplayH);
        ApplyWindowMode(full, w, h);
        base.Initialize();
    }

    static int DisplayW => GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width;
    static int DisplayH => GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height;

    void ApplyWindowMode(bool full, int w, int h)
    {
        _gdm.IsFullScreen = full;
        _gdm.PreferredBackBufferWidth = w;
        _gdm.PreferredBackBufferHeight = h;
        _gdm.ApplyChanges();
        IsMouseVisible = !full;
    }

    /// <summary>Remembers the window mode in port.json on every Alt+Enter (a crash does not lose it).</summary>
    void SaveWindowMode(bool full)
    {
        if (_port == null) return;
        _port.FullScreen = full;
        if (_port.Save(PortSettings.DefaultPath) is { } err) HostLog.Info(err);
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        _tex = new Texture2D(GraphicsDevice, W, H, false, SurfaceFormat.Color);
        if (_opt.Touch || OperatingSystem.IsAndroid()) _touch = new TouchHost(GraphicsDevice, mouseAsTouch: !OperatingSystem.IsAndroid());
        _loop = new GameLoop(_opt.GameDir, _opt.DriveAutopilot, _opt.UsesUserSettings ? UserSettings.DefaultPath : null,
            hudconDir: _opt.UsesHudcon ? Path.GetDirectoryName(UserSettings.DefaultPath) : null);
        _loop.Menu.AnimateChrome = true;
        _loop.Menu.Controller.Sound += s => _menuAudio?.Play(s);
        _loop.Menu.Speech += clip => _menuAudio?.PlaySpeech(clip, _loop.Menu.Controller.Config.Language);
        if (_loop.SettingsError != null) HostLog.Info(_loop.SettingsError);
        // Original start-up screens (title, race loading): only for a windowed launch from the menu; not with --race/--track/--autotest/--server. [ref:1066]
        if (_opt.Menu && _opt.AutotestDir == null && _opt.Race == null && !_opt.Remote)
        {
            _loop.LoadingScreens = true;
            _loop.ShowTitle();
        }
        var tracks = FlyThrough.AvailableTracks(_opt.GameDir);
        if (tracks.Length == 0) tracks = [_opt.Track];
        _fly = new FlyThrough(tracks, _opt.Track, 1, _opt.Speed);
        if (_opt.Remote)
        {
            // --server HOST[:PORT] --name NAME: the network menu opens with those fields and connects at once; a failure
            // leaves the Connect screen with the reason (fix the data or go back, which closes the game).
            _menu = _loop.Menu;
            var (serverHost, serverPort) = NetworkMenuHost.SplitServer(_opt.Server!);
            OpenNetwork(_opt.Name!, serverHost, serverPort.ToString(), autoConnect: true);
        }
        else if (_opt.Race != null)
        {
            var startupKeys = Keyboard.GetState();
            var scene = new RaceScene(_opt.Race, _opt.GameDir, _loop.HudconDirForNewRace());
            if (_opt.Cam is { } cam) scene.IntroReturnMode = cam;
            StartRace(scene,
                orbitCameraActivation: InputMapping.OrbitCameraActivation(startupKeys),
                fixedCameraActivation: InputMapping.FixedCameraActivation(startupKeys));
            HostLog.Info($"race {_opt.Race}: track {_race!.Session.TrackNumber}, {_race.Session.Cars.Count} cars, {_race.Cars.Count} models, " +
                         $"player slice {_drive!.StartSlice}, automatic {_drive.Runner.Automatic}");
            if (_opt.AutotestDir != null && _opt.DriveAutopilot)
            {
                _drive.RunAutopilot(_opt.Seconds); // deterministic, independent of the wall clock
                HostLog.Info(_drive.Summary());
            }
        }
        else if (_opt.Menu)
        {
            _menu = _loop.Menu;
            Window.Title = WindowTitle;
        }
        else LoadTrack(_fly.Track, _opt.StartSlice);
        if (_opt.AutotestDir != null && _opt.GfxNotice) ApplyGraphics(_gfx, GfxNotice.ForMode);   // only the message: no port.json in the autotest
        HostLog.Info($"gl: {GraphicsDevice.Adapter.Description} backbuffer {GraphicsDevice.PresentationParameters.BackBufferWidth}x{GraphicsDevice.PresentationParameters.BackBufferHeight}");
    }

    void StopRace()
    {
        _raceAudio?.Dispose(); _raceAudio = null;
        _pcm?.Dispose(); _pcm = null;
        _drive = null;
        _race = null;
    }

    void StartMenuAudio()
    {
        if (_opt.AutotestDir != null || _menuAudio != null || _menuAudioFailed) return;
        try
        {
            _menuAudio = new MenuAudio(_opt.GameDir);
            _menuPcm = new PcmStream(_menuAudio.Mixer.OutputRate);
            _menuAudio.BufferReady += _menuPcm.Enqueue;
        }
        catch (Exception e)
        {
            // No sound card or FESFX.BNK: the menu keeps running without sound, without retrying every frame.
            HostLog.Info($"menu audio unavailable: {e.Message}");
            _menuAudioFailed = true;
            _menuAudio?.Dispose(); _menuAudio = null;
            _menuPcm?.Dispose(); _menuPcm = null;
        }
    }

    void ServiceMenuAudio(double dt)
    {
        StartMenuAudio();
        if (_menuAudio is not { } a) return;
        var c = _loop.Menu.Controller;
        a.MasterVolume = Math.Min((int)c.Config[0x5E], 127);
        a.SetRoar(c.RoarLoopActive, c.Config[0x5E]);
        a.Service(dt);
        _menuPcm?.Pump();
    }

    /// <summary>Menu sounds of the network screens: the same device as the main menu, without the start roar loop; once a race
    /// drive exists the tail of the last sound (the start of the race) is drained and the device closed.</summary>
    void ServiceNetworkAudio(double dt)
    {
        if (_net!.Drive == null)
        {
            StartMenuAudio();
            if (_menuAudio is not { } a) return;
            a.MasterVolume = Math.Min((int)_loop.Menu.Controller.Config[0x5E], 127);
            a.SetRoar(false, 0);
            a.Service(dt);
            _menuPcm?.Pump();
        }
        else if (_menuAudio != null)
        {
            _menuAudio.SetRoar(false, 0);
            _menuAudio.Service(dt);
            _menuPcm?.Pump();
            if (!_menuAudio.HasActiveVoices) StopMenuAudio();
        }
    }

    void StopMenuAudio()
    {
        _menuPcm?.Dispose(); _menuPcm = null;
        _menuAudio?.Dispose(); _menuAudio = null;
    }

    void StartRace(RaceScene scene, bool prepared = false, bool orbitCameraActivation = false, bool fixedCameraActivation = false)
    {
        StopRace();
        _race = scene;
        _track = scene.Track;
        _menu = _loop.Menu;
        if (!prepared) _loop.StartRace(scene, orbitCameraActivation, fixedCameraActivation);
        _drive = _loop.Drive!;
        if (_opt.AutotestDir == null) StartRaceAudio();
        Window.Title = WindowTitle;
    }

    void StartRaceAudio()
    {
        var scene = _race!;
        if (_opt.AutotestDir == null)
        {
            _raceAudio = new NFS.Game.Race.RaceAudio(scene.Session, _drive!.Provider.Description, _opt.GameDir);
            _drive.FramePresented += _raceAudio.Present;   // stage 5a.2: audio is fed by the published frames/events
            _raceAudio.Listener = () =>
            {
                var cam = _drive.Cam;
                var right = Vector3.Normalize(Vector3.Cross(cam.Up, cam.Forward));
                static NFS.Core.Vec3 Fx(Vector3 v) => new((int)(v.X * 65536), (int)(v.Y * 65536), (int)(v.Z * 65536));
                return (Fx(cam.Position), Fx(right), Fx(cam.Forward));
            };
            if (_drive.Cam2 != null)
                _raceAudio.Listener2 = () =>
                {
                    var cam = _drive.Cam2!;
                    var right = Vector3.Normalize(Vector3.Cross(cam.Up, cam.Forward));
                    static NFS.Core.Vec3 Fx(Vector3 v) => new((int)(v.X * 65536), (int)(v.Y * 65536), (int)(v.Z * 65536));
                    return (Fx(cam.Position), Fx(right), Fx(cam.Forward));
                };
            try
            {
                _pcm = new PcmStream(_raceAudio.Mixer.OutputRate);
                _raceAudio.BufferReady += _pcm.Enqueue;
            }
            catch (Microsoft.Xna.Framework.Audio.NoAudioHardwareException e)
            {
                HostLog.Info($"audio device unavailable: {e.Message}");
                _raceAudio.Dispose(); _raceAudio = null;
            }
        }
    }

    void LoadTrack(int track, int startSlice = 0)
    {
        _track = new TrackRenderer(track, _opt.GameDir);
        _fly.SetSliceCount(_track.Slices.Length, startSlice);
        HostLog.Info($"track {track}: {_track.Slices.Length} slices");
    }

    bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);

    /// <summary>Car model selected in the menu, sent to the network server with the hello. A CONFIG.DAT value outside the
    /// selectable range (0..14) is not sent: the server keeps its frozen race model instead of refusing the connection.</summary>
    int SelectedCarType
    {
        get
        {
            int car = _loop.Menu.Controller.Config.P1Car;
            if (NetProtocol.IsCarType(car) || car >= NFS.Game.Cars.CarCatalog.VanillaCount) return car;   // a mod car is sent by key; the server refuses what it does not have
            HostLog.Info($"menu car {car} is not a selectable model (0..{NetProtocol.MaxCarType}); keeping the race's model");
            return NetProtocol.NoCarType;
        }
    }

    void OpenNetwork(string nick, string host, string port, bool autoConnect = false, bool remembered = false)
    {
        int.TryParse(port, out int portNumber);
        _net = new NetworkMenuHost(_opt.GameDir, () => _loop.Menu.Controller.Config.P1Car, nick, host,
            portNumber is >= 1 and <= 65535 ? portNumber : NetworkMenuController.DefaultPort, remembered: remembered) { HudconDir = _loop.HudconDir };
        _net.Connecting += RememberNetwork;
        _net.Menu.Sound += s => _menuAudio?.Play(s);
        if (autoConnect) _net.Menu.StartConnect();
        Window.Title = WindowTitle;
    }

    /// <summary>Remembers the Connect-screen entries in port.json (a crash later does not lose them).</summary>
    void RememberNetwork(string nick, string host, int port)
    {
        if (_port == null) return;
        _port.RememberNetwork(nick, host, port);
        if (_port.Save(PortSettings.DefaultPath) is { } err) HostLog.Info(err);
    }

    void CloseNetwork()
    {
        if (_net != null)
        {
            _lastNick = _net.Menu.Nick; _lastHost = _net.Menu.Host; _lastPort = _net.Menu.PortText;
            _net.Dispose();
            _net = null;
        }
        StopRace();
        _track = null!;
        _menu = _loop.Menu;
        Window.Title = WindowTitle;
    }

    /// <summary>Follows the host's race drive: starts/stops the race audio when the drive appears or goes away.</summary>
    void SyncNetworkRace()
    {
        if (ReferenceEquals(_drive, _net!.Drive)) return;
        StopRace();
        if (_net.Drive is { } drive)
        {
            _drive = drive;
            _race = drive.Scene;
            _track = drive.Scene.Track;
            StartRaceAudio();
            Window.Title = WindowTitle;
        }
        else
        {
            _track = null!;
            Window.Title = WindowTitle;
        }
    }

    void UpdateNetwork(GameTime gameTime, DesktopInput input)
    {
        _net!.Frame(input, gameTime.ElapsedGameTime.TotalSeconds);
        if (_net.ExitRequested)
        {
            CloseNetwork();
            if (_opt.Remote) Exit();
            return;
        }
        SyncNetworkRace();
        if (_opt.AutotestDir == null) ServiceNetworkAudio(gameTime.ElapsedGameTime.TotalSeconds);
        if (_drive != null)
        {
            _pcm?.SetPaused(_drive.RemotePaused);
            if (!_drive.RemotePaused) _pcm?.Pump();
        }
    }

    void DrawNetwork(NetworkMenuHost net)
    {
        if (net.Drive is { } drive)
        {
            var scene = _targets.BeginScene();
            var fb = _targets.Overlay;
            drive.Render(scene, fb, _render);
            if (drive.RemotePaused) NetworkMenuRenderer.DrawPause(fb, net.PauseSelection, CurrentNetSkin());
            if (net.Session?.PlayerNames.Count > 0)
                HudOverlay.DrawText(fb, 8, 466, string.Join("  ", net.Session.PlayerNames), 0xFFFFFF);
            NetworkMenuRenderer.DrawRaceOverlay(fb, net.Menu, net.Chat, net.NowMicros, CurrentNetSkin());
        }
        else NetworkMenuRenderer.Draw(_targets.BeginMenu(), net.Menu, _frames, chat: net.Chat, nowMicros: net.NowMicros, skin: CurrentNetSkin());
    }

    NetSkin? _netSkin;

    /// <summary>The look of the network screens from port.json (original unless <c>netMenuLook</c> says classic); the renderer
    /// falls back to classic by itself when the fonts or the art of the game cannot be loaded.</summary>
    NetSkin CurrentNetSkin() => _netSkin ??= new NetSkin(_loop.Menu, _port?.NetMenuLook ?? NetMenuLook.Original);

    protected override void Update(GameTime gameTime)
    {
        var k = Keyboard.GetState();
        _softKeyboard.Update(SoftKeyboard.Editing(_loop.Menu.Controller, _net));
        _input.Bindings = DriveKeyMap.From(_loop.Menu.Controller);   // drive keys from the controls screen
        _input.SplitBindings = SplitKeyMap.From(_loop.Menu.Controller);   // split screen: the +0x94 table (two players)
        _input.SetJoystickBindings(_loop.Menu.Controller);   // gamepad bindings (joystick columns)
        var pad = GamePad.GetState(PlayerIndex.One, GamePadDeadZone.IndependentAxes);
        // Android: the system Back button arrives through PlatformBack (touch pause / menu Esc); MonoGame also shows it as the
        // pad's Back, which would cycle the camera, so it is hidden from the pad mapping.
        if (OperatingSystem.IsAndroid()) pad = new GamePadState(pad.ThumbSticks, pad.Triggers,
            new GamePadButtons(WithoutBack(pad)), pad.DPad);
        var input = _input.Read(k, pad, OperatingSystem.IsAndroid() ? default : GamePad.GetState(PlayerIndex.Two, GamePadDeadZone.IndependentAxes));   // pad 2 → player 2 in split screen
        if (_touch != null) input = _touch.Apply(input, TouchHost.ModeFor(_drive != null, _drive?.Paused == true || _drive?.RemotePaused == true,
            _drive?.Results != null, _drive?.PlaybackSource != null), PlatformBack.Consume());
        input = SoftKeyboard.Merge(input);
        // Alt+Enter before the menu and the game keys: this frame's Enter must not accept a menu item.
        if (_opt.AutotestDir == null && WindowMode.IsToggle(k, _prevKeys))
        {
            var (full, w, h) = WindowMode.Toggle(_gdm.IsFullScreen, _opt.Scale, DisplayW, DisplayH);
            ApplyWindowMode(full, w, h);
            SaveWindowMode(full);
            _prevKeys = k;
            base.Update(gameTime);
            return;
        }
        if (_opt.AutotestDir == null && Pressed(k, Keys.F8))
        {
            // port-only: F8 Enhanced <-> Vanilla, Shift+F8 internal scale auto/1/2/3/4 (remembered in port.json)
            bool scaleKey = k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift);
            bool ssaaKey = k.IsKeyDown(Keys.LeftControl) || k.IsKeyDown(Keys.RightControl);   // Ctrl+F8: SSAA auto <-> off (slow CPU)
            bool displayKey = k.IsKeyDown(Keys.LeftAlt) || k.IsKeyDown(Keys.RightAlt);        // Alt+F8: display filter sharp -> fsr -> crt (CPU)
            ApplyGraphics(displayKey ? _gfx.NextDisplay() : ssaaKey ? _gfx.ToggledSsaa() : scaleKey ? _gfx.NextScale() : _gfx.Toggled(),
                displayKey ? GfxNotice.ForDisplay : ssaaKey ? GfxNotice.ForSsaa : scaleKey ? GfxNotice.ForScale : GfxNotice.ForMode);
        }
        // F7 (F9 is the race sky toggle): port options screen, from the menu or the pause menu; the touch GFX button does the same.
        bool padBack = !OperatingSystem.IsAndroid() && pad.IsButtonDown(Buttons.Back);
        bool padOptions = PortOptionsScreen.PadToggles(padBack, _padBackHeld, pad.IsButtonDown(Buttons.Start), _loop.Drive?.Paused == true && _net == null,
            _loop.Drive?.PlaybackSource != null) || _options != null && padBack && !_padBackHeld;   // Back also closes the screen
        _padBackHeld = padBack;
        if (_opt.AutotestDir == null && (Pressed(k, Keys.F7) || input.PortOptions || padOptions))
        {
            if (_options != null) _options = null;
            else if (PortOptionsScreen.CanOpen(_net != null, _menu != null, _loop.Drive != null, _loop.Drive?.Paused == true,
                         _loop.Drive?.PlaybackSource != null, _loop.Menu.Controller.NameEditing != null || _loop.Menu.Controller.Dialog != null ||
                         _loop.Menu.Controller.Capturing))
                _options = new PortOptionsScreen(_gfx, FramePacing.IsBatterySaver(_port), batterySaverAvailable: _port != null, rumble: _rumbleOn, language: _port?.LanguageIndex ?? 0, languageAvailable: _port != null);
        }
        else if (_options != null)
        {
            // The screen swallows the keys (the menu / pause menu below gets an empty input and only keeps running: animation, music).
            _options.Settings = _gfx;
            bool close = input.Pause;
            foreach (var key in input.Menu)
            {
                var r = _options.Handle(key);
                if (r.Changed) ApplyGraphics(_options.Settings, null);
                if (r.BatteryChanged && _port != null && _options.BatterySaver != FramePacing.IsBatterySaver(_port))
                {
                    FramePacing.SetBatterySaver(_port, _options.BatterySaver);   // takes effect on the next frame (TargetFps reads _port)
                    HostLog.Info($"battery saver: {(_options.BatterySaver ? "on (race 30 fps, menus 20 fps)" : "off")}");
                    if (_port.Save(PortSettings.DefaultPath) is { } err) HostLog.Info(err);
                }
                if (r.RumbleChanged && _options.Rumble != _rumbleOn)
                {
                    _rumbleOn = _options.Rumble;
                    HostLog.Info($"pad rumble: {(_rumbleOn ? "on" : "off")}");
                    if (_port != null) { _port.Rumble = _rumbleOn; if (_port.Save(PortSettings.DefaultPath) is { } err) HostLog.Info(err); }
                }
                if (r.LanguageChanged && _port != null && _options.LanguageIndex != _port.LanguageIndex)
                {
                    _port.SetLanguage(_options.LanguageIndex);   // PortLanguage.Current stays: the menu is built once, the new language applies from the next start
                    HostLog.Info($"language: {NFS.Data.FrontEnd.TextFile.CodeFor(_options.LanguageIndex)} (from the next start)");
                    if (_port.Save(PortSettings.DefaultPath) is { } err) HostLog.Info(err);
                }
                if (r.Close) { close = true; break; }
            }
            if (close) _options = null;
            input = new DesktopInput(default, [], false);
        }
        if (_opt.AutotestDir == null && _net != null)
        {
            _rumble.Stop();
            UpdateNetwork(gameTime, input);
            _prevKeys = k;
            base.Update(gameTime);
            return;
        }
        if (_menu != null)
        {
            if (_opt.AutotestDir == null)
            {
                var padState = GamePad.GetState(PlayerIndex.One);
                bool networkChord = padState.IsButtonDown(Buttons.Back) && padState.IsButtonDown(Buttons.Start);
                bool openNetwork = _loop.Drive == null && NetworkGameMenu.ShouldOpen(_loop.Menu.Controller, input,
                    Pressed(k, Keys.F10), networkChord && !_networkChordHeld);
                _networkChordHeld = networkChord;
                if (openNetwork)
                {
                    var name = _loop.Menu.Controller.Config.ProfileName(0);
                    var saved = (_port ?? new PortSettings()).NetworkDefaults(name.Length == 0 ? "PLAYER1" : name, "127.0.0.1",
                        NetworkMenuController.DefaultPort);
                    OpenNetwork(_lastNick ?? saved.Nick, _lastHost ?? saved.Host, _lastPort ?? saved.Port,
                        remembered: _lastNick != null || (_port?.HasSavedNetwork ?? false));
                    _prevKeys = k;
                    base.Update(gameTime);
                    return;
                }
                var before = _loop.Drive;
                _loop.Frame(gameTime.ElapsedGameTime.TotalSeconds, input);
                if (_loop.ExitRequested) Exit();
                if (!ReferenceEquals(before, _loop.Drive))
                {
                    if (_loop.Drive is { } next) StartRace(next.Scene, prepared: true);
                    else StopRace();
                }
                if (_drive?.Results != null)
                {
                    _raceAudio?.Dispose(); _raceAudio = null;
                    _pcm?.Dispose(); _pcm = null;
                }
                if (_raceAudio != null) _raceAudio.MasterVolume = _loop.PauseMenu.EffectsVolume;
                _pcm?.SetPaused(_drive?.Paused == true);
                if (_drive?.Paused != true) _pcm?.Pump();
                if (_loop.Drive == null)
                {
                    ServiceMenuAudio(gameTime.ElapsedGameTime.TotalSeconds);
                }
                else if (_menuAudio != null)
                {
                    // Drain the start-up roar (slot 6) after switching to the race, then close the menu device.
                    _menuAudio.SetRoar(false, 0);
                    _menuAudio.Service(gameTime.ElapsedGameTime.TotalSeconds);
                    _menuPcm?.Pump();
                    if (!_menuAudio.HasActiveVoices) StopMenuAudio();
                }
            }
        }
        else if (_opt.AutotestDir == null)
        {
            if (k.IsKeyDown(Keys.Escape)) Exit();
            if (Pressed(k, Keys.Up)) _fly.Faster();
            if (Pressed(k, Keys.Down)) _fly.Slower();
            if (Pressed(k, Keys.Space)) _fly.Paused = !_fly.Paused;
            if (_race == null && Pressed(k, Keys.Right)) LoadTrack(_fly.ChangeTrack(+1));
            if (_race == null && Pressed(k, Keys.Left)) LoadTrack(_fly.ChangeTrack(-1));
            _fly.Update(gameTime.ElapsedGameTime.TotalSeconds);
        }
        else
        {
            _fly.Update(1.0 / 30); // deterministic step in autotest
        }
        UpdateRumble(gameTime.ElapsedGameTime.TotalSeconds);
        _prevKeys = k;
        base.Update(gameTime);
    }

    /// <summary>Pad vibration: reads the live cars (read-only) of a local race and feeds <see cref="PadRumble"/>; any non-race state
    /// (menu, pause, port options, results, replay, lost focus) = motors 0. Pad 2 vibrates for player 2 of a split screen.</summary>
    void UpdateRumble(double dt)
    {
        var d = _loop?.Drive;
        bool active = _opt.AutotestDir == null && IsActive && _options == null && d is { Paused: false, Results: null, PlaybackSource: null, RemotePaused: false };
        if (!active) { _rumble.Update(dt, _rumbleOn, false, null); return; }
        long tick = d!.View.SimulationTick64;
        _rumble.Update(dt, _rumbleOn, true, RumbleSample.From(d.Car, tick), d.Car2 is { } c2 ? RumbleSample.From(c2, tick) : null);
    }

    /// <summary>Applies new graphics settings (F8 keys and the port options screen): logs, shows the notice (when given), stores them in port.json.
    /// Only the presentation changes – the simulation (<c>CarObj</c> / <c>RaceRunner</c>) is not touched.</summary>
    void ApplyGraphics(GraphicsSettings next, Func<GraphicsSettings, int, (string Title, string? Detail, uint Colour)>? notice)
    {
        _gfx = next;
        HostLog.Info($"graphics: {_gfx.Mode.Name()}, internal scale {(_gfx.Scale == 0 ? "auto" : _gfx.Scale.ToString())}");
        if (notice != null)
        {
            var pp = GraphicsDevice.PresentationParameters;
            var box = Letterbox(pp.BackBufferWidth, pp.BackBufferHeight);
            int kNow = _gfx.EffectiveScale(box.Width, box.Height);
            var (title, detail, colour) = notice(_gfx, kNow);
            _notice.Show(title, detail, _clock.Elapsed.TotalSeconds, colour);
            HostLog.Info($"{title}; {detail}");
        }
        if (_port != null)
        {
            _gfx.WriteTo(_port);
            if (_port.Save(PortSettings.DefaultPath) is { } err) HostLog.Info(err);
        }
    }

    static Buttons WithoutBack(GamePadState b)
    {
        Buttons all = 0;
        foreach (Buttons v in Enum.GetValues<Buttons>())
            if (v != Buttons.Back && b.IsButtonDown(v)) all |= v;
        return all;
    }

    Camera CurrentCamera()
    {
        var (s, n, f) = _fly.CameraSlice();
        var cam = _track.CameraAtSlice(s, width: _targets.SceneWidth, screenHeight: _targets.SceneHeight);
        var next = _track.CameraAtSlice(n, width: _targets.SceneWidth, screenHeight: _targets.SceneHeight);
        cam.Position = Vector3.Lerp(cam.Position, next.Position, f);
        return cam;
    }

    protected override void UnloadContent()
    {
        _rumble.Stop();
        // Leaving the menu (QUIT GAME, Esc, closing the window) saves the settings: %APPDATA%\NFS2SE-port\settings.json.
        if (_opt.UsesUserSettings && _menu != null)
        {
            _loop.SaveSettings();
            _loop.Shutdown();
            if (_loop.SettingsError != null) HostLog.Info(_loop.SettingsError);
        }
        else _loop.Shutdown(); // --race / --server: the HUDCON.DAT copy only, no settings [ref:1067]
        _raceAudio?.Dispose();
        _pcm?.Dispose();
        _menuAudio?.Dispose();
        _menuPcm?.Dispose();
        _net?.Dispose();
        _touch?.Dispose();
        base.UnloadContent();
    }

    /// <summary>Frame-rate cap after the present (CPU / battery); the simulation steps are driven by the frame time, not the frame count.</summary>
    protected override void EndDraw()
    {
        base.EndDraw();
        if (_opt.AutotestDir == null) _limiter.Wait(FramePacing.TargetFps(_port, racing: _drive != null || _menu == null && _net == null, IsActive));
    }

    protected override void Draw(GameTime gameTime)
    {
        var pp = GraphicsDevice.PresentationParameters;
        var box = Letterbox(pp.BackBufferWidth, pp.BackBufferHeight);
        int k = _gfx.EffectiveScale(box.Width, box.Height);
        _targets.Configure(_gfx.Mode, k, _gfx.EffectiveSsaa(k), _gfx.ViewWidth(pp.BackBufferWidth, pp.BackBufferHeight));
        _gfx.ApplyTo(_render);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderFrame();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        _targets.ExtendEdges = _targets.Wide && _drive?.CockpitDrawn == true;
        if (_options != null) PortOptionsRenderer.Draw(_targets.Overlay, _options, k);   // port options panel on the 2D layer
        _notice.Draw(_targets.Overlay, _clock.Elapsed.TotalSeconds);   // F8 message on the 2D layer
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        Present(box);
        _touch?.Draw(_sb);
        base.Draw(gameTime);
        if (_opt.Perf) PerfFrame(t0, t1, t2);

        _frames++;
        if (_opt.AutotestDir != null && _frames >= _opt.AutotestFrames)
            FinishAutotest(pp.BackBufferWidth, pp.BackBufferHeight);
    }

    /// <summary><c>--perf</c>: render = software renderer, present = composition + upload + sprite batches (GL calls may return before the GPU is done:
    /// the rest shows up in frame = Draw-to-Draw incl. Update and the buffer swap). Logged every 600 frames.</summary>
    void PerfFrame(long t0, long t1, long t2)
    {
        _perf ??= new FrameTimings();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        _perf.Add(FrameTimings.Stage.Render, System.Diagnostics.Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
        _perf.Add(FrameTimings.Stage.Present, System.Diagnostics.Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
        if (_perfLast != 0) _perf.Add(FrameTimings.Stage.Frame, System.Diagnostics.Stopwatch.GetElapsedTime(_perfLast, t0).TotalMilliseconds);
        _perfLast = t0;
        _perf.EndFrame();
        if (_perf.Frames >= 600)
        {
            HostLog.Info($"{_perf.Report(skip: 10)} (k={_targets.Scale}, {_gfx.Mode.Name()}, display {_gfx.Display})");
            _perf.Clear();
        }
    }

    /// <summary>Texture upload, timed for <c>--perf</c>.</summary>
    void Upload(Texture2D tex, byte[] rgba)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        tex.SetData(rgba);
        if (_opt.Perf) (_perf ??= new FrameTimings()).Add(FrameTimings.Stage.Upload, System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
    }

    /// <summary>Hint under the main menu (centred; must fit the 640-wide layer at <see cref="HudOverlay.Scale"/>).</summary>
    /// <summary>Main window title (same in every state).</summary>
    internal const string WindowTitle = "Need For Speed 2 Special Edition (Indigo x64)";

    internal const string NetworkHint = "NET RACE: GAME TYPE+RACE | F10 / BACK+START";

    /// <summary>Software renderer (CPU) → <see cref="_targets"/>: the 3D scene and the 2D layer (the same buffer in Vanilla).</summary>
    void RenderFrame()
    {
        if (_net != null) DrawNetwork(_net);
        else if (_menu != null)
        {
            if (_loop.ShowsScene) _loop.Render(_targets.BeginScene(), _targets.Overlay, _render);
            else
            {
                var fb = _targets.BeginMenu();
                _loop.Render(fb, fb, _render);
                if (_loop.Menu.Controller.Status == MenuStatus.Menu && _loop.Drive == null &&
                    _loop.Menu.Controller.NameEditing == null && _loop.Menu.Controller.CurrentScreen.Id == "main")
                    HudOverlay.DrawText(fb, (640 - HudOverlay.TextWidth(NetworkHint)) / 2, 460, NetworkHint, 0xFFFFFF);
            }
        }
        else _track.Render(_targets.BeginScene(), CurrentCamera(), _render);
    }

    /// <summary>Frame → window. Vanilla: one 640×480 texture, point sampling into the letterbox (the original picture). Enhanced: scene texture and
    /// alpha-masked 2D layer drawn (nearest, whole multiples) into a render target of 640k·n×480k·n, then that target is scaled bilinearly to the letterbox.</summary>
    void Present(XnaRect box)
    {
        GraphicsDevice.Clear(XnaColor.Black);
        // panorama: the frame of a race / fly-through is wider than 4:3 and fills the window (the menus stay 4:3)
        bool wide = _targets.Wide && _targets.HasScene;
        if (wide)
        {
            var pp = GraphicsDevice.PresentationParameters;
            var (wx, wy, ww, wh) = Layout.Fit(pp.BackBufferWidth, pp.BackBufferHeight, _targets.ViewWidth, H);
            box = new XnaRect(wx, wy, ww, wh);
        }
        int k = _targets.Scale;
        int n = DisplayFilter.IntegerFactor(_targets.ViewWidth * k, box.Width);
        var layer = _targets.FinishOverlay();
        // 2D SMOOTH: the layer magnified 2× by MMPX and filtered linearly straight into the letterbox (sharp display only)
        bool smooth = _targets.Mode == GraphicsMode.Enhanced && _gfx.Display == DisplayFilterKind.Sharp && _gfx.Smooth2D && FrameTargets.Smooths(layer.Width, box.Width);
        if (smooth)
        {
            var big = _targets.SmoothOverlay(out bool changed);
            if (_smoothTex == null || _smoothTex.Width != big.Width || _smoothTex.Height != big.Height)
            {
                _smoothTex?.Dispose();
                _smoothTex = new Texture2D(GraphicsDevice, big.Width, big.Height, false, SurfaceFormat.Color);
                changed = true;
            }
            if (changed) Upload(_smoothTex, big.Rgba);
        }
        else if (wide)
        {
            if (_wideTex == null || _wideTex.Width != layer.Width) { _wideTex?.Dispose(); _wideTex = new Texture2D(GraphicsDevice, layer.Width, layer.Height, false, SurfaceFormat.Color); }
            Upload(_wideTex, layer.Rgba);
        }
        else Upload(_tex, layer.Rgba);
        if (_targets.Mode == GraphicsMode.Vanilla)
        {
            _sb.Begin(samplerState: SamplerState.PointClamp);
            _sb.Draw(_tex, box, XnaColor.White);
            _sb.End();
            return;
        }
        if (_gfx.Display != DisplayFilterKind.Sharp && box.Width > 0 && box.Height > 0)
        {
            PresentFiltered(box);
            return;
        }
        bool scene = _targets.HasScene;
        if (smooth && !scene)
        {
            _sb.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.Opaque);
            _sb.Draw(_smoothTex!, box, XnaColor.White);
            _sb.End();
            return;
        }
        int rw = _targets.ViewWidth * k * n, rh = 480 * k * n;
        if (_rt == null || _rt.Width != rw || _rt.Height != rh)
        {
            _rt?.Dispose();
            _rt = new RenderTarget2D(GraphicsDevice, rw, rh, false, SurfaceFormat.Color, DepthFormat.None);
        }
        if (scene)
        {
            var shown = _targets.Scene;   // SSAA: resolved from the render buffer here
            if (_sceneTex == null || _sceneTex.Width != shown.Width || _sceneTex.Height != shown.Height)
            {
                _sceneTex?.Dispose();
                _sceneTex = new Texture2D(GraphicsDevice, shown.Width, shown.Height, false, SurfaceFormat.Color);
            }
            Upload(_sceneTex, shown.Rgba);
        }
        GraphicsDevice.SetRenderTarget(_rt);
        GraphicsDevice.Clear(XnaColor.Black);
        var whole = new XnaRect(0, 0, rw, rh);
        if (scene)
        {
            _sb.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.Opaque);
            _sb.Draw(_sceneTex!, whole, XnaColor.White);
            _sb.End();
        }
        if (!smooth)
        {
            _sb.Begin(samplerState: SamplerState.PointClamp, blendState: scene ? BlendState.NonPremultiplied : BlendState.Opaque);
            _sb.Draw(wide ? _wideTex! : _tex, whole, XnaColor.White);
            _sb.End();
        }
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(XnaColor.Black);
        _sb.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.Opaque);
        _sb.Draw(_rt, box, XnaColor.White);
        _sb.End();
        if (smooth)
        {
            // premultiplied layer over the scene: linear filtering without dark fringes
            _sb.Begin(samplerState: SamplerState.LinearClamp, blendState: BlendState.AlphaBlend);
            _sb.Draw(_smoothTex!, box, XnaColor.White);
            _sb.End();
        }
    }

    /// <summary>Enhanced with a CPU display filter (<see cref="DisplayFilters"/>: FSR-like EASU + RCAS, CRT): the composed picture is filtered to the size of the
    /// letterbox (at most about 2 megapixels, a bigger window stretches the result bilinearly) and shown 1:1. Slow on a weak CPU (Alt+F8 goes back).</summary>
    void PresentFiltered(XnaRect box)
    {
        double shrink = Math.Min(1.0, Math.Sqrt(2.1e6 / ((double)box.Width * box.Height)));
        int fw = Math.Max(2, (int)(box.Width * shrink)), fh = Math.Max(2, (int)(box.Height * shrink));
        var filtered = _filterRunner.Apply(_gfx.Display, _targets.ComposeCpuReused(), fw, fh);
        if (_displayTex == null || _displayTex.Width != fw || _displayTex.Height != fh)
        {
            _displayTex?.Dispose();
            _displayTex = new Texture2D(GraphicsDevice, fw, fh, false, SurfaceFormat.Color);
        }
        Upload(_displayTex, filtered.Rgba);
        GraphicsDevice.Clear(XnaColor.Black);
        _sb.Begin(samplerState: fw == box.Width ? SamplerState.PointClamp : SamplerState.LinearClamp, blendState: BlendState.Opaque);
        _sb.Draw(_displayTex, box, XnaColor.White);
        _sb.End();
    }

    /// <summary>Reads the composed back buffer (whole GL pipeline), writes PNG + the raw FrameBuffer for comparison, exits 0.</summary>
    void FinishAutotest(int bw, int bh)
    {
        try
        {
            Directory.CreateDirectory(_opt.AutotestDir!);
            var px = new XnaColor[bw * bh];
            GraphicsDevice.GetBackBufferData(px);
            var rgba = new byte[bw * bh * 4];
            int nonBlack = 0;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                rgba[i * 4] = c.R; rgba[i * 4 + 1] = c.G; rgba[i * 4 + 2] = c.B; rgba[i * 4 + 3] = 255;
                if ((c.R | c.G | c.B) != 0) nonBlack++;
            }
            string tag = _menu != null && _menu.Controller.Status != MenuStatus.Race ? "menu" : _race == null ? $"tr{_fly.Track:00}" : _opt.DriveAutopilot ? "drive" : "race";
            if (_drive != null) HostLog.Info(_drive.Summary());
            string shot = Path.Combine(_opt.AutotestDir!, $"backbuffer_{tag}.png");
            NFS.Data.PngWriter.WriteRgba(shot, bw, bh, rgba);
            _targets.ComposeCpu().SavePng(Path.Combine(_opt.AutotestDir!, $"framebuffer_{tag}.png"));
            HostLog.Info($"autotest: graphics {_gfx.Mode.Name()} k={_targets.Scale}");
            // the periodic report (every 600 frames) may have just emptied the timings
            if (_perf is { Frames: > 0 }) HostLog.Info($"{_perf.Report(skip: Math.Min(10, _perf.Frames / 2))} (k={_targets.Scale}, display {_gfx.Display})");
            HostLog.Info($"autotest: {_frames} frames, slice {_fly.CameraSlice().Slice}, drawn {_track?.LastDrawn ?? 0}, " +
                         $"backbuffer {bw}x{bh} nonblack {nonBlack} -> {shot}");
            ExitCode = nonBlack > 0 ? 0 : 3;
        }
        catch (Exception e)
        {
            HostLog.Error("autotest failed: " + e);
            ExitCode = 2;
        }
        Exit();
    }

    internal static XnaRect Letterbox(int bw, int bh)
    {
        var (x, y, w, h) = Layout.Fit(bw, bh, W, H);
        return new XnaRect(x, y, w, h);
    }
}
