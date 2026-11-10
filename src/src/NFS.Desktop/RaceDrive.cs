using System.Numerics;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Keyboard state relevant for driving (host-neutral: Game1 fills it from MonoGame keys).</summary>
public readonly record struct DriveKeys(bool Up, bool Down, bool Left, bool Right, bool GearUp, bool GearDown, bool Reset, bool Horn = false, bool Handbrake = false,
    sbyte AnalogSteer = 0, byte AnalogGas = 0, byte AnalogBrake = 0, bool CycleCamera = false, bool ToggleHud = false, bool LookBehind = false,
    bool HorizonToggle = false,
    bool SpectatePrev = false, bool SpectateNext = false, bool SpectateAuto = false);

/// <summary>
/// Keys → <see cref="PlayerInput"/> for one 32 Hz sim step: ↑ gas 255, ↓ brake 255, ←/→ steer ∓127 (input steer &gt; 0 =
/// right), space handbrake and H horn while held; A/Z gear up/down and R reset as one-shot buttons (edge-triggered: first step after the key went down).
/// </summary>
public sealed class KeyboardDriver
{
    DriveKeys _prev;
    byte _pending;

    // Capture short presses even when the render frame contains no 32 Hz input step.
    public void Observe(DriveKeys k)
    {
        if (k.GearUp && !_prev.GearUp) _pending |= PlayerInput.BtnGearUp;
        if (k.GearDown && !_prev.GearDown) _pending |= PlayerInput.BtnGearDown;
        if (k.Reset && !_prev.Reset) _pending |= PlayerInput.BtnReset;
        _prev = k;
    }

    public void DiscardPresses(DriveKeys held) { _pending = 0; _prev = held; }

    public PlayerInput Next(DriveKeys k)
    {
        var input = Peek(k);
        AcceptPresses(input.Buttons);
        return input;
    }

    public void AcceptPresses(byte buttons) => _pending &= (byte)~buttons;

    public PlayerInput Peek(DriveKeys k)
    {
        Observe(k);
        int steer = k.Left || k.Right ? (k.Right ? 127 : 0) - (k.Left ? 127 : 0) : k.AnalogSteer;
        byte buttons = _pending;
        if (k.Horn) buttons |= PlayerInput.BtnHorn;
        if (k.Handbrake) buttons |= PlayerInput.BtnHandbrake;
        return new PlayerInput((sbyte)steer, k.Up ? (byte)255 : k.AnalogGas, k.Down ? (byte)255 : k.AnalogBrake, buttons);
    }
}

/// <summary>
/// Fixed-step clock with a time accumulator: wall-clock dt in, number of whole sim steps out (the sim runs at its [ref:3405]
/// own fixed rate, render at most once per main-loop iteration). Steps per call are capped to avoid a spiral of death.
/// </summary>
public sealed class FixedStepClock(double hz, int maxSteps = 8)
{
    public double Hz { get; } = hz;
    public double Step => 1.0 / Hz;
    public double Accumulator { get; private set; }

    public int Advance(double dt)
    {
        Accumulator += Math.Max(0, dt);
        int n = (int)Math.Floor(Accumulator * Hz + 1e-9);
        Accumulator = Math.Max(0, Accumulator - n * Step);
        if (n > maxSteps) { n = maxSteps; Accumulator = 0; }
        return n;
    }
}

/// <summary>Chase camera: target <see cref="Back"/> m behind and <see cref="Height"/> m above the car, exponentially smoothed.</summary>
public sealed class ChaseCam
{
    public float Back { get; init; } = 6f;
    public float Height { get; init; } = 2f;
    /// <summary>Smoothing rate (1/s): fraction 1−e^(−rate·dt) of the gap closed per update.</summary>
    public float Rate { get; init; } = 8f;
    public Vector3 Position { get; private set; }
    public Vector3 Forward { get; private set; } = Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    bool _init;

    public static (Vector3 Pos, Vector3 Fwd, Vector3 Up) Target(Vec3 pos, Mat3 o, float back, float height)
    {
        var p = RaceScene.ToWorld(pos);
        var up = Vector3.Normalize(RaceScene.ToWorld(o.Row1));
        var fwd = Vector3.Normalize(RaceScene.ToWorld(o.Row2));
        return (p - fwd * back + up * height, fwd, up);
    }

    public void Snap(Vec3 pos, Mat3 o) { (Position, Forward, Up) = Target(pos, o, Back, Height); _init = true; }
    public void Snap(RenderPose pose)
    {
        Forward = Vector3.Normalize(pose.Forward); Up = Vector3.Normalize(pose.Up);
        Position = pose.Position - Forward * Back + Up * Height; _init = true;
    }

    public void Update(Vec3 pos, Mat3 o, double dt)
        => Update(RenderPose.From(pos, o), dt);

    public void Update(RenderPose pose, double dt)
    {
        if (!_init) { Snap(pose); return; }
        var tf = Vector3.Normalize(pose.Forward);
        var tu = Vector3.Normalize(pose.Up);
        var tp = pose.Position - tf * Back + tu * Height;
        float a = 1f - MathF.Exp(-Rate * (float)Math.Max(0, dt));
        Position = Vector3.Lerp(Position, tp, a);
        Forward = Vector3.Normalize(Vector3.Lerp(Forward, tf, a));
        Up = Vector3.Normalize(Vector3.Lerp(Up, tu, a));
    }

    public Camera Camera(int width = 640, int height = 480)
    {
        var cam = new Camera(width, height, baseWidth: NFS.Render.Camera.PanoramaBase(width, height));
        cam.LookAlong(Forward, Up);
        cam.Position = Position;
        return cam;
    }
}

/// <summary>Simple test autopilot (not the game's AI; same as RaceRunnerTests): steer to the slice heading and back to the centre line.</summary>
public static class Autopilot
{
    public static PlayerInput Input(RaceRunner run, byte gas = 200)
    {
        var c = run.Car;
        int dir = run.Session.Backward ? -1 : 1;
        var sl = run.Session.Slices[c.Trk.Slice];
        int want = FxMath.Atan2(sl.Forward[0] * dir, sl.Forward[2] * dir);   // 0x400 = 360°
        int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        int err = ((want - have + 0x200) & 0x3FF) - 0x200;
        int lat = c.LateralOffset * dir;
        int steer = Math.Clamp(err * 2 - lat / 0x4000, -127, 127);
        return new PlayerInput((sbyte)steer, gas, 0, 0);
    }
}

/// <summary>
/// Local clock driver (stage 5a.2): turns wall-clock dt of the Desktop loop into whole steps of an embedded
/// <see cref="LocalRaceProvider"/>. Kept apart from <see cref="IRaceSessionProvider"/> on purpose – a remote host has its own
/// scheduler and must never be stepped by a client's frame rate.
/// </summary>
public sealed class LocalClockDriver(LocalRaceProvider provider, FixedStepClock clock)
{
    public FixedStepClock Clock { get; } = clock;

    /// <summary>Runs the whole steps due after <paramref name="dt"/>: for each one <paramref name="submitInputs"/> queues the samples
    /// of the local seats, the host steps, then <paramref name="afterStep"/> (presentation drains the new frame). Stops early when a
    /// replay ran out. Returns the number of steps that were due (as <see cref="FixedStepClock.Advance"/>).</summary>
    public int Advance(double dt, Action submitInputs, Action afterStep)
    {
        int n = Clock.Advance(dt);
        for (int i = 0; i < n; i++)
        {
            if (provider.PlaybackEnded) break;
            submitInputs();
            provider.Step();
            afterStep();
        }
        return n;
    }
}

