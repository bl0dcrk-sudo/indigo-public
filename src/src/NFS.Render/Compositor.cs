namespace NFS.Render;

/// <summary>
/// Layer composition in Enhanced mode on the CPU (a testable model of what the host does on the GPU): 3D scene <c>640k×480k</c> + 2D layer 640×480
/// with an alpha mask (alpha 0 = the scene shows, 255 = a 2D pixel), scaled entirely ×k (nearest). Also averaging down
/// (<see cref="Downsample"/>, for SSAA and comparisons) and whole-number enlargement (<see cref="UpscaleNearest"/>).
/// </summary>
public static class Compositor
{
    /// <summary>Enlarges the buffer <paramref name="factor"/>× (nearest, each pixel → a factor×factor block).</summary>
    public static FrameBuffer UpscaleNearest(FrameBuffer src, int factor)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        var dst = new FrameBuffer(src.Width * factor, src.Height * factor);
        UpscaleNearestInto(src, dst, factor);
        return dst;
    }

    /// <summary><see cref="UpscaleNearest"/> into an existing buffer of <c>src × factor</c> size (no allocation; whole pixels as 32-bit words).</summary>
    public static void UpscaleNearestInto(FrameBuffer src, FrameBuffer dst, int factor)
    {
        if (factor < 1 || dst.Width != src.Width * factor || dst.Height != src.Height * factor) throw new ArgumentException("size mismatch", nameof(factor));
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src.Rgba.AsSpan());
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.Rgba.AsSpan());
        int dw = dst.Width;
        for (int y = 0; y < src.Height; y++)
        {
            int row0 = y * factor * dw, srow = y * src.Width;
            if (factor == 1) { s.Slice(srow, src.Width).CopyTo(d.Slice(row0, dw)); continue; }
            for (int x = 0; x < src.Width; x++)
                d.Slice(row0 + x * factor, factor).Fill(s[srow + x]);
            for (int j = 1; j < factor; j++)
                d.Slice(row0, dw).CopyTo(d.Slice(row0 + j * dw, dw));
        }
    }
    /// <summary>Averages factor×factor blocks (box filter, each channel rounded); the dimensions must divide evenly.</summary>
    public static FrameBuffer Downsample(FrameBuffer src, int factor)
    {
        if (factor < 1 || src.Width % factor != 0 || src.Height % factor != 0) throw new ArgumentException("size not divisible by factor", nameof(factor));
        var dst = new FrameBuffer(src.Width / factor, src.Height / factor);
        DownsampleInto(src, dst, factor);
        return dst;
    }

    /// <summary><see cref="Downsample"/> into an existing buffer of <c>src / factor</c> size (SSAA resolve every frame, no allocation).
    /// Factors 2, 3 and 4 work on whole 32-bit pixels (R and B summed as a pair in one word); the result is the same as the per-byte loop
    /// <c>(sum + n/2) / n</c> with alpha 255 (test <c>DownsampleTests</c>). Frames of at least <see cref="ResolveParallelMinPixels"/> are split into
    /// row blocks over the thread pool (work items kept between frames, like <see cref="FillRecorder"/>); a concurrent second call runs on its own thread.</summary>
    public static void DownsampleInto(FrameBuffer src, FrameBuffer dst, int factor)
    {
        if (factor < 1 || src.Width != dst.Width * factor || src.Height != dst.Height * factor) throw new ArgumentException("size mismatch", nameof(factor));
        if (dst.Width * dst.Height >= ResolveParallelMinPixels && ResolveMaxThreads != 1 && Monitor.TryEnter(ResolveGate))
        {
            try { ResolveParallel(src, dst, factor); }
            finally { Monitor.Exit(ResolveGate); }
            return;
        }
        DownsampleRows(src, dst, factor, 0, dst.Height);
    }

    /// <summary>Thread cap of the parallel SSAA resolve (0 = <see cref="Environment.ProcessorCount"/>, 1 = single thread).</summary>
    public static int ResolveMaxThreads { get; set; }

    /// <summary>Smallest destination size (pixels) resolved in parallel: 640×480 (smaller frames are not worth the hand-off).</summary>
    public const int ResolveParallelMinPixels = 640 * 480;

    const int ResolveBlock = 16; // destination rows per work block
    static readonly object ResolveGate = new();
    static readonly SemaphoreSlim ResolveFinished = new(0);
    static ResolveHelper[] _resolveHelpers = [];
    static FrameBuffer? _rsSrc, _rsDst;
    static int _rsFactor, _rsRows, _rsNext;
    static Exception? _rsError;

    sealed class ResolveHelper : IThreadPoolWorkItem
    {
        public void Execute()
        {
            try { ResolveBlocks(); }
            finally { ResolveFinished.Release(); }
        }
    }

    static void ResolveParallel(FrameBuffer src, FrameBuffer dst, int factor)
    {
        _rsSrc = src; _rsDst = dst; _rsFactor = factor; _rsRows = dst.Height; _rsNext = 0; _rsError = null;
        int threads = ResolveMaxThreads <= 0 ? Environment.ProcessorCount : ResolveMaxThreads;
        int blocks = (dst.Height + ResolveBlock - 1) / ResolveBlock;
        int helpers = Math.Min(threads, blocks) - 1;
        if (helpers > 0)
        {
            if (_resolveHelpers.Length < helpers)
            {
                int old = _resolveHelpers.Length;
                Array.Resize(ref _resolveHelpers, helpers);
                for (int i = old; i < helpers; i++) _resolveHelpers[i] = new ResolveHelper();
            }
            for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(_resolveHelpers[i], preferLocal: false);
        }
        ResolveBlocks();
        for (int i = 0; i < helpers; i++) ResolveFinished.Wait();
        _rsSrc = _rsDst = null;
        if (_rsError != null)
        {
            var e = _rsError; _rsError = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        }
    }

    static void ResolveBlocks()
    {
        try
        {
            var src = _rsSrc!; var dst = _rsDst!;
            for (int b; (b = Interlocked.Increment(ref _rsNext) - 1) * ResolveBlock < _rsRows;)
            {
                int y0 = b * ResolveBlock;
                DownsampleRows(src, dst, _rsFactor, y0, Math.Min(_rsRows, y0 + ResolveBlock));
            }
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _rsError, e, null);
            Volatile.Write(ref _rsNext, int.MaxValue / 2);
        }
    }

    /// <summary>Resolves destination rows <paramref name="y0"/>..<paramref name="y1"/> (exclusive).</summary>
    static void DownsampleRows(FrameBuffer src, FrameBuffer dst, int factor, int y0, int y1)
    {
        var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(src.Rgba.AsSpan());
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.Rgba.AsSpan());
        int sw = src.Width, dw = dst.Width;
        for (int y = y0; y < y1; y++)
        {
            var drow = d.Slice(y * dw, dw);
            switch (factor)
            {
                case 2: Resolve2(s.Slice(y * 2 * sw, 2 * sw), drow, sw); break;
                case 3: Resolve3(s.Slice(y * 3 * sw, 3 * sw), drow, sw); break;
                case 4: Resolve4(s.Slice(y * 4 * sw, 4 * sw), drow, sw); break;
                default: ResolveBytes(src, dst, factor, y); break;
            }
        }
    }

    static void Resolve2(ReadOnlySpan<uint> s, Span<uint> d, int sw)
    {
        var r0 = s.Slice(0, sw); var r1 = s.Slice(sw, sw);
        for (int x = 0; x < d.Length; x++)
        {
            uint a = r0[2 * x], b = r0[2 * x + 1], c = r1[2 * x], e = r1[2 * x + 1];
            uint rb = ((a & 0x00FF00FFu) + (b & 0x00FF00FFu) + (c & 0x00FF00FFu) + (e & 0x00FF00FFu) + 0x00020002u) >> 2 & 0x00FF00FFu;
            uint g = (((a & 0xFF00u) + (b & 0xFF00u) + (c & 0xFF00u) + (e & 0xFF00u) + 0x0200u) >> 2) & 0xFF00u;
            d[x] = rb | g | 0xFF000000u;
        }
    }

    // 3×3: R and B summed as a pair in one word (each half at most 9·255 = 2295 < 65536), G separately; (sum + 4) / 9 per channel
    // (constant divisor: the JIT turns it into a multiply).
    static void Resolve3(ReadOnlySpan<uint> s, Span<uint> d, int sw)
    {
        var r0 = s.Slice(0, sw); var r1 = s.Slice(sw, sw); var r2 = s.Slice(2 * sw, sw);
        for (int x = 0, i = 0; x < d.Length; x++, i += 3)
        {
            uint p0 = r0[i], p1 = r0[i + 1], p2 = r0[i + 2], p3 = r1[i], p4 = r1[i + 1], p5 = r1[i + 2], p6 = r2[i], p7 = r2[i + 1], p8 = r2[i + 2];
            uint rb = (p0 & 0x00FF00FFu) + (p1 & 0x00FF00FFu) + (p2 & 0x00FF00FFu) + (p3 & 0x00FF00FFu) + (p4 & 0x00FF00FFu)
                    + (p5 & 0x00FF00FFu) + (p6 & 0x00FF00FFu) + (p7 & 0x00FF00FFu) + (p8 & 0x00FF00FFu);
            uint g = (p0 & 0xFF00u) + (p1 & 0xFF00u) + (p2 & 0xFF00u) + (p3 & 0xFF00u) + (p4 & 0xFF00u)
                   + (p5 & 0xFF00u) + (p6 & 0xFF00u) + (p7 & 0xFF00u) + (p8 & 0xFF00u);
            uint r = ((rb & 0xFFFFu) + 4) / 9, b = ((rb >> 16) + 4) / 9, gg = ((g >> 8) + 4) / 9;
            d[x] = r | gg << 8 | b << 16 | 0xFF000000u;
        }
    }

    // 4×4: each half of the paired sum stays within 16·255 + 8 < 65536, so the rounding (sum + 8) / 16 is a shift of the whole word.
    static void Resolve4(ReadOnlySpan<uint> s, Span<uint> d, int sw)
    {
        for (int x = 0, i = 0; x < d.Length; x++, i += 4)
        {
            uint rb = 0x00080008u, g = 0x0800u;
            for (int j = 0; j < 4; j++)
            {
                var q = s.Slice(j * sw + i, 4);
                uint a = q[0], b = q[1], c = q[2], e = q[3];
                rb += (a & 0x00FF00FFu) + (b & 0x00FF00FFu) + (c & 0x00FF00FFu) + (e & 0x00FF00FFu);
                g += (a & 0xFF00u) + (b & 0xFF00u) + (c & 0xFF00u) + (e & 0xFF00u);
            }
            d[x] = (rb >> 4 & 0x00FF00FFu) | (g >> 4 & 0xFF00u) | 0xFF000000u;
        }
    }

    // any other factor: the original per-byte loop
    static void ResolveBytes(FrameBuffer src, FrameBuffer dst, int factor, int y)
    {
        int n = factor * factor;
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
    }

    /// <summary>3D scene (k× larger than the layer) with the 2D layer overlaid: an overlay pixel with alpha 255 covers the k×k block of the scene, alpha 0 leaves the scene,
    /// intermediate alphas blend. Returns a new buffer of the scene size.</summary>
    public static FrameBuffer Compose(FrameBuffer scene, FrameBuffer overlay)
    {
        var dst = new FrameBuffer(scene.Width, scene.Height);
        ComposeInto(scene, overlay, dst);
        return dst;
    }

    /// <summary><see cref="Compose"/> into an existing buffer of the scene's size (no allocation; whole pixels as 32-bit words, the blend is the integer
    /// arithmetic of <see cref="FrameBuffer.Blend"/>). <paramref name="dst"/> must not be <paramref name="scene"/>.</summary>
    public static void ComposeInto(FrameBuffer scene, FrameBuffer overlay, FrameBuffer dst)
    {
        int k = scene.Width / overlay.Width;
        if (k < 1 || scene.Width != overlay.Width * k || scene.Height != overlay.Height * k)
            throw new ArgumentException("scene size must be an integer multiple of the overlay size");
        if (dst.Width != scene.Width || dst.Height != scene.Height) throw new ArgumentException("size mismatch", nameof(dst));
        if (ReferenceEquals(dst, scene)) throw new ArgumentException("dst must differ from scene", nameof(dst));
        Buffer.BlockCopy(scene.Rgba, 0, dst.Rgba, 0, scene.Rgba.Length);
        var ov = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(overlay.Rgba.AsSpan());
        var d = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.Rgba.AsSpan());
        int dw = dst.Width, ow = overlay.Width;
        for (int y = 0; y < overlay.Height; y++)
            for (int x = 0; x < ow; x++)
            {
                uint o = ov[y * ow + x];
                uint a = o >> 24;
                if (a == 0) continue;
                if (a == 255)
                {
                    for (int j = 0; j < k; j++) d.Slice((y * k + j) * dw + x * k, k).Fill(o);
                    continue;
                }
                int alpha = (int)a, inv = 255 - alpha;
                int sr = (int)(o & 0xFF), sg = (int)(o >> 8 & 0xFF), sb = (int)(o >> 16 & 0xFF);
                for (int j = 0; j < k; j++)
                    for (int i = 0; i < k; i++)
                    {
                        int idx = (y * k + j) * dw + x * k + i;
                        uint p = d[idx];
                        if (p >> 24 != 255) { dst.Blend(x * k + i, y * k + j, (uint)(sr << 16 | sg << 8 | sb), alpha); continue; }
                        uint r = (uint)(((int)(p & 0xFF) * inv + sr * alpha + 127) / 255);
                        uint g = (uint)(((int)(p >> 8 & 0xFF) * inv + sg * alpha + 127) / 255);
                        uint b = (uint)(((int)(p >> 16 & 0xFF) * inv + sb * alpha + 127) / 255);
                        d[idx] = r | g << 8 | b << 16 | 0xFF000000u;
                    }
            }
    }
}