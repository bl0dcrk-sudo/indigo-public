using System.Numerics;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Data.Track;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Camera-cycle views and special cameras: finish camera, crash camera, [ref:1330]
/// reset, flipped-car camera and the camera driving along the track (mode 6). [ref:1331]
/// Presentation-only state: reads the interpolated pose and never writes to CarObj.
/// </summary>
public sealed class RaceCamera
{
    int _carType;
    readonly Vec3[] _offsets;
    int _carHeight;
    Vector3 _worldOffset;
    double _slowTime;
    bool _initialized, _presented;
    // +0x17C > 0; +0x2E8 ≠ 0; both look at the car from outside. [ref:1332]
    bool _crashing, _orbit;
    Vector3 _crashPos;
    // 3: up·n < 0xE666 → for 100 ticks (counter +0x30F0), flag +0x30E0 = car went backwards. [ref:1333]
    double _flipTicks;
    bool _flipBackwards;
    int _orbitPhase;
    double _orbitSubticks, _orbitLastTick;
    bool _orbitTickInitialized;
    // the first tick with +0x280 == 2 starts CAN 1 (6 on a reverse track) and mode 0xC; flag +0x30CC is never cleared. [ref:1334]
    bool _finishStarted;
    double _finishTicks;
    Can? _startAnimation;
    int _startAnimationIndex;
    int _startReturnMode;
    double _startTicks, _startRaceTick;
    // station = −1, zoom flag +0x10C = 1, orientation +0x50 = 0. [ref:1335]
    int _tvStation = -1;
    bool _tvSide;
    bool _tvZoom = true;
    int _tvOrientation;
    int _tvDistance;
    // offset (car axes) and offset (world axes), 16.16. [ref:1336]
    readonly Vec3 _mode7Offset, _mode8Offset;
    // Track camera state (per camera; original counter, slice, speed index). Tables: [ref:1337]
    // entry speed thresholds and ticks per slice (16.16 m/s and camera ticks). [ref:1338]
    readonly int[] _trackEntrySpeeds, _trackSpeeds, _trackTicks;
    int _trackCounter, _trackSlice, _trackIdx;
    double _trackSubticks, _trackLastTick;
    bool _trackTickInitialized;

    public const int FinishMode = 0xC;
    public const int StartAnimationMode = 0xB;

    /// <summary>CAN slot 0 for forward circuit races, otherwise 4 forward / 5 reverse. [ref:1339]</summary>
    public static int StartCanIndex(int raceType, int commMode, bool trackReverse)
        => raceType == 0 && commMode != 4 && !trackReverse ? 0 : trackReverse ? 5 : 4;

    public int Mode { get; private set; } = 2;

    /// <summary>the view (0..3) the camera returns to when the start animation ends. [ref:1340]
    /// Starts from HUDCON.DAT +0x1C, is overwritten by the camera key (mode &lt; 4, not in split screen and not [ref:1341]
    /// while a replay plays) and when the local car finishes (the view it had, if &lt; 4). [ref:1342]</summary>
    public int HudconCamMode => _startReturnMode;
    /// <summary>False while a replay plays (<c>!= 0</c>): the camera key then does not write <see cref="HudconCamMode"/>. [ref:1343]</summary>
    public bool RememberCameraKey { get; set; } = true;

    void RememberMode()
    {
        if (!Split && !SkipCockpit && RememberCameraKey && Mode < 4) _startReturnMode = Mode;
    }
    /// <summary>Port-only (network spectator): the cockpit view (mode 0) is left out of the cycle (it would show the dashboard of the seat car)
    /// and the camera key does not write <see cref="HudconCamMode"/>. False for a driver: nothing changes for them.</summary>
    public bool SkipCockpit { get; set; }

    /// <summary>Spectator: when the camera ended up in the cockpit view by another way than the camera key (the start, a replay mode), it moves
    /// on to the next view at once (without touching <see cref="HudconCamMode"/>); true when it did.</summary>
    public bool LeaveCockpit(RenderPose pose, CarView car, CarAnim? anim = null, double? raceTick = null)
    {
        if (!SkipCockpit || Mode != 0) return false;
        Mode = NextMode(0, _carType, Split, orbitEnabled: OrbitEnabled, fixedEnabled: FixedEnabled);
        _orbitTickInitialized = false;
        Snap(pose, car, anim, raceTick);
        return true;
    }
    /// <summary>Cockpit/hood views; false while the crash or reset-orbit camera looks at the car from outside.</summary>
    public bool InCar => Mode < 2 && !_crashing && !_orbit;
    /// <summary>9 while the rear cockpit, the crash camera or the reset orbit is active. [ref:1344]</summary>
    public int DisplayMode => (LookingBehind && Mode is not (5 or 6 or 7 or 8)) || _crashing || _orbit ? 9 : Mode;
    public bool Crashing => _crashing;
    /// <summary>active while the flip camera drives the camera (car tilted: up·n &lt; 0xE666, 100 ticks). [ref:1345]</summary>
    public bool Flipping => _flipTicks > 0;
    /// <summary>the race runs against the track direction. [ref:1346]</summary>
    public bool TrackReverse { get; set; }
    /// <summary>Slices of the track (checks the road edges of the car slice); null = no check. [ref:1347]</summary>
    public TrackSlice[]? Slices { get; set; }
    /// <summary>Locator of the track for the view corrections, from mode 2 up [ref:1348]
    /// null = no correction. [ref:1349]</summary>
    public TrackLocator? Locator
    {
        get => _locator;
        set { _locator = value; _corrector = value == null ? null : new CameraTrackCorrector(value); }
    }
    TrackLocator? _locator;
    CameraTrackCorrector? _corrector;
    /// <summary>TRxx01.CAN (TRxx06.CAN on a reverse track) played by the finish camera; null = no finish camera. [ref:1350]</summary>
    public Can? FinishAnimation { get; set; }
    /// <summary>the camera start CAN (slot 0, 4 or 5); null when it is absent/invalid. [ref:1351]</summary>
    public Can? StartAnimation => _startAnimation;
    public int StartAnimationIndex => _startAnimationIndex;
    public bool LookingBehind { get; private set; }
    /// <summary>current roadside station slice. [ref:1352]</summary>
    public int TvStationSlice => _tvStation;
    /// <summary>side of the road selected for the current station. [ref:1353]</summary>
    public bool TvSide => _tvSide;
    /// <summary>station changes negate camera field +0x50; it starts at 0 and only the replay look-at reads it. [ref:1354]</summary>
    public int TvOrientation => _tvOrientation;
    /// <summary>zoom flag (+0x10C) toggled when the station changes; starts enabled. [ref:1355]</summary>
    public bool TvZoomEnabled => _tvZoom;
    /// <summary>Slice of the camera's retained locator position (cam+0x60) used by the mask check of the next TV frame; −1 = none.</summary>
    public int TvLocatorSlice => _corrector?.LocatorSlice ?? -1;
    /// <summary>Projection of the trackside TV view (mode 5) is active: not while crashing or reset (cam_get_mode9 restores the normal lens).</summary>
    bool TvActive => Mode == 5 && !_crashing && !_orbit;

