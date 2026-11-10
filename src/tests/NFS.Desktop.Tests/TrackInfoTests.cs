using System.Security.Cryptography;
using NFS.Core.Tables;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Menu screens built from the original game: track description [ref:2964] [ref:3464]
/// track records, personal statistics and credits [ref:2965]
/// We check behaviour – navigation, content from the game data (tables of the original game, TEXT, files from [ref:2966]
/// FEDATA/PC/STATS and FEDATA/PC/STATS/PRH) and scrolling the credits list – not implementation details.</summary>
public class TrackInfoTests
{
    const string MainMenu = "main", Location = "location", Options = "options";

    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_T9_REVIEW_DIR") is not { Length: > 0 } dir)
            return;   // review PNGs only on request
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    /// <summary>Diagnostics (when NFS_T9_REVIEW_DIR is set): reference, screen and diff map.</summary>
    static void ReviewDiff(FrameBuffer reference, FrameBuffer screen, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_T9_REVIEW_DIR") is not { Length: > 0 } dir)
            return;
        Directory.CreateDirectory(dir);
        reference.SavePng(Path.Combine(dir, name + "-ref.png"));
        screen.SavePng(Path.Combine(dir, name + "-got.png"));
        var diff = new FrameBuffer();
        for (int y = 0; y < LegendTop; y++)
            for (int x = 0; x < 640; x++)
                diff.Set(x, y, reference.Get(x, y) == screen.Get(x, y) ? 0u : 0xFF0000u);
        diff.SavePng(Path.Combine(dir, name + "-diff.png"));
    }

    /// <summary>Below the screen the port draws the key legend (the original has none) – we compare only y &lt; 450.</summary>
    const int LegendTop = 450;

    /// <summary>All non-zero pixels of the reference (without the key legend) must appear on the screen –
    /// a drawing replaces a background pixel.</summary>
    static void AssertPainted(FrameBuffer reference, FrameBuffer screen)
    {
        int painted = 0;
        for (int y = 0; y < LegendTop; y++)
            for (int x = 0; x < 640; x++)
            {
                uint want = reference.Get(x, y);
                if (want == 0) continue;
                painted++;
                Assert.True(want == screen.Get(x, y),
                    $"pixel ({x},{y}): reference 0x{want:X6}, screen 0x{screen.Get(x, y):X6}");
            }
        Assert.True(painted > 0);
    }

    static Fsh TrackArt(string name) => Fsh.Load($"FEDATA/PC/ART/TRACK/{name.ToUpperInvariant()}.QFS");

    static FshImage Shape(Fsh art, string name) => art.Decode(art.Find(name)!, art.GlobalPalette())!;