/// <summary>
/// Driving on top of <see cref="RaceScene"/>: the race runs in an embedded host behind <see cref="LocalRaceProvider"/> (32 Hz steps =
/// 2 × 64 Hz), stepped by <see cref="LocalClockDriver"/>. Presentation (cameras, pose history, HUD, cockpit, map, mirror [ref:1433]
/// weather, audio feed, skids, dynamic objects) reads only the published <see cref="RaceViewFrame"/>s and events, interpolated at the
/// render rate. No MonoGame types.
/// </summary>
public sealed class RaceDrive
{
    public const double StepHz = 32;

    public RaceScene Scene { get; }
    /// <summary>The session as presentation sees it: frames, events, input submission, pause.</summary>
    public IRaceSessionProvider Provider { get; }
    /// <summary>The embedded in-process provider (local-only controls: stepping, replay, manual reset, abort, recording).</summary>
    LocalRaceProvider? _local;
    public LocalRaceProvider Local => _local ?? throw new InvalidOperationException("This race is owned by a remote provider.");
    public LocalClockDriver Driver { get; }
    public FixedStepClock Clock { get; } = new(StepHz);

    // --- local host access (tests, the test autopilot's input, diagnostics); presentation never reads these ---
    /// <summary>Owner of the simulation (stage 5a.1). Local host access only.</summary>
    public RaceHost Host => Local.Host;
    /// <summary>Local host access only.</summary>
    public RaceRunner Runner => Host.Runner;
    /// <summary>Host-side tyre-mark producer (frames carry copies of its buffer). Local host access only.</summary>
    public SkidMarkTask Skids => Host.View.Skids!;
    /// <summary>The live player car. Local host access only; presentation uses <see cref="PlayerView"/>.</summary>
    public CarObj Car => Runner.Car;
    /// <summary>The second human's live car (split screen). Local host access only.</summary>
    public CarObj? Car2 => Runner.Player2?.Obj;
    public ReplayRecorder Recorder => Host.Recorder;
    public int Resets => Runner.ManualResets;

    /// <summary>Latest presented frame.</summary>
    public RaceViewFrame View { get; private set; }
    /// <summary>Raised for every consumed frame with its events, in order (the audio feed).</summary>
    public event Action<RaceViewFrame, IReadOnlyList<RaceEvent>>? FramePresented;
    public int PlayerCarId { get; }
    /// <summary>Split screen: CarId of the second human.</summary>
    public int? SecondCarId { get; }
    public CarView PlayerView => View.Car(PlayerCarId);

    /// <summary>The round is only watched (an eliminated knockout player, protocol v13): the camera, HUD, minimap and names refer to
    /// <see cref="WatchedCarId"/> instead of the seat car of the welcome. Always false for a driver (nothing changes for them).</summary>
    public bool Spectator { get; }
    int _watched;
    int _manualWatch = -1;
    /// <summary>CarId the camera and HUD follow: <see cref="PlayerCarId"/> for a driver; for a spectator the leader of the ranking
    /// (automatic) or the car chosen with <see cref="SpectateStep"/> until <see cref="SpectateAuto"/>.</summary>
    public int WatchedCarId => Spectator ? _watched : PlayerCarId;
    /// <summary>Spectator: the camera follows the leader by itself (no car picked by hand).</summary>
    public bool SpectatorAuto => _manualWatch < 0;
    public CarView WatchedView => View.Car(WatchedCarId);
    CarAnim? WatchedAnim => Spectator ? Scene.AnimationOf(Scene.InstanceIndex(_watched)) : Scene.PlayerAnimation;
    int CarTypeOf(int carId) => Scene.Session.Cars[carId].Setup.CarType;

    /// <summary>Spectator: name of the watched car (network nick, else the race configuration name of the AI driver).</summary>
    public string WatchedName
    {
        get
        {
            int id = WatchedCarId;
            string name = DriverNames != null && DriverNames.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n.Trim() : RaceLoader.CarName(Scene.Session.Setup, View.Car(id).PlayerIndex);
            return string.IsNullOrWhiteSpace(name) ? "BOT" : DriverLabels.AsciiLabel(name.Trim()).ToUpperInvariant();
        }
    }

    readonly List<int> _spectateCandidates = [];

