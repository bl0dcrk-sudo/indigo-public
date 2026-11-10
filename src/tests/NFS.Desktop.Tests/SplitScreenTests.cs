using Microsoft.Xna.Framework.Input;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Split screen in the host: two players' keys, camera cycle without cockpit, two humans driving from separate inputs,
/// HUD layout in halves and rendering of two views. No window – just the RGBA buffer. [ref:3461]</summary>
public class SplitScreenTests
{
    static RaceScene SplitScene(int opponents = 0, Action<FeConfig>? edit = null, bool rain = false)
    {
        var menu = MenuController.Load();
        menu.Config.GameType = 1; menu.Config.RaceType = 0; menu.Config.OpponentCount = (byte)opponents;
        menu.Config.TrafficOff = 1; menu.Config.Track = 1;
        edit?.Invoke(menu.Config);
        menu.Cheats.Effects.Rain = rain;
        return new RaceScene(new RaceLoader().Load(menu.BuildSetup()));
    }

    static DriveKeys Gas => new(true, false, false, false, false, false, false);

    [GameDataFact]
    public void LocalSplitMapKeepsBothHumansWhileRemoteWelcomeSelectsExactlyOne()
    {
        var local = SplitScene();
        Assert.Equal(2, local.MapCars().Count(car => car.Human));
        foreach (int seat in new[] { local.PlayerCarId, local.CarIdOf(local.SecondPlayerInstance) })
        {
            var remote = new RaceScene(local.Session, playerCarId: seat);
            Assert.Equal(1, remote.MapCars().Count(car => car.Human));
            Assert.True(remote.MapCars()[remote.MapPlayer].Human);
            Assert.Equal(-1, remote.SecondPlayerInstance);
        }
    }

    [GameDataFact]
    public void BothSplitCamerasRunTheStartCanAndIgnoreCameraCycleUntilItEnds()
    {
        var drive = new RaceDrive(SplitScene());
        Assert.Equal(RaceCamera.StartAnimationMode, drive.Cam.Mode);
        Assert.Equal(RaceCamera.StartAnimationMode, drive.Cam2!.Mode);
        var can = Assert.IsType<NFS.Data.Track.Can>(drive.Cam.StartAnimation);
        Assert.Same(can, drive.Cam2.StartAnimation);

        drive.Frame(1.0 / 64, default(DriveKeys) with { CycleCamera = true },
            default(DriveKeys) with { CycleCamera = true });
        Assert.Equal(RaceCamera.StartAnimationMode, drive.Cam.Mode);
        Assert.Equal(RaceCamera.StartAnimationMode, drive.Cam2.Mode);
        drive.Frame(0, default, default);
        var first = (drive.Cam.Position, drive.Cam.Forward, drive.Cam.Up);
        var second = (drive.Cam2.Position, drive.Cam2.Forward, drive.Cam2.Up);
        foreach (bool held in new[] { true, false })
        {
            drive.Frame(0, default(DriveKeys) with { LookBehind = held }, default(DriveKeys) with { LookBehind = held });
            Assert.Equal(first, (drive.Cam.Position, drive.Cam.Forward, drive.Cam.Up));
            Assert.Equal(second, (drive.Cam2.Position, drive.Cam2.Forward, drive.Cam2.Up));
        }
        while (drive.Runner.Tick <= (can.Keys.Count - 1) * can.TicksPerKey + 2)
            drive.Frame(1.0 / 32, default, default);
        Assert.Equal(1, drive.Cam.Mode); // (0) skips cockpit mode in split [ref:2934]
        Assert.Equal(1, drive.Cam2.Mode);
    }

    [Fact]
    public void DefaultSplitKeys_AreTheTwoPlayerLayout()
    {
        var map = SplitKeyMap.Default;
        Assert.Equal((Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.PageDown, Keys.PageUp, Keys.Home, Keys.End, Keys.Insert, Keys.Delete),
            (map.Player1.Left, map.Player1.Right, map.Player1.Gas, map.Player1.Brake, map.Player1.LookBehind, map.Player1.Camera,
             map.Player1.Handbrake, map.Player1.Horn, map.Player1.ShiftUp, map.Player1.ShiftDown));
        Assert.Equal((Keys.S, Keys.F, Keys.E, Keys.D, Keys.Z, Keys.R, Keys.W, Keys.T, Keys.Q, Keys.A),
            (map.Player2.Left, map.Player2.Right, map.Player2.Gas, map.Player2.Brake, map.Player2.LookBehind, map.Player2.Camera,
             map.Player2.Handbrake, map.Player2.Horn, map.Player2.ShiftUp, map.Player2.ShiftDown));
        Assert.Equal((Keys.F11, Keys.F12), (map.Reset1, map.Reset2));
        Assert.Equal(Keys.F5, map.Player1.ToggleHud);   // C1_TOGGLEHUD
    }

