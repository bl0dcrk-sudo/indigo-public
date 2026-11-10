using System.Diagnostics;
using NFS.Desktop;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Graphics modes Enhanced / Vanilla (stage 1): Vanilla = image bit-identical to the earlier port [ref:3450]
/// (golden sha256 taken BEFORE the changes), Enhanced = 3D scene at k× + 2D layer 640×480 ×k. No window – only RGBA buffers.</summary>
public class GraphicsModeTests(ITestOutputHelper output)
{
    /// <summary>sha256 of the 640×480 buffer taken from the port before stage 1 (one frame of each scene from <see cref="GfxScenes"/>).
    /// 2026-10-20: day/tunnel/cockpit/split/rain refreshed after adding the lane lines; car/chase/menu unchanged. [ref:2749]
    /// 2026-10-21: car/chase/cockpit/split/rain refreshed – the car model drawn around CarObj.Pos as in the original (RE), day/tunnel/menu unchanged. [ref:2750]</summary>
    static readonly Dictionary<string, string> VanillaGolden = new()
    {
        ["day"] = "9c49c3373ff79f4fdf38e754afd89714078b399b868845216cb4ad4ff27b90cf",
        ["tunnel"] = "f0c8c74924b7915bfceb4d7fea3e1a9a612bb46310c595bf54c0dad183557e8f",
        ["car"] = "47b3f21cc58cbecd8ca041245c8de1d6741837a89ffa4e2f175f402876356a77",
        ["chase"] = "f7513b500e9f85013be780c8d4b65b5efbd61edb164596055e3dff6771a38d72",
        ["cockpit"] = "ece384fa7ec831ea87911a210cc6afa70e7787631360b5306ee009b213f17c2d",
        ["split"] = "0cbd6b84292fab369f7752764e33226f7c7ac05c8173b3d1511d3afa3f004d63",
        ["rain"] = "87de7d17e1bafa88060edc8c7655a9dc9197e2d599533546c93ea685a52ff948",
        ["menu"] = "057965082bf7e0309fe7db8a41087b1a3d7092619d7c14103fafdb92b2c63a20",
    };

    static FrameBuffer Frame(string scene, GraphicsMode mode, int k)
    {
        var t = new FrameTargets();
        t.Configure(mode, k);
        GfxScenes.Render(scene, t);
        return t.ComposeCpu();
    }

    static FrameTargets Targets(string scene, GraphicsMode mode, int k)
    {
        var t = new FrameTargets();
        t.Configure(mode, k);
        GfxScenes.Render(scene, t);
        return t;
    }

    [Fact]
    public void GoldenTableCoversEveryScene() => Assert.Equal(GfxScenes.Names.Order(), VanillaGolden.Keys.Order());

    [GameDataTheory, MemberData(nameof(SceneNames))]
    public void Vanilla_IsBitIdenticalToThePortBeforeTheGraphicsModes(string scene)
    {
        // k = 3 requested: Vanilla ignores the internal scale
        var fb = Frame(scene, GraphicsMode.Vanilla, 3);
        Assert.Equal((640, 480), (fb.Width, fb.Height));
        Assert.Equal(VanillaGolden[scene], fb.Sha256());
    }

    public static IEnumerable<object[]> SceneNames() => GfxScenes.Names.Select(n => new object[] { n });

