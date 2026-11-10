using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NFS.Render;

/// <summary>Texture sampling of the Enhanced renderer. <see cref="Nearest"/> = the original's point sampling; <see cref="Bilinear"/> = 4 texels of level 0;
/// <see cref="Trilinear"/> = bilinear inside the two nearest mip levels, blended (level from the UV density of each 16-pixel run).</summary>
public enum TextureFilter { Nearest, Bilinear, Trilinear }

/// <summary>Per-frame settings of <see cref="EnhancedRasterizer"/> (null context = the original <see cref="Rasterizer.Fill"/>).</summary>
public sealed class FillContext
{
    /// <summary>Perspective-correct texture coordinates (u/z, v/z, 1/z interpolated, divided every 16 pixels) instead of affine ones.</summary>
    public bool Perspective;
    public TextureFilter Filter;
    /// <summary>Added to the mip level (negative = sharper).</summary>
    public double LodBias;
    /// <summary>Per-pixel fog: fog weight (0..256 = 0..100 %) of every z-sort bucket (0.5 m); null = the polygon's constant fog level.</summary>
    public int[]? FogWeight;
    /// <summary>Largest weight of buckets 0..i (a polygon whose far end has 0 here is fog free: fast path).</summary>
    public int[]? FogMaxUpTo;
    /// <summary>Bucket of a pixel = 2·z + this; set per draw item as (item bucket − 2·average z), so the item's centre keeps its original level.</summary>
    public double FogBucketOffset;
    /// <summary>Gouraud lighting: the colour of a pixel is multiplied by the interpolated <see cref="ScreenVertex.L"/> of the polygon (cars).</summary>
    public bool Shade;
    /// <summary>The same context without fog (sky).</summary>
    public FillContext WithoutFog() => new() { Perspective = Perspective, Filter = Filter, LodBias = LodBias };
    /// <summary>Copies every field of <paramref name="o"/> into this context (reuse instead of <see cref="Clone"/>).</summary>
    public void SetFrom(FillContext o)
    {
        Perspective = o.Perspective; Filter = o.Filter; LodBias = o.LodBias; FogWeight = o.FogWeight; FogMaxUpTo = o.FogMaxUpTo;
        FogBucketOffset = o.FogBucketOffset; Shade = o.Shade;
    }
    /// <summary>Turns this context into its <see cref="WithoutFog"/> form in place.</summary>
    public void ClearFog() { FogWeight = null; FogMaxUpTo = null; FogBucketOffset = 0; Shade = false; }
    /// <summary>Shallow copy (a worker thread of the strip fill sets <see cref="FogBucketOffset"/> per polygon on its own copy).</summary>
    public FillContext Clone() => (FillContext)MemberwiseClone();
}

/// <summary>
/// Enhanced convex polygon filler (same edge walk and top-left coverage as <see cref="Rasterizer.Fill"/>, so the silhouette is identical), adding
/// perspective-correct UV, bilinear / mip-mapped sampling with alpha masks and per-pixel fog. Texture coordinates and 1/z are interpolated linearly
/// along the edges and across the span; the exact UV is computed every <see cref="Run"/> pixels and stepped linearly in 16.16 inside a run.
/// </summary>
public static class EnhancedRasterizer
{
    const int Run = 16;
    const uint RB = 0x00FF00FF;
    static readonly int[] InvAlpha = BuildInv();

    static int[] BuildInv()
    {
        var t = new int[256];
        for (int a = 1; a < 256; a++) t[a] = (255 * 65536 + a / 2) / a;
        return t;
    }

