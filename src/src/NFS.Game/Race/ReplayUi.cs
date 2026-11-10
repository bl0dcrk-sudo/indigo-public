using NFS.Render;

namespace NFS.Game.Race;

/// <summary>Keys of the replay strip (3 = I_LEFT, 4 = I_RIGHT, 5 = I_SELECT, as in the original). [ref:2355]</summary>
public enum ReplayUiKey { Left, Right, Select }

/// <summary>Camera chosen with the camera icon (case 3 / the next call): the mode argument [ref:2356]
/// <see cref="ReplayUi.AutoCameraMode"/> = the replay camera director.</summary>
public readonly record struct ReplayUiCamera(int Mode, bool Auto);

/// <summary>Command model of the replay strip, 1:1 with the original, plus the commit in [ref:2357]
/// (<c>4</c>). Pure state, no input devices or rendering: the presentation feeds <see cref="Press"/> and calls [ref:2358]
/// <see cref="Tick"/> once per 32 Hz input tick.
/// Slots (<see cref="Selected"/>; Left/Right move without wrapping): 0 pause, 1 speed (cycles ¼×, ½×, 1×, 2×, in the order [ref:2359]
/// of the index 0..3; starts at 2), 2 eject (leaves the replay), 3 camera (cycles <see cref="CameraModes"/>; index 0 = director).
/// A command locks the strip for 8 ticks (24 for eject) and is committed when the counter reaches 4 (pause/eject take effect then;
/// speed and the camera icon change at once). There is no car selection in the original: the camera follows the human's car.</summary>
public sealed class ReplayUi
{
    /// <summary>Seconds per input tick (32 Hz). [ref:2360]</summary>
    public const double TickSeconds = 1.0 / 32;
    /// <summary>Camera mode of each camera icon index; 10 = automatic (replay camera director). [ref:2361]</summary>
    public static readonly int[] CameraModes = { 10, 4, 6, 5, 7, 2, 3, 1, 0 };
    public const int AutoCameraMode = 10;
    public const int CommandCount = 4;
    /// <summary>The original starts the speed at index 2 (1×). [ref:2362]</summary>
    public const int InitialSpeedIndex = 2;

    readonly bool _split;
    readonly int _cameraSlots;
    bool _pendingLeft, _pendingRight, _pendingSelect;
    bool _cameraApply;       // apply the chosen camera on the next tick [ref:2363]
    int _appliedCamera;      // [ref:2364]
    bool _eject;             // [ref:2365]

    /// <param name="carType">Model of the human car (<c>**(car+0x21C)</c>): models above 14 have no icon 8 (cockpit).</param>
    /// <param name="split">1: speed cycles through 3 values and only 8 camera icons. [ref:2366]</param>
    public ReplayUi(int carType, bool split = false)
    {
        _split = split;
        _cameraSlots = split || carType > 14 ? 8 : 9;
    }

    /// <summary>Selected command (0..3). [ref:2367]</summary>
    public int Selected { get; private set; }
    /// <summary>Press counter: non-zero while a command is locked in (the button is drawn pressed). [ref:2368]</summary>
    public int PressTicks { get; private set; }
    /// <summary>The pause flag shown by the strip, bit 0 (set at once by the pause command). [ref:2369]</summary>
    public bool PauseFlag { get; private set; }
    /// <summary>The pause actually in force (follows <see cref="PauseFlag"/> when the command is committed). [ref:2370]</summary>
    public bool Paused { get; private set; }
    /// <summary>Speed index 0..3 (¼×, ½×, 1×, 2×); takes effect at once. [ref:2371]</summary>
    public int SpeedIndex { get; private set; } = InitialSpeedIndex;
    /// <summary>Camera icon index. [ref:2372]</summary>
    public int CameraIndex { get; private set; }
    /// <summary>Eject was committed (1): the replay is left. [ref:2373]</summary>
    public bool EjectRequested { get; private set; }
    /// <summary>The camera director runs (camera icon index 0, the original's initial state). [ref:2374]</summary>
    public bool AutoCamera => CameraModes[CameraIndex] == AutoCameraMode;

