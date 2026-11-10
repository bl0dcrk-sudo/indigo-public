using System.Numerics;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Hor+ panorama: the camera keeps the vertical angle and the pixel scale of the 4:3 frame, the frame is wider [ref:3457]
/// the 640×480 2D layer is centred; the option (<c>--gfx-wide</c>, port.json, PORT OPTIONS row, quality text). Off by default; Vanilla and Enhanced with the
/// panorama off are unchanged (golden hashes in <see cref="GraphicsModeTests"/>). Review pictures: NFS_GFX_CHECK_DIR=&lt;dir&gt; writes wide_*.png.</summary>
public class PanoramaTests
{
    // ---- camera and frame size (no game data) ----

    [Theory]
    [InlineData(1.0, 640)]
    [InlineData(4.0 / 3, 640)]
    [InlineData(16.0 / 9, 856)]
    [InlineData(16.0 / 10, 768)]
    [InlineData(21.0 / 9, 1120)]
    [InlineData(32.0 / 9, 1120)]   // capped at 21:9
    [InlineData(0.5, 640)]         // portrait: never narrower than 4:3
    [InlineData(double.NaN, 640)]
    public void ViewWidth_FollowsTheAspectRatio_CappedAndMultipleOfEight(double aspect, int expected)
    {
        Assert.Equal(expected, Panorama.ViewWidth(aspect));
        Assert.Equal(0, Panorama.ViewWidth(aspect) % 8);
    }

    [Fact]
    public void HorizontalAngleGrowsWithTheWidth_FocalLengthStays()
    {
        Assert.Equal(45, Panorama.HalfAngleDegrees(640), 6);
        Assert.InRange(Panorama.HalfAngleDegrees(856), 53, 54);     // 16:9 is about 107 degrees in total
        var normal = new Camera(640, 480);
        var wide = new Camera(856, 480, baseWidth: Camera.PanoramaBase(856, 480));
        Assert.Equal(normal.Fx, wide.Fx);                            // the same pixel scale (Hor+), not a stretched picture
        Assert.Equal(normal.Fy, wide.Fy);
        Assert.Equal(428, wide.Cx);
        Assert.Equal(640, wide.LayoutWidth);
        Assert.Equal(1.0, normal.CullSlope);
        Assert.Equal(856 / 2.0 / normal.Fx, wide.CullSlope, 9);      // tan of the wider half angle
        Assert.Null(Camera.PanoramaBase(640, 480));                  // a 4:3 frame is never a panorama
    }

    [Fact]
    public void ObjectOnTheAxis_StaysInTheCentre_AndKeepsItsOffsetFromIt()
    {
        foreach (int k in new[] { 1, 2 })
        {
            var wide = new Camera(856 * k, 480 * k, baseWidth: Camera.PanoramaBase(856 * k, 480 * k));
            var scaled = new Camera(640 * k, 480 * k);
            Assert.Equal(wide.Cx, wide.Project(new Vector3(0, 0, 25)).X, 9);        // on the axis: the middle of the wide frame
            Assert.Equal(wide.Cy, wide.Project(new Vector3(0, 0, 25)).Y, 9);
            foreach (var p in new[] { new Vector3(3, 1.5f, 20), new Vector3(-7, -2, 12), new Vector3(0.4f, 0.1f, 3) })
            {
                // the same distance from the centre as in the 4:3 frame of the same scale
                var a = scaled.Project(p); var b = wide.Project(p);
                Assert.Equal(a.X - scaled.Cx, b.X - wide.Cx, 9);
                Assert.Equal(a.Y - scaled.Cy, b.Y - wide.Cy, 9);
            }
        }
    }

    [Fact]
    public void CameraAt4x3_IsUnchanged()
    {
        var a = new Camera(640, 480);
        var b = new Camera(1280, 960, baseWidth: Camera.PanoramaBase(1280, 960));
        Assert.Null(Camera.PanoramaBase(1280, 960));
        Assert.Equal(a.Fx * 2, b.Fx, 9);
        Assert.Equal(a.Fy * 2, b.Fy, 9);
        Assert.Equal(1.0, b.CullSlope);
    }