    /// <summary>Split screen: true when the finish animation (CAN) of this player's camera has played to its last key [ref:1356]
    /// The original then hands the camera to the other human's car. [ref:1357]</summary>
    public bool FinishAnimationDone => Mode == FinishMode && _finishStarted && FinishAnimation is { Keys.Count: > 0 } can
        && (can.Keys.Count <= 1 || _finishTicks / can.TicksPerKey >= can.Keys.Count - 1);

    /// <summary>In split screen, after the finish animation the camera takes the other [ref:1358]
    /// human's car (target and player car copied) and moves on to the next mode of the finished-player table. [ref:1359]</summary>
    public void FollowOtherCar(RenderPose pose, CarView car, int carType, CarAnim? anim = null)
    {
        _carType = carType;
        _carHeight = CameraConstants.CarHeights[Math.Min(carType, 9)];
        Mode = NextMode(Mode, _carType, split: true, finished: true);
        if (Mode == 6) EnterTrack(car);
        Snap(pose, car, anim);
    }
    /// <summary>Port-only (network spectator): the camera now follows a car of another model; keeps the mode, only the model-dependent
    /// car height changes (the caller snaps the view).</summary>
    public void Retarget(int carType)
    {
        _carType = carType;
        _carHeight = CameraConstants.CarHeights[Math.Min(carType, 9)];
    }
    public Vector3 Position { get; private set; }
    public Vector3 Forward { get; private set; } = Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;

    public RaceCamera(int carType)
    {
        _carType = carType;
        // cockpit, hood, far, near (same order as in the original). [ref:1360]
        _offsets = [FromInts(CameraConstants.CockpitOffset), FromInts(CameraConstants.HoodOffset),
            FromInts(CameraConstants.FarOffset), FromInts(CameraConstants.NearOffset)];
        _carHeight = CameraConstants.CarHeights[Math.Min(carType, 9)];
        _mode7Offset = FromInts(CameraConstants.Mode7Offset);
        _mode8Offset = FromInts(CameraConstants.Mode8Offset);
        _trackEntrySpeeds = CameraConstants.TrackEntrySpeeds.ToArray();
        _trackSpeeds = CameraConstants.TrackSpeeds.ToArray();
        _trackTicks = CameraConstants.TrackTicks.ToArray();
    }

    /// <summary>A 16.16 vector stored as an int triple (x, y, z) in <see cref="CameraConstants"/>.</summary>
    static Vec3 FromInts(ReadOnlySpan<int> v) => new(v[0], v[1], v[2]);

    /// <summary>Split screen (<c>1</c>): camera of one of two players; skips mode 0 (cockpit with the dashboard [ref:1361]
    /// ), a player who has finished the race cycles through the table instead. [ref:1362]</summary>
    public bool Split { get; set; }
    /// <summary>RightShift+C enables orbit mode 4 and its following TV station mode 5. [ref:1363]</summary>
    /// <summary>Replay look-at field +0x50 (16.16): x component of the look-at up hint, so the [ref:1364]
    /// view is rolled to one side. Used only while a replay plays (the original reads it only with == 2); 0 in a race. [ref:1365]</summary>
    public int ReplayTilt { get; set; }

    /// <summary>Up hint of the look-at ((0, 1, 0), x replaced by <see cref="ReplayTilt"/>); cockpit views and the reset orbit [ref:1366]
    /// (zeroes +0x50) stay upright. [ref:1367]</summary>
    Vector3 LookHint => new(InCar || _orbit ? 0 : ReplayTilt / 65536f, 1, 0);

    /// <summary>Used by the replay camera director: the mode is taken as is and the view jumps to it [ref:1368]
    /// like after <see cref="Cycle"/>. Ignored during the start animation (the first change comes after it) and in the finish mode.
    /// Mode 0 (cockpit; only the camera icon of the replay strip asks for it) goes through: cars above model 11 have [ref:1369]
    /// no cockpit view and take the next mode.</summary>
    public void SetReplayMode(int mode, RenderPose pose, CarView car, CarAnim? anim = null, double? raceTick = null)
    {
        if (Mode is StartAnimationMode or FinishMode || mode is < 0 or > 8) return;
        if (mode == 0 && _carType > 11) mode = NextMode(0, _carType, Split);
        Mode = mode;
        if (Mode == 6) EnterTrack(car);
        _orbitTickInitialized = false;
        Snap(pose, car, anim, raceTick);
    }

    public bool OrbitEnabled { get; set; }
    /// <summary>RightShift+B enables fixed camera mode 8 (successor of mode 3 without C, of mode 7 with C). [ref:1370]</summary>
    public bool FixedEnabled { get; set; }
    /// <summary>counts its ticks only after the race start (RaceRunner.RaceStarted, set by RaceDrive). [ref:1371]</summary>
    public bool RaceStarted { get; set; }

    /// <summary>Original cycle 0 → 1 → 2 → 3 → 0; the new view appears at the car at once. [ref:1372]</summary>
    /// <param name="finished">Split screen: this player's own car has finished (+0x280 == 2); default = the viewed car.</param>
    public void Cycle(RenderPose pose, CarView car, CarAnim? anim = null, bool? finished = null, double? raceTick = null)
    {
        // returns immediately in mode 0xB. [ref:1373]
        if (Mode == StartAnimationMode) { Snap(pose, car, anim, raceTick); return; }
        Mode = NextMode(Mode, _carType, Split, Split && (finished ?? car.RaceState == 2), OrbitEnabled, FixedEnabled);
        if (SkipCockpit && Mode == 0) Mode = NextMode(0, _carType, Split, orbitEnabled: OrbitEnabled, fixedEnabled: FixedEnabled);
        RememberMode();
        if (Mode == 6) EnterTrack(car);
        _orbitTickInitialized = false;
        Snap(pose, car, anim, raceTick);
    }

