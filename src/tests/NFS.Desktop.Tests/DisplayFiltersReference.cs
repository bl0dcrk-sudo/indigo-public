using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Frozen copies of the display filters and of Compositor.Compose / UpscaleNearest as they were before the task 08i rewrite (per-call float planes,
/// byte-wise compose). The tests compare the reusable-buffer implementation with them pixel for pixel.</summary>
public static class DisplayFiltersOld
{
    /// <summary>Scales <paramref name="src"/> to <paramref name="dstW"/>×<paramref name="dstH"/> with the filter <paramref name="kind"/>; <see cref="DisplayFilterKind.Sharp"/>
    /// is the GPU path, here it is a plain bilinear scale (reference for tests).</summary>
    public static FrameBuffer Apply(DisplayFilterKind kind, FrameBuffer src, int dstW, int dstH) => kind switch
    {
        DisplayFilterKind.Fsr => Rcas(Easu(src, dstW, dstH)),
        DisplayFilterKind.Crt => Crt(src, dstW, dstH),
        _ => Bilinear(src, dstW, dstH),
    };

    static float[] ToPlanes(FrameBuffer fb)
    {
        var f = new float[fb.Width * fb.Height * 3];
        var px = fb.Rgba;
        for (int i = 0, o = 0; i < f.Length; i += 3, o += 4) { f[i] = px[o] / 255f; f[i + 1] = px[o + 1] / 255f; f[i + 2] = px[o + 2] / 255f; }
        return f;
    }

    static byte Byte(float v) => (byte)(v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255f + 0.5f));

    // ---- bilinear (reference) ----

    public static FrameBuffer Bilinear(FrameBuffer src, int dstW, int dstH)
    {
        var s = ToPlanes(src);
        var dst = new FrameBuffer(dstW, dstH);
        int sw = src.Width, sh = src.Height;
        Parallel.For(0, dstH, y =>
        {
            float sy = (y + 0.5f) * sh / dstH - 0.5f;
            int y0 = (int)MathF.Floor(sy); float fy = sy - y0;
            int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
            for (int x = 0; x < dstW; x++)
            {
                float sx = (x + 0.5f) * sw / dstW - 0.5f;
                int x0 = (int)MathF.Floor(sx); float fx = sx - x0;
                int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                int o = (y * dstW + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float top = s[(ya * sw + xa) * 3 + c] * (1 - fx) + s[(ya * sw + xb) * 3 + c] * fx;
                    float bot = s[(yb * sw + xa) * 3 + c] * (1 - fx) + s[(yb * sw + xb) * 3 + c] * fx;
                    dst.Rgba[o + c] = Byte(top * (1 - fy) + bot * fy);
                }
                dst.Rgba[o + 3] = 255;
            }
        });
        return dst;
    }

    // ---- FSR 1: EASU ----

    /// <summary>FSR 1 EASU (edge adaptive spatial upsampling): for every output pixel the gradient direction and strength are estimated from the four source
    /// texels around it (12 taps, luma 0.5R + G + 0.5B), and a directional, stretched Lanczos-like kernel is evaluated over the taps; the result is clamped
    /// to the range of the 4 nearest texels (no ringing). Sharper diagonal edges than bilinear, never invents detail.</summary>
    public static FrameBuffer Easu(FrameBuffer src, int dstW, int dstH)
    {
        var s = ToPlanes(src);
        var dst = new FrameBuffer(dstW, dstH);
        int sw = src.Width, sh = src.Height;
        var lum = new float[sw * sh];
        for (int i = 0; i < lum.Length; i++) lum[i] = 0.5f * s[i * 3] + s[i * 3 + 1] + 0.5f * s[i * 3 + 2];
        // tap offsets relative to f:  b c / e f g h / i j k l / n o
        ReadOnlySpan<int> tx = [0, 1, -1, 0, 1, 2, -1, 0, 1, 2, 0, 1];
        ReadOnlySpan<int> ty = [-1, -1, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2];
        var txa = tx.ToArray(); var tya = ty.ToArray();
        Parallel.For(0, dstH, y =>
        {
            Span<float> tl = stackalloc float[12];
            Span<int> ti = stackalloc int[12];
            float sy = (y + 0.5f) * sh / dstH - 0.5f;
            int iy = (int)MathF.Floor(sy); float ppy = sy - iy;
            for (int x = 0; x < dstW; x++)
            {
                float sx = (x + 0.5f) * sw / dstW - 0.5f;
                int ix = (int)MathF.Floor(sx); float ppx = sx - ix;
                for (int t = 0; t < 12; t++)
                {
                    int cx = Math.Clamp(ix + txa[t], 0, sw - 1), cy = Math.Clamp(iy + tya[t], 0, sh - 1);
                    ti[t] = cy * sw + cx; tl[t] = lum[ti[t]];
                }
                // indices: 0 b, 1 c, 2 e, 3 f, 4 g, 5 h, 6 i, 7 j, 8 k, 9 l, 10 n, 11 o
                float dirX = 0, dirY = 0, len = 0;
                SetF(ref dirX, ref dirY, ref len, (1 - ppx) * (1 - ppy), tl[0], tl[2], tl[3], tl[4], tl[7]);
                SetF(ref dirX, ref dirY, ref len, ppx * (1 - ppy), tl[1], tl[3], tl[4], tl[5], tl[8]);
                SetF(ref dirX, ref dirY, ref len, (1 - ppx) * ppy, tl[3], tl[6], tl[7], tl[8], tl[10]);
                SetF(ref dirX, ref dirY, ref len, ppx * ppy, tl[4], tl[7], tl[8], tl[9], tl[11]);
                float dirR = dirX * dirX + dirY * dirY;
                bool zro = dirR < 1f / 32768f;
                dirR = zro ? 1f : 1f / MathF.Sqrt(dirR);
                if (zro) dirX = 1f;
                dirX *= dirR; dirY *= dirR;
                len = len * 0.5f; len *= len;
                float stretch = (dirX * dirX + dirY * dirY) / MathF.Max(MathF.Abs(dirX), MathF.Abs(dirY));
                float len2x = 1f + (stretch - 1f) * len, len2y = 1f - 0.5f * len;
                float lob = 0.5f + ((1f / 4f - 0.04f) - 0.5f) * len, clp = 1f / lob;
                float r = 0, g = 0, b = 0, wsum = 0;
                for (int t = 0; t < 12; t++)
                {
                    float ox = txa[t] - ppx, oy = tya[t] - ppy;
                    float vx = (ox * dirX + oy * dirY) * len2x, vy = (ox * -dirY + oy * dirX) * len2y;
                    float d2 = MathF.Min(vx * vx + vy * vy, clp);
                    float wB = 2f / 5f * d2 - 1f, wA = lob * d2 - 1f;
                    wB *= wB; wA *= wA;
                    wB = 25f / 16f * wB - (25f / 16f - 1f);
                    float w = wB * wA;
                    int p = ti[t] * 3;
                    r += s[p] * w; g += s[p + 1] * w; b += s[p + 2] * w; wsum += w;
                }
                float inv = 1f / wsum;
                r *= inv; g *= inv; b *= inv;
                int o = (y * dstW + x) * 4;
                // anti-ringing: stay inside the 4 nearest texels (f g j k)
                dst.Rgba[o] = Byte(Math.Clamp(r, Min4(s, ti, 0), Max4(s, ti, 0)));
                dst.Rgba[o + 1] = Byte(Math.Clamp(g, Min4(s, ti, 1), Max4(s, ti, 1)));
                dst.Rgba[o + 2] = Byte(Math.Clamp(b, Min4(s, ti, 2), Max4(s, ti, 2)));
                dst.Rgba[o + 3] = 255;
            }
        });
        return dst;
    }

    static float Min4(float[] s, ReadOnlySpan<int> ti, int c) => MathF.Min(MathF.Min(s[ti[3] * 3 + c], s[ti[4] * 3 + c]), MathF.Min(s[ti[7] * 3 + c], s[ti[8] * 3 + c]));
    static float Max4(float[] s, ReadOnlySpan<int> ti, int c) => MathF.Max(MathF.Max(s[ti[3] * 3 + c], s[ti[4] * 3 + c]), MathF.Max(s[ti[7] * 3 + c], s[ti[8] * 3 + c]));

    /// <summary>FsrEasuSetF: gradient direction and "edge-ness" contributed by one of the four central texels (up, left, centre, right, down luma), weighted by its bilinear weight.</summary>
    static void SetF(ref float dirX, ref float dirY, ref float len, float w, float lUp, float lLeft, float lC, float lRight, float lDown)
    {
        float lenX = MathF.Max(MathF.Abs(lRight - lC), MathF.Abs(lC - lLeft)), dX = lRight - lLeft;
        dirX += dX * w;
        lenX = lenX > 0 ? Math.Clamp(MathF.Abs(dX) / lenX, 0f, 1f) : 0; lenX *= lenX; len += lenX * w;
        float lenY = MathF.Max(MathF.Abs(lDown - lC), MathF.Abs(lC - lUp)), dY = lDown - lUp;
        dirY += dY * w;
        lenY = lenY > 0 ? Math.Clamp(MathF.Abs(dY) / lenY, 0f, 1f) : 0; lenY *= lenY; len += lenY * w;
    }

    // ---- FSR 1: RCAS ----

    /// <summary>Largest negative lobe of RCAS.</summary>
    public const float RcasLimit = 0.1875f;

    /// <summary>FSR 1 RCAS (robust contrast adaptive sharpening): out = (lobe·(up+left+right+down) + centre) / (4·lobe + 1) per channel with a negative lobe
    /// that is limited so that the result cannot leave 0..1 (the strongest lobe that does not clip given the 4 neighbours). <paramref name="sharpness"/> in
    /// stops (0 = full strength, 2 = weakest): the lobe is scaled by 2^−sharpness.</summary>
    public static FrameBuffer Rcas(FrameBuffer src, float sharpness = 0.25f)
    {
        var s = ToPlanes(src);
        var dst = new FrameBuffer(src.Width, src.Height);
        int w = src.Width, h = src.Height;
        float con = MathF.Pow(2f, -sharpness);
        Parallel.For(0, h, y =>
        {
            int ya = Math.Max(y - 1, 0), yb = Math.Min(y + 1, h - 1);
            for (int x = 0; x < w; x++)
            {
                int xa = Math.Max(x - 1, 0), xb = Math.Min(x + 1, w - 1);
                int ib = (ya * w + x) * 3, id = (y * w + xa) * 3, ie = (y * w + x) * 3, iff = (y * w + xb) * 3, ih = (yb * w + x) * 3;
                float lobe = float.MinValue;
                for (int c = 0; c < 3; c++)
                {
                    float mn = MathF.Min(MathF.Min(s[ib + c], s[id + c]), MathF.Min(s[iff + c], s[ih + c]));
                    float mx = MathF.Max(MathF.Max(s[ib + c], s[id + c]), MathF.Max(s[iff + c], s[ih + c]));
                    float hitMin = mn / MathF.Max(4f * mx, 1e-6f);
                    float hitMax = (1f - mx) / MathF.Min(4f * mn - 4f, -1e-6f);
                    lobe = MathF.Max(lobe, MathF.Max(-hitMin, hitMax));
                }
                lobe = MathF.Max(-RcasLimit, MathF.Min(lobe, 0f)) * con;
                float rcp = 1f / (4f * lobe + 1f);
                int o = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                    dst.Rgba[o + c] = Byte((lobe * (s[ib + c] + s[id + c] + s[iff + c] + s[ih + c]) + s[ie + c]) * rcp);
                dst.Rgba[o + 3] = 255;
            }
        });
        return dst;
    }

    // ---- CRT ----

    /// <summary>Strength of the dark gap between scanlines (0 = none, 1 = black between rows).</summary>
    public const float DefaultScanline = 0.42f;
    /// <summary>Strength of the red / green / blue aperture grille (columns of the output).</summary>
    public const float DefaultMask = 0.22f;

    /// <summary>Scanlines + aperture grille: every source row becomes a bright line on the output (cosine profile over the row, gap darkness
    /// <paramref name="scanline"/>), source columns are interpolated linearly, the output columns are tinted R/G/B in turn (<paramref name="mask"/>).
    /// The brightness is compensated so that the mean level stays close to the source. Needs an output of at least 2 rows and 3 columns per source pixel
    /// to look right.</summary>
    public static FrameBuffer Crt(FrameBuffer src, int dstW, int dstH, float scanline = DefaultScanline, float mask = DefaultMask)
    {
        var s = ToPlanes(src);
        var dst = new FrameBuffer(dstW, dstH);
        int sw = src.Width, sh = src.Height;
        // mean of the row profile (1 − s·(1 − p), p = 0.5 + 0.5·cos) is 1 − s/2; the grille keeps (1 + 2(1 − m))/3 of the light
        float gain = 1f / ((1f - scanline * 0.5f) * (1f - mask * 2f / 3f));
        Parallel.For(0, dstH, y =>
        {
            float sy = (y + 0.5f) * sh / dstH;                     // continuous source row coordinate (row r spans r..r+1)
            int row = Math.Clamp((int)sy, 0, sh - 1);
            float frac = sy - MathF.Floor(sy);                        // 0 at the row's top edge, 0.5 at its centre
            float p = 0.5f + 0.5f * MathF.Cos((frac - 0.5f) * 2f * MathF.PI);
            float beam = 1f - scanline * (1f - p);
            for (int x = 0; x < dstW; x++)
            {
                float sx = (x + 0.5f) * sw / dstW - 0.5f;
                int x0 = (int)MathF.Floor(sx); float fx = sx - x0;
                int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                int o = (y * dstW + x) * 4, phase = x % 3;
                for (int c = 0; c < 3; c++)
                {
                    float v = s[(row * sw + xa) * 3 + c] * (1 - fx) + s[(row * sw + xb) * 3 + c] * fx;
                    float m = c == phase ? 1f : 1f - mask;
                    dst.Rgba[o + c] = Byte(v * beam * m * gain);
                }
                dst.Rgba[o + 3] = 255;
            }
        });
        return dst;
    }
}