    /// <summary>Menu → "location" with item <paramref name="index"/> selected. [ref:2967]</summary>
    static MenuController LocationMenu(int index)
    {
        var menu = MenuController.Load();
        Assert.Equal(MainMenu, menu.CurrentScreen.Id);
        menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Location, menu.CurrentScreen.Id);
        for (int i = 0; i < index; i++) menu.HandleKey(MenuKey.Down);
        Assert.Equal(index, menu.SelectedIndex);
        return menu;
    }

    /// <summary>DOWN until the selected item leads to screen <paramref name="target"/> (disabled items
    /// of the main menu – e.g. "player two" – are skipped, so steps cannot be counted in advance).</summary>
    static void SelectTarget(MenuController menu, string target)
    {
        for (int i = 0; i < 20 && menu.CurrentScreen.Items[menu.SelectedIndex].Target != target; i++)
            menu.HandleKey(MenuKey.Down);
        Assert.Equal(target, menu.CurrentScreen.Items[menu.SelectedIndex].Target);
    }

    /// <summary>DOWN until the selected item has text <paramref name="text"/>.</summary>
    static void SelectItem(MenuController menu, string text)
    {
        for (int i = 0; i < 20 && menu.CurrentScreen.Items[menu.SelectedIndex].Text != text; i++)
            menu.HandleKey(MenuKey.Down);
        Assert.Equal(text, menu.CurrentScreen.Items[menu.SelectedIndex].Text);
    }

    /// <summary>Menu → "location" → "track info" (track 0 = OVAL). [ref:2968]</summary>
    static MenuController OpenTrackInfo()
    {
        var menu = LocationMenu(2);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.TrackInfoScreen, menu.CurrentScreen.Id);
        Assert.True(menu.InTrackInfo);
        return menu;
    }

    /// <summary>Menu → "location" → "track records". [ref:2969]</summary>
    static MenuController OpenTrackRecords()
    {
        var menu = LocationMenu(3);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.TrackRecordsScreen, menu.CurrentScreen.Id);
        Assert.True(menu.InTrackRecords);
        return menu;
    }

    /// <summary>Menu → "location" → "track records" → "personal statistics". [ref:2970]</summary>
    static MenuController OpenPersonalStats()
    {
        var menu = OpenTrackRecords();
        menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down);
        Assert.Equal(3, menu.SelectedIndex);                       // item 3 = "personal statistics"
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.PersonalStatsScreen, menu.CurrentScreen.Id);
        Assert.True(menu.InPersonalStats);
        return menu;
    }

    /// <summary>Menu → "options"  → "credits". [ref:2971]</summary>
    static MenuController OpenCredits()
    {
        var menu = MenuController.Load();
        SelectTarget(menu, Options);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Options, menu.CurrentScreen.Id);
        SelectTarget(menu, MenuController.CreditsScreen);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.CreditsScreen, menu.CurrentScreen.Id);
        Assert.True(menu.InCredits);
        Assert.Equal(MenuController.CreditsStartY, menu.CreditsY);
        Assert.Equal(-1, menu.CreditsFinale);
        return menu;
    }

    [GameDataFact]
    public void TrackInfoCyclesTheTrackAndKeepsItOnDoneButNotOnEscape()
    {
        var menu = OpenTrackInfo();
        Assert.Equal(0, menu.Config.Track);
        menu.HandleKey(MenuKey.Right); Assert.Equal(1, menu.Config.Track);
        menu.HandleKey(MenuKey.Left); Assert.Equal(0, menu.Config.Track);
        menu.HandleKey(MenuKey.Left);                              // 0 → last track
        Assert.Equal(menu.TrackCount - 1, menu.Config.Track);
        menu.HandleKey(MenuKey.Enter);                             // RIGHT and ENTER do the same
        Assert.Equal(0, menu.Config.Track);
        // The last item ("done") does not scroll the track, but ENTER leaves with the selected track.
        while (menu.SelectedIndex < menu.CurrentScreen.Items.Count - 1) menu.HandleKey(MenuKey.Down);
        int done = menu.SelectedIndex;
        menu.HandleKey(MenuKey.Right); menu.HandleKey(MenuKey.Left);   // on "done" the arrows do nothing
        Assert.Equal(MenuController.TrackInfoScreen, menu.CurrentScreen.Id);
        Assert.Equal(0, menu.Config.Track);
        menu.HandleKey(MenuKey.Up); menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.Config.Track);
        while (menu.SelectedIndex < done) menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Location, menu.CurrentScreen.Id);
        Assert.Equal(1, menu.Config.Track);

        var escaped = OpenTrackInfo();
        escaped.HandleKey(MenuKey.Right);
        Assert.Equal(1, escaped.Config.Track);
        escaped.HandleKey(MenuKey.Escape);
        Assert.Equal(Location, escaped.CurrentScreen.Id);
        Assert.Equal(0, escaped.Config.Track);                     // ESC restores the config from entering the screen
    }

    [GameDataFact]
    public void TrackRecordsLapsCycleAndLandInTheConfigOnDone()
    {
        var menu = OpenTrackRecords();
        Assert.Equal(4, menu.RecordsLaps);                        // config has no 2/8 → 4
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(1, menu.SelectedIndex);                      // item 1 = "laps"
        menu.HandleKey(MenuKey.Right); Assert.Equal(8, menu.RecordsLaps);
        menu.HandleKey(MenuKey.Right); Assert.Equal(2, menu.RecordsLaps);   // 8 * 2 > 8 → 2
        menu.HandleKey(MenuKey.Right); Assert.Equal(4, menu.RecordsLaps);
        menu.HandleKey(MenuKey.Left); Assert.Equal(2, menu.RecordsLaps);
        menu.HandleKey(MenuKey.Left); Assert.Equal(8, menu.RecordsLaps);    // 2 / 2 < 2 → 8
        menu.HandleKey(MenuKey.Left); Assert.Equal(4, menu.RecordsLaps);

        var scene = new MenuScene(menu);
        scene.Render(new FrameBuffer());
        var text = TextFile.Load("ENG");
        string LapsHeader() => $"{menu.RecordsLaps} {text[MenuController.RecordsLapsText]}";
        Assert.Equal(LapsHeader(), scene.TrackRecordLines()[0].Text);
        Assert.Equal("4 laps", LapsHeader());
        // Item 0 ("next track") scrolls the track and does not change the number of laps.
        menu.HandleKey(MenuKey.Up);
        Assert.Equal(0, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.Config.Track);
        Assert.Equal(4, menu.RecordsLaps);
        // Item 1: 8 laps reads the rows from the entry RowOffset(8) = 0x15.
        menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Right);
        Assert.Equal(8, menu.RecordsLaps);
        Assert.Equal("8 laps", LapsHeader());
        Assert.Equal(LapsHeader(), scene.TrackRecordLines()[0].Text);
        Assert.Equal(0x15, TrackRecords.RowOffset(menu.RecordsLaps));

        for (int i = 0; i < 3; i++) menu.HandleKey(MenuKey.Down);
        Assert.Equal(4, menu.SelectedIndex);                                 // "done"
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Location, menu.CurrentScreen.Id);
        Assert.Equal(8, menu.Config.Laps);                                   // saves the laps [ref:2972]
        Assert.Equal(1, menu.Config.LapsMenuIndex);                          // 4 → 0, 8 → 1, 2 → 2

        var escaped = OpenTrackRecords();
        escaped.HandleKey(MenuKey.Down); escaped.HandleKey(MenuKey.Right);
        Assert.Equal(8, escaped.RecordsLaps);
        escaped.HandleKey(MenuKey.Escape);
        Assert.Equal(Location, escaped.CurrentScreen.Id);               // ESC reverts the number of laps
        Assert.NotEqual(8, escaped.Config.Laps);
    }

    static string Snapshot(string dir) => string.Join('\n', Directory.GetFiles(dir).OrderBy(f => f)
        .Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} "
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    [GameDataFact]
    public void PersonalStatisticsItemFollowsTheConfigFlag()
    {
        var menu = OpenTrackRecords();
        Assert.NotEqual(0, menu.Config.PersonalStats);            // default +0x0F = 1
        menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down);
        Assert.Equal(3, menu.SelectedIndex);                      // item 3 = "personal statistics"
        menu.Config[0x0F] = 0;
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(4, menu.SelectedIndex);                      // disabled item is skipped
        menu.HandleKey(MenuKey.Up);
        Assert.Equal(2, menu.SelectedIndex);                      // and upward too
        menu.Config[0x0F] = 1;
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(3, menu.SelectedIndex);
    }

    [GameDataFact]
    public void PersonalStatisticsScreenCyclesTrackLapsCarAndGraphType()
    {
        var menu = OpenPersonalStats();
        Assert.Equal(0, menu.SelectedIndex);
        Assert.Equal(menu.Config.Track, menu.StatTrack);          // screen state starts from the config
        Assert.Equal(4, menu.StatLaps);                           // config has no 2/8 → 4
        Assert.Equal(menu.Config.P1Car, menu.StatCar);
        Assert.Equal(0, menu.StatView);

        // Item 0 "track": wraps around the number of tracks and does not change the config (is local). [ref:3465]
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.StatTrack);
        Assert.Equal(0, menu.Config.Track);
        Assert.Equal(TrackRecords.RecordsArt(1), menu.PersonalStatsArt);
        Assert.NotEqual("ovalrec", menu.PersonalStatsArt);         // new track = new artwork [ref:2973]
        menu.HandleKey(MenuKey.Left); menu.HandleKey(MenuKey.Left);
        Assert.Equal(menu.TrackCount - 1, menu.StatTrack);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(0, menu.StatTrack);

        // Item 1 "laps": LEFT / 2 (below 2 → 8), RIGHT/ENTER * 2 (above 8 → 2).
        menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Right); Assert.Equal(8, menu.StatLaps);
        menu.HandleKey(MenuKey.Enter); Assert.Equal(2, menu.StatLaps);
        menu.HandleKey(MenuKey.Left); Assert.Equal(8, menu.StatLaps);     // 2 / 2 = 1 < 2 → 8
        menu.HandleKey(MenuKey.Left); Assert.Equal(4, menu.StatLaps);

        // Item 2 "car": LEFT − 1 (below 0 → 11), RIGHT/ENTER + 1 (12 → 0).
        menu.HandleKey(MenuKey.Down);
        int car = menu.StatCar;
        menu.HandleKey(MenuKey.Left);
        Assert.Equal((car + PersonalStats.CarCount - 1) % PersonalStats.CarCount, menu.StatCar);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(car, menu.StatCar);

        // Item 3 "graph type": 0..3 with wrap-around.
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(3, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Left); Assert.Equal(3, menu.StatView);
        menu.HandleKey(MenuKey.Right); Assert.Equal(0, menu.StatView);
        menu.HandleKey(MenuKey.Enter); Assert.Equal(1, menu.StatView);
        menu.HandleKey(MenuKey.Left); Assert.Equal(0, menu.StatView);

        // "done" (item 4) returns to the track records screen, and the screen did not save anything to the config.
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(4, menu.SelectedIndex);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.TrackRecordsScreen, menu.CurrentScreen.Id);
        Assert.Equal(0, menu.Config.Track);
        Assert.NotEqual(8, menu.Config.Laps);
    }

    [GameDataFact]
    public void PersonalStatisticsScreenPaintsTheHeaderAndTheGraphGrid()
    {
        var menu = OpenPersonalStats();
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Review(fb, "personal-stats.png");
        Assert.True(scene.UsesOriginalFonts);

        // Header (TEXT 0x2B4 + 0x1E, font 0x18, right-aligned, colour 2) and the car name at the same x,
        // 0x19 lower than y(0xC9): every glyph pixel drawn on a clean frame must appear on the screen.
        var text = TextFile.Load("ENG");
        var font = new FrontEndFont();
        int headerX = text.Entries[MenuController.PersonalStatsLapsText].X + MenuController.PersonalStatsHeaderDx;
        var header = new FrameBuffer();
        font.Draw(header, menu.PersonalStatsHeader, headerX,
            text.Entries[MenuController.PersonalStatsLapsText].Y, 24, 1, 2);
        font.Draw(header, menu.PersonalStatsCarName, headerX,
            text.Entries[MenuController.PersonalStatsHistoryText].Y + MenuController.PersonalStatsCarDy, 24, 1, 2);
        int ink = 0;
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (header.Get(x, y) != 0)
                {
                    ink++;
                    Assert.Equal(header.Get(x, y), fb.Get(x, y));
                }
        Assert.True(ink > 0);

        // Graph: ten horizontal lines, one every 1/10 of the height, and nine vertical lines, one every 1/10 [ref:2974]
        // of the width, and axis bands around the area 0xF3..0x273 / 0x8F..0x1B0; with no entries there is nothing more.
        int rowY = MenuScene.PlotTop + (MenuScene.PlotBottom - MenuScene.PlotTop) / MenuScene.GridLinesY;
        Assert.Equal(MenuScene.GridColour & 0xFFFFFFu, fb.Get(MenuScene.PlotLeft + 20, rowY));
        int colX = MenuScene.PlotLeft + (MenuScene.PlotRight - MenuScene.PlotLeft) / (MenuScene.GridLinesX + 1);
        Assert.Equal(MenuScene.GridColour & 0xFFFFFFu, fb.Get(colX, MenuScene.PlotTop + 20));
        Assert.Equal(MenuScene.AxisColour & 0xFFFFFFu, fb.Get(MenuScene.PlotLeft + 20, MenuScene.PlotBottom + 2));
        Assert.Equal(MenuScene.AxisColour & 0xFFFFFFu, fb.Get(0xF1 + 2, MenuScene.PlotTop + 20));
    }

    [GameDataFact]
    public void CreditsScrollsTheListAndThenRunsTheFinaleCard()
    {
        var menu = OpenCredits();
        var scene = new MenuScene(menu);
        var text = TextFile.Load("ENG");
        // The list scrolls until row 0x5D6 reaches the band (y ≤ 0x6E). [ref:2975]
        int line0 = MenuController.CreditsLineY(text, 0, MenuController.CreditsFinaleText);
        Assert.True(line0 > 10000);                               // hundreds of list rows, not a few
        int needed = (MenuController.CreditsStartY + line0 - MenuController.CreditsEndLineY)
            / MenuController.CreditsStep + 1;
        for (int frame = 0; frame < needed - 1; frame++) scene.Advance(MenuScene.SlideStepSeconds);
        Assert.Equal(-1, menu.CreditsFinale);                     // finale card not started yet
        Assert.Equal(MenuController.CreditsStartY - MenuController.CreditsStep * (needed - 1), menu.CreditsY);
        scene.Advance(MenuScene.SlideStepSeconds);
        Assert.Equal(0, menu.CreditsFinale);                      // finale card start: offset 0
        Assert.Equal(MenuController.CreditsEndLineY, menu.CreditsFinaleLineY);

        // The offset grows x = x + x + 2 (0, 2, 6, 14, ...) until it reaches 600 and the screen returns to options.
        var seen = new List<int> { menu.CreditsFinale };
        for (int frame = 0; frame < 100 && menu.InCredits; frame++)
        {
            scene.Advance(MenuScene.SlideStepSeconds);
            if (menu.InCredits) seen.Add(menu.CreditsFinale);
        }
        Assert.Equal(new[] { 0, 2, 6, 14, 30, 62, 126, 254, 510 }, seen);
        Assert.False(menu.InCredits);
        Assert.Equal(Options, menu.CurrentScreen.Id);
    }

    [GameDataFact]
    public void CreditsKeysChangeTheScrollSpeedAndLeaveTheScreen()
    {
        var menu = OpenCredits();
        var scene = new MenuScene(menu);
        scene.Advance(MenuScene.SlideStepSeconds);
        Assert.Equal(MenuController.CreditsStartY - MenuController.CreditsStep, menu.CreditsY);
        // "P" (0x50) scrolls faster by 0xA.
        int y = menu.CreditsY;
        scene.Advance(MenuScene.SlideStepSeconds, creditsSlow: false, creditsFast: true);
        Assert.Equal(y - MenuController.CreditsFastStep - MenuController.CreditsStep, menu.CreditsY);
        // "H" (0x48) moves back by 7, but does not push the list above the start y (clamp 0x1C2).
        y = menu.CreditsY;
        scene.Advance(MenuScene.SlideStepSeconds, creditsSlow: true, creditsFast: false);
        Assert.Equal(y + MenuController.CreditsSlowStep - MenuController.CreditsStep, menu.CreditsY);
        var fresh = OpenCredits();
        var freshScene = new MenuScene(fresh);
        freshScene.Advance(MenuScene.SlideStepSeconds, creditsSlow: true, creditsFast: false);
        Assert.Equal(MenuController.CreditsStartY - MenuController.CreditsStep, fresh.CreditsY);

        // A key ends the scrolling without the finale card (the loop exits when the key is not Enter/ESC). [ref:2976]
        menu.HandleKey(MenuKey.Up);
        Assert.False(menu.InCredits);
        Assert.Equal(Options, menu.CurrentScreen.Id);
        Assert.Equal(-1, menu.CreditsFinale);
    }

    [GameDataFact]
    public void CreditsLinesShiftGoldRowsAndOnlyDrawInsideTheBand()
    {
        var text = TextFile.Load("ENG");
        // First list rows: 0x35A = colour 5 (section heading), 0x35B = colour 4 (gold, with ":" 0xA to the right),
        // 0x35C = colour 2 – its caption goes back by 0x1A, so it lands on the same y as the gold row.
        int first = MenuController.CreditsFirstText;
        int y0 = MenuController.CreditsClipTop + 1;
        var lines = MenuController.CreditsLines(text, y0, 640);
        Assert.Equal(5, text.Entries[first].Colour);
        Assert.Equal(4, text.Entries[first + 1].Colour);
        Assert.Equal(2, text.Entries[first + 2].Colour);
        Assert.Contains(lines, l => l.Text == text[first] && l.Y == y0 && l.X == 320 - 0x14);
        var heading = lines.Single(l => l.Text == text[first + 1] && l.Colour == 4);
        Assert.Equal(MenuController.CreditsRowStep + 0xD, heading.Y - y0);   // the gold row goes down by 0xD
        // The ":" glyph takes x from the scrolled frame y + 0xA, not from the row column. [ref:2977]
        var gold = lines.Single(l => l.Text == ":" && l.Colour == 4 && l.Y == heading.Y);
        Assert.Equal(y0 + 10, gold.X);
        var name = lines.Single(l => l.Text == text[first + 2] && l.Colour == 2);
        Assert.Equal(heading.Y, name.Y);                          // the caption lands on the line of the gold heading
        Assert.Equal(320, name.X);                                // colour 2 does not shift x (only ≥ 4 by 0x14)

        // Visibility band (0x4A, 0x1DC) – both edges sharp.
        Assert.Empty(MenuController.CreditsLines(text, -20000, 640));
        Assert.Empty(MenuController.CreditsLines(text, 10000, 640));
        int lead = MenuController.CreditsLineY(text, 0, first + 1) + 0xD;   // gold row drawn 0xD lower
        Assert.DoesNotContain(MenuController.CreditsLines(text, MenuController.CreditsClipTop - lead, 640),
            l => l.Text == text[first + 1]);
        Assert.Contains(MenuController.CreditsLines(text, MenuController.CreditsClipTop + 1 - lead, 640),
            l => l.Text == text[first + 1] && l.Y == MenuController.CreditsClipTop + 1);

        // Graphics of row 0x5CC are always drawn, also outside the band: "knwd" (100, y + 0x32) i "chy2" (300, y + 0x1E).
        var menu = OpenCredits();
        menu.CreditsFrame(false, false);
        int rowY = MenuController.CreditsLineY(text, menu.CreditsY, 0x5CC);
        var shapes = menu.CreditsShapes;
        Assert.Equal("credits", MenuController.CreditsArt);
        Assert.Equal(100, shapes[0].X);
        Assert.Equal(300, shapes[1].X);
        Assert.Equal(rowY + 0x32, shapes[0].Y);
        Assert.Equal(rowY + 0x1E, shapes[1].Y);
    }

    [GameDataFact]
    public void CreditsScreenPaintsTheBackgroundAndTheScrollingRows()
    {
        var menu = OpenCredits();
        var scene = new MenuScene(menu);
        for (int i = 0; i < 400; i++) scene.Advance(MenuScene.SlideStepSeconds);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Review(fb, "credits.png");

        var fsh = Fsh.Load($"FEDATA/PC/ART/{MenuController.CreditsArt.ToUpperInvariant()}.QFS");
        var reference = new FrameBuffer();
        reference.Clear(0, 0, 0);
        OriginalHud.Blit(reference, Shape(fsh, "Bac1"), 0, 0);
        OriginalHud.Blit(reference, Shape(fsh, menu.CreditsOverlay), 0, 0);
        var text = TextFile.Load("ENG");
        var lines = MenuController.CreditsLines(text, menu.CreditsY, fb.Width);
        Assert.NotEmpty(lines);
        var font = new FrontEndFont();
        foreach (var (shape, x, y) in menu.CreditsShapes)
            OriginalHud.Blit(reference, Shape(fsh, shape), x, y);
        foreach (var line in lines)
            font.Draw(reference, line.Text, line.X, line.Y, 18, line.Align, line.Colour);
        ReviewDiff(reference, fb, "credits");
        AssertPainted(reference, fb);
    }

    /// <summary>Screen items: entry colour from TEXT, highlight of the selected item [ref:2978]
    /// colour for a disabled item, "..." for flag 2 – as much as these screens draw. [ref:2979]</summary>
    static void DrawItems(FrameBuffer fb, MenuController menu, FrontEndFont font, TextFile text)
    {
        var items = menu.CurrentScreen.Items;
        int disabledColour = NFS.Core.Constants.FrontEndConstants.DisabledColour;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind is not (0 or 2) || item.TextId >= text.Entries.Count) continue;
            var e = text.Entries[item.TextId];
            int colour = (item.Flags & 1) != 0 ? disabledColour
                : i == menu.SelectedIndex ? e.HighlightColour : e.Colour;
            font.Draw(fb, e.Text, e.X, e.Y, e.FontSize, e.Align, colour);
            if ((item.Flags & 2) != 0)
                font.Draw(fb, "...", e.X + (e.FontSize < 18 ? 0xD : 0x13), e.Y, e.FontSize, e.Align, colour);
        }
    }

    [GameDataFact]
    public void TrackInfoReadsDifficultyAndLengthFromTheGameTablesInTheChosenUnit()
    {
        var menu = OpenTrackInfo();
        var text = TextFile.Load("ENG");
        Assert.Equal(text[NFS.Core.Constants.FrontEndConstants.TrackInfoDifficultyTexts[0]], menu.TrackInfoDifficulty);   // table[track] [ref:2980]
        Assert.Equal("novice", menu.TrackInfoDifficulty);                          // track 0 (OVAL) = TEXT 0x9F
        Assert.Equal("3.2 miles", menu.TrackInfoLength);                           // +0x5A = 0 → miles table [ref:2981]
        menu.Config[0x5A] = 1;
        Assert.Equal("5.1 km", menu.TrackInfoLength);                              // km table + TEXT 0x9D [ref:2982]
        menu.Config.Language = 5;
        Assert.Equal("5,1 km", menu.TrackInfoLength);                              // decimal comma [ref:2983]
        menu.Config[0x5A] = 0;
        Assert.Equal("3,2 miles", menu.TrackInfoLength);

        menu.Config.Language = 0;
        menu.Config.Track = 5;
        Assert.Equal("advanced", menu.TrackInfoDifficulty);                        // track 5 (MED) = TEXT 0xA1
        menu.Config.Track = 6;
        Assert.Equal("advanced", menu.TrackInfoDifficulty);
        menu.Config.Track = 2;
        Assert.Equal("intermediate", menu.TrackInfoDifficulty);                    // track 2 = TEXT 0xA0
    }

    [GameDataFact]
    public void TrackInfoArtAndLanguageOverlayComeFromTheTables()
    {
        var menu = OpenTrackInfo();
        // Track 0: QFS with six language overlays ([track * 6 +]). [ref:2984]
        var oval = TrackArt(menu.TrackInfoArt);
        Assert.Equal("ovalinfo", menu.TrackInfoArt);               // the original's tables keep the names in lowercase
        foreach (int language in new[] { 0, 1, 2, 3, 4, 5 })
        {
            menu.Config.Language = (byte)language;
            Assert.Equal(NFS.Core.Constants.MenuArtConstants.TrackInfoOverlays[language], menu.TrackInfoOverlay);
            Assert.NotNull(oval.Find(menu.TrackInfoOverlay));
        }
        // The second track has its own file and its own overlay name.
        menu.Config.Language = 0;
        menu.Config.Track = 1;
        Assert.Equal(NFS.Core.Constants.TextConstants.TrackInfoArt[1] + "info", menu.TrackInfoArt);
        Assert.Equal("ozinfo", menu.TrackInfoArt);
        Assert.NotNull(TrackArt(menu.TrackInfoArt).Find(menu.TrackInfoOverlay));
        Assert.NotEqual(oval, TrackArt(menu.TrackInfoArt));
    }

    [GameDataFact]
    public void TrackRecordsArtOverlayAndClearRecordsComeFromTheData()
    {
        var menu = OpenTrackRecords();
        Assert.Equal("ovalrec", menu.TrackRecordsArt);
        Assert.Equal(NFS.Core.Constants.TextConstants.TrackRecordsArt[0] + "rec", menu.TrackRecordsArt);
        var fsh = TrackArt(menu.TrackRecordsArt);
        Assert.NotNull(fsh.Find(menu.TrackRecordsOverlay));       // overlay [ref:2985]
        Assert.NotNull(fsh.Find("back"));

        // "clear records" (0xAC): ENTER asks for confirmation (TEXT 0x349), and only ENTER on the question [ref:2986]
        // saves the cleared.STF to the user directory; the game directory is untouched. [ref:2987]
        menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down);
        Assert.Equal(2, menu.SelectedIndex);
        string gameDir = Path.GetDirectoryName(GameFiles.FullPath(TrackRecords.FilePath(0)))!;
        string before = Snapshot(gameDir);
        string userDir = Directory.CreateTempSubdirectory("nfs-stats-").FullName;
        try
        {
            menu.StatsDirectory = userDir;
            menu.HandleKey(MenuKey.Enter);
            Assert.Equal(MenuController.TrackRecordsScreen, menu.CurrentScreen.Id);
            Assert.True(menu.ConfirmingClearRecords);
            Assert.True(menu.HasTrackRecords);
            menu.HandleKey(MenuKey.Enter);                        // default "no" (saves nothing) [ref:2988]
            Assert.False(menu.ConfirmingClearRecords);
            Assert.True(menu.HasTrackRecords);

            menu.HandleKey(MenuKey.Enter);                        // the question again
            menu.HandleKey(MenuKey.Right);                        // "yes"
            menu.HandleKey(MenuKey.Enter);                        // → the cleared user copy
            Assert.False(menu.ConfirmingClearRecords);
            Assert.False(menu.HasTrackRecords);                   // screen refreshed: no entries
            Assert.Equal(0, menu.SelectedIndex);                  // the greyed-out item does not stay selected
            string userFile = UserStats.PathFor(userDir, TrackRecords.FilePath(0));
            Assert.Equal(TrackRecords.ClearedBytes(), File.ReadAllBytes(userFile));
            Assert.Equal(before, Snapshot(gameDir));              // game directory untouched
        }
        finally { Directory.Delete(userDir, recursive: true); }
    }

    [GameDataFact]
    public void PersonalStatisticsTextsAndArtComeFromTheData()
    {
        var menu = OpenPersonalStats();
        var text = TextFile.Load("ENG");
        Assert.Equal(0, menu.StatTrack);
        Assert.Equal("ovalrec", menu.PersonalStatsArt);           // "<track>rec" [ref:2989]
        Assert.Equal(NFS.Core.Constants.TextConstants.TrackRecordsArt[0] + "rec", menu.PersonalStatsArt);
        var fsh = TrackArt(menu.PersonalStatsArt);
        Assert.NotNull(fsh.Find("back"));
        Assert.NotNull(fsh.Find(menu.PersonalStatsOverlay));      // overlay [ref:2990]
        Assert.NotNull(fsh.Find("dot "));                         // graph dots [ref:2991]

        // Laps header (TEXT 0x2B4) and car name; with a reversed track (config +0x1A) [ref:2992]
        // " (%s)" from TEXT 0xD ("backwards") is appended.
        Assert.Equal($"{text[MenuController.PersonalStatsLapsText]} 4", menu.PersonalStatsHeader);
        menu.Config.Backward = 1;
        Assert.Equal($"{text[MenuController.PersonalStatsLapsText]} 4 "
            + $"({text[MenuController.PersonalStatsBackwardText]})", menu.PersonalStatsHeader);
        menu.Config.Backward = 0;
        Assert.Equal(TrackRecords.CarName(text, menu.StatCar), menu.PersonalStatsCarName);
        Assert.Equal(text[MenuController.PersonalStatsViewText], menu.PersonalStatsViewName);

        // Graph: title "… history" (TEXT 0xC5) and time-axis labels 0xCB/0xCA at y 0x1B9; the PRH file of this
        // install state has no entries yet, so there are no end values (it finishes earlier). [ref:2993]
        var scene = new MenuScene(menu);
        scene.Render(new FrameBuffer());                          // fonts and TEXT load lazily in Render
        var lines = scene.PersonalStatLines();
        Assert.Contains(lines, l => l.Text == text[MenuController.PersonalStatsViewTitleText] && l.X == 0xF3
            && l.Y == text.Entries[MenuController.PersonalStatsHistoryText].Y + MenuController.PersonalStatsCarDy
            && l.Colour == 4 && l.Align == 3);
        Assert.Contains(lines, l => l.Text == text[MenuController.PersonalStatsOldestText] && l.X == 0xF3
            && l.Y == MenuScene.AxisLabelY && l.Colour == 4);
        Assert.Contains(lines, l => l.Text == text[MenuController.PersonalStatsNewestText] && l.X == 0x273
            && l.Y == MenuScene.AxisLabelY && l.Align == 4);
        Assert.Equal(3, lines.Count);
        Assert.Empty(menu.StatValues);

        // The graph type changes the name and the title (0xC1..0xC4 / 0xC5..0xC8).
        menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down); menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Right);
        Assert.Equal(1, menu.StatView);
        Assert.Equal(text[MenuController.PersonalStatsViewText + 1], menu.PersonalStatsViewName);
        Assert.Contains(scene.PersonalStatLines(),
            l => l.Text == text[MenuController.PersonalStatsViewTitleText + 1]);
    }

    [GameDataFact]
    public void TrackRecordsRowsComeFromTheStfFileForTheChosenLaps()
    {
        var menu = OpenTrackRecords();
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Review(fb, "track-records.png");

        var text = TextFile.Load("ENG");
        var stats = menu.TrackStats;
        Assert.NotNull(stats);
        Assert.True(menu.HasTrackRecords);                        // .STF from the disc has 31 entries with time ≠ 0
        Assert.EndsWith("/OVAL.STF", TrackRecords.FilePath(0));

        var lines = scene.TrackRecordLines();
        // Laps header: "<laps> <TEXT 0xAB>", in column 0xAB − 0x14, right-aligned, colour 2.
        var lapHeader = text.Entries[MenuController.RecordsLapsText];
        Assert.Equal($"{menu.RecordsLaps} {lapHeader.Text}", lines[0].Text);
        Assert.Equal(lapHeader.X - 0x14, lines[0].X);
        Assert.Equal(lapHeader.Y, lines[0].Y);
        Assert.Equal(2, lines[0].Colour);
        Assert.Equal(4, lines[0].Align);

        // Lap record label (0xB0) and the record itself (entry 0) at y 0x1AC, colour 2.
        var label = text.Entries[MenuController.RecordsLabelText];
        int labelX = text.Entries[MenuController.RecordsColumnText].X;
        Assert.Contains(lines, l => l.Text == label.Text && l.X == labelX
            && l.Y == MenuController.RecordsRecordRowY && l.Colour == label.Colour);
        Assert.Contains(lines, l => l.Text == TrackRecords.RecName(stats!.Records[0])
            && l.Y == MenuController.RecordsRecordRowY && l.Colour == 2
            && l.X == text.Entries[MenuController.RecordsNameText].X);
        Assert.Contains(lines, l => l.Text == text[TrackRecords.SkillText(stats.Records[0].Skill)]
            && l.Y == MenuController.RecordsRecordRowY && l.Colour == 2
            && l.X == text.Entries[MenuController.RecordsSkillText].X);

        // Ten position rows 0xB1..0xBA: label from TEXT, .STF entries from RowOffset(laps).
        int first = TrackRecords.RowOffset(menu.RecordsLaps);
        Assert.Equal(0xB, first);
        var time = new ResultScreen();
        for (int row = 0; row < MenuController.RecordsRowTexts; row++)
        {
            var rowLabel = text.Entries[MenuController.RecordsFirstRowText + row];
            Assert.Contains(lines, l => l.Text == rowLabel.Text && l.X == labelX && l.Y == rowLabel.Y
                && l.Colour == rowLabel.Colour);
            var rec = stats.Records[first + row];
            Assert.True(rec.Time != 0);                            // the game draws only entries with a time
            Assert.Contains(lines, l => l.Text == TrackRecords.RecName(rec) && l.Y == rowLabel.Y && l.Colour == 4
                && l.X == text.Entries[MenuController.RecordsNameText].X);
            Assert.Contains(lines, l => l.Text == TrackRecords.CarName(text, rec.Car) && l.Y == rowLabel.Y
                && l.Colour == 4 && l.X == text.Entries[MenuController.RecordsCarText].X + MenuController.RecordsCarOffset);
            Assert.Contains(lines, l => l.Text == time.Time(rec.Time) && l.Y == rowLabel.Y && l.Colour == 4
                && l.X == text.Entries[MenuController.RecordsTimeText].X);
            Assert.Contains(lines, l => l.Text == text[TrackRecords.SkillText(rec.Skill)] && l.Y == rowLabel.Y
                && l.Colour == 4 && l.X == text.Entries[MenuController.RecordsSkillText].X);
        }
    }

    [GameDataFact]
    public void PersonalStatisticsFilesAndRecordsComeFromTheData()
    {
        // "FEDATA/PC/STATS/PRH/prh0n" with the extension .STF (tracks 0..19) or .SSF (mirrored [ref:2994]
        // 20..27, where file index = track − 20); the "backwards" flag inserts "b" before the name.
        Assert.EndsWith("/PRH/PRH01.STF", PersonalStats.FilePath(0, false));
        Assert.EndsWith("/PRH/BPRH01.STF", PersonalStats.FilePath(0, true));
        Assert.EndsWith("/PRH/PRH02.STF", PersonalStats.FilePath(1, false));
        Assert.EndsWith("/PRH/PRH01.SSF", PersonalStats.FilePath(0x14, false));
        Assert.EndsWith("/PRH/BPRH08.SSF", PersonalStats.FilePath(0x1B, true));

        var file = PersonalStats.Load(0, false);
        Assert.NotNull(file);
        Assert.Equal(451, file!.Records.Count);                   // 9020 B / 20 B
        // Graph block = car * 30 + laps category * 10 entries (2 → 0, 4 → 1, 8 → 2). [ref:2995]
        var block = PersonalStats.Block(file, 1, 4);
        Assert.Equal(10, block.Count);
        Assert.All(block, r => Assert.Equal(1, r.Car));           // the record carries the car (+0) and the laps (+2)
        Assert.All(block, r => Assert.Equal(4, r.Laps));
        Assert.Equal(file.Records[1 * PersonalStats.RecordsPerCar + PersonalStats.RecordsPerLaps], block[0]);
        Assert.Equal(0, PersonalStats.LapsCategory(2));
        Assert.Equal(1, PersonalStats.LapsCategory(4));
        Assert.Equal(2, PersonalStats.LapsCategory(8));
        Assert.Empty(PersonalStats.Entries(block));               // all lap times in this file = 0

        // The graph type selects the record field: 0 crashes (+4), 1 lap time (+8) [ref:2996]
        // 2 total time (+0xC), 3 top speed (+0x10).
        var record = new PersonalStatRecord(3, 2, 7, 12345, 67890, 4321);
        Assert.Equal(new[] { 7, 7 }, PersonalStats.Values([record, record], 0));
        Assert.Equal(new[] { 12345 }, PersonalStats.Values([record], 1));
        Assert.Equal(new[] { 67890 }, PersonalStats.Values([record], 2));
        Assert.Equal(new[] { 4321 }, PersonalStats.Values([record], 3));
    }

    [GameDataFact]
    public void TrackInfoScreenPaintsTheTrackArtAndTheTwoLines()
    {
        var menu = OpenTrackInfo();
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Review(fb, "track-info.png");

        // Order: background "back" and language overlay (flag 0x400 at (0,0)), then the panel "sldr" on them [ref:2997]
        // with flag 0x1000, i.e. at the position from the shape header, and at the end two text lines: difficulty
        // (y 0x3F) and length (y 0x5A), right edge at x 0x268, font 0x12, alignment 4, colour 2.
        var fsh = TrackArt(menu.TrackInfoArt);
        var reference = new FrameBuffer();
        reference.Clear(0, 0, 0);
        OriginalHud.Blit(reference, Shape(fsh, "back"), 0, 0);
        OriginalHud.Blit(reference, Shape(fsh, menu.TrackInfoOverlay), 0, 0);
        var slider = Shape(fsh, "sldr");
        OriginalHud.Blit(reference, slider, slider.PosX, slider.PosY);
        var font = new FrontEndFont();
        var text = TextFile.Load("ENG");
        DrawItems(reference, menu, font, text);   // screen items [ref:2998]
        font.Draw(reference, menu.TrackInfoDifficulty, 0x268, 0x3F, 18, 4, 2);
        font.Draw(reference, menu.TrackInfoLength, 0x268, 0x5A, 18, 4, 2);
        ReviewDiff(reference, fb, "track-info");
        AssertPainted(reference, fb);
    }
}