    /// <summary>enters mode 0xB when the selected CAN is available. [ref:1374]</summary>
    public void StartIntro(Can? can, int canIndex, RenderPose pose, CarView car, CarAnim? anim = null, int returnMode = 0, double raceTick = 0)
    {
        _startAnimation = can is { Keys.Count: >= 2 } ? can : null;
        _startAnimationIndex = canIndex;
        _startReturnMode = returnMode is >= 0 and <= 3 ? returnMode : 0;
        _startTicks = 0;
        _startRaceTick = raceTick;
        if (_startAnimation == null)
        {
            Mode = IntroReturnMode();
            Snap(pose, car, anim);
            return;
        }
        Mode = StartAnimationMode;
        _worldOffset = Vector3.Zero;
        _initialized = true;
        PresentIntro(pose, car, anim);
    }

    /// <summary>(ported modes only): next view after <paramref name="mode"/>. [ref:1375]
    /// = table {1,2,3,0,5,6,7,0,…,6} (next mode after the mode), = the player's table after the finish in split [ref:1376]
    /// {1,2,3,4,5,6,7,1,4,…}; in split mode 0 is skipped (recursion). [ref:1377]
    /// The table is copied and modified by the keys: RShift+C → successor of 3 = 4; [ref:1378]
    /// RShift+B → successor of 3 = 8 (without C), and with C successor of 7 = 8. Mode 6 (track camera) is a regular cycle mode.</summary>
    public static int NextMode(int mode, int carType, bool split = false, bool finished = false, bool orbitEnabled = false,
        bool fixedEnabled = false)
    {
        var table = finished ? CameraConstants.SuccessorFinished : CameraConstants.SuccessorDefault;
        mode = Successor(mode, finished, orbitEnabled, fixedEnabled);
        if (mode > 8) mode = 0;
        if (mode == 0 && split) mode = table[0];
        if (mode == 0 && carType > 11) mode = CameraConstants.SuccessorDefault[0];
        return mode;
    }

    /// <summary>Successor of the mode in the table (after key adjustments) or in the player's table after the finish. [ref:1379]</summary>
    static int Successor(int mode, bool finished, bool orbitEnabled, bool fixedEnabled)
    {
        if (finished) return CameraConstants.SuccessorFinished[mode];
        if (mode == 3 && orbitEnabled) return 4;
        if (mode == 3 && fixedEnabled) return 8;
        if (mode == 7 && orbitEnabled && fixedEnabled) return 8;
        return CameraConstants.SuccessorDefault[mode];
    }

    public void Snap(RenderPose pose, CarView car, CarAnim? anim = null, double? raceTick = null)
    {
        // Presentation snaps must consume elapsed camera time, including RunAutopilot's final snap.
        // Reentry through Cycle rebases the clock separately while retaining the original phase counters.
        if (Mode == 4 && _initialized && _orbitTickInitialized && raceTick is { } orbitTick)
        {
            Update(pose, car, Math.Max(1e-12, (orbitTick - _orbitLastTick) / 64), anim, orbitTick);
            return;
        }
        SetIntroTime(raceTick);
        if (Mode == 4 && raceTick is { } tick)
        {
            _orbitLastTick = tick;
            _orbitTickInitialized = true;
        }
        else if (Mode != 4) _orbitTickInitialized = false;
        // snaps place the camera without a step; they only re-base the tick clock. [ref:1380]
        if (Mode == 6) { if (raceTick is { } t6) { _trackLastTick = t6; _trackTickInitialized = true; } }
        else _trackTickInitialized = false;
        Observe(car);
        if (Mode is not (FinishMode or 5 or 6 or 7 or 8)) _worldOffset = TargetOffset(pose, car);
        if (Mode == 7) _worldOffset = Mode7Offset(pose);
        _slowTime = 50.0 / 32;
        if (!_initialized) _crashPos = pose.Position + _worldOffset;
        _initialized = true;
        _flipTicks = 0;
        if (Detached(pose, car, anim, 0)) return;
        if (Mode == FinishMode) PresentFinish(pose, car);
        else if (Mode == StartAnimationMode) PresentIntro(pose, car, anim);
        else if (Mode == 4) PresentOrbit(pose, car, anim);
        else if (Mode == 5) PresentTv(pose, car);
        else if (Mode == 8) PresentFixed(pose, car);
        else if (Mode == 6) PresentTrack(pose, car);
        else Present(pose, car, anim);
    }

