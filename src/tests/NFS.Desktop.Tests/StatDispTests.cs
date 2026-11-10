using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Result screens after the race (statdisp): the driver panel (screen) and lap times [ref:2946]
/// (screen), driver selection with keys 0x4B00/0x4D00. We check the view's behaviour – content and colour [ref:2947]
/// rows, painted glyphs, race data – not implementation details.</summary>
public class StatDispTests
{
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_T9_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    static RacerResult Racer(int place, string driver, string model, bool human, int race, int[] laps, int best,
        int state = 2, int top = 0) => new(place, place - 1, model, human, false, race, laps, best, state, driver, top);

    /// <summary>Four cars in finishing order; the player (CarObj Flags +0x1F4 bit 4) in the given place.</summary>
    static RaceResults Grid(int playerPlace) => new(
    [
        Racer(1, "Slick", "MCF1", playerPlace == 1, 12100, [2000, 1900, 2100], 1900),
        Racer(2, "PLAYER", "FF50", playerPlace == 2, 12200, [2000, 1900, 2100], 1900),
        Racer(3, "Crash", "F355", playerPlace == 3, 12300, [2000, 1900, 2100], 1900),
        Racer(4, "Racer", "GT90", playerPlace == 4, 12400, [2000, 1900, 2100], 1900)
    ]);

    /// <summary>Menu after the race: CompleteRace needs a race in progress, so the session is taken from the replay (track 4).</summary>
    static MenuController Results(RaceResults results)
    {
        var menu = MenuController.Load();
        menu.AdoptRace(new RaceScene("replay").Session);
        menu.CompleteRace(results);
        menu.EndVictory();   // the victory screen precedes the table – here we test just the table (VictoryScreenTests) [ref:2948]
        return menu;
    }

    /// <summary>All non-zero pixels of the pattern must appear on the screen (the glyph replaces the background pixel).</summary>
    static void AssertPainted(FrameBuffer reference, FrameBuffer screen)
    {
        int painted = 0;
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (reference.Get(x, y) != 0) { painted++; Assert.Equal(reference.Get(x, y), screen.Get(x, y)); }
        Assert.True(painted > 0);
    }

    [GameDataFact]
    public void DriverSelectionStartsOnTheLocalPlayerAndSkipsItWhenCycling()
    {
        var menu = Results(Grid(playerPlace: 2));
        Assert.Equal(MenuStatus.Results, menu.Status);
        Assert.Equal(MenuController.RaceResultsScreen, menu.CurrentScreen.Id);
        Assert.Equal(1, menu.ResultDriver);                       // start = the local player's place [ref:2949]
        menu.HandleKey(MenuKey.Right); Assert.Equal(2, menu.ResultDriver);
        menu.HandleKey(MenuKey.Right); Assert.Equal(3, menu.ResultDriver);
        menu.HandleKey(MenuKey.Right); Assert.Equal(0, menu.ResultDriver);   // wraps, but skips the player
        menu.HandleKey(MenuKey.Right); Assert.Equal(2, menu.ResultDriver);   // 0 → (1 = player) → 2
        menu.HandleKey(MenuKey.Left); Assert.Equal(0, menu.ResultDriver);
        menu.HandleKey(MenuKey.Left); Assert.Equal(3, menu.ResultDriver);
        menu.HandleKey(MenuKey.Down);                             // navigating the entries does not move the driver
        Assert.Equal(3, menu.ResultDriver);
    }

    [GameDataFact]
    public void SelectionNeedsTwoRacersAndOnlyPicksTheOtherWithTwo()
    {
        var solo = Results(new RaceResults([Racer(1, "PLAYER", "MCF1", true, 12000, [2000], 2000)]));
        Assert.Equal(0, solo.ResultDriver);
        solo.HandleKey(MenuKey.Right); solo.HandleKey(MenuKey.Left);
        Assert.Equal(0, solo.ResultDriver);                       // fewer than 2 racers: nothing to scroll [ref:3462]

        var duel = Results(new RaceResults(
            [Racer(1, "PLAYER", "MCF1", true, 12000, [2000], 2000), Racer(2, "Slick", "FF50", false, 12100, [2100], 2100)]));
        Assert.Equal(0, duel.ResultDriver);
        duel.HandleKey(MenuKey.Right); Assert.Equal(1, duel.ResultDriver);
        duel.HandleKey(MenuKey.Right); Assert.Equal(1, duel.ResultDriver);   // the only other driver
        duel.HandleKey(MenuKey.Left); Assert.Equal(1, duel.ResultDriver);
    }

