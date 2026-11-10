using System.Numerics;
using NFS.Render;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>Split-screen layout (projection, HUD): pure logic [ref:3303]
/// without game data.</summary>
public class SplitLayoutTests
{
    [Fact]
    public void Views_AreTwoHorizontalHalvesThatTileTheScreen()
    {
        var top = SplitLayout.View(0);
        var bottom = SplitLayout.View(1);
        Assert.Equal(new ViewRect(0, 0, 640, 240), top);
        Assert.Equal(new ViewRect(0, 240, 640, 240), bottom);
        Assert.Equal(top.Bottom, bottom.Y);
        Assert.Equal(480, bottom.Bottom);
        for (int y = 0; y < 480; y += 17)
            Assert.NotEqual(top.Contains(5, y), bottom.Contains(5, y));   // every row belongs to exactly one view
    }

    [Fact]
    public void ProjectionWindow_IsAFullScreenFrameCentredOnTheHalf()
    {
        foreach (int p in new[] { 0, 1 })
        {
            var v = SplitLayout.View(p);
            var w = SplitLayout.ProjectionWindow(p);
            Assert.Equal(640, w.Width);
            Assert.Equal(480, w.Height);
            Assert.Equal(v.Y + v.Height / 2, w.Y + w.Height / 2);   // same centre row as the half
        }
        Assert.Equal(-120, SplitLayout.ProjectionWindow(0).Y);
        Assert.Equal(120, SplitLayout.ProjectionWindow(1).Y);
    }

    [Fact]
    public void SplitCamera_IsTheMiddleOfTheFullScreenImage_NotAScaledSqueeze()
    {
        var full = new Camera();
        var half = SplitLayout.CreateCamera();
        Assert.Equal(full.Fx, half.Fx, 9);
        Assert.Equal(full.Fy, half.Fy, 9);          // same focal lengths as the full screen: no squash
        Assert.Equal(320, half.Cx, 9);
        Assert.Equal(120, half.Cy, 9);               // centre of the 640x240 half
        foreach (var world in new[] { new Vector3(3, 1, 20), new Vector3(-8, -2, 15), new Vector3(0.5f, 0.3f, 60) })
        {
            var (fx, fy) = full.Project(world);
            var (hx, hy) = half.Project(world);
            Assert.Equal(fx, hx, 6);
            Assert.Equal(fy - 120, hy, 6);           // the half shows rows 120..359 of the full frame
        }
    }

    [Fact]
    public void Hud_Offsets_FollowThePlayersHalf()
    {
        Assert.Equal(1, SplitLayout.HudOffsetY(0));
        Assert.Equal(0xF1, SplitLayout.HudOffsetY(1));
        Assert.Equal(SplitLayout.View(1).Y + 1, SplitLayout.HudOffsetY(1));
        Assert.Equal(-0xF0, SplitLayout.HudBottomOffsetY(0));   // hud2 sits at the bottom of the upper half
        Assert.Equal(0, SplitLayout.HudBottomOffsetY(1));
    }

    [Fact]
    public void Compose_PutsEachViewInItsOwnHalf()
    {
        var top = new FrameBuffer(640, 240);
        var bottom = new FrameBuffer(640, 240);
        top.Clear(255, 0, 0);
        bottom.Clear(0, 0, 255);
        var screen = new FrameBuffer();
        screen.Clear(1, 2, 3);
        SplitLayout.Compose(screen, top, bottom);
        Assert.Equal(0xFF0000u, screen.Get(0, 0));
        Assert.Equal(0xFF0000u, screen.Get(639, 239));
        Assert.Equal(0x0000FFu, screen.Get(0, 240));
        Assert.Equal(0x0000FFu, screen.Get(639, 479));
    }

    [Fact]
    public void OnlyTwoPlayersExist() => Assert.Throws<ArgumentOutOfRangeException>(() => SplitLayout.View(2));
}
