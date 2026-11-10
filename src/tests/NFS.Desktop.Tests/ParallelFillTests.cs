using System.Diagnostics;
using NFS.Desktop;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Parallel strip fill of the Enhanced renderer (task 08f, <see cref="FillRecorder"/>): the polygons of a frame are recorded and filled by horizontal
/// strips on several threads. The result must be bit-identical to the single-threaded order, for every strip layout; Vanilla never uses it.</summary>
public class ParallelFillTests(ITestOutputHelper output)
{
    static RenderOptions Options(bool parallel) => new()
    {
        PerspectiveCorrect = true, Filter = TextureFilter.Trilinear, PixelFog = true, CarLight = true, CarShadow = true, ParallelFill = parallel,
    };

    static FrameBuffer Frame(string scene, int k, int ssaa, bool parallel, int threads = 0, int stripsPerThread = 4)
    {
        int oldThreads = FillRecorder.MaxThreads, oldStrips = FillRecorder.StripsPerThread;
        FillRecorder.MaxThreads = threads; FillRecorder.StripsPerThread = stripsPerThread;
        try
        {
            var t = new FrameTargets();
            t.Configure(GraphicsMode.Enhanced, k, ssaa);
            GfxScenes.Render(scene, t, Options(parallel));
            return t.ComposeCpu();
        }
        finally { FillRecorder.MaxThreads = oldThreads; FillRecorder.StripsPerThread = oldStrips; }
    }

    [GameDataTheory]
    [InlineData("day")]
    [InlineData("tunnel")]
    [InlineData("car")]
    [InlineData("chase")]
    [InlineData("cockpit")]
    [InlineData("split")]
    public void ParallelStrips_AreBitIdenticalToTheSingleThreadedFill(string scene)
    {
        var reference = Frame(scene, 2, 1, parallel: false);
        // default threads, a single strip, many thin strips (odd counts too): the layout must not change a pixel
        foreach (var (threads, per) in new[] { (0, 4), (1, 1), (3, 5), (8, 7), (2, 1) })
            Assert.Equal(reference.Sha256(), Frame(scene, 2, 1, true, threads, per).Sha256());
    }

    [GameDataFact]
    public void ParallelStrips_AlsoMatchWithSupersamplingAndHigherScales()
    {
        foreach (var (k, s) in new[] { (1, 2), (3, 1), (2, 2) })
            Assert.Equal(Frame("chase", k, s, false).Sha256(), Frame("chase", k, s, true).Sha256());
    }

    [GameDataFact]
    public void SmallFramesAndVanillaDoNotUseTheRecorder_AndStayTheSame()
    {
        // 640x480 (below ParallelMinHeight): the flag changes nothing
        Assert.Equal(Frame("chase", 1, 1, false).Sha256(), Frame("chase", 1, 1, true).Sha256());
        var vanilla = new FrameTargets(); vanilla.Configure(GraphicsMode.Vanilla, 1);
        GfxScenes.Render("chase", vanilla, new RenderOptions { ParallelFill = true });
        var plain = new FrameTargets(); plain.Configure(GraphicsMode.Vanilla, 1);
        GfxScenes.Render("chase", plain, new RenderOptions());
        Assert.Equal(plain.ComposeCpu().Sha256(), vanilla.ComposeCpu().Sha256());
    }

    [Fact]
    public void RasterizerStrips_ReproduceTheWholeFill_AlsoForTrilinearRowDependence()
    {
        // a large textured triangle strip at an angle: trilinear mip selection looks at the previous row, which crosses strip borders
        var tex = new Texture(64, 64, Enumerable.Range(0, 4096).Select(i => (uint)(((i * 2654435761u) >> 8) & 0xFFFFFF)).ToArray(), new bool[4096]);
        ScreenVertex V(double x, double y, double u, double v, double z) => new(x, y, u, v) { W = 1 / z, L = 1 };
        var poly = new[] { V(5, 4, 0, 0, 40), V(190, 10, 64, 0, 40), V(200, 190, 64, 640, 1.5), V(10, 195, 0, 640, 1.5) };
        var ctx = new FillContext { Perspective = true, Filter = TextureFilter.Trilinear };
        var whole = new FrameBuffer(200, 200); whole.Clear(1, 2, 3);
        EnhancedRasterizer.Fill(whole, poly, tex, 0, false, 0, 0, ctx);
        foreach (int step in new[] { 1, 7, 33, 100 })
        {
            var strips = new FrameBuffer(200, 200); strips.Clear(1, 2, 3);
            for (int y = 0; y < 200; y += step) EnhancedRasterizer.Fill(strips, poly, tex, 0, false, 0, 0, ctx, y, y + step);
            Assert.Equal(whole.Sha256(), strips.Sha256());
        }
        var flatWhole = new FrameBuffer(200, 200); var flatStrips = new FrameBuffer(200, 200);
        Rasterizer.Fill(flatWhole, poly, tex, 0, false, 5, 0x808080);
        for (int y = 0; y < 200; y += 13) Rasterizer.Fill(flatStrips, poly, tex, 0, false, 5, 0x808080, y, y + 13);
        Assert.Equal(flatWhole.Sha256(), flatStrips.Sha256());
    }

    [GameDataFact]
    public void FrameTime_SingleThreadedVersusStrips()
    {
        // not an assertion of speed (machine dependent): prints the frame time for the report; checks only that the result is the same
        foreach (int k in new[] { 2, 3, 4 })
        {
            var t = new FrameTargets(); t.Configure(GraphicsMode.Enhanced, k, 1);
            var opt = Options(false);
            var draw = GfxScenes.Prepare("chase", opt);
            double Time(bool parallel)
            {
                opt.ParallelFill = parallel;
                draw(t); draw(t);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 15; i++) draw(t);
                return sw.Elapsed.TotalMilliseconds / 15;
            }
            double single = Time(false), par = Time(true);
            output.WriteLine($"[{Environment.ProcessorCount} cores] k={k} ({640 * k}x{480 * k}): single {single:0.0} ms, strips {par:0.0} ms, x{single / par:0.00}");
        }
    }
}
