using System.Runtime.Intrinsics;

namespace NFS.Render;

/// <summary>How the finished frame is scaled to the window (Enhanced). <see cref="Sharp"/> = the GPU path (integer nearest [ref:3437]
/// enlargement + bilinear, no CPU work); <see cref="Fsr"/> = <see cref="DisplayFilters.Easu"/> + <see cref="DisplayFilters.Rcas"/> on the CPU;
/// <see cref="Crt"/> = <see cref="DisplayFilters.Crt"/> scanlines + shadow mask on the CPU.</summary>
public enum DisplayFilterKind { Sharp, Fsr, Crt }

public static class DisplayFilterKinds
{
    public static DisplayFilterKind? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "sharp" or "bilinear" or "off" or "none" => DisplayFilterKind.Sharp,
        "fsr" or "easu" => DisplayFilterKind.Fsr,
        "crt" or "scanlines" => DisplayFilterKind.Crt,
        _ => null,
    };

    public static string Name(this DisplayFilterKind kind) => kind switch { DisplayFilterKind.Fsr => "fsr", DisplayFilterKind.Crt => "crt", _ => "sharp" };

    public static DisplayFilterKind Next(this DisplayFilterKind kind) => kind switch { DisplayFilterKind.Sharp => DisplayFilterKind.Fsr, DisplayFilterKind.Fsr => DisplayFilterKind.Crt, _ => DisplayFilterKind.Sharp };
}

/// <summary>
/// Reference (CPU) implementations of the display filters, working on <see cref="FrameBuffer"/> so that they are testable and show up in PNG captures
/// (a GPU shader would not). <see cref="Easu"/> / <see cref="Rcas"/> follow AMD FidelityFX Super Resolution 1 (MIT): edge-adaptive 12-tap upscale and
/// contrast-adaptive sharpening, in linear 0..1 float of the stored (gamma) values; <see cref="Crt"/> is a simple scanline + aperture-grille model.
/// All are row-parallel; alpha of the result is 255. The static methods allocate a result the caller owns; a host that filters every frame uses one
/// <see cref="DisplayFilterRunner"/> (same arithmetic, reusable buffers, no allocation per frame).
/// </summary>
public static class DisplayFilters
{
    /// <summary>Scales <paramref name="src"/> to <paramref name="dstW"/>×<paramref name="dstH"/> with the filter <paramref name="kind"/>; <see cref="DisplayFilterKind.Sharp"/>
    /// is the GPU path, here it is a plain bilinear scale (reference for tests).</summary>
    public static FrameBuffer Apply(DisplayFilterKind kind, FrameBuffer src, int dstW, int dstH) => new DisplayFilterRunner().Apply(kind, src, dstW, dstH);

    public static FrameBuffer Bilinear(FrameBuffer src, int dstW, int dstH) => new DisplayFilterRunner().Bilinear(src, dstW, dstH);

    /// <summary>FSR 1 EASU (edge adaptive spatial upsampling): for every output pixel the gradient direction and strength are estimated from the four source
    /// texels around it (12 taps, luma 0.5R + G + 0.5B), and a directional, stretched Lanczos-like kernel is evaluated over the taps; the result is clamped
    /// to the range of the 4 nearest texels (no ringing). Sharper diagonal edges than bilinear, never invents detail.</summary>
    public static FrameBuffer Easu(FrameBuffer src, int dstW, int dstH) => new DisplayFilterRunner().Easu(src, dstW, dstH);

    /// <summary>Largest negative lobe of RCAS.</summary>
    public const float RcasLimit = 0.1875f;

    /// <summary>FSR 1 RCAS (robust contrast adaptive sharpening): out = (lobe·(up+left+right+down) + centre) / (4·lobe + 1) per channel with a negative lobe
    /// that is limited so that the result cannot leave 0..1 (the strongest lobe that does not clip given the 4 neighbours). <paramref name="sharpness"/> in
    /// stops (0 = full strength, 2 = weakest): the lobe is scaled by 2^−sharpness.</summary>
    public static FrameBuffer Rcas(FrameBuffer src, float sharpness = 0.25f) => new DisplayFilterRunner().Rcas(src, sharpness);

