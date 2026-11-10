using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NFS.Core;
using NFS.Desktop;
using NFS.Game.Flow;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class GameLoopTests
{
    static DesktopInput Input(MenuKey key) => new(default, [key], false);
    static DesktopInput Idle => new(default, [], false);
    static DesktopInput Pause => new(default, [], true);

    /// <summary>Smoke test of the user path at a real frame rate: menu → default race, gas held through the countdown
    /// and 25 s of driving, every 60 Hz frame rendered. Regression: the HUD crashed at tick 0x121 (missing cnt0 shape),
    /// which single-frame autotests never reached.</summary>
    [GameDataFact]
    public void UserPath_MenuToRace_RendersEveryFrameThroughCountdownAndDriving()
    {
        var loop = new GameLoop();
        var fb = new FrameBuffer();
        for (int i = 0; i < 4; i++) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, Input(MenuKey.Enter));
        Assert.NotNull(loop.Drive);
        var gas = new DesktopInput(new DriveKeys(true, false, false, false, false, false, false), [], false);
        for (int frame = 0; frame < 35 * 60; frame++)
        {
            loop.Frame(1.0 / 60, gas);
            loop.Render(fb);
        }
        Assert.True(loop.Drive!.Runner.Tick > 0x200 + 25 * 64);
        Assert.NotEqual(default, loop.Drive.Runner.Car.Vel);
    }

    [GameDataFact]
    public void MenuAndDirectRaceInitializationSampleOrbitActivationOnce()
    {
        var loop = new GameLoop();
        for (int i = 0; i < 4; i++) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false, OrbitCameraActivation: true));
        Assert.True(loop.Drive!.Cam.OrbitEnabled);
        loop.Frame(0, new DesktopInput(default, [], false, OrbitCameraActivation: false));
        Assert.True(loop.Drive.Cam.OrbitEnabled); // sampled at initialization, not live-toggled.

        var direct = new GameLoop();
        direct.StartRace(new RaceScene("replay"), orbitCameraActivation: true);
        Assert.True(direct.Drive!.Cam.OrbitEnabled);
        var normal = new GameLoop();
        normal.StartRace(new RaceScene("replay"));
        Assert.False(normal.Drive!.Cam.OrbitEnabled);
    }

    /// <summary>The I_HORIZON key (0xE): starts with HORIZONDISPLAY (CONFIG.DAT +0x132) [ref:2741]
    /// F9 toggles it (F5 keeps the HUD), and Render actually passes the flag to the sky.</summary>
    [GameDataFact]
    public void HorizonKey_StartsFromConfig_TogglesTheRenderedSky_AndRestartReloadsConfig()
    {
        var loop = new GameLoop();   // no autopilot: Frame passes the driving keys through
        loop.StartRace(new RaceScene("replay"));
        var d = loop.Drive!;
        bool config = loop.Menu.Controller.Config[0x132] == 0;
        Assert.Equal(config, d.HorizonDisplay);
        for (int i = 0; i < 320; i++) loop.Frame(1.0 / 32, Idle);   // 0x140 = the earliest pause [ref:2742]
        uint sky = (uint)(d.Scene.Track.Hrz!.SkyRgb[0] << 16 | d.Scene.Track.Hrz.SkyRgb[1] << 8 | d.Scene.Track.Hrz.SkyRgb[2]);
        var full = new FrameBuffer(); loop.Render(full);
        d.ToggleHorizon();
        var flat = new FrameBuffer(); loop.Render(flat);
        Assert.True(Count(flat, sky) > Count(full, sky) + 1000); // a flat quad of the sky colour instead of the gradient [ref:2743]
        // Host key: F9 acts on one edge at a time, and a race restart returns to the CONFIG (+0x132) value.
        var horizon = Idle with { Drive = default(DriveKeys) with { HorizonToggle = true } };
        loop.Frame(1.0 / 32, horizon);
        Assert.True(d.HorizonDisplay); // ToggleHorizon above set it to false; F9 returns it to true
        loop.Frame(0, Pause); loop.Frame(0, Input(MenuKey.Down)); loop.Frame(0, Input(MenuKey.Enter));
        Assert.NotSame(d, loop.Drive);
        Assert.Equal(config, loop.Drive!.HorizonDisplay);
    }

    static int Count(FrameBuffer fb, uint rgb)
    {
        int n = 0;
        for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) if ((fb.Get(x, y) & 0xFFFFFF) == rgb) n++;
        return n;
    }

    [Fact]
    public void PadAnalogAndKeyboardPriorityAndDisconnectedPad()
    {
        var mapper = new InputMapping();
        var pad = new GamePadState(new Vector2(-0.5f, 0), Vector2.Zero, 0.25f, 0.75f,
            [Buttons.RightShoulder, Buttons.X, Buttons.LeftStick]);
        var keys = new KeyboardDriver();
        Assert.Equal(new PlayerInput(-64, 191, 64,
            PlayerInput.BtnGearUp | PlayerInput.BtnHandbrake | PlayerInput.BtnHorn),
            keys.Next(mapper.Read(default, pad).Drive));
        Assert.Equal(PlayerInput.BtnHandbrake | PlayerInput.BtnHorn, keys.Next(mapper.Read(default, pad).Drive).Buttons);
        var keyboard = new KeyboardState(Keys.Up, Keys.Down, Keys.Right);
        var result = keys.Next(mapper.Read(keyboard, pad).Drive);
        Assert.Equal(127, result.Steer); Assert.Equal(255, result.Gas); Assert.Equal(255, result.Brake);
        Assert.Equal(default(PlayerInput), keys.Next(mapper.Read(default, default).Drive));
    }

    [Fact]
    public void PadMenuPauseAndDrivingButtonsAreMappedOncePerPress()
    {
        var mapper = new InputMapping();
        var pad = new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0,
            [Buttons.DPadDown, Buttons.A, Buttons.Start, Buttons.LeftShoulder, Buttons.Y]);
        var first = mapper.Read(default, pad);
        Assert.Equal([MenuKey.Down, MenuKey.Enter], first.Menu);
        Assert.True(first.Pause);
        var driver = new KeyboardDriver();
        Assert.Equal(PlayerInput.BtnGearDown | PlayerInput.BtnReset, driver.Next(first.Drive).Buttons);
        var held = mapper.Read(default, pad);
        Assert.Empty(held.Menu); Assert.False(held.Pause);
        Assert.Equal(0, driver.Next(held.Drive).Buttons);
        mapper.Read(default, default);
        Assert.True(mapper.Read(default, pad).Pause);
        var back = mapper.Read(default, new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, new[] { Buttons.B }));
        Assert.Equal([MenuKey.Escape], back.Menu);
    }

    [Fact]
    public void ShortButtonPressBetweenInputStepsIsRetainedExactlyOnce()
    {
        var driver = new KeyboardDriver();
        var reset = new DriveKeys(false, false, false, false, false, false, true);
        driver.Observe(reset);
        driver.Observe(default);
        Assert.Equal(PlayerInput.BtnReset, driver.Next(default).Buttons);
        Assert.Equal(0, driver.Next(default).Buttons);
    }

    [Fact]
    public void PauseVolumesUseOriginalStepAndLimitsAndConfirmDefaultsToNo()
    {
        var menu = new PauseMenu();
        menu.Open();
        menu.Handle(MenuKey.Down); menu.Handle(MenuKey.Down);
        menu.Handle(MenuKey.Left); Assert.Equal(124, menu.MusicVolume);
        for (int i = 0; i < 50; i++) menu.Handle(MenuKey.Left);
        Assert.Equal(0, menu.MusicVolume);
        for (int i = 0; i < 50; i++) menu.Handle(MenuKey.Right);
        Assert.Equal(127, menu.MusicVolume);
        menu.Handle(MenuKey.Down); menu.Handle(MenuKey.Left);
        Assert.Equal(124, menu.EffectsVolume);
        menu.Handle(MenuKey.Down); menu.Handle(MenuKey.Enter);
        Assert.True(menu.Confirming); Assert.False(menu.ConfirmYes);
        Assert.Null(menu.Handle(MenuKey.Enter));
        Assert.False(menu.Confirming);
        menu.Handle(MenuKey.Enter); menu.Handle(MenuKey.Left);
        Assert.Equal(PauseChoice.EndRace, menu.Handle(MenuKey.Enter));
        menu.Open(); menu.Handle(MenuKey.Up); menu.Handle(MenuKey.Enter);
        Assert.Equal(PauseChoice.Resume, menu.Handle(MenuKey.Escape));
    }

    static long[] State(RaceDrive drive) => drive.Runner.Cars.SelectMany(c => CarDump.Slots.Select(s => s.Get(c.Obj))).ToArray();

    [GameDataFact]
    public void PauseFreezesAllCarsClockCameraAndAudio_ResumeMatchesUninterruptedRace()
    {
        var a = new RaceDrive(new RaceScene("replay"), withAi: true, automatic: true);
        var b = new RaceDrive(new RaceScene("replay"), withAi: true, automatic: true);
        Assert.False(b.Pause()); // before 0x140 ignored. [ref:2744]
        var keys = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 240; i++) { a.Frame(1.0 / 32, keys); b.Frame(1.0 / 32, keys); }
        a.Frame(0.01, keys); b.Frame(0.01, keys); // fractional accumulator must survive pause.
        Assert.True(b.Pause());
        long[] before = State(b);
        var pose = b.Cam.Position; double accumulator = b.Clock.Accumulator;
        using var audio = new NFS.Game.Race.RaceAudio(b.Runner);
        int buffers = 0; audio.BufferReady += _ => buffers++;
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(0, b.Frame(1, keys with { Reset = true, GearUp = true }));
            b.Step(new PlayerInput(127, 255, 255, PlayerInput.BtnReset));
            b.ResetToRoad();
            b.RunAutopilot(1);
        }
        Assert.Equal(before, State(b)); Assert.Equal(accumulator, b.Clock.Accumulator);
        Assert.Equal(pose, b.Cam.Position); Assert.Equal(0, buffers);
        b.Resume(keys);
        for (int i = 0; i < 320; i++) { a.Frame(1.0 / 32, keys); b.Frame(1.0 / 32, keys); }
        Assert.Equal(a.Runner.Tick, b.Runner.Tick); Assert.Equal(State(a), State(b));
        Assert.Equal(a.Clock.Accumulator, b.Clock.Accumulator);
        Assert.Equal(a.Cam.Position, b.Cam.Position);
    }

    [GameDataFact]
    public void LoopResumeRestartAndExitKeepSetupAndResetRace()
    {
        var loop = new GameLoop(autopilot: true);
        loop.StartRace(new RaceScene("replay")); // also verifies direct --race sessions.
        for (int i = 0; i < 320; i++) loop.Frame(1.0 / 32, Idle);
        var old = loop.Drive!; var setup = old.Scene.Session.Setup;
        loop.Frame(0, Pause);
        Assert.Equal(GameState.Pause, loop.Menu.Controller.Flow.State);
        loop.Frame(20, Pause); // resume frame consumes no wall time.
        Assert.False(old.Paused); Assert.Equal(640, old.Runner.Tick);
        loop.Frame(0, Pause); loop.Frame(0, Input(MenuKey.Down)); loop.Frame(0, Input(MenuKey.Enter));
        var fresh = loop.Drive!;
        Assert.NotSame(old, fresh); Assert.NotSame(old.Car, fresh.Car);
        Assert.Equal(0, fresh.Runner.Tick); Assert.Equal(setup.Block, fresh.Scene.Session.Setup.Block);
        var baseline = new RaceDrive(new RaceScene("replay"), withAi: true, automatic: true);
        Assert.Equal(State(baseline), State(fresh));
        Assert.Equal(GameState.Race, loop.Menu.Controller.Flow.State);
        Assert.Equal(2, loop.Menu.Controller.Flow.RaceStarts);
        for (int i = 0; i < 160; i++) loop.Frame(1.0 / 32, Idle);
        loop.Frame(0, Pause);
        for (int i = 0; i < 4; i++) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, Input(MenuKey.Enter)); loop.Frame(0, Input(MenuKey.Left)); loop.Frame(0, Input(MenuKey.Enter));
        // End race (->(1)) opens the results screen with the player aborted. [ref:2745]
        Assert.Null(loop.Drive); Assert.Equal(MenuStatus.Results, loop.Menu.Controller.Status);
        Assert.Equal(1, loop.Menu.Controller.Results!.Racers.Single(r => r.Human).RaceState);
        loop.Frame(0, Input(MenuKey.Escape));
        Assert.Equal(MenuStatus.Menu, loop.Menu.Controller.Status);
        Assert.Equal(GameState.FrontEnd, loop.Menu.Controller.Flow.State);
    }

    [GameDataFact]
    public void ShortResetPressAcrossRealFramesIsConsumedByNextSimStep()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false);
        d.Frame(0.001, new DriveKeys(false, false, false, false, false, false, true));
        d.Frame(0.001, default);
        d.Frame(1.0 / 32, default);
        Assert.Equal(1, d.Resets);
        d.Frame(1.0 / 32, default);
        Assert.Equal(1, d.Resets);
    }

    [GameDataFact]
    public void ConfirmedQuitLeavesNoRunningRace_AndFurtherFramesDoNothing()
    {
        var loop = new GameLoop(autopilot: true);
        loop.StartRace(new RaceScene("replay"));
        for (int i = 0; i < 160; i++) loop.Frame(1.0 / 32, Idle);
        loop.Frame(0, Pause);
        loop.Frame(0, Input(MenuKey.Up)); // wrap to quit
        loop.Frame(0, Input(MenuKey.Enter));
        Assert.False(loop.ExitRequested); // confirmation initially NO
        loop.Frame(0, Input(MenuKey.Left)); loop.Frame(0, Input(MenuKey.Enter));
        Assert.True(loop.ExitRequested); Assert.Null(loop.Drive);
        loop.Frame(1, Idle);
        Assert.Equal(GameState.Unload, loop.Menu.Controller.Flow.State);
        var fb = new FrameBuffer();
        loop.Render(fb); // host may still draw its last frame after Exit().
        Assert.Equal(0u, fb.Get(0, 0));
    }

    [GameDataFact]
    public void NaturalFinishStillOpensResultsAndKeyboardReturnsToMenu()
    {
        var loop = new GameLoop(autopilot: true);
        loop.StartRace(new RaceScene("replay"));
        var drive = loop.Drive!;
        drive.Runner.SetLaps(2);
        for (int i = 0; i < 400; i++) loop.Frame(1.0 / 32, Idle);
        drive.Car.Lap = 1;
        loop.Frame(1.0 / 32, Idle);
        for (int i = 0; i < 50; i++) loop.Frame(1.0 / 32, Idle);
        drive.Car.Lap = 2;
        for (int i = 0; i < 170 && loop.Menu.Controller.Status == MenuStatus.Race; i++)
            loop.Frame(1.0 / 32, Idle);
        Assert.Equal(MenuStatus.Results, loop.Menu.Controller.Status);
        Assert.NotNull(loop.Menu.Controller.Results);
        Assert.True(loop.Menu.Controller.VictoryActive);                 // the player's win: the victory screen comes first [ref:2746]
        loop.Frame(0, Input(MenuKey.Escape));                           // the first key ends the victory screen
        Assert.Equal(MenuStatus.Results, loop.Menu.Controller.Status);
        loop.Frame(0, Idle);                                            // a shortened race beats the track record – name window [ref:2747]
        Assert.NotNull(loop.Menu.Controller.NameEditing);
        loop.Frame(0, Input(MenuKey.Escape));
        Assert.Null(loop.Menu.Controller.NameEditing);
        loop.Frame(0, Input(MenuKey.Escape));
        Assert.Equal(MenuStatus.Menu, loop.Menu.Controller.Status);
        Assert.Null(loop.Drive);
    }

    [GameDataFact]
    public void LoopReviewWritesAllKeyScreensWithoutGraphicsDevice()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nfs-loop-" + Guid.NewGuid());
        try
        {
            var loop = LoopReview.Run(directory);
            Assert.Equal(MenuStatus.Menu, loop.Menu.Controller.Status);
            Assert.Equal(7, Directory.GetFiles(directory, "*.png").Length);
            Assert.All(Directory.GetFiles(directory), p => Assert.True(new FileInfo(p).Length > 1000));
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(directory, "03-race.png")),
                File.ReadAllBytes(Path.Combine(directory, "04-pause.png")));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory));
            Directory.Delete(directory, recursive: true);
        }
    }
}