    [Fact]
    public void InputMapping_ReadsBothPlayersFromOneKeyboard()
    {
        var input = new InputMapping();
        var keys = new KeyboardState(Keys.Up, Keys.Left, Keys.E, Keys.F, Keys.F12, Keys.PageUp);
        var read = input.Read(keys, default);
        Assert.True(read.Split1!.Value.Up);
        Assert.True(read.Split1.Value.Left);
        Assert.False(read.Split1.Value.Right);
        Assert.True(read.Split1.Value.CycleCamera);          // C1_CYCLECAMERA = PgUp in split
        Assert.False(read.Split1.Value.Reset);
        Assert.True(read.Split2!.Value.Up);                  // E = gas
        Assert.True(read.Split2.Value.Right);                // F = right
        Assert.False(read.Split2.Value.Left);
        Assert.True(read.Split2.Value.Reset);                // F12 = reset car 2
        Assert.False(read.Split2.Value.CycleCamera);
        // the one-player mapping is independent of the split table
        Assert.True(read.Drive.Up);
        Assert.False(read.Drive.Reset);                      // R is player 2's camera in split, F12 does not reset player 1
        Assert.True(input.Read(new KeyboardState(Keys.R), default).Split2!.Value.CycleCamera);
    }

    [GameDataFact]
    public void RebindingSplitKeys_ReachesTheSplitMapping()
    {
        var menu = MenuController.Load();
        KeyBindings.AssignSplit(menu.Config, 1, KeyAction.Accelerate, 0x11, 'W');   // player 2 gas = W
        var input = new InputMapping { SplitBindings = SplitKeyMap.From(menu) };
        Assert.True(input.Read(new KeyboardState(Keys.W), default).Split2!.Value.Up);
        Assert.False(input.Read(new KeyboardState(Keys.E), default).Split2!.Value.Up);
        Assert.False(input.Read(new KeyboardState(Keys.W), default).Split2!.Value.Handbrake);   // W was player 2's handbrake: displaced
    }

    [GameDataFact]
    public void SplitCamera_UsesTheHalfScreenProjection()
    {
        var drive = new RaceDrive(SplitScene(), countdown: false);
        Assert.True(drive.IsSplit);
        var cam = drive.Cam.Camera();
        var half = SplitLayout.CreateCamera();
        Assert.Equal((half.Width, half.Height, half.Fx, half.Fy, half.Cx, half.Cy), (cam.Width, cam.Height, cam.Fx, cam.Fy, cam.Cx, cam.Cy));
        Assert.Equal(240, drive.Cam2!.Camera().Height);
        Assert.NotEqual(0, drive.Cam.Mode);   // never the cockpit with dashboard
        Assert.NotEqual(0, drive.Cam2.Mode);
    }

    [GameDataFact]
    public void TwoHumans_AreDrivenByTheirOwnKeys()
    {
        var scene = SplitScene();
        var drive = new RaceDrive(scene, countdown: false, automatic: true);
        Assert.NotNull(drive.Car2);
        Assert.Equal(1, drive.Car2!.HumanIndex);
        for (int i = 0; i < 8 * 32; i++) drive.Frame(1.0 / 32, default, Gas);             // only player 2 holds the throttle
        Assert.True(drive.Car2.SpeedApprox > 5 * Fx.One);
        Assert.True(drive.Car.SpeedApprox < Fx.One / 2);
        Assert.NotEqual(drive.Cam.Position, drive.Cam2!.Position);
        var scene2 = SplitScene();
        var other = new RaceDrive(scene2, countdown: false, automatic: true);
        for (int i = 0; i < 8 * 32; i++) other.Frame(1.0 / 32, Gas, default);
        Assert.True(other.Car.SpeedApprox > 5 * Fx.One);
        Assert.True(other.Car2!.SpeedApprox < Fx.One / 2);
    }

