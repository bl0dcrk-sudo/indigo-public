using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Frame of the panels the port draws itself (<see cref="PopupBox"/>): the border is darker than the fill and than the
/// picture around it, so a pop-up does not melt into the background.</summary>
public sealed class PopupBoxTests
{
    static int Lum(uint rgb) => (int)((rgb >> 16 & 0xFF) * 3 + (rgb >> 8 & 0xFF) * 6 + (rgb & 0xFF));

    static FrameBuffer Background(uint rgb)
    {
        var fb = new FrameBuffer(640, 480);
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++) fb.Set(x, y, rgb);
        return fb;
    }

    [Theory]
    [InlineData(0x101820u, 0x141E28u, 0x0A0F14u)]    // network menu panel over its own dark background
    [InlineData(0x101820u, 0x101820u, 0x000000u)]    // over black
    [InlineData(0x101820u, 0x141E28u, 0x6080C0u)]    // over a bright picture
    [InlineData(0x000000u, 0x080808u, 0x202020u)]    // chat plate
    public void Panel_BorderIsDarkerThanFillAndDiffersFromIt(uint a, uint b, uint background)
    {
        var fb = Background(background);
        PopupBox.Panel(fb, 100, 80, 300, 200, a, b);
        int fillMin = Math.Min(Lum(a), Lum(b));
        // every pixel of the outermost ring: ≠ fill colours and darker than both (all-black fill: border stays black, not brighter)
        for (int x = 100; x < 300; x++)
            foreach (int y in new[] { 80, 199 })
            {
                uint p = fb.Get(x, y);
                Assert.True(Lum(p) <= fillMin, $"({x},{y}) {p:X6}");
                if (fillMin > 0) { Assert.NotEqual(a, p); Assert.NotEqual(b, p); Assert.True(Lum(p) < fillMin); }
            }
        for (int y = 80; y < 200; y++)
            foreach (int x in new[] { 100, 299 })
                Assert.True(Lum(fb.Get(x, y)) <= fillMin);
        // inner bevel is lighter than the fill; the interior still carries the chequer
        Assert.True(Lum(fb.Get(150, 82)) > Math.Max(Lum(a), Lum(b)));
        Assert.Equal(((150 + 150) & 1) == 0 ? a : b, fb.Get(150, 150));
        // nothing outside the rectangle is touched
        Assert.Equal(background, fb.Get(99, 150));
        Assert.Equal(background, fb.Get(300, 150));
        Assert.Equal(background, fb.Get(150, 79));
        Assert.Equal(background, fb.Get(150, 200));
    }

    [Fact]
    public void Panel_ClipsAtTheScreenEdgeWithoutThrowing()
    {
        var fb = Background(0x303030);
        PopupBox.Panel(fb, -2, -2, 642, 18, 0, 0x080808);
        PopupBox.Frame(fb, -10, 470, 700, 40, 0x101820);
        Assert.True(Lum(fb.Get(320, 17)) < Lum(0x303030));        // bottom border of a banner that runs off the screen
    }

    [Fact]
    public void GfxNotice_PlateHasADarkBorder()
    {
        var notice = new GfxNotice();
        notice.Show("GFX = TEST", "DETAIL TEXT", 0, 0xFFFFFF);
        var fb = Background(0x6080C0);
        notice.Draw(fb, 0.5);
        // find the plate's top-left corner on row 56 (y0): first column that differs from the background
        int x0 = -1;
        for (int x = 0; x < 640 && x0 < 0; x++) if (fb.Get(x, 70) != 0x6080C0) x0 = x;
        Assert.True(x0 > 0);
        uint edge = fb.Get(x0, 70), inside = fb.Get(x0 + 4, 70);
        Assert.True(Lum(edge) < Lum(inside), $"edge {edge:X6} inside {inside:X6}");
        Assert.True(Lum(edge) < Lum(fb.Get(x0 - 1, 70)));
    }
}
