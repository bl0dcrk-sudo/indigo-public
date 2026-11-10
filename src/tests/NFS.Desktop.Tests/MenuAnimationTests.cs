using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Menu animation (stage 2): panel slide, tournament badge [ref:2771]
/// colour swatch. [ref:2772]</summary>
public class MenuAnimationTests
{
    const string Main = "main", Location = "location", PlayerOne = "player-one";

    static MenuController Open(string screen)
    {
        var menu = MenuController.Load();
        MenuKey[] path = screen switch
        {
            Main => [],
            Location => [MenuKey.Down, MenuKey.Enter],
            "0x4C1014x" => [MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter, MenuKey.Down, MenuKey.Enter],
            _ => [MenuKey.Down, MenuKey.Down, MenuKey.Enter]
        };
        foreach (var key in path) menu.HandleKey(key);
        if (screen != "0x4C1014x") Assert.Equal(screen, menu.CurrentScreen.Id);
        return menu;
    }

    static FrameBuffer Draw(MenuScene scene) { var fb = new FrameBuffer(); scene.Render(fb); return fb; }

    /// <summary>Left edge of the configuration icon column (the furthest-out icon is at 0x1DF + 0x1C) [ref:2773]
    /// the bar animates on its own clock, so the "static frame" tests compare the frame without it.</summary>
    const int IconsX0 = 496;

