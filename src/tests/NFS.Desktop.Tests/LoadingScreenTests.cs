using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Race loading screen and title screen: [ref:2765]
/// image selection (track → PIO/OUT/PAC/LAS/NOR/MED/MYS/MONLOAD.QFS, language → text eng1..swe1 / engl..swed, auto → slide SLD*.QFS),
/// disappears after a time or key press, and is skipped in automatic modes (GameLoop has no screens by default).
/// PNG for review: set NFS_LOADING_REVIEW_DIR (e.g. out/gfx_check/loading).</summary>
public class LoadingScreenTests
{
    static DesktopInput Key(MenuKey key) => new(default, [key], false);
    static DesktopInput Idle => new(default, [], false);

    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_LOADING_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    static bool Same(FrameBuffer a, FrameBuffer b) => Diff(a, b, 0, 0, 640, 480) == 0;

    /// <summary>Number of pixels that differ in the rectangle [x0,x1) × [y0,y1).</summary>
    static int Diff(FrameBuffer a, FrameBuffer b, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (a.Get(x, y) != b.Get(x, y)) n++;
        return n;
    }

    static FrameBuffer Copy(FrameBuffer source)
    {
        var copy = new FrameBuffer();
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++) copy.Set(x, y, source.Get(x, y));
        return copy;
    }

    // ---- image selection (no game data) ----

    [Theory]
    [InlineData(0, "PIOLOAD"), InlineData(2, "OUTLOAD"), InlineData(3, "PACLOAD"), InlineData(4, "LASLOAD"), InlineData(5, "NORLOAD"),
     InlineData(6, "MEDLOAD"), InlineData(7, "MYSLOAD"), InlineData(8, "MONLOAD"), InlineData(-1, "PIOLOAD"), InlineData(9, "PIOLOAD"),
     InlineData(1, "LOAD")]
    public void TheTrackPictureIsChosenByTrackId_OutOfRangeFallsBackToTrackZero(int track, string file) =>
        Assert.Equal($"GAMEDATA/RENDER/PC/{file}.QFS", LoadingScreen.TrackFile(track));

    [Fact]
    public void TheCarSlideNeedsAnUnlockedModel_AndVariantsFollowTheSequenceTable()
    {
        Assert.True(LoadingScreen.HasSlide(0, 0));
        Assert.True(LoadingScreen.HasSlide(11, 0));
        Assert.False(LoadingScreen.HasSlide(12, 0));      // model >= 0xC + unlock level: track screen
        Assert.True(LoadingScreen.HasSlide(12, 1));
        Assert.False(LoadingScreen.HasSlide(15, 9));      // no name in the table [ref:2766]
        Assert.Equal(7, LoadingScreen.SlideVariants(3));
        Assert.Equal(7, LoadingScreen.SlideVariants(5));
        Assert.Equal(8, LoadingScreen.SlideVariants(1));
        Assert.Equal("FEDATA/PC/ART/SLIDES/SLDFER.QFS", LoadingScreen.SlideFile(1, 1));
        Assert.Equal("FEDATA/PC/ART/SLIDES/SLD355F8.QFS", LoadingScreen.SlideFile(2, 8));
        // [0] = 0x5A, [1] = 0x92: (0x5A % 8) + 1 = 3, (0x92 % 7) + 1 = 7; models >= 0xC: always 1, no random draw [ref:2767]
        var screen = new LoadingScreen();
        Assert.Equal(3, screen.NextVariant(1));
        Assert.Equal(7, screen.NextVariant(3));
        Assert.Equal(1, screen.NextVariant(13));
        Assert.Equal(1, LoadingScreen.SlideVariants(14));
        Assert.Equal(2, screen.Sequence);
    }

    // ---- drawing (game data) ----

    [GameDataFact]
    public void TheTrackPictureShowsTheLanguageCaption_AndDiffersPerTrackAndLanguage()
    {
        var eng = new FrameBuffer(); Assert.True(new LoadingScreen(null, 0).DrawTrack(eng, 4, 0));
        var ger = new FrameBuffer(); Assert.True(new LoadingScreen(null, 1).DrawTrack(ger, 4, 0));
        var other = new FrameBuffer(); Assert.True(new LoadingScreen(null, 0).DrawTrack(other, 8, 0));
        Assert.True(Diff(eng, ger, 100, 380, 540, 430) > 500);   // language text (y 382, shape header)
        Assert.Equal(0, Diff(eng, ger, 0, 0, 640, 380));         // the rest of the track image is shared
        Assert.True(Diff(eng, other, 0, 0, 640, 480) > 20000);
        for (int lang = 0; lang < 6; lang++)
        {
            var fb = new FrameBuffer(); Assert.True(new LoadingScreen(null, lang).DrawTrack(fb, 4, 0));
            Review(fb, $"track4_lang{lang}.png");
        }
        foreach (int track in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var fb = new FrameBuffer(); Assert.True(new LoadingScreen().DrawTrack(fb, track, 0));
            Review(fb, $"track{track}_eng.png");
        }
    }

    [GameDataFact]
    public void TheProgressBarGrowsUpwardsWithEachStep()
    {
        var screen = new LoadingScreen();
        var s0 = new FrameBuffer(); screen.DrawTrack(s0, 4, 0);
        var s1 = new FrameBuffer(); screen.DrawTrack(s1, 4, 1);
        var s5 = new FrameBuffer(); screen.DrawTrack(s5, 4, 5);
        var s10 = new FrameBuffer(); screen.DrawTrack(s10, 4, 10);
        Review(s1, "bar_step01.png"); Review(s5, "bar_step05.png"); Review(s10, "bar_step10.png");
        int d1 = Diff(s0, s1, 0, 0, 640, 480), d5 = Diff(s0, s5, 0, 0, 640, 480), d10 = Diff(s0, s10, 0, 0, 640, 480);
        Assert.True(d1 > 0 && d5 > d1 && d10 > d5);
        // step 1 lies at y 0x1B4-35..0x1B4+4, step 10 starts 9 × 40 higher; the bar is 14 px wide
        Assert.Equal(0, Diff(s0, s1, 0, 0, 640, LoadingScreen.BarBottom - 35));
        Assert.Equal(0, Diff(s0, s1, LoadingScreen.BarX + 14, 0, 640, 480));
    }

    [GameDataFact]
    public void TheCarSlideIsOnlyForOrdinaryModels_AndAllVariantsOfEveryCarExist()
    {
        var screen = new LoadingScreen();
        var fb = new FrameBuffer();
        Assert.False(screen.DrawSlide(fb, 12, 0, 1));
        for (int car = 0; car < LoadingScreen.CarSlides.Length; car++)
            for (int v = 1; v <= LoadingScreen.SlideVariants(car); v++)
                Assert.True(screen.DrawSlide(fb, car, 9, v), $"car {car} variant {v}");
        var a = new FrameBuffer(); screen.DrawSlide(a, 1, 0, 1);
        var b = new FrameBuffer(); screen.DrawSlide(b, 1, 0, 2);
        Review(a, "slide_fer1.png"); Review(b, "slide_fer2.png");
        Assert.False(Same(a, b));
    }

    [GameDataFact]
    public void TheTitleShowsTheLanguageCaption()
    {
        var eng = new FrameBuffer(); new TitleScreen(null, 0).Draw(eng);
        var swe = new FrameBuffer(); new TitleScreen(null, 5).Draw(swe);
        Review(eng, "title_eng.png"); Review(swe, "title_swe.png");
        Assert.True(Diff(eng, swe, 120, 415, 520, 465) > 500);
        Assert.Equal(0, Diff(eng, swe, 0, 0, 640, 410));   // the rest of the title is shared
    }

    // ---- behaviour in the loop ----

    [GameDataFact]
    public void ByDefaultTheLoopHasNoStartupScreens_RaceStartsInTheSameFrame()
    {
        var loop = new GameLoop();
        for (int i = 0; i < 4; i++) loop.Frame(0, Key(MenuKey.Down));
        loop.Frame(0, Key(MenuKey.Enter));
        Assert.NotNull(loop.Drive);
        Assert.False(loop.Loading);
        Assert.False(loop.TitleActive);
    }

    [GameDataFact]
    public void TheTitleStaysUntilAKeyIsPressed_ThenTheMenuShows_AndTheKeyIsNotPassedOn()
    {
        var loop = new GameLoop();
        loop.ShowTitle();
        var fb = new FrameBuffer();
        Assert.True(loop.TitleActive);
        for (int i = 0; i < 100; i++) loop.Frame(1.0, Idle);   // time does not end the title
        Assert.True(loop.TitleActive);
        Assert.False(loop.ShowsScene);
        loop.Render(fb);
        Review(fb, "loop_title.png");
        var title = new FrameBuffer(); new TitleScreen().Draw(title);
        Assert.True(Same(fb, title));
        int selected = loop.Menu.Controller.SelectedIndex;
        loop.Frame(0, Key(MenuKey.Down));
        Assert.False(loop.TitleActive);
        Assert.Equal(selected, loop.Menu.Controller.SelectedIndex);   // a key only closes the title
        var menu = new FrameBuffer(); loop.Render(menu);
        Assert.False(Same(menu, title));
    }

    [GameDataFact]
    public void TheLoadingScreenShowsBeforeTheRaceLoads_ThenTheRaceStarts()
    {
        var loop = new GameLoop { LoadingScreens = true };
        for (int i = 0; i < 4; i++) loop.Frame(0, Key(MenuKey.Down));
        loop.Frame(0, Key(MenuKey.Enter));
        Assert.True(loop.Loading);
        Assert.Null(loop.Drive);
        Assert.False(loop.ShowsScene);
        var controller = loop.Menu.Controller;
        int track = controller.Session!.TrackNumber;
        var fb = new FrameBuffer();
        Assert.False(loop.Render(fb, fb));
        Review(fb, "loop_loading_first.png");
        var first = Copy(fb);
        FrameBuffer last = first;
        for (int i = 0; i < 400 && loop.Loading; i++)
        {
            loop.Frame(1.0 / 30, Idle);
            if (!loop.Loading) break;
            loop.Render(fb, fb); last = Copy(fb);
        }
        Review(last, "loop_loading_last.png");
        var reference = new FrameBuffer();
        new LoadingScreen(null, controller.Config.Language).DrawTrack(reference, track, 1);
        // last image = track screen with the language text (the bar is further than step 1, so the comparison excludes the bar at x < 64)
        Assert.Equal(0, Diff(last, reference, 64, 0, 640, 480));
        Assert.False(loop.Loading);
        Assert.NotNull(loop.Drive);
        Assert.True(loop.ShowsScene);
    }

    [GameDataFact]
    public void TheLoadingScreenLastsAFixedTime_AndKeysDoNotSkipOrLeakIntoTheRace()
    {
        var loop = new GameLoop { LoadingScreens = true };
        for (int i = 0; i < 4; i++) loop.Frame(0, Key(MenuKey.Down));
        loop.Frame(0, Key(MenuKey.Enter));
        var fb = new FrameBuffer();
        loop.Render(fb, fb);
        loop.Frame(0.1, Key(MenuKey.Escape));   // nothing skips the screen
        Assert.True(loop.Loading);
        double total = 0; int frames = 0;
        while (loop.Loading && frames < 1000) { loop.Render(fb, fb); loop.Frame(1.0 / 60, Idle); total += 1.0 / 60; frames++; }
        Assert.InRange(total, LoadingScreen.TrackSeconds, LoadingScreen.SlideSeconds + LoadingScreen.TrackSeconds + 0.2);
        Assert.NotNull(loop.Drive);
    }
}