    public void Update(RenderPose pose, CarView car, double dt, CarAnim? anim = null, double? raceTick = null)
    {
        SetIntroTime(raceTick);
        if (!_initialized) { Snap(pose, car, anim); return; }
        Observe(car);
        if (Mode == StartAnimationMode)
        {
            if (!Detached(pose, car, anim, Math.Max(0, dt) * 32)) PresentIntro(pose, car, anim);
            return;
        }
        if (dt <= 0) return;
        double ticks = dt * 32;
        double cameraTick = raceTick ?? (_orbitTickInitialized ? _orbitLastTick + ticks * 2 : ticks * 2);
        double orbitDelta = Mode == 4 && _orbitTickInitialized ? Math.Max(0, cameraTick - _orbitLastTick) : 0;
        if (Mode == 4) { _orbitLastTick = cameraTick; _orbitTickInitialized = true; }
        // clock: same rule as the orbit clock (no raceTick → cumulative dt·64), the elapsed ticks are taken below. [ref:1381]
        double trackNow = raceTick ?? (_trackTickInitialized ? _trackLastTick + ticks * 2 : ticks * 2);
        double trackDelta = Mode == 6 && _trackTickInitialized ? Math.Max(0, trackNow - _trackLastTick) : 0;
        if (Mode == 6) { _trackLastTick = trackNow; _trackTickInitialized = true; }
        if (Detached(pose, car, anim, ticks)) return;
        if (Mode == 5) { PresentTv(pose, car); return; }
        if (Mode == FinishMode)
        {
            _finishTicks += ticks;
            PresentFinish(pose, car);
            return;
        }
        // up·n < 0xE666 → for 100 camera updates (mode 7 as well). [ref:1382]
        if (Mode is 2 or 3 or 4 or 7)
        {
            if (car.UpDotN < 0xE666)
            {
                if (_flipTicks <= 0) _flipBackwards = car.WrongWayTicks > 0;
                _flipTicks = 100;
            }
            if (_flipTicks > 0)
            {
                ChaseCommon(car, Mode == 4 ? orbitDelta : ticks);
                Present(pose, car, anim);
                return;
            }
        }
        if (Mode == 4)
        {
            AdvanceOrbit(orbitDelta);
            PresentOrbit(pose, car, anim);
            return;
        }
        if (Mode == 6)
        {
            AdvanceTrack(trackDelta, car);
            PresentTrack(pose, car);
            return;
        }
        _orbitTickInitialized = false;
        if (Mode == 8) { PresentFixed(pose, car); return; }
        // the rigid car-relative offset is recomputed every update (B flips z); no smoothing and no rear path. [ref:1383]
        if (Mode == 7) { _worldOffset = Mode7Offset(pose); Present(pose, car, anim); return; }
        // rear view is an immediate temporary offset; [ref:1384]
        // the retained forward chase offset must not fly through the car on release.
        if (LookingBehind) { Present(pose, car, anim); return; }
        var target = TargetOffset(pose, car);
        if (Mode < 2) _worldOffset = target;
        else
        {
            // far camera starts with 50 slow 32 Hz updates, then 0x3333. [ref:1385]
            // near camera always uses 0x1999. Smooth the relative WORLD offset [ref:1386]
            // so straight-line car motion is followed immediately rather than lagging behind.
            double slow = Math.Min(dt, _slowTime);
            double fast = dt - slow;
            double retention = Math.Pow(1 - 0x1999 / 65536.0, slow * 32) *
                Math.Pow(1 - (Mode == 2 ? 0x3333 : 0x1999) / 65536.0, fast * 32);
            _worldOffset = Vector3.Lerp(_worldOffset, target, (float)(1 - retention));
            _slowTime = Math.Max(0, _slowTime - dt);
        }
        Present(pose, car, anim);
    }

    void AdvanceOrbit(double cameraDelta)
    {
        _orbitSubticks += cameraDelta;
        _orbitPhase += (int)Math.Floor((_orbitSubticks + 1e-9) / 3);
        _orbitSubticks %= 3;
    }

    void PresentOrbit(RenderPose pose, CarView car, CarAnim? anim)
    {
        FxMath.SinCosTab(_orbitPhase, out int sin, out int cos);
        FxMath.SinCosTab(_orbitPhase * 2 / 3, out int heightSin, out _);
        var offset = pose.Right * (cos / 65536f * 6) + pose.Forward * (sin / 65536f * 6) +
            pose.Up * ((2 * 65536 + heightSin) / 65536f);
        _worldOffset = offset;
        Present(pose, car, anim);
    }

    /// <summary>offset (3 m, 1.3 m, −3.9 m) in the car axes; the rear look (B) negates z. [ref:1387]</summary>
    Vector3 Mode7Offset(RenderPose pose)
    {
        int z = LookingBehind ? -_mode7Offset.Z : _mode7Offset.Z;
        return pose.Right * (_mode7Offset.X / 65536f) + pose.Up * (_mode7Offset.Y / 65536f) + pose.Forward * (z / 65536f);
    }

    /// <summary>offset (0, 15 m, −2 m) in WORLD axes, independent of the car heading. No track [ref:1388]
    /// correction (flag +0xF4 = 0); still locates the camera for the next TV frame. Look-at car, pitch 0. [ref:1389]</summary>
    void PresentFixed(RenderPose pose, CarView car)
    {
        Position = pose.Position + RaceScene.ToWorld(_mode8Offset);
        _corrector?.Locate(Position);
        AimAtCar(pose);
    }

    /// <summary>Slices used by the track-following views: the locator geometry when present, otherwise the plain slices.</summary>
    TrackSlice[]? TrackSlices => Locator?.Geometry.Slices ?? Slices;

    static int WrapSlice(int slice, TrackSlice[]? slices) => slices is { Length: > 0 } s ? ((slice % s.Length) + s.Length) % s.Length : slice;

    /// <summary>Entry (called when the next mode is 6): the camera starts 3, 5 or 7 slices ahead of [ref:1390]
    /// the car (3 behind when the car is slower than the first threshold), the speed index is searched in the table. Deviation: the [ref:1391]
    /// search stops at index 6 so it never reads past the table. Counter cleared.</summary>
    void EnterTrack(CarView car)
    {
        int speed = car.AiSpeed;
        int idx = 0;
        while (idx < 6 && _trackEntrySpeeds[idx + 1] < speed) idx++;
        int rem = speed - _trackEntrySpeeds[idx];
        int offset = rem < 0 ? -3 : rem < 0x30000 ? 3 : rem < 0x140000 ? 5 : 7;
        _trackIdx = idx;
        _trackSlice = WrapSlice(car.Slice + offset, TrackSlices);
        _trackCounter = 0;
        _trackSubticks = 0;
    }

    /// <summary>runs once per 64 Hz (a skipped replay step calls only), so [ref:1392] [ref:3404]
    /// one update = one race-clock unit, as for the orbit; a partial tick carries over to the next update. [ref:1393]</summary>
    void AdvanceTrack(double delta, CarView car)
    {
        _trackSubticks += delta;
        int steps = (int)Math.Floor(_trackSubticks + 1e-9);
        _trackSubticks -= steps;
        for (int i = 0; i < steps; i++) TrackTick(car);
    }

    /// <summary>One camera tick (steps 1–2): the counter runs only once the race has started; when it reaches [ref:1394]
    /// the ticks of the current slice, the camera moves one slice forward and may jump (slice relative to the car, sign flip of the TV
    /// orientation). Deviation: the speed index search stops at index 7 so it stays inside the table.</summary>
    void TrackTick(CarView car)
    {
        var slices = TrackSlices;
        if (!RaceStarted) return;
        if (++_trackCounter != _trackTicks[_trackIdx] || slices is not { Length: > 0 }) return;
        _trackCounter = 0;
        int carSlice = car.Slice, speed = car.AiSpeed;
        _trackSlice = WrapSlice(_trackSlice + 1, slices);
        int d = carSlice - _trackSlice;
        int r = speed - _trackSpeeds[_trackIdx];
        int jump = d >= 8 || (d >= 5 && r is >= 1 and <= 0x4FFFF) ? 1
            : d <= -7 || (d <= -5 && r is >= -0x4FFFF and <= -1) ? 2 : 0;
        if (jump == 0) return;
        int idx = 0;
        while (idx < 7 && _trackSpeeds[idx] < speed) idx++;
        _trackIdx = idx;
        int r2 = speed - _trackSpeeds[idx];
        int offset = jump == 1 ? (r2 < 0x30000 ? 3 : r2 < 0xF0000 ? 5 : 7) : (r2 < -0x7FFFF ? -5 : -3);
        _trackSlice = WrapSlice(carSlice + offset, slices);
        _tvOrientation = -_tvOrientation;
    }