    /// <summary>Strength of the dark gap between scanlines (0 = none, 1 = black between rows).</summary>
    public const float DefaultScanline = 0.42f;
    /// <summary>Strength of the red / green / blue aperture grille (columns of the output).</summary>
    public const float DefaultMask = 0.22f;

    /// <summary>Scanlines + aperture grille: every source row becomes a bright line on the output (cosine profile over the row, gap darkness
    /// <paramref name="scanline"/>), source columns are interpolated linearly, the output columns are tinted R/G/B in turn (<paramref name="mask"/>).
    /// The brightness is compensated so that the mean level stays close to the source. Needs an output of at least 2 rows and 3 columns per source pixel
    /// to look right.</summary>
    public static FrameBuffer Crt(FrameBuffer src, int dstW, int dstH, float scanline = DefaultScanline, float mask = DefaultMask) =>
        new DisplayFilterRunner().Crt(src, dstW, dstH, scanline, mask);
}

/// <summary>
/// The display filters with reusable state: the result buffers, the luma plane and the per-column tables are kept between calls and the rows are handed
/// to the thread pool through pre-created work items, so filtering a frame allocates nothing once the sizes repeat (test <c>DisplayFilterRunnerTests</c>).
/// The arithmetic is exactly that of the old per-call implementation (float operations in the same order; the 8-bit → float conversion is a 256-entry
/// table of the very same <c>v / 255f</c> values), so the pixels are bit-identical to it. The returned buffer belongs to the runner and is overwritten by
/// the next call that needs it: two buffers alternate, so <c>Rcas(Easu(x))</c> and <c>Apply</c> work, but keep the result only until the next frame.
/// Not thread-safe (one runner per filtering thread).
/// </summary>
public sealed class DisplayFilterRunner
{
    /// <summary>Upper bound of worker threads; 0 = all processors, 1 = everything on the calling thread.</summary>
    public int MaxThreads { get; set; }

    const int Block = 8;
    enum Op { Lum, Bilinear, Easu, Rcas, Crt }

