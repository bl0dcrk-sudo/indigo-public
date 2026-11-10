using NFS.Data;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

public class OriginalHudTests
{
    [Theory]
    [InlineData(0x120, -1)]
    [InlineData(0x121, 1)] // blank plate
    [InlineData(0x13F, 1)]
    [InlineData(0x140, 2)] // "3"
    [InlineData(0x17F, 2)]
    [InlineData(0x180, 3)] // "2"
    [InlineData(0x1BF, 3)]
    [InlineData(0x1C0, 4)] // "1"
    [InlineData(0x1FF, 4)]
    [InlineData(0x200, 5)] // "GO!"
    [InlineData(0x21F, 5)]
    [InlineData(0x220, -1)]
    public void Countdown_HasOriginalBoundaries(int tick, int shape) => Assert.Equal(shape, OriginalHud.CountdownShape(tick));

    [Theory]
    [InlineData(0, 5)] [InlineData(1, -1)] [InlineData(2, 5)] [InlineData(3, -1)] [InlineData(4, -1)] [InlineData(5, 5)]
    public void Countdown_GoIsHiddenForLanguages134(int language, int shape)
    {
        Assert.Equal(shape, OriginalHud.CountdownShape(0x200, language));
        Assert.Equal(4, OriginalHud.CountdownShape(0x1C0, language));
    }

    /// <summary>Regression: the port looked up cnt0, which DTEXTURE.FSH does not have (cnt1..cnt5) → crash at tick 0x121.</summary>
    [GameTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RealArtwork_DrawsEveryCountdownTick(int language)
    {
        var hud = new OriginalHud(GameData.Dir, language);
        var fb = new FrameBuffer();
        for (int tick = 0; tick <= 0x240; tick++)
        {
            fb.Clear(1, 2, 3);
            hud.Draw(fb, new(0, 1, 1, 8, 1, 8, 0, 0, 0, 7000, tick, false, false));
            bool shown = OriginalHud.CountdownShape(tick, language) > 0;
            Assert.Equal(shown, fb.Get(191 + 95, 89 + 44) != 0x010203u);
        }
    }

    [Theory]
    [InlineData(0, " 0:00.00")]
    [InlineData(63, " 0:00.98")]
    [InlineData(65, " 0:01.01")]
    [InlineData(-65, " 0:01.01")]
    [InlineData(3840, " 1:00.00")]
    [InlineData(230400, " 0:00.00")]
    public void Time_FormatsLikeDhFormatTime(int ticks, string expected) => Assert.Equal(expected, OriginalHud.Time(ticks));

    [Theory]
    [InlineData(-100, 7000, 0)]
    [InlineData(3500, 7000, 7)]
    [InlineData(6999, 7000, 13)]
    [InlineData(7000, 7000, 14)]
    [InlineData(9000, 7000, 14)]
    public void Tachometer_ClampsOriginalFifteenSteps(int rpm, int redline, int level) =>
        Assert.Equal(level, OriginalHud.GaugeLevel(rpm, redline));

    [GameTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RealArtwork_UsesOriginalLayoutAndTransparentViewport(int language)
    {
        var hud = new OriginalHud(GameData.Dir, language);
        Assert.Equal((12, 5), hud.Slot(3));
        Assert.Equal((113, 34), hud.Slot(4));
        Assert.Equal((318, 4), hud.Slot(0));
        Assert.Equal((612, 4), hud.Slot(1));
        Assert.Equal((477, 4), hud.Slot(2));
        Assert.Equal((53, 475), hud.Slot(11));
        var fb = new FrameBuffer(); fb.Clear(1, 2, 3);
        hud.Draw(fb, new(123, 4, 2, 8, 3, 8, 4000, 3900, 3500, 7000, 1000, false, true));
        Assert.Equal(0x010203u, fb.Get(320, 240)); // no opaque rectangle over the race
        Assert.NotEqual(0x010203u, fb.Get(10, 10));
        var low = fb.Rgba.ToArray();
        hud.Draw(fb, new(123, 4, 2, 8, 3, 8, 4000, 3900, 7000, 7000, 1000, false, true));
        Assert.NotEqual(low, fb.Rgba); // redline changes the original three tachometer sprites
    }

    static FrameBuffer Frame(OriginalHud hud, OriginalHudValues v)
    {
        var fb = new FrameBuffer(); fb.Clear(1, 2, 3);
        hud.Draw(fb, v);
        return fb;
    }

    /// <summary>The 468×38 centre band of HUD00.FSH (hud5/hud6/hud7 all carry the same header position).</summary>
    static readonly (int X, int Y, int W, int H) BandRect = (172, 0, 468, 38);

    static (int X, int Y, int W, int H) SlotRect(OriginalHud hud, int slot)
    {
        var (x, y) = hud.Slot(slot);
        return (x - 0x100, y - 0x20, 0x120, 0x40); // digits and text are drawn to the left of the slot anchor
    }

    static int DiffCount(FrameBuffer a, FrameBuffer b, (int X, int Y, int W, int H) r)
    {
        int n = 0;
        for (int y = Math.Max(r.Y, 0); y < Math.Min(r.Y + r.H, a.Height); y++)
            for (int x = Math.Max(r.X, 0); x < Math.Min(r.X + r.W, a.Width); x++)
                if (a.Get(x, y) != b.Get(x, y)) n++;
        return n;
    }
    static int DiffOutside(FrameBuffer a, FrameBuffer b, params (int X, int Y, int W, int H)[] rects)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.Get(x, y) != b.Get(x, y) && !rects.Any(r => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H)) n++;
        return n;
    }