    /// <summary>position: the slice centre interpolated from the camera slice to the next one (step (next − slice) / K [ref:1395]
    /// integer division as in C), the road-side offset along the car slice Right when the camera or the car sits next to a mask gap, and a
    /// height of 5 m minus the distance to the car (capped at 3.5 m, so never below 1.5 m). Look-at the car, pitch 0, no track correction
    /// of the position (flag +0xF4 = 0).</summary>
    void PresentTrack(RenderPose pose, CarView car)
    {
        var slices = TrackSlices;
        if (slices is not { Length: > 0 })
        {
            Position = pose.Position + Vector3.UnitY * TvHeight;
            AimAtCar(pose);
            return;
        }
        int n = slices.Length;
        var s0 = slices[_trackSlice];
        var s1 = slices[(_trackSlice + 1) % n];
        int k = _trackTicks[_trackIdx];
        int x = (s1.X - s0.X) / k * _trackCounter + s0.X;
        int y = (s1.Y - s0.Y) / k * _trackCounter + s0.Y;
        int z = (s1.Z - s0.Z) / k * _trackCounter + s0.Z;

        // tests the mask of the retained camera locator (cam+0x60) and of the car slice, as the TV view does. [ref:1396]
        int carSlice = WrapSlice(car.Slice, slices);
        int located = TvLocatorSlice;
        int cameraSlice = located >= 0 && located < n ? located : carSlice;
        if (HasMaskGap(slices[cameraSlice].Mask) || HasMaskGap(slices[carSlice].Mask))
        {
            var edges = slices[carSlice];
            // Edges × 256 → 16.16 and halved (× 128); the original multiplies by the Right vector (×512) with a plain >> 16.
            int offset = car.LateralOffset < 0 ? -(edges.LeftEdge * 128) : edges.RightEdge * 128;
            x += (int)(((long)offset * (edges.Right[0] << 9)) >> 16);
            y += (int)(((long)offset * (edges.Right[1] << 9)) >> 16);
            z += (int)(((long)offset * (edges.Right[2] << 9)) >> 16);
        }

        // Height: distance octagon of the car (pose) and the camera, the original 16.16 arithmetic; v/4 then cap 0x38000.
        var carFixed = CameraTrackCorrector.ToFixed(pose.Position);
        int ax = Math.Abs(carFixed.X - x), az = Math.Abs(carFixed.Z - z);
        int v = (Math.Max(ax, az) + (Math.Min(ax, az) >> 2)) / 4;
        if (v > 0x37FFF) v = 0x38000;
        y += 0x50000 - v;

        Position = new Vector3(x, y, z) / 65536f;
        _corrector?.Locate(Position); // locates the camera for the next frame (no correction applied). [ref:1397]
        AimAtCar(pose);
    }

    /// <summary>Look-at flag (+0x104) with pitch 0: the view points at the car and stays upright. [ref:1398]</summary>
    void AimAtCar(RenderPose pose)
    {
        Vector3 forward = Vector3.Normalize(pose.Position - Position);
        Vector3 upHint = LookHint;
        if (MathF.Abs(Vector3.Dot(upHint, forward)) > 0.99f) upHint = pose.Up;
        Vector3 right = Vector3.Normalize(Vector3.Cross(upHint, forward));
        Forward = forward;
        Up = Vector3.Cross(forward, right);
        _presented = true;
    }

    /// <summary>A station every 30 slices, 3 m above the road. [ref:1399]</summary>
    void PresentTv(RenderPose pose, CarView car)
    {
        var slices = Locator?.Geometry.Slices ?? Slices;
        if (slices is not { Length: > 0 })
        {
            Position = pose.Position + Vector3.UnitY * 3;
            LookAtTv(pose.Position);
            _tvDistance = TvDistance(Position, pose.Position);
            return;
        }

        int count = slices.Length;
        int current = ((car.Slice % count) + count) % count;
        // first adds half the configured spacing, wraps once, then rounds down to a station. [ref:1400]
        int station = (current + 15) % count;
        station -= station % 30;
        if (station != _tvStation)
        {
            _tvOrientation = -_tvOrientation;
            _tvZoom = !_tvZoom;
        }
        _tvStation = station;

        // the mask test reads the camera's retained locator slice (cam+0x60), then the car slice. [ref:1401]
        // Without a locator the retained slice is the car slice.
        int located = TvLocatorSlice;
        int cameraSlice = located >= 0 && located < count ? located : current;
        bool directionChange = HasMaskGap(slices[cameraSlice].Mask) || HasMaskGap(slices[current].Mask);
        _tvSide = !directionChange || car.LateralOffset >= 0;

        // lateral along slice Right (+0x12) from Lanes × HalfWidth, height along Up (+0xC) × 3 m. [ref:1402]
        var s = slices[station];
        int laneCount = _tvSide ? s.Lanes & 0x0f : (s.Lanes >> 4) & 0x0f;
        int halfWidth = _tvSide ? s.HalfWidthRight : s.HalfWidthLeft;
        int lateralOffset = unchecked(halfWidth * 0x8000 * laneCount);
        int sign = _tvSide ? 1 : -1;
        int x = s.X + sign * Fx.Mul(lateralOffset, s.Right[0] << 9) + (s.Up[0] << 9) * TvHeight;
        int y = s.Y + sign * Fx.Mul(lateralOffset, s.Right[1] << 9) + (s.Up[1] << 9) * TvHeight;
        int z = s.Z + sign * Fx.Mul(lateralOffset, s.Right[2] << 9) + (s.Up[2] << 9) * TvHeight;
        Position = new Vector3(x, y, z) / 65536f;
        // The camera position goes through the locator like any other view, so the next frame sees its slice. [ref:1403]
        _corrector?.Locate(Position);
        LookAtTv(pose.Position);
        _tvDistance = TvDistance(Position, pose.Position);
    }