    [GameDataFact]
    public void ContinueOpensLapTimesAndPreviousReturnsKeepingTheSelectedDriver()
    {
        var menu = Results(Grid(playerPlace: 2));
        menu.HandleKey(MenuKey.Right);
        menu.HandleKey(MenuKey.Enter);                            // "continue": [ref:2950]
        Assert.Equal(MenuController.LapTimesScreen, menu.CurrentScreen.Id);
        menu.HandleKey(MenuKey.Right);                            // also scrolls the driver [ref:2951]
        Assert.Equal(3, menu.ResultDriver);
        for (int n = 0; n < 8 && menu.CurrentScreen.Items[menu.SelectedIndex].Text != "previous"; n++)
            menu.HandleKey(MenuKey.Down);
        Assert.Equal("previous", menu.CurrentScreen.Items[menu.SelectedIndex].Text);
        menu.HandleKey(MenuKey.Enter);                            // [ref:2952]
        Assert.Equal(MenuController.RaceResultsScreen, menu.CurrentScreen.Id);
        Assert.Equal(3, menu.ResultDriver);
    }

    [GameDataFact]
    public void LapTimesViewListsEveryLapAndMarksTheFastestOne()
    {
        var screen = new ResultScreen();
        var text = TextFile.Load("ENG");
        int headerY = text.Entries[ResultScreen.LapHeaderText].Y;
        int numberX = text.Entries[ResultScreen.LapHeaderText].X, timeX = text.Entries[ResultScreen.LapColumnText].X;
        var lines = screen.LapTimesLines(Racer(2, "PLAYER", "FF50", true, 4100, [2000, 1900, 2100], 1900), 3);

        Assert.Equal("PLAYER", lines[0].Text);                    // name at y(0x2A6) = 100
        Assert.Equal(text.Entries[ResultScreen.DriverNameText].Y, lines[0].Y);
        Assert.Equal("FF50", lines[1].Text);                      // car name at the height of header 0x2B2
        Assert.Equal(headerY, lines[1].Y);
        Assert.Equal(text[ResultScreen.LapHeaderText], lines[2].Text);
        Assert.Equal(timeX, lines[1].X);
        var times = lines.Where(l => l.Y > headerY && l.X == timeX).ToArray();
        string[] expectedTimes = ["0:31.25", "0:29.69", "0:32.81"];
        int[] expectedColours = [4, 2, 4];   // fastest lap in colour 2, the others 4 [ref:2953]
        Assert.Equal(expectedTimes, times.Select(l => l.Text).ToArray());
        Assert.Equal(expectedColours, times.Select(l => l.Colour).ToArray());
        string[] expectedNumbers = ["1", "2", "3"];   // lap number in column 0x2B2 [ref:2954]
        Assert.Equal(expectedNumbers, lines.Where(l => l.Y > headerY && l.X == numberX).Select(l => l.Text).ToArray());
        int[] expectedRows = [.. Enumerable.Range(1, 3).Select(n => headerY + ResultScreen.Spacing * n)];
        Assert.Equal(expectedRows, times.Select(l => l.Y).ToArray());
        Assert.All(times, l => Assert.Equal(5, l.Align));         // times at the centre of column 0x2A4
        Assert.All(lines.Where(l => l.Y > headerY && l.X == numberX), l => Assert.Equal(4, l.Align));   // numbers right-aligned
    }

    [GameDataFact]
    public void AbortedRacerShowsAbortedInsteadOfTheTotalAndTopSpeed()
    {
        var screen = new ResultScreen();
        var text = TextFile.Load("ENG");
        var racer = Racer(5, "Crash", "GT90", false, 4000, [2000], 2000, state: 1);   // state +0x280 ≠ 2

        var lines = screen.LapTimesLines(racer, 4);
        Assert.Contains(lines, l => l.Text == text[ResultScreen.AbortedText]);
        Assert.DoesNotContain(lines, l => l.Text == screen.Time(4000));

        var panel = screen.DriverPanelLines(racer, 242);
        Assert.Equal(text[ResultScreen.AbortedText], panel[5].Text);
        Assert.Equal(text[ResultScreen.NotAvailableText], panel[7].Text);
    }

