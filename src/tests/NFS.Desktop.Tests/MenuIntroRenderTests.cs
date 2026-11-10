using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Entry of the list panel and the track info screen, drawn with [ref:2807]
/// <see cref="MenuScene.AnimateChrome"/>; without it the scene shows the target frame.</summary>
public class MenuIntroRenderTests
{
    static DesktopInput Input(MenuKey key) => new(default, [key], false);

    /// <summary>Screenshots for review (PNG) - only when <c>NFS_INTRO_REVIEW_DIR</c> is set.</summary>
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_INTRO_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    static FrameBuffer Render(MenuScene scene) { var fb = new FrameBuffer(); scene.Render(fb); return fb; }

    static readonly FrameBuffer Black = new();

    static int Differences(FrameBuffer a, FrameBuffer b, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) if (a.Get(x, y) != b.Get(x, y)) n++;
        return n;
    }

    /// <summary>Left edge of the changed pixels in the strip y 83..313 (list panel) relative to <paramref name="before"/>; 640 = none.</summary>
    static int LeftEdge(FrameBuffer before, FrameBuffer now)
    {
        for (int x = 400; x < 640; x++)
            if (Differences(before, now, x, 83, x + 1, 313) > 0) return x;
        return 640;
    }

    static (GameLoop Loop, string Dir) OpenList()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-intro-").FullName;
        string saves = Directory.CreateDirectory(Path.Combine(dir, "saves")).FullName;
        for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(saves, $"game{i:D2}.tou"), []);
        var loop = new GameLoop(null, false, Path.Combine(dir, "settings.json"));
        var m = loop.Menu.Controller;
        foreach (string item in new[] { "options", "load/save", "load tournament" })
        {
            while (m.CurrentScreen.Items[m.SelectedIndex].Text != item) loop.Frame(0, Input(MenuKey.Down));
            if (item != "load tournament") loop.Frame(0, Input(MenuKey.Enter));
        }
        return (loop, dir);
    }

    [GameDataFact]
    public void ListPanelSlidesInFromTheRightThenTheCancelTabFollows()
    {
        var (loop, dir) = OpenList();
        try
        {
            var scene = loop.Menu;
            var before = Render(scene);
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.List, loop.Menu.Controller.Dialog!.Kind);
            var final = Render(scene);                        // without AnimateChrome: target frame
            Review(final, "list-final.png");
            scene.AnimateChrome = true;
            Render(scene);                                    // the entry clock counts from the window's first appearance
            scene.Advance(MenuScene.SlideStepSeconds * 0.5);  // middle of frame 0
            var all = FrontEndDialog.ListIntroFrames(202);
            var frames = new List<FrameBuffer>();
            for (int k = 0; k < all.Count; k++)
            {
                frames.Add(Render(scene));
                if (k is 0 or 8 or 25 or 30) Review(frames[k], $"list-intro-{k:D2}.png");
                scene.Advance(MenuScene.SlideStepSeconds);
            }
            Assert.Equal(0, Differences(before, frames[0], 400, 0, 640, 480));    // offset 202: panel completely off-screen
            int edge = 640;
            for (int k = 1; k <= 25; k++)                                         // the panel moves toward its place by 8 px per frame
            {
                int left = LeftEdge(before, frames[k]);
                Assert.True(left < edge, $"frame {k}: {left} !< {edge}");
                Assert.InRange(left, 640 - 8 * k - 2, 640 - 8 * k + 2);
                edge = left;
            }
            Assert.InRange(LeftEdge(before, frames[25]), 438, 442);               // offset 2
            // the "tsld" tab (596..640) and "cancel" only arrive in the second phase, once the panel is in place
            Assert.True(Differences(frames[25], frames[^1], 596, 80, 640, 304) > 0, "the tab is drawn only after the panel stopped");
            Assert.Equal(0, Differences(frames[^1], final, 596, 83, 640, 304));   // last entry frame = target (tab; background bars outside the window animate separately)
            Assert.True(Differences(frames[^1], final, 0x1E8, 0x62, 0x1E8 + 120, 0x62 + 22) > 0, "row 0 is not highlighted during the intro");
            scene.Advance(1);
            Assert.Equal(0, Differences(Render(scene), final, 438, 83, 640, 313));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void CancelLettersAreStackedByTheFontHeightTimesZeroPointSix()
    {
        var (loop, dir) = OpenList();
        try
        {
            var m = loop.Menu.Controller;
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.NotNull(m.Dialog);
            var fb = Render(loop.Menu);
            Review(fb, "list-final-cancel.png");
            // "cancel" (English) starts at y = 118 (table), letters every 23 px (swiss18: height 39 · 0.6) [ref:2808]
            int step = FrontEndDialog.VerticalLetterStep(39);
            Assert.Equal(23, step);
            for (int k = 0; k < 6; k++)
                Assert.True(Differences(fb, Black, 0x26C, 118 + k * step, 0x26C + 18, 118 + k * step + 18) > 0, $"letter {k}");
        }
        finally { Directory.Delete(dir, true); }
    }

    static MenuController OpenTrackInfo()
    {
        var menu = MenuController.Load();
        menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Enter);
        Assert.Equal("location", menu.CurrentScreen.Id);
        for (int i = 0; i < 20 && menu.CurrentScreen.Items[menu.SelectedIndex].Text != "track info"; i++) menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Enter);
        Assert.True(menu.InTrackInfo);
        return menu;
    }

    [GameDataFact]
    public void TrackInfoSliderSlidesInOverBlackThenTheBackgroundFadesIn()
    {
        var menu = OpenTrackInfo();
        var final = Render(new MenuScene(menu));
        Review(final, "track-final.png");
        var scene = new MenuScene(menu) { AnimateChrome = true };
        Render(scene);
        scene.Advance(MenuScene.SlideStepSeconds * 0.5);
        var frames = TrackInfoIntro.Frames(menu.Config[MenuScene.DetailOffset]);
        Assert.Equal(27 + 5, frames.Count);
        var shots = new List<FrameBuffer>();
        for (int k = 0; k < frames.Count; k++)
        {
            shots.Add(Render(scene));
            if (k is 0 or 13 or 26 or 28 or 30 or 31) Review(shots[k], $"track-intro-{k:D2}.png");
            scene.Advance(MenuScene.SlideStepSeconds);
        }
        // phase 1: only the "sldr" panel (333 px) on black - right part of the screen empty, and the panel slides to x = 0
        for (int k = 0; k < 27; k++) Assert.Equal(0, Differences(shots[k], Black, 340, 0, 640, 480));
        int Right(FrameBuffer f) { for (int x = 339; x >= 0; x--) if (Differences(f, Black, x, 0, x + 1, 480) > 0) return x; return -1; }
        Assert.InRange(Right(shots[0]), 333 - 0x138 - 8, 333 - 0x138 + 3);    // x = −0x138, width 333 → right edge ≈ 20 (the outermost shape columns are dark)
        Assert.InRange(Right(shots[26]), 322, 333);
        Assert.True(Right(shots[13]) > Right(shots[0]) && Right(shots[26]) > Right(shots[13]));
        // phase 2: the "back" background brightens; the brightness of the right part (outside the panel) rises from black to full
        long Sum(FrameBuffer f) { long s = 0; for (int y = 0; y < 480; y += 4) for (int x = 340; x < 640; x += 4) { uint c = f.Get(x, y); s += (c >> 16 & 255) + (c >> 8 & 255) + (c & 255); } return s; }
        Assert.Equal(0, Sum(shots[27]));                                      // level 0: black background
        for (int k = 28; k < frames.Count; k++) Assert.True(Sum(shots[k]) > Sum(shots[k - 1]), $"fade frame {k}");
        scene.Advance(1);                                                     // after the entry the screen matches the target frame
        Assert.Equal(0, Differences(Render(scene), final, 0, 0, 640, 480));
    }

    [GameDataFact]
    public void ListPanelSlidesOutToTheRightAfterEscapeAndKeysAreIgnoredDuringTheIntroAndTheExit()
    {
        var (loop, dir) = OpenList();
        try
        {
            var scene = loop.Menu;
            var m = scene.Controller;
            scene.AnimateChrome = true;
            loop.Frame(0, Input(MenuKey.Enter));
            var list = m.Dialog;
            Assert.Equal(DialogKind.List, list!.Kind);
            // entry: keys ignored (the original does not read input in these loops)
            loop.Frame(MenuScene.SlideStepSeconds * 3, Input(MenuKey.Down));
            loop.Frame(0, Input(MenuKey.Escape));
            Assert.Same(list, m.Dialog);
            Assert.Equal(0, list.Selected);
            Assert.False(list.Done);
            loop.Frame(2, Input(MenuKey.Down));                   // Advance(2) ends the entry before the key: this one already works
            Assert.Equal(1, list.Selected);
            var final = Render(scene);
            loop.Frame(0, Input(MenuKey.Escape));
            Assert.True(list.Done);
            Assert.Same(list, m.Dialog);                          // the window stays during the exit
            var exit = FrontEndDialog.ListExitFrames();
            Assert.Equal(18, exit.Count);
            var shots = new List<FrameBuffer>();
            scene.Advance(MenuScene.SlideStepSeconds * 0.5);
            for (int k = 0; k < exit.Count; k++)
            {
                shots.Add(Render(scene));
                if (k is 0 or 6 or 12 or 17) Review(shots[k], $"list-exit-{k:D2}.png");
                loop.Frame(0, Input(MenuKey.Enter));              // keys are also ignored during the exit
                Assert.Same(list, m.Dialog);
                if (k < exit.Count - 1) { scene.Advance(MenuScene.SlideStepSeconds); Assert.NotNull(m.Dialog); }
            }
            for (int k = 1; k < exit.Count; k++)                  // the panel and tab move off to the right by 12 px per frame
                Assert.True(Differences(shots[k], shots[k - 1], 438, 83, 640, 313) > 0, $"exit frame {k}");
            scene.Advance(MenuScene.SlideStepSeconds);
            Assert.Null(m.Dialog);                                // end of exit: the calling screen continues
            Assert.Equal(0, Differences(shots[17], Render(scene), 438, 83, 640, 313));   // last frame: panel off-screen
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ListClosesAtOnceWithoutAnimateChrome()
    {
        var (loop, dir) = OpenList();
        try
        {
            loop.Frame(0, Input(MenuKey.Enter));
            Assert.NotNull(loop.Menu.Controller.Dialog);
            loop.Frame(0, Input(MenuKey.Escape));
            Assert.Null(loop.Menu.Controller.Dialog);
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void TrackInfoIgnoresKeysDuringTheIntro()
    {
        var menu = OpenTrackInfo();
        var scene = new MenuScene(menu) { AnimateChrome = true };
        Render(scene);
        menu.HandleKey(MenuKey.Escape);
        Assert.True(menu.InTrackInfo);                            // entry still running
        scene.Advance(2);
        Render(scene);
        menu.HandleKey(MenuKey.Escape);
        Assert.False(menu.InTrackInfo);
    }

    [GameDataFact]
    public void TrackInfoHasNoIntroWhenTheDetailLevelIsZero()
    {
        var menu = OpenTrackInfo();
        menu.Config[MenuScene.DetailOffset] = 0;
        var animated = Render(new MenuScene(menu) { AnimateChrome = true });
        Assert.Equal(0, Differences(animated, Render(new MenuScene(menu)), 0, 0, 640, 480));
    }
}