    /// <summary>(3, 0x1E): the station is 3 m above the road. [ref:1404]</summary>
    const int TvHeight = 3;

    /// <summary>true when the set bits of a slice mask are not one run of adjacent bits (a gap, e.g. 0b101). [ref:1405]
    /// The walk mirrors the original: each step checks that the next lowest set bit is the next bit above the previous one.</summary>
    static bool HasMaskGap(ushort mask)
    {
        int rest = (mask - 1) & mask;
        int step = mask - rest;
        for (int p = mask; p != 0; )
        {
            if (p - rest != step) return true;
            step = (ushort)(step << 1);
            p = rest;
            rest &= rest - 1;
        }
        return false;
    }

    /// <summary>octagonal distance in 16.16 units, including the original arithmetic shifts. [ref:1406]</summary>
    static int TvDistance(Vector3 a, Vector3 b)
    {
        int dx = Math.Abs((int)(a.X * Fx.One) - (int)(b.X * Fx.One));
        int dy = Math.Abs((int)(a.Y * Fx.One) - (int)(b.Y * Fx.One));
        int dz = Math.Abs((int)(a.Z * Fx.One) - (int)(b.Z * Fx.One));
        int s = Math.Max(dx, dy) + (Math.Min(dx, dy) >> 2);
        return dz < s ? s + (dz >> 2) : (s >> 2) + dz;
    }

    void LookAtTv(Vector3 target)
    {
        Vector3 forward = Vector3.Normalize(target - Position);
        Vector3 hint = MathF.Abs(Vector3.Dot(Vector3.UnitY, forward)) > 0.99f ? Vector3.UnitZ : LookHint;
        Vector3 right = Vector3.Normalize(Vector3.Cross(hint, forward));
        Forward = forward;
        // The replay look-at reads camera field +0x50 only in the hint X term; the normal race keeps Up upright.
        Up = Vector3.Cross(forward, right);
        _presented = true;
    }

    (int Fx, int Fy) TvProjection(int width, int height)
    {
        // panorama (Hor+): the horizontal focal length stays that of the 4:3 frame of this height
        int halfWidth = (NFS.Render.Camera.PanoramaBase(width, height) is { } bw ? (int)bw : width) / 2, halfHeight = height / 2;
        FxMath.SinCosTab((45 << 10) / 360, out int sx, out int cx);
        FxMath.SinCosTab((36 << 10) / 360, out int sy, out int cy);
        int fx = halfWidth * cx / sx, fy = -(halfHeight * cy / sy);
        if (!_tvZoom || _tvDistance <= 0x60000) return (fx, fy);
        int d = _tvDistance;
        fx = unchecked(halfWidth * d) / 0x60000;
        int vertical = unchecked(halfHeight * d) / 0x60000;
        fy = -Fx.Mul(vertical, 0x1592C);
        return (fx, fy);
    }

    void SetIntroTime(double? raceTick)
    {
        if (raceTick is { } tick) _startTicks = Math.Max(0, tick - _startRaceTick);
    }

    int IntroReturnMode() => _startReturnMode == 0 && (Split || _carType > 11)
        ? NextMode(0, _carType, Split) : _startReturnMode;

    /// <summary>the local player finishing (+0x280 == 2) switches to mode 0xC once. [ref:1407]</summary>
    void Observe(CarView car)
    {
        if (_finishStarted || car.RaceState != 2 || (car.Flags & CarFlags.LocalHuman) == 0 || FinishAnimation is not { Keys.Count: > 0 }) return;
        _finishStarted = true;
        if (!Split && Mode < 4) _startReturnMode = Mode;
        Mode = FinishMode;
        _finishTicks = 0;
        _worldOffset = Vector3.Zero;
        _slowTime = 50.0 / 32;
    }

    /// <summary>Special views take precedence over the selected mode; true = handled. [ref:1408]</summary>
    bool Detached(RenderPose pose, CarView car, CarAnim? anim, double ticks)
    {
        bool wasCrashing = _crashing;
        _crashing = car.CrashSlice > 0;
        _orbit = !_crashing && car.ManualResetLock != 0;
        if (_crashing)
        {
            if (!wasCrashing && _presented) _crashPos = Position;
            CrashStep(pose, car, ticks);
            _corrector?.Locate(Position); // locates every presented view, including crash drift. [ref:1409]
            return true;
        }
        if (_orbit)
        {
            // angle (lock·256/0x30 − 0x100) in 1/1024 turns, offset (±cos·6, 2, sin·6) in the car frame (± = slice parity). [ref:1410]
            FxMath.SinCosTab(car.ManualResetLock * 256 / 0x30 - 0x100, out int sin, out int cos);
            int side = (car.Slice & 1) == 0 ? -1 : 1;
            int x = (int)((long)(cos * side) * 0x60000 >> 16), z = (int)((long)sin * 0x60000 >> 16);
            _worldOffset = pose.Right * (x / 65536f) + pose.Up * (0x20000 / 65536f) + pose.Forward * (z / 65536f);
            _slowTime = 50.0 / 32;
            Present(pose, car, anim);
            return true;
        }
        return false;
    }

    /// <summary>octagonal distance approximation (16.16 in the original; metres here). [ref:1411]</summary>
    static float Distance(Vector3 a, Vector3 b)
    {
        float dx = MathF.Abs(a.X - b.X), dy = MathF.Abs(a.Y - b.Y), dz = MathF.Abs(a.Z - b.Z);
        float s = MathF.Max(dx, dy) + MathF.Min(dx, dy) / 4;
        return dz < s ? s + dz / 4 : s / 4 + dz;
    }