    [GameDataFact]
    public void DriverPanelShowsOrdinalPlaceTotalTimeAndTopSpeedInTheSelectedUnit()
    {
        var screen = new ResultScreen();
        var text = TextFile.Load("ENG");
        var racer = Racer(3, "PLAYER", "F355", true, 4100, [2000, 2100], 2000, top: 0x300000);   // 48 m/s
        var panel = screen.DriverPanelLines(racer, 242);

        Assert.Equal(text[ResultScreen.PlaceLabelText], panel[2].Text);
        Assert.Equal("3rd", panel[3].Text);                       // (0x284 + place) [ref:2955]
        Assert.Equal(text[ResultScreen.TopSpeedText], panel[6].Text);
        int valueX = text.Entries[ResultScreen.DriverValueText].X;
        Assert.All(new[] { 3, 5, 7 }, i => Assert.Equal(valueX, panel[i].X));   // values in column 0x279
        Assert.Equal(screen.Time(4100), panel[5].Text);
        Assert.Equal("172.80 KMH", panel[7].Text);                // km/h [ref:2956]
        // Name (0x29d, y 184) and car (y(0x280) − 0x18 = 218) above the value column.
        Assert.Equal("PLAYER", panel[0].Text); Assert.Equal(184, panel[0].Y);
        Assert.Equal("F355", panel[1].Text); Assert.Equal(242 - 0x18, panel[1].Y);
        screen.Metric = false;
        Assert.Equal("107.37 MPH", screen.DriverPanelLines(racer, 242)[7].Text);
    }

    [GameDataFact]
    public void EmptyRecordHidesTheRecordLine()
    {
        var screen = new ResultScreen();
        Assert.Empty(screen.RecordLines(ResultScreen.TotalRecordText, null));   // time 0 in .STF → the game does not draw the line
        var text = TextFile.Load("ENG");
        var anchor = text.Entries[ResultScreen.LapRecordText];
        var lines = screen.RecordLines(ResultScreen.LapRecordText, "Daniel     0:49.50");
        string[] expected = [text[ResultScreen.LapRecordText], "Daniel     0:49.50"];
        Assert.Equal(expected, lines.Select(l => l.Text).ToArray());
        Assert.Equal(anchor.Y, lines[1].Y);
        Assert.Equal(anchor.Y, lines[0].Y);
        Assert.Equal(anchor.X + 10, lines[1].X);                 // stattool: value 10 px to the right of the label
        Assert.Equal(2, lines[1].Colour);                        // white (2), to the left, with a shadow (align 3)
        Assert.Equal(3, lines[1].Align);
    }

    [GameDataFact]
    public void LapTimesScreenPaintsTheSelectedRacersLapsOverThePostBackground()
    {
        var menu = Results(Grid(playerPlace: 1));
        menu.HandleKey(MenuKey.Enter);                            // "continue" [ref:2957]
        Assert.Equal(MenuController.LapTimesScreen, menu.CurrentScreen.Id);
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        Review(fb, "lap-times.png");

        var text = TextFile.Load("ENG");
        var glyph = new FrameBuffer(); glyph.Clear(0, 0, 0);
        new ResultScreen().Font.Draw(glyph, "1", text.Entries[ResultScreen.LapHeaderText].X,
            text.Entries[ResultScreen.LapHeaderText].Y + ResultScreen.Spacing, align: 4, colour: 2);
        AssertPainted(glyph, fb);                                 // number of the first lap, in the selection colour
    }

    [GameDataFact]
    public void ResultsScreenDrawsTheTrackRecordForTheRacesLapCount()
    {
        var menu = Results(Grid(playerPlace: 2));
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        Review(fb, "results-record.png");

        var screen = new ResultScreen();
        var text = TextFile.Load("ENG");
        var record = TrackRecords.Total(VictoryRules.MenuTrack(menu.Session!.TrackNumber), menu.Session.Setup.NumLaps);   // [ref:2958]
        Assert.NotNull(record);
        var anchor = text.Entries[ResultScreen.TotalRecordText];
        var glyph = new FrameBuffer(); glyph.Clear(0, 0, 0);
        screen.Font.Draw(glyph, TrackRecords.Text(text, record!.Value, screen.Time), anchor.X + 10, anchor.Y,
            align: 3, colour: 2);
        AssertPainted(glyph, fb);
    }

    [GameDataFact]
    public void CaptureTakesTopSpeedFromTheLapMaximumAndKeepsEveryRacer()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false);
        d.Runner.SetLaps(1);
        for (int n = 0; n < 80; n++) d.Step(default);
        d.Car.Lap = 1; d.Step(default);
        for (int n = 0; n < 400 && d.Results == null; n++) d.Step(default);
        var human = d.Results!.Racers.Single(r => r.Human);
        Assert.Equal(d.Runner.Cars[human.CarIndex].Obj.LapMaxSpeed.Max(), human.TopSpeedFx);
        Assert.True(human.TopSpeedFx > 0);
    }
}
