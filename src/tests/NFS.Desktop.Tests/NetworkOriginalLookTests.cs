using NFS.Core.Constants;
using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Change network-menu-look, stage 2: the game resources of the original look (<see cref="FrontEndFont"/> with an RGB colour,
/// <see cref="MenuScene"/> backdrop / dialog / dim) and <see cref="OriginalNetBackend"/>, with the fallback to classic.</summary>
public sealed class NetworkOriginalLookTests
{
    static bool Same(FrameBuffer a, FrameBuffer b) => a.Rgba.AsSpan().SequenceEqual(b.Rgba);

    static int NonBlack(FrameBuffer fb, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) if (fb.Get(x, y) != 0) n++;
        return n;
    }

    [GameDataFact]
    public void FrontEndFont_RgbOverload_DrawsTheSamePixelsAsTheIndexOverload()
    {
        var font = new FrontEndFont();
        for (int colour = 0; colour < 9; colour++)
            foreach (int align in new[] { 0, 1, 2, 3, 4, 5 })
                foreach (int size in new[] { 13, 16, 18, 36 })
                {
                    var byIndex = new FrameBuffer(640, 480);
                    var byRgb = new FrameBuffer(640, 480);
                    font.Draw(byIndex, "Network 42: ready", 320, 100, size, align, colour);
                    font.Draw(byRgb, "Network 42: ready", 320, 100, size, align, RenderConstants.FrontEndTextColours[colour]);
                    Assert.True(NonBlack(byIndex, 0, 0, 640, 480) > 0);
                    Assert.True(Same(byIndex, byRgb), $"colour {colour} align {align} size {size}");
                }
    }

    [GameDataFact]
    public void FrontEndFont_RgbOverload_AnyColourGivesGlyphsOfThatColour()
    {
        var fb = new FrameBuffer(640, 480);
        new FrontEndFont().Draw(fb, "READY", 10, 10, 18, 0, 0x6FD27Au);
        uint max = 0;
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 200; x++)
            {
                uint c = fb.Get(x, y);
                if (c == 0) continue;
                // every glyph pixel is the colour scaled by the coverage: same hue, green strongest
                Assert.True((c >> 8 & 255) >= (c >> 16 & 255) && (c >> 8 & 255) >= (c & 255), $"{c:X6}");
                max = Math.Max(max, c >> 8 & 255);
            }
        Assert.True(max > 0x80);
    }

    [Fact]
    public void Dim_MovesPixelsTowardsTheDarkColourAndClipsToTheBuffer()
    {
        var fb = new FrameBuffer(640, 480);
        for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) fb.Set(x, y, 0xFFC080);
        MenuScene.Dim(fb, -10, 5, 20, 15, 0xB4);
        // (v * (255 - 0xB4) + 8 * 0xB4) / 255 per channel
        Assert.Equal(0x503E2Bu, fb.Get(0, 5));
        Assert.Equal(0x503E2Bu, fb.Get(19, 14));
        Assert.Equal(0xFFC080u, fb.Get(20, 14));   // right of the rectangle
        Assert.Equal(0xFFC080u, fb.Get(5, 15));    // below it
        Assert.Equal(0xFFC080u, fb.Get(5, 4));     // above it
        MenuScene.Dim(fb, 100, 100, 100, 200, 0xFF);     // empty rectangle: nothing
        Assert.Equal(0xFFC080u, fb.Get(100, 150));
    }

    [GameDataFact]
    public void Backdrop_DrawsTheLogoAndTheSideOfTheGameWhateverTheDetailLevel()
    {
        var menu = MenuController.Load();
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer(640, 480);
        Assert.True(scene.DrawNetworkBackdrop(fb));
        Assert.True(NonBlack(fb, 10, 20, 200, 90) > 190 * 70 / 2, "logo area");
        Assert.True(NonBlack(fb, 215, 0, 330, 480) > 115 * 480 / 2, "side area");
        Assert.True(NonBlack(fb, 34, 100, 210, 450) > 176 * 350 / 2, "board area");
        // no animated bars and no icons, so animation and the detail level change nothing
        menu.Config[0x12D] = 0;
        scene.AnimateChrome = true;
        scene.Advance(1.3);
        var again = new FrameBuffer(640, 480);
        Assert.True(scene.DrawNetworkBackdrop(again));
        Assert.True(Same(fb, again));
    }

    [GameDataFact]
    public void Fonts_HasTheFourSizesOfTheLayout()
    {
        var fonts = new MenuScene(MenuController.Load()).Fonts;
        Assert.NotNull(fonts);
        foreach (int size in new[] { NetFont.Small, NetFont.Medium, NetFont.Normal, NetFont.Digit })
        {
            Assert.Equal(size, OriginalNetBackend.FontSize(size));
            Assert.True(fonts!.Font(size).Measure("RACE") > 0);
        }
    }

    [GameDataFact]
    public void NetworkDialog_DrawsTheBoxInTheMiddle()
    {
        var scene = new MenuScene(MenuController.Load());
        var fb = new FrameBuffer(640, 480);
        for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) fb.Set(x, y, 0xFFFFFF);
        Assert.True(scene.DrawNetworkDialog(fb, "network race", "connecting to host:1"));
        Assert.NotEqual(0xFFFFFFu, fb.Get(320, 240));
        Assert.Equal(0xFFFFFFu, fb.Get(5, 5));
    }

    [GameDataFact]
    public void Fit_CutsALongTextWithDotsAndLeavesAShortOneAlone()
    {
        var font = new FrontEndFont().Font(18);
        Assert.Equal("short", OriginalNetBackend.Fit(font, "short", 18, 200));
        string cut = OriginalNetBackend.Fit(font, "A very long name that does not fit anywhere", 18, 120);
        Assert.EndsWith("...", cut);
        Assert.True(font.Measure(cut) <= 120, cut);
        Assert.StartsWith("A very", cut);
        Assert.Equal("", OriginalNetBackend.Fit(font, "A very long name", 18, 3));
        Assert.Same(cut, OriginalNetBackend.Fit(font, "A very long name that does not fit anywhere", 18, 120));   // cached
    }

    [GameDataFact]
    public void Fit_KeepTailKeepsTheEndAndTheCaretOfAFieldBeingEdited()
    {
        var font = new FrontEndFont().Font(18);
        const string typed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ_";
        Assert.Equal("short_", OriginalNetBackend.Fit(font, "short_", 18, 200, tail: true));
        string cut = OriginalNetBackend.Fit(font, typed, 18, 160, tail: true);
        Assert.StartsWith("...", cut);
        Assert.EndsWith("XYZ_", cut);
        Assert.True(font.Measure(cut) <= 160, cut);
        // the longest tail that fits: one more letter would not
        string longer = string.Concat("...", typed.AsSpan(typed.Length - (cut.Length - 3) - 1));
        Assert.True(font.Measure(longer) > 160, longer);
        // the blinking caret: the same field with a blank in its place keeps its end too
        Assert.EndsWith("XYZ ", OriginalNetBackend.Fit(font, typed[..^1] + " ", 18, 160, tail: true));
        Assert.Equal("", OriginalNetBackend.Fit(font, typed, 18, 3, tail: true));
        Assert.Same(cut, OriginalNetBackend.Fit(font, typed, 18, 160, tail: true));   // cached: nothing is built again per frame
        // without the flag the beginning stays
        Assert.StartsWith("ABC", OriginalNetBackend.Fit(font, typed, 18, 160));
    }

    [Fact]
    public void Roles_HaveReadableColoursOfTheOriginalPalette()
    {
        Assert.Equal(0x908080u, OriginalNetBackend.Colour(NetRole.Label));
        Assert.Equal(0xFFFFFFu, OriginalNetBackend.Colour(NetRole.Selected));
        Assert.Equal(0xD59C30u, OriginalNetBackend.Colour(NetRole.Value));
        Assert.Equal(0xF4CC28u, OriginalNetBackend.Colour(NetRole.ValueSelected));
        Assert.Equal(0x08F0F0u, OriginalNetBackend.Colour(NetRole.Header));
        Assert.Equal(0x857A7Au, OriginalNetBackend.Colour(NetRole.Disabled));   // lighter than before: it reads on the dim panels (S11)
        // the roles of the second review round: the idle button, the lines under the name on the board, the dimmed board, a warning on it
        Assert.Equal(0xC8C0C0u, OriginalNetBackend.Colour(NetRole.ButtonIdle));
        Assert.Equal(0x815412u, OriginalNetBackend.Colour(NetRole.InkSoft));
        Assert.Equal(0x484040u, OriginalNetBackend.Colour(NetRole.InkDisabled));   // the dim brown of the game, index 6 of its colour table
        Assert.Equal(0x800000u, OriginalNetBackend.Colour(NetRole.InkWarn));
        Assert.Equal(0x6FD27Au, OriginalNetBackend.Colour(NetRole.Good));
        Assert.Equal(0xF2573Fu, OriginalNetBackend.Colour(NetRole.Bad));
        Assert.Equal(0x815412u, OriginalNetBackend.Colour(NetRole.Muted));
        foreach (var role in Enum.GetValues<NetRole>()) Assert.NotEqual(0u, OriginalNetBackend.Colour(role));
    }

    [Fact]
    public void Panel_Original_DimsAndFramesOneKindPerState()
    {
        var fb = new FrameBuffer(640, 480);
        for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) fb.Set(x, y, 0x808080);
        NetPanel.Draw(fb, 10, 10, 110, 60, NetPanelKind.Data, original: true);
        NetPanel.Draw(fb, 200, 10, 300, 60, NetPanelKind.DataActive, original: true);
        NetPanel.Draw(fb, 400, 10, 500, 60, NetPanelKind.Banner, original: true);
        NetPanel.Draw(fb, 10, 100, 110, 150, NetPanelKind.ChatPlate, original: true);
        Assert.Equal(NetPanel.Border, fb.Get(10, 30));
        Assert.Equal(NetPanel.Border, fb.Get(109, 59));
        Assert.Equal(NetPanel.BorderActive, fb.Get(200, 30));
        Assert.Equal(0x2B2B2Bu, fb.Get(50, 30));                       // 70% towards 0x080808
        Assert.Equal(0x2B2B2Bu, fb.Get(450, 30));                      // the banner: the same dimming, no frame
        Assert.Equal(0x2B2B2Bu, fb.Get(400, 30));
        // the chat plate over the race picture is lighter (55%) and has no frame
        uint plate = fb.Get(10, 120);
        Assert.Equal(fb.Get(60, 120), plate);
        Assert.True((plate & 255) > 0x2B && (plate & 255) < 0x80);
    }

    [Fact]
    public void RowBar_MixesTheBarColourAt45Percent()
    {
        var fb = new FrameBuffer(640, 480);
        for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) fb.Set(x, y, 0x000000);
        NetPanel.RowBar(fb, 10, 10, 50, 20);
        Assert.Equal((0x7Au * 45 / 100) << 16 | (0x52u * 45 / 100) << 8 | 0x10u * 45 / 100, fb.Get(20, 15));
        Assert.Equal(0u, fb.Get(50, 15));
    }
}