    /// <summary>the camera stays where it was and drifts 0xCCC/65536 per tick towards the car (+4 m in y), as long as [ref:1412]
    /// the new spot is inside the road edges of the car slice; a car further than 150 m (0x960000) is ignored.</summary>
    void CrashStep(RenderPose pose, CarView car, double ticks)
    {
        if (Distance(pose.Position, _crashPos) > 0x960000 / 65536f) return;
        float f = (float)(1 - Math.Pow(1 - 0xCCC / 65536.0, ticks));
        var next = _crashPos + new Vector3(pose.Position.X - _crashPos.X, pose.Position.Y - _crashPos.Y + 4, pose.Position.Z - _crashPos.Z) * f;
        bool inside = true;
        if (Slices is { } slices && (uint)car.Slice < (uint)slices.Length)
        {
            var sl = slices[car.Slice];
            var centre = new Vector3(sl.X, sl.Y, sl.Z) / 65536f;
            var right = new Vector3(sl.Right[0], sl.Right[1], sl.Right[2]) / 128f;
            float lateral = Vector3.Dot(next - centre, right);
            inside = -sl.LeftEdge / 256f < lateral && lateral < sl.RightEdge / 256f;
        }
        if (inside) _crashPos = next;
        Position = _crashPos;
        Forward = Vector3.Normalize(pose.Position - _crashPos);
        Vector3 r = Vector3.Normalize(Vector3.Cross(LookHint, Forward));
        Up = Vector3.Cross(Forward, r);
        _presented = true;
    }

    /// <summary>the offset eases (0x51E/65536 per tick) to the vector in the road frame [ref:1413]
    /// (+0x118), z negated when the flip started against the track direction; once the car is released from the
    /// backwards state the camera cuts to 8 units behind the car heading and the counter ends.</summary>
    void ChaseCommon(CarView car, double ticks)
    {
        var v = FromInts(CameraConstants.ChaseOffset);
        if (_flipBackwards != TrackReverse) v = new Vec3(v.X, v.Y, -v.Z);
        var road = car.RoadFrame;
        var target = RaceScene.ToWorld(road.Row0) * (v.X / 65536f) + RaceScene.ToWorld(road.Row1) * (v.Y / 65536f) +
            RaceScene.ToWorld(road.Row2) * (v.Z / 65536f);
        float f = (float)(1 - Math.Pow(1 - 0x51E / 65536.0, ticks));
        _worldOffset = Vector3.Lerp(_worldOffset, target, f);
        _flipTicks = Math.Max(0, _flipTicks - ticks);
        if (_flipBackwards && car.WrongWayTicks == 0)
        {
            _worldOffset = new Vector3(car.Orient.M20 / -8192f, 1, car.Orient.M22 / -8192f);
            _flipTicks = 0;
        }
        if (_flipTicks <= 0) _slowTime = 50.0 / 32;
    }

    /// <summary>car position + CAN position, orientation = CAN matrix (flag 1 swaps [ref:1414]
    /// the axes); after the last key the final key is held. [ref:1415]</summary>
    void PresentFinish(RenderPose pose, CarView car)
    {
        var can = FinishAnimation!;
        int tpk = can.TicksPerKey, seg = can.Keys.Count - 1;
        int k = seg > 0 ? (int)Math.Min(seg, _finishTicks / tpk) : 0;
        int[] m; int x, y, z;
        if (k >= seg)
        {
            var key = can.Keys[^1];
            m = KnockedObjectPose.QuatToMatrix(key.Qx, key.Qy, key.Qz, key.Qw);
            (x, y, z) = (key.X, key.Y, key.Z);
        }
        else (m, x, y, z) = KnockedObjectPose.Eval(can.Keys[k], can.Keys[k + 1], (int)((_finishTicks - k * tpk) / tpk * 65536));
        // rows 1 and 2 swap: new row 1 = old row 2, new row 2 = -old row 1.
        Position = pose.Position + RaceScene.ToWorld(new Vec3(x, y, z));
        Forward = Vector3.Normalize(RaceScene.ToWorld(new Vec3(-m[3], -m[4], -m[5])));
        Up = Vector3.Normalize(RaceScene.ToWorld(new Vec3(m[6], m[7], m[8])));
        _presented = true;
        // sets the look-at flag of mode 0xC when it moved the camera: then aims at the car. [ref:1416]
        if (CorrectForTrack(pose, car) && Distance(pose.Position, Position) <= 0x960000 / 65536f)
        {
            Forward = Vector3.Normalize(pose.Position - Position);
            Vector3 upHint = LookHint;
            if (MathF.Abs(Vector3.Dot(upHint, Forward)) > 0.99f) upHint = Up;
            Vector3 r = Vector3.Normalize(Vector3.Cross(upHint, Forward));
            Up = Vector3.Cross(Forward, r);
        }
    }

    /// <summary>start CAN position is added to the car pose; slots 4/5 are car-relative xforms. [ref:1417]</summary>
    void PresentIntro(RenderPose pose, CarView car, CarAnim? anim)
    {
        if (_startTicks >= (_startAnimation!.Keys.Count - 1) * _startAnimation.TicksPerKey)
        {
            Mode = IntroReturnMode();
            _worldOffset = TargetOffset(pose, car);
            _slowTime = 50.0 / 32;
            Present(pose, car, anim);
            return;
        }
        var can = _startAnimation!;
        int tpk = can.TicksPerKey;
        int seg = Math.Min(can.Keys.Count - 2, (int)(_startTicks / tpk));
        int frac = (int)((_startTicks - seg * tpk) / tpk * 65536);
        var (m, x, y, z) = KnockedObjectPose.Eval(can.Keys[seg], can.Keys[seg + 1], frac);
        bool carRelative = _startAnimationIndex is 4 or 5;
        // reads CarObj +0x118 (RoadFrame), not +0xC4 (Orient). [ref:1418]
        var right = RaceScene.ToWorld(car.RoadFrame.Row0);
        var up = RaceScene.ToWorld(car.RoadFrame.Row1);
        var forward = RaceScene.ToWorld(car.RoadFrame.Row2);
        Vector3 PositionOf(int a, int b, int c) => carRelative
            ? pose.Position + right * (a / 65536f) + up * (b / 65536f) + forward * (c / 65536f)
            : pose.Position + RaceScene.ToWorld(new Vec3(a, b, c));
        Vector3 DirectionOf(int a, int b, int c) => carRelative
            ? right * (a / 65536f) + up * (b / 65536f) + forward * (c / 65536f)
            : RaceScene.ToWorld(new Vec3(a, b, c));
        Position = PositionOf(x, y, z);
        Forward = Vector3.Normalize(DirectionOf(-m[3], -m[4], -m[5]));
        Up = Vector3.Normalize(DirectionOf(m[6], m[7], m[8]));
        _presented = true;
    }