    static readonly float[] Lut = BuildLut();
    static float[] BuildLut()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = i / 255f;
        return t;
    }

    static byte Byte(float v) => (byte)(v <= 0 ? 0 : v >= 1 ? 255 : (int)(v * 255f + 0.5f));
    static uint Pack(float r, float g, float b) => Byte(r) | (uint)Byte(g) << 8 | (uint)Byte(b) << 16 | 0xFF000000u;

    FrameBuffer? _a, _b;
    float[] _lum = [], _planes = [];
    int[] _ix = [];
    float[] _fx = [], _ofx = [];

    // the job being run (set by Run, read by the workers)
    Op _op;
    FrameBuffer _src = null!, _dst = null!;
    int _sw, _sh, _dw, _dh, _rows, _next;
    float _p0, _p1, _p2;
    Exception? _error;
    Helper[] _helpers = [];
    readonly SemaphoreSlim _finished = new(0);

    public FrameBuffer Apply(DisplayFilterKind kind, FrameBuffer src, int dstW, int dstH) => kind switch
    {
        DisplayFilterKind.Fsr => Rcas(Easu(src, dstW, dstH)),
        DisplayFilterKind.Crt => Crt(src, dstW, dstH),
        _ => Bilinear(src, dstW, dstH),
    };

    /// <summary>A result buffer of the given size that is not <paramref name="src"/>.</summary>
    FrameBuffer Out(FrameBuffer src, int w, int h)
    {
        if (!ReferenceEquals(_a, src)) { if (_a == null || _a.Width != w || _a.Height != h) _a = new FrameBuffer(w, h); return _a; }
        if (_b == null || _b.Width != w || _b.Height != h) _b = new FrameBuffer(w, h);
        return _b;
    }

    void Setup(FrameBuffer src, FrameBuffer dst)
    {
        _src = src; _dst = dst; _sw = src.Width; _sh = src.Height; _dw = dst.Width; _dh = dst.Height;
    }

    /// <summary>Per output column: floor of the source coordinate and its fraction (and 1 − fraction), the same expressions as the per-pixel original.</summary>
    void ColumnTables()
    {
        if (_ix.Length < _dw) { _ix = new int[_dw]; _fx = new float[_dw]; _ofx = new float[_dw]; }
        int sw = _sw, dw = _dw;
        for (int x = 0; x < dw; x++)
        {
            float sx = (x + 0.5f) * sw / dw - 0.5f;
            int x0 = (int)MathF.Floor(sx); float fx = sx - x0;
            _ix[x] = x0; _fx[x] = fx; _ofx[x] = 1 - fx;
        }
    }

    public FrameBuffer Bilinear(FrameBuffer src, int dstW, int dstH)
    {
        var dst = Out(src, dstW, dstH);
        Setup(src, dst);
        ColumnTables();
        Run(Op.Bilinear, dstH);
        return dst;
    }

    public FrameBuffer Easu(FrameBuffer src, int dstW, int dstH)
    {
        var dst = Out(src, dstW, dstH);
        Setup(src, dst);
        if (_lum.Length < _sw * _sh) { _lum = new float[_sw * _sh]; _planes = new float[_sw * _sh * 3]; }
        ColumnTables();
        Run(Op.Lum, _sh);
        Run(Op.Easu, dstH);
        return dst;
    }

    public FrameBuffer Rcas(FrameBuffer src, float sharpness = 0.25f)
    {
        var dst = Out(src, src.Width, src.Height);
        Setup(src, dst);
        _p0 = MathF.Pow(2f, -sharpness);
        Run(Op.Rcas, _sh);
        return dst;
    }

    public FrameBuffer Crt(FrameBuffer src, int dstW, int dstH, float scanline = DisplayFilters.DefaultScanline, float mask = DisplayFilters.DefaultMask)
    {
        var dst = Out(src, dstW, dstH);
        Setup(src, dst);
        ColumnTables();
        _p0 = scanline; _p1 = mask;
        // mean of the row profile (1 − s·(1 − p), p = 0.5 + 0.5·cos) is 1 − s/2; the grille keeps (1 + 2(1 − m))/3 of the light
        _p2 = 1f / ((1f - scanline * 0.5f) * (1f - mask * 2f / 3f));
        Run(Op.Crt, dstH);
        return dst;
    }

    // ---- row scheduling (work items and the semaphore are kept: nothing is allocated per call) ----

    sealed class Helper(DisplayFilterRunner owner) : IThreadPoolWorkItem
    {
        public void Execute()
        {
            try { owner.RunBlocks(); }
            finally { owner._finished.Release(); }
        }
    }

    void Run(Op op, int rows)
    {
        _op = op; _rows = rows; _next = 0; _error = null;
        int threads = MaxThreads <= 0 ? Environment.ProcessorCount : MaxThreads;
        int blocks = (rows + Block - 1) / Block;
        int helpers = Math.Min(threads, blocks) - 1;
        if (helpers <= 0) { RunBlocks(); Rethrow(); return; }
        if (_helpers.Length < helpers)
        {
            int old = _helpers.Length;
            Array.Resize(ref _helpers, helpers);
            for (int i = old; i < helpers; i++) _helpers[i] = new Helper(this);
        }
        for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
        RunBlocks();
        for (int i = 0; i < helpers; i++) _finished.Wait();
        Rethrow();
    }

    void Rethrow()
    {
        if (_error == null) return;
        var e = _error; _error = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
    }

    void RunBlocks()
    {
        try
        {
            for (int b; (b = Interlocked.Increment(ref _next) - 1) * Block < _rows;)
            {
                int y0 = b * Block, y1 = Math.Min(_rows, y0 + Block);
                for (int y = y0; y < y1; y++)
                    switch (_op)
                    {
                        case Op.Lum: LumRow(y); break;
                        case Op.Bilinear: BilinearRow(y); break;
                        case Op.Easu: EasuRow(y); break;
                        case Op.Rcas: RcasRow(y); break;
                        default: CrtRow(y); break;
                    }
            }
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _error, e, null);
            Volatile.Write(ref _next, int.MaxValue / 2);
        }
    }

    // ---- rows ----

    /// <summary>Source row → float planes (RGB, <c>v / 255f</c>) and luma plane; EASU reads these instead of looking the bytes up per tap.</summary>
    void LumRow(int y)
    {
        var s = _src.Rgba; var lut = Lut; var lum = _lum; var pl = _planes;
        int i = y * _sw, o = i * 4;
        for (int x = 0; x < _sw; x++, i++, o += 4)
        {
            float r = lut[s[o]], g = lut[s[o + 1]], b = lut[s[o + 2]];
            pl[i * 3] = r; pl[i * 3 + 1] = g; pl[i * 3 + 2] = b;
            lum[i] = 0.5f * r + g + 0.5f * b;
        }
    }

    void BilinearRow(int y)
    {
        var s = _src.Rgba; var lut = Lut;
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_dst.Rgba.AsSpan(y * _dw * 4, _dw * 4));
        int sw = _sw, sh = _sh;
        float sy = (y + 0.5f) * sh / _dh - 0.5f;
        int y0 = (int)MathF.Floor(sy); float fy = sy - y0, ofy = 1 - fy;
        int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
        for (int x = 0; x < d.Length; x++)
        {
            float fx = _fx[x], ofx = _ofx[x];
            int x0 = _ix[x];
            int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
            int pa = (ya * sw + xa) * 4, pb = (ya * sw + xb) * 4, pc = (yb * sw + xa) * 4, pd = (yb * sw + xb) * 4;
            float top0 = lut[s[pa]] * ofx + lut[s[pb]] * fx, bot0 = lut[s[pc]] * ofx + lut[s[pd]] * fx;
            float top1 = lut[s[pa + 1]] * ofx + lut[s[pb + 1]] * fx, bot1 = lut[s[pc + 1]] * ofx + lut[s[pd + 1]] * fx;
            float top2 = lut[s[pa + 2]] * ofx + lut[s[pb + 2]] * fx, bot2 = lut[s[pc + 2]] * ofx + lut[s[pd + 2]] * fx;
            d[x] = Pack(top0 * ofy + bot0 * fy, top1 * ofy + bot1 * fy, top2 * ofy + bot2 * fy);
        }
    }

    // tap offsets relative to f:  b c / e f g h / i j k l / n o
    static ReadOnlySpan<sbyte> Tx => [0, 1, -1, 0, 1, 2, -1, 0, 1, 2, 0, 1];
    static ReadOnlySpan<sbyte> Ty => [-1, -1, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2];

    void EasuRow(int y)
    {
        var pl = _planes; var lumPlane = _lum;
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_dst.Rgba.AsSpan(y * _dw * 4, _dw * 4));
        int sw = _sw, sh = _sh;
        var txa = Tx; var tya = Ty;
        Span<float> tl = stackalloc float[12];
        Span<int> ti = stackalloc int[12];
        Span<float> wt = stackalloc float[12];
        float sy = (y + 0.5f) * sh / _dh - 0.5f;
        int iy = (int)MathF.Floor(sy); float ppy = sy - iy;
        bool rowInner = iy >= 1 && iy + 2 <= sh - 1;
        for (int x = 0; x < d.Length; x++)
        {
            int ix = _ix[x]; float ppx = _fx[x];
            if (rowInner && ix >= 1 && ix + 2 <= sw - 1)
            {
                int c0 = iy * sw + ix;
                for (int t = 0; t < 12; t++) { ti[t] = c0 + tya[t] * sw + txa[t]; tl[t] = lumPlane[ti[t]]; }
            }
            else
                for (int t = 0; t < 12; t++)
                {
                    int cx = Math.Clamp(ix + txa[t], 0, sw - 1), cy = Math.Clamp(iy + tya[t], 0, sh - 1);
                    ti[t] = cy * sw + cx; tl[t] = lumPlane[ti[t]];
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
            // the 12 tap weights, four at a time (the same float operations as the scalar formula, no fused multiply-add, so every lane is bit-identical
            // to it); the colours are accumulated afterwards in tap order
            var ppxv = Vector128.Create(ppx); var ppyv = Vector128.Create(ppy);
            var dxv = Vector128.Create(dirX); var dyv = Vector128.Create(dirY); var ndyv = Vector128.Create(-dirY);
            var l2xv = Vector128.Create(len2x); var l2yv = Vector128.Create(len2y); var lobv = Vector128.Create(lob); var clpv = Vector128.Create(clp);
            Weights(TapX0 - ppxv, TapY0 - ppyv, dxv, dyv, ndyv, l2xv, l2yv, lobv, clpv).CopyTo(wt);
            Weights(TapX1 - ppxv, TapY1 - ppyv, dxv, dyv, ndyv, l2xv, l2yv, lobv, clpv).CopyTo(wt.Slice(4));
            Weights(TapX2 - ppxv, TapY2 - ppyv, dxv, dyv, ndyv, l2xv, l2yv, lobv, clpv).CopyTo(wt.Slice(8));
            float r = 0, g = 0, b = 0, wsum = 0;
            for (int t = 0; t < 12; t++)
            {
                float w = wt[t];
                int p = ti[t] * 3;
                r += pl[p] * w; g += pl[p + 1] * w; b += pl[p + 2] * w; wsum += w;
            }            float inv = 1f / wsum;
            r *= inv; g *= inv; b *= inv;
            // anti-ringing: stay inside the 4 nearest texels (f g j k)
            int pf = ti[3] * 3, pg = ti[4] * 3, pj = ti[7] * 3, pk = ti[8] * 3;
            float f0 = pl[pf], g0 = pl[pg], j0 = pl[pj], k0 = pl[pk];
            float f1 = pl[pf + 1], g1 = pl[pg + 1], j1 = pl[pj + 1], k1 = pl[pk + 1];
            float f2 = pl[pf + 2], g2 = pl[pg + 2], j2 = pl[pj + 2], k2 = pl[pk + 2];
            d[x] = Pack(Math.Clamp(r, Min4(f0, g0, j0, k0), Max4(f0, g0, j0, k0)),
                        Math.Clamp(g, Min4(f1, g1, j1, k1), Max4(f1, g1, j1, k1)),
                        Math.Clamp(b, Min4(f2, g2, j2, k2), Max4(f2, g2, j2, k2)));
        }
    }
    static readonly Vector128<float> TapX0 = Vector128.Create(0f, 1f, -1f, 0f), TapX1 = Vector128.Create(1f, 2f, -1f, 0f), TapX2 = Vector128.Create(1f, 2f, 0f, 1f);
    static readonly Vector128<float> TapY0 = Vector128.Create(-1f, -1f, 0f, 0f), TapY1 = Vector128.Create(0f, 0f, 1f, 1f), TapY2 = Vector128.Create(1f, 1f, 2f, 2f);

    /// <summary>EASU kernel weight of four taps at offsets (ox, oy) from the output position.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static Vector128<float> Weights(Vector128<float> ox, Vector128<float> oy, Vector128<float> dirX, Vector128<float> dirY, Vector128<float> negDirY,
        Vector128<float> len2x, Vector128<float> len2y, Vector128<float> lob, Vector128<float> clp)
    {
        var vx = (ox * dirX + oy * dirY) * len2x;
        var vy = (ox * negDirY + oy * dirX) * len2y;
        var d2 = Vector128.Min(vx * vx + vy * vy, clp);
        var wB = Vector128.Create(2f / 5f) * d2 - Vector128<float>.One;
        var wA = lob * d2 - Vector128<float>.One;
        wB *= wB; wA *= wA;
        wB = Vector128.Create(25f / 16f) * wB - Vector128.Create(25f / 16f - 1f);
        return wB * wA;
    }
    static float Min4(float a, float b, float c, float d) => MathF.Min(MathF.Min(a, b), MathF.Min(c, d));
    static float Max4(float a, float b, float c, float d) => MathF.Max(MathF.Max(a, b), MathF.Max(c, d));

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

    void RcasRow(int y)
    {
        var s = _src.Rgba; var lut = Lut;
        int w = _sw, h = _sh;
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_dst.Rgba.AsSpan(y * w * 4, w * 4));
        float con = _p0;
        int ya = Math.Max(y - 1, 0), yb = Math.Min(y + 1, h - 1);
        for (int x = 0; x < w; x++)
        {
            int xa = Math.Max(x - 1, 0), xb = Math.Min(x + 1, w - 1);
            int ib = (ya * w + x) * 4, id = (y * w + xa) * 4, ie = (y * w + x) * 4, iff = (y * w + xb) * 4, ih = (yb * w + x) * 4;
            float lobe = float.MinValue;
            for (int c = 0; c < 3; c++)
            {
                float vb = lut[s[ib + c]], vd = lut[s[id + c]], vf = lut[s[iff + c]], vh = lut[s[ih + c]];
                float mn = MathF.Min(MathF.Min(vb, vd), MathF.Min(vf, vh));
                float mx = MathF.Max(MathF.Max(vb, vd), MathF.Max(vf, vh));
                float hitMin = mn / MathF.Max(4f * mx, 1e-6f);
                float hitMax = (1f - mx) / MathF.Min(4f * mn - 4f, -1e-6f);
                lobe = MathF.Max(lobe, MathF.Max(-hitMin, hitMax));
            }
            lobe = MathF.Max(-DisplayFilters.RcasLimit, MathF.Min(lobe, 0f)) * con;
            float rcp = 1f / (4f * lobe + 1f);
            d[x] = Pack((lobe * (lut[s[ib]] + lut[s[id]] + lut[s[iff]] + lut[s[ih]]) + lut[s[ie]]) * rcp,
                        (lobe * (lut[s[ib + 1]] + lut[s[id + 1]] + lut[s[iff + 1]] + lut[s[ih + 1]]) + lut[s[ie + 1]]) * rcp,
                        (lobe * (lut[s[ib + 2]] + lut[s[id + 2]] + lut[s[iff + 2]] + lut[s[ih + 2]]) + lut[s[ie + 2]]) * rcp);
        }
    }

    void CrtRow(int y)
    {
        var s = _src.Rgba; var lut = Lut;
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_dst.Rgba.AsSpan(y * _dw * 4, _dw * 4));
        int sw = _sw, sh = _sh;
        float scanline = _p0, mask = _p1, gain = _p2, nm = 1f - mask;
        float sy = (y + 0.5f) * sh / _dh;                        // continuous source row coordinate (row r spans r..r+1)
        int row = Math.Clamp((int)sy, 0, sh - 1);
        float frac = sy - MathF.Floor(sy);                        // 0 at the row's top edge, 0.5 at its centre
        float p = 0.5f + 0.5f * MathF.Cos((frac - 0.5f) * 2f * MathF.PI);
        float beam = 1f - scanline * (1f - p);
        int phase = 0;
        for (int x = 0; x < d.Length; x++, phase = phase == 2 ? 0 : phase + 1)
        {
            float fx = _fx[x], ofx = _ofx[x];
            int x0 = _ix[x];
            int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
            int pa = (row * sw + xa) * 4, pb = (row * sw + xb) * 4;
            float v0 = lut[s[pa]] * ofx + lut[s[pb]] * fx, v1 = lut[s[pa + 1]] * ofx + lut[s[pb + 1]] * fx, v2 = lut[s[pa + 2]] * ofx + lut[s[pb + 2]] * fx;
            d[x] = Pack(v0 * beam * (phase == 0 ? 1f : nm) * gain, v1 * beam * (phase == 1 ? 1f : nm) * gain, v2 * beam * (phase == 2 ? 1f : nm) * gain);
        }
    }
}
