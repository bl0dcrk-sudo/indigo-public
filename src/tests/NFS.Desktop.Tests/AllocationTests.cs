using NFS.Desktop;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Task 17 (allocations): once warm, rendering a race scene frame must not allocate per polygon / per car / per object
/// (it used to allocate ~110 kB per frame: LINQ in DrawCar, a vertex array per object, re-parsed XBID data, ...). Measured on the calling thread.</summary>
[CollectionDefinition("Allocations", DisableParallelization = true)]
public class AllocationsCollection;

[Collection("Allocations")]
public class AllocationTests(ITestOutputHelper output)
{
    /// <summary>Bytes per frame the render thread may allocate after warm-up (the scene helpers build a Camera per frame; was 100+ kB, now 0.25-0.7 kB).</summary>
    const long Limit = 1024;

    static long PerFrame(Action<FrameTargets> draw, FrameTargets t, int frames = 8)
    {
        for (int i = 0; i < 4; i++) draw(t);   // warm: pools, caches, texture mips
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) draw(t);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
    }

    [GameDataTheory]
    [InlineData("day")]
    [InlineData("tunnel")]
    [InlineData("car")]
    [InlineData("chase")]
    [InlineData("cockpit")]
    [InlineData("split")]
    [InlineData("rain")]
    public void VanillaRaceFrame_AllocatesAlmostNothingWhenWarm(string scene)
    {
        var t = new FrameTargets(); t.Configure(GraphicsMode.Vanilla, 1);
        long bytes = PerFrame(GfxScenes.Prepare(scene), t);
        output.WriteLine($"{scene} vanilla: {bytes} B/frame");
        Assert.True(bytes < Limit, $"{scene}: {bytes} B per frame");
    }

    [GameDataTheory]
    [InlineData("day")]
    [InlineData("car")]
    [InlineData("chase")]
    [InlineData("cockpit")]
    [InlineData("split")]
    public void EnhancedRaceFrame_AllocatesAlmostNothingWhenWarm(string scene)
    {
        // all Enhanced fill features + car light and shadow, single-threaded fill (ParallelFill off): everything runs on this thread
        var opt = new RenderOptions { PerspectiveCorrect = true, Filter = TextureFilter.Trilinear, PixelFog = true, CarLight = true, CarShadow = true, TrackShade = true, ExtendedRange = true };
        var t = new FrameTargets(); t.Configure(GraphicsMode.Enhanced, 2, 1);
        long bytes = PerFrame(GfxScenes.Prepare(scene, opt), t);
        output.WriteLine($"{scene} enhanced: {bytes} B/frame");
        Assert.True(bytes < Limit, $"{scene}: {bytes} B per frame");
    }

    [GameDataTheory]
    [InlineData("chase")]
    [InlineData("split")]
    public void ParallelFill_AllocatesAlmostNothingWhenWarm_OnEveryThread(string scene)
    {
        // 1280x960 Enhanced with the strip fill on worker threads: process-wide bytes (this class runs alone, see the collection), so the workers count too
        var opt = new RenderOptions { PerspectiveCorrect = true, Filter = TextureFilter.Trilinear, PixelFog = true, CarLight = true, CarShadow = true, TrackShade = true, ParallelFill = true };
        var t = new FrameTargets(); t.Configure(GraphicsMode.Enhanced, 2, 1);
        var draw = GfxScenes.Prepare(scene, opt);
        for (int i = 0; i < 6; i++) draw(t);
        GC.Collect();
        // best of 3 batches: under CPU load the thread pool may inject a new worker (its stack objects count once), which is not a per-frame cost
        const int frames = 10;
        long bytes = long.MaxValue;
        for (int batch = 0; batch < 3; batch++)
        {
            long before = GC.GetTotalAllocatedBytes(false);
            for (int i = 0; i < frames; i++) draw(t);
            bytes = Math.Min(bytes, (GC.GetTotalAllocatedBytes(false) - before) / frames);
        }
        output.WriteLine($"{scene} parallel: {bytes} B/frame (all threads)");
        Assert.True(bytes < ParallelLimit, $"{scene}: {bytes} B per frame");
    }

    [GameDataFact]
    public void PresentPoses_AllocatesNothingWhenWarm()
    {
        // the per-frame pose interpolation updates the render instances in place (it used to build a new CarInstance per car per frame)
        var scene = new RaceScene("replay");
        var d = new RaceDrive(scene, countdown: false, automatic: true);
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 40; i++) d.Frame(1.0 / 32, gas);   // a published frame, as in a real race
        scene.PresentPoses(0.5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 16;
        for (int i = 0; i < frames; i++) scene.PresentPoses(i / (double)frames);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        output.WriteLine($"PresentPoses: {bytes} B/frame");
        Assert.True(bytes == 0, $"PresentPoses: {bytes} B per frame");
    }

    /// <summary>The strip fill queues pooled work items (a few kB of thread-pool bookkeeping per frame at most; Parallel.For used 19 kB); contexts, delegates and closures are reused.</summary>
    const long ParallelLimit = 12288;
}
