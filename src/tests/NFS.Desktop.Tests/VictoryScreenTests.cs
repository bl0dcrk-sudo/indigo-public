using NFS.Data;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Victory screen of statdisp (mode 1): shown before the results table only for [ref:3003]
/// the winning human (place 1, state 2, at least 2 cars); it ends on a key press or after 0x500 ticks of the 128 Hz clock,
/// the brand logo (car7/cpg7 from the brand's QFS, a board) rotates like the Omega logo on the results screen. [ref:3004]</summary>
public class VictoryScreenTests
{
    static RacerResult Racer(int place, bool human, int carType, int state = 2) =>
        new(place, place - 1, "MCF1", human, false, 12000 + place * 100, [2000, 1900, 2100], 1900, state,
            human ? "PLAYER" : "Slick", 0, carType);

    static RaceResults Grid(int winnerCarType, bool winnerHuman = true, int winnerState = 2) => new(
    [
        Racer(1, winnerHuman, winnerCarType, winnerState), Racer(2, !winnerHuman, 1), Racer(3, false, 2), Racer(4, false, 3)
    ]);

    // one scene per class: loading the race for every test slowed the suite down (and the network tests with time limits)
    static readonly Lazy<RaceSession> Session = new(() => new RaceScene("replay").Session);
    static readonly Lazy<VictoryScreen> Screen = new(() => new VictoryScreen());

    static MenuController Menu(RaceResults results)
    {
        var menu = MenuController.Load();
        menu.AdoptRace(Session.Value);
        menu.CompleteRace(results);
        return menu;
    }

    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_VICTORY_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    static FrameBuffer Draw(int model, long ticks, bool animated = true, int unlock = 0)
    {
        var fb = new FrameBuffer();
        Screen.Value.Draw(fb, Racer(1, true, model), 4, ticks, animated, unlock);
        return fb;
    }

    static bool Same(FrameBuffer a, FrameBuffer b)
    {
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (a.Get(x, y) != b.Get(x, y)) return false;
        return true;
    }

    // ---- when the screen appears (no game data needed) ----

    [Fact]
    public void TheWinnerIsAHumanInFirstPlaceWhoFinishedInARaceOfAtLeastTwoCars()
    {
        Assert.Equal("PLAYER", VictoryRules.Winner(Grid(0))!.Driver);
        Assert.Null(VictoryRules.Winner(Grid(0, winnerHuman: false)));              // AI won
        Assert.Null(VictoryRules.Winner(Grid(0, winnerState: 1)));                  // race state other than 2 (did not finish)
        Assert.Null(VictoryRules.Winner(new RaceResults([Racer(1, true, 0)])));     // one car: the condition "1 < number of cars" does not hold
        Assert.Null(VictoryRules.Winner(new RaceResults([Racer(2, true, 0), Racer(1, false, 1)])));   // human second
    }

    [Fact]
    public void TheDurationIs0x500TicksOfThe128HzClock()
    {
        Assert.Equal(0x500, VictoryRules.DurationTicks);
        Assert.Equal(128, VictoryRules.TicksPerSecond);
        Assert.Equal(10.0, VictoryRules.DurationTicks / (double)VictoryRules.TicksPerSecond);
    }

    [Fact]
    public void TheBrandShapeAndFileFollowTheOriginalTables()
    {
        string[] files = ["mac", "ferr", "ferr", "gt90", "indigo", "mus", "jag", "gt1", "lotus", "nazca", "ital", "isdera"];
        for (int m = 0; m < 12; m++) Assert.Equal(files[m], VictoryRules.BrandArt[m]);
        foreach (int m in new[] { 0, 3, 4, 5 }) Assert.Equal("cpg7", VictoryRules.BrandShape(m));
        foreach (int m in new[] { 1, 2, 6, 7, 8, 9, 10, 11 }) Assert.Equal("car7", VictoryRules.BrandShape(m));
        Assert.Equal(-10, VictoryRules.BrandDx(7));
        Assert.Equal(-10, VictoryRules.BrandDx(8));
        Assert.Equal(0, VictoryRules.BrandDx(0));
        Assert.Equal(0, VictoryRules.BrandDx(11));
        Assert.Equal([0, 0, 1, 4, 2, 3, 5, 6, 7], Enumerable.Range(0, 9).Select(VictoryRules.MenuTrack));
    }

    [Fact]
    public void TheAnimationRunsInTenTickFramesFadingInOver35Frames()
    {
        Assert.Equal(0, VictoryRules.Frame(9, true));
        Assert.Equal(1, VictoryRules.Frame(10, true));
        Assert.Equal(0x23, VictoryRules.Frame(350, true));
        Assert.Equal(0x23, VictoryRules.Frame(0, false));
        Assert.Equal(0, VictoryRules.Level(0));
        Assert.Equal(256, VictoryRules.Level(0x23));
        Assert.Equal(0x1E, VictoryRules.Phase(0));
    }

    // ---- menu flow ----

    [GameDataFact]
    public void TheScreenAppearsAfterAWonRaceAndAnyKeyLeadsToTheResultsTable()
    {
        var menu = Menu(Grid(0));
        Assert.True(menu.VictoryActive);
        Assert.Equal("PLAYER", menu.VictoryWinner!.Driver);
        int driver = menu.ResultDriver;
        menu.HandleKey(MenuKey.Right);               // the first key only dismisses the victory screen
        Assert.False(menu.VictoryActive);
        Assert.Equal(driver, menu.ResultDriver);
        Assert.Equal(MenuController.RaceResultsScreen, menu.CurrentScreen.Id);
        menu.HandleKey(MenuKey.Right);               // only the next one acts on the results table
        Assert.NotEqual(driver, menu.ResultDriver);
    }