    [GameDataFact]
    public void Hud_StaysInsideTheHalfOfItsPlayer()
    {
        var scene = SplitScene();
        var drive = new RaceDrive(scene, countdown: false);
        for (int p = 0; p < 2; p++)
        {
            var fb = new FrameBuffer();
            fb.Clear(0, 0, 0);
            scene.Hud.DrawSplit(fb, drive.SplitHudValues(p), p);
            int inside = 0, outside = 0;
            for (int y = 0; y < fb.Height; y++)
                for (int x = 0; x < fb.Width; x++)
                    if (fb.Get(x, y) != 0) { if (SplitLayout.View(p).Contains(x, y)) inside++; else outside++; }
            Assert.True(inside > 500, $"player {p + 1}: HUD must be drawn in its own half ({inside} px)");
            Assert.Equal(0, outside);
        }
    }

    [GameDataFact]
    public void Render_ShowsTwoDifferentViews_AndEachHalfHasItsOwnHud()
    {
        var scene = SplitScene(opponents: 2);
        var drive = new RaceDrive(scene, withAi: true, countdown: false, automatic: true);
        for (int i = 0; i < 6 * 32; i++) drive.Frame(1.0 / 32, Gas, default);   // player 1 drives, player 2 waits at the start
        var fb = new FrameBuffer();
        drive.Render(fb);
        // two independent 3D views: different pixels in the halves
        int differing = 0;
        for (int y = 0; y < 240; y += 3)
            for (int x = 0; x < 640; x += 3)
                if (fb.Get(x, y) != fb.Get(x, y + 240)) differing++;
        Assert.True(differing > 2000, $"{differing} differing samples");
        // switching one HUD off ((1): text+map → text → nothing) removes only that player's panel pixels [ref:2935]
        var withBoth = (byte[])fb.Rgba.Clone();
        drive.ToggleHud2(); drive.ToggleHud2();
        Assert.False(drive.ShowHud2);
        var fb2 = new FrameBuffer();
        drive.Render(fb2);
        int topChanged = 0, bottomChanged = 0;
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (fb2.Get(x, y) != ((uint)(withBoth[(y * 640 + x) * 4] << 16 | withBoth[(y * 640 + x) * 4 + 1] << 8 | withBoth[(y * 640 + x) * 4 + 2])))
                { if (y < 240) topChanged++; else bottomChanged++; }
        Assert.True(bottomChanged > 200);
        Assert.True(topChanged < bottomChanged / 4, $"top {topChanged}, bottom {bottomChanged}");
    }

    /// <summary>Visual review aid: with NFS_SPLIT_PNG_DIR set the split frames (start grid, mid-race, after a manual camera change) are
    /// written as PNG there (headless, nothing is shown on screen); otherwise the frames are only encoded.</summary>
    [GameDataFact]
    public void Render_EncodesFrames_AndCanDumpThemForReview()
    {
        var drive = new RaceDrive(SplitScene(opponents: 2), withAi: true, countdown: false, automatic: true);
        string? dir = Environment.GetEnvironmentVariable("NFS_SPLIT_PNG_DIR");
        if (dir != null) Directory.CreateDirectory(dir);
        void Shot(string name)
        {
            var fb = new FrameBuffer();
            drive.Render(fb);
            var png = fb.EncodePng();
            Assert.True(png.Length > 5000);
            if (dir != null) File.WriteAllBytes(Path.Combine(dir, name + ".png"), png);
        }
        Shot("split_grid");
        var right = default(DriveKeys) with { Up = true, Right = true };
        for (int i = 0; i < 10 * 32; i++) drive.Frame(1.0 / 32, Gas, right);
        Shot("split_driving");
        drive.Frame(1.0 / 32, default(DriveKeys) with { CycleCamera = true }, default);
        drive.Frame(1.0 / 32, default, default(DriveKeys) with { CycleCamera = true });
        Shot("split_camera_cycled");
    }

    [GameDataFact]
    public void GameLoop_RoutesBothInputsToTheSplitDrive()
    {
        var loop = new GameLoop();
        loop.StartRace(SplitScene());
        Assert.True(loop.Drive!.IsSplit);
        var input = new DesktopInput(default, [], false, Split1: Gas, Split2: default(DriveKeys));
        for (int i = 0; i < 16 * 32; i++) loop.Frame(1.0 / 32, input);   // 8 s countdown (0x200 ticks) + 8 s of driving
        Assert.True(loop.Drive.Car.SpeedApprox > 5 * Fx.One);
        Assert.True(loop.Drive.Car2!.SpeedApprox < Fx.One / 2);
        var fb = new FrameBuffer();
        loop.Render(fb);   // does not throw, draws both halves
        Assert.NotEqual(0u, fb.Get(320, 100) | fb.Get(320, 340));
    }

