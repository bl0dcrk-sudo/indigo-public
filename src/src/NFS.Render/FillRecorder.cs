namespace NFS.Render;

/// <summary>
/// Records the polygon fills (and shadow multiplies) of one draw list in painter order and then executes them by horizontal strips on several threads:
/// every strip replays all recorded commands clipped to its own rows. Rows never depend on each other except through the trilinear level of the previous
/// row of the same polygon, which <see cref="EnhancedRasterizer.Fill"/> reproduces at a strip start, so the result is bit-identical to filling
/// the commands one after another on a single thread (test <c>ParallelFillTests</c>). Strip count and thread count do not change the pixels.
/// </summary>
public sealed class FillRecorder
{
    /// <summary>The recorder of the current thread while a draw list is being recorded; null = fills go straight to the frame buffer.</summary>
    [ThreadStatic] internal static FillRecorder? Active;

    struct Cmd
    {
        public int V, N, Y0, Y1, Mul;
        /// <summary>Index of this polygon's 4·N precomputed attributes in <c>_attr</c> (-1 = none: the polygon is filled from scratch; <c>Persp</c> = the perspective flag it was prepared with), its smallest W and whether a vertex light differs from 1.</summary>
        public int Attr;
        public double MinW;
        public bool AnyLight, Persp;
        public Texture? Tex;
        public uint Flat, FogRgb;
        public bool Transparent, Multiply;
        /// <summary>0 = the frame's context, 1 = <see cref="SkyCtx"/> (sky: no fog), 2 = the original <see cref="Rasterizer"/> whatever the context.</summary>
        public byte Kind;
        public int Fog;
        public double FogOffset;
    }

    Cmd[] _cmds = new Cmd[4096];
    ScreenVertex[] _verts = new ScreenVertex[16384];
    double[] _attr = new double[65536];
    int _count, _vertCount, _attrCount;
    FillContext? _ctx;
    /// <summary>The context of the sky quads (the frame context without fog / light); null = the sky uses the frame context.</summary>
    internal FillContext? SkyCtx;

    internal FrameBuffer? Target { get; private set; }

    /// <summary>Number of strips = this many times the thread count (more strips than threads evens out the load).</summary>
    public static int StripsPerThread { get; set; } = 4;
    /// <summary>Upper bound of worker threads; 0 = all processors, 1 = run the whole frame as one strip (the single-threaded reference of the tests).</summary>
    public static int MaxThreads { get; set; }

    internal void Begin(FrameBuffer target, FillContext? ctx)
    {
        Target = target; _ctx = ctx; _count = 0; _vertCount = 0; _attrCount = 0; SkyCtx = null;
    }

    internal void AddFill(ReadOnlySpan<ScreenVertex> poly, Texture? tex, uint flat, bool transparent, int fog, uint fogRgb, FillContext? ctx) =>
        Add(poly, new Cmd { Tex = tex, Flat = flat, Transparent = transparent, Fog = fog, FogRgb = fogRgb, FogOffset = ctx?.FogBucketOffset ?? 0,
            Kind = (byte)(ctx != null && ctx == SkyCtx && ctx != _ctx ? 1 : 0) }, ctx);

    /// <summary>A fill by the original <see cref="Rasterizer.Fill"/> (flat or textured), whatever the frame context.</summary>
    internal void AddOriginalFill(ReadOnlySpan<ScreenVertex> poly, Texture? tex, uint flat, bool transparent, int fog, uint fogRgb) =>
        Add(poly, new Cmd { Tex = tex, Flat = flat, Transparent = transparent, Fog = fog, FogRgb = fogRgb, Kind = 2 }, null);

    internal void AddMultiply(ReadOnlySpan<ScreenVertex> poly, int mul) => Add(poly, new Cmd { Multiply = true, Mul = mul }, null);

