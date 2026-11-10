using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Car showcase: from the PLAYER ONE / PLAYER TWO screen [ref:2901] [ref:3458]
/// page texts (from the tables) and the "car7" logo. [ref:2902]</summary>
public class ShowcaseTests
{
    /// <summary>First TEXT id of a showcase car's value column per page (<c>FrontEndConstants.ShowcasePage*Texts</c>).</summary>
    static int FirstValueId(int car, int page) => (page switch
    {
        1 => NFS.Core.Constants.FrontEndConstants.ShowcasePage1Texts,
        2 => NFS.Core.Constants.FrontEndConstants.ShowcasePage2Texts,
        _ => NFS.Core.Constants.FrontEndConstants.ShowcasePage3Texts,
    })[car];

    const string PlayerOne = "player-one", Showcase = MenuController.ShowcaseScreen;
    /// <summary>Page label texts 1 / 2 / 3 (page 2 ends with a space, TEXT 0x157). [ref:2903]</summary>
    static readonly int[] Page1Labels = [381, 382, 383, 384, 385, 386, 387, 388];
    static readonly int[] Page2Labels = [565, 566, 567, 568, 569, 570, 571, 343];
    static readonly int[] Page3Labels = [473, 474, 475, 476, 477, 478, 479, 480];

    /// <summary>Menu → PLAYER ONE → "showcase"; first car from the config. [ref:2904]</summary>
    static MenuController OpenShowcase(int? car = null)
    {
        var menu = MenuController.Load();
        if (car is { } c) menu.Config.P1Car = (byte)c;
        foreach (var key in new[] { MenuKey.Down, MenuKey.Down, MenuKey.Enter }) menu.HandleKey(key);
        Assert.Equal(PlayerOne, menu.CurrentScreen.Id);
        int showcase = menu.CurrentScreen.Items.ToList().FindIndex(i => i.Target == Showcase);
        for (int i = 0; menu.SelectedIndex != showcase; i++)
        {
            Assert.True(i < 10, "the 'showcase' entry is not reachable");
            menu.HandleKey(MenuKey.Down);
        }
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Showcase, menu.CurrentScreen.Id);
        return menu;
    }

    static int[] TextIds(MenuController menu, int first, int count) =>
        menu.CurrentScreen.Items.Skip(first).Take(count).Select(i => i.TextId).ToArray();

    static Fsh CarArt(MenuController menu) => Fsh.Load($"FEDATA/PC/ART/{menu.ShowcaseArt.ToUpperInvariant()}.QFS");

    static FshImage Img(Fsh art, string name) => art.Decode(art.Find(name)!, art.GlobalPalette())!;

    /// <summary>Screen drawing in the original's order: "back" from the car's QFS, the page (gnrl/perf/mech) [ref:2905]
    /// the "car7" logo at the <paramref name="logoLevel"/> level, and the entries. [ref:2906]</summary>
    static FrameBuffer Reference(MenuController menu, string page, int logoLevel)
    {
        var art = CarArt(menu);
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        foreach (string name in new[] { "back", page })
        {
            var image = Img(art, name);
            OriginalHud.Blit(fb, image, image.PosX, image.PosY);
        }
        var logo = Img(art, "car7");
        MenuScene.BlitLevel(fb, logo, logo.PosX, logo.PosY + MenuScene.ShowcaseLogoDy, logoLevel);
        DrawItems(fb, menu, new FrontEndFont(), TextFile.Load("ENG"));
        return fb;
    }

    /// <summary>For a screen with text-only entries. [ref:2907]</summary>
    static void DrawItems(FrameBuffer fb, MenuController menu, FrontEndFont font, TextFile text)
    {
        var items = menu.CurrentScreen.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind is not (0 or 2) || item.TextId >= text.Entries.Count) continue;
            var e = text.Entries[item.TextId];
            // The history and slide-show screens take the entry colour from the TEXT record (221/232/233: 0 = white).
            int colour = (item.Flags & 1) != 0 ? 6 : i == menu.SelectedIndex ? e.HighlightColour : e.Colour;
            font.Draw(fb, e.Text, e.X, e.Y, e.FontSize, e.Align, colour);
            if ((item.Flags & 2) != 0)
                font.Draw(fb, "...", e.X + (e.FontSize < 18 ? 0xD : 0x13), e.Y, e.FontSize, e.Align, colour);
        }
    }

    /// <summary>First pixel that differs between the drawing and the pattern (null = identical).</summary>
    static string? Diff(FrameBuffer expected, FrameBuffer fb, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < Math.Min(y1, fb.Height); y++)
            for (int x = x0; x < Math.Min(x1, fb.Width); x++)
                if (fb.Get(x, y) != expected.Get(x, y)) return $"pixel {x},{y}: {fb.Get(x, y):X6} != {expected.Get(x, y):X6}";
        return null;
    }

    [GameDataFact]
    public void ShowcaseOpensOnThePlayersCarWithTheGeneralPage()
    {
        var menu = OpenShowcase(car: 0);
        Assert.Equal(0, menu.ShowcaseCar);
        Assert.Equal(1, menu.ShowcasePage);
        Assert.Equal("mac", menu.ShowcaseArt);
        // Page 1: eight GENERAL labels and seven car values. [ref:2908]
        Assert.Equal(Page1Labels, TextIds(menu, 6, 8));
        Assert.Equal(Enumerable.Range(FirstValueId(0, 1), 7), TextIds(menu, 14, 7));
        // Entry texts come from TEXT.ENG.
        var text = TextFile.Load("ENG");
        Assert.Equal("showcase", text[79]);
        Assert.Equal("GENERAL", menu.CurrentScreen.Items[6].Text);
        Assert.Equal(text[FirstValueId(0, 1)], menu.CurrentScreen.Items[14].Text);
    }

    [GameDataFact]
    public void LeftRightSwitchThePageAndWrapInBothDirections()
    {
        var menu = OpenShowcase(car: 0);
        menu.HandleKey(MenuKey.Down); // entry 1 = "more stats"
        Assert.Equal(1, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(2, menu.ShowcasePage);
        Assert.Equal(Page2Labels, TextIds(menu, 6, 8));
        Assert.Equal(Enumerable.Range(FirstValueId(0, 2), 5).Append(343).Append(343), TextIds(menu, 14, 7));
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(3, menu.ShowcasePage);
        Assert.Equal(Page3Labels, TextIds(menu, 6, 8));
        Assert.Equal(Enumerable.Range(FirstValueId(0, 3), 7), TextIds(menu, 14, 7));
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.ShowcasePage); // 3 → 1
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(3, menu.ShowcasePage); // 1 → 3
        Assert.Equal(1, menu.SelectedIndex); // changing the page does not move the selection
    }

    [GameDataFact]
    public void CarIsPickedWithLeftRightOnTheFirstItemAndTheTextsFollow()
    {
        var menu = OpenShowcase(car: 0);
        Assert.Equal(0, menu.SelectedIndex); // entry 0 = "next car"
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.ShowcaseCar);
        Assert.Equal("ferr", menu.ShowcaseArt);
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(0, menu.ShowcaseCar);
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(MenuController.ShowcaseCarCount - 1, menu.ShowcaseCar); // 0 → 11
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(0, menu.ShowcaseCar); // 11 → 0
        // Changing the car rewrites the values of the current page, and the page stays.
        menu.HandleKey(MenuKey.Down); // "more stats"
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(2, menu.ShowcasePage);
        menu.HandleKey(MenuKey.Up); // "next car"
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Right); // car 0 → 2
        Assert.Equal(2, menu.ShowcaseCar);
        Assert.Equal(2, menu.ShowcasePage);
        Assert.Equal("f355", menu.ShowcaseArt);
        Assert.Equal(Enumerable.Range(FirstValueId(2, 2), 5).Append(343).Append(343), TextIds(menu, 14, 7));
    }

    [GameDataFact]
    public void DoneKeepsTheBrowsedCarAndEscRestoresThePreviousOne()
    {
        var menu = OpenShowcase(car: 0);
        menu.HandleKey(MenuKey.Right);
        for (int i = 0; i < 5; i++) menu.HandleKey(MenuKey.Down);
        Assert.Equal(5, menu.SelectedIndex); // "done"
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(PlayerOne, menu.CurrentScreen.Id);
        Assert.Equal(1, menu.Config.P1Car); // saves the car being viewed [ref:2909]
        Assert.Equal(5, menu.SelectedIndex); // returns to the entry we entered from

        menu = OpenShowcase(car: 0);
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Escape);
        Assert.Equal(PlayerOne, menu.CurrentScreen.Id);
        Assert.Equal(0, menu.Config.P1Car); // ESC undoes the browsing (a copy of the config from before entering the screen)
    }

    [GameDataFact]
    public void ShowcaseScreenIsDrawnFromTheCarQfs()
    {
        var menu = OpenShowcase(car: 0);
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Assert.True(scene.UsesOriginalFonts);
        Assert.Equal("mac/back", scene.BackgroundSource);
        // The port's key legend at the bottom of the screen is not part of the original.
        Assert.Null(Diff(Reference(menu, "gnrl", MenuScene.ShowcaseFullLevel), fb, 0, 0, 640, 460));
        // The background picture alone shows only where the photo column, logo and texts do not reach.
        var back = Img(CarArt(menu), "back");
        foreach (var (x, y) in new[] { (8, 300), (100, 460), (600, 470) })
            Assert.Equal(HudPixel(back, x, y), fb.Get(x, y));
    }

    [GameDataFact]
    public void ChangingTheCarRedrawsFromThatCarsQfs()
    {
        var menu = OpenShowcase(car: 0);
        var scene = new MenuScene(menu);
        var first = new FrameBuffer();
        scene.Render(first);
        string macBack = "mac/back";
        Assert.Equal(macBack, scene.BackgroundSource);
        menu.HandleKey(MenuKey.Right); // mac → ferr
        var second = new FrameBuffer();
        scene.Render(second);
        Assert.Equal("ferr/back", scene.BackgroundSource);
        Assert.Null(Diff(Reference(menu, "gnrl", MenuScene.ShowcaseFullLevel), second, 0, 0, 640, 460));
        Assert.False(Img(Fsh.Load("FEDATA/PC/ART/MAC.QFS"), "back").Rgba
            .SequenceEqual(Img(CarArt(menu), "back").Rgba), "other car = other background");
    }

    [GameDataTheory]
    [InlineData(1, "gnrl")]
    [InlineData(2, "perf")]
    [InlineData(3, "mech")]
    public void EveryPageDrawsItsOwnPhotoShape(int page, string shape)
    {
        var menu = OpenShowcase(car: 0);
        menu.HandleKey(MenuKey.Down);
        for (int i = 1; i < page; i++) menu.HandleKey(MenuKey.Right);
        Assert.Equal(page, menu.ShowcasePage);
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        Assert.Null(Diff(Reference(menu, shape, MenuScene.ShowcaseFullLevel), fb, 0, 0, 640, 460));
        // The photo column (361x143) differs between pages.
        var image = Img(CarArt(menu), shape);
        var other = Img(CarArt(menu), shape == "perf" ? "mech" : "perf");
        Assert.True(image.Width > 300 && image.Height > 100);
        Assert.False(image.Rgba.SequenceEqual(other.Rgba));
    }

    [GameDataFact]
    public void TheCarLogoFadesIn()
    {
        var menu = OpenShowcase(car: 0);
        var scene = new MenuScene(menu) { AnimateChrome = true };
        var logo = Img(CarArt(menu), "car7");
        int x = logo.PosX, y = logo.PosY + MenuScene.ShowcaseLogoDy;
        // The first frames are the "squash" phase (adds 0x1E per frame, see MenuShowcaseLogoSquashTests) [ref:2910]
        // a scaled drawing has no level yet: the logo slides in by level only after the phase (0x400 = 35 frames).
        for (int frame = 0; frame < 35; frame++) scene.Render(new FrameBuffer());
        var transparent = new FrameBuffer();
        scene.Render(transparent);
        // Frame 0 → level 0: the logo is not visible yet (level = frame * 256 / 0x23). [ref:2911]
        Assert.Null(Diff(Reference(menu, "gnrl", 0), transparent, 0, 0, 640, 460));
        scene.Advance(1.0); // 128 ticks / 10 = frame 12
        var mid = new FrameBuffer();
        scene.Render(mid);
        Assert.Null(Diff(Reference(menu, "gnrl", 12 * 0x100 / MenuScene.ShowcaseFrames), mid, 0, 0, 640, 460));
        Assert.NotNull(Diff(transparent, mid, x, y, x + logo.Width, y + logo.Height));
        scene.Advance(3.0); // frame 0x23 = full level
        var full = new FrameBuffer();
        scene.Render(full);
        Assert.Null(Diff(Reference(menu, "gnrl", MenuScene.ShowcaseFullLevel), full, 0, 0, 640, 460));
    }

    [GameDataFact]
    public void PageChangeKeepsTheLogoLevelButANewCarRestartsTheFadeIn()
    {
        var menu = OpenShowcase(car: 0);
        var scene = new MenuScene(menu) { AnimateChrome = true };
        // The "squash" phase draws the logo opaque, so first let it run to the end (0x400) –
        // we compare just the slide-in level.
        for (int frame = 0; frame < 35; frame++) scene.Render(new FrameBuffer());
        scene.Advance(1.0);
        var fb = new FrameBuffer();
        scene.Render(fb);
        int level = 12 * 0x100 / MenuScene.ShowcaseFrames;
        menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Right); // page change: animation unchanged (does not reset) [ref:2912] [ref:3459]
        scene.Render(fb);
        Assert.Null(Diff(Reference(menu, "perf", level), fb, 0, 0, 640, 460));
        menu.HandleKey(MenuKey.Up);
        menu.HandleKey(MenuKey.Right); // car change: animation restarts (0) – phase and level from zero [ref:3460]
        for (int frame = 0; frame < 35; frame++) scene.Render(new FrameBuffer());
        scene.Render(fb);
        Assert.Null(Diff(Reference(menu, "perf", 0), fb, 0, 0, 640, 460));
    }

    static uint HudPixel(FshImage image, int x, int y)
    {
        int o = (y * image.Width + x) * 4;
        return (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]);
    }

    // ---- history and slide show – entries 2 and 3 of the showcase screen ---- [ref:2913]

    const string History = MenuController.HistoryScreen, SlideShow = MenuController.SlideShowScreen;

    /// <summary>Showcase screen → entry 2 ("history") or 3 ("slide show") → Enter.</summary>
    static MenuController OpenShowcaseItem(int index, int? car = null)
    {
        var menu = OpenShowcase(car);
        for (int i = 0; i < index; i++) menu.HandleKey(MenuKey.Down);
        Assert.Equal(index, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Enter);
        return menu;
    }

    static MenuController OpenHistory(int? car = null, int language = 0)
    {
        var menu = OpenShowcaseItem(2, car);
        menu.Config.Language = (byte)language;
        Assert.Equal(History, menu.CurrentScreen.Id);
        Assert.True(menu.InHistory);
        Assert.Equal(0, menu.SelectedIndex); // entry 0 = "next car" (LEFT/RIGHT only work on it)
        return menu;
    }

    static MenuController OpenSlideShow(int? car = null)
    {
        var menu = OpenShowcaseItem(3, car);
        Assert.Equal(SlideShow, menu.CurrentScreen.Id);
        Assert.True(menu.InSlideShow);
        Assert.Equal(0, menu.SelectedIndex); // entry 0 = "next slide"
        return menu;
    }

    static Fsh HisArt(MenuController menu) => Fsh.Load($"FEDATA/PC/ART/{menu.HistoryArt.ToUpperInvariant()}.QFS");

    static Fsh SlideFsh(string name) => Fsh.Load($"FEDATA/PC/ART/SLIDES/{name.ToUpperInvariant()}.QFS");

    /// <summary>History background: "back" and the language overlay at (0,0), photo captions and the timeline. [ref:2914]
    /// Timeline wrapping, repeated independently of <see cref="MenuScene.WrapTimeline"/>.</summary>
    static FrameBuffer ReferenceHistory(MenuController menu)
    {
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        var art = HisArt(menu);
        foreach (string name in new[] { "back", menu.HistoryOverlay })
        {
            var image = Img(art, name);
            OriginalHud.Blit(fb, image, 0, 0);
        }
        var text = TextFile.Load("ENG");
        var font = new FrontEndFont();
        DrawItems(fb, menu, font, text);
        for (int i = 0; i < MenuController.HistoryCaptionCount; i++)
        {
            var (x, y) = MenuScene.HistoryCaptionPositions[i];
            font.Draw(fb, text[menu.HistoryCaptionId(i)], x, y, 18, 3, 2);
        }
        var ids = menu.HistoryTimelineIds;
        int lineY = text.Entries[ids[0]].Y;
        foreach (int id in ids)
        {
            var e = text.Entries[id];
            var (line, wrapped) = Wrap(e.Text, e.X, fb.Width - 5, font.Font(e.FontSize));
            if (line.Length > 0) { font.Draw(fb, line, e.X, lineY, e.FontSize, e.Align, e.Colour); lineY += 0x1A; }
            if (wrapped.Length > 0)
            { font.Draw(fb, wrapped, e.X + 0x31, lineY, e.FontSize, e.Align, e.Colour); lineY += 0x1A; }
        }
        return fb;
    }

    /// <summary>Copy of the wrap rule from: it moves the tail FROM the last space, and [ref:2915]
    /// it calls `sprintf(buf, "%s %s", tail, buf)` – the tail goes BEFORE the existing text. [ref:2916]</summary>
    static (string Line, string Wrapped) Wrap(string text, int x, int limit, Ffn font)
    {
        string wrapped = "";
        while (text.Length > 0 && font.Measure(text) + x > limit)
        {
            int space = text.LastIndexOf(' ');
            if (space < 0) break;
            string tail = text[space..];
            wrapped = wrapped.Length == 0 ? tail : tail + " " + wrapped;
            text = text[..space];
        }
        return (text, wrapped);
    }

    /// <summary>Slide show background: "back" from `FEDATA/PC/ART/SLIDES/&lt;name&gt;.QFS` at (0,0) + entries.</summary>
    static FrameBuffer ReferenceSlideShow(MenuController menu)
    {
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        var image = Img(SlideFsh(menu.SlideArt), "back");
        OriginalHud.Blit(fb, image, 0, 0);
        DrawItems(fb, menu, new FrontEndFont(), TextFile.Load("ENG"));
        return fb;
    }

    [GameDataFact]
    public void HistoryOpensOnThePlayersCarWithItsHistoryFile()
    {
        var menu = OpenHistory(car: 0);
        Assert.Equal(0, menu.HistoryCar);
        Assert.Equal("hismac", menu.HistoryArt);
        // "history" and "done" (TEXT 781 / 782) are screen entries, both from TEXT.ENG.
        var text = TextFile.Load("ENG");
        Assert.Equal("history", text[227]);
        Assert.Equal([781, 782], TextIds(menu, 0, 2));
        Assert.Equal([400, 430], menu.CurrentScreen.Items.Select(i => text.Entries[i.TextId].Y));
        // The same file serves several cars (table). [ref:2917]
        Assert.Equal("hisfrd", OpenShowcaseItem(2, 3).HistoryArt);
        Assert.Equal("hisfrd", OpenShowcaseItem(2, 5).HistoryArt);
        Assert.Equal("hisltus", OpenShowcaseItem(2, 8).HistoryArt);
        Assert.Equal("hisisd", OpenShowcaseItem(2, 11).HistoryArt);
    }

    [GameDataFact]
    public void HistoryOverlayFollowsTheLanguageAndSwedishFallsBackToSpanish()
    {
        //  Languages: engl, germ, fren, span, itly, span; each overlay is in HIS*.QFS. [ref:2918]
        Assert.Equal(["engl", "germ", "fren", "span", "itly", "span"],
            Enumerable.Range(0, 6).Select(l => OpenHistory(car: 6, language: l).HistoryOverlay));
        var mac = HisArt(OpenHistory(car: 0));
        foreach (string name in new[] { "back", "engl", "germ", "fren", "span", "itly" }) Assert.NotNull(mac.Find(name));
        // Language 5 (Swedish) draws "span", although the file has its own, different shape "swed".
        Assert.False(Img(mac, "span").Rgba.SequenceEqual(Img(mac, "swed").Rgba));
        var spanish = Render(OpenHistory(car: 0, language: 3));
        var swedish = Render(OpenHistory(car: 0, language: 5));
        Assert.Null(Diff(spanish, swedish, 0, 0, 640, 460));
        Assert.NotNull(Diff(Render(OpenHistory(car: 0, language: 0)), spanish, 0, 0, 640, 460));
    }

    static FrameBuffer Render(MenuController menu)
    {
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        return fb;
    }

    [GameDataTheory]
    [InlineData(0, "F1 LM", "1995 LeMans winner", "1996 F1 GTR", 810, 6)]
    [InlineData(6, "E-type", "SS 100", "XJ13", 833, 4)]
    [InlineData(11, "Spyder 033i", "Commendatore 112i", "Imperator 108i", 837, 4)]
    public void HistoryDrawsTheCaptionsAndTimelineOfTheCar(int car, string c1, string c2, string c3, int first, int rows)
    {
        var menu = OpenHistory(car);
        var text = TextFile.Load("ENG");
        Assert.Equal([c1, c2, c3], Enumerable.Range(0, 3).Select(i => text[menu.HistoryCaptionId(i)]));
        var ids = menu.HistoryTimelineIds;
        Assert.Equal(Enumerable.Range(first, rows), ids);
        // The timeline: first row from the TEXT record (x = 410, y = 90), each next one 0x1A further down. [ref:2919]
        Assert.Equal(410, text.Entries[first].X);
        Assert.Equal(90, text.Entries[first].Y);
        Assert.Equal([18, 2, 2, 3], new[]
        { (int)text.Entries[first].FontSize, (int)text.Entries[first].Colour,
          (int)text.Entries[first].HighlightColour, (int)text.Entries[first].Align });
    }

    [GameDataFact]
    public void HistoryLeftRightJumpBetweenCarsThatShareOneHistoryFile()
    {
        // RIGHT first jumps to the twin sharing the history file (7→8, 3→5, 1→2, 9→10) [ref:2920]
        // only then increases the number (-), so 0 → 1 → 3 → 6 → 7 → 9 → 11 → 0. [ref:2921]
        var menu = OpenHistory(car: 0);
        int[] right = [1, 3, 6, 7, 9, 11, 0];
        foreach (int car in right) { menu.HandleKey(MenuKey.Right); Assert.Equal(car, menu.HistoryCar); }
        // ENTER on the "next car" entry works like RIGHT (handles both 0xD and 0x4D00). [ref:2922]
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(1, menu.HistoryCar);
        // LEFT works analogously (8→7, 5→3, 2→1, 10→9) and decreases: 1 → 0 → 11 → 10 → 8 → 6 → 5 → 2 → 0.
        int[] left = [0, 11, 10, 8, 6, 5, 2, 0];
        foreach (int car in left) { menu.HandleKey(MenuKey.Left); Assert.Equal(car, menu.HistoryCar); }
        // On the "done" entry, LEFT/RIGHT do not select a car.
        int chosen = menu.HistoryCar;
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(1, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(chosen, menu.HistoryCar);
    }

    [GameDataFact]
    public void HistoryScreenIsDrawnFromItsQfsWithTheLanguageOverlay()
    {
        var menu = OpenHistory(car: 0, language: 2);
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Assert.Equal("hismac/back", scene.BackgroundSource);
        Assert.Null(Diff(ReferenceHistory(menu), fb, 0, 0, 640, 460));
        // The language overlay sits on the background (the "history" drawing from the top of the screen) and changes pixels relative to language 0.
        var art = HisArt(menu);
        var (x, y) = (200, 30);
        Assert.Equal(HudPixel(Img(art, "fren"), x, y), fb.Get(x, y));
        Assert.NotEqual(HudPixel(Img(art, "engl"), x, y), fb.Get(x, y));
        // Changing the car redraws from its file.
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Right); // 0 → 1 → 3 (hisfrd; see the jumps above) [ref:2923]
        Assert.Equal("hisfrd", menu.HistoryArt);
        scene.Render(fb);
        Assert.Equal("hisfrd/back", scene.BackgroundSource);
        Assert.Null(Diff(ReferenceHistory(menu), fb, 0, 0, 640, 460));
    }

    [GameDataFact]
    public void HistoryDoneKeepsTheCarAndEscUndoesTheWholeShowcaseVisit()
    {
        var menu = OpenHistory(car: 0);
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Right); // 0 → 1 → 3 (history "hisfrd")
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(1, menu.SelectedIndex); // "done"
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Showcase, menu.CurrentScreen.Id);
        // The showcase screen starts over: car from the config (3), page 1.
        Assert.Equal(3, menu.ShowcaseCar);
        Assert.Equal(1, menu.ShowcasePage);
        Assert.Equal("gt90", menu.ShowcaseArt); // [3]; the history of car 3 is "hisfrd" [ref:2924]
        Assert.Equal(3, menu.Config.P1Car);
        menu.HandleKey(MenuKey.Escape); // ESC undoes the whole visit to the showcase
        Assert.Equal(0, menu.Config.P1Car); // ...the car from before entering the showcase
    }

    [GameDataFact]
    public void SlideShowWalksTheFrameFilesOfTheCar()
    {
        var menu = OpenSlideShow(car: 0);
        Assert.Equal(0, menu.SlideCar);
        Assert.Equal(0, menu.SlideFrame);
        Assert.Equal("sldmac", menu.SlideArt); // frame 0 = the file without a digit [ref:2925]
        Assert.Equal([231, 232, 233], TextIds(menu, 0, 3));
        for (int frame = 1; frame < 8; frame++)
        {
            menu.HandleKey(MenuKey.Right);
            Assert.Equal(frame, menu.SlideFrame);
            Assert.Equal($"sldmac{frame + 1}", menu.SlideArt);
            Assert.NotNull(SlideFsh(menu.SlideArt).Find("back")); // each frame is a separate QFS file
        }
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(0, menu.SlideFrame);
        Assert.Equal("sldmac", menu.SlideArt);
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(7, menu.SlideFrame);
        Assert.Equal("sldmac8", menu.SlideArt);
        // ENTER on the "next slide" entry works like RIGHT.
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(6, menu.SlideFrame);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(7, menu.SlideFrame);
        Assert.NotNull(SlideFsh("sldmac").Find("back")); // frame 0 = the file without a digit
    }

    [GameDataTheory]
    [InlineData(3, 7)]
    [InlineData(5, 7)]
    [InlineData(4, 8)]
    [InlineData(10, 8)]
    public void SlidesOfTheFordStopAtSevenFramesAndTheOthersRunToEight(int car, int frames)
    {
        var menu = OpenSlideShow(car);
        Assert.Equal(frames, MenuController.SlideShowFrames(car));
        for (int i = 0; i < frames - 1; i++) menu.HandleKey(MenuKey.Right);
        Assert.Equal(frames - 1, menu.SlideFrame);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(0, menu.SlideFrame); // last frame → 0 (7 → 0 for cars 3 and 5)
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(frames - 1, menu.SlideFrame);
    }

    [GameDataFact]
    public void SlideShowNextCarSwitchesTheCarAndClampsTheFrame()
    {
        var menu = OpenSlideShow(car: 0);
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(1, menu.SelectedIndex); // "next car"
        for (int car = 1; car < 12; car++) { menu.HandleKey(MenuKey.Right); Assert.Equal(car, menu.SlideCar); }
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(0, menu.SlideCar); // wraps 11 → 0
        menu.HandleKey(MenuKey.Left);
        Assert.Equal(11, menu.SlideCar);
        Assert.Equal("sldisd", menu.SlideArt);
        // Frame 7 on the 8-frame car, then a move to car 3 (7 frames) – the frame goes back to 0.
        menu.HandleKey(MenuKey.Up);
        for (int i = 0; i < 7; i++) menu.HandleKey(MenuKey.Right);
        Assert.Equal(7, menu.SlideFrame);
        menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Right); // 11 → 0
        Assert.Equal(7, menu.SlideFrame); // McLaren has 8 frames
        menu.HandleKey(MenuKey.Enter);  // ENTER works like RIGHT
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Right);  // 2 → 3 (Ford, 7 frames)
        Assert.Equal(3, menu.SlideCar);
        Assert.Equal(0, menu.SlideFrame);
        Assert.Equal("sldfrd", menu.SlideArt);
    }

    [GameDataFact]
    public void SlideShowScreenIsDrawnFromTheSlidesDirectory()
    {
        var menu = OpenSlideShow(car: 0);
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Assert.Equal("SLIDES/sldmac/back", scene.BackgroundSource);
        Assert.Null(Diff(ReferenceSlideShow(menu), fb, 0, 0, 640, 460));
        // Frames are separate files: each frame draws a different image.
        var first = new FrameBuffer();
        menu.HandleKey(MenuKey.Right);
        scene.Render(first);
        Assert.Equal("SLIDES/sldmac2/back", scene.BackgroundSource);
        Assert.Null(Diff(ReferenceSlideShow(menu), first, 0, 0, 640, 460));
        Assert.False(Img(SlideFsh("sldmac2"), "back").Rgba.SequenceEqual(Img(SlideFsh("sldmac"), "back").Rgba));
        // There is no timer: the passage of frame time alone does not change it (only a key does). [ref:2926]
        scene.Advance(5.0);
        var later = new FrameBuffer();
        scene.Render(later);
        Assert.Null(Diff(first, later, 0, 0, 640, 460));
    }

    [GameDataFact]
    public void SlideShowDoneKeepsTheChosenCar()
    {
        var menu = OpenSlideShow(car: 0);
        menu.HandleKey(MenuKey.Down); // "next car"
        Assert.Equal(1, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.SlideCar);
        menu.HandleKey(MenuKey.Down); // "done"
        Assert.Equal(2, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Showcase, menu.CurrentScreen.Id);
        Assert.Equal(1, menu.ShowcaseCar);
        Assert.Equal(1, menu.Config.P1Car);
    }

    [GameDataFact]
    public void TimelineWrapsToTheSecondColumnWithTheOriginalRule()
    {
        var font = new FrontEndFont().Font(18);
        const string text = "1989: Company founded by a very long name";
        Assert.True(font.Measure(text) + 410 > 635, "the test text must be wider than the column");
        // The tail moves to the second column (x + 0x31) when x + 0x31 = 459, with a 26-pixel row.
        var (line, wrapped) = MenuScene.WrapTimeline(text, 410, 635, font);
        Assert.NotEqual("", wrapped);
        Assert.StartsWith(" ", wrapped);      // moves the tail FROM the space, inclusive [ref:2927]
        Assert.False(line.EndsWith(" "), "the header does not end with a space");
        Assert.True(font.Measure(line) + 410 <= 635, "the header must fit");
        Assert.True(font.Measure(line + " " + wrapped.TrimStart()) + 410 > 635, "with the last word it no longer fits");
        Assert.Equal(text.Split(' '), (line + wrapped).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Each move appends the tail with the "%s %s" pattern – hence the double space. [ref:2928]
        int limit = font.Measure("aa bb") + 1;
        Assert.Equal(("aa bb", " cc  dd"), MenuScene.WrapTimeline("aa bb cc dd", 0, limit, font));
        // A short text stays on one line, and text without a space cannot be split (in the original the loop
        // would never end – the port stops the split). [ref:2929]
        Assert.Equal(("F1 LM", ""), MenuScene.WrapTimeline("F1 LM", 410, 635, font));
        Assert.Equal((text, ""), MenuScene.WrapTimeline(text, 0, 1000, font)); // fits entirely
        Assert.Equal(("cc", ""), MenuScene.WrapTimeline("cc", 0, 10, font));   // no space → no splitting
    }

    /// <summary>Starts of "lines" with ink: a run of ink rows separated by more than one
    /// blank row counts as one line of text (there is a ~6 px gap between lines).</summary>
    static List<int> InkedBands(FrameBuffer fb, FrameBuffer background, int x0, int x1, int y1)
    {
        var starts = new List<int>();
        int previous = -10;
        for (int y = 0; y < Math.Min(y1, fb.Height); y++)
        {
            bool ink = false;
            for (int x = x0; x < x1 && !ink; x++) ink = fb.Get(x, y) != background.Get(x, y);
            if (!ink) continue;
            if (y - previous > 2) starts.Add(y);
            previous = y;
        }
        return starts;
    }

    [GameDataFact]
    public void HistoryTimelineIsSpacedByTheDrawStepNotByTheTextRecords()
    {
        // The TEXT records have y = 90, 120, 150… (step 30), but it draws the next rows every 0x1A (26). [ref:2930]
        var menu = OpenHistory(car: 0);
        var text = TextFile.Load("ENG");
        var ids = menu.HistoryTimelineIds;
        var fb = Render(menu);
        var art = HisArt(menu);
        var background = new FrameBuffer();
        background.Clear(0, 0, 0);
        foreach (string name in new[] { "back", menu.HistoryOverlay }) OriginalHud.Blit(background, Img(art, name), 0, 0);

        // How many rows fit on the screen is counted by an independent copy of the wrap rule (see Wrap).
        var font = new FrontEndFont();
        int lines = 0;
        foreach (int id in ids)
        {
            var e = text.Entries[id];
            var (line, wrapped) = Wrap(e.Text, e.X, fb.Width - 5, font.Font(e.FontSize));
            if (line.Length > 0) lines++;
            if (wrapped.Length > 0) lines++;
        }
        Assert.True(lines >= ids.Count, "each timeline row draws at least one line");
        int firstY = text.Entries[ids[0]].Y;             // 90 – of the first record [ref:2931]
        // The timeline column (x = 410, tail from 459); y < 450, because the port draws the key legend below the screen.
        var bands = InkedBands(fb, background, 410, 640, 450);
        Assert.Equal(lines, bands.Count);            // each line of text is one band of ink
        Assert.True(lines > 1);
        // The ink does not reach the top edge of the row (the font has its own spacing), so we measure from the first band.
        int inkOffset = bands[0] - firstY;
        for (int i = 0; i < lines; i++)
            Assert.InRange(bands[i], firstY + inkOffset + 0x1A * i - 2, firstY + inkOffset + 0x1A * i + 2);
        // The average step is 0x1A (26), not 0x1E (30) from the TEXT records – at 30 the deviation would grow with every row.
        Assert.InRange((bands[^1] - bands[0]) / (lines - 1), 0x1A - 1, 0x1A + 1);
    }

    [GameDataFact]
    public void ShowcaseWithoutGameDataFallsBackWithoutThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nfs2se-nodata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, GameFiles.DataDirName));   // a game dir without front-end files
        Directory.CreateDirectory(Path.Combine(dir, GameFiles.FeDirName));
        try
        {
            var menu = OpenShowcase(car: 0);
            var scene = new MenuScene(menu, dir);
            var fb = new FrameBuffer();
            scene.Render(fb);
            Assert.Null(scene.BackgroundSource);
            Assert.False(scene.UsesOriginalFonts);
            Assert.All(fb.Rgba.Where((_, n) => n % 4 == 3), a => Assert.Equal(255, a));
        }
        finally { Directory.Delete(dir, true); }
    }
}
