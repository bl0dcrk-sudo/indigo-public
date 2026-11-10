using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Task 08i point 1: the display filters on reusable buffers (<see cref="DisplayFilterRunner"/>, <see cref="Compositor.ComposeInto"/>) give exactly
/// the pixels of the old per-call implementation (frozen in <see cref="DisplayFiltersOld"/>), on noise, edges and odd sizes, with any thread count, and
/// allocate nothing per frame once the sizes repeat.</summary>
public class DisplayFilterRunnerTests
{
    /// <summary>Noise with flat patches and hard edges (extreme values included) so that every branch of EASU / RCAS clamps.</summary>
    static FrameBuffer Image(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var f = new FrameBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                int mode = (x / 5 + y / 7 + seed) % 4;
                for (int c = 0; c < 3; c++)
                    f.Rgba[o + c] = mode switch
                    {
                        0 => (byte)rng.Next(256),
                        1 => (byte)((x * 255 / Math.Max(1, w - 1) + c * 40) & 255),
                        2 => (byte)(rng.Next(2) * 255),
                        _ => (byte)(90 + c),
                    };
                f.Rgba[o + 3] = (byte)rng.Next(256);   // the filters must ignore (and overwrite) the source alpha
            }
        return f;
    }

    public static IEnumerable<object[]> Cases() =>
    [
        [64, 48, 192, 144], [37, 23, 101, 59], [16, 12, 70, 50], [9, 5, 9, 5], [40, 30, 20, 15], [1, 1, 7, 3], [200, 120, 640, 360],
    ];

    static void AssertSame(FrameBuffer expected, FrameBuffer actual, string what)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.True(expected.Rgba.AsSpan().SequenceEqual(actual.Rgba), what + ": pixels differ from the old implementation");
    }

    [Theory, MemberData(nameof(Cases))]
    public void AllFilters_AreBitIdenticalToTheOldImplementation(int sw, int sh, int dw, int dh)
    {
        var src = Image(sw, sh, sw + sh);
        var runner = new DisplayFilterRunner();
        AssertSame(DisplayFiltersOld.Bilinear(src, dw, dh), runner.Bilinear(src, dw, dh), "bilinear");
        AssertSame(DisplayFiltersOld.Easu(src, dw, dh), runner.Easu(src, dw, dh), "easu");
        AssertSame(DisplayFiltersOld.Rcas(src), runner.Rcas(src), "rcas");
        AssertSame(DisplayFiltersOld.Rcas(src, 0f), runner.Rcas(src, 0f), "rcas sharpness 0");
        AssertSame(DisplayFiltersOld.Rcas(src, 2f), runner.Rcas(src, 2f), "rcas sharpness 2");
        AssertSame(DisplayFiltersOld.Crt(src, dw, dh), runner.Crt(src, dw, dh), "crt");
        AssertSame(DisplayFiltersOld.Crt(src, dw, dh, 0.7f, 0.5f), runner.Crt(src, dw, dh, 0.7f, 0.5f), "crt custom");
        foreach (DisplayFilterKind kind in Enum.GetValues<DisplayFilterKind>())
        {
            AssertSame(DisplayFiltersOld.Apply(kind, src, dw, dh), runner.Apply(kind, src, dw, dh), "apply " + kind);
            AssertSame(DisplayFiltersOld.Apply(kind, src, dw, dh), DisplayFilters.Apply(kind, src, dw, dh), "static apply " + kind);
        }
    }

    [Fact]
    public void FullHd_FsrAndCrt_MatchTheOldImplementation_AnyThreadCount()
    {
        var src = Image(640, 480, 5);
        var fsrOld = DisplayFiltersOld.Apply(DisplayFilterKind.Fsr, src, 1920, 1080);
        var crtOld = DisplayFiltersOld.Apply(DisplayFilterKind.Crt, src, 1920, 1080);
        foreach (int threads in new[] { 0, 1, 3 })
        {
            var runner = new DisplayFilterRunner { MaxThreads = threads };
            for (int rep = 0; rep < 2; rep++)   // the second round runs on the reused buffers
            {
                AssertSame(fsrOld, runner.Apply(DisplayFilterKind.Fsr, src, 1920, 1080), $"fsr, threads {threads}");
                AssertSame(crtOld, runner.Apply(DisplayFilterKind.Crt, src, 1920, 1080), $"crt, threads {threads}");
            }
        }
    }

    [Fact]
    public void Runner_ReusesItsBuffers_AndChangesSizeCleanly()
    {
        var runner = new DisplayFilterRunner();
        var a = Image(32, 24, 1); var b = Image(32, 24, 2);
        var r1 = runner.Apply(DisplayFilterKind.Fsr, a, 96, 72);
        var r2 = runner.Apply(DisplayFilterKind.Fsr, b, 96, 72);
        Assert.Same(r1, r2);                                         // same buffer, new content
        AssertSame(DisplayFiltersOld.Apply(DisplayFilterKind.Fsr, b, 96, 72), r2, "second frame");
        var r3 = runner.Apply(DisplayFilterKind.Fsr, a, 64, 48);     // other output size -> reallocated, still correct
        AssertSame(DisplayFiltersOld.Apply(DisplayFilterKind.Fsr, a, 64, 48), r3, "resized");
        // a filter fed with the result of another one (output must not alias the input)
        var chained = runner.Rcas(runner.Bilinear(a, 64, 48));
        AssertSame(DisplayFiltersOld.Rcas(DisplayFiltersOld.Bilinear(a, 64, 48)), chained, "rcas(bilinear)");
    }

    [Fact]
    public void Runner_AllocatesNothingPerFrame()
    {
        var src = Image(320, 240, 3);
        var runner = new DisplayFilterRunner();
        foreach (var kind in Enum.GetValues<DisplayFilterKind>())
            for (int i = 0; i < 3; i++) runner.Apply(kind, src, 960, 720);   // warm up: buffers, work items, JIT
        foreach (var kind in Enum.GetValues<DisplayFilterKind>())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++) runner.Apply(kind, src, 960, 720);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256, $"{kind} allocates per frame");
        }
    }

    // ---- Compositor ----

    static FrameBuffer Overlay(int w, int h, int seed, bool translucentScene = false)
    {
        var rng = new Random(seed);
        var f = new FrameBuffer(w, h);
        for (int i = 0; i < w * h; i++)
        {
            int r = rng.Next(10);
            byte a = r < 4 ? (byte)0 : r < 7 ? (byte)255 : (byte)rng.Next(1, 255);
            f.Rgba[i * 4] = (byte)rng.Next(256); f.Rgba[i * 4 + 1] = (byte)rng.Next(256); f.Rgba[i * 4 + 2] = (byte)rng.Next(256); f.Rgba[i * 4 + 3] = a;
        }
        return f;
    }

    [Theory]
    [InlineData(1, false), InlineData(2, false), InlineData(3, false), InlineData(4, false), InlineData(2, true), InlineData(3, true)]
    public void Compose_IsBitIdenticalToTheOldByteWiseOne(int k, bool translucentScene)
    {
        var overlay = Overlay(40, 30, k);
        var scene = Image(40 * k, 30 * k, k + 10);
        if (!translucentScene) for (int i = 3; i < scene.Rgba.Length; i += 4) scene.Rgba[i] = 255;
        var expected = CompositorOld.Compose(scene, overlay);
        AssertSame(expected, Compositor.Compose(scene, overlay), "Compose");
        var dst = new FrameBuffer(scene.Width, scene.Height);
        for (int rep = 0; rep < 2; rep++) { Compositor.ComposeInto(scene, overlay, dst); AssertSame(expected, dst, "ComposeInto"); }
    }

    [Theory, InlineData(1), InlineData(2), InlineData(3), InlineData(5)]
    public void UpscaleNearest_IsBitIdenticalToTheOldOne(int k)
    {
        var src = Image(23, 17, k);
        var expected = CompositorOld.UpscaleNearest(src, k);
        AssertSame(expected, Compositor.UpscaleNearest(src, k), "UpscaleNearest");
        var dst = new FrameBuffer(src.Width * k, src.Height * k);
        Compositor.UpscaleNearestInto(src, dst, k);
        AssertSame(expected, dst, "UpscaleNearestInto");
    }

    [Fact]
    public void ComposeInto_And_UpscaleNearestInto_AllocateNothing()
    {
        var overlay = Overlay(160, 120, 1); var scene = Image(480, 360, 2);
        var dst = new FrameBuffer(480, 360); var up = new FrameBuffer(480, 360);
        Compositor.ComposeInto(scene, overlay, dst); Compositor.UpscaleNearestInto(overlay, up, 3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4; i++) { Compositor.ComposeInto(scene, overlay, dst); Compositor.UpscaleNearestInto(overlay, up, 3); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256);
    }

    [Fact]
    public void ComposeInto_RejectsAliasedOrWrongSizedTarget()
    {
        var overlay = Overlay(8, 6, 1); var scene = Image(16, 12, 2);
        Assert.Throws<ArgumentException>(() => Compositor.ComposeInto(scene, overlay, scene));
        Assert.Throws<ArgumentException>(() => Compositor.ComposeInto(scene, overlay, new FrameBuffer(8, 6)));
    }

    // ---- FillRecorder: per-polygon work done once (task 08i point 3) ----

    static ScreenVertex[] RandomConvex(Random rng, int w, int h)
    {
        int n = rng.Next(3, 8);
        double cx = rng.NextDouble() * w, cy = rng.NextDouble() * h, rx = 5 + rng.NextDouble() * w / 2, ry = 5 + rng.NextDouble() * h / 2;
        var v = new ScreenVertex[n];
        for (int i = 0; i < n; i++)
        {
            double a = (i + rng.NextDouble() * 0.6) * 2 * Math.PI / n;
            double wv = 0.5 + rng.NextDouble() * 1.5;
            v[i] = new ScreenVertex(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry, rng.NextDouble() * 6 - 1, rng.NextDouble() * 6 - 1) { W = wv, L = rng.Next(3) == 0 ? 1 : 0.4 + rng.NextDouble() * 1.4 };
        }
        return v;
    }

    [Theory]
    [InlineData(false, TextureFilter.Nearest, false), InlineData(true, TextureFilter.Trilinear, true), InlineData(true, TextureFilter.Bilinear, false), InlineData(false, TextureFilter.Trilinear, true)]
    public void RecordedPolygons_GiveTheSamePixelsAsDirectFills_ForAnyStripLayout(bool persp, TextureFilter filter, bool shade)
    {
        const int W = 160, H = 130;
        var rng = new Random(77);
        var tex = new Texture(32, 32, Enumerable.Range(0, 1024).Select(i => (uint)(((i * 2654435761u) >> 8) & 0xFFFFFF)).ToArray(), Enumerable.Range(0, 1024).Select(i => i % 7 == 0).ToArray());
        var fog = Enumerable.Range(0, 128).Select(i => Math.Min(256, i * 3)).ToArray();
        var fogMax = new int[128]; for (int i = 0, m = 0; i < 128; i++) { m = Math.Max(m, fog[i]); fogMax[i] = m; }
        var ctx = new FillContext { Perspective = persp, Filter = filter, LodBias = 0.25, FogWeight = fog, FogMaxUpTo = fogMax, Shade = shade, FogBucketOffset = 3 };
        var polys = Enumerable.Range(0, 80).Select(_ => RandomConvex(rng, W, H)).ToList();

        var direct = new FrameBuffer(W, H); direct.Clear(10, 20, 30);
        foreach (var (p, i) in polys.Select((p, i) => (p, i)))
            EnhancedRasterizer.Fill(direct, p, i % 3 == 0 ? null : tex, 0x336699, i % 5 == 0, i % 4, 0xA0B0C0, ctx);

        int oldThreads = FillRecorder.MaxThreads, oldStrips = FillRecorder.StripsPerThread;
        try
        {
            foreach (var (threads, perThread) in new[] { (1, 4), (3, 4), (4, 1), (2, 13) })
            {
                FillRecorder.MaxThreads = threads; FillRecorder.StripsPerThread = perThread;
                var fb = new FrameBuffer(W, H); fb.Clear(10, 20, 30);
                var rec = new FillRecorder();
                rec.Begin(fb, ctx);
                foreach (var (p, i) in polys.Select((p, i) => (p, i)))
                    rec.AddFill(p, i % 3 == 0 ? null : tex, 0x336699, i % 5 == 0, i % 4, 0xA0B0C0, ctx);
                rec.Flush();
                Assert.True(direct.Rgba.AsSpan().SequenceEqual(fb.Rgba), $"recorded fill differs (threads {threads}, strips/thread {perThread})");
            }
        }
        finally { FillRecorder.MaxThreads = oldThreads; FillRecorder.StripsPerThread = oldStrips; }
    }

    /// <summary>The real thing: a rendered Enhanced frame, filtered to Full HD by the old and by the new implementation.</summary>
    [GameDataFact]
    public void RenderedFrame_FsrAndCrt_AreBitIdenticalToTheOldImplementation()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 2);
        GfxScenes.Render(GfxScenes.Names.First(), t);
        var frame = t.ComposeCpu();
        var runner = new DisplayFilterRunner();
        foreach (var kind in new[] { DisplayFilterKind.Fsr, DisplayFilterKind.Crt, DisplayFilterKind.Sharp })
            AssertSame(DisplayFiltersOld.Apply(kind, frame, 1920, 1080), runner.Apply(kind, frame, 1920, 1080), kind.ToString());
    }

    /// <summary>The host path (Game1.PresentFiltered): the reused composition equals <see cref="FrameTargets.ComposeCpu"/> and keeps its buffer.</summary>
    [GameDataFact]
    public void ComposeCpuReused_EqualsComposeCpu_AndReusesTheBuffer()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Enhanced, 2);
        GfxScenes.Render(GfxScenes.Names.First(), t);
        var a = t.ComposeCpuReused();
        AssertSame(t.ComposeCpu(), a, "scene");
        Assert.Same(a, t.ComposeCpuReused());
        t.BeginMenu();
        AssertSame(t.ComposeCpu(), t.ComposeCpuReused(), "menu");
    }
}
