using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.Race;
using NFS.Game.FrontEnd;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

public sealed class ReplayPlaybackTests
{
    [GameDataFact]
    public void ReplayPlaybackSpeed_AllFourRatesUseElapsedTimeAt64Hz()
    {
        string dir = GameFiles.FindGameDir();
        var replay = Replay.Parse(GameFiles.ReadRaw("REPLAY.RPY", dir));
        for (int speedIndex = 0; speedIndex < 4; speedIndex++)
        {
            var session = new RaceLoader(dir).Load(RaceInfo.Setup("replay", dir));
            var drive = new RaceDrive(new RaceScene(session, dir), withAi: true,
                playbackSource: new ReplayInputSource(replay));
            var ui = drive.ReplayUi!;
            ui.Press(ReplayUiKey.Right);
            ui.Tick();
            for (int presses = (speedIndex - ReplayUi.InitialSpeedIndex + 4) % 4; presses > 0; presses--)
            {
                ui.Press(ReplayUiKey.Select);
                for (int tick = 0; tick < 6; tick++) ui.Tick();
            }
            Assert.Equal(speedIndex, ui.SpeedIndex);
            for (int frame = 0; frame < 64; frame++) drive.Frame(1.0 / 64, default);
            Assert.Equal(new[] { 16, 32, 64, 128 }[speedIndex], drive.Runner.Tick);
        }
    }