    [GameDataFact]
    public void TheScreenIsSkippedWhenTheHumanDidNotWin()
    {
        Assert.False(Menu(Grid(0, winnerHuman: false)).VictoryActive);
        Assert.False(Menu(Grid(0, winnerState: 1)).VictoryActive);
        Assert.False(Menu(new RaceResults([Racer(1, true, 0)])).VictoryActive);
    }

    [GameDataFact]
    public void TheClockEndsTheScreenAfter0x500TicksOnly()
    {
        var menu = Menu(Grid(0));
        var scene = new MenuScene(menu);
        scene.Advance(0x4FF / 128.0);
        Assert.True(menu.VictoryActive);
        scene.Advance(2 / 128.0);                    // 0x501 ticks > 0x500
        Assert.False(menu.VictoryActive);
        Assert.Equal(MenuStatus.Results, menu.Status);
        Assert.Equal(MenuController.RaceResultsScreen, menu.CurrentScreen.Id);
    }

    [GameDataFact]
    public void TheSceneRendersTheVictoryScreenFirstAndTheResultsAfterwards()
    {
        var menu = Menu(Grid(0));
        var scene = new MenuScene(menu);
        var victory = new FrameBuffer(); scene.Render(victory);
        menu.EndVictory();
        var results = new FrameBuffer(); scene.Render(results);
        Assert.False(Same(victory, results));
        Review(victory, "scene_victory.png"); Review(results, "scene_results.png");
        var direct = Draw(0, 0, unlock: menu.Config[0x1D]);
        Assert.True(Same(victory, direct), "the scene draws the victory screen with the same code as VictoryScreen");
    }

    // ---- rysunek ----

    [GameDataFact]
    public void TheBrandLogoIsTheRightShapeOfTheRightFileForEveryModel()
    {
        string[] shapes = ["cpg7", "car7", "car7", "cpg7", "cpg7", "cpg7", "car7", "car7", "car7", "car7", "car7", "car7"];
        for (int model = 0; model < 12; model++)
        {
            var logo = Screen.Value.Brand(model);
            Assert.NotNull(logo);
            Assert.Equal(shapes[model], logo!.Name);
            var fb = Draw(model, 100000);            // after the animation: full width, alpha 256 = shape pixels without blending
            int x0 = logo.PosX + VictoryRules.BrandDx(model), y0 = logo.PosY + VictoryRules.BrandDy, opaque = 0;
            for (int sy = 0; sy < logo.Height; sy++)
                for (int sx = 0; sx < logo.Width; sx++)
                {
                    int o = (sy * logo.Width + sx) * 4;
                    if (logo.Rgba[o + 3] == 0) continue;
                    opaque++;
                    uint want = (uint)(logo.Rgba[o] << 16 | logo.Rgba[o + 1] << 8 | logo.Rgba[o + 2]);
                    Assert.Equal(want, fb.Get(x0 + sx, y0 + sy));
                }
            Assert.True(opaque > 500, $"model {model}: {opaque} pikseli");
            if (model is 0 or 1 or 3 or 7) Review(fb, $"victory_model{model:D2}.png");
        }
    }

    [GameDataFact]
    public void TheModelsOfTheSameBrandFileShareTheShapeButDifferentBrandsDiffer()
    {
        var f50 = Screen.Value.Brand(1)!; var f355 = Screen.Value.Brand(2)!;
        Assert.Equal(f50.Rgba, f355.Rgba);                                       // both from FERR.QFS / car7
        Assert.NotEqual(Screen.Value.Brand(0)!.Rgba.Length, f50.Rgba.Length);    // MAC.QFS cpg7 215x100, different from FERR car7 97x160
        Assert.Equal((215, 100), (Screen.Value.Brand(0)!.Width, Screen.Value.Brand(0)!.Height));
        Assert.Null(Screen.Value.Brand(12));                                     // bonus cars have no brand shape
        Assert.Null(Screen.Value.Brand(14));
    }

    [GameDataFact]
    public void TheBrandLogoSpinsThenSettlesAndWithoutAnimationDetailIsDrawnAtOnce()
    {
        var start = Draw(0, 0); var mid = Draw(0, 7 * 10); var end = Draw(0, 40 * 10);
        Assert.False(Same(start, mid));
        Assert.False(Same(mid, end));
        Assert.True(Same(end, Draw(0, 100000)));
        Assert.True(Same(Draw(0, 0, animated: false), end));
        Review(start, "victory_frame00.png"); Review(Draw(0, 4 * 10), "victory_frame04.png"); Review(mid, "victory_frame07.png");
        Review(Draw(0, 14 * 10), "victory_frame14.png"); Review(Draw(0, 20 * 10), "victory_frame20.png"); Review(end, "victory_frame39.png");
    }

    [GameDataFact]
    public void BonusCarsGetThePhotoOnlyWhenUnlockedAndNeverTheBrandLogo()
    {
        Assert.True(Same(Draw(12, 1000), Draw(12, 100000)));                        // without the brand logo the frame is constant
        Assert.False(Same(Draw(12, 1000, unlock: 0), Draw(12, 1000, unlock: 1)));   // 12 < 0xC + 1: Bomber photo
        Assert.True(Same(Draw(13, 1000, unlock: 1), Draw(13, 1000, unlock: 0)));    // 13 >= 0xC + 1: still no photo
        Review(Draw(12, 1000, unlock: 1), "victory_bonus12.png");
    }

    [GameDataFact]
    public void EveryLanguageHasItsCongratulationsShape()
    {
        var baseline = Draw(0, 100000);
        for (int language = 1; language < 6; language++)
        {
            var fb = new FrameBuffer();
            new VictoryScreen(null, language).Draw(fb, Racer(1, true, 0), 4, 100000, true);
            Assert.False(Same(baseline, fb), $"language {language}");
        }
    }
}