    /// <param name="ctx">The context the polygon will be filled with by <see cref="EnhancedRasterizer"/> (null = it will not be: shadows, original fills).</param>
    void Add(ReadOnlySpan<ScreenVertex> poly, Cmd c, FillContext? ctx)
    {
        int n = poly.Length;
        if (n < 3) return;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++) { minY = Math.Min(minY, poly[i].Y); maxY = Math.Max(maxY, poly[i].Y); }
        int h = Target!.Height;
        c.Y0 = Math.Max(0, (int)Math.Ceiling(minY - 0.5));
        c.Y1 = Math.Min(h - 1, (int)Math.Ceiling(maxY - 0.5) - 1);
        if (c.Y1 < c.Y0) return;   // covers no row
        if (_count == _cmds.Length) Array.Resize(ref _cmds, _count * 2);
        if (_vertCount + n > _verts.Length) Array.Resize(ref _verts, Math.Max(_verts.Length * 2, _vertCount + n));
        poly.CopyTo(_verts.AsSpan(_vertCount));
        c.V = _vertCount; c.N = n;
        _vertCount += n;
        c.Attr = -1;
        if (ctx != null)   // the per-polygon work of EnhancedRasterizer.Fill, once here instead of once per strip
        {
            if (_attrCount + 4 * n > _attr.Length) Array.Resize(ref _attr, Math.Max(_attr.Length * 2, _attrCount + 4 * n));
            EnhancedRasterizer.Prepare(poly, ctx.Perspective, _attr.AsSpan(_attrCount, 4 * n), out c.MinW, out c.AnyLight);
            c.Persp = ctx.Perspective;
            c.Attr = _attrCount; _attrCount += 4 * n;
        }
        _cmds[_count++] = c;
    }

    /// <summary>Executes the recorded commands (in recording order inside every strip) and empties the recorder.</summary>
    internal void Flush()
    {
        var fb = Target!;
        int h = fb.Height;
        // the mip chains are built lazily and cached in the texture: build them here, before the worker threads read them
        for (int i = 0; i < _count; i++)
            if (_cmds[i].Tex is { } t && !_cmds[i].Multiply) t.Mips(_cmds[i].Transparent);
        int threads = MaxThreads <= 0 ? Environment.ProcessorCount : MaxThreads;
        int strips = threads <= 1 ? 1 : Math.Clamp(threads * StripsPerThread, 1, Math.Max(1, h / 24));
        int rows = (h + strips - 1) / strips;
        if (_stripCtx.Length < strips) { Array.Resize(ref _stripCtx, strips); Array.Resize(ref _stripSkyCtx, strips); }
        if (strips == 1) RunStrip(fb, 0, h, 0);
        else
        {
            // Strips are handed out by an atomic counter to this thread and to helper work items on the thread pool. Everything (work items, contexts,
            // semaphore) is kept between frames: unlike Parallel.For the strip fill allocates nothing per frame.
            _rows = rows; _strips = strips; _next = 0; _error = null;
            int helpers = Math.Min(threads, strips) - 1;
            if (_helpers.Length < helpers)
            {
                int old = _helpers.Length;
                Array.Resize(ref _helpers, helpers);
                for (int i = old; i < helpers; i++) _helpers[i] = new Helper(this);
            }
            for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
            RunStrips();
            for (int i = 0; i < helpers; i++) _finished.Wait();   // a helper that starts late finds no strip left and returns at once
            if (_error != null) { var e = _error; _error = null; Target = null; _ctx = null; _count = 0; _vertCount = 0; _attrCount = 0; System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw(); }
        }
        _count = 0; _vertCount = 0; _attrCount = 0;
        Target = null; _ctx = null;
    }

    sealed class Helper(FillRecorder owner) : IThreadPoolWorkItem
    {
        public void Execute()
        {
            try { owner.RunStrips(); }
            finally { owner._finished.Release(); }
        }
    }

    FillContext?[] _stripCtx = [], _stripSkyCtx = [];
    Helper[] _helpers = [];
    readonly SemaphoreSlim _finished = new(0);
    int _rows, _strips, _next;
    Exception? _error;

    /// <summary>Takes strips from the shared counter until none is left (called on the rendering thread and on the helpers).</summary>
    void RunStrips()
    {
        var fb = Target!;
        try
        {
            for (int s; (s = Interlocked.Increment(ref _next) - 1) < _strips;)
                RunStrip(fb, s * _rows, Math.Min(fb.Height, (s + 1) * _rows), s);
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _error, e, null);
            Volatile.Write(ref _next, int.MaxValue / 2);   // stop handing out strips
        }
    }

    void RunStrip(FrameBuffer fb, int yMin, int yMax, int slot)
    {
        if (yMin >= yMax) return;
        FillContext? ctx = null, skyCtx = null;
        if (_ctx != null) { ctx = _stripCtx[slot] ??= new FillContext(); ctx.SetFrom(_ctx); }   // each strip sets FogBucketOffset on its own copy
        if (SkyCtx != null) { skyCtx = _stripSkyCtx[slot] ??= new FillContext(); skyCtx.SetFrom(SkyCtx); }
        for (int i = 0; i < _count; i++)
        {
            ref readonly var c = ref _cmds[i];
            if (c.Y1 < yMin || c.Y0 >= yMax) continue;
            var poly = new ReadOnlySpan<ScreenVertex>(_verts, c.V, c.N);
            if (c.Multiply) CarShadow.Multiply(fb, poly, c.Mul, yMin, yMax);
            else if (c.Kind == 2 || ctx == null) Rasterizer.Fill(fb, poly, c.Tex, c.Flat, c.Transparent, c.Fog, c.FogRgb, yMin, yMax);
            else
            {
                var cc = c.Kind == 1 ? skyCtx! : ctx;
                cc.FogBucketOffset = c.FogOffset;
                if (c.Attr >= 0 && c.Persp == cc.Perspective)
                    EnhancedRasterizer.FillPrepared(fb, poly, c.Tex, c.Flat, c.Transparent, c.Fog, c.FogRgb, cc, yMin, yMax,
                        new ReadOnlySpan<double>(_attr, c.Attr, 4 * c.N), c.MinW, c.AnyLight, c.Y0, c.Y1);
                else EnhancedRasterizer.Fill(fb, poly, c.Tex, c.Flat, c.Transparent, c.Fog, c.FogRgb, cc, yMin, yMax);
            }
        }
    }
}