    [GameDataFact]
    public void OrbitActivationInitializesBothSplitCameras()
    {
        var loop = new GameLoop();
        loop.Menu.Controller.Config.GameType = 1;
        loop.Menu.Controller.Config.Track = 1;
        var menu = loop.Menu.Controller;
        int raceItem = menu.CurrentScreen.Items.Select((item, i) => (item, i)).First(p => p.item.Target == "race").i;
        for (int guard = 0; menu.SelectedIndex != raceItem && guard < 40; guard++)
            loop.Frame(0, new DesktopInput(default, [MenuKey.Down], false));
        loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false)); // opens the two-player name editors.
        Assert.NotNull(menu.NameEditing);
        loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false));
        loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false, OrbitCameraActivation: true));
        Assert.True(loop.Drive!.IsSplit);
        Assert.True(loop.Drive!.Cam.OrbitEnabled);
        Assert.True(loop.Drive.Cam2!.OrbitEnabled);
        var normal = new RaceDrive(SplitScene(), countdown: false);
        Assert.False(normal.Cam.OrbitEnabled);
        Assert.False(normal.Cam2!.OrbitEnabled);
    }

    [GameDataFact]
    public void FinishedHuman_WatchesTheOtherOne_AfterTheFinishAnimation()
    {
        var drive = new RaceDrive(SplitScene(), countdown: false, automatic: true);
        Assert.Same(drive.Car, drive.TargetCar(0));
        Assert.Same(drive.Car2, drive.TargetCar(1));
        drive.Car.RaceState = 2;                                          // player 1 crosses the line
        for (int i = 0; i < 40 * 32 && !drive.Follows(0); i++) drive.Frame(1.0 / 32, default, default);
        if (drive.Cam.FinishAnimation is { Keys.Count: > 0 })
        {
            Assert.True(drive.Follows(0), "after the CAN animation the camera takes the other human's car");
            Assert.Same(drive.Car2, drive.TargetCar(0));
            Assert.Same(drive.Car2, drive.TargetCar(1));
            Assert.NotEqual(RaceCamera.FinishMode, drive.Cam.Mode);
        }
        Assert.False(drive.Follows(1));
        Assert.Same(drive.Car2, drive.TargetCar(1));
    }

    static RaceDrive RainDrive(int menuTrack) =>
        new(SplitScene(edit: c => c.Track = (ushort)menuTrack, rain: true), countdown: false, automatic: true);

    static int RainTrack() => Enumerable.Range(0, 8).First(t => RainDrive(t).Weather.Zones.Count > 0);   // zones: tracks 0, 5, 7

    /// <summary>Counts grey streak pixels (0x848484) in the rows [y0, y1) that the dry frame does not have.</summary>
    static int Streaks(FrameBuffer wet, FrameBuffer dry, int y0, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < wet.Width; x++) if (wet.Get(x, y) == (uint)Weather.Gray && dry.Get(x, y) != (uint)Weather.Gray) n++;
        return n;
    }

    [GameDataFact]
    public void Rain_FallsInBothHalves_InTheYBandsOfWthrUpdateStreaksView()
    {
        int track = RainTrack();
        var d = RainDrive(track);
        Assert.True(d.Weather.Enabled);
        int centre = d.Weather.Zones[0].Center;
        d.Car.Trk.Slice = centre; d.Car2!.Trk.Slice = centre; d.SnapToHost();
        var dry = new RaceDrive(SplitScene(edit: c => c.Track = (ushort)track), countdown: false, automatic: true);
        dry.Car.Trk.Slice = centre; dry.Car2!.Trk.Slice = centre; dry.SnapToHost();
        var fb = new FrameBuffer(); var fbDry = new FrameBuffer();
        for (int i = 0; i < 4; i++) { d.Render(fb); dry.Render(fbDry); }
        int top = Streaks(fb, fbDry, 0, 0xC0), bottom = Streaks(fb, fbDry, 0xF0, 0x1B0);
        Assert.True(top > 10 && bottom > 10, $"top {top}, bottom {bottom}");
        Assert.Equal(0, Streaks(fb, fbDry, 0xC8, 0xE8));   // ranges: 0..0xB4 and 0xF0..0x1A4, nothing in between [ref:2936]
        Assert.Equal(0, Streaks(fb, fbDry, 0x1B8, 480));
        var (first, count) = d.Weather.VisibleStreaksSplit(0);
        Assert.Equal((0, 0x14), (first, count));            // 20 records each: player 1 from 0, player 2 from 0x14
        Assert.Equal((0x14, 0x14), d.Weather.VisibleStreaksSplit(1));
        if (Environment.GetEnvironmentVariable("NFS_SPLIT_PNG_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            fb.SavePng(Path.Combine(dir, "split_rain.png"));
        }
    }

    [GameDataFact]
    public void Rain_IntensityIsPerPlayer()
    {
        int track = RainTrack();
        var d = RainDrive(track);
        var zone = d.Weather.Zones[0];
        int far = Enumerable.Range(0, d.Scene.Session.Slices.Length).First(i => Math.Min(Math.Abs(i - zone.Center), d.Scene.Session.Slices.Length - Math.Abs(i - zone.Center)) > zone.HalfWidth + 10);
        d.Car.Trk.Slice = zone.Center; d.Car2!.Trk.Slice = far; d.SnapToHost();
        var fb = new FrameBuffer();
        for (int i = 0; i < 4; i++) d.Render(fb);
        var dry = new RaceDrive(SplitScene(edit: c => c.Track = (ushort)track), countdown: false, automatic: true);
        dry.Car.Trk.Slice = zone.Center; dry.Car2!.Trk.Slice = far; dry.SnapToHost();
        var fbDry = new FrameBuffer();
        dry.Render(fbDry);
        Assert.True(Streaks(fb, fbDry, 0, 0xC0) > 10);       // player 1 in the zone
        Assert.Equal(0, Streaks(fb, fbDry, 0xF0, 0x1B0));    // player 2 outside it: its own intensity 0 ([1]) [ref:2937]
    }

    [GameDataFact]
    public void Finish_DrawsNoNoticeInEitherHalf()
    {
        // task 07a: the original has no FINISH text; a finished player's half shows the race view only
        var d = new RaceDrive(SplitScene(), countdown: false, automatic: true);
        var before = new FrameBuffer(); d.Render(before);
        d.Car2!.RaceState = 2;                                // player 2 finishes (+0x280 == 2)
        d.SnapToHost();                                       // stage 5a.2: presentation reads the published frame
        Assert.Equal(2, d.View.Car(d.SecondCarId!.Value).RaceState);
        var after = new FrameBuffer(); d.Render(after);
        Assert.Equal(before.Rgba, after.Rgba);
        d.Car.RaceState = 2;
        d.SnapToHost();
        var both = new FrameBuffer(); d.Render(both);
        Assert.Equal(before.Rgba, both.Rgba);
    }

    [GameDataFact]
    public void Menu_AllowsSplitSingleRaceAndSplitSeries()
    {
        foreach (int race in new[] { 0, 1, 2 })
        {
            var m = MenuController.Load();
            m.Config.GameType = 1; m.Config.RaceType = (byte)race;
            m.StartRace();
            Assert.Equal(MenuStatus.Race, m.Status);
            Assert.Equal(2, m.Session!.Setup.NumPlayerCars);
            Assert.Equal(1, m.Session.Setup.CommMode);
        }
    }

    /// <summary>Series race results: the humans are cars 0 and 1 (when there are two of them), <paramref name="loser"/> finishes last.</summary>
    static RaceResults SeriesResults(int cars, int humans, int loser = -1)
    {
        loser = loser < 0 ? cars - 1 : loser;
        var order = Enumerable.Range(0, cars).Where(i => i != loser).Append(loser).ToArray();
        return new RaceResults(order.Select((car, p) => new RacerResult(p + 1, car, "MCF1", car < humans, car >= humans,
            4000 + 100 * p, [], 2000 + 10 * p, Driver: car < humans ? $"PLAYER{car + 1}" : "Slick")).ToArray());
    }

    static void Enter(GameLoop loop, int times)
    {
        var enter = new DesktopInput(default, [MenuKey.Enter], false);
        for (int i = 0; i < times; i++) loop.Frame(1.0 / 32, enter);
    }

    /// <summary>Results → laps → summary → NEXT RACE built from the menu, as in the game (no window).</summary>
    static GameLoop SplitKnockoutAfterHumanZeroOut()
    {
        var loop = new GameLoop();
        var c = loop.Menu.Controller;
        c.Config.GameType = 1; c.Config.RaceType = 2; c.Config.TrafficOff = 1;
        c.StartRace();
        loop.StartRace(new RaceScene(c.Session!));
        Assert.True(loop.Drive!.IsSplit);
        c.CompleteRace(SeriesResults(9, 2, loser: 0));
        Enter(loop, 2);   // statdisp → laps → summary
        Assert.True(c.KnockoutSummary);
        Enter(loop, 1);   // NEXT RACE
        Assert.Equal(MenuStatus.Race, c.Status);
        return loop;
    }

    [GameDataFact]
    public void SplitKnockout_SolePlayerTwo_RacesInOneViewWithHisOwnKeys()
    {
        var loop = SplitKnockoutAfterHumanZeroOut();
        Assert.False(loop.Drive!.IsSplit);
        Assert.Equal(1, loop.Menu.Controller.SoleSplitHuman);
        var gasOfPlayerTwo = new DesktopInput(default, [], false, Split1: default(DriveKeys), Split2: Gas);
        for (int i = 0; i < 16 * 32; i++) loop.Frame(1.0 / 32, gasOfPlayerTwo);   // countdown + 8 s
        Assert.True(loop.Drive.Car.SpeedApprox > 5 * Fx.One, "player 2's keys must drive the only human");

        var other = SplitKnockoutAfterHumanZeroOut();
        var gasOfPlayerOne = new DesktopInput(Gas, [], false, Split1: Gas, Split2: default(DriveKeys));
        for (int i = 0; i < 16 * 32; i++) other.Frame(1.0 / 32, gasOfPlayerOne);
        Assert.True(other.Drive!.Car.SpeedApprox < Fx.One / 2, "player 1's keys must not drive player 2's car");
    }

    [GameDataFact]
    public void SplitSeriesSummaries_DrawBothHumansRows()
    {
        string? dir = Environment.GetEnvironmentVariable("NFS_SPLIT_PNG_DIR");
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

        var tournament = new GameLoop();
        var t = tournament.Menu.Controller;
        t.Config.GameType = 1; t.Config.RaceType = 1;
        t.StartRace(); tournament.StartRace(new RaceScene(t.Session!));
        t.CompleteRace(SeriesResults(8, 2, loser: 5));
        Enter(tournament, 2);
        Assert.True(t.TournamentSummary);
        var fbT = new FrameBuffer(); tournament.Render(fbT);
        Assert.Equal(2, t.Tournament!.Standings.Take(8).Count(i => i < 2));

        var knockout = new GameLoop();
        var k = knockout.Menu.Controller;
        k.Config.GameType = 1; k.Config.RaceType = 2; k.Config.TrafficOff = 1;
        k.StartRace(); knockout.StartRace(new RaceScene(k.Session!));
        k.CompleteRace(SeriesResults(9, 2, loser: 1));
        Enter(knockout, 2);
        Assert.True(k.KnockoutSummary);
        Assert.True(k.Knockout!.CanContinue);
        var fbK = new FrameBuffer(); knockout.Render(fbK);
        Assert.NotEqual(fbT.Sha256(), fbK.Sha256());
        if (dir is { Length: > 0 })
        {
            fbT.SavePng(Path.Combine(dir, "split_tournament_summary.png"));
            fbK.SavePng(Path.Combine(dir, "split_knockout_summary.png"));
        }
    }

    [Fact]
    public void CameraCycle_SkipsTheCockpitInSplit_AndFinishedPlayerHasTheLongTable()
    {
        Assert.Equal(0, RaceCamera.NextMode(3, 0));                  // one player: 1 → 2 → 3 → 0 (cockpit)
        Assert.Equal(1, RaceCamera.NextMode(3, 0, split: true));     // split: skips mode 0 [ref:2938]
        Assert.Equal(2, RaceCamera.NextMode(1, 0, split: true));
        Assert.Equal(3, RaceCamera.NextMode(2, 0, split: true));
        // After finishing, admits orbit mode 4 without the initialization chord. [ref:2939]
        Assert.Equal(4, RaceCamera.NextMode(3, 0, split: true, finished: true));
        Assert.Equal(5, RaceCamera.NextMode(4, 0, split: true, finished: true));
        Assert.Equal((byte)4, NFS.Core.Constants.CameraConstants.SuccessorFinished[RaceCamera.FinishMode]);
        Assert.Equal(4, RaceCamera.NextMode(RaceCamera.FinishMode, 0, split: true, finished: true));
        for (int mode = 1; mode <= 3; mode++) Assert.NotEqual(0, RaceCamera.NextMode(mode, 0, split: true));
    }
}