    /// <summary>(0x17,0), active for as long as B is held. [ref:1419]</summary>
    public void SetLookBehind(bool held, RenderPose pose, CarView car, CarAnim? anim = null)
    {
        if (LookingBehind == held) return;
        LookingBehind = held;
        // The TV view (mode 5) has no rear look: checks the rear view for mode 0 only and never repositions the TV camera. [ref:1420]
        // Fixed mode 8 ignores it too; mode 7 keeps its car offset and flips z (recomputed here, not by the rear path).
        if (Mode is FinishMode or StartAnimationMode or 5 or 6 or 8 || _crashing || _orbit) return;
        if (Mode == 7 && !Flipping) _worldOffset = Mode7Offset(pose);
        Present(pose, car, anim);
    }

    /// <summary>for every view of this class (flag +0x30DC), unless the [ref:1421]
    /// selected mode is 0 or 1. Returns true when it moved the position. [ref:1422]</summary>
    bool CorrectForTrack(RenderPose pose, CarView car)
    {
        if (_corrector == null || (uint)car.Slice >= (uint)_locator!.Geometry.NumSlices) return false;
        var position = Position;
        bool moved = _corrector.Correct(ref position, pose.Position, car.Slice, Mode >= 2);
        Position = position;
        return moved;
    }

    Vector3 TargetOffset(RenderPose pose, CarView car, bool rear = false)
    {
        var offset = _offsets[Math.Min(Mode, 3)];
        int y = offset.Y, z = offset.Z;
        if (Mode == 3) y += _carHeight;
        if (Mode < 2) y = Fx.Mul(y, car.UpDotN);
        else
        {
            if (Mode == 2)
            {
                int vx = Fx.Abs(car.LocalVel.X), vz = Fx.Abs(car.LocalVel.Z);
                z -= Math.Min(6 * Fx.One, (Math.Max(vx, vz) + (Math.Min(vx, vz) >> 2)) / 20);
            }
            if (_carType >= 15)
            {
                y += car.HalfHeight / 2;
                z -= car.HalfLength / 2;
            }
        }
        if (rear) z = -z;
        return pose.Right * (offset.X / 65536f) + pose.Up * (y / 65536f) + pose.Forward * (z / 65536f);
    }

    void Present(RenderPose pose, CarView car, CarAnim? anim)
    {
        // The rear view is ignored by the crash, reset and flip cameras (they return before the check). [ref:1423]
        bool rear = Mode is not (4 or 7 or 8) && LookingBehind && !_crashing && !_orbit && !Flipping;
        var offset = rear ? TargetOffset(pose, car, rear: true) : _worldOffset;
        Position = pose.Position + offset;
        bool corrected = CorrectForTrack(pose, car) && Position != pose.Position + offset;
        _presented = true;
        // reverses Right and Forward, preserving Up. [ref:1424]
        Vector3 forward = InCar ? Vector3.Normalize(pose.Forward) * (rear ? -1 : 1) : Vector3.Normalize(corrected ? pose.Position - Position : -offset);
        Vector3 upHint = InCar ? pose.Up : LookHint;
        // uses the previous up vector near vertical to avoid a degenerate cross product. [ref:1425]
        if (MathF.Abs(Vector3.Dot(upHint, forward)) > 0.99f) upHint = pose.Up;
        Vector3 right = Vector3.Normalize(Vector3.Cross(upHint, forward));
        Vector3 up = Vector3.Cross(forward, right);
        // Camera pitch angles use the original 16-bit full-turn convention.
        int bodyPitch = anim?.Pitch ?? 0;
        // Some views keep their pitch flag (+0x4C) at 0; the reset orbit [ref:1426]
        // and the flip fallback (writes 0x2A) keep their own pitch. [ref:1427]
        int pitch = Mode is 4 or 7 && !_orbit && !Flipping ? 0 : !InCar ? 0x2A : Mode == 1 ? -0x38E + bodyPitch : bodyPitch * 2;
        float angle = pitch * (2 * MathF.PI / 65536);
        Forward = forward * MathF.Cos(angle) + up * (MathF.Sin(angle) * (InCar ? -1 : 1));
        Up = Vector3.Cross(Forward, right);
        if (InCar)
        {
            int roll = anim?.Roll ?? 0;
            if (Mode == 1) roll >>= 1;
            if (car.DistToPlayer > 0x3C0000) roll >>= 1;
            float rollAngle = roll * (2 * MathF.PI / 65536);
            Up = Up * MathF.Cos(rollAngle) - right * MathF.Sin(rollAngle);
        }
    }

    public Camera Camera(int width = 640, int height = 480)
    {
        if (Split)
        {
            // in split screen the same focal length as full screen, centre in the middle of the 640×240 half (SplitLayout.CreateCamera) [ref:1428]
            // the distance zoom is not applied in split: the callback restores FOV 90 through this branch. [ref:1429]
            var half = SplitLayout.CreateCamera(Math.Max(1, height / 480), width);
            half.Position = Position;
            half.LookAlong(Forward, Up);
            return half;
        }
        // reports rear cockpit as mode9, without dashboard projection offset. [ref:1430]
        Camera camera;
        if (TvActive)
        {
            var (fx, fy) = TvProjection(width, height);
            camera = new Camera(width, height, focalX: fx, focalY: fy, baseWidth: NFS.Render.Camera.PanoramaBase(width, height));
        }
        else camera = new Camera(width, height, 45, 36, InCar && Mode == 0 && !LookingBehind ? -height / 12 : 0, baseWidth: NFS.Render.Camera.PanoramaBase(width, height));
        camera.Position = Position;
        camera.LookAlong(Forward, Up);
        return camera;
    }

    /// <summary>view1: current main position, raw rear car basis without body animation. [ref:1431]</summary>
    /// <param name="scale">Enhanced: the strip is rendered 640K x 70K with the same angles (K = scale).</param>
    public Camera MirrorCamera(RenderPose pose, int scale = 1)
    {
        // x=-150,y=-70,w=450,h=210, horizontal half-angle 68 [ref:1432]
        // vertical half-angle=(68*13)/16=55. Projection uses integer sine-table quotients.
        FxMath.SinCosTab((68 << 10) / 360, out int sx, out int cx);
        FxMath.SinCosTab((55 << 10) / 360, out int sy, out int cy);
        int fx = 225 * cx / sx, fy = -105 * cy / sy;
        var camera = new Camera(640 * scale, 70 * scale, centreXOffset: (75 - 320) * scale, focalX: fx * scale, focalY: fy * scale);
        camera.Position = Position;
        camera.LookAlong(-pose.Forward, pose.Up);
        return camera;
    }
}
