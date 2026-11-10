using NFS.Render;

namespace NFS.Tests.Render;

/// <summary>SSAA resolve (<see cref="Compositor.DownsampleInto"/>): the 32-bit paths for factors 2/3/4 and the parallel row blocks must give exactly
/// the bytes of the original per-byte loop (<see cref="Reference"/>, a copy of the code before the change).</summary>
public class DownsampleTests
{
    /// <summary>The original per-byte box filter: each channel <c>(sum + n/2) / n</c>, alpha 255.</summary>
    static FrameBuffer Reference(FrameBuffer src, int factor)
    {
        var dst = new FrameBuffer(src.Width / factor, src.Height / factor);
        int n = factor * factor;
        for (int y = 0; y < dst.Height; y++)
            for (int x = 0; x < dst.Width; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int j = 0; j < factor; j++)
                    for (int i = 0; i < factor; i++)
                    {
                        int o = ((y * factor + j) * src.Width + x * factor + i) * 4;
                        r += src.Rgba[o]; g += src.Rgba[o + 1]; b += src.Rgba[o + 2];
                    }
                int q = (y * dst.Width + x) * 4;
                dst.Rgba[q] = (byte)((r + n / 2) / n); dst.Rgba[q + 1] = (byte)((g + n / 2) / n); dst.Rgba[q + 2] = (byte)((b + n / 2) / n);
                dst.Rgba[q + 3] = 255;
            }
        return dst;
    }

    static FrameBuffer Random(int w, int h, int seed)
    {
        var fb = new FrameBuffer(w, h);
        new Random(seed).NextBytes(fb.Rgba);
        return fb;
    }

    /// <summary>Splits a channel sum over the n samples of a block (as full samples as possible, the rest in one, zeros after) – rotated by
    /// <paramref name="rot"/> so the channels do not share the pattern.</summary>
    static int Sample(int sum, int k, int n, int rot)
    {
        int idx = (k + rot) % n;
        return Math.Clamp(sum - 255 * idx, 0, 255);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryChannelSum_RoundsLikeTheByteLoop(int factor)
    {
        // one destination pixel per possible channel sum 0..n·255 (R = s, G = max − s, B = a permutation of s), alpha bytes random:
        // the packed sums are exact, so covering every sum in every channel covers every rounding case
        int n = factor * factor, max = n * 255, count = max + 1;
        int dw = 64, dh = (count + dw - 1) / dw;
        var src = new FrameBuffer(dw * factor, dh * factor);
        var rnd = new Random(factor);
        for (int p = 0; p < dw * dh; p++)
        {
            int s = p % count;
            int sr = s, sg = max - s, sb = (int)((long)s * 7919 % count);
            int px = p % dw, py = p / dw;
            for (int k = 0; k < n; k++)
            {
                int o = ((py * factor + k / factor) * src.Width + px * factor + k % factor) * 4;
                src.Rgba[o] = (byte)Sample(sr, k, n, 0);
                src.Rgba[o + 1] = (byte)Sample(sg, k, n, 1);
                src.Rgba[o + 2] = (byte)Sample(sb, k, n, 2);
                src.Rgba[o + 3] = (byte)rnd.Next(256);
            }
        }
        Assert.Equal(Reference(src, factor).Rgba, Compositor.Downsample(src, factor).Rgba);
    }

    [Theory]
    [InlineData(1, 8, 6)]
    [InlineData(2, 10, 7)]
    [InlineData(3, 9, 5)]
    [InlineData(4, 7, 3)]
    [InlineData(5, 4, 4)]
    [InlineData(2, 640, 480)]    // parallel row blocks from here on
    [InlineData(3, 640, 480)]
    [InlineData(4, 640, 480)]
    [InlineData(3, 643, 487)]    // odd sizes: last block partial
    [InlineData(5, 640, 480)]
    [InlineData(1, 1280, 960)]
    public void RandomFrames_MatchTheByteLoop(int factor, int dw, int dh)
    {
        for (int seed = 0; seed < 2; seed++)
        {
            var src = Random(dw * factor, dh * factor, seed * 100 + factor);
            var dst = new FrameBuffer(dw, dh);
            new Random(seed).NextBytes(dst.Rgba); // stale contents must be overwritten
            Compositor.DownsampleInto(src, dst, factor);
            Assert.Equal(Reference(src, factor).Rgba, dst.Rgba);
        }
    }

    [Fact]
    public void ExtremeValues_MatchTheByteLoop()
    {
        foreach (int factor in new[] { 2, 3, 4 })
            foreach (uint v in new[] { 0u, 0xFFFFFFFFu, 0x00FFFFFFu, 0xFF000000u, 0x00FF00FFu, 0xFF00FF00u, 0x80808080u, 0x7F7F7F7Fu })
            {
                var src = new FrameBuffer(640 * factor, 480 * factor);
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src.Rgba.AsSpan()).Fill(v);
                Assert.Equal(Reference(src, factor).Rgba, Compositor.Downsample(src, factor).Rgba);
            }
    }

    [Fact]
    public void ConcurrentCalls_GiveTheSameBytes()
    {
        var srcs = Enumerable.Range(0, 4).Select(i => Random(640 * 3, 480 * 3, 1000 + i)).ToArray();
        var expected = srcs.Select(s => Reference(s, 3).Rgba).ToArray();
        Parallel.For(0, 16, i =>
        {
            var dst = new FrameBuffer(640, 480);
            Compositor.DownsampleInto(srcs[i % 4], dst, 3);
            Assert.Equal(expected[i % 4], dst.Rgba);
        });
    }

    [Fact]
    public void Resolve_AllocatesNothingPerFrame()
    {
        var src = Random(1280, 960, 7);
        var dst = new FrameBuffer(640, 480);
        for (int i = 0; i < 5; i++) Compositor.DownsampleInto(src, dst, 2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Compositor.DownsampleInto(src, dst, 2);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
