using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Task 08i point 2: the specialised pixel loops of <see cref="EnhancedRasterizer"/> (filter × mip blend × alpha × light × fog, clamp-free
/// when the whole run lies inside the texture) give exactly the pixels of the clamping loops, for every combination of the options.</summary>
public class EnhancedPixelLoopTests
{
    static ScreenVertex[] RandomConvex(Random rng, int w, int h, int tw, int th, bool outside)
    {
        int n = rng.Next(3, 8);
        double cx = rng.NextDouble() * w, cy = rng.NextDouble() * h, rx = 5 + rng.NextDouble() * w / 2, ry = 5 + rng.NextDouble() * h / 2;
        // texture coordinates mostly well inside the texture (clamp-free runs), sometimes across its edges
        double lo = outside ? -3 : 1.5, hu = outside ? tw + 3 : tw - 1.5, hv = outside ? th + 3 : th - 1.5;
        var v = new ScreenVertex[n];
        for (int i = 0; i < n; i++)
        {
            double a = (i + rng.NextDouble() * 0.6) * 2 * Math.PI / n;
            double u = lo + rng.NextDouble() * (hu - lo), vv = lo + rng.NextDouble() * (hv - lo);
            v[i] = new ScreenVertex(cx + Math.Cos(a) * rx, cy + Math.Sin(a) * ry, u, vv) { W = 0.3 + rng.NextDouble() * 1.7, L = rng.Next(3) == 0 ? 1 : 0.4 + rng.NextDouble() * 1.4 };
        }
        return v;
    }

    static byte[] Render(bool clampAlways, FillContext ctx, Texture[] texs, int seed)
    {
        const int W = 200, H = 150;
        var rng = new Random(seed);
        var fb = new FrameBuffer(W, H); fb.Clear(10, 20, 30);
        bool old = EnhancedRasterizer.ClampAlways;
        EnhancedRasterizer.ClampAlways = clampAlways;
        try
        {
            for (int i = 0; i < 120; i++)
            {
                var tex = texs[i % texs.Length];
                var p = RandomConvex(rng, W, H, tex.Width, tex.Height, i % 4 == 0);
                EnhancedRasterizer.Fill(fb, p, tex, 0x336699, i % 3 == 0, i % 5, 0xA0B0C0, ctx);
            }
        }
        finally { EnhancedRasterizer.ClampAlways = old; }
        return fb.Rgba;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var f in new[] { TextureFilter.Nearest, TextureFilter.Bilinear, TextureFilter.Trilinear })
            foreach (bool persp in new[] { false, true })
                foreach (bool shade in new[] { false, true })
                    foreach (int fog in new[] { 0, 1 })
                        yield return [f, persp, shade, fog];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClampFreeLoops_GiveTheSamePixelsAsClampingLoops(TextureFilter filter, bool persp, bool shade, int fogMode)
    {
        var big = new Texture(64, 32, Enumerable.Range(0, 2048).Select(i => (uint)(((i * 2654435761u) >> 8) & 0xFFFFFF)).ToArray(),
            Enumerable.Range(0, 2048).Select(i => i % 7 == 0).ToArray());
        var small = new Texture(8, 16, Enumerable.Range(0, 128).Select(i => (uint)(((i * 40503u + 977) * 2246822519u >> 6) & 0xFFFFFF)).ToArray(),
            Enumerable.Range(0, 128).Select(i => i % 5 == 1).ToArray());
        var thin = new Texture(1, 4, [0xFF0000u, 0x00FF00u, 0x0000FFu, 0xFFFFFFu], [false, false, true, false]);
        var fog = Enumerable.Range(0, 128).Select(i => Math.Min(256, i * 3)).ToArray();
        var fogMax = new int[128]; for (int i = 0, m = 0; i < 128; i++) { m = Math.Max(m, fog[i]); fogMax[i] = m; }
        var ctx = new FillContext { Perspective = persp, Filter = filter, LodBias = 0.25, Shade = shade };
        if (fogMode == 1) { ctx.FogWeight = fog; ctx.FogMaxUpTo = fogMax; ctx.FogBucketOffset = 3; }
        Texture[] texs = [big, small, thin];

        var reference = Render(true, ctx, texs, 91);
        var fast = Render(false, ctx, texs, 91);
        Assert.Equal(reference, fast);
        Assert.NotEqual(new FrameBuffer(200, 150).Rgba, fast);
    }

    [Fact]
    public void Interior_HoldsOnlyWhenEveryTexelOfTheRunIsInsideTheLevel()
    {
        var lev = new uint[16 * 8];
        EnhancedRasterizer.RunArgs Args(double u0, double du, double v0) => new()
        {
            Lev = lev, Lw = 16, Lh = 8, Su = (int)(u0 * 65536), Sv = (int)(v0 * 65536), Dsu = (int)(du * 65536), Dsv = 0,
        };
        // bilinear reads texels floor(u − 0.5) and +1: inside for u in [0.5, 15.5)
        Assert.True(EnhancedRasterizer.Interior(Args(0.5, 0.9, 4), 16, true));
        Assert.False(EnhancedRasterizer.Interior(Args(0.49, 0.9, 4), 16, true));
        Assert.True(EnhancedRasterizer.Interior(Args(15.4, -0.9, 4), 16, true));
        Assert.False(EnhancedRasterizer.Interior(Args(15.5, -0.9, 4), 16, true));
        Assert.False(EnhancedRasterizer.Interior(Args(1, 1, 4), 16, true));          // end at u = 16
        Assert.False(EnhancedRasterizer.Interior(Args(4, 0, 7.6), 16, true));        // v row 7 has no row below
        // nearest reads texel floor(u): inside for u in [0, 16)
        Assert.True(EnhancedRasterizer.Interior(Args(0, 1, 7.9), 16, false));
        Assert.False(EnhancedRasterizer.Interior(Args(0.1, 1, 0), 17, false));
        // a step that would wrap the 32-bit coordinate is never clamp-free
        Assert.False(EnhancedRasterizer.Interior(new EnhancedRasterizer.RunArgs { Lev = lev, Lw = 16, Lh = 8, Su = 2 << 16, Sv = 2 << 16, Dsu = 1_000_000_000 }, 16, true));
    }
}
