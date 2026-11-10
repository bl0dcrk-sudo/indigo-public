using NFS.Data;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Animation of the result screens (statdisp): in mode 0 it animates the sponsor logo "omg7" [ref:2941]
/// (Omega, EPOST.QFS 215x112 @13,27) – not the cars. The frame grows every 10 ticks of the 128 Hz clock, the "squash" phase [ref:2942]
/// adds 0x1E per drawing; after the 0x400 phase, the logo is drawn with alpha frame × 256 / 0x23. [ref:2943]</summary>
public class StatDispLogoTests
{
    const int LogoX = 13, LogoY = 27, LogoW = 215, LogoH = 112;

    static RacerResult Racer(int place, string model, bool human, int carType) =>
        new(place, place - 1, model, human, false, 12000 + place * 100, [2000, 1900, 2100], 1900, 2, human ? "PLAYER" : "Slick", 0, carType);

    static RaceResults Grid(int winnerCarType) => new(
    [
        Racer(1, "MCF1", false, winnerCarType), Racer(2, "FF50", true, 1), Racer(3, "F355", false, 2), Racer(4, "GT90", false, 3)
    ]);

    // one scene per class: loading the race for each screen slowed the whole set down (and the network tests with time limits)
    static readonly Lazy<RaceSession> Session = new(() => new RaceScene("replay").Session);

    static MenuController Menu(RaceResults results, int detail = 2)
    {
        var menu = MenuController.Load();
        menu.AdoptRace(Session.Value);
        menu.CompleteRace(results);
        menu.Config[MenuScene.DetailOffset] = (byte)detail;
        return menu;
    }

    static FrameBuffer Render(ResultScreen screen, RaceResults results, MenuController menu, long ticks)
    {
        screen.SetLogoTicks(ticks);
        var fb = new FrameBuffer();
        screen.Draw(fb, results, menu);
        return fb;
    }

    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_STATDISP_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    /// <summary>Columns of the logo rectangle where the frame differs from the one without the logo (left, right, column count).</summary>
    static (int Min, int Max, int Count) Ink(FrameBuffer fb, FrameBuffer without)
    {
        int min = int.MaxValue, max = int.MinValue, count = 0;
        for (int x = LogoX; x < LogoX + LogoW; x++)
        {
            bool ink = false;
            for (int y = LogoY; y < LogoY + LogoH && !ink; y++) ink = fb.Get(x, y) != without.Get(x, y);
            if (!ink) continue;
            min = Math.Min(min, x); max = Math.Max(max, x); count++;
        }
        return (min, max, count);
    }

    static bool Same(FrameBuffer a, FrameBuffer b)
    {
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (a.Get(x, y) != b.Get(x, y)) return false;
        return true;
    }

    [Fact]
    public void FramesAdvanceEveryTenTicksOfThe128HzClockAndStopAtTheLastFrame()
    {
        Assert.Equal(10, ResultScreen.LogoFrameTicks);
        Assert.Equal(0, ResultScreen.LogoFrame(0, true));
        Assert.Equal(0, ResultScreen.LogoFrame(9, true));
        Assert.Equal(1, ResultScreen.LogoFrame(10, true));
        Assert.Equal(34, ResultScreen.LogoFrame(349, true));
        Assert.Equal(0x23, ResultScreen.LogoFrame(350, true));
        Assert.Equal(0x23, ResultScreen.LogoFrame(100000, true));
        Assert.Equal(0x23, ResultScreen.LogoFrame(0, false));      // config +0x12D = 0: the last frame straight away
    }

    [Fact]
    public void ThePhaseGrowsBy0x1EPerDrawAndTheSpinEndsAfter35Draws()
    {
        Assert.Equal(0x1E, ResultScreen.LogoPhase(0));
        Assert.Equal(0x3C, ResultScreen.LogoPhase(1));
        Assert.Equal(MenuScene.SquashFactor(0x1E), ResultScreen.LogoFactor(0, true));
        Assert.InRange(ResultScreen.LogoFactor(7, true), -6, 6);                       // phase 0xF0 – just before zero (0x100)
        Assert.True(ResultScreen.LogoFactor(8, true) < 0);                             // phase 0x10E – mirror reflection
        Assert.True(ResultScreen.LogoFactor(33, true) < MenuScene.IconSquashFull);     // phase 0x3FC < 0x400 – still scaled
        Assert.Equal(MenuScene.IconSquashFull, ResultScreen.LogoFactor(34, true));     // phase 0x41A ≥ 0x400
        Assert.Equal(MenuScene.IconSquashFull, ResultScreen.LogoFactor(0, false));
    }

