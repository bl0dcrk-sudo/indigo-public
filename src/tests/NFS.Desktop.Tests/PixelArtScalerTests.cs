using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Task 08j: MMPX 2× of the 2D layer (<see cref="PixelArtScaler"/>) and its cache in <see cref="FrameTargets.SmoothOverlay"/>.</summary>
public class PixelArtScalerTests
{
    static uint Px(FrameBuffer fb, int x, int y) => BitConverter.ToUInt32(fb.Rgba, (y * fb.Width + x) * 4);

    static void Put(FrameBuffer fb, int x, int y, uint c) => BitConverter.GetBytes(c).CopyTo(fb.Rgba, (y * fb.Width + x) * 4);

    const uint White = 0xFFFFFFFF, Black = 0xFF000000, Orange = 0xFF2090F0;

    [Fact]
    public void FlatAreasAndStraightEdgesAreCopiedLikeNearest()
    {
        var src = new FrameBuffer(16, 8);
        src.Clear(0xF0, 0x90, 0x20);
        for (int y = 0; y < 8; y++) for (int x = 8; x < 16; x++) Put(src, x, y, Black);   // one vertical edge
        var dst = new FrameBuffer(32, 16);
        PixelArtScaler.Mmpx2x(src, dst);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++)
                Assert.Equal(Px(src, x / 2, y / 2), Px(dst, x, y));
    }

    [Fact]
    public void ADiagonalStaircaseIsJoinedWithoutNewColours()
    {
        var src = new FrameBuffer(12, 12);
        src.Clear(255, 255, 255);
        for (int i = 0; i < 12; i++) Put(src, i, i, Black);   // a 1-pixel 45° line: nearest gives touching 2×2 blocks
        var dst = new FrameBuffer(24, 24);
        PixelArtScaler.Mmpx2x(src, dst);
        int black = 0;
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 24; x++)
            {
                uint c = Px(dst, x, y);
                Assert.True(c == White || c == Black);
                if (c == Black) black++;
            }
        Assert.True(black > 12 * 4, $"the steps are filled in ({black} black pixels, nearest has 48)");
        Assert.Equal(Black, Px(dst, 11, 10));   // the corner between (5,5) and (6,6) on the upper side
    }

    [Fact]
    public void TheCornersOfABlockGlyphAreRounded()
    {
        var src = new FrameBuffer(10, 10);
        src.Clear(0xF0, 0x90, 0x20);
        for (int y = 3; y < 7; y++) for (int x = 3; x < 7; x++) Put(src, x, y, Black);
        Put(src, 3, 3, Orange);   // a cut corner, as in the menu font
        var dst = new FrameBuffer(20, 20);
        PixelArtScaler.Mmpx2x(src, dst);
        Assert.Equal(Orange, Px(dst, 6, 6));
        Assert.Equal(Black, Px(dst, 7, 7));   // the cut corner pixel turns half black: a 45° edge instead of a 2×2 notch
    }

    [Fact]
    public void PremultiplyClearsTransparentColourAndScalesPartialAlpha()
    {
        var src = new FrameBuffer(4, 4);   // transparent (0) everywhere
        Put(src, 1, 1, 0x80FF8040);        // half-transparent pixel: r=0x40 g=0x80 b=0xFF alpha 0x80
        Put(src, 2, 2, Black);
        var dst = new FrameBuffer(8, 8);
        PixelArtScaler.Mmpx2x(src, dst, premultiply: true);
        Assert.Equal(0u, Px(dst, 0, 0));
        Assert.Equal(Black, Px(dst, 5, 5));
        uint c = Px(dst, 2, 2);
        Assert.Equal(0x80u, c >> 24);
        Assert.Equal(0x20u, c & 0xFF);
        Assert.Equal(0x40u, c >> 8 & 0xFF);
        Assert.Equal(0x80u, c >> 16 & 0xFF);
    }

    [Fact]
    public void SmoothOverlayIsKeptWhileTheLayerDoesNotChangeAndAllocatesNothingOnceWarm()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 2);
        var fb = t.BeginMenu();
        fb.Clear(0xF0, 0x90, 0x20);
        HudOverlay.DrawText(fb, 100, 100, "GAME SETUP", 0);
        var a = t.SmoothOverlay(out bool first);
        Assert.True(first);
        Assert.Equal(1280, a.Width);
        Assert.Equal(960, a.Height);
        Assert.Same(a, t.SmoothOverlay(out bool again));
        Assert.False(again);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++)
        {
            fb.Set(i, 0, 0xFFFFFF);   // a changed layer is magnified again into the same buffer
            Assert.Same(a, t.SmoothOverlay(out bool changed));
            Assert.True(changed);
            t.SmoothOverlay(out changed);
            Assert.False(changed);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(640, 640, false)]
    [InlineData(640, 480, false)]
    [InlineData(640, 960, true)]
    [InlineData(640, 1422, true)]
    [InlineData(640, 2133, true)]   // 2560×1600 full screen: 2× + linear as well
    [InlineData(1138, 2560, true)]
    public void EveryMagnifiedLayerIsSmoothed(int layer, int box, bool smooth) => Assert.Equal(smooth, FrameTargets.Smooths(layer, box));

    [Fact]
    public void OverASceneTheLayerIsMagnifiedPremultiplied()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 2);
        t.BeginScene();
        t.Overlay.Set(10, 10, 0xFFFFFF);
        t.Overlay.Blend(20, 20, 0xFFFFFF, 128);
        var s = t.SmoothOverlay(out _);
        Assert.Equal(0u, Px(s, 0, 0));
        Assert.Equal(White, Px(s, 20, 20));
        uint c = Px(s, 40, 40);
        Assert.True((c >> 24) is > 0 and < 255);
        Assert.True((c & 0xFF) <= (c >> 24), "colour ≤ alpha: premultiplied");
    }

    [Fact]
    public void Smooth2DIsOnByDefaultAndRoundTripsThroughPortJson()
    {
        var d = GraphicsSettings.Resolve(new DesktopOptions(), null);
        Assert.True(d.Smooth2D);
        var port = new PortSettings();
        (d with { Smooth2D = false }).WriteTo(port);
        Assert.False(port.GfxSmooth2D);
        Assert.False(GraphicsSettings.Resolve(new DesktopOptions(), port).Smooth2D);
        Assert.True(GraphicsSettings.Resolve(new DesktopOptions { GfxSmooth2D = true }, port).Smooth2D);
    }
}