    /// <summary>Cars a spectator can watch, in the order of the places (active racers only): first those still driving, then (at the end)
    /// those that crossed the finish line (<c>RaceState == 2</c>; they stay active and in the ranking but stand still). So the automatic
    /// view follows the first car that is still racing and only the leader when everybody finished. Reused list (valid until the next call).</summary>
    List<int> SpectateCandidates()
    {
        var list = _spectateCandidates;
        list.Clear();
        var ranking = View.Ranking;                                // indexed loops: no enumerator allocation per frame
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < ranking.Count; i++)
            {
                int id = ranking[i];
                if ((uint)id < (uint)View.Cars.Count && View.Car(id) is { Active: not 0 } car && (car.RaceState == 2) == (pass == 1)) list.Add(id);
            }
        return list;
    }

    /// <summary>Spectator: switches to the previous (<paramref name="direction"/> &lt; 0, towards the leader) or next (&gt; 0) car of the
    /// ranking, wrapping around; the choice stays until <see cref="SpectateAuto"/>.</summary>
    public void SpectateStep(int direction)
    {
        if (!Spectator) return;
        var list = SpectateCandidates();
        if (list.Count == 0) return;
        int at = list.IndexOf(_watched);
        int next = at < 0 ? 0 : ((at + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
        _manualWatch = list[next];
        RefreshWatched();
    }

    /// <summary>Spectator: back to following the leader.</summary>
    public void SpectateAuto()
    {
        if (!Spectator) return;
        _manualWatch = -1;
        RefreshWatched();
    }

    /// <summary>Spectator: recomputes <see cref="WatchedCarId"/> from the latest <see cref="View"/> (the leader, or the chosen car while it
    /// is still racing). A change retargets the camera (its car type) and snaps it onto the new car.</summary>
    void RefreshWatched()
    {
        if (!Spectator) return;
        if (_manualWatch >= 0 && ((uint)_manualWatch >= (uint)View.Cars.Count || View.Car(_manualWatch).Active == 0)) _manualWatch = -1;
        int id = _manualWatch;
        if (id < 0) { var list = SpectateCandidates(); id = list.Count > 0 ? list[0] : PlayerCarId; }
        if (id == _watched) return;
        _watched = id;
        _remotePlayerPose = RenderPose.From(View.Car(id).Pos, View.Car(id).Orient);
        Cam?.Retarget(CarTypeOf(id));
        _snapCamera = true;
    }
    CarView SecondView => View.Car(SecondCarId!.Value);
    readonly RaceEventInbox _inbox;
    readonly RemoteInterpolationClock _remoteInterpolation;
    readonly Func<long>? _monotonicMicros;
    readonly List<RaceViewFrame> _remoteFrames = new();
    readonly FixedStepClock _remoteInputClock = new(StepHz);
    readonly FixedStepClock _remoteAudioClock = new(StepHz);
    RaceViewFrame? _remotePresentedBasis;
    bool _remoteTerminalPresented;
    RenderPose _remotePlayerPose;

    public RaceCamera Cam { get; }
    /// <summary>Split screen: camera of the second human (null for one player).</summary>
    public RaceCamera? Cam2 { get; }
    public KeyboardDriver Keys2 { get; } = new();
    /// <summary>Two humans on one screen (COMMMODE 1, <c>1</c>): two views, two cameras, two HUDs. [ref:1434]</summary>
    public bool IsSplit => Cam2 != null;
    public KeyboardDriver Keys { get; } = new();
    public int Steps => View.Steps;
    public int StartSlice { get; }
    public RaceResults? Results => View.Results;
    public bool ConnectionLost => Results == null && !Provider.ReadLatestState().Connected;
    public ReplayInputSource? PlaybackSource => _local?.PlaybackSource;
    public bool PlaybackEnded => Provider.ReadLatestState().PlaybackEnded;
    public bool Paused => Provider.ReadLatestState().Paused;
    public bool RemotePaused { get; private set; }
    public void ToggleRemotePause() { if (_local == null) RemotePaused = !RemotePaused; }
    /// <summary>Replay strip commands; null unless a replay plays. [ref:1435]</summary>
    public ReplayUi? ReplayUi { get; }
    double _replayUiClock;
    /// <summary>Speed index of the replay strip (¼×, ½×, 1×, 2×; the original starts at 1×).</summary>
    public int ReplaySpeedIndex => ReplayUi?.SpeedIndex ?? ReplayUi.InitialSpeedIndex;
    public bool ReplayPaused => PlaybackSource != null && Paused;
    /// <summary>The eject command of the strip was committed: the caller leaves the replay (1). [ref:1436]</summary>
    public bool ReplayEjectRequested => ReplayUi is { EjectRequested: true };
    /// <summary>The strip as <see cref="ReplayStatusHud"/> draws it (null unless a replay plays).</summary>
    public ReplayStripState? ReplayStrip => ReplayUi?.Strip(View.SimulationTick64, PlaybackSource?.RaceTicks64 ?? 0);
    public static double ReplaySpeedMultiplier(int index) => index switch
    {
        0 => 0.25, 1 => 0.5, 2 => 1, 3 => 2, _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
    bool _snapCamera;
    // Replay camera director (auto camera): applied to Cam from the frames' ReplayCam; a manual [ref:1437]
    // camera change during playback switches the auto camera off (0) and the look-at tilt back to 0. [ref:1438]
    bool _replayAutoCam = true;
    int _replayCamSeen;
    int? _replayCamPending;
    /// <summary>Playback: the camera follows the replay camera director (false after a manual camera change).</summary>
    public bool ReplayAutoCamera => PlaybackSource != null && _replayAutoCam;
    bool _cameraHeld;
    bool _hudHeld;
    // split screen, second human ((1), C2_CYCLECAMERA) and the "watch the other human" state after a finish [ref:1439]
    bool _snapCamera2, _cameraHeld2, _hudHeld2;
    readonly bool[] _follow = new bool[2];
    public bool ShowMap2 { get; private set; } = true;
    public bool ShowHud2 { get; set; } = true;
    // (1) [ref:1440]
    public void ToggleHud2()
    {
        if (!ShowHud2) { ShowHud2 = true; ShowMap2 = true; }
        else if (ShowMap2) ShowMap2 = false;
        else ShowHud2 = false;
    }
    public bool ShowMap { get; private set; } = true;
    // KEYBOARD.TXT C1_TOGGLEHUD=F5. [ref:1441]
    public void ToggleHud()
    {
        if (!ShowHud) { ShowHud = true; ShowMap = true; }
        else if (ShowMap) ShowMap = false;
        else ShowHud = false;
    }

    /// <summary>takes them from HORIZONDISPLAY (CONFIG.DAT +0x132); [ref:1442] [ref:3406]
    /// false = draws one flat quad in the sky colour instead of the cloud gradient. [ref:1443]</summary>
    public bool HorizonDisplay { get; set; } = true;
    bool _horizonHeld;
    // Predict through the current render interval from the last two local states. This keeps 32 Hz
    // simulation smooth at render rates above 32 Hz without the full-step delay of backward interpolation.
    double LocalPoseFraction => 1 + Clock.Accumulator / Clock.Step;
    /// <summary>(0xE, tag 0x49 I_HORIZON): toggles the bit. [ref:1444]</summary>
    public void ToggleHorizon() => HorizonDisplay = !HorizonDisplay;

    /// <summary>normal offline race cannot pause before 0x140. [ref:1445]</summary>
    public bool Pause()
    {
        if (Results != null || View.SimulationTick64 < NFS.Game.Flow.GameStateMachine.PauseFromTick) return false;
        return Provider.RequestPause();
    }

    public void Resume(DriveKeys held = default, DriveKeys? held2 = null)
    {
        Keys.DiscardPresses(held); _cameraHeld = held.CycleCamera; _hudHeld = held.ToggleHud; _horizonHeld = held.HorizonToggle;
        Provider.RequestResume();
        if (held2 is { } h2) { Keys2.DiscardPresses(h2); _cameraHeld2 = h2.CycleCamera; _hudHeld2 = h2.ToggleHud; }
    }

    /// <summary>A key edge for the replay strip: Left/Right move the selection, Select runs the selected command.</summary>
    public void ReplayPress(ReplayUiKey key) => ReplayUi?.Press(key);

    /// <summary>Camera key during a replay: same as the camera command of the strip (the original ignores the key then).</summary>
    void ReplayCycleCamera()
    {
        if (ReplayUi is { } ui) ApplyReplayCamera(ui.CycleCamera());
    }

    public double ReplaySpeed => ReplayUi?.SpeedMultiplier ?? 1;

    /// <summary>Runs the strip's 32 Hz ticks for the elapsed time and applies what they commit (pause) or choose (camera).</summary>
    void StepReplayUi(double dt)
    {
        if (ReplayUi is not { } ui) return;
        _replayUiClock += dt;
        for (int ticks = 0; _replayUiClock >= ReplayUi.TickSeconds && ticks < 8; ticks++)
        {
            _replayUiClock -= ReplayUi.TickSeconds;
            ApplyReplayCamera(ui.Tick(Cam.Mode));
        }
        if (_replayUiClock >= ReplayUi.TickSeconds) _replayUiClock = 0; // do not queue a backlog after a stall
        if (ui.Paused && !Paused) Provider.RequestPause();
        else if (!ui.Paused && Paused) Provider.RequestResume();
    }

    /// <summary>camera command: the director's icon hands the camera back to the director, any other icon switches it off [ref:1446]
    /// (0, field +0x50 = 0) and sets that mode. The host's director keeps running either way: its draws [ref:1447]
    /// belong to the simulation, which the presentation must not change.</summary>
    void ApplyReplayCamera(ReplayUiCamera? camera)
    {
        if (camera is not { } c) return;
        if (c.Auto)
        {
            _replayAutoCam = true;
            if (View.ReplayCam is { } rcv) { Cam.ReplayTilt = rcv.Field50; _replayCamPending = rcv.Mode; }
        }
        else
        {
            _replayAutoCam = false;
            Cam.ReplayTilt = 0;
            _replayCamPending = c.Mode;
        }
    }

    public RaceDrive(RaceScene scene, bool withAi = false, bool countdown = true, bool? automatic = null,
        bool orbitCameraActivation = false, bool fixedCameraActivation = false, ReplayInputSource? playbackSource = null,
        IRaceSessionProvider? provider = null, Func<long>? monotonicMicros = null, int remoteInterpolationDelaySteps = 4,
        bool spectator = false)
    {
        Scene = scene;
        (ShowMap, ShowHud) = scene.InitialHudVisibility;
        var session = scene.Session;
        int idx = provider?.Description.PlayerCarId ?? scene.PlayerCarId;
        if ((uint)idx >= (uint)session.Cars.Count || idx != scene.PlayerCarId)
            throw new InvalidOperationException("Race provider's local seat does not match the race scene player car.");
        var player = session.Cars[idx];
        int? second = scene.SecondPlayerCar is { } sc ? session.Cars.ToList().IndexOf(sc) : null;
        _local = provider is LocalRaceProvider localProvider ? localProvider : provider == null
            ? new LocalRaceProvider(session, idx, automatic: automatic, withAi: withAi, countdown: countdown, secondPlayerIndex: second,
                playbackSource: playbackSource)
            : null!;
        Provider = provider ?? _local!;
        Driver = _local == null ? null! : new LocalClockDriver(_local, Clock);
        _monotonicMicros = monotonicMicros;
        _remoteInterpolation = new RemoteInterpolationClock(remoteInterpolationDelaySteps);
        var desc = Provider.Description;
        _inbox = new RaceEventInbox(desc.RaceId, expiryTicks64: _local == null ? 128 : null);
        View = Provider.ReadLatestState().Frame;
        if (_local == null)
        {
            _remoteFrames.Add(View);
            _remoteInterpolation.Observe(View, _monotonicMicros?.Invoke() ?? 0);
        }
        PlayerCarId = desc.PlayerCarId;
        SecondCarId = desc.SecondPlayerCarId;
        Spectator = _local == null && (spectator || provider is RemoteRaceProvider { Spectator: true });
        _watched = PlayerCarId;
        if (Spectator && SpectateCandidates() is { Count: > 0 } firstCars) _watched = firstCars[0];
        // Read-only track geometry of the presentation's own (corrections); never the host's locator. [ref:1448]
        var locator = new TrackLocator(new TrackGeometry(session.Trk, session.Slices));
        if (playbackSource != null) ReplayUi = new ReplayUi(player.Setup.CarType, scene.SecondPlayerCar != null);
        Cam = new RaceCamera(CarTypeOf(WatchedCarId))
        {
            RememberCameraKey = playbackSource == null, SkipCockpit = Spectator,
            TrackReverse = session.Backward, Slices = session.Slices, FinishAnimation = scene.FinishCan,
            OrbitEnabled = orbitCameraActivation, FixedEnabled = fixedCameraActivation,
            Locator = locator,
        };
        if (SecondCarId is { } p2)
        {
            Cam.Split = true;
            Cam2 = new RaceCamera(session.Cars[p2].Setup.CarType)
            {
                Split = true, TrackReverse = session.Backward, Slices = session.Slices, FinishAnimation = scene.FinishCan,
                Locator = locator, OrbitEnabled = orbitCameraActivation, FixedEnabled = fixedCameraActivation,
            };
        }
        HudMetric = session.Setup.Get(0x40) != 0;
        Weather = new Weather(session.Setup.Get(0x20), session.TrackNumber, new EaRandom(), scene.Cockpit?.CarType ?? -1); // (option 0x40, RAIN) [ref:1449]
        if (scene.SecondPlayerInstance >= 0) _weather2 = Weather.SecondView();
        StartSlice = PlayerView.Slice;
        Scene.SnapPoses(View);
        if (_local == null)
        {
            // ReadLatestState is the initial-state contract; DrainFrames may already be empty.
            _remoteFrames.Add(View);
            _remoteInterpolation.Observe(View, _monotonicMicros?.Invoke() ?? 0);
            _remotePlayerPose = RenderPose.From(WatchedView.Pos, WatchedView.Orient);
        }
        SyncRaceStarted();
        var pv = WatchedView;
        if (desc.Countdown)
        {
            Cam.StartIntro(scene.IntroCan, scene.IntroCanIndex, RenderPose.From(pv.Pos, pv.Orient), pv,
                WatchedAnim, scene.IntroReturnMode, View.SimulationTick64);
            if (Cam2 != null) Cam2.StartIntro(scene.IntroCan, scene.IntroCanIndex,
                RenderPose.From(SecondView.Pos, SecondView.Orient), SecondView, Scene.SecondPlayerAnimation, scene.IntroReturnMode, View.SimulationTick64);
        }
        else
        {
            Cam.Snap(RenderPose.From(pv.Pos, pv.Orient), pv, WatchedAnim);
            if (Cam2 != null) Cam2.Snap(RenderPose.From(SecondView.Pos, SecondView.Orient), SecondView, Scene.SecondPlayerAnimation);
        }
    }

    /// <summary>Weather of the race (<see cref="Weather.Enabled"/> only with option 0x40); render-only state with its own rand stream.</summary>
    public Weather Weather { get; }
    /// <summary>Split screen: weather state of the second player (<see cref="Core.Weather.SecondView"/>).</summary>
    readonly Weather? _weather2;

    /// <summary>One sim step with these inputs (button 0x40 → inside the step), then its frame is presented. [ref:1450]</summary>
    public void Step(PlayerInput input, PlayerInput? input2 = null)
    {
        if (_local == null) throw new InvalidOperationException("A remote race is advanced by its server.");
        SubmitInputs(input, input2);
        Local.Step();
        Present();
    }

    void SubmitInputs(PlayerInput input, PlayerInput? input2)
    {
        Provider.SubmitInput(0, input);
        if (input2 is { } i2 && SecondCarId != null) Provider.SubmitInput(1, i2);
    }

    /// <summary>Consumes the frames and events published since the last call (in order): pose history, car animation and the audio feed
    /// advance once per Step frame, exactly as the old per-step capture did.</summary>
    void Present()
    {
        _inbox.Accept(Provider.DrainEvents());
        foreach (var frame in Provider.DrainFrames())
        {
            if (frame.Kind is RaceViewFrameKind.Step or RaceViewFrameKind.Reset)
            {
                bool snapped = Scene.CapturePoses(frame);
                if (frame.Kind == RaceViewFrameKind.Step) { _snapCamera |= snapped; _snapCamera2 |= Scene.SecondSnapped; }
            }
            if (_replayAutoCam && frame.ReplayCam is { } rcv)
            {
                Cam.ReplayTilt = rcv.Field50;
                if (rcv.Changes != _replayCamSeen) { _replayCamSeen = rcv.Changes; _replayCamPending = rcv.Mode; }
            }
            View = frame;
            FramePresented?.Invoke(frame, _inbox.TakeThrough(frame.SnapshotSequence, frame.SimulationTick64));
        }
    }

    void PresentRemote(double dt)
    {
        long now = _monotonicMicros?.Invoke() ?? 0;
        _remoteInterpolation.Synchronize((Provider as RemoteRaceProvider)?.StepOneLocalTime);
        _inbox.Accept(Provider.DrainEvents());
        foreach (var frame in Provider.DrainFrames())
        {
            _remoteInterpolation.Observe(frame, now);
            int existing = _remoteFrames.FindIndex(f => f.SnapshotSequence == frame.SnapshotSequence);
            if (existing >= 0) _remoteFrames[existing] = frame;
            else if (_remoteFrames.Count == 0 || frame.SnapshotSequence > _remoteFrames[^1].SnapshotSequence)
                _remoteFrames.Add(frame);
            if (_remoteFrames.Count > 64) _remoteFrames.RemoveAt(0);
        }
        int audioTicks = _remoteAudioClock.Advance(dt);
        var terminal = Provider.ReadLatestState().Frame;
        if (terminal.Results != null)
        {
            // Reliable results may retain an older pose/time and the same sequence as the last unreliable snapshot.
            // Snap to that actual pose, and flush the finish event even when its authoritative finish tick is later.
            if (!_remoteTerminalPresented)
            {
                _remoteTerminalPresented = true;
                RemotePaused = false;
                SetRemoteView(terminal, terminal);
                _snapCamera = true;
            }
            var events = _inbox.TakeThrough(long.MaxValue, terminal.SimulationTick64);
            if (events.Count > 0) FramePresented?.Invoke(terminal with { Kind = RaceViewFrameKind.Resync }, events);
            return;
        }
        if (RemotePaused || _remoteFrames.Count == 0) return;
        // One sound/animation frame per elapsed 1/32 s, including holds during loss. At most eight after a stall.
        // Process tick times before the final visual time, so the monotonic clock also orders catch-up events.
        for (int i = 0; i < audioTicks; i++)
        {
            long tickTime = now - (long)Math.Round((_remoteAudioClock.Accumulator + (audioTicks - 1 - i) / StepHz) * 1_000_000);
            var (sample, _) = SampleRemote(_remoteInterpolation.TargetStep(tickTime));
            Scene.TickRemoteAnimations(sample);
            FramePresented?.Invoke(sample with { Kind = RaceViewFrameKind.Step },
                _inbox.TakeThrough(long.MaxValue, sample.SimulationTick64, sample.SimulationTick64));
        }
        var (view, basis) = SampleRemote(_remoteInterpolation.TargetStep(now));
        SetRemoteView(view, basis);
    }

    (RaceViewFrame Sample, RaceViewFrame Basis) SampleRemote(double target)
    {
        var before = _remoteFrames.LastOrDefault(f => f.Steps <= target) ?? _remoteFrames[0];
        var after = _remoteFrames.FirstOrDefault(f => f.Steps > target) ?? before;
        double fraction = after.Steps == before.Steps ? 0 : Math.Clamp((target - before.Steps) / (after.Steps - before.Steps), 0, 1);
        CarFrame Blend(CarFrame a, CarFrame b)
        {
            if (a.PoseEpoch != b.PoseEpoch || after.Kind is RaceViewFrameKind.Initial or RaceViewFrameKind.Resync) return a;
            var p = RenderPose.Interpolate(RenderPose.From(a.State.Pos, a.State.Orient), RenderPose.From(b.State.Pos, b.State.Orient), fraction);
            static int Fx(float v) => (int)Math.Round(v * NFS.Core.Fx.One);
            static Vec3 V(Vector3 v) => new(Fx(v.X), Fx(v.Y), Fx(v.Z));
            var r = V(p.Right); var u = V(p.Up); var f = V(p.Forward);
            return a with { State = a.State with { Pos = V(p.Position), Orient = new Mat3(r.X, r.Y, r.Z, u.X, u.Y, u.Z, f.X, f.Y, f.Z) } };
        }
        int step = Math.Clamp((int)Math.Floor(target), before.Steps, Math.Max(before.Steps, after.Steps));
        return (before with
        {
            Steps = step, SimulationTick64 = before.SimulationTick64 + 2 * (step - before.Steps),
            Cars = before.Cars.Zip(after.Cars, Blend).ToArray(), Parked = before.Parked.Zip(after.Parked, Blend).ToArray(),
        }, before);
    }

    void SetRemoteView(RaceViewFrame frame, RaceViewFrame basis)
    {
        int watched = WatchedCarId;
        _snapCamera |= View.Cars[watched].PoseEpoch != frame.Cars[watched].PoseEpoch ||
            (!ReferenceEquals(_remotePresentedBasis, basis) && basis.Kind is RaceViewFrameKind.Initial or RaceViewFrameKind.Resync);
        _remotePresentedBasis = basis;
        View = frame;
        RefreshWatched();
        // These are the exact buffered endpoints sampled above, not local 32 Hz PoseHistory.Push (8 m snap heuristic).
        Scene.SnapPoses(frame);
        watched = WatchedCarId;
        _remotePlayerPose = RenderPose.From(frame.Car(watched).Pos, frame.Car(watched).Orient);
    }

    /// <summary>Real-time frame: accumulate wall dt, run the whole steps with the keyboard input, smooth the camera.</summary>
    /// <param name="keys2">Split screen: keys of the second human (ignored for one player).</param>
    public int Frame(double dt, DriveKeys keys, DriveKeys? keys2 = null, Action? afterRemoteInput = null)
    {
        if (_local == null) return FrameRemote(dt, keys, afterRemoteInput);
        Local.EndPlaybackIfFinished();
        if (PlaybackSource != null && Results != null) return 0;
        if (Results != null) return 0;
        StepReplayUi(dt);
        if (Paused && PlaybackSource == null) return 0;
        if (keys.ToggleHud && !_hudHeld) ToggleHud();
        _hudHeld = keys.ToggleHud;
        if (keys.HorizonToggle && !_horizonHeld) ToggleHorizon();
        _horizonHeld = keys.HorizonToggle;
        Keys.Observe(keys);
        var k2 = keys2 ?? default;
        if (IsSplit)
        {
            if (k2.ToggleHud && !_hudHeld2) ToggleHud2();
            _hudHeld2 = k2.ToggleHud;
            Keys2.Observe(k2);
        }
        if (PlaybackSource != null && Paused)
        {
            // Presentation inputs remain live while the replay clock and race tick are frozen.
            // Local presentation follows the newest completed simulation step. Interpolating from the
            // previous step adds a full 32 Hz tick of control latency; remotes keep their own buffer.
            var pausedPose = Scene.PresentPoses(1);
            SyncRaceStarted();
            if (IsSplit) UpdateSplitCameras(0, keys, k2);
            else
            {
                var pv = PlayerView;
                Cam.SetLookBehind(keys.LookBehind, pausedPose, pv, Scene.PlayerAnimation);
                bool pausedCycle = keys.CycleCamera && !_cameraHeld;
                _cameraHeld = keys.CycleCamera;
                if (pausedCycle) ReplayCycleCamera();
                if (TakeReplayMode() is { } pausedMode) Cam.SetReplayMode(pausedMode, pausedPose, pv, Scene.PlayerAnimation, CameraRaceTick);
                else Cam.Update(pausedPose, pv, 0, Scene.PlayerAnimation, CameraRaceTick);
            }
            return 0;
        }
        if (Paused) return 0;
        int n = Driver.Advance(PlaybackSource == null ? dt : dt * ReplaySpeed,
            () => SubmitInputs(Keys.Next(keys), IsSplit ? Keys2.Next(k2) : null), Present);
        Local.EndPlaybackIfOutOfInput();
        var pose = Scene.PresentPoses(LocalPoseFraction);
        SyncRaceStarted();
        if (IsSplit) { UpdateSplitCameras(dt, keys, k2); return n; }
        var view = PlayerView;
        Cam.SetLookBehind(keys.LookBehind, pose, view, Scene.PlayerAnimation);
        bool cycle = keys.CycleCamera && !_cameraHeld;
        _cameraHeld = keys.CycleCamera;
        if (cycle && PlaybackSource != null) ReplayCycleCamera();
        if (cycle && PlaybackSource == null) Cam.Cycle(pose, view, Scene.PlayerAnimation, raceTick: CameraRaceTick);
        else if (TakeReplayMode() is { } replayMode) Cam.SetReplayMode(replayMode, pose, view, Scene.PlayerAnimation, CameraRaceTick);
        else if (_snapCamera) Cam.Snap(pose, view, Scene.PlayerAnimation, CameraRaceTick);
        else Cam.Update(pose, view, dt, Scene.PlayerAnimation, CameraRaceTick);
        _snapCamera = false;
        return n;
    }

    /// <summary>The pending camera mode of the replay (director change or camera command), once.</summary>
    int? TakeReplayMode()
    {
        if (Cam.Mode == RaceCamera.StartAnimationMode) return null; // the start animation is not interrupted; the choice waits for its end
        var m = _replayCamPending;
        _replayCamPending = null;
        return m;
    }

    /// <summary>Remote presentation polls at render cadence and samples controls at 32 Hz; the client never steps a Runner.</summary>
    int FrameRemote(double dt, DriveKeys keys, Action? afterRemoteInput)
    {
        if (keys.ToggleHud && !_hudHeld) ToggleHud();
        _hudHeld = keys.ToggleHud;
        if (keys.HorizonToggle && !_horizonHeld) ToggleHorizon();
        _horizonHeld = keys.HorizonToggle;
        // Drop paused edges while remembering held keys, so Resume cannot replay them.
        if (RemotePaused) Keys.DiscardPresses(keys);
        else Keys.Observe(keys);
        int inputs = _remoteInputClock.Advance(dt);
        for (int i = 0; i < inputs && Results == null; i++)
        {
            var input = RemotePaused ? default : Keys.Peek(keys);
            try
            {
                Provider.SubmitInput(0, input);
                Keys.AcceptPresses(input.Buttons);
            }
            catch (InvalidOperationException) { /* provider reports queue pressure/disconnect; polling continues */ }
        }
        if (Provider is RemoteRaceProvider remoteProvider)
            remoteProvider.UpdateRealtimeControls(RemotePaused ? default : Keys.Peek(keys));
        // Let the transport sample/send the controls from this render frame before presenting its replies.
        afterRemoteInput?.Invoke();
        PresentRemote(dt);
        if (RemotePaused && Results == null) return 0;
        var pose = _remotePlayerPose;
        SyncRaceStarted();
        if (Spectator) ReadSpectatorKeys(keys);
        pose = _remotePlayerPose;
        var view = WatchedView;
        var anim = WatchedAnim;
        Cam.SetLookBehind(keys.LookBehind, pose, view, anim);
        bool cycle = keys.CycleCamera && !_cameraHeld;
        _cameraHeld = keys.CycleCamera;
        if (cycle) Cam.Cycle(pose, view, anim, raceTick: CameraRaceTick);
        else if (_snapCamera) Cam.Snap(pose, view, anim, CameraRaceTick);
        else Cam.Update(pose, view, dt, anim, CameraRaceTick);
        // No dashboard of somebody else's car: a spectator skips the cockpit view (mode 0) for the hood view.
        if (Spectator) Cam.LeaveCockpit(pose, view, anim, CameraRaceTick);
        _snapCamera = false;
        return 0;
    }

    bool _specPrevHeld, _specNextHeld, _specAutoHeld;

    /// <summary>Spectator keys (edges): previous / next car (Q / E, or the gear down / up keys: LB / RB on a pad, the +/- buttons on touch),
    /// leader again (W).</summary>
    void ReadSpectatorKeys(DriveKeys keys)
    {
        bool prev = keys.SpectatePrev || keys.GearDown, next = keys.SpectateNext || keys.GearUp, auto = keys.SpectateAuto;
        // Independent edges: two keys pressed in the same frame both act (auto, then previous, then next).
        if (auto && !_specAutoHeld) SpectateAuto();
        if (prev && !_specPrevHeld) SpectateStep(-1);
        if (next && !_specNextHeld) SpectateStep(1);
        (_specPrevHeld, _specNextHeld, _specAutoHeld) = (prev, next, auto);
    }

    /// <summary>Deterministic autopilot run of <paramref name="seconds"/> of sim time (independent of the wall clock). The test
    /// autopilot is a local input source and reads the live host car to compute its input.</summary>
    public void RunAutopilot(double seconds)
    {
        if (Paused || Results != null) return;
        int n = (int)Math.Round(seconds * StepHz);
        for (int i = 0; i < n; i++) Step(Autopilot.Input(Runner));
        Scene.SnapPoses(View);
        SyncRaceStarted();
        var pv = PlayerView;
        Cam.Snap(RenderPose.From(pv.Pos, pv.Orient), pv, Scene.PlayerAnimation, View.SimulationTick64);
        if (Cam2 != null) Cam2.Snap(RenderPose.From(SecondView.Pos, SecondView.Orient), SecondView, Scene.SecondPlayerAnimation, View.SimulationTick64);
        _snapCamera = false;
    }

    /// <summary>Local tools and tests that edited the host directly (teleported a car, cleared the skid buffer): publish a fresh [ref:3407]
    /// frame of the host state and place the presentation exactly on it (no interpolation, no animation tick).</summary>
    public void SnapToHost()
    {
        Local.Resync();
        Present();
        Scene.SnapPoses(View);
        SyncRaceStarted();
    }

    /// <summary>Slices travelled in race direction since the start (mod track length).</summary>
    public int Progress
    {
        get
        {
            int n = Scene.Session.Slices.Length, dir = Scene.Session.Backward ? -1 : 1;
            return ((dir * (PlayerView.Slice - StartSlice)) % n + n) % n;
        }
    }

    /// <summary>Diagnostic line of the autotest (wall/edge/car hit counters and AI progress come from the local host).</summary>
    public string Summary() =>
        $"drive: {Steps} steps ({Steps / StepHz:F1} s), slice {StartSlice} -> {PlayerView.Slice} (+{Progress}), " +
        $"speed {PlayerView.SpeedApprox / (double)Fx.One:F1} m/s, gear {PlayerView.Gear}, wall hits {Runner.WallHits}, " +
        $"edge hits {Runner.EdgeHits}, resets {Resets}" + (Runner.Cars.Count > 1 ? $", AI progress [{string.Join(",", Runner.Cars.Skip(1).Select(Runner.Progress))}], car hits {Runner.CarHits}" : "");

    /// <summary>player reset outside a step, including original guards and lockout. [ref:1451]</summary>
    public void ResetToRoad()
    {
        if (!Paused && Results == null && Local.ManualReset())
        {
            Present();
            SyncRaceStarted();
            Cam.Snap(Scene.PresentPoses(1), PlayerView, Scene.PlayerAnimation, CameraRaceTick);
            _snapCamera = false;
        }
    }

    public Camera Camera(int width = 640, int height = 480) => Cam.Camera(width, height);

    FrameBuffer _mirror = new(640, 70); // Enhanced: 640K x 70K (see MirrorScale) [ref:1452]
    public bool ShowMirror => !Spectator && Results == null && Cam.InCar && Cam.Mode == 0 && !Cam.LookingBehind && Scene.Cockpit?.MirrorEnabled == true;
    /// <summary>HUDCON.DAT snapshot of this race (map, HUD text, remembered camera view, mirror) over the file the race started from. [ref:1453]</summary>
    public byte[] HudconSnapshot() => HudConfig.Compose(Scene.HudconBaseline, ShowMap, ShowHud, Cam.HudconCamMode, Scene.Cockpit?.MirrorEnabled == true);

    /// <summary>Split screen: CarId the car camera and HUD of <paramref name="player"/> refer to – its own car, or (after its finish
    /// animation) the other human's car. [ref:1454]</summary>
    public int TargetCarId(int player) => (player == 0) != _follow[player] ? PlayerCarId : SecondCarId ?? PlayerCarId;

    /// <summary>Live car of <see cref="TargetCarId"/>. Local host access only (tests).</summary>
    public CarObj TargetCar(int player) => Scene.Session.Cars[TargetCarId(player)].Obj;

    /// <summary>Whether the camera of <paramref name="player"/> already watches the other human.</summary>
    public bool Follows(int player) => _follow[player];

    double CameraRaceTick => View.SimulationTick64 + 2 * Clock.Accumulator / Clock.Step;

    /// <summary>Race-start state for the ticks of both views. [ref:1455]</summary>
    void SyncRaceStarted()
    {
        Cam.RaceStarted = View.RaceStarted;
        if (Cam2 != null) Cam2.RaceStarted = View.RaceStarted;
    }

    RaceCamera SplitCam(int player) => player == 0 ? Cam : Cam2!;

    void UpdateSplitCameras(double dt, DriveKeys keys, DriveKeys keys2)
    {
        double fraction = Paused ? 1 : LocalPoseFraction;
        for (int p = 0; p < 2; p++)
        {
            var cam = SplitCam(p);
            var k = p == 0 ? keys : keys2;
            var own = p == 0 ? PlayerView : SecondView;
            int targetId = TargetCarId(p);
            var target = View.Car(targetId);
            int instance = Scene.InstanceIndex(targetId);
            var pose = Scene.PoseOf(instance, fraction);
            var anim = Scene.AnimationOf(instance);
            cam.SetLookBehind(k.LookBehind, pose, target, anim);
            bool held = p == 0 ? _cameraHeld : _cameraHeld2;
            bool cycle = k.CycleCamera && !held;
            if (p == 0) _cameraHeld = k.CycleCamera; else _cameraHeld2 = k.CycleCamera;
            bool snap = p == 0 ? _snapCamera : _snapCamera2;
            if (cycle) cam.Cycle(pose, target, anim, own.RaceState == 2, CameraRaceTick);
            else if (snap) cam.Snap(pose, target, anim, CameraRaceTick);
            else cam.Update(pose, target, dt, anim, CameraRaceTick);
            if (p == 0) _snapCamera = false; else _snapCamera2 = false;
            // once its finish animation is over the camera is handed to the other human's car [ref:1456]
            if (!_follow[p] && cam.FinishAnimationDone)
            {
                _follow[p] = true;
                int otherId = TargetCarId(p);
                int oi = Scene.InstanceIndex(otherId);
                int otherType = Scene.Session.Cars[otherId].Setup.CarType;
                cam.FollowOtherCar(Scene.PoseOf(oi, fraction), View.Car(otherId), otherType, Scene.AnimationOf(oi));
            }
        }
    }

    /// <summary>For one split-screen HUD half (the car <see cref="TargetCarId"/> of that player). No rival name in split [ref:1457]
    /// (it is only set when <c>0</c>). [ref:1458]</summary>
    public OriginalHudValues SplitHudValues(int player)
    {
        int id = TargetCarId(player);
        var target = View.Car(id);
        return RaceHud.Read(target, View.SimulationTick64, View.NumLaps, View.RacerCount, Provider.Description.Redline[id], HudMetric,
            Scene.Session.Setup.Get(0x44), null, WrongWayCounter.Band(target.WrongWayTicks));
    }

    FrameBuffer[] _halves = [new(640, 240), new(640, 240)];
    readonly List<CarInstance> _viewCars = new(), _mirrorCars = new();   // reused every frame (the render loop allocates nothing once warm)

    /// <summary><c>1</c>: the 3D scene of each human into its own half (640x240, the middle of a [ref:1459]
    /// full-screen projection, <see cref="SplitLayout"/>), no mirror and no dashboard, then per-player HUD [ref:1460]
    /// minimap and the whole-screen countdown. [ref:1461]</summary>
    void RenderSplit(FrameBuffer scene, FrameBuffer fb, RenderOptions? opt)
    {
        opt ??= new RenderOptions();
        int tick = View.SimulationTick64;
        int k = Math.Max(1, scene.Height / 480);   // Enhanced: the 3D halves are 640k×240k (fb = the 640×480 2D layer); panorama: wider than 640k
        int vw = scene.Width;
        if (_halves[0].Width != vw || _halves[0].Height != 240 * k) _halves = [new(vw, 240 * k), new(vw, 240 * k)];
        for (int p = 0; p < 2; p++)
        {
            var cam = SplitCam(p);
            int targetId = TargetCarId(p);
            var target = View.Car(targetId);
            int instance = Scene.InstanceIndex(targetId);
            // in the hood view (mode 1) the viewed car itself is not drawn [ref:1462]
            var cars = _viewCars;
            Scene.FillRenderCars(cars, tick, Paused, cam.InCar ? instance : -1);
            var halfCamera = cam.Camera(vw, 480 * k);
            Scene.Track.Render(_halves[p], halfCamera, opt, cars, View.Knocked, tick, View.Skids,
                targetSlice: target.Slice, targetWrongWayTicks: target.WrongWayTicks, viewIndex: p, range: ViewRange.Split.Scaled(0));
            DrawParticles(_halves[p], halfCamera, cars, new SceneCall(opt, tick, target.Slice, target.WrongWayTicks, p, false, ViewRange.Split.Scaled(0)));
        }
        SplitLayout.Compose(scene, _halves[0], _halves[1], k, vw);
        if (Weather.Enabled) DrawSplitWeather(fb);
        if (Results is { } results) { Scene.ResultScreen.Draw(fb, results); return; }
        for (int p = 0; p < 2; p++)
        {
            if (p == 0 ? ShowHud : ShowHud2) Scene.Hud.DrawSplit(fb, SplitHudValues(p), p);
            if (p == 0 ? ShowMap : ShowMap2)
            {
                int targetId = TargetCarId(p);
                var target = View.Car(targetId);
                Scene.Map.Draw(fb, Scene.MapCarsShared(), Scene.MapIndex(targetId), target.Slice, Scene.Session.Backward,
                    Scene.Session.Setup.Mirrored, target.WrongWayTicks, false, originX: 0, originY: p == 0 ? 0x45 : 0x135);
            }
        }
        Scene.Hud.DrawCountdown(fb, tick);   // whole screen, independent of the HUD flags [ref:1463]
    }

    /// <summary>Whole frame into one 640×480 buffer (Vanilla and every headless tool): the 3D scene and the 2D layer share <paramref name="fb"/>.</summary>
    public void Render(FrameBuffer fb, RenderOptions? opt = null) => Render(fb, fb, opt);

    /// <summary>Two-layer frame (Enhanced): the 3D scene goes to <paramref name="scene"/> (640k×480k, the same [ref:3408]
    /// camera angles), everything drawn in screen pixels of the original (HUD, dashboard, mirror, minimap, rain, names, finish/result
    /// screens) to <paramref name="fb"/> (640×480, cleared transparent beforehand – see <see cref="FrameBuffer.ClearTransparent"/>).
    /// With the same buffer for both (<see cref="Render(FrameBuffer, RenderOptions?)"/>) the result is the original image.</summary>
    public void Render(FrameBuffer scene, FrameBuffer fb, RenderOptions? opt = null)
    {
        if (IsSplit) { RenderSplit(scene, fb, opt); return; }
        int tick = View.SimulationTick64;
        var pv = WatchedView;
        var viewCamera = Camera(scene.Width, scene.Height);
        var labelCamera = ReferenceEquals(scene, fb) ? viewCamera : Camera(fb.Width, fb.Height);   // 2D labels project into 640×480 pixels
        // excludes the target in modes0/1, but rear cockpit reports effective mode9. [ref:1464]
        bool hideOwnCar = Cam.InCar && !(Cam.Mode == 0 && Cam.LookingBehind);
        var cars = _viewCars;
        opt ??= new RenderOptions();
        Scene.FillRenderCars(cars, tick, Paused, hideOwnCar ? (Spectator ? Scene.InstanceIndex(WatchedCarId) : Scene.PlayerInstance) : -1);
        Scene.Track.Render(scene, viewCamera, opt, cars, View.Knocked, tick, View.Skids,
            targetSlice: pv.Slice, targetWrongWayTicks: pv.WrongWayTicks);
        DrawParticles(scene, viewCamera, cars, new SceneCall(opt, tick, pv.Slice, pv.WrongWayTicks, 0, false, null));
        if (Weather.Enabled) DrawWeather(fb);
        if (ShowDriverLabels) DrawDriverLabels(fb, labelCamera);
        bool hiresMirror = false;
        if (ShowMirror)
        {
            var player = Scene.Cars[Scene.PlayerInstance];
            // Enhanced (scene at K >= 2 times the 2D layer): the strip is rendered K times larger and pasted into the scene under the mask
            int mk = !ReferenceEquals(scene, fb) && scene.Height >= 960 && scene.Height % 480 == 0 ? scene.Height / 480 : 1;
            if (_mirror.Width != 640 * mk) _mirror = new FrameBuffer(640 * mk, 70 * mk);
            hiresMirror = mk > 1;
            var camera = Cam.MirrorCamera(new RenderPose(player.Position, player.Right, player.Up, player.Forward), mk);
            Scene.FillMirrorCars(_mirrorCars, camera);
            Scene.Track.Render(_mirror, camera, opt, _mirrorCars, View.Knocked, tick, View.Skids, rearMirror: true,
                targetSlice: pv.Slice, targetWrongWayTicks: pv.WrongWayTicks, range: ViewRange.Mirror); // (0) [ref:1465]
            DrawParticles(_mirror, camera, _mirrorCars, new SceneCall(opt, tick, pv.Slice, pv.WrongWayTicks, 0, true, ViewRange.Mirror));
        }
        if (Results is { } results) Scene.ResultScreen.Draw(fb, results);
        else
        {
            if (!Spectator) Scene.Cockpit?.Draw(fb, new CockpitValues(pv.LocalVel.Z, pv.Rpm, Provider.Description.Redline[PlayerCarId],
                pv.Steer, pv.Gear, HudMetric, Scene.Session.Setup.Mirrored), Cam.DisplayMode,
                ShowMirror ? _mirror : null, ShowHud, hiresMirror ? scene : null);
            if (ShowHud) Scene.Hud.Draw(fb, OriginalHudValues());
            else Scene.Hud.DrawCountdown(fb, tick); // independent of HUD flags. [ref:1466]
            if (ShowMap) Scene.Map.Draw(fb, Scene.MapCarsShared(), Spectator ? Scene.MapIndex(WatchedCarId) : Scene.MapPlayer, pv.Slice,
                Scene.Session.Backward, Scene.Session.Setup.Mirrored, pv.WrongWayTicks, Cam.InCar && Cam.Mode == 0 && !Cam.LookingBehind);
        }
    }

    ParticleSprites? _particleSprites;
    readonly ParticleRenderer _particles = new();

    /// <summary>Port option: draw the effect particles (tyre smoke, sparks, dust, grass/gravel/snow spray). [ref:1467]</summary>
    public bool ShowParticles { get; set; } = true;

    /// <summary>The arguments of the Track.Render call that made the view being decorated with particles (its options and range are reused for the
    /// silhouette of the cars that hide a sprite, <see cref="ParticleRenderer"/>).</summary>
    readonly record struct SceneCall(RenderOptions Opt, int Tick, int Slice, int WrongWayTicks, int ViewIndex, bool RearMirror, ViewRange? Range);

    SceneCall _sceneCall;
    Camera? _sceneCamera;
    Action<FrameBuffer, IReadOnlyList<CarInstance>>? _renderWithout;

    void RenderCarsSilhouette(FrameBuffer buffer, IReadOnlyList<CarInstance> cars) =>
        Scene.Track.RenderCarsOnly(buffer, _sceneCamera!, _sceneCall.Opt, cars, _sceneCall.Range);

    /// <summary>The effect particles of the latest frame over the finished 3-D view <paramref name="target"/> (no allocation once the shapes are loaded);
    /// sprites that the original's z-sort puts behind a car do not cover it.</summary>
    void DrawParticles(FrameBuffer target, Camera camera, IReadOnlyList<CarInstance> cars, in SceneCall call)
    {
        if (!ShowParticles || View.Particles is not Particle[] items || items.Length == 0) return;
        (_sceneCall, _sceneCamera) = (call, camera);
        _particles.Draw(target, camera, items, _particleSprites ??= new ParticleSprites(Scene.Session.TrackNumber), cars,
            _renderWithout ??= RenderCarsSilhouette);
    }

    /// <summary>Port-only network overlay: a name above every car in view except the local player's own car. Names come from
    /// the welcome roster; the cars of the AI opponents (no roster entry) show <see cref="DriverLabels.BotTag"/>.</summary>
    void DrawDriverLabels(FrameBuffer fb, Camera camera)
    {
        var cars = new List<DriverLabels.Car>();
        for (int i = 0; i < Scene.Cars.Count; i++)
        {
            int carId = Scene.CarIdOf(i);
            if (View.Car(carId).Active == 0 || carId == WatchedCarId) continue;
            var car = Scene.Cars[i];
            cars.Add(new DriverLabels.Car(car.Position, car.Up,
                DriverLabels.NameOf(DriverNames, carId), !DriverLabels.IsPlayer(DriverNames, carId)));
        }
        DriverLabels.Draw(fb, camera, cars);
    }

    /// <summary>with <c>!= 0</c>: <c>(0)</c> and <c>(1)</c> (intensity of the car of each [ref:1468]
    /// player, 20 streak records per player, y range 0..0xB4 / 0xF0..0x1A4 of the whole screen), no windscreen drops.</summary>
    void DrawSplitWeather(FrameBuffer fb)
    {
        var slices = Scene.Session.Slices;
        for (int p = 0; p < 2; p++)
        {
            var w = p == 0 ? Weather : _weather2!;
            var car = View.Car(TargetCarId(p));
            w.Update(car.Slice, slices.Length, slices[car.Slice].Surroundings);
            var (first, count) = w.VisibleStreaksSplit(p);
            if (count == 0) continue;
            var right = SplitCam(p).Camera().Right;
            var (y0, y1) = Weather.SplitYRange(p);
            w.UpdateStreaks(first, count, car.LocalVel.Z, (int)(right.X * 65536), (int)(right.Z * 65536), y0, y1);
            RainOverlay.Draw(fb, w, first, count);
        }
    }

    /// <summary>(first overlay): intensity of the player's slice, streak update, lines. [ref:1469]</summary>
    DropSprites? _dropSprites;

    void DrawWeather(FrameBuffer fb)
    {
        var slices = Scene.Session.Slices;
        var car = WatchedView;
        Weather.Update(car.Slice, slices.Length, slices[car.Slice].Surroundings);
        // windscreen drops only in the cockpit view (dash active), updated before the streaks are drawn [ref:1470]
        if (Cam.DisplayMode == 0)
        {
            var (df, dc) = Weather.UpdateDrops(car.LocalVel.Z, Paused);
            if (dc > 0) RainOverlay.DrawDrops(fb, Weather, df, dc, _dropSprites ??= new DropSprites());
        }
        var (first, count) = Weather.VisibleStreaks();
        if (count == 0) return;
        var right = Camera(fb.Width, fb.Height).Right;
        Weather.UpdateStreaks(first, count, car.LocalVel.Z, (int)(right.X * 65536), (int)(right.Z * 65536));
        RainOverlay.Draw(fb, Weather, first, count);
    }

    /// <summary>The dashboard is part of this frame (cockpit view, not split, no result screen): the panorama then repeats its edges into the side margins.</summary>
    public bool CockpitDrawn => !Spectator && !IsSplit && Results == null && Scene.Cockpit is { } c && c.Active(Cam.DisplayMode);

    public bool ShowHud { get; set; } = true;
    /// <summary>(race config +0x40): km/h when nonzero, otherwise mph. [ref:1471]</summary>
    public bool HudMetric { get; set; } = true;

    /// <summary>HUD values of the player car.</summary>
    public OriginalHudValues OriginalHudValues()
    {
        var pv = WatchedView;
        return RaceHud.Read(pv, View.SimulationTick64, View.NumLaps, View.RacerCount, Provider.Description.Redline[WatchedCarId], HudMetric,
            Scene.Session.Setup.Get(0x44), RivalName(), WrongWayCounter.Band(pv.WrongWayTicks));
    }

    /// <summary>Name (race config record +0x34) of the car one place ahead (behind when looking back). [ref:1472]</summary>
    /// <summary>Presentation-only network names keyed by authoritative CarId; local/AI names keep the frozen setup.</summary>
    public IReadOnlyDictionary<int, string>? DriverNames { get; set; }

    bool _driverLabels = true;
    /// <summary>Port-only overlay: names above the cars of a network race, never above the local player's own car (the local
    /// and split-screen games keep the original screen). Every car without a network player name is an AI opponent and is
    /// shown as <see cref="DriverLabels.BotTag"/>.</summary>
    public bool ShowDriverLabels { get => _driverLabels && _local == null; set => _driverLabels = value; }

    public string? RivalName()
    {
        if (RaceHud.RivalIndex(View.Ranking, WatchedCarId, Cam.LookingBehind) is not { } rival) return null;
        return DriverNames != null && DriverNames.TryGetValue(rival, out var name)
            ? name : RaceLoader.CarName(Scene.Session.Setup, View.Car(rival).PlayerIndex);
    }

    public HudState Hud()
    {
        var pv = WatchedView;
        int ticks = pv.RaceState == 2 ? pv.LapStartTick : Math.Max(0, View.SimulationTick64 - RaceProgress.StartTick);
        return new HudState(pv.SpeedApprox, pv.Gear, pv.LapsDone + 1, View.NumLaps, pv.RacePosition, View.RacerCount, ticks, HudMetric);
    }
}