    /// <summary>Playback speed multiplier of <see cref="SpeedIndex"/> (skips or doubles simulation steps). [ref:2375]</summary>
    public double SpeedMultiplier => SpeedIndex switch { 0 => 0.25, 1 => 0.5, 2 => 1, _ => 2 };

    /// <summary>A key edge ((n, 1)); consumed by the next <see cref="Tick"/>, dropped there while a command is locked in. [ref:2376]</summary>
    public void Press(ReplayUiKey key)
    {
        switch (key)
        {
            case ReplayUiKey.Left: _pendingLeft = true; break;
            case ReplayUiKey.Right: _pendingRight = true; break;
            default: _pendingSelect = true; break;
        }
    }

    /// <summary>Selects the camera command and activates it (what Left/Right + Select on the camera icon does); a port convenience for
    /// the camera key, which does nothing during a replay in the original (the camera call only applies when the replay state is [ref:2377]
    /// &lt; 2). Not subject to the strip lock. [ref:2378]</summary>
    public ReplayUiCamera? CycleCamera()
    {
        Selected = CommandCount - 1;
        CameraIndex = (CameraIndex + 1) % _cameraSlots;
        _appliedCamera = CameraIndex;
        _cameraApply = false;
        return CurrentCamera();
    }

    ReplayUiCamera CurrentCamera() => new(CameraModes[CameraIndex], CameraIndex == 0);

    /// <summary>One input tick, followed by the commit of the locked command. <paramref name="cameraMode"/> is the mode of [ref:2379]
    /// the camera now. Returns the camera to apply, if the camera command was used on the previous tick. [ref:2380]</summary>
    public ReplayUiCamera? Tick(int cameraMode = -1)
    {
        ReplayUiCamera? camera = null;
        // camera icon 3 (mode 5) while the camera sits in mode 7 is moved to icon 4 (mode 7). [ref:2381]
        if (CameraModes[CameraIndex] == 5 && cameraMode == 7) CameraIndex = 4;
        if (_appliedCamera > 0 && _cameraApply)
        {
            CameraIndex = _appliedCamera;
            camera = CurrentCamera();
        }
        _cameraApply = false;
        if (PressTicks > 0)
        {
            PressTicks--;
            Drop();
        }
        else if (_pendingLeft)
        {
            if (Selected > 0) Selected--;
            Drop();
        }
        else if (_pendingRight)
        {
            if (Selected < CommandCount - 1) Selected++;
            Drop();
        }
        else if (_pendingSelect)
        {
            Drop();
            camera ??= Activate();
        }
        if (PressTicks == 4) Commit();
        return camera;
    }

    void Drop() => _pendingLeft = _pendingRight = _pendingSelect = false;

    /// <summary>Camera switch: selecting the director (icon 0) takes effect in the same call; other cameras apply on the next tick. [ref:2382]</summary>
    ReplayUiCamera? Activate()
    {
        PressTicks = 8;
        switch (Selected)
        {
            case 0: PauseFlag = !PauseFlag; break;
            case 1: SpeedIndex = (SpeedIndex + 1) % (_split ? 3 : 4); break;
            case 2: _eject = true; PressTicks = 0x18; break;
            default:
                _cameraApply = true;
                CameraIndex = (CameraIndex + 1) % _cameraSlots;
                _appliedCamera = CameraIndex;
                if (CameraIndex == 0) { _cameraApply = false; return CurrentCamera(); }
                break;
        }
        return null;
    }

    /// <summary><c>4</c> ends the button animation; eject leaves the replay, the pause flag becomes effective. [ref:2383]</summary>
    void Commit()
    {
        PressTicks = 0;
        if (_eject) { EjectRequested = true; return; }
        Paused = PauseFlag;
    }

    /// <summary>The strip as <see cref="ReplayStatusHud"/> draws it.</summary>
    public ReplayStripState Strip(int raceTick64, int replayTicks64)
        => new(Selected, PressTicks != 0, PauseFlag, SpeedIndex, CameraIndex, raceTick64, replayTicks64);
}