    [Fact]
    public void ThePlainDrawFadesInWithFrameTimes256Over0x23()
    {
        Assert.Equal(0, ResultScreen.LogoLevel(0));
        Assert.Equal(34 * 256 / 0x23, ResultScreen.LogoLevel(34));
        Assert.Equal(256, ResultScreen.LogoLevel(0x23));
        Assert.Equal(256, ResultScreen.LogoLevel(1000));
    }

    [GameDataFact]
    public void TheLogoSpinsAroundItsCentreThenSettlesAtFullWidth()
    {
        var results = Grid(winnerCarType: 0);
        var baseline = Grid(winnerCarType: 12);                  // car ≥ 0xC: no logo [ref:2944]
        var menu = Menu(results);
        var screen = new ResultScreen();
        var without = Render(screen, baseline, Menu(baseline), 0);
        var widths = new List<(int Min, int Max, int Count)>();
        for (int frame = 0; frame < 40; frame++)
        {
            var fb = Render(screen, results, menu, frame * 10);
            widths.Add(Ink(fb, without));
            if (frame is 0 or 4 or 7 or 9 or 14 or 20 or 34 or 36) Review(fb, $"results_frame{frame:D2}.png");
        }
        var full = widths[39];
        Assert.True(full.Count >= 200, $"logo {full}");        // 215 px of shape, some columns transparent
        // Phase 0x1E: almost full width, but already narrower than after the animation ends.
        Assert.True(widths[0].Count < full.Count && widths[0].Count > full.Count * 3 / 4, $"{widths[0]} vs {full}");
        // Narrowing to a strip in the phase ~0x100 (frame 7/8), then reflection and stretching back.
        Assert.True(widths[7].Count < full.Count / 8, $"{widths[7]}");
        Assert.True(widths[14].Count > widths[7].Count * 4 && widths[14].Count < full.Count, $"{widths[14]}");
        // Scaling from the centre of the shape (x + w/2): the centre of the range stays in place (before the reflection; after it the ink is asymmetric).
        int centre = (full.Min + full.Max) / 2;
        foreach (int f in new[] { 0, 4 }) Assert.InRange((widths[f].Min + widths[f].Max) / 2, centre - 5, centre + 5);
        // End of motion: from frame 34 the width is constant (alpha 248/256 at frame 34 – the same width).
        Assert.Equal(full, widths[36]);
        Assert.Equal(full, widths[34]);
        Assert.Equal(widths[39], widths[38]);
    }

    [GameDataFact]
    public void ADifferentFrameChangesOnlyTheLogoRectangle()
    {
        var results = Grid(0);
        var menu = Menu(results);
        var screen = new ResultScreen();
        var a = Render(screen, results, menu, 0);
        var b = Render(screen, results, menu, 40);
        bool logoDiffers = false, restDiffers = false;
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (a.Get(x, y) != b.Get(x, y))
                {
                    if (x is >= LogoX and < LogoX + LogoW && y is >= LogoY and < LogoY + LogoH) logoDiffers = true; else restDiffers = true;
                }
        Assert.True(logoDiffers);
        Assert.False(restDiffers);
    }

    [GameDataFact]
    public void WithoutAnimationDetailTheLogoIsDrawnAtOnceAsInTheFinalFrame()
    {
        var results = Grid(0);
        var screen = new ResultScreen();
        var still = Render(screen, results, Menu(results, detail: 0), 0);
        var done = Render(screen, results, Menu(results, detail: 2), 100000);
        Assert.True(Same(still, done));
    }

    [GameDataFact]
    public void TheLogoShapeIsPresentInTheArtOfEveryLanguage()
    {
        for (int language = 0; language < 6; language++)
        {
            var results = Grid(0);
            var screen = new ResultScreen(null, language);
            var without = Render(screen, Grid(12), Menu(Grid(12)), 0);
            Assert.True(Ink(Render(screen, results, Menu(results), 100000), without).Count >= 100, $"language {language}");
        }
    }
}
