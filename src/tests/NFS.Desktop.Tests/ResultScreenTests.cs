using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class ResultScreenTests
{
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_T9_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    [GameDataFact]
    public void FinishedPlayer_ResultsComeAtOriginalStrictFiveSecondBoundary_WithoutAFinishNotice()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false);
        d.Runner.SetLaps(1);
        for (int n = 0; n < 80; n++) d.Step(default);
        d.Car.Lap = 1; d.Step(default);
        int finish = d.Car.LastProgressTick;
        Assert.Equal(2, d.Car.RaceState);
        Assert.Null(d.Results);
        // task 07a: no FINISH notice (the original has none); the race view stays until the results (PNG: finish.png,
        // pixel check without the notice in SplitScreenTests.Finish_DrawsNoNoticeInEitherHalf)
        var fb = new FrameBuffer(); d.Render(fb); Review(fb, "finish.png");
        while (d.Runner.Tick <= finish + 320) d.Step(default);
        // CheckFinish runs before the tick increments: the first step beyond the boundary still shows the race.
        Assert.Null(d.Results);
        d.Step(default);
        Assert.NotNull(d.Results);
        d.Render(fb); Review(fb, "race-results.png");
    }

    [GameDataFact]
    public void FontUsesFileMetricsCoverageColourAndRightAlignment()
    {
        var renderer = new FrontEndFont();
        var fb = new FrameBuffer(); fb.Clear(0, 0, 0);
        renderer.Draw(fb, "A", 100, 50, align: 1);
        // Independent reads of the glyph record and packed atlas, not the renderer's decoded coverage.
        var raw = GameFiles.ReadRaw("FEDATA/PC/TEXT/SWISS18.FFN");
        int record = 32 + 34 * 11;
        int gx = Bin.U16(raw, record + 4), gy = Bin.U16(raw, record + 6);
        int atlas = Bin.S32(raw, 28) + 16, stride = 68;
        for (int y = 0; y < 19; y++)
            for (int x = 0; x < 10; x++)
            {
                int packed = raw[atlas + (gy + y) * stride + (gx + x) / 2];
                int a = (packed >> (((gx + x) & 1) == 0 ? 4 : 0)) & 15;
                uint v = (uint)(255 * a >> 4);   // ramp: (fg * k + black * (15 - k)) >> 4, replaces the pixel [ref:2898]
                Assert.Equal(v * 0x010101u, fb.Get(91 + x, 55 + y));
            }
        Assert.Equal(0u, fb.Get(90, 55));
    }

    [GameDataFact]
    public void ResultTimeUsesNearestEvenAndLanguageSeparators()
    {
        var screen = new ResultScreen();
        Assert.Equal("0:00.02", screen.Time(1));
        Assert.Equal("0:00.12", screen.Time(8)); // 12.5 -> even, not HUD truncation at 1 tick.
        Assert.Equal("1:02.50", screen.Time(4000));
        Assert.Equal("60:00.00", screen.Time(230400));
        Assert.Equal("1'02\"50", new ResultScreen(language: 2).Time(4000));
    }

    [GameDataFact]
    public void ResultsUseUndimmedPostBackgroundAndAuthoritativeRowOrder()
    {
        var rows = new[] {
            new RacerResult(1, 4, "MCF1", false, false, 4000, [2000,2000], 2000, Driver: "Slick", CarType: 12),   // car >= 0xC: no Omega logo, row background without animation [ref:2899]
            new RacerResult(2, 0, "FF50", true, false, 4100, [1900,2200], 1900, Driver: "PLAYER") };
        var screen = new ResultScreen(); var fb = new FrameBuffer(); screen.Draw(fb, new(rows));
        var art = Fsh.Load("FEDATA/PC/ART/EPOST.QFS");
        var back = art.Decode(art.Find("back")!, art.GlobalPalette())!;
        int p = (470 * 640 + 630) * 4;
        Assert.Equal((uint)(back.Rgba[p] << 16 | back.Rgba[p + 1] << 8 | back.Rgba[p + 2]), fb.Get(630, 470));
        // First row's driver is drawn at TEXT.ENG's right-aligned action anchor, baseline100.
        var expected = new FrameBuffer(); screen.Background(expected);
        screen.Font.Draw(expected, "Slick", 167, 100, align: 1);
        for (int y = 100; y < 126; y++)
            for (int x = 100; x < 168; x++) Assert.Equal(expected.Get(x, y), fb.Get(x, y));
        screen.Draw(expected, new(rows.Reverse().ToArray()));
        Assert.NotEqual(expected.Sha256(), fb.Sha256());
    }

    [GameDataFact]
    public void DnfAndEstimatedTimesUseDistinctVisibleLabels()
    {
        var screen = new ResultScreen(); var fb = new FrameBuffer();
        var result = new RaceResults([
            new(1, 0, "FF50", false, true, 4000, [], null, RaceState: 1, Driver: "Slick"),
            new(2, 1, "MCF1", false, true, 4000, [], null, Driver: "Crash")]);
        screen.Draw(fb, result);
        var expected = new FrameBuffer(); screen.Background(expected);
        screen.Font.Draw(expected, "aborted", 480, 100, align: 1);
        screen.Font.Draw(expected, "1:02.50*", 480, 126, align: 1);
        for (int y = 100; y < 152; y++)
            for (int x = 400; x < 481; x++) Assert.Equal(expected.Get(x, y), fb.Get(x, y));
    }

    [GameDataFact]
    public void AllSixLocalizedPostArchivesRenderAllRacerRows()
    {
        var result = new RaceResults(Enumerable.Range(0, 9).Select(i =>
            new RacerResult(i + 1, i, "MCF1", false, false, 4000, [], null, Driver: "Slick")).ToArray());
        for (int language = 0; language < 6; language++)
        {
            var screen = new ResultScreen(language: language);
            var fb = new FrameBuffer(); screen.Draw(fb, result);
            var background = new FrameBuffer(); screen.Background(background);
            // All nine place labels, including KO's ninth racer, remain at the left table anchor.
            for (int row = 0; row < 9; row++)
                Assert.Contains(Enumerable.Range(100 + row * 26, 26).SelectMany(y =>
                    Enumerable.Range(20, 40).Select(x => fb.Get(x, y) != background.Get(x, y))), changed => changed);
        }
    }

    [GameDataFact]
    public void TournamentSummaryKeepsPointsAndKeyboardActions()
    {
        var loop = new GameLoop(); var m = loop.Menu.Controller; m.Config.RaceType = 1;
        for (int i = 0; i < m.CurrentScreen.Items.Count && m.CurrentScreen.Items[m.SelectedIndex].Text != "RACE"; i++)
            m.HandleKey(MenuKey.Down);
        m.HandleKey(MenuKey.Enter);
        var runner = new RaceRunner(m.Session!, withAi: true, countdown: false);
        runner.Step(default);
        var result = RaceResults.Capture(runner);
        m.CompleteRace(new(result.Racers.Select((r, i) => r with { RaceTicks = 6000 + i * 300 }).ToArray()));
        m.HandleKey(MenuKey.Enter); m.HandleKey(MenuKey.Enter);
        Assert.True(m.TournamentSummary);
        var fb = new FrameBuffer(); loop.Render(fb); Review(fb, "tournament-results.png");
        string before = fb.Sha256(); m.HandleKey(MenuKey.Down); loop.Render(fb);
        Assert.NotEqual(before, fb.Sha256());
        Assert.Equal(1, m.SelectedIndex);
    }

    [GameDataFact]
    public void CaptureIncludesEveryRacerAndNoTrafficWithOriginalDriverNames()
    {
        var cfg = FeConfig.Load();
        cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.TrafficOff = 0; cfg.Track = 1;
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        var run = new RaceRunner(new RaceLoader().Load(setup), withAi: true, countdown: false);
        run.Step(default);
        var result = RaceResults.Capture(run);
        Assert.True(run.Cars.Count > run.RacerCount);
        Assert.Equal(run.Laps.Ranking(), result.Racers.Select(r => r.CarIndex));
        Assert.Equal(run.RacerCount, result.Racers.Count);
        Assert.All(result.Racers, r => Assert.True((run.Cars[r.CarIndex].Race.Setup.CarClass & 3) != 0));
        var ai = Assert.Single(result.Racers, r => !r.Human);
        int personality = run.Cars[ai.CarIndex].Race.Setup.Personality;
        Assert.Equal(NFS.Core.Constants.RaceConstants.AiDriverNames[personality], ai.Driver);
        Assert.Equal(RaceLoader.CarName(setup, 0), Assert.Single(result.Racers, r => r.Human).Driver);
    }
}
