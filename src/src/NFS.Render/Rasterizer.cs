namespace NFS.Render;

/// <summary>Screen vertex: pixel position and texture coordinate in texels.</summary>
public struct ScreenVertex(double x, double y, double u, double v)
{
    public double X = x, Y = y, U = u, V = v;
    /// <summary>1/z of the vertex (view-space depth), used only by <see cref="EnhancedRasterizer"/> (perspective-correct UV, per-pixel fog); 1 = flat.</summary>
    public double W = 1;
    /// <summary>Light intensity of the vertex (1 = unlit texel colour), used only by <see cref="EnhancedRasterizer"/> when <see cref="FillContext.Shade"/> is on
    /// (Gouraud: interpolated linearly in screen space and multiplied into the texel colour).</summary>
    public double L = 1;
}

/// <summary>
/// Convex polygon scan converter (after the original): edges in double, <b>affine</b> UV in screen space [ref:2631]
/// (perspective only via the caller's subdivision), clipped to the framebuffer. Pixel centres at +0.5.
/// </summary>
public static class Rasterizer
{
    /// <summary>Fills a polygon. Texture null → flat <paramref name="color"/>. Fog: lerp to fogRgb by fogLevel/16. Only the rows
    /// <paramref name="yMin"/>..<paramref name="yMax"/>-1 are written (a horizontal strip: every row is independent, so strips give the same pixels as the whole fill).</summary>
    public static void Fill(FrameBuffer fb, ReadOnlySpan<ScreenVertex> p, Texture? tex, uint color,
        bool transparent = false, int fogLevel = 0, uint fogRgb = 0, int yMin = 0, int yMax = int.MaxValue)
    {
        int n = p.Length;
        if (n < 3) return;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++) { minY = Math.Min(minY, p[i].Y); maxY = Math.Max(maxY, p[i].Y); }
        int y0 = Math.Max(Math.Max(0, yMin), (int)Math.Ceiling(minY - 0.5));
        int y1 = Math.Min(Math.Min(fb.Height, yMax) - 1, (int)Math.Ceiling(maxY - 0.5) - 1);
        if (tex == null) color = Fog(color, fogLevel, fogRgb);
        // pixels are written as whole 32-bit words (r | g<<8 | b<<16 | 255<<24 = what FrameBuffer.Set stores byte by byte)
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(fb.Rgba.AsSpan());
        int fbw = fb.Width;
        uint flatMem = ToMem(color);
        uint[]? rgbTab = tex?.Rgb; bool[]? maskTab = tex?.Transparent;
        int tw = tex?.Width ?? 0, th = tex?.Height ?? 0;
        bool fogged = fogLevel > 0;
        for (int y = y0; y <= y1; y++)
        {
            double sy = y + 0.5;
            double xl = double.MaxValue, xr = double.MinValue, ul = 0, vl = 0, ur = 0, vr = 0;
            for (int i = 0; i < n; i++)
            {
                var a = p[i]; var b = p[(i + 1) % n];
                if (a.Y == b.Y) continue;
                if ((sy < a.Y || sy >= b.Y) && (sy < b.Y || sy >= a.Y)) continue;
                double t = (sy - a.Y) / (b.Y - a.Y);
                double x = a.X + (b.X - a.X) * t, u = a.U + (b.U - a.U) * t, v = a.V + (b.V - a.V) * t;
                if (x < xl) { xl = x; ul = u; vl = v; }
                if (x > xr) { xr = x; ur = u; vr = v; }
            }
            if (xr < xl) continue;
            int x0 = Math.Max(0, (int)Math.Ceiling(xl - 0.5));
            int x1 = Math.Min(fb.Width - 1, (int)Math.Ceiling(xr - 0.5) - 1);
            if (x1 < x0) continue;
            if (tex == null)
            {
                px.Slice(y * fbw + x0, x1 - x0 + 1).Fill(flatMem);
                continue;
            }
            double span = xr - xl, du = span > 0 ? (ur - ul) / span : 0, dv = span > 0 ? (vr - vl) / span : 0;
            var row = px.Slice(y * fbw, fbw);
            for (int x = x0; x <= x1; x++)
            {
                double d = x + 0.5 - xl;
                int tu = Math.Clamp((int)(ul + du * d), 0, tw - 1);
                int tv = Math.Clamp((int)(vl + dv * d), 0, th - 1);
                int k = tv * tw + tu;
                if (transparent && maskTab![k]) continue;
                uint c = rgbTab![k];
                row[x] = ToMem(fogged ? Fog(c, fogLevel, fogRgb) : c);
            }
        }
    }

    /// <summary>0xRRGGBB → the framebuffer word (byte order R, G, B, 255).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint ToMem(uint rgb) => (rgb >> 16 & 255) | (rgb >> 8 & 255) << 8 | (rgb & 255) << 16 | 0xFF000000u;

    /// <summary>colour lerped toward the fog colour with weight level/16 (RGB system). [ref:2632]</summary>
    public static uint Fog(uint c, int level, uint fog)
    {
        if (level <= 0) return c;
        int r = (int)(c >> 16 & 255), g = (int)(c >> 8 & 255), b = (int)(c & 255);
        int fr = (int)(fog >> 16 & 255), fg = (int)(fog >> 8 & 255), fbb = (int)(fog & 255);
        r += (fr - r) * level / 16; g += (fg - g) * level / 16; b += (fbb - b) * level / 16;
        return (uint)(r << 16 | g << 8 | b);
    }

    /// <summary>Signed doubled area (screen y down): > 0 = clockwise on screen.</summary>
    public static double Area2(ReadOnlySpan<ScreenVertex> p)
    {
        double a = 0;
        int n = p.Length;
        if (n == 0) return a;
        for (int i = 0; i < n - 1; i++) { ref readonly var q = ref p[i]; ref readonly var r = ref p[i + 1]; a += q.X * r.Y - r.X * q.Y; }
        { ref readonly var q = ref p[n - 1]; ref readonly var r = ref p[0]; a += q.X * r.Y - r.X * q.Y; }
        return a;
    }

    /// <summary><see cref="Area2(ReadOnlySpan{ScreenVertex})"/> of the triangle (a, b, c) without building a span (same summation order).</summary>
    public static double Area2(in ScreenVertex a, in ScreenVertex b, in ScreenVertex c)
    {
        double s = 0;
        s += a.X * b.Y - b.X * a.Y;
        s += b.X * c.Y - c.X * b.Y;
        s += c.X * a.Y - a.X * c.Y;
        return s;
    }
}
