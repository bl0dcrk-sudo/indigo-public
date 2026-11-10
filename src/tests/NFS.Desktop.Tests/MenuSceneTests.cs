using NFS.Data;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class MenuSceneTests
{
    [Fact]
    public void DesktopStartsAtMenu_ExplicitTrackAndRaceStillSelectTheirViews()
    {
        Assert.True(DesktopOptions.Parse([]).Menu);
        Assert.False(DesktopOptions.Parse(["--track", "4"]).Menu);
        Assert.False(DesktopOptions.Parse(["--race", "replay"]).Menu);
        Assert.True(DesktopOptions.Parse(["--track", "4", "--menu"]).Menu);
    }

    [GameDataFact]
    public void OriginalBackgroundAndSelectionAreVisible_WithoutGraphicsDevice()
    {
        var menu = MenuController.Load();
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Assert.Equal("main/blk6", scene.BackgroundSource);
        var fsh = Fsh.Load("FEDATA/PC/ART/MAIN.QFS");
        var original = fsh.Decode(fsh.Find("blk6")!, fsh.GlobalPalette())!;
        // Right of the side panel the original bitmap is shown at full brightness (draws it as is). [ref:2823]
        int i = (350 * original.Width + 500) * 4;
        Assert.Equal((uint)(original.Rgba[i] << 16 | original.Rgba[i + 1] << 8 | original.Rgba[i + 2]),
            fb.Get(500 + original.PosX, 350 + original.PosY));
        string first = fb.Sha256();
        menu.HandleKey(MenuKey.Down);
        scene.Render(fb);
        Assert.NotEqual(first, fb.Sha256());
        Assert.All(fb.Rgba.Where((_, n) => n % 4 == 3), a => Assert.Equal(255, a));
    }

    [GameDataFact]
    public void SelectedManualTransmissionReachesDesktopRunner()
    {
        var menu = MenuController.Load();
        foreach (var key in new[] { MenuKey.Down, MenuKey.Down, MenuKey.Enter, MenuKey.Down, MenuKey.Left,
            MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter,
            MenuKey.Down, MenuKey.Down, MenuKey.Enter }) menu.HandleKey(key);
        Assert.Equal(MenuStatus.Race, menu.Status);
        var scene = new RaceScene(menu.Session!);
        Assert.Same(menu.Session, scene.Session);
        var drive = new RaceDrive(scene, withAi: true, automatic: null);
        Assert.False(drive.Runner.Automatic);
        drive.Step(default);
        Assert.Equal(2, drive.Runner.Tick);
    }

    [GameDataFact]
    public void MenuRaceNaturalFinishResultsAndReturn_UseTheSameSelections()
    {
        var menu = MenuController.Load();
        foreach (var key in new[] { MenuKey.Down, MenuKey.Enter, MenuKey.Right, MenuKey.Down, MenuKey.Left,
            MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter,
            MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter }) menu.HandleKey(key);
        Assert.Equal(MenuStatus.Race, menu.Status);
        Assert.Equal(2, menu.Session!.TrackNumber);
        Assert.Equal(2, menu.Session.Setup.NumLaps);
        var drive = new RaceDrive(new RaceScene(menu.Session), withAi: true, countdown: false, automatic: null);
        for (int n = 0; n < 100; n++) drive.Step(default);
        drive.Car.Lap = 1; // Feed crossings to the existing authoritative lap task.
        drive.Step(default);
        for (int n = 0; n < 50; n++) drive.Step(default);
        drive.Car.Lap = 2;
        drive.Step(default);
        for (int n = 0; n < 170 && drive.Results == null; n++) drive.Step(default);
        Assert.NotNull(drive.Results);
        menu.CompleteRace(drive.Results!);
        Assert.Equal(MenuStatus.Results, menu.Status);
        menu.EndVictory();   // The victory screen precedes the results table (VictoryScreenTests) [ref:2824]
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        Assert.Contains((byte)(255 * 15 >> 4), fb.Rgba.Where((_, i) => i % 4 == 0)); // full glyph pixel = ramp entry 15 [ref:2825]
        menu.HandleKey(MenuKey.Enter); // continue to the next screen [ref:2826]
        menu.HandleKey(MenuKey.Enter); // single-race summary -> main
        Assert.Equal(MenuStatus.Menu, menu.Status);
        Assert.Equal("main", menu.CurrentScreen.Id);
        Assert.Equal(2, menu.BuildSetup().Track);
        Assert.Equal(2, menu.BuildSetup().NumLaps);
        Assert.Null(menu.Session);
    }
}