    /// <summary>Centre band: band 1 swaps hud5 for hud6 (the "WRONG WAY!" band) and hides the time/lap/position [ref:3289]
    /// digits with the LAP/POS labels; band 2 swaps in hud7 and keeps the digits in the yellow font. [ref:3290]</summary>
    [GameFact]
    public void RealArtwork_WrongWayBandSwitchesTheCentreBandAndHidesLabelsAndDigits()
    {
        var hud = new OriginalHud(GameData.Dir);
        var v = new OriginalHudValues(100, 3, 1, 4, 3, 8, 4000, 0, 5000, 7000, 1000, false, true);
        var normal = Frame(hud, v);
        var hazard = Frame(hud, v with { Band = 1 });
        var yellow = Frame(hud, v with { Band = 2 });
        Assert.True(DiffCount(normal, hazard, BandRect) > 400, "hud6 replaces hud5");
        Assert.True(DiffCount(hazard, yellow, BandRect) > 400, "hud7 replaces hud6");
        Assert.True(DiffCount(normal, yellow, BandRect) > 400, "hud7 replaces hud5");

        // Outside the band rectangle only the hidden fields change: time (0), lap (1), position (2), LAP (7), POS (8).
        var hidden = new[] { 0, 1, 2, 7, 8 }.Select(s => SlotRect(hud, s)).ToArray();
        Assert.Equal(0, DiffOutside(normal, hazard, hidden.Append(BandRect).ToArray()));
        Assert.Equal(0, DiffOutside(hazard, yellow, hidden.Append(BandRect).ToArray()));
        // Band 2 redraws the numbers in the yellow font, but never the labels.
        Assert.Equal(0, DiffOutside(normal, yellow,
            new[] { 0, 1, 2 }.Select(s => SlotRect(hud, s)).Append(BandRect).ToArray()));

        // Whatever those slots would show is irrelevant while band 1 is up …
        Assert.Equal(hazard.Rgba, Frame(hud, v with { Band = 1, LapTicks = 30000, Lap = 7, Place = 2 }).Rgba);
        // … and does change the frame without it.
        Assert.NotEqual(normal.Rgba, Frame(hud, v with { LapTicks = 30000, Lap = 7, Place = 2 }).Rgba);
        // Gear and speed have their own fonts (tests slots 3/4 before the band) and keep showing. [ref:3291]
        Assert.NotEqual(hazard.Rgba, Frame(hud, v with { Band = 1, Gear = 5 }).Rgba);
        Assert.NotEqual(hazard.Rgba, Frame(hud, v with { Band = 1, Speed = 190 }).Rgba);
    }

    [Fact]
    public void Blit_ClipsAndPreservesTransparentPixels()
    {
        var fb = new FrameBuffer(2, 2); fb.Clear(1, 2, 3);
        var img = new FshImage("test", 2, 2, [255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255], 0, 0, 0, 0);
        OriginalHud.Blit(fb, img, 0, 0);
        Assert.Equal(0xFF0000u, fb.Get(0, 0));
        Assert.Equal(0x010203u, fb.Get(1, 0));
        Assert.Equal(0x0000FFu, fb.Get(0, 1));
        OriginalHud.Blit(fb, img, -1, -1);
        Assert.Equal(0xFFFFFFu, fb.Get(0, 0));
    }
}