/// <summary>Old byte-wise compositor (reference).</summary>
public static class CompositorOld
{
    /// <summary>Upscales the buffer by <paramref name="factor"/>× (nearest; each pixel → a factor×factor block).</summary>
    public static FrameBuffer UpscaleNearest(FrameBuffer src, int factor)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        var dst = new FrameBuffer(src.Width * factor, src.Height * factor);
        for (int y = 0; y < src.Height; y++)
        {
            int row0 = y * factor * dst.Width * 4;
            for (int x = 0; x < src.Width; x++)
                for (int i = 0; i < factor; i++)
                    Buffer.BlockCopy(src.Rgba, (y * src.Width + x) * 4, dst.Rgba, row0 + (x * factor + i) * 4, 4);
            for (int j = 1; j < factor; j++)
                Buffer.BlockCopy(dst.Rgba, row0, dst.Rgba, row0 + j * dst.Width * 4, dst.Width * 4);
        }
        return dst;
    }

    /// <summary>3D scene (k× larger than the layer) with a 2D layer on top: an overlay pixel with alpha 255 hides the k×k block of the scene, alpha 0 leaves the scene,
    /// intermediate alpha blends. Returns a new buffer of the scene's size.</summary>
    public static FrameBuffer Compose(FrameBuffer scene, FrameBuffer overlay)
    {
        int k = scene.Width / overlay.Width;
        if (k < 1 || scene.Width != overlay.Width * k || scene.Height != overlay.Height * k)
            throw new ArgumentException("scene size must be an integer multiple of the overlay size");
        var dst = new FrameBuffer(scene.Width, scene.Height);
        Buffer.BlockCopy(scene.Rgba, 0, dst.Rgba, 0, scene.Rgba.Length);
        for (int y = 0; y < overlay.Height; y++)
            for (int x = 0; x < overlay.Width; x++)
            {
                int o = (y * overlay.Width + x) * 4, a = overlay.Rgba[o + 3];
                if (a == 0) continue;
                uint rgb = (uint)(overlay.Rgba[o] << 16 | overlay.Rgba[o + 1] << 8 | overlay.Rgba[o + 2]);
                for (int j = 0; j < k; j++)
                    for (int i = 0; i < k; i++)
                    {
                        int px = x * k + i, py = y * k + j;
                        if (a == 255) dst.Set(px, py, rgb); else dst.Blend(px, py, rgb, a);
                    }
            }
        return dst;
    }
}
