using NFS.Render;
using Xunit;

namespace NFS.Tests.Render;

public class HudOverlayTests
{
    private static readonly HudState S = new(25 << 16, 4, 2, 3, 5, 8, 64 * 75 + 32, true);

    [Fact]
    public void Texts()
    {
        Assert.Equal("90 KMH", HudOverlay.SpeedText(S));
        Assert.Equal("56 MPH", HudOverlay.SpeedText(S with { Metric = false }));
        Assert.Equal("R", HudOverlay.GearText(0));
        Assert.Equal("N", HudOverlay.GearText(1));
        Assert.Equal("3", HudOverlay.GearText(4));
        Assert.Equal("1:15.50", HudOverlay.TimeText(64 * 75 + 32));
    }

    [Fact]
    public void Draw_Deterministic_TextInExpectedPlaces()
    {
        var a = new FrameBuffer(); a.Clear(40, 40, 40);
        var b = new FrameBuffer(); b.Clear(40, 40, 40);
        HudOverlay.Draw(a, S);
        HudOverlay.Draw(b, S);
        Assert.Equal(a.Sha256(), b.Sha256());
        // "LAP" top-left: 'L' left column at (8..10, 8..22) is white
        Assert.Equal(HudOverlay.TextColor, a.Get(8, 8));
        Assert.Equal(HudOverlay.TextColor, a.Get(8, 8 + 14));
        // position text right-aligned to x = 631
        string pos = "POS 5/8";
        int px = 640 - 8 - HudOverlay.TextWidth(pos);
        Assert.Equal(HudOverlay.TextColor, a.Get(px, 8)); // 'P' top-left pixel
        // nothing drawn in the screen centre
        Assert.Equal(0x282828u, a.Get(320, 240));
        // gear line bottom-right: 'GEAR 3' last glyph '3' top-right pixel
        int gy = 480 - 8 - 15;
        Assert.Equal(HudOverlay.TextColor, a.Get(640 - 8 - 1, gy));
        var changed = new FrameBuffer(); changed.Clear(40, 40, 40);
        HudOverlay.Draw(changed, S with { Place = 6 });
        Assert.NotEqual(a.Sha256(), changed.Sha256());
    }

    [Fact]
    public void DrawText_AlphaBlendsTheGlyphsWithTheScene()
    {
        // 'A' top pixel is the middle column of the glyph box: (10 + 1·2, 10) at scale 2.
        var half = new FrameBuffer(); half.Clear(0, 0, 0);
        HudOverlay.DrawText(half, 10, 10, "A", 0xFFFFFF, 2, 128);
        Assert.Equal(0x808080u, half.Get(12, 10)); // (255·128 + 127)/255 = 128 per channel over black

        var invisible = new FrameBuffer(); invisible.Clear(0, 0, 0);
        HudOverlay.DrawText(invisible, 10, 10, "A", 0xFFFFFF, 2, 0);
        var untouched = new FrameBuffer(); untouched.Clear(0, 0, 0);
        Assert.Equal(untouched.Sha256(), invisible.Sha256());
    }
}