    [GameDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LiveRace_PauseEndReplayAndReturn_PreservesResultsAndSeries(int raceType)
    {
        var loop = new GameLoop();
        var menu = loop.Menu.Controller;
        menu.Config.RaceType = (byte)raceType;
        menu.StartRace();
        loop.StartRace(new RaceScene(menu.Session!));
        var gas = new DesktopInput(new DriveKeys(true, false, false, false, false, false, false), [], false);
        for (int i = 0; i < 400; i++) loop.Frame(1.0 / 32, gas);
        var live = loop.Drive!;
        int tick = live.Runner.Tick;
        var positions = live.Runner.Cars.Select(c => c.Obj.Pos).ToArray();
        var gears = live.Runner.Cars.Select(c => c.Obj.Gear).ToArray();
        loop.Frame(0, new(default, [], true));
        Assert.True(live.Paused);
        // Resume, optional restart, music, effects, end race; series skip restart.
        while (loop.PauseMenu.Selected != 4) loop.Frame(0, new(default, [MenuKey.Down], false));
        loop.Frame(0, new(default, [MenuKey.Enter], false));
        loop.Frame(0, new(default, [MenuKey.Left], false));
        loop.Frame(0, new(default, [MenuKey.Enter], false));
        Assert.Equal(MenuStatus.Results, menu.Status);
        var result = menu.Results;
        var session = menu.Session;
        var tournament = menu.Tournament;
        var knockout = menu.Knockout;
        byte[] config = menu.Config.ToBytes();
        loop.Frame(0, new(default, [MenuKey.Down], false));
        loop.Frame(0, new(default, [MenuKey.Down], false));
        loop.Frame(0, new(default, [MenuKey.Enter], false));
        var replayDrive = Assert.IsType<RaceDrive>(loop.Drive);
        Assert.True(menu.ResultsReplay);
        Assert.Equal(1, replayDrive.ReplaySpeed);
        // Enter runs the selected icon (pause at the start); the pause takes effect when the press counter reaches 4 (5 ticks).
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.True(replayDrive.ReplayPaused);
        Assert.True(replayDrive.ReplayUi!.PauseFlag);
        int pausedTick = replayDrive.Runner.Tick;
        loop.Frame(2, new(default(DriveKeys) with { LookBehind = true }, [], false));
        Assert.Equal(pausedTick, replayDrive.Runner.Tick);
        Assert.True(replayDrive.Cam.LookingBehind);
        bool mapBefore = replayDrive.ShowMap;
        loop.Frame(0, new(default(DriveKeys) with { ToggleHud = true, HorizonToggle = true }, [], false));
        Assert.NotEqual(mapBefore, replayDrive.ShowMap);
        if (Environment.GetEnvironmentVariable("NFS_REPLAY_REVIEW_DIR") is { Length: > 0 } review)
        {
            Directory.CreateDirectory(review);
            var pausedFrame = new FrameBuffer(640, 480);
            loop.Render(pausedFrame);
            pausedFrame.SavePng(Path.Combine(review, "replay_controls_paused.png"));
        }
        // Speed icon: Right selects it, Enter cycles 1x -> 2x -> 1/4x -> 1/2x -> 1x -> 2x (paused: no simulation meanwhile).
        loop.Frame(1.0 / 32, new(default, [MenuKey.Right], false));
        Assert.Equal(1, replayDrive.ReplayUi!.Selected);
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.Equal(2, replayDrive.ReplaySpeed);
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.Equal(0.25, replayDrive.ReplaySpeed);
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.Equal(0.5, replayDrive.ReplaySpeed);
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.Equal(1, replayDrive.ReplaySpeed);
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.Equal(2, replayDrive.ReplaySpeed);
        Assert.Equal(pausedTick, replayDrive.Runner.Tick);
        // Back to the pause icon and resume.
        loop.Frame(1.0 / 32, new(default, [MenuKey.Left], false));
        loop.Frame(0.2, new(default, [MenuKey.Enter], false));
        Assert.False(replayDrive.ReplayPaused);
        int before = replayDrive.Runner.Tick;
        loop.Frame(1.0 / 20, new(default, [], false));
        Assert.InRange(replayDrive.Runner.Tick - before, 6, 7);
        before = replayDrive.Runner.Tick;
        loop.Frame(1.0 / 64, new(default, [], false));
        Assert.Equal(before + 2, replayDrive.Runner.Tick);
        loop.Frame(0, new(default, [MenuKey.Up], false));
        replayDrive = loop.Drive!;
        Assert.Equal(0, replayDrive.Runner.Tick);
        Assert.Equal(1, replayDrive.ReplaySpeed);
        Assert.Same(result, menu.Results);
        Assert.Same(tournament, menu.Tournament);
        Assert.Same(knockout, menu.Knockout);
        Assert.Equal(config, menu.Config.ToBytes());
        var replaySource = replayDrive.PlaybackSource!;
        var expectedReplay = new RaceRunner(new RaceLoader().Load(replayDrive.Scene.Session.Setup), 0, withAi: true);
        var hostileReplayInput = new DesktopInput(
            default(DriveKeys) with { Up = true, Right = true, Reset = true }, [], false);
        for (int i = 0; i < 400 && loop.Drive != null; i++)
        {
            loop.Frame(1.0 / 32, hostileReplayInput);
            expectedReplay.Step(replaySource.ForRaceTick(expectedReplay.Tick));
        }
        Assert.Equal(expectedReplay.Tick, replayDrive.Runner.Tick);
        Assert.Equal(expectedReplay.Cars.Select(c => c.Obj.Pos), replayDrive.Runner.Cars.Select(c => c.Obj.Pos));
        Assert.Equal(expectedReplay.Cars.Select(c => c.Obj.Orient), replayDrive.Runner.Cars.Select(c => c.Obj.Orient));
        Assert.Equal(expectedReplay.Cars.Select(c => c.Obj.Gear), replayDrive.Runner.Cars.Select(c => c.Obj.Gear));
        Assert.Equal(expectedReplay.DynObjects.HitCount, replayDrive.Runner.DynObjects.HitCount);
        Assert.Equal(expectedReplay.DynObjects.Knocked, replayDrive.Runner.DynObjects.Knocked);
        Assert.Null(loop.Drive);
        Assert.Same(result, menu.Results);
        Assert.Same(session, menu.Session);
        Assert.Same(tournament, menu.Tournament);
        Assert.Same(knockout, menu.Knockout);
        Assert.Equal(config, menu.Config.ToBytes());
        Assert.Equal(MenuStatus.Results, menu.Status);
        // Re-enter and leave early through the actual desktop loop.
        loop.Frame(0, new(default, [MenuKey.Enter], false));
        Assert.NotNull(loop.Drive);
        loop.Frame(1.0 / 32, gas);
        loop.Frame(0, new(default, [MenuKey.Enter], false));
        var pausedReplay = loop.Drive!;
        var pausedReplayTick = pausedReplay.Runner.Tick;
        loop.Frame(3, new(default, [MenuKey.Escape], true));
        Assert.Null(loop.Drive);
        Assert.Equal(pausedReplayTick, pausedReplay.Runner.Tick);
        Assert.Same(result, menu.Results);
        Assert.Same(tournament, menu.Tournament);
        Assert.Same(knockout, menu.Knockout);
        Assert.Equal(MenuStatus.Results, menu.Status);
    }

    [GameDataFact]
    public void OriginalReplay_RaceDriveMatchesIndependentRunnerAndStopsAtEof()
    {
        string dir = GameFiles.FindGameDir();
        var replay = Replay.Parse(GameFiles.ReadRaw("REPLAY.RPY", dir));
        var source = new ReplayInputSource(replay);
        RaceSession Fresh() => new RaceLoader(dir).Load(RaceInfo.Setup("replay", dir));
        var drive = new RaceDrive(new RaceScene(Fresh(), dir), withAi: true, playbackSource: source);
        var expected = new RaceRunner(Fresh(), 0, withAi: true);

        // 500 simulation steps = 1000 original 64 Hz race ticks. Compare the real desktop playback
        // path against the existing headless runner consuming the same original input stream.
        for (int i = 0; i < 500; i++)
        {
            drive.Step(default);
            expected.Step(source.ForRaceTick(expected.Tick));
            Assert.Equal(expected.Tick, drive.Runner.Tick);
            Assert.Equal(expected.Car.Pos, drive.Runner.Car.Pos);
            Assert.Equal(expected.Car.Orient, drive.Runner.Car.Orient);
            Assert.Equal(expected.Car.SpeedApprox, drive.Runner.Car.SpeedApprox);
            Assert.Equal(expected.Car.Gear, drive.Runner.Car.Gear); // includes the recorded manual shifts
            Assert.Equal(expected.Laps.Cars[0].LapsDone, drive.Runner.Laps.Cars[0].LapsDone);
            for (int car = 1; car < expected.Cars.Count; car++)
                Assert.Equal(expected.Cars[car].Obj.Pos, drive.Runner.Cars[car].Obj.Pos);
        }
        Assert.Equal(1000, drive.Runner.Tick);

        if (Environment.GetEnvironmentVariable("NFS_REPLAY_REVIEW_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            drive.Frame(0, default); // publish render poses and camera without advancing simulation
            var frame = new FrameBuffer(640, 480);
            drive.Render(frame);
            frame.SavePng(Path.Combine(output, "original_replay_1000ticks.png"));
        }

        // File has 564 complete packets; the final partial packet was never flushed by the original.
        // Playback must still end cleanly at the header EOF, without creating a result for this view.
        while (!drive.PlaybackEnded && drive.Runner.Tick < source.RaceTicks64 + 2) drive.Step(default);
        Assert.True(drive.PlaybackEnded);
        Assert.Null(drive.Results);
    }
}