    [Fact]
    public void SplitCamera_KeepsTheFocalLengthOfTheFullScreen_InAWiderView()
    {
        var normal = SplitLayout.CreateCamera(2);
        var wide = SplitLayout.CreateCamera(2, 856 * 2);
        Assert.Equal(1280, normal.Width);
        Assert.Equal(1712, wide.Width);
        Assert.Equal(normal.Fx, wide.Fx);
        Assert.Equal(normal.Fy, wide.Fy);
        Assert.Equal(normal.Cy, wide.Cy);
        Assert.Equal(1.0, normal.CullSlope);
        Assert.True(wide.CullSlope > 1.3);
        var screen = new FrameBuffer(1712, 960);
        var top = new FrameBuffer(1712, 480); top.Clear(255, 0, 0);
        var bottom = new FrameBuffer(1712, 480); bottom.Clear(0, 0, 255);
        SplitLayout.Compose(screen, top, bottom, 2, 1712);
        Assert.Equal(0xFF0000u, screen.Get(1711, 0));
        Assert.Equal(0x0000FFu, screen.Get(1711, 959));
    }

    [Fact]
    public void FrameTargets_WideSceneAndCentredLayer()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 2, 1, Panorama.ViewWidth(16.0 / 9));
        Assert.True(t.Wide);
        Assert.Equal((1712, 960), (t.SceneWidth, t.SceneHeight));
        var scene = t.BeginScene();
        scene.Clear(10, 20, 30);
        Assert.Equal(1712, scene.Width);
        Assert.Equal((640, 480), (t.Overlay.Width, t.Overlay.Height));    // the drawing code still sees 640x480
        t.Overlay.Set(0, 0, 0xFF0000); t.Overlay.Set(639, 479, 0x00FF00);
        var c = t.ComposeCpu();
        Assert.Equal((1712, 960), (c.Width, c.Height));
        Assert.Equal(0xFF0000u, c.Get(216, 0));            // (856 - 640) / 2 = 108 pixels, x2
        Assert.Equal(0xFF0000u, c.Get(217, 1));
        Assert.Equal(0x00FF00u, c.Get(216 + 1279, 959));
        Assert.Equal(0x0A141Eu, c.Get(215, 0));            // margins show the scene
        Assert.Equal(0x0A141Eu, c.Get(216 + 1280, 959));
    }

    [Fact]
    public void FrameTargets_CockpitRepeatsTheEdgeColumnsIntoTheMargins()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 1, 1, 768);
        t.BeginScene().Clear(0, 0, 0);
        t.Overlay.Set(0, 100, 0x112233); t.Overlay.Set(639, 100, 0x445566);
        Assert.Equal(0u, t.ComposeCpu().Get(10, 100));     // without the cockpit the margins are the scene
        t.ExtendEdges = true;
        var c = t.ComposeCpu();
        Assert.Equal(0x112233u, c.Get(0, 100));
        Assert.Equal(0x112233u, c.Get(63, 100));
        Assert.Equal(0x445566u, c.Get(768 - 1, 100));
        Assert.Equal(0u, c.Get(10, 101));                  // other rows repeat their own edge pixel (transparent here)
        Assert.Equal(0x112233u, c.Get(64, 100));           // the layer itself
    }

    [Fact]
    public void FrameTargets_NarrowWidthOrVanillaStayTheOriginalFrame()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 2, 1, 640);
        Assert.False(t.Wide);
        Assert.Equal((1280, 960), (t.SceneWidth, t.SceneHeight));
        Assert.Same(t.Overlay, t.FinishOverlay());
        t.Configure(GraphicsMode.Vanilla, 2, 1, 856);      // Vanilla ignores the panorama
        Assert.False(t.Wide);
        Assert.Equal((640, 480), (t.SceneWidth, t.SceneHeight));
        t.Configure(GraphicsMode.Enhanced, 1, 2, 856);     // SSAA multiplies the wide frame
        Assert.Equal((856 * 2, 480 * 2), (t.SceneWidth, t.SceneHeight));
        t.BeginScene();
        Assert.Equal((856, 480), (t.Scene.Width, t.Scene.Height));
    }

    // ---- option: parse, port.json, row, text ----

    [Fact]
    public void Option_IsOffByDefault_ParsedAndRoundTripsThroughPortJson()
    {
        Assert.False(GraphicsSettings.Resolve(DesktopOptions.Parse([]), null).Wide);
        Assert.True(GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx-wide", "on"]), null).Wide);
        Assert.False(GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx-wide", "off"]), new PortSettings { GfxWide = true }).Wide);   // CLI wins
        Assert.True(GraphicsSettings.Resolve(DesktopOptions.Parse([]), new PortSettings { GfxWide = true }).Wide);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-wide", "maybe"]));
        string path = Path.Combine(Path.GetTempPath(), $"nfs2se_wide_{Guid.NewGuid():N}.json");
        try
        {
            var s = new GraphicsSettings(GraphicsMode.Enhanced, 2) { Wide = true };
            var port = new PortSettings(); s.WriteTo(port); Assert.Null(port.Save(path));
            Assert.Contains("\"gfxWide\": true", File.ReadAllText(path));
            Assert.Equal(s, GraphicsSettings.Resolve(DesktopOptions.Parse([]), PortSettings.Load(path)));
            var off = new PortSettings(); (s with { Wide = false }).WriteTo(off); Assert.Null(off.Save(path));
            Assert.False(GraphicsSettings.Resolve(DesktopOptions.Parse([]), PortSettings.Load(path)).Wide);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ViewWidth_OnlyInEnhancedWithTheOptionOn_FollowsTheWindow()
    {
        var on = new GraphicsSettings(GraphicsMode.Enhanced, 0) { Wide = true };
        Assert.Equal(856, on.ViewWidth(1920, 1080));
        Assert.Equal(640, on.ViewWidth(1024, 768));                       // 4:3 window = no panorama
        Assert.Equal(640, (on with { Wide = false }).ViewWidth(1920, 1080));
        Assert.Equal(640, (on with { Mode = GraphicsMode.Vanilla }).ViewWidth(1920, 1080));
        Assert.Equal(640, on.ViewWidth(0, 0));
    }

    [Fact]
    public void QualityText_MentionsTheWideView_OnlyWhenOn()
    {
        var g = new GraphicsSettings(GraphicsMode.Enhanced, 0);
        Assert.DoesNotContain("WIDE", g.QualityText(1));
        Assert.Contains("WIDE VIEW", (g with { Wide = true }).QualityText(1));
        Assert.Equal("ORIGINAL RENDERER", (g with { Wide = true, Mode = GraphicsMode.Vanilla }).QualityText(1));
    }

    [Fact]
    public void PortOptionsRow_TogglesTheWideView()
    {
        var s = new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Enhanced, 0));
        while (s.SelectedOption != PortOption.Wide) s.Handle(MenuKey.Down);
        Assert.Equal("WIDE VIEW", PortOptionsScreen.Label(PortOption.Wide));
        Assert.Equal("OFF", s.Value(PortOption.Wide));
        Assert.True(s.Handle(MenuKey.Right).Changed);
        Assert.True(s.Settings.Wide);
        Assert.Equal("ON", s.Value(PortOption.Wide));
        s.Handle(MenuKey.Left);
        Assert.False(s.Settings.Wide);
        Assert.True(s.IsActive(PortOption.Wide));
        Assert.False(new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Vanilla, 0)).IsActive(PortOption.Wide));
    }

    // ---- scenes (need game data) ----

    static FrameBuffer Frame(string scene, int viewWidth, int k = 1, bool cockpitEdges = false)
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, k, 1, viewWidth);
        GfxScenes.Render(scene, t);
        t.ExtendEdges = cockpitEdges;
        return t.ComposeCpu();
    }

    /// <summary>Mean absolute error between the 4:3 frame a and the columns [bx, bx + a.Width) of the wide frame b.</summary>
    static double MeanAbsError(FrameBuffer a, FrameBuffer b, int bx)
    {
        long sum = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                for (int c = 0; c < 3; c++) sum += Math.Abs(a.Rgba[(y * a.Width + x) * 4 + c] - b.Rgba[(y * b.Width + bx + x) * 4 + c]);
        return sum / (3.0 * a.Height * a.Width);
    }

    [GameDataTheory]
    [InlineData("day")]
    [InlineData("tunnel")]
    [InlineData("car")]
    [InlineData("chase")]
    public void WideFrame_ContainsTheOriginalFrameInItsCentre(string scene)
    {
        // Hor+: the central 640 columns show the same picture as the 4:3 frame (same pixel scale and centre); the 3D scene only continues at the sides
        var normal = Frame(scene, 640);
        var wide = Frame(scene, 856);
        Assert.Equal((856, 480), (wide.Width, wide.Height));
        double mae = MeanAbsError(normal, wide, 108);
        Assert.True(mae < 0.5, $"{scene}: MAE of the centre {mae}");
        Assert.NotEqual(normal.Sha256(), wide.Sha256());
        Assert.True(Enumerable.Range(0, 480).Select(y => wide.Get(5, y)).Distinct().Count() > 3);   // the side margin is drawn, not blank
    }

    [GameDataFact]
    public void EnhancedWithoutThePanorama_IsTheSameAsBefore()
    {
        foreach (string scene in new[] { "day", "chase", "cockpit", "split" })
        {
            var a = new FrameTargets(); a.Configure(GraphicsMode.Enhanced, 2, 1); GfxScenes.Render(scene, a);
            var b = new FrameTargets(); b.Configure(GraphicsMode.Enhanced, 2, 1, 640); GfxScenes.Render(scene, b);
            Assert.Equal(a.ComposeCpu().Sha256(), b.ComposeCpu().Sha256());
        }
    }

    [GameDataFact]
    public void CockpitAndSplit_RenderInAWideFrame()
    {
        var cockpit = Frame("cockpit", 856, cockpitEdges: true);
        Assert.Equal((856, 480), (cockpit.Width, cockpit.Height));
        // the dashboard continues to the sides: the margin repeats the edge column of the layer
        Assert.Equal(cockpit.Get(108, 470), cockpit.Get(3, 470));
        Assert.Equal(cockpit.Get(108 + 639, 470), cockpit.Get(850, 470));
        var split = Frame("split", 856);
        Assert.Equal((856, 480), (split.Width, split.Height));
        Assert.NotEqual(0u, split.Get(3, 100));   // the halves are drawn across the whole width
        Assert.NotEqual(0u, split.Get(852, 400));
    }

    [GameDataFact]
    public void ReviewPictures()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        foreach (string scene in new[] { "day", "tunnel", "car", "chase", "cockpit", "split", "rain" })
        {
            Frame(scene, 856, 2, cockpitEdges: scene == "cockpit").SavePng(Path.Combine(dir, $"wide_{scene}_16x9.png"));
            Frame(scene, 640, 2).SavePng(Path.Combine(dir, $"wide_{scene}_4x3.png"));
        }
        Frame("chase", 1120, 1).SavePng(Path.Combine(dir, "wide_chase_21x9.png"));
        Frame("cockpit", 1120, 1, cockpitEdges: true).SavePng(Path.Combine(dir, "wide_cockpit_21x9.png"));
    }
}