    static int Differences(FrameBuffer a, FrameBuffer b, int x0 = 0, int y0 = 0, int x1 = 640, int y1 = 480)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) if (a.Get(x, y) != b.Get(x, y)) n++;
        return n;
    }

    [Fact]
    public void BarsStartAtRestAndTheFourthBarGrowsThenShrinksLikeTheOriginal()
    {
        var a = new MenuAnimation();
        Assert.Equal([0x28, 0x28, 0x28, 0x28], a.Level);
        a.Step();                                   // speed is chosen first, the level moves on the next step
        Assert.Equal(3, a.Speed[3]);
        Assert.Equal(0x28, a.Level[3]);
        Assert.InRange(a.Variant[3], 2, 3);         // (even + 2) % 6 plus one random bit: bspb/bsps -> bndb/bnds
        for (int i = 0; i < 23; i++) a.Step();
        Assert.Equal(0x28 + 3 * 23, a.Level[3]);    // 0x6D, still growing
        a.Step();
        Assert.Equal(0, a.Speed[3]);                // > 0x6D stops it
        Assert.Equal(0x70, a.Level[3]);
        a.Step();
        Assert.Equal(-3, a.Speed[3]);
        while (a.Level[3] > 0x28) a.Step();
        Assert.Equal(0x28, a.Level[3]);             // back at rest
        Assert.Equal(0, a.Speed[3]);
    }

    [Fact]
    public void OtherBarsFlickerWithRandomOffsetsAndNeverLeaveTheirRange()
    {
        var a = new MenuAnimation();
        bool moved = false;
        for (int n = 0; n < 4000; n++)
        {
            a.Step();
            for (int i = 0; i < 3; i++)
            {
                Assert.InRange(a.Level[i], 0x28, 0xB3 + 19);
                Assert.InRange(a.OffsetX[i], -12, 11);
                Assert.InRange(a.OffsetY[i], i == 2 ? -0x2A : -12, 11);
                Assert.InRange(a.Variant[i], 0, 1);
                moved |= a.Level[i] > 0x28;
            }
        }
        Assert.True(moved);
    }

    [Fact]
    public void ClockAdvancesOneStepPerTwentyTimerTicks_AndTitleColourCyclesEvery80Steps()
    {
        var a = new MenuAnimation();
        a.Advance(MenuAnimation.StepSeconds * 0.99);
        Assert.Equal(0, a.Counter);
        a.Advance(MenuAnimation.StepSeconds * 0.02);
        Assert.Equal(1, a.Counter);
        Assert.Equal(0.15625, MenuAnimation.StepSeconds);
        a = new MenuAnimation();
        for (int i = 0; i < 40; i++) a.Step();
        Assert.Equal((0, 127), (a.TitleIndex, a.TitleFade));  // 40 * 255 / 80
        for (int i = 0; i < 40; i++) a.Step();
        Assert.Equal((1, 0), (a.TitleIndex, a.TitleFade));
        for (int i = 0; i < 240; i++) a.Step();
        Assert.Equal((0, 0), (a.TitleIndex, a.Counter));      // modulo 0x140
    }

    [GameDataFact]
    public void MenuClockMovesBarsAndTitle_FrameAtTPlusDtDiffers()
    {
        var scene = new MenuScene(Open(Main)) { AnimateChrome = true };
        var still = Draw(new MenuScene(Open(Main)));
        var t0 = Draw(scene);
        Assert.True(Differences(t0, still) > 0, "bars at rest (level 0x28) are drawn translucently over the background");
        Assert.Equal(t0.Sha256(), Draw(scene).Sha256());                       // no time, no change
        scene.Advance(MenuAnimation.StepSeconds * 10);
        var t1 = Draw(scene);
        Assert.NotEqual(t0.Sha256(), t1.Sha256());
        Assert.True(scene.Animation.Level[3] > 0x28);
        // Title: colour 0 (gren) -> cross-fade into colour 1 (redd) -> colour 1 alone, all inside the title shape's box.
        var art = Fsh.Load("FEDATA/PC/ART/MAIN.QFS");
        var gren = art.Decode(art.Find("gren")!, art.GlobalPalette())!;
        int InBox(FrameBuffer a, FrameBuffer b) => Differences(a, b, gren.PosX, gren.PosY, gren.PosX + gren.Width, gren.PosY + gren.Height);
        var counter10 = t1;
        while (scene.IconStepPending) Draw(scene);              // the icons finish their pulse (the gate then holds the bar clock) [ref:2774]
        scene.Advance(MenuAnimation.StepSeconds * 30);          // counter 40: half faded into the next colour
        var half = Draw(scene);
        scene.Advance(MenuAnimation.StepSeconds * 40);          // counter 80: next colour fully in
        Assert.Equal(1, scene.Animation.TitleIndex);
        var full = Draw(scene);
        Assert.True(InBox(counter10, half) > 0 && InBox(half, full) > 0);
        scene.Advance(MenuAnimation.StepSeconds * 80);          // counter 160: colour 2 (yell), clearly not colour 1
        Assert.Equal(2, scene.Animation.TitleIndex);
        Assert.True(InBox(full, Draw(scene)) > 0);
    }

    [Fact]
    public void KeyPressStopsTheBarsForTwentyTimerTicks()
    {
        var quiet = new MenuAnimation();
        for (int i = 0; i < 4; i++) quiet.Advance(MenuAnimation.StepSeconds);
        Assert.Equal(4, quiet.Steps);

        var typed = new MenuAnimation();
        typed.Advance(MenuAnimation.StepSeconds);
        Assert.Equal(1, typed.Steps);
        typed.Advance(MenuAnimation.StepSeconds, keyPressed: true);     // pause timer set to PauseTicks: the step falls here [ref:2775] [ref:3453]
        Assert.Equal(1, typed.Steps);
        Assert.True(typed.Paused);
        typed.Advance(MenuAnimation.StepSeconds * 0.5);                 // the pause is still running (0x14 ticks at 128 Hz)
        Assert.Equal(1, typed.Steps);
        Assert.Equal(0.15625, MenuAnimation.PauseTicks / 128.0);
        typed.Advance(MenuAnimation.StepSeconds * 0.5);                 // the gate released: step immediately [ref:2776]
        Assert.Equal(2, typed.Steps);
        Assert.False(typed.Paused);
        typed.Advance(MenuAnimation.StepSeconds);
        Assert.Equal(3, typed.Steps);
    }

    [GameDataFact]
    public void MenuKeyThroughTheGameLoopPausesTheBars()
    {
        var loop = new GameLoop();
        loop.Menu.AnimateChrome = true;
        loop.Frame(MenuAnimation.StepSeconds, new DesktopInput(default, [], false));
        long steps = loop.Menu.Animation.Steps;                       // one frame without a key = one step
        Assert.Equal(1, steps);
        loop.Frame(0, new DesktopInput(default, [MenuKey.Down], false));
        Assert.True(loop.Menu.Animation.Paused);                      // pause timer set to PauseTicks [ref:2777] [ref:3454]
        loop.Frame(MenuAnimation.StepSeconds * 0.5, new DesktopInput(default, [], false));
        Assert.Equal(steps, loop.Menu.Animation.Steps);
        loop.Frame(MenuAnimation.StepSeconds * 0.5, new DesktopInput(default, [], false));
        Assert.Equal(steps + 1, loop.Menu.Animation.Steps);
        Assert.False(loop.Menu.Animation.Paused);
    }

    [GameDataFact]
    public void DetailLevelBelowTwoDrawsNoChromeAtAll()
    {
        var menu = Open(Main);
        menu.Config[MenuScene.DetailOffset] = 1;                        // < 2 [ref:2778]
        var scene = new MenuScene(menu) { AnimateChrome = true };
        var off = Draw(scene);
        scene.Advance(MenuAnimation.StepSeconds * 10);
        Assert.Equal(0L, scene.Animation.Steps);                        // exits before the step and the drawing [ref:2779]
        // Outside the icon column the frame does not change. The column also pulses when detail < 2, as in the original [ref:2780]
        // the bar runs on both paths, and the phase is counted from the 128 Hz hardware counter. [ref:2781]
        var next = Draw(scene);
        Assert.Equal(0, Differences(off, next, 0, 0, IconsX0, 480));
        Assert.True(Differences(off, next, IconsX0, 0, 640, 480) > 0);
        menu.Config[MenuScene.DetailOffset] = 2;
        var on = Draw(new MenuScene(menu) { AnimateChrome = true });
        Assert.True(Differences(off, on) > 0);                          // bars, title, "side" and "logo" come back
    }

    [GameDataFact]
    public void ChromeIsStaticUntilTheGameLoopEnablesAnimation()
    {
        var scene = new MenuScene(Open(Main));
        var a = Draw(scene);
        scene.Advance(5);
        // the chrome is still, but the icon bar runs on its own 128 Hz clock - the squash of the highlighted icon keeps moving [ref:2782]
        // without AnimateChrome, so we compare everything outside the icon column.
        Assert.Equal(0, Differences(a, Draw(scene), 0, 0, IconsX0, 480));
    }

    [GameDataFact]
    public void PreviewPanelSlidesInFromTheLeftAfterTheTrackChanges()
    {
        var menu = Open(Location);
        var scene = new MenuScene(menu) { AnimateChrome = true };
        Draw(scene);                                       // entering the screen draws the panel in place [ref:2783]
        menu.Config.Track = 3;
        var first = Draw(scene);                           // x = -13 for the first frame [ref:2784]
        const int y0 = 0x13C, y1 = y0 + 0x80;
        var finalPanel = Draw(new MenuScene(menu));        // static render = panel at 0xD3
        Assert.True(Differences(first, finalPanel, 0xD3 + 0x100, y0, 0xD3 + 0x118, y1) > 0, "panel not yet at its final place");
        scene.Advance(MenuScene.SlideStepSeconds * 1.5);
        var second = Draw(scene);
        Assert.NotEqual(first.Sha256(), second.Sha256());  // moved by 0x40
        scene.Advance(MenuScene.SlideStepSeconds * 3);
        var done = Draw(scene);
        Assert.Equal(0, Differences(done, finalPanel, 0xD3, y0, 0xD3 + 0x118, y1));
        Assert.Equal([-13, 51, 115, 179], Enumerable.Range(0, MenuScene.SlideSteps).Select(i => MenuScene.SlideStart + i * MenuScene.SlideStepPixels));
    }

    static void SetSeries(MenuController menu, TournamentSeries? series) =>
        typeof(MenuController).GetProperty(nameof(MenuController.Tournament))!.SetValue(menu, series);

    static MenuController TournamentMenu(int seriesTrack)
    {
        var menu = Open(Location);
        menu.Config.RaceType = 1;
        var series = new TournamentSeries(seriesTrack);
        var order = Enumerable.Range(1, 7).ToList(); order.Insert(0, 0);
        series.Complete(new RaceResults(order.Select((id, p) => new RacerResult(p + 1, id, $"CAR{id}", id == 0, id != 0, 1000 + p * 100, [], null, 2)).ToArray()));
        SetSeries(menu, series);
        Assert.Equal(1 << (seriesTrack == 2 ? 4 : 0), series.CompletedMask);
        return menu;
    }

    [GameDataFact]
    public void TournamentBadgeAppearsOnlyForACompletedTrackOfTheSeries()
    {
        var menu = TournamentMenu(2);                           // track 2 = bit 4 of the mask (case 2) [ref:2785]
        var series = menu.Tournament!;
        int Badge(int track)
        {
            menu.Config.Track = (ushort)track; menu.Config.RaceType = 1;
            var withBadge = Draw(new MenuScene(menu));
            SetSeries(menu, null);
            var plain = Draw(new MenuScene(menu));
            SetSeries(menu, series);
            // trnm at (0xD3 + 0xB5, 0x13C + 0x2D), language word at (.., +0xA): only the panel's right side may differ.
            int inside = Differences(withBadge, plain, 0xD3 + 0xB5, 0x13C + 0xA, 0xD3 + 0x118, 0x13C + 0x80);
            Assert.Equal(inside, Differences(withBadge, plain, 0xD3, 0x13C, 0xD3 + 0x118, 0x13C + 0x80));
            return inside;
        }
        Assert.True(Badge(2) > 0);
        Assert.Equal(0, Badge(3));                              // bit 2, not completed
        Assert.Equal(0, Badge(4));
    }

    [GameDataFact]
    public void ColourSwatchShowsTheConfiguredCarColour()
    {
        var menu = Open(PlayerOne);
        var item = menu.CurrentScreen.Items.First(i => i.Action == 15 && i.ValueRaw == 1);
        var entry = TextFile.Load("ENG").Entries[item.TextId];
        int x = entry.X + (entry.FontSize < 18 ? 0xD : 0x13), y = entry.Y;
        var seen = new HashSet<uint>();
        foreach (byte paint in new byte[] { 0, 5, 20, 40 })
        {
            menu.Config[0x21 + menu.Config.P1Car] = paint;
            var fb = Draw(new MenuScene(menu));
            uint expected = MenuScene.SwatchColour(paint);
            Assert.Equal(expected, fb.Get(x + 1 + 40, y + 5 + 10));
            Assert.Equal(expected, fb.Get(x + 1, y + 5));
            Assert.Equal(0u, fb.Get(x + 2 + 0x53, y + 6 + 0x14));   // black shadow below / right
            seen.Add(expected);
        }
        Assert.True(seen.Count >= 3);
    }

    [Fact]
    public void LevelBlitMixesTowardTheBackgroundLikeTheTranslucentPalette()
    {
        var fb = new FrameBuffer(); fb.Clear(100, 100, 100);
        var image = new FshImage("t", 2, 1, [200, 0, 0, 255, 0, 0, 0, 0], 0, 0, 0, 0);
        MenuScene.BlitLevel(fb, image, 0, 0, 128);
        Assert.Equal((uint)(((200 * 128 >> 8) + (100 * (255 - (255 * 128 >> 8)) >> 8)) << 16 | 50 << 8 | 50), fb.Get(0, 0));
        Assert.Equal(0x646464u, fb.Get(1, 0));          // transparent pixel untouched
        MenuScene.BlitLevel(fb, image, 0, 0, 0);        // level 0 draws nothing
        Assert.Equal(0x646464u, fb.Get(1, 0));
    }

    [GameDataFact]
    public void FontEdgesInterpolateTowardsBlackInsteadOfBlendingWithTheBackground()
    {
        var font = new FrontEndFont();
        var fb = new FrameBuffer(); fb.Clear(200, 40, 40);
        font.Draw(fb, "A", 100, 50, align: 0);                  // white (colour 2) on red
        int glyph = 0;
        for (int y = 50; y < 75; y++)
            for (int x = 100; x < 115; x++)
            {
                uint p = fb.Get(x, y);
                if (p == 0xC82828u) continue;                   // untouched background
                glyph++;
                Assert.Equal(p >> 16 & 255, p & 255);           // grey ramp entry, no trace of the red background
                Assert.True((p & 255) is > 0 and <= 255 * 15 >> 4);
            }
        Assert.True(glyph > 20);
    }

    [GameDataFact]
    public void VolumeSlidersShowTheLevelOfEachRowAndHighlightTheSelectedOne()
    {
        var menu = Open("0x4C1014x");
        Assert.Equal("audio", menu.CurrentScreen.Id);
        Assert.Equal(-0xA3, MenuScene.VolumeBarOffset(0));
        Assert.Equal(0x8C - 0xA3, MenuScene.VolumeBarOffset(0x7F));
        Assert.Equal(0x8C - 0xA3, MenuScene.VolumeBarOffset(128));           // config default 128 is clamped to the 0..0x7F range
        menu.Config[0x5C] = 0; menu.Config[0x5D] = 0x7F; menu.Config[0x5E] = 0x40;
        var low = Draw(new MenuScene(menu));
        menu.Config[0x5C] = 0x7F;
        var high = Draw(new MenuScene(menu));
        // Row 0 (dy = 6, shapes 31 / 10 px high at y = 141 + 6 ...): only the bar of the first row changed, and only right of x = 0x188.
        Assert.True(Differences(low, high, 0x188, 141 + 6, 640, 141 + 6 + 31) > 0);
        Assert.Equal(0, Differences(low, high, 0, 0, 0x188, 480));
        Assert.Equal(0, Differences(low, high, 0, 141 + 6 + 31, 640, 480));
        // The selected row is drawn at full level, the others dimmed (0x8C): moving the selection changes the track pixels.
        menu.HandleKey(MenuKey.Down);
        var second = Draw(new MenuScene(menu));
        Assert.True(Differences(high, second, 409, 141 + 6, 409 + 183, 141 + 6 + 31) > 0);
        Assert.True(Differences(high, second, 409, 141 + 0x21, 409 + 183, 141 + 0x21 + 31) > 0);
    }
}
