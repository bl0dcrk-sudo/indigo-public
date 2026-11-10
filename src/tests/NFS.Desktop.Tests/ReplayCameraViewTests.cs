using System.Numerics;
using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Data.Track;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Replay view: the camera follows the replay camera director, and its look-at field +0x50 rolls the view [ref:2886]
/// (the x of the up hint (0, 1, 0) is replaced by the field while it equals 2). [ref:2887]</summary>
public sealed class ReplayCameraViewTests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    static CarObj Car() => new() { UpDotN = Fx.One };

    static RaceCamera Camera(int mode)
    {
        var c = new RaceCamera(0);
        while (c.Mode != mode) c.Cycle(Pose, Car());
        c.Snap(Pose, Car());
        return c;
    }

    /// <summary>The look-at basis: right = hint x forward, up = forward x right (hint (tilt, 1, 0)). [ref:2888]</summary>
    static Vector3 ExpectedUp(Vector3 forward, float tilt)
    {
        var right = Vector3.Normalize(Vector3.Cross(new Vector3(tilt, 1, 0), forward));
        return Vector3.Cross(forward, right);
    }

    [GameDataTheory]
    [InlineData(0x547A)]
    [InlineData(-0x5486)]
    public void ChaseCamera_TiltRollsTheViewLikeTheLookAtUpHint(int field50)
    {
        var upright = Camera(2);
        var tilted = Camera(2);
        tilted.ReplayTilt = field50;
        tilted.Snap(Pose, Car());
        Assert.True(Vector3.Distance(upright.Forward, tilted.Forward) < 0.005f); // only the 0x2A pitch (0.23 deg) is applied to the rolled basis
        Assert.True(Vector3.Distance(ExpectedUp(tilted.Forward, field50 / 65536f), tilted.Up) < 0.005f);
        Assert.True(Vector3.Dot(upright.Up, tilted.Up) < 0.99f, "the tilt must visibly roll the view");
        Assert.True(Vector3.Distance(ExpectedUp(upright.Forward, 0), upright.Up) < 0.005f);
    }

    [GameDataFact]
    public void Cockpit_IgnoresTheTilt()
    {
        var plain = Camera(0);
        var tilted = Camera(0);
        tilted.ReplayTilt = 0x547A;
        tilted.Snap(Pose, Car());
        Assert.Equal(plain.Up, tilted.Up);
        Assert.Equal(plain.Forward, tilted.Forward);
    }

    [GameDataFact]
    public void SetReplayMode_TakesTheModeAndIgnoresTheStartAnimationAndUnknownModes()
    {
        var c = Camera(2);
        c.SetReplayMode(7, Pose, Car());
        Assert.Equal(7, c.Mode);
        c.SetReplayMode(6, Pose, Car());
        Assert.Equal(6, c.Mode);
        c.SetReplayMode(9, Pose, Car()); // not a camera mode
        Assert.Equal(6, c.Mode);
        c.SetReplayMode(0, Pose, Car()); // the camera icon of the replay strip asks for the cockpit
        Assert.Equal(0, c.Mode);
    }

    [Fact]
    public void SetReplayMode_CockpitRequestOfACarWithoutCockpitTakesTheNextMode()
    {
        var c = new RaceCamera(12); // car model mode above 11 [ref:2889]
        c.SetReplayMode(0, Pose, Car());
        Assert.Equal(1, c.Mode);
    }

    [Fact]
    public void HudconCamMode_IsWrittenByTheCameraKeyOnlyOutsideReplayAndSplit()
    {
        var race = new RaceCamera(0);
        Assert.Equal(0, race.HudconCamMode);
        race.Cycle(Pose, Car());                 // 2 -> 3
        Assert.Equal(3, race.HudconCamMode);
        race.Cycle(Pose, Car());                 // 3 -> 0
        Assert.Equal(0, race.HudconCamMode);
        race.Cycle(Pose, Car());                 // 0 -> 1
        Assert.Equal(1, race.HudconCamMode);

        var replay = new RaceCamera(0) { RememberCameraKey = false }; // flag != 0 in the original [ref:2890]
        replay.Cycle(Pose, Car());
        Assert.Equal(0, replay.HudconCamMode);
        var split = new RaceCamera(0) { Split = true };                // split flag == 1 in the original [ref:2891]
        split.Cycle(Pose, Car());
        Assert.Equal(0, split.HudconCamMode);
    }

    static Can OneKeyCan()
    {
        var bytes = new byte[28];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 6);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 16384); // identity quaternion
        return Can.Parse(bytes);
    }

    [Theory]
    [InlineData(3, 3)]   // a driving view (< 4) is remembered when the local car finishes [ref:2892]
    [InlineData(1, 1)]
    [InlineData(6, 0)]   // other modes (e.g. the replay director's track camera) leave the value alone
    public void HudconCamMode_IsWrittenWhenTheLocalCarFinishes(int mode, int expected)
    {
        var car = Car();
        car.Flags = CarFlags.LocalHuman;
        var c = new RaceCamera(0) { FinishAnimation = OneKeyCan(), RememberCameraKey = false };
        c.SetReplayMode(mode, Pose, car);
        Assert.Equal(mode, c.Mode);
        car.RaceState = 2;
        c.Update(Pose, car, 1.0 / 32, raceTick: 1);
        Assert.Equal(RaceCamera.FinishMode, c.Mode);
        Assert.Equal(expected, c.HudconCamMode);
    }

    [GameDataFact]
    public void Playback_CameraIconPutsTheChosenModeOnTheCameraAndDirectorIconReturnsToTheDirector()
    {
        var drive = Playback(out _);
        var ui = drive.ReplayUi!;
        for (int guard = 0; drive.Cam.Mode == RaceCamera.StartAnimationMode && guard < 3000; guard++) drive.Frame(1.0 / 32, default);
        for (int i = 0; i < 3; i++) { drive.ReplayPress(ReplayUiKey.Right); drive.Frame(1.0 / 32, default); }
        Assert.Equal(3, ui.Selected);
        var seen = new List<int>();
        for (int press = 0; press < 9; press++)
        {
            drive.ReplayPress(ReplayUiKey.Select);
            for (int f = 0; f < 8; f++) drive.Frame(1.0 / 32, default);
            if (ui.AutoCamera)
            {
                Assert.True(drive.ReplayAutoCamera);
                break;
            }
            Assert.False(drive.ReplayAutoCamera);
            Assert.Equal(0, drive.Cam.ReplayTilt);
            Assert.Equal(ReplayUi.CameraModes[ui.CameraIndex], drive.Cam.Mode);
            seen.Add(drive.Cam.Mode);
        }
        Assert.Equal(new[] { 4, 6, 5, 7, 2, 3, 1, 0 }, seen);
        Assert.True(drive.ReplayAutoCamera);
    }

    [GameDataFact]
    public void Playback_UiCommandsNeverChangeTheSimulation()
    {
        var plain = Playback(out _);
        var used = Playback(out _);
        for (int i = 0; i < 3; i++) { used.ReplayPress(ReplayUiKey.Right); used.Frame(1.0 / 32, default); plain.Frame(1.0 / 32, default); }
        Assert.Equal(3, used.ReplayUi!.Selected);
        for (int frame = 0; frame < 1500; frame++)
        {
            // Every 40 frames press the selected icon (camera icon: all nine cameras, director included).
            if (frame % 40 == 5) used.ReplayPress(ReplayUiKey.Select);
            plain.Frame(1.0 / 32, default);
            used.Frame(1.0 / 32, default);
        }
        Assert.Equal(plain.Runner.Tick, used.Runner.Tick);
        Assert.Equal(plain.Runner.Cars.Select(c => c.Obj.Pos), used.Runner.Cars.Select(c => c.Obj.Pos));
        Assert.Equal(plain.Runner.Cars.Select(c => c.Obj.Orient), used.Runner.Cars.Select(c => c.Obj.Orient));
        Assert.Equal(plain.Runner.Rand.State, used.Runner.Rand.State);          // same draws, same order [ref:2893]
        Assert.Equal(plain.Runner.ReplayCam!.Draws, used.Runner.ReplayCam!.Draws);
        Assert.Equal(2, used.ReplayUi.SpeedIndex);                              // only camera commands were given
        Assert.NotEqual(0, used.ReplayUi.CameraIndex + (used.Cam.ReplayTilt == 0 ? 0 : 1)); // the commands were really used
    }

    static RaceDrive Playback(out ReplayInputSource source)
    {
        string dir = GameFiles.FindGameDir();
        source = new ReplayInputSource(Replay.Parse(GameFiles.ReadRaw("REPLAY.RPY", dir)));
        var session = new RaceLoader(dir).Load(RaceInfo.Setup("replay", dir));
        return new RaceDrive(new RaceScene(session, dir), withAi: true, playbackSource: source);
    }

    [GameDataFact]
    public void LiveRace_HasNoReplayCameraAndTheViewIsNotTilted()
    {
        string dir = GameFiles.FindGameDir();
        var session = new RaceLoader(dir).Load(RaceInfo.Setup("replay", dir));
        var drive = new RaceDrive(new RaceScene(session, dir), withAi: true);
        for (int i = 0; i < 600; i++) drive.Frame(1.0 / 32, default);
        Assert.Null(drive.Runner.ReplayCam);
        Assert.Null(drive.View.ReplayCam);
        Assert.Equal(0, drive.Cam.ReplayTilt);
        Assert.False(drive.ReplayAutoCamera);
    }

    [GameDataFact]
    public void Playback_CameraModeAndTiltFollowTheDirector_AndRaceStateIsUntouched()
    {
        var drive = Playback(out _);
        Assert.NotNull(drive.Runner.ReplayCam);
        var modes = new List<int>();
        int seen = 0;
        for (int i = 0; i < 4000 && !drive.PlaybackEnded; i++)
        {
            drive.Frame(1.0 / 32, default);
            var rc = drive.View.ReplayCam!;
            Assert.Equal(drive.Runner.ReplayCam!.Field50, rc.Field50);
            Assert.Equal(rc.Field50, drive.Cam.ReplayTilt);
            if (rc.Changes != seen)
            {
                seen = rc.Changes;
                modes.Add(rc.Mode);
                Assert.Equal(rc.Mode, drive.Cam.Mode);          // the camera shows the director's choice
            }
        }
        Assert.True(modes.Count >= 4, "several camera changes in 4000 steps");
        Assert.Contains(modes, m => m is 6 or 5 or 7);          // not only chase views: track / TV / fixed cameras of the cycle
    }

    [GameDataFact]
    public void Playback_ManualCameraKeyEndsTheAutoCameraAndTheTilt()
    {
        var drive = Playback(out _);
        int guard = 0;
        while (drive.Cam.ReplayTilt == 0 && guard++ < 20000) drive.Frame(1.0 / 32, default);
        Assert.NotEqual(0, drive.Cam.ReplayTilt);
        Assert.True(drive.ReplayAutoCamera);
        drive.Frame(0, new DriveKeys(false, false, false, false, false, false, false) with { CycleCamera = true });
        Assert.False(drive.ReplayAutoCamera);
        Assert.Equal(0, drive.Cam.ReplayTilt);
        int mode = drive.Cam.Mode;
        drive.Frame(0, default);
        for (int i = 0; i < 1000; i++) drive.Frame(1.0 / 32, default);
        Assert.Equal(0, drive.Cam.ReplayTilt);
    }

    [GameDataFact]
    public void Playback_RendersTiltedAndUprightFramesToPng()
    {
        string root = FindRoot();
        string outDir = Path.Combine(root, "out", "gfx_check");
        Directory.CreateDirectory(outDir);
        var drive = Playback(out _);
        bool savedTilt = false, savedFlat = false;
        for (int i = 0; i < 25000 && !(savedTilt && savedFlat) && !drive.PlaybackEnded; i++)
        {
            drive.Frame(1.0 / 32, default);
            int mode = drive.Cam.Mode;
            if (mode < 2 || mode > 8 || drive.Cam.InCar) continue;
            if (!savedTilt && drive.Cam.ReplayTilt != 0 && drive.Cam.Mode is 2 or 6 or 7)
            {
                Save(drive, Path.Combine(outDir, $"replay_cam_tilt_mode{mode}.png"));
                savedTilt = true;
            }
            else if (!savedFlat && drive.Cam.ReplayTilt == 0 && drive.Cam.Mode is 2 or 6 or 7 && drive.Runner.Tick > 900)
            {
                Save(drive, Path.Combine(outDir, $"replay_cam_flat_mode{mode}.png"));
                savedFlat = true;
            }
        }
        Assert.True(savedTilt && savedFlat, "the recording contains tilted and upright director views");
    }

    static void Save(RaceDrive drive, string path)
    {
        var frame = new FrameBuffer(640, 480);
        drive.Render(frame);
        frame.SavePng(path);
        Assert.True(new FileInfo(path).Length > 1000);
    }

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, Path.Combine("src", "NFS2SE.sln")))) return d.FullName;
        return Path.GetTempPath();
    }
}