    [GameDataTheory, MemberData(nameof(SceneNames))]
    public void Enhanced_RendersTheSceneAtKTimesTheSize_AndTheOverlayAtOriginalSize(string scene)
    {
        foreach (int k in new[] { 1, 2 })
        {
            var t = Targets(scene, GraphicsMode.Enhanced, k);
            var composed = t.ComposeCpu();
            Assert.Equal((640 * k, 480 * k), (composed.Width, composed.Height));
            Assert.Equal((640, 480), (t.Overlay.Width, t.Overlay.Height));
            Assert.Equal(scene != "menu", t.HasScene);
            if (t.HasScene) Assert.Equal((640 * k, 480 * k), (t.Scene.Width, t.Scene.Height));
            Assert.All(composed.Rgba.Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));   // the finished image is opaque
        }
    }

    [GameDataTheory, MemberData(nameof(SceneNames))]
    public void Enhanced_Scale1_IsTheSameImageAsVanilla(string scene)
    {
        // k = 1 + layers: the same image as one buffer, except for the pixels of semi-transparent text (Blend on the scene vs on the layer) – here: exactly
        // the same pixels for scenes without semi-transparent 2D, and in the others a negligible mean error
        var vanilla = Frame(scene, GraphicsMode.Vanilla, 1);
        var enhanced = Frame(scene, GraphicsMode.Enhanced, 1);
        double mae = MeanAbsError(vanilla, enhanced);
        output.WriteLine($"{scene}: MAE {mae:F4}");
        Assert.True(mae < 0.35, $"{scene}: MAE {mae}");
    }

    [GameDataTheory, MemberData(nameof(SceneNames))]
    public void Overlay2D_StaysAtTheSamePositions_TimesK(string scene)
    {
        if (scene is "menu" or "day" or "tunnel" or "car") return;   // no 2D layer over the scene
        var vanilla = Frame(scene, GraphicsMode.Vanilla, 1);
        const int k = 2;
        var t = Targets(scene, GraphicsMode.Enhanced, k);
        var composed = t.ComposeCpu();
        int opaque = 0, differing = 0;
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
            {
                int a = t.Overlay.Rgba[(y * 640 + x) * 4 + 3];
                if (a == 0) continue;
                // 2D pixel (alpha 255) = a k×k block of the composed image, the same as in Vanilla (HUD/cockpit do not depend on the scene)
                if (a == 255)
                {
                    opaque++;
                    for (int j = 0; j < k; j++)
                        for (int i = 0; i < k; i++)
                            Assert.Equal(t.Overlay.Get(x, y), composed.Get(x * k + i, y * k + j));
                    if (t.Overlay.Get(x, y) != vanilla.Get(x, y)) differing++;
                }
            }
        output.WriteLine($"{scene}: opaque overlay pixels {opaque}, differing from Vanilla {differing}");
        Assert.True(opaque > 1000, $"{scene}: overlay is empty ({opaque})");
        // the 2D layer draws the same pixels regardless of the scene below (a fraction is allowed: blended text edges)
        Assert.True(differing < opaque / 50, $"{scene}: {differing} of {opaque} overlay pixels differ from Vanilla");
    }

    [GameDataTheory, MemberData(nameof(SceneNames))]
    public void Enhanced_Downsample2xTo1x_ApproximatesTheOriginalFrame(string scene)
    {
        if (scene == "menu") return;
        var vanilla = Frame(scene, GraphicsMode.Vanilla, 1);
        var down = Compositor.Downsample(Frame(scene, GraphicsMode.Enhanced, 2), 2);
        double mae = MeanAbsError(vanilla, down), psnr = Psnr(vanilla, down);
        output.WriteLine($"{scene}: MAE {mae:F2}, PSNR {psnr:F1} dB");
        Assert.True(psnr > 24, $"{scene}: PSNR {psnr:F1} dB");
        Assert.True(mae < 8, $"{scene}: MAE {mae:F2}");
    }

    [GameDataTheory, InlineData("day"), InlineData("tunnel"), InlineData("chase")]
    public void Enhanced_HasNoHolesBetweenPolygons(string scene)
    {
        // Sky=false → black background; in the lower half of the image (road, grass, tunnel wall) black pixels may come only from textures – the count does not grow with k
        static int Black(FrameBuffer fb)
        {
            int n = 0;
            for (int y = fb.Height * 3 / 5; y < fb.Height; y++)
                for (int x = 0; x < fb.Width; x++) if (fb.Get(x, y) == 0) n++;
            return n;
        }
        var counts = new List<double>();
        foreach (int k in new[] { 1, 2 })
        {
            var t = new FrameTargets();
            t.Configure(GraphicsMode.Enhanced, k);
            var opt = new RenderOptions { Sky = false };
            if (scene == "chase")
            {
                var d = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
                d.Frame(1.0 / 32, new DriveKeys(true, false, false, false, false, false, false));
                d.Render(t.BeginScene(), t.Overlay, opt);
            }
            else
            {
                var tr = new TrackRenderer(2);
                int slice = 120;
                tr.Render(t.BeginScene(), tr.CameraAtSlice(slice, width: t.SceneWidth, screenHeight: t.SceneHeight), opt);
            }
            counts.Add(Black(t.Scene) / (double)(k * k));
        }
        output.WriteLine($"{scene}: black pixels (per 1x) k1 {counts[0]:F0}, k2 {counts[1]:F0}");
        Assert.True(counts[1] <= counts[0] * 1.5 + 200, $"{scene}: holes {counts[1]:F0} vs {counts[0]:F0}");
    }

    [Fact]
    public void Camera_KeepsTheSameAnglesAtEveryScale()
    {
        var a = new Camera(640, 480); var b = new Camera(1280, 960);
        var v = new System.Numerics.Vector3(3, 1, 20);
        var (x1, y1) = a.Project(v); var (x2, y2) = b.Project(v);
        Assert.Equal(x1 * 2, x2, 6);
        Assert.Equal(y1 * 2, y2, 6);
        var (sx, sy) = SplitLayout.CreateCamera().Project(v); var (sx2, sy2) = SplitLayout.CreateCamera(2).Project(v);
        Assert.Equal(sx * 2, sx2, 6);
        Assert.Equal(sy * 2, sy2, 6);
    }

    // ---- settings and arguments ----

    [Fact]
    public void GfxArgument_Parses_AndDefaultsToNothing()
    {
        Assert.Null(DesktopOptions.Parse([]).Gfx);
        Assert.Equal(GraphicsMode.Vanilla, DesktopOptions.Parse(["--gfx", "vanilla"]).Gfx);
        Assert.Equal(GraphicsMode.Enhanced, DesktopOptions.Parse(["--gfx", "Enhanced"]).Gfx);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx", "ultra"]));
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx"]));
        Assert.Equal(0, DesktopOptions.Parse(["--gfx-scale", "auto"]).GfxScale);
        Assert.Equal(3, DesktopOptions.Parse(["--gfx-scale", "3"]).GfxScale);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-scale", "5"]));
    }

    [Fact]
    public void Settings_DefaultToEnhanced_WhenTheFileHasNoField()
    {
        Assert.Equal(GraphicsMode.Enhanced, GraphicsModes.Default);
        Assert.Equal(GraphicsMode.Enhanced, new PortSettings().Graphics);
        string path = Path.Combine(Path.GetTempPath(), $"port_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"fullScreen\": false }\n");   // old file without the graphics field
            var old = PortSettings.Load(path);
            var s = GraphicsSettings.Resolve(DesktopOptions.Parse([]), old);
            Assert.Equal(new GraphicsSettings(GraphicsMode.Enhanced, 0), s);
            File.WriteAllText(path, "{ \"graphicsMode\": \"banana\" }\n");   // unknown value = default
            Assert.Equal(GraphicsMode.Enhanced, PortSettings.Load(path).Graphics);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_RoundTripThroughPortJson_AndCommandLineWins()
    {
        string path = Path.Combine(Path.GetTempPath(), $"port_{Guid.NewGuid():N}.json");
        try
        {
            var port = new PortSettings { FullScreen = true };
            new GraphicsSettings(GraphicsMode.Vanilla, 3).WriteTo(port);
            Assert.Null(port.Save(path));
            var back = PortSettings.Load(path);
            Assert.True(back.FullScreen);
            Assert.Equal(new GraphicsSettings(GraphicsMode.Vanilla, 3), GraphicsSettings.Resolve(DesktopOptions.Parse([]), back));
            Assert.Equal(new GraphicsSettings(GraphicsMode.Enhanced, 3),
                GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx", "enhanced"]), back));
            Assert.Equal(new GraphicsSettings(GraphicsMode.Vanilla, 2),
                GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx-scale", "2"]), back));
            Assert.Contains("\"graphicsMode\": \"vanilla\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void F8Toggles_AndShiftF8CyclesTheScale()
    {
        var s = new GraphicsSettings(GraphicsMode.Enhanced, 0);
        Assert.Equal(GraphicsMode.Vanilla, s.Toggled().Mode);
        Assert.Equal(GraphicsMode.Enhanced, s.Toggled().Toggled().Mode);
        var seen = new List<int>();
        for (int i = 0; i < 5; i++) { seen.Add(s.Scale); s = s.NextScale(); }
        Assert.Equal([0, 1, 2, 3, 4], seen);
        Assert.Equal(0, s.Scale);
    }

    [Theory]
    [InlineData(640, 480, 1)]
    [InlineData(1279, 959, 1)]
    [InlineData(1280, 960, 2)]
    [InlineData(1440, 1080, 2)]
    [InlineData(2560, 1920, 2)]   // auto capped to AutoCap
    [InlineData(100, 100, 1)]
    public void AutoScale_IsTheLargestWholeMultipleThatFitsTheImageArea(int w, int h, int expected) =>
        Assert.Equal(expected, InternalScale.Auto(w, h, InternalScale.AutoCap));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(20, 2)]
    [InlineData(31, 2)]
    [InlineData(32, 3)]
    [InlineData(64, 3)]
    public void AutoCapFor_GrowsWithTheCoreCount_NeverReachesFour(int processors, int expected)
    {
        Assert.Equal(expected, InternalScale.AutoCapFor(processors));
        Assert.True(InternalScale.AutoCapFor(processors) < InternalScale.Max);
    }

    [Fact]
    public void Auto_UsesTheCapOfThisMachine_AndAHigherCapAllowsThree()
    {
        int cap = InternalScale.AutoCapFor(Environment.ProcessorCount);
        Assert.Equal(cap, InternalScale.Auto(3840, 2880));
        Assert.Equal(2, InternalScale.Auto(3840, 2880, InternalScale.AutoCapFor(20)));
        Assert.Equal(3, InternalScale.Auto(3840, 2880, InternalScale.AutoCapFor(32)));
        Assert.Equal(2, InternalScale.Auto(1920, 1439, InternalScale.AutoCapFor(32)));   // 3x needs 1920x1440
    }

    [Fact]
    public void AutoSupersampling_StaysTwoTimesAtCapThree()
    {
        int k = InternalScale.Auto(1920, 1440, InternalScale.AutoCapFor(32));
        Assert.Equal(3, k);
        Assert.Equal(1, Supersampling.Resolve(GraphicsMode.Enhanced, 0, k));   // k*s = 3, no extra SSAA
    }

    [Fact]
    public void ResolveScale_VanillaIsAlwaysOne_ExplicitScaleWins()
    {
        Assert.Equal(1, InternalScale.Resolve(GraphicsMode.Vanilla, 4, 2560, 1920));
        Assert.Equal(4, InternalScale.Resolve(GraphicsMode.Enhanced, 4, 640, 480));
        Assert.Equal(4, InternalScale.Resolve(GraphicsMode.Enhanced, 9, 640, 480));   // clamped to 1..4
        Assert.Equal(3, InternalScale.Auto(1920, 1440, cap: 3));
    }

    [Theory]
    [InlineData(1280, 1280, 1)]
    [InlineData(1280, 1440, 2)]
    [InlineData(640, 1440, 3)]
    [InlineData(640, 100, 1)]
    [InlineData(640, 100000, 8)]    // MaxIntermediateWidth 5120 / 640
    [InlineData(2560, 100000, 2)]
    public void DisplayFilter_UsesTheSmallestWholeUpscaleCoveringTheTarget(int src, int dst, int expected) =>
        Assert.Equal(expected, DisplayFilter.IntegerFactor(src, dst));

    // ---- layers and composition ----

    [Fact]
    public void Compositor_UpscaleAndDownsample_AreInverse_AndComposeKeepsTheMask()
    {
        var src = new FrameBuffer(4, 3);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 4; x++) src.Set(x, y, (uint)((x * 60) << 16 | (y * 100) << 8 | 7));
        var up = Compositor.UpscaleNearest(src, 3);
        Assert.Equal((12, 9), (up.Width, up.Height));
        Assert.Equal(src.Get(2, 1), up.Get(8, 4));
        Assert.Equal(src.Sha256(), Compositor.Downsample(up, 3).Sha256());

        var scene = new FrameBuffer(8, 6); scene.Clear(10, 20, 30);
        var overlay = new FrameBuffer(4, 3); overlay.ClearTransparent();
        overlay.Set(1, 1, 0xFF0000);
        overlay.Blend(2, 2, 0x00FF00, 128);
        var c = Compositor.Compose(scene, overlay);
        Assert.Equal(0xFF0000u, c.Get(2, 2)); Assert.Equal(0xFF0000u, c.Get(3, 3));   // 2×2 block
        Assert.Equal(0x0A141Eu, c.Get(0, 0));                                            // alpha 0 → scene
        Assert.Equal(0x0A141Eu, c.Get(3, 2 * 1 - 1 + 0));                                // (3,1) lies in block (1,0): scene
        uint mix = c.Get(4, 4);
        Assert.InRange((int)(mix >> 8 & 255), 120, 140);                                 // half green + half scene
        Assert.Equal(c.Get(4, 4), c.Get(5, 5));
    }

    [Fact]
    public void Blend_OverTransparentPixelKeepsTheColour_OverOpaqueStaysOpaque()
    {
        var fb = new FrameBuffer(2, 1); fb.ClearTransparent();
        fb.Blend(0, 0, 0xFFFFFF, 100);
        Assert.Equal(100, fb.Rgba[3]);
        Assert.Equal(255, fb.Rgba[0]);                         // the source colour, not darkened by the black background
        fb.Blend(0, 0, 0xFFFFFF, 0);
        Assert.Equal(100, fb.Rgba[3]);
        var op = new FrameBuffer(2, 1); op.Clear(0, 0, 0);
        op.Blend(1, 0, 0xFFFFFF, 100);
        Assert.Equal(255, op.Rgba[7]);
        Assert.InRange((int)op.Rgba[4], 99, 101);
    }

    [Fact]
    public void FrameTargets_VanillaSharesOneBuffer_EnhancedSeparatesTheLayers()
    {
        var t = new FrameTargets();
        t.Configure(GraphicsMode.Vanilla, 3);
        Assert.Same(t.Overlay, t.BeginScene());
        Assert.Equal((640, 480), (t.SceneWidth, t.SceneHeight));
        t.Configure(GraphicsMode.Enhanced, 3);
        Assert.Equal((1920, 1440), (t.SceneWidth, t.SceneHeight));
        var scene = t.BeginScene();
        Assert.NotSame(scene, t.Overlay);
        Assert.Equal(1920, scene.Width);
        Assert.All(t.Overlay.Rgba, b => Assert.Equal(0, b));   // transparent 2D layer
        t.Overlay.Set(5, 5, 0x123456);
        var menu = t.BeginMenu();                               // after a frame with the menu scene it starts from clear, opaque black
        Assert.Same(t.Overlay, menu);
        Assert.False(t.HasScene);
        Assert.Equal(255, menu.Rgba[3]);
        Assert.Equal(0u, menu.Get(5, 5));
    }

    // ---- measurements and preview (not in CI: only with NFS_GFX_CHECK_DIR) ----

    /// <summary>PNG pairs (Vanilla ×k | Enhanced) and render times; the directory from <c>NFS_GFX_CHECK_DIR</c> (usually <c>out/gfx_check</c>).</summary>
    [GameDataFact]
    public void Report_WritesPairsAndTimings_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        const int k = 2;
        var lines = new List<string>();
        foreach (string scene in GfxScenes.Names)
        {
            var vanilla = Frame(scene, GraphicsMode.Vanilla, 1);
            var enhanced = Frame(scene, GraphicsMode.Enhanced, k);
            var left = Compositor.UpscaleNearest(vanilla, k);
            var pair = new FrameBuffer(left.Width * 2 + 8, left.Height); pair.Clear(255, 0, 255);
            for (int y = 0; y < left.Height; y++)
            {
                Buffer.BlockCopy(left.Rgba, y * left.Width * 4, pair.Rgba, y * pair.Width * 4, left.Width * 4);
                Buffer.BlockCopy(enhanced.Rgba, y * enhanced.Width * 4, pair.Rgba, (y * pair.Width + left.Width + 8) * 4, enhanced.Width * 4);
            }
            pair.SavePng(Path.Combine(dir, $"pair_{scene}.png"));
            vanilla.SavePng(Path.Combine(dir, $"vanilla_{scene}.png"));
            enhanced.SavePng(Path.Combine(dir, $"enhanced_{scene}.png"));
            lines.Add($"{scene}: PSNR(down2 vs 1x) {Psnr(vanilla, Compositor.Downsample(enhanced, k)):F1} dB");
        }
        File.WriteAllLines(Path.Combine(dir, "report.txt"), lines);
    }

    /// <summary>Time of one <c>Render</c> frame (3D scene + 2D layer in CPU buffers, without copying to the GPU) for k = 1/2/3/4; the scene is built once.</summary>
    [GameDataFact]
    public void Timing_RenderTimePerScale_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        foreach (string scene in new[] { "day", "chase", "cockpit", "split", "rain" })
        {
            var draw = GfxScenes.Prepare(scene);
            foreach (int k in new[] { 1, 2, 3, 4 })
            {
                var t = new FrameTargets();
                t.Configure(k == 1 ? GraphicsMode.Vanilla : GraphicsMode.Enhanced, k);
                for (int i = 0; i < 3; i++) draw(t);   // rozgrzewka + JIT
                var times = new List<double>();
                for (int i = 0; i < 15; i++)
                {
                    var sw = Stopwatch.StartNew();
                    draw(t);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                lines.Add($"{scene} k={k}: median {times[times.Count / 2]:F1} ms, min {times[0]:F1} ms, max {times[^1]:F1} ms");
            }
        }
        File.WriteAllLines(Path.Combine(dir, "timing_render.txt"), lines);
    }

    static double MeanAbsError(FrameBuffer a, FrameBuffer b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        long sum = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            sum += Math.Abs(a.Rgba[i] - b.Rgba[i]) + Math.Abs(a.Rgba[i + 1] - b.Rgba[i + 1]) + Math.Abs(a.Rgba[i + 2] - b.Rgba[i + 2]);
        return sum / (3.0 * (a.Rgba.Length / 4));
    }

    static double Psnr(FrameBuffer a, FrameBuffer b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        double sum = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            for (int c = 0; c < 3; c++) { double d = a.Rgba[i + c] - b.Rgba[i + c]; sum += d * d; }
        double mse = sum / (3.0 * (a.Rgba.Length / 4));
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
