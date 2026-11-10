using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>In mode 2 (KO/tournament screens): the same animation as [ref:2945]
/// mode 0, but the shape "rec7" from the track records QFS, shifted by (−10, +0x19), only for the winner &lt; 0xC.</summary>
public class StatDispSeriesLogoTests
{
    static RaceResults Results(int winnerCarType, int cars = 9, int humans = 1)
    {
        var order = Enumerable.Range(0, cars).Select(car => new RacerResult(car + 1, car, "MCF1", car < humans, car >= humans,
            4000 + 100 * car, [], 2000 + 10 * car, Driver: car < humans ? "PLAYER" : "Slick", CarType: car == 0 ? winnerCarType : 1)).ToArray();
        return new RaceResults(order);
    }

    // one controller per series kind (StartRace loads the race) – network tests have time limits
    static MenuController Series(int raceType, RaceResults results)
    {
        var c = MenuController.Load();
        c.Config.GameType = 1; c.Config.RaceType = (byte)raceType; c.Config.TrafficOff = 1;
        c.StartRace();
        c.CompleteRace(results);
        c.HandleKey(MenuKey.Enter); c.HandleKey(MenuKey.Enter);
        return c;
    }

    static readonly Lazy<MenuController> Knockout = new(() => Series(2, Results(0)));
    static readonly Lazy<MenuController> Tournament = new(() => Series(1, Results(0, 8, 2)));
    static readonly Lazy<MenuController> KnockoutBigCar = new(() => Series(2, Results(12)));
    static readonly Lazy<ResultScreen> Screen = new(() => new ResultScreen());

    static FrameBuffer Render(MenuController menu, RaceResults results, long ticks)
    {
        Screen.Value.SetLogoTicks(ticks);
        var fb = new FrameBuffer();
        Screen.Value.Draw(fb, results, menu);
        return fb;
    }

    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_STATDISP_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    static int Diff(FrameBuffer a, FrameBuffer b, Func<int, int, bool> inside, bool wantInside)
    {
        int n = 0;
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
                if (a.Get(x, y) != b.Get(x, y) && inside(x, y) == wantInside) n++;
        return n;
    }

    [GameDataFact]
    public void TheRec7ShapeComesFromTheTrackRecordsArtOfEveryTrack()
    {
        var screen = Screen.Value;
        for (int track = 0; track < 8; track++)
        {
            var image = screen.SeriesLogo(track);
            Assert.True(image is { Width: > 0, Height: > 0 }, $"tor {track}");
        }
    }

    [GameDataFact]
    public void TheKnockoutSummaryDrawsTheLogoAndOnlyTheFrameChanges()
    {
        var menu = Knockout.Value;
        Assert.True(menu.KnockoutSummary);
        var results = menu.Results!;
        var logo = Screen.Value.SeriesLogo(menu.Config.Track)!;
        int x0 = logo.PosX + ResultScreen.SeriesLogoDx, y0 = logo.PosY + ResultScreen.SeriesLogoDy;
        bool In(int x, int y) => x >= x0 && x < x0 + logo.Width && y >= y0 && y < y0 + logo.Height;
        var a = Render(menu, results, 0);
        var b = Render(menu, results, 400);
        Review(a, "ko_frame00.png"); Review(Render(menu, results, 80), "ko_frame08.png"); Review(Render(menu, results, 140), "ko_frame14.png");
        Review(b, "ko_frame40.png");
        Assert.True(Diff(a, b, In, true) > 100, "the logo changes between frames");
        Assert.Equal(0, Diff(a, b, In, false));
    }

    [GameDataFact]
    public void TheTournamentSummaryDrawsTheLogoToo()
    {
        var menu = Tournament.Value;
        Assert.True(menu.TournamentSummary);
        var big = Render(KnockoutBigCar.Value, KnockoutBigCar.Value.Results!, 400);
        var tournament = Render(menu, menu.Results!, 400);
        Review(tournament, "tournament_final.png");
        var logo = Screen.Value.SeriesLogo(menu.Config.Track)!;
        int x0 = logo.PosX + ResultScreen.SeriesLogoDx, y0 = logo.PosY + ResultScreen.SeriesLogoDy;
        bool In(int x, int y) => x >= x0 && x < x0 + logo.Width && y >= y0 && y < y0 + logo.Height;
        Assert.True(Diff(tournament, Render(menu, menu.Results! with { Racers = menu.Results!.Racers.Select(r => r with { CarType = 12 }).ToArray() }, 400), In, true) > 100);
        Assert.NotNull(big);
    }

    [GameDataFact]
    public void AWinnerWithACarOf0xCOrAboveGetsNoLogo()
    {
        var menu = KnockoutBigCar.Value;
        Assert.True(menu.KnockoutSummary);
        var with = Render(Knockout.Value, Results(0), 400);
        var without = Render(menu, Results(12), 400);
        var logo = Screen.Value.SeriesLogo(menu.Config.Track)!;
        int x0 = logo.PosX + ResultScreen.SeriesLogoDx, y0 = logo.PosY + ResultScreen.SeriesLogoDy;
        bool In(int x, int y) => x >= x0 && x < x0 + logo.Width && y >= y0 && y < y0 + logo.Height;
        Assert.True(Diff(with, without, In, true) > 100);
        Assert.Equal(0, Diff(with, without, In, false));
    }

    [GameDataFact]
    public void WithoutAnimationDetailTheSeriesLogoIsDrawnAtOnceAsInTheFinalFrame()
    {
        var menu = Knockout.Value;
        byte detail = menu.Config[MenuScene.DetailOffset];
        try
        {
            menu.Config[MenuScene.DetailOffset] = 0;
            var still = Render(menu, menu.Results!, 0);
            var done = Render(menu, menu.Results!, 100000);
            Assert.Equal(0, Diff(still, done, (_, _) => true, true));
        }
        finally { menu.Config[MenuScene.DetailOffset] = detail; }
    }
}
