using NFS.Core;
using NFS.Core.Constants;

namespace NFS.Render;

/// <summary>Car paint (lakier), colour.c: the colour index of the race config record (+0xC, arg tag 0x7E) selects an
/// entry {h, s, v, threshold} (16 B) of the HSV table (0x59 entries; used entries are cleared). [ref:2485]
/// repaints the green key entries of the car palette [ref:2486]
/// (r &lt; 0x56, g &gt; 0x7F, b &lt; 0x56) from the HSV colour; each entry is [ref:2487]
/// computed from it. Components are 0..256 ints (HSV and RGB both on the 0x100 scale). [ref:2488]</summary>
public static class CarPaint
{
    public const int TableEntries = 0x59;

    /// <summary>{h, s, v, threshold} of table entry <paramref name="index"/> (16 B per entry, <see cref="RenderConstants.CarPaintHsv"/>). [ref:2489]</summary>
    public static int[] Hsv(int index) => RenderConstants.CarPaintHsv.Slice(index * 4, 4).ToArray();

    /// <summary>(h, s, v 0..256 → r, g, b 0..256). v &lt; 1 → black, s &lt; 1 → grey (v); otherwise [ref:2490]
    /// sector = h·6 &gt;&gt; 8 and fraction F (h·6 − sector·256)·256 (16.16; the FPU part reduces to [ref:2491]
    /// exactly this for integer h); p = (256 − s)·v &gt;&gt; 8, q = (256 − s·F)·v &gt;&gt; 8, t = (256 − s·(1 − F))·v &gt;&gt; 8
    /// (s·F = rounded 16.16 product); sector 0..5 (v,t,p) (q,v,p) (p,v,t) (p,q,v) (t,p,v) (v,p,q); [ref:2492]
    /// finally each component is clamped to 0..256. Sector 6 (h = 256) leaves the outputs untouched in the original (stack
    /// garbage); here they stay 0.</summary>
    public static (int R, int G, int B) HsvToRgb(int h, int s, int v)
    {
        int r = 0, g = 0, b = 0;
        if (v < 1) { }
        else if (s < 1) { r = g = b = v; }
        else
        {
            int sector = h * 6 >> 8, fraction = (h * 6 - (sector << 8)) << 8;
            int p = (256 - s) * v >> 8;
            int q = (256 - Fx.Mul(s, fraction)) * v >> 8;
            int t = (256 - Fx.Mul(s, 65536 - fraction)) * v >> 8;
            switch (sector)
            {
                case 0: (r, g, b) = (v, t, p); break;
                case 1: (r, g, b) = (q, v, p); break;
                case 2: (r, g, b) = (p, v, t); break;
                case 3: (r, g, b) = (p, q, v); break;
                case 4: (r, g, b) = (t, p, v); break;
                case 5: (r, g, b) = (v, p, q); break;
            }
        }
        return (Math.Clamp(r, 0, 256), Math.Clamp(g, 0, 256), Math.Clamp(b, 0, 256));
    }

    /// <summary>(inverse; used by the HSV fog, not by the paint). v = max, s (max − min)·256 / max [ref:2493]
    /// hue in 16.16 sixths: r = max → (g−b)/Δ, g = max → 2 + (b−r)/Δ, else 4 + (r−g)/Δ (each quotient 16.16 =
    /// (q &lt;&lt; 16) + (rem &lt;&lt; 16)/Δ), divided by 6 (truncating), +1.0 when negative, ×256 (rounded 16.16 product);
    /// all three clamped to 0..256. v = 0 or Δ = 0 → h = s = 0.</summary>
    public static (int H, int S, int V) RgbToHsv(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        int h6 = 0, s = 0;
        if (max != 0 && delta != 0)
        {
            s = delta * 256 / max;
            static int Q(int num, int delta) => (num / delta << 16) + (int)((long)(num % delta) * 65536 / delta);
            int x = r == max ? Q(g - b, delta) : g == max ? Q(b - r, delta) + 0x20000 : Q(r - g, delta) + 0x40000;
            h6 = x / 6;
            if (h6 < 0) h6 += 0x10000;
        }
        return (Math.Clamp(Fx.Mul(h6, 0x100), 0, 256), Math.Clamp(s, 0, 256), Math.Clamp(max, 0, 256));
    }

    /// <summary>Recolours one key entry (<paramref name="key"/> = original r, g, b; g &gt; 0x7F) [ref:2494]
    /// base colour <paramref name="rgb"/> from HSV, threshold = table +0xC. i = g − 0x80. i ≤ threshold: rgb·i / threshold;
    /// else rgb + (min(255, 4·rgb) − rgb)·(i − threshold) / (0x7F − threshold) (divisions truncate, results kept as bytes).
    /// Highlight (original r &gt; 0 and b &gt; 0): r += 3·r_key/2, g += i/2, b += 3·b_key/2 on the byte values, each capped at 255.</summary>
    public static (byte R, byte G, byte B) RecolourEntry((int R, int G, int B) key, int threshold, (int R, int G, int B) rgb)
    {
        int i = key.G - 0x80, extra = i - threshold;
        byte Shade(int c) => unchecked((byte)(extra < 1
            ? (threshold == 0 ? 0 : c * i / threshold) // threshold 0 with g = 0x80 would trap (#DE) in the original
            : (byte)((Math.Min(255, c * 4) - c) * extra / (0x7F - threshold)) + (byte)c));
        int r = Shade(rgb.R), g = Shade(rgb.G), b = Shade(rgb.B);
        if (key.R > 0 && key.B > 0)
        {
            r = Math.Min(255, (key.R * 3 >> 1) + r);
            g = Math.Min(255, g + (i >> 1));
            b = Math.Min(255, b + (key.B * 3 >> 1));
        }
        return ((byte)r, (byte)g, (byte)b);
    }

    /// <summary>On a 256 × RGBA palette (the original has 3-byte entries; alpha is untouched): [ref:2495]
    /// the HSV entry {h, s, v, threshold} becomes an RGB base once, then every key entry (r &lt; 0x56, g &gt; 0x7F, b &lt; 0x56)
    /// is replaced by <see cref="RecolourEntry"/>.</summary>
    public static void RecolourPalette(byte[] pal, int[] hsv)
    {
        var rgb = HsvToRgb(hsv[0], hsv[1], hsv[2]);
        for (int o = 0; o + 3 < pal.Length; o += 4)
        {
            int r = pal[o], g = pal[o + 1], b = pal[o + 2];
            if (r >= 0x56 || g <= 0x7F || b >= 0x56) continue;
            var (nr, ng, nb) = RecolourEntry((r, g, b), hsv[3], rgb);
            pal[o] = nr; pal[o + 1] = ng; pal[o + 2] = nb;
        }
    }
}