    public static void Fill(FrameBuffer fb, ReadOnlySpan<ScreenVertex> p, Texture? tex, uint color, bool transparent,
        int fogLevel, uint fogRgb, FillContext ctx, int yMin = 0, int yMax = int.MaxValue)
    {
        int n = p.Length;
        if (n < 3) return;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++) { minY = Math.Min(minY, p[i].Y); maxY = Math.Max(maxY, p[i].Y); }
        int y0 = Math.Max(0, (int)Math.Ceiling(minY - 0.5));
        int y1 = (int)Math.Ceiling(maxY - 0.5) - 1;
        Span<double> attr = stackalloc double[4 * n];
        Prepare(p, ctx.Perspective, attr, out double minW, out bool anyLight);
        FillPrepared(fb, p, tex, color, transparent, fogLevel, fogRgb, ctx, yMin, yMax, attr, minW, anyLight, y0, y1);
    }

    /// <summary>The per-polygon part of <see cref="Fill"/> that does not depend on the strip: the four interpolated attributes of every vertex
    /// (U, V, W, L; U and V pre-multiplied by W when perspective-correct) laid out as <paramref name="attr"/> = [A0 × n, A1 × n, A2 × n, A3 × n],
    /// the smallest W and whether any vertex has a light other than 1. <see cref="FillRecorder"/> computes it once per recorded polygon, not once per strip.</summary>
    internal static void Prepare(ReadOnlySpan<ScreenVertex> p, bool persp, Span<double> attr, out double minW, out bool anyLight)
    {
        int n = p.Length;
        minW = double.MaxValue; anyLight = false;
        for (int i = 0; i < n; i++)
        {
            minW = Math.Min(minW, p[i].W);
            attr[i] = persp ? p[i].U * p[i].W : p[i].U;
            attr[n + i] = persp ? p[i].V * p[i].W : p[i].V;
            attr[2 * n + i] = p[i].W;
            attr[3 * n + i] = p[i].L;
            if (p[i].L != 1) anyLight = true;
        }
    }

    /// <summary><see cref="Fill"/> with the strip-independent part given (<see cref="Prepare"/>); <paramref name="y0"/> / <paramref name="y1"/> = first row and last row
    /// of the polygon before clipping to <paramref name="yMin"/>..<paramref name="yMax"/> and the frame (y0 ≥ 0 already).</summary>
    internal static void FillPrepared(FrameBuffer fb, ReadOnlySpan<ScreenVertex> p, Texture? tex, uint color, bool transparent,
        int fogLevel, uint fogRgb, FillContext ctx, int yMin, int yMax, ReadOnlySpan<double> attr, double minW, bool anyLight, int y0, int y1)
    {
        int n = p.Length;
        y1 = Math.Min(Math.Min(fb.Height, yMax) - 1, y1);
        if (y1 < y0 || y1 < yMin) return;
        // strip fill (yMin > 0, see FillRecorder): the row above the strip is walked without drawing, because the trilinear level of a row
        // looks at the previous row of the same polygon; with it the strip gives exactly the pixels of the whole-polygon fill.
        int yStart = Math.Max(y0, yMin - 1);

        bool persp = ctx.Perspective;
        ReadOnlySpan<double> A0 = attr.Slice(0, n), A1 = attr.Slice(n, n), A2 = attr.Slice(2 * n, n), A3 = attr.Slice(3 * n, n);
        bool shaded = ctx.Shade && anyLight;
        // fog: per pixel from z, or the polygon's constant level
        int[]? fogTab = ctx.FogWeight;
        int constFog = Math.Clamp(fogLevel, 0, 16) * 16;
        if (fogTab != null)
        {
            int far = (int)Math.Clamp(2 / Math.Max(minW, 1e-9) + ctx.FogBucketOffset, 0, fogTab.Length - 1);
            if (ctx.FogMaxUpTo![far] == 0) fogTab = null;
            constFog = 0;
        }
        uint fogMem = (fogRgb >> 16 & 255) | (fogRgb >> 8 & 255) << 8 | (fogRgb & 255) << 16;
        uint fogRB = fogMem & RB, fogG = fogMem & 0xFF00;
        double fogOff = ctx.FogBucketOffset;
        bool anyFog = fogTab != null || constFog > 0;

        uint flatMem = (color >> 16 & 255) | (color >> 8 & 255) << 8 | (color & 255) << 16 | 0xFF000000u;
        var px = MemoryMarshal.Cast<byte, uint>(fb.Rgba.AsSpan());
        int fbw = fb.Width;

        MipChain? chain = tex == null ? null : tex.Mips(transparent);
        var filter = tex == null ? TextureFilter.Nearest : ctx.Filter;
        bool alphaChain = chain is { HasAlpha: true };
        double lodBias = ctx.LodBias;

        bool hasPrev = false;
        double pxl = 0, pxr = 0, p0l = 0, p1l = 0, p2l = 0, p0r = 0, p1r = 0, p2r = 0;
        for (int y = yStart; y <= y1; y++)
        {
            double sy = y + 0.5;
            double xl = double.MaxValue, xr = double.MinValue;
            double l0 = 0, l1 = 0, l2 = 0, l3 = 0, r0 = 0, r1 = 0, r2 = 0, r3 = 0;
            for (int i = 0; i < n; i++)
            {
                int j = i + 1 == n ? 0 : i + 1;
                var a = p[i]; var b = p[j];
                if (a.Y == b.Y) continue;
                if ((sy < a.Y || sy >= b.Y) && (sy < b.Y || sy >= a.Y)) continue;
                double t = (sy - a.Y) / (b.Y - a.Y);
                double x = a.X + (b.X - a.X) * t;
                if (x < xl) { xl = x; l0 = A0[i] + (A0[j] - A0[i]) * t; l1 = A1[i] + (A1[j] - A1[i]) * t; l2 = A2[i] + (A2[j] - A2[i]) * t; l3 = A3[i] + (A3[j] - A3[i]) * t; }
                if (x > xr) { xr = x; r0 = A0[i] + (A0[j] - A0[i]) * t; r1 = A1[i] + (A1[j] - A1[i]) * t; r2 = A2[i] + (A2[j] - A2[i]) * t; r3 = A3[i] + (A3[j] - A3[i]) * t; }
            }
            if (xr < xl) { hasPrev = false; continue; }
            int x0 = Math.Max(0, (int)Math.Ceiling(xl - 0.5));
            int x1 = Math.Min(fbw - 1, (int)Math.Ceiling(xr - 0.5) - 1);
            if (x1 < x0) { hasPrev = false; continue; }
            if (y < yMin) { hasPrev = true; pxl = xl; pxr = xr; p0l = l0; p1l = l1; p2l = l2; p0r = r0; p1r = r1; p2r = r2; continue; }
            double span = xr - xl, inv = span > 0 ? 1 / span : 0;
            int row = y * fbw;

            if (tex == null && !anyFog && !shaded)
            {
                px.Slice(row + x0, x1 - x0 + 1).Fill(flatMem);
            }
            else
            {
                double rho2x = 0;
                for (int xa = x0; xa <= x1; xa += Run)
                {
                    int xb = Math.Min(xa + Run - 1, x1), cnt = xb - xa + 1;
                    double ta = (xa + 0.5 - xl) * inv, tb = (xb + 0.5 - xl) * inv;
                    double w0 = l2 + (r2 - l2) * ta, w1 = l2 + (r2 - l2) * tb;

                    // fog weight at both ends of the run (stepped in 24.8)
                    int fw = constFog << 8, dfw = 0;
                    if (fogTab != null)
                    {
                        int f0 = FogAt(fogTab, 2 / w0 + fogOff), f1 = cnt > 1 ? FogAt(fogTab, 2 / w1 + fogOff) : f0;
                        fw = f0 << 8; dfw = cnt > 1 ? ((f1 - f0) << 8) / (cnt - 1) : 0;
                    }

                    // Gouraud: light multiplier (24.8) at both ends of the run, stepped linearly
                    int lm = 256 << 8, dlm = 0;
                    if (shaded)
                    {
                        int m0 = LightFix(l3 + (r3 - l3) * ta), m1 = cnt > 1 ? LightFix(l3 + (r3 - l3) * tb) : m0;
                        lm = m0 << 8; dlm = cnt > 1 ? ((m1 - m0) << 8) / (cnt - 1) : 0;
                    }

                    if (tex == null)
                    {
                        for (int x = xa; x <= xb; x++, fw += dfw, lm += dlm)
                            px[row + x] = FogMix(shaded ? Lit(flatMem, lm >> 8) : flatMem, fw >> 8, fogRB, fogG);
                        continue;
                    }

                    double u0 = l0 + (r0 - l0) * ta, v0 = l1 + (r1 - l1) * ta, u1 = l0 + (r0 - l0) * tb, v1 = l1 + (r1 - l1) * tb;
                    if (persp) { double i0 = 1 / w0, i1 = 1 / w1; u0 *= i0; v0 *= i0; u1 *= i1; v1 *= i1; }
                    double dux = cnt > 1 ? (u1 - u0) / (cnt - 1) : 0, dvx = cnt > 1 ? (v1 - v0) / (cnt - 1) : 0;

                    int lo = 0, mixW = 0;
                    if (filter == TextureFilter.Trilinear)
                    {
                        double rx = dux * dux + dvx * dvx;
                        if (cnt > 1) rho2x = rx; else rx = rho2x;
                        double ry = 0;
                        if (hasPrev)
                        {
                            double tp = pxr > pxl ? Math.Clamp((xa + 0.5 - pxl) / (pxr - pxl), 0, 1) : 0;
                            double up = p0l + (p0r - p0l) * tp, vp = p1l + (p1r - p1l) * tp;
                            if (persp) { double ip = 1 / (p2l + (p2r - p2l) * tp); up *= ip; vp *= ip; }
                            double ddu = u0 - up, ddv = v0 - vp;
                            ry = ddu * ddu + ddv * ddv;
                        }
                        double rho2 = Math.Max(rx, ry);
                        if (rho2 > 1)
                        {
                            double lod = 0.5 * Math.Log2(rho2) + lodBias;
                            if (lod > 0)
                            {
                                if (lod >= chain!.Count - 1) lo = chain.Count - 1;
                                else { lo = (int)lod; mixW = (int)((lod - lo) * 256); if (mixW < 6) mixW = 0; else if (mixW > 250) { lo++; mixW = 0; } }
                            }
                        }
                        else if (lodBias > 0) lo = Math.Min((int)lodBias, chain!.Count - 1);
                    }

                    var ra = new RunArgs
                    {
                        Lev = chain!.Levels[lo], Lw = chain.Widths[lo], Lh = chain.Heights[lo],
                        Sh = lo,                                    // level lo texel = level 0 coordinate >> lo
                        Su = ToFix(u0), Sv = ToFix(v0),
                        Dsu = (int)Math.Clamp(dux * 65536, -1e9, 1e9), Dsv = (int)Math.Clamp(dvx * 65536, -1e9, 1e9),
                        MixW = mixW, Fw = fw, Dfw = dfw, Lm = lm, Dlm = dlm, FogRB = fogRB, FogG = fogG,
                    };
                    if (mixW > 0) { ra.Lev2 = chain.Levels[lo + 1]; ra.Lw2 = chain.Widths[lo + 1]; ra.Lh2 = chain.Heights[lo + 1]; }
                    bool bil = filter != TextureFilter.Nearest;
                    DispatchRun(px.Slice(row + xa, cnt), ref ra, bil, mixW > 0, alphaChain, shaded, anyFog, !ClampAlways && Interior(in ra, cnt, bil));
                }
            }
            hasPrev = true; pxl = xl; pxr = xr; p0l = l0; p1l = l1; p2l = l2; p0r = r0; p1r = r1; p2r = r2;
        }
    }

    /// <summary>Tests only: always take the clamping pixel loops (the clamp-free ones must give the same pixels).</summary>
    internal static bool ClampAlways;

    /// <summary>Everything the pixel loop of one textured run needs (level(s), 16.16 texture coordinates and steps, fog and light in 24.8).</summary>
    internal struct RunArgs
    {
        public uint[] Lev; public int Lw, Lh, Sh;
        public uint[]? Lev2; public int Lw2, Lh2, MixW;
        public int Su, Sv, Dsu, Dsv, Fw, Dfw, Lm, Dlm;
        public uint FogRB, FogG;
    }

    /// <summary>Whether every texel the run reads lies inside its level(s), so the sampling needs no clamping. The coordinates are linear along the
    /// run, so checking both ends (in 64-bit, which also rules out a wrapped 32-bit coordinate) covers every pixel.</summary>
    internal static bool Interior(in RunArgs a, int cnt, bool bilinear)
    {
        long suE = a.Su + (long)(cnt - 1) * a.Dsu, svE = a.Sv + (long)(cnt - 1) * a.Dsv;
        if (suE is < int.MinValue or > int.MaxValue || svE is < int.MinValue or > int.MaxValue) return false;
        int su1 = (int)suE, sv1 = (int)svE;
        if (a.Lev.Length < (long)a.Lw * a.Lh) return false;
        if (!bilinear) return InRange(a.Su >> 16, su1 >> 16, a.Lw - 1) && InRange(a.Sv >> 16, sv1 >> 16, a.Lh - 1);
        if (!BilerpInside(a.Su, su1, a.Sh, a.Lw) || !BilerpInside(a.Sv, sv1, a.Sh, a.Lh)) return false;
        if (a.Lev2 == null) return true;
        return a.Lev2.Length >= (long)a.Lw2 * a.Lh2 && BilerpInside(a.Su, su1, a.Sh + 1, a.Lw2) && BilerpInside(a.Sv, sv1, a.Sh + 1, a.Lh2);
    }

    static bool InRange(long a, long b, int max) => a >= 0 && b >= 0 && a <= max && b <= max;

    /// <summary>The first texel of <see cref="Bilerp"/> lies in 0..size−2 at both ends (so its +1 neighbour is inside too).</summary>
    static bool BilerpInside(int s0, int s1, int sh, int size) =>
        InRange(((long)(s0 >> sh) - 32768) >> 16, ((long)(s1 >> sh) - 32768) >> 16, size - 2);

    interface IFlag { static abstract bool On { get; } }
    struct Yes : IFlag { public static bool On => true; }
    struct No : IFlag { public static bool On => false; }

    // filter × mip blend × alpha × light × fog × clamp-free: each combination gets its own pixel loop (the generic flags are JIT constants)
    static void DispatchRun(Span<uint> dst, ref RunArgs a, bool bil, bool mix, bool alpha, bool shade, bool fog, bool inside)
    { if (bil) D1<Yes>(dst, ref a, mix, alpha, shade, fog, inside); else D1<No>(dst, ref a, false, alpha, shade, fog, inside); }
    static void D1<TB>(Span<uint> dst, ref RunArgs a, bool mix, bool alpha, bool shade, bool fog, bool inside) where TB : struct, IFlag
    { if (mix) D2<TB, Yes>(dst, ref a, alpha, shade, fog, inside); else D2<TB, No>(dst, ref a, alpha, shade, fog, inside); }
    static void D2<TB, TM>(Span<uint> dst, ref RunArgs a, bool alpha, bool shade, bool fog, bool inside) where TB : struct, IFlag where TM : struct, IFlag
    { if (alpha) D3<TB, TM, Yes>(dst, ref a, shade, fog, inside); else D3<TB, TM, No>(dst, ref a, shade, fog, inside); }
    static void D3<TB, TM, TA>(Span<uint> dst, ref RunArgs a, bool shade, bool fog, bool inside)
        where TB : struct, IFlag where TM : struct, IFlag where TA : struct, IFlag
    { if (shade) D4<TB, TM, TA, Yes>(dst, ref a, fog, inside); else D4<TB, TM, TA, No>(dst, ref a, fog, inside); }
    static void D4<TB, TM, TA, TS>(Span<uint> dst, ref RunArgs a, bool fog, bool inside)
        where TB : struct, IFlag where TM : struct, IFlag where TA : struct, IFlag where TS : struct, IFlag
    { if (fog) D5<TB, TM, TA, TS, Yes>(dst, ref a, inside); else D5<TB, TM, TA, TS, No>(dst, ref a, inside); }
    static void D5<TB, TM, TA, TS, TF>(Span<uint> dst, ref RunArgs a, bool inside)
        where TB : struct, IFlag where TM : struct, IFlag where TA : struct, IFlag where TS : struct, IFlag where TF : struct, IFlag
    { if (inside) PixelRun<TB, TM, TA, TS, TF, Yes>(dst, ref a); else PixelRun<TB, TM, TA, TS, TF, No>(dst, ref a); }

    /// <summary>The pixel loop of one textured run: TB bilinear (else nearest), TM blend with the next mip level, TA alpha mask, TS Gouraud light,
    /// TF fog, TI every texel inside the level (no clamping, see <see cref="Interior"/>). The same integer operations as before the split.</summary>
    static void PixelRun<TB, TM, TA, TS, TF, TI>(Span<uint> dst, ref RunArgs a)
        where TB : struct, IFlag where TM : struct, IFlag where TA : struct, IFlag where TS : struct, IFlag where TF : struct, IFlag where TI : struct, IFlag
    {
        uint[] lev = a.Lev; int lw = a.Lw, lh = a.Lh, sh = a.Sh;
        uint[]? lev2 = a.Lev2; int lw2 = a.Lw2, lh2 = a.Lh2, mixW = a.MixW;
        int su = a.Su, sv = a.Sv, dsu = a.Dsu, dsv = a.Dsv, fw = a.Fw, dfw = a.Dfw, lm = a.Lm, dlm = a.Dlm;
        uint fogRB = a.FogRB, fogG = a.FogG;
        for (int k = 0; k < dst.Length; k++, su += dsu, sv += dsv, fw += dfw, lm += dlm)
        {
            uint c;
            if (!TB.On)
            {
                if (TI.On) c = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(lev), (sv >> 16) * lw + (su >> 16));
                else
                {
                    int iu = Math.Clamp(su >> 16, 0, lw - 1), iv = Math.Clamp(sv >> 16, 0, lh - 1);
                    c = lev[iv * lw + iu];
                }
            }
            else if (TI.On)
            {
                c = BilerpIn(lev, lw, su >> sh, sv >> sh);
                if (TM.On) c = Mix(c, BilerpIn(lev2!, lw2, su >> (sh + 1), sv >> (sh + 1)), mixW);
            }
            else
            {
                c = Bilerp(lev, lw, lh, su >> sh, sv >> sh);
                if (TM.On) c = Mix(c, Bilerp(lev2!, lw2, lh2, su >> (sh + 1), sv >> (sh + 1)), mixW);
            }
            if (TA.On)
            {
                uint al = c >> 24;
                if (al < 128) continue;
                if (al != 255)
                {
                    int iv2 = InvAlpha[al];
                    uint r = Math.Min(255u, (uint)(((c & 255) * iv2 + 32768) >> 16));
                    uint g = Math.Min(255u, (uint)((((c >> 8) & 255) * iv2 + 32768) >> 16));
                    uint b = Math.Min(255u, (uint)((((c >> 16) & 255) * iv2 + 32768) >> 16));
                    c = r | g << 8 | b << 16 | 0xFF000000u;
                }
            }
            if (TS.On) c = Lit(c, lm >> 8);
            dst[k] = TF.On && (fw >> 8) > 0 ? FogMix(c, fw >> 8, fogRB, fogG) : c | 0xFF000000u;
        }
    }

    /// <summary><see cref="Bilerp"/> for coordinates known to lie inside the level (<see cref="Interior"/>): no clamping, no bounds checks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint BilerpIn(uint[] t, int w, int su, int sv)
    {
        int fu = su - 32768, fv = sv - 32768;
        int x0 = fu >> 16, y0 = fv >> 16;
        int wx = (fu >> 8) & 255, wy = (fv >> 8) & 255;
        ref uint p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), y0 * w + x0);
        uint c00 = p, c10 = Unsafe.Add(ref p, 1), c01 = Unsafe.Add(ref p, w), c11 = Unsafe.Add(ref p, w + 1);
        int w11 = (wx * wy) >> 8, w10 = wx - w11, w01 = wy - w11, w00 = 256 - wx - wy + w11;
        uint rb = ((c00 & RB) * (uint)w00 + (c10 & RB) * (uint)w10 + (c01 & RB) * (uint)w01 + (c11 & RB) * (uint)w11) >> 8 & RB;
        uint ga = (((c00 >> 8) & RB) * (uint)w00 + ((c10 >> 8) & RB) * (uint)w10 + ((c01 >> 8) & RB) * (uint)w01 + ((c11 >> 8) & RB) * (uint)w11) & 0xFF00FF00u;
        return rb | ga;
    }

    /// <summary>Light intensity → multiplier in 8.8 (256 = 1.0), limited to 0..2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int LightFix(double l) => (int)(Math.Clamp(l, 0, 2) * 256 + 0.5);

    /// <summary>Colour times <paramref name="m"/>/256 per channel, clamped to 255 (alpha untouched).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Lit(uint c, int m)
    {
        uint r = (uint)((c & 255) * m) >> 8, g = (uint)(((c >> 8) & 255) * m) >> 8, b = (uint)(((c >> 16) & 255) * m) >> 8;
        if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255;
        return r | g << 8 | b << 16 | (c & 0xFF000000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ToFix(double v) => (int)(Math.Clamp(v, -30000, 30000) * 65536);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FogAt(int[] tab, double f)
    {
        if (f <= 0) return tab[0];
        if (f >= tab.Length - 1) return tab[^1];
        int i = (int)f, fr = (int)((f - i) * 256);
        return tab[i] + (((tab[i + 1] - tab[i]) * fr) >> 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint FogMix(uint c, int wt, uint fogRB, uint fogG)
    {
        if (wt <= 0) return c | 0xFF000000u;
        int iw = 256 - wt;
        uint rb = (((c & RB) * (uint)iw) + fogRB * (uint)wt) >> 8 & RB;
        uint g = (((c & 0xFF00) * (uint)iw) + fogG * (uint)wt) >> 8 & 0xFF00;
        return rb | g | 0xFF000000u;
    }

    /// <summary>Blend of two packed colours: <paramref name="w"/>/256 of <paramref name="b"/> (all four channels).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Mix(uint a, uint b, int w)
    {
        uint iw = (uint)(256 - w), ww = (uint)w;
        uint rb = ((a & RB) * iw + (b & RB) * ww) >> 8 & RB;
        uint ga = (((a >> 8) & RB) * iw + ((b >> 8) & RB) * ww) & 0xFF00FF00u;
        return rb | ga;
    }

    /// <summary>Bilinear sample at 16.16 texel coordinates (texel centres at +0.5), edges clamped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Bilerp(uint[] t, int w, int h, int su, int sv)
    {
        int fu = su - 32768, fv = sv - 32768;
        int x0 = fu >> 16, y0 = fv >> 16;
        int wx = (fu >> 8) & 255, wy = (fv >> 8) & 255;
        int x1 = x0 + 1, y1 = y0 + 1;
        uint c00, c10, c01, c11;
        if ((uint)x0 < (uint)(w - 1) && (uint)y0 < (uint)(h - 1))
        {
            int i0 = y0 * w + x0;
            c00 = t[i0]; c10 = t[i0 + 1]; c01 = t[i0 + w]; c11 = t[i0 + w + 1];
        }
        else
        {
            x0 = x0 < 0 ? 0 : x0 >= w ? w - 1 : x0; x1 = x1 < 0 ? 0 : x1 >= w ? w - 1 : x1;
            y0 = y0 < 0 ? 0 : y0 >= h ? h - 1 : y0; y1 = y1 < 0 ? 0 : y1 >= h ? h - 1 : y1;
            c00 = t[y0 * w + x0]; c10 = t[y0 * w + x1]; c01 = t[y1 * w + x0]; c11 = t[y1 * w + x1];
        }
        int w11 = (wx * wy) >> 8, w10 = wx - w11, w01 = wy - w11, w00 = 256 - wx - wy + w11;
        uint rb = ((c00 & RB) * (uint)w00 + (c10 & RB) * (uint)w10 + (c01 & RB) * (uint)w01 + (c11 & RB) * (uint)w11) >> 8 & RB;
        uint ga = (((c00 >> 8) & RB) * (uint)w00 + ((c10 >> 8) & RB) * (uint)w10 + ((c01 >> 8) & RB) * (uint)w01 + ((c11 >> 8) & RB) * (uint)w11) & 0xFF00FF00u;
        return rb | ga;
    }
}
