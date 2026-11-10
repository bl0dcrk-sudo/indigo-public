using System.Diagnostics;
using NFS.Desktop;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Enhanced image quality (stage 2): perspective-correct UV, bilinear + mip maps with alpha masks, per-pixel fog [ref:3451]
/// SSAA. Vanilla stays bit-identical (golden hashes in <see cref="GraphicsModeTests"/>).</summary>
public class GraphicsQualityTests(ITestOutputHelper output)
{
    internal static RenderOptions Quality(bool persp = false, TextureFilter filter = TextureFilter.Nearest, bool fog = false) =>
        new() { PerspectiveCorrect = persp, Filter = filter, PixelFog = fog };

    internal static RenderOptions All => Quality(true, TextureFilter.Trilinear, true);

    static FrameBuffer Frame(string scene, RenderOptions opt, int k = 1, int s = 1)
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, k, s);
        GfxScenes.Render(scene, t, opt);
        return t.ComposeCpu();
    }

    // ---- rasteriser maths (no game data) ----

    static ScreenVertex V(double x, double y, double u, double v, double z = 1) => new(x, y, u, v) { W = 1 / z };

    /// <summary>Receding strip: near edge z=1 at y=300, far edge z=3 at y=100 (y = 300/z); v runs 0..2 linearly in z (world position).</summary>
    static FrameBuffer Strip(bool persp, TextureFilter filter = TextureFilter.Nearest)
    {
        var tex = new Texture(1, 2, [0xFF0000u, 0x0000FFu], [false, false]);   // texel row 0 red (near half), row 1 blue (far half)
        var fb = new FrameBuffer(64, 400); fb.Clear(0, 0, 0);
        var ctx = new FillContext { Perspective = persp, Filter = filter };
        // vertices: far-left, far-right, near-right, near-left; v = 2·(z−1)/2
        ReadOnlySpan<ScreenVertex> poly = [V(10, 100, 0, 2, 3), V(50, 100, 1, 2, 3), V(50, 300, 1, 0, 1), V(10, 300, 0, 0, 1)];
        EnhancedRasterizer.Fill(fb, poly, tex, 0, false, 0, 0, ctx);
        return fb;
    }

    [Fact]
    public void Perspective_PutsTheTextureEdgeAtTheWorldMidpoint_AffineDoesNot()
    {
        // v = 1 at z = 2 → y = 150; affine interpolation would put it at y = 200
        var p = Strip(persp: true); var a = Strip(persp: false);
        Assert.Equal(0x0000FFu, p.Get(30, 120));    // far side, blue
        Assert.Equal(0xFF0000u, p.Get(30, 175));    // between 150 and 200: already the near texel
        Assert.Equal(0x0000FFu, a.Get(30, 175));    // affine: still the far texel
        Assert.Equal(0xFF0000u, p.Get(30, 290));
        Assert.Equal(0xFF0000u, a.Get(30, 290));
    }

    [Fact]
    public void Perspective_ChessboardOnAGroundPlaneKeepsStraightEdges()
    {
        // 8×8 checker on a receding quad: the columns are lines through the vanishing point; every row's colour change positions follow x = cx + (x0 − cx)/z
        var tex = new Texture(8, 8, Enumerable.Range(0, 64).Select(i => ((i % 8) + (i / 8)) % 2 == 0 ? 0xFFFFFFu : 0u).ToArray(), new bool[64]);
        var fb = new FrameBuffer(200, 200); fb.Clear(128, 128, 128);
        var ctx = new FillContext { Perspective = true };
        // ground: x from −2..2, z from 1 to 5 ; screen x = 100 + 100·x/z, y = 100 + 100/z
        ScreenVertex P(double x, double z, double u, double v) => V(100 + 100 * x / z, 100 + 100 / z, u, v, z);
        EnhancedRasterizer.Fill(fb, [P(-2, 5, 0, 8), P(2, 5, 8, 8), P(2, 1, 8, 0), P(-2, 1, 0, 0)], tex, 0, false, 0, 0, ctx);
        // at row y the column boundary u = 4 (x = 0) must sit at screen x = 100 for every row
        int checked_ = 0;
        for (int y = 130; y < 195; y += 7)
        {
            int cx = -1;
            for (int x = 60; x < 140; x++) if (fb.Get(x, y) != fb.Get(x + 1, y) && Math.Abs(x - 99.5) < 3) cx = x;
            Assert.True(cx >= 97 && cx <= 101, $"row {y}: column edge at {cx}");
            checked_++;
        }
        Assert.True(checked_ > 8);
    }

    static Texture Noise(int w, int h, int seed, bool maskHoles = false)
    {
        var r = new Random(seed); var c = new uint[w * h]; var t = new bool[w * h];
        for (int i = 0; i < c.Length; i++) { c[i] = (uint)(r.Next(256) << 16 | r.Next(256) << 8 | r.Next(256)); t[i] = maskHoles && r.Next(3) == 0; }
        return new Texture(w, h, c, t);
    }

    static double Luma(FrameBuffer fb)
    {
        double s = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4) s += 0.299 * fb.Rgba[i] + 0.587 * fb.Rgba[i + 1] + 0.114 * fb.Rgba[i + 2];
        return s / (fb.Rgba.Length / 4);
    }

    [Theory, InlineData(TextureFilter.Bilinear), InlineData(TextureFilter.Trilinear)]
    public void Filters_KeepTheMeanLuminance_AndSmooth(TextureFilter filter)
    {
        var tex = Noise(64, 64, 5);
        FrameBuffer Draw(TextureFilter f)
        {
            var fb = new FrameBuffer(256, 256); fb.Clear(0, 0, 0);
            EnhancedRasterizer.Fill(fb, [V(0, 0, 0, 0), V(256, 0, 64, 0), V(256, 256, 64, 64), V(0, 256, 0, 64)], tex, 0, false, 0, 0,
                new FillContext { Perspective = true, Filter = f });
            return fb;
        }
        var nearest = Draw(TextureFilter.Nearest); var filtered = Draw(filter);
        Assert.InRange(Luma(filtered) - Luma(nearest), -1.5, 1.5);
        // 4× magnification: neighbouring pixels differ far less than in the blocky nearest image (blocks have hard 255-level steps)
        int Step(FrameBuffer fb) { int s = 0; for (int y = 0; y < 256; y++) for (int x = 1; x < 256; x++) s = Math.Max(s, Math.Abs(fb.Rgba[(y * 256 + x) * 4 + 1] - fb.Rgba[(y * 256 + x - 1) * 4 + 1])); return s; }
        Assert.True(Step(filtered) < Step(nearest) / 2, $"{Step(filtered)} vs {Step(nearest)}");
    }

    [Theory, InlineData(TextureFilter.Bilinear), InlineData(TextureFilter.Trilinear)]
    public void Mask_IsFilteredWithAlpha_NoColourBleedFromMaskedTexels(TextureFilter filter)
    {
        // opaque texels red, masked texels bright green: after filtering no output pixel may turn green, and masked areas keep the background
        int w = 32, h = 32; var c = new uint[w * h]; var m = new bool[w * h];
        for (int i = 0; i < c.Length; i++) { m[i] = (i % w / 4 + i / w / 4) % 2 == 1; c[i] = m[i] ? 0x00FF00u : 0xFF0000u; }
        var tex = new Texture(w, h, c, m);
        var fb = new FrameBuffer(128, 128); fb.Clear(10, 20, 30);
        EnhancedRasterizer.Fill(fb, [V(0, 0, 0, 0), V(128, 0, w, 0), V(128, 128, w, h), V(0, 128, 0, h)], tex, 0, true, 0, 0,
            new FillContext { Perspective = true, Filter = filter });
        int red = 0, bg = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4)
        {
            Assert.True(fb.Rgba[i + 1] <= 30 || fb.Rgba[i] < 10, "green bleed");   // background g = 20, red texels g = 0
            if (fb.Rgba[i] > 200 && fb.Rgba[i + 1] == 0) red++; else if (fb.Rgba[i] == 10) bg++;
        }
        Assert.True(red > 4000 && bg > 4000, $"red {red}, background {bg}");   // about half each; threshold keeps hard-ish edges
        Assert.InRange(red, 128 * 128 * 0.4, 128 * 128 * 0.6);
    }

    [Fact]
    public void MipChain_HasAllLevels_AveragesColour_AndCarriesAlpha()
    {
        var tex = Noise(16, 8, 1, maskHoles: true);
        var opaque = tex.Mips(false); var alpha = tex.Mips(true);
        Assert.Equal(5, opaque.Count);                       // 16×8, 8×4, 4×2, 2×1, 1×1
        Assert.Equal((1, 1), (opaque.Widths[^1], opaque.Heights[^1]));
        Assert.False(opaque.HasAlpha); Assert.True(alpha.HasAlpha);
        Assert.Same(tex.Mips(true), alpha);
        Assert.All(opaque.Levels[0], p => Assert.Equal(255u, p >> 24));
        Assert.Contains(alpha.Levels[0], p => p == 0u);
        Assert.Contains(alpha.Levels[2], p => (p >> 24) is > 0 and < 255);   // partial coverage on coarser levels
    }

    [Fact]
    public void PixelFog_FollowsTheDepthAlongAPolygon_AndConstantTableEqualsConstantLevel()
    {
        var tex = Texture.Solid(0xFFFFFF);
        // fog weight table: 0 up to bucket 20 (z=0 … 10 m with offset 20), then rising to 240 at bucket 80
        var weight = new int[1240]; var max = new int[1240]; int mx = 0;
        for (int b = 0; b < 1240; b++) { weight[b] = Math.Clamp((b - 20) * 4, 0, 240); mx = Math.Max(mx, weight[b]); max[b] = mx; }
        var fb = new FrameBuffer(64, 400); fb.Clear(0, 0, 0);
        var ctx = new FillContext { Perspective = true, FogWeight = weight, FogMaxUpTo = max, FogBucketOffset = 0 };
        ReadOnlySpan<ScreenVertex> poly = [V(10, 100, 0, 0, 30), V(50, 100, 1, 0, 30), V(50, 300, 1, 1, 1), V(10, 300, 0, 1, 1)];   // y = 3000/z? use as depth ramp
        EnhancedRasterizer.Fill(fb, poly, tex, 0xFFFFFF, false, 0, 0x000000, ctx);
        int Lum(int y) => fb.Rgba[(y * 64 + 30) * 4];
        Assert.Equal(255, Lum(299));                         // near: no fog
        Assert.True(Lum(110) < Lum(200) && Lum(200) <= Lum(299), $"{Lum(110)} {Lum(200)} {Lum(299)}");   // farther = more fog (fog colour black)
        Assert.True(Lum(101) < 150, $"far pixel {Lum(101)}");

        // constant table = polygon's constant level
        var flat = new int[1240]; Array.Fill(flat, 8 * 16);
        var a = new FrameBuffer(32, 32); a.Clear(0, 0, 0); var b2 = new FrameBuffer(32, 32); b2.Clear(0, 0, 0);
        ReadOnlySpan<ScreenVertex> sq = [V(0, 0, 0, 0, 5), V(32, 0, 1, 0, 5), V(32, 32, 1, 1, 5), V(0, 32, 0, 1, 5)];
        EnhancedRasterizer.Fill(a, sq, tex, 0xFFFFFF, false, 0, 0x000000, new FillContext { FogWeight = flat, FogMaxUpTo = flat });
        EnhancedRasterizer.Fill(b2, sq, tex, 0xFFFFFF, false, 8, 0x000000, new FillContext());
        Assert.Equal(a.Sha256(), b2.Sha256());
        Assert.InRange((int)a.Rgba[0], 126, 129);
    }

    [Fact]
    public void FogFreePolygon_TakesTheFastPath_SameAsNoFog()
    {
        var tex = Noise(8, 8, 3);
        var zero = new int[1240];
        var a = new FrameBuffer(40, 40); var b = new FrameBuffer(40, 40);
        ReadOnlySpan<ScreenVertex> sq = [V(0, 0, 0, 0, 2), V(40, 0, 8, 0, 2), V(40, 40, 8, 8, 2), V(0, 40, 0, 8, 2)];
        EnhancedRasterizer.Fill(a, sq, tex, 0, false, 0, 0xFF00FF, new FillContext { Perspective = true, FogWeight = zero, FogMaxUpTo = zero });
        EnhancedRasterizer.Fill(b, sq, tex, 0, false, 0, 0xFF00FF, new FillContext { Perspective = true });
        Assert.Equal(a.Sha256(), b.Sha256());
    }

    [Fact]
    public void Enhanced_NearestWithoutExtras_MatchesTheOriginalRasteriserOnAffineQuads()
    {
        // same coverage and the same texel for texel at 1:1 (the new filler is only a different implementation of the same span rule)
        var tex = Noise(32, 32, 9);
        var a = new FrameBuffer(64, 64); var b = new FrameBuffer(64, 64); a.Clear(1, 2, 3); b.Clear(1, 2, 3);
        ScreenVertex[] q = [V(3.3, 5.2, 0, 0), V(50.7, 8.1, 32, 0), V(47.2, 55.9, 32, 32), V(6.6, 52.4, 0, 32)];
        Rasterizer.Fill(a, q, tex, 0);
        EnhancedRasterizer.Fill(b, q, tex, 0, false, 0, 0, new FillContext());
        int diff = 0; for (int i = 0; i < a.Rgba.Length; i += 4) if (a.Rgba[i] != b.Rgba[i] || a.Rgba[i + 1] != b.Rgba[i + 1] || a.Rgba[i + 2] != b.Rgba[i + 2]) diff++;
        Assert.True(diff < 64 * 64 / 40, $"{diff} differing pixels");   // sub-texel rounding of the stepped coordinates only
    }

    // ---- SSAA, settings, notice ----

    [Theory, InlineData(GraphicsMode.Enhanced, 0, 1, 2), InlineData(GraphicsMode.Enhanced, 0, 2, 1), InlineData(GraphicsMode.Enhanced, 1, 1, 1),
        InlineData(GraphicsMode.Enhanced, 3, 1, 3), InlineData(GraphicsMode.Enhanced, 4, 2, 2), InlineData(GraphicsMode.Enhanced, 4, 4, 1),
        InlineData(GraphicsMode.Vanilla, 0, 1, 1)]
    public void Supersampling_AutoReachesRenderScale2_AndNeverExceedsTheCap(GraphicsMode mode, int setting, int k, int expected) =>
        Assert.Equal(expected, Supersampling.Resolve(mode, setting, k));

    [Fact]
    public void FrameTargets_Ssaa_RendersBigAndShowsTheBoxFilteredScene()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 1, 2);
        Assert.Equal((1280, 960), (t.SceneWidth, t.SceneHeight));
        var big = t.BeginScene();
        Assert.Equal(1280, big.Width);
        big.Clear(10, 20, 30);
        big.Set(0, 0, 0xFFFFFF);                      // one white sample out of four in the first output pixel
        Assert.Equal((640, 480), (t.Scene.Width, t.Scene.Height));
        Assert.Equal(10 + (255 - 10 + 2) / 4, t.Scene.Rgba[0]);
        Assert.Equal(10, t.Scene.Rgba[4]);
        Assert.Equal((640, 480), (t.ComposeCpu().Width, t.ComposeCpu().Height));
        t.Configure(GraphicsMode.Vanilla, 3, 3);
        Assert.Equal((1, 1), (t.Scale, t.Ssaa));
    }

    [Fact]
    public void QualitySettings_DefaultOn_ParseFromCommandLine_AndRoundTrip()
    {
        var d = GraphicsSettings.Resolve(DesktopOptions.Parse([]), null);
        Assert.True(d.Perspective); Assert.True(d.PixelFog); Assert.Equal(TextureFilter.Trilinear, d.Filter); Assert.Equal(0, d.Ssaa);
        var o = DesktopOptions.Parse(["--gfx-persp", "off", "--gfx-filter", "bilinear", "--gfx-fog", "off", "--gfx-ssaa", "3"]);
        var s = GraphicsSettings.Resolve(o, null);
        Assert.False(s.Perspective); Assert.False(s.PixelFog); Assert.Equal(TextureFilter.Bilinear, s.Filter); Assert.Equal(3, s.Ssaa);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-filter", "cubic"]));
        string path = Path.Combine(Path.GetTempPath(), $"port_{Guid.NewGuid():N}.json");
        try
        {
            var port = new PortSettings(); s.WriteTo(port); Assert.Null(port.Save(path));
            var back = GraphicsSettings.Resolve(DesktopOptions.Parse([]), PortSettings.Load(path));
            Assert.Equal(s, back);
        }
        finally { File.Delete(path); }
        var r = new RenderOptions();
        d.ApplyTo(r); Assert.True(r.PerspectiveCorrect && r.PixelFog && r.Filter == TextureFilter.Trilinear && r.EnhancedFill);
        (d with { Mode = GraphicsMode.Vanilla }).ApplyTo(r); Assert.False(r.EnhancedFill);
    }

    [Fact]
    public void Notice_ShowsForTwoSeconds_FadesAtTheEnd_AndHasTheRightText()
    {
        var n = new GfxNotice();
        Assert.False(n.Active(0));
        n.Show("GFX = ENHANCED X2", "detail", now: 10);
        Assert.True(n.Active(10)); Assert.True(n.Active(11.99)); Assert.False(n.Active(12.0)); Assert.False(n.Active(9));
        Assert.Equal(255, n.Alpha(10)); Assert.Equal(255, n.Alpha(11.5));
        Assert.InRange(n.Alpha(11.8), 100, 160); Assert.Equal(0, n.Alpha(12.5));

        var g = new GraphicsSettings(GraphicsMode.Enhanced, 0);
        Assert.Equal("GFX = ENHANCED X2", GfxNotice.ForMode(g, 1).Title);               // small window: k = 1 with SSAA 2 → rendered at x2
        Assert.Equal("GFX = ENHANCED X2", GfxNotice.ForMode(g, 2).Title);               // big window: k = 2
        Assert.Equal("GFX = VANILLA", GfxNotice.ForMode(g.Toggled(), 1).Title);
        Assert.Equal("GFX SCALE = AUTO (X1)", GfxNotice.ForScale(g, 1).Title);
        Assert.Equal("GFX SCALE = 3 (X3)", GfxNotice.ForScale(g with { Scale = 3 }, 3).Title);
        Assert.Contains("SSAA 2X", GfxNotice.ForMode(g, 1).Detail);
        Assert.Equal("GFX SSAA = AUTO (X2)", GfxNotice.ForSsaa(g, 1).Title);
        Assert.Equal("GFX SSAA = OFF", GfxNotice.ForSsaa(g.ToggledSsaa(), 1).Title);
        Assert.Equal("GFX = ENHANCED X1", GfxNotice.ForMode(g.ToggledSsaa(), 1).Title);
        Assert.Equal(0, g.ToggledSsaa().ToggledSsaa().Ssaa);

        var fb = new FrameBuffer(640, 480); fb.Clear(0, 0, 0);
        n.Draw(fb, 5); Assert.All(fb.Rgba.Where((_, i) => i % 4 < 3), b => Assert.Equal(0, b));   // not yet / no longer shown: untouched
        n.Draw(fb, 11); Assert.Contains(fb.Rgba.Where((_, i) => i % 4 < 3), b => b > 100);
        var layer = new FrameBuffer(640, 480); layer.ClearTransparent();     // Enhanced overlay: the plate is a translucent alpha, the text opaque
        n.Draw(layer, 11);
        Assert.Contains(Enumerable.Range(0, 640 * 480).Select(i => layer.Rgba[i * 4 + 3]), a => a == 255);
        Assert.Contains(Enumerable.Range(0, 640 * 480).Select(i => layer.Rgba[i * 4 + 3]), a => a is > 100 and < 255);
    }

    [GameDataFact]
    public void Notice_PngOnMenuAndRace_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var g = new GraphicsSettings(GraphicsMode.Enhanced, 0);
        foreach (string scene in new[] { "menu", "chase" })
            foreach (var mode in new[] { GraphicsMode.Enhanced, GraphicsMode.Vanilla })
            {
                var gg = g with { Mode = mode };
                var t = new FrameTargets();
                t.Configure(mode, 1, gg.EffectiveSsaa(1));
                var opt = new RenderOptions(); gg.ApplyTo(opt);
                GfxScenes.Render(scene, t, opt);
                var n = new GfxNotice();
                var (title, detail, colour) = GfxNotice.ForMode(gg, 1);
                n.Show(title, detail, 0, colour);
                n.Draw(t.Overlay, 0.5);
                t.ComposeCpu().SavePng(Path.Combine(dir, $"notice_{scene}_{mode.Name()}.png"));
            }
    }

    [GameDataTheory, InlineData("day"), InlineData("chase"), InlineData("tunnel")]
    public void AllItems_StillApproximateTheOriginalPicture_AtScale1(string scene)
    {
        var vanilla = Frame(scene, new RenderOptions());
        var all = Compositor.Downsample(Frame(scene, All, 2), 2);
        double sum = 0;
        for (int i = 0; i < vanilla.Rgba.Length; i += 4)
            for (int c = 0; c < 3; c++) { double d = vanilla.Rgba[i + c] - all.Rgba[i + c]; sum += d * d; }
        double psnr = 10 * Math.Log10(255.0 * 255 / (sum / (3.0 * 640 * 480)));
        output.WriteLine($"{scene}: PSNR {psnr:F1} dB");
        Assert.True(psnr > 19, $"{scene}: {psnr:F1} dB");   // the same picture, only filtered (not a different scene)
        Assert.InRange(Luma(all) - Luma(vanilla), -6, 6);
    }

    // ---- previews and timings (only with NFS_GFX_CHECK_DIR) ----

    [GameDataFact]
    public void Report_WritesQualityVariants_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var variants = new (string Name, RenderOptions Opt, int K, int S)[]
        {
            ("persp", Quality(persp: true), 2, 1),
            ("bilinear", Quality(filter: TextureFilter.Bilinear), 2, 1),
            ("mip", Quality(filter: TextureFilter.Trilinear), 2, 1),
            ("fog", Quality(fog: true), 2, 1),
            ("all_k2", All, 2, 1),
            ("all_k1ssaa2", All, 1, 2),
            ("ssaa_only_k1", Quality(), 1, 2),
        };
        foreach (string scene in GfxScenes.Names)
        {
            var vanilla = Compositor.UpscaleNearest(Frame(scene, new RenderOptions()), 2);
            vanilla.SavePng(Path.Combine(dir, $"q_{scene}_vanilla.png"));
            foreach (var v in variants)
            {
                var fb = Frame(scene, v.Opt, v.K, v.S);
                if (fb.Width < 1280) fb = Compositor.UpscaleNearest(fb, 1280 / fb.Width);
                fb.SavePng(Path.Combine(dir, $"q_{scene}_{v.Name}.png"));
                if (v.Name is "all_k2" or "all_k1ssaa2")
                {
                    var pair = new FrameBuffer(vanilla.Width * 2 + 8, vanilla.Height); pair.Clear(255, 0, 255);
                    for (int y = 0; y < vanilla.Height; y++)
                    {
                        Buffer.BlockCopy(vanilla.Rgba, y * vanilla.Width * 4, pair.Rgba, y * pair.Width * 4, vanilla.Width * 4);
                        Buffer.BlockCopy(fb.Rgba, y * fb.Width * 4, pair.Rgba, (y * pair.Width + vanilla.Width + 8) * 4, fb.Width * 4);
                    }
                    pair.SavePng(Path.Combine(dir, $"pair2_{scene}_{v.Name}.png"));
                }
            }
        }
    }

    /// <summary>Render time of a frame per feature (median of 15). [ref:3452]</summary>
    [GameDataFact]
    public void Timing_QualityItems_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        var configs = new (string Name, RenderOptions Opt, int K, int S)[]
        {
            ("vanilla k1", new RenderOptions(), 1, 1),
            ("persp k1", Quality(persp: true), 1, 1),
            ("bilinear k1", Quality(filter: TextureFilter.Bilinear), 1, 1),
            ("mip k1", Quality(filter: TextureFilter.Trilinear), 1, 1),
            ("fog k1", Quality(fog: true), 1, 1),
            ("all k1", All, 1, 1),
            ("plain k2", new RenderOptions(), 2, 1),
            ("all k2", All, 2, 1),
            ("all k1 ssaa2", All, 1, 2),
            ("all k2 ssaa2 (4x)", All, 2, 2),
        };
        foreach (string scene in new[] { "day", "chase", "tunnel", "rain" })
            foreach (var c in configs)
            {
                var draw = GfxScenes.Prepare(scene, c.Opt);
                var t = new FrameTargets();
                t.Configure(c.Name.StartsWith("vanilla") ? GraphicsMode.Vanilla : GraphicsMode.Enhanced, c.K, c.S);
                for (int i = 0; i < 3; i++) { draw(t); _ = t.Scene; }
                var times = new List<double>();
                for (int i = 0; i < 15; i++)
                {
                    var sw = Stopwatch.StartNew();
                    draw(t);
                    _ = t.Scene;   // SSAA resolve
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                lines.Add($"{scene,-7} {c.Name,-18}: median {times[times.Count / 2]:F1} ms, min {times[0]:F1} ms");
            }
        File.WriteAllLines(Path.Combine(dir, "timing_quality.txt"), lines);
        foreach (var l in lines) output.WriteLine(l);
    }
}
