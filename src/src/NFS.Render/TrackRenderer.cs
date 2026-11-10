using System.Numerics;
using NFS.Core;
using NFS.Data;
using NFS.Data.Track;

namespace NFS.Render;

public sealed class RenderOptions
{
    public bool Textured { get; set; } = true;
    public bool Fog { get; set; } = true;
    public bool Sky { get; set; } = true;
    /// <summary>HORIZONDISPLAY (CONFIG.DAT +0x132 == 0, inverted): false = fills the [ref:2654] [ref:3443]
    /// whole view with the flat HRZ sky colour (template) instead of the pixmap ring and cones. [ref:3444]</summary>
    public bool HorizonDisplay { get; set; } = true;
    /// <summary>Back-face culling of one-sided polygons. [ref:2655]</summary>
    public bool BackfaceCull { get; set; } = true;
    /// <summary>Track objects (XBID 7/18/19 + COL globals).</summary>
    public bool Objects { get; set; } = true;
    /// <summary>Tyre marks. [ref:2656]</summary>
    public bool Skids { get; set; } = true;
    /// <summary>Road marking lines of the near LOD blocks (<see cref="LaneMarks"/>). [ref:2657]</summary>
    public bool LaneMarks { get; set; } = true;
    /// <summary>Fallback COL object draw range in metres (type 0, +0xA4). [ref:2658]</summary>
    public float ObjectRange { get; set; } = 500f;

    // ---- Enhanced image quality; all off = the original rasteriser, bit-identical ---- [ref:3445]
    /// <summary>Perspective-correct texture coordinates (and fewer quad subdivisions).</summary>
    public bool PerspectiveCorrect { get; set; }
    /// <summary>Texture sampling: nearest (original), bilinear, or bilinear + mip maps (trilinear); masks (trees, fences) are filtered with alpha.</summary>
    public TextureFilter Filter { get; set; }
    /// <summary>Fog level computed per pixel from the depth instead of once per polygon.</summary>
    public bool PixelFog { get; set; }
    /// <summary>Mip level bias (negative = sharper).</summary>
    public double LodBias { get; set; }
    /// <summary>Gouraud light on the cars (<see cref="CarLighting"/>): the texture is multiplied by a per-corner sun / sky intensity.</summary>
    public bool CarLight { get; set; }
    /// <summary>Soft shadow blob under every car (<see cref="CarShadow"/>).</summary>
    public bool CarShadow { get; set; }
    /// <summary>Track polygon shading (<see cref="NFS.Render.TrackShade"/>): the 4 corner intensities of the TRK / XBID 8 polygon flags darken the road and [ref:2659]
    /// the scenery (Gouraud). Off = no light on the track (Vanilla, bit-identical).</summary>
    public bool TrackShade { get; set; }
    /// <summary>"Extended" view range: objects / blocks / cars up to twice as far and a z-sort of 2480 buckets (1240 m); not used by the rear mirror.</summary>
    public bool ExtendedRange { get; set; }
    /// <summary>Fill the polygons of a frame on several threads, by horizontal strips (pixel-identical to the single-threaded fill); only for frames at least
    /// <see cref="TrackRenderer.ParallelMinHeight"/> rows high. Off = the original single-threaded order (Vanilla).</summary>
    public bool ParallelFill { get; set; }
    /// <summary>True when any Enhanced fill feature is on.</summary>
    public bool EnhancedFill => PerspectiveCorrect || Filter != TextureFilter.Nearest || PixelFog || CarLight || TrackShade;
}

/// <summary>
/// Track scene renderer; draws through the z-sort. [ref:2660]
/// Whole TRK held in memory (no streaming). Geometry in metres: world = blockRef/65536 + v/256; vertices with index
/// &lt; nStickToNext are relative to the NEXT block's reference point.
/// </summary>
public sealed class TrackRenderer
{
    public int TrackNo { get; }
    public Trk Trk { get; }
    public Col Col { get; }
    public Dis Dis { get; }
    public Hrz? Hrz { get; }
    public TrackSlice[] Slices { get; }
    public string QfsPath { get; }
    readonly Vector3[][] _verts;
    readonly Texture?[] _tex;       // per XBID 2 record (first animation frame), orientation applied
    readonly TextureRecord[] _texRec;
    readonly Texture?[]?[] _texAnim; // XBID 2 flag 0x04: frames ShapeIndex + 0..n-1, orientation applied [ref:2661]
    readonly TrackLocator _viewLocator;
    readonly TrackPos _mainView = new() { Slice = -1, Column = -1 };
    readonly TrackPos _mirrorView = new() { Slice = -1, Column = -1 };
    /// <summary>Split screen: the second player's main view keeps its own locator (type 2). [ref:2662]</summary>
    readonly TrackPos _secondView = new() { Slice = -1, Column = -1 };
    readonly ushort[][] _objIds;    // per block: XBID 11 point → object id (index) [ref:2663]
    readonly Texture?[] _skyTex = new Texture?[8]; // QFS shapes 0..7 [ref:2664]
    readonly Can?[] _hitCan = new Can?[4]; // TRxx02/03.CAN: tumble animations of knocked-over objects [ref:2665]
    /// <summary>skd0..skd3 of DTEXTURE.FSH. [ref:2666] [ref:3446]</summary>
    public Texture?[] SkidTextures { get; } = new Texture?[4];
    /// <summary>lin0..lin7 of DTEXTURE.FSH : the textures of the road marking lines. [ref:2667] [ref:3447]</summary>
    public Texture?[] LaneTextures { get; } = new Texture?[8];

    /// <summary>Statistics of the last frame.</summary>
    public int LastEmitted { get; private set; }
    public int LastDrawn { get; private set; }
    /// <summary>Polygons of the last frame that lay beyond the last z-sort bucket (620 m, Extended 1240 m) and were therefore not drawn.</summary>
    public int LastFarDropped { get; private set; }

    public TrackRenderer(int trackNo, string? gameDir = null)
    {
        TrackNo = trackNo;
        Trk = Trk.Load($"GAMEDATA/TRACKS/SE/TR{trackNo:00}.TRK", gameDir);
        Col = Col.Load($"GAMEDATA/TRACKS/SE/TR{trackNo:00}.COL", gameDir);
        Slices = Col.Slices;
        _viewLocator = new TrackLocator(new TrackGeometry(Trk, Slices));
        string dis = $"GAMEDATA/TRACKS/SE/TR{trackNo:00}.DIS";
        Dis = GameFiles.Exists(dis, gameDir) ? Dis.Parse(GameFiles.Read(dis, gameDir), Slices.Length) : Dis.Default(Slices.Length);
        foreach (var h in new[] { $"GAMEDATA/TRACKS/PC/TR{trackNo:00}.HRZ", $"GAMEDATA/TRACKS/SE/TR{trackNo:00}.HRZ" })
            if (Hrz == null && GameFiles.Exists(h, gameDir)) Hrz = Hrz.Load(h, gameDir);

        int nb = Trk.Blocks.Count;
        _objIds = Trk.Blocks.Select(b => b.Xb(11)?.Points().Select(p => p.ObjectId).ToArray() ?? []).ToArray();
        for (int n = 2; n <= 3; n++)
        {
            string can = $"GAMEDATA/TRACKS/DATA/PC/TR{trackNo:00}{n:00}.CAN";
            if (GameFiles.Exists(can, gameDir)) _hitCan[n] = Can.Load(can, gameDir);
        }
        _verts = new Vector3[nb][];
        for (int bi = 0; bi < nb; bi++)
        {
            var b = Trk.Blocks[bi];
            var own = Ref(bi); var next = Ref((bi + 1) % nb);
            var v = new Vector3[b.NumVertices];
            for (int i = 0; i < v.Length; i++)
                v[i] = (i < b.NumStickToNext ? next : own) + new Vector3(b.Vertices[i * 3], b.Vertices[i * 3 + 1], b.Vertices[i * 3 + 2]) / 256f;
            _verts[bi] = v;
        }

        const string dtexture = "GAMEDATA/RENDER/PC/DTEXTURE.FSH";
        if (GameFiles.Exists(dtexture, gameDir))
        {
            var dt = Fsh.Load(dtexture, gameDir);
            var dpal = dt.GlobalPalette();
            for (int i = 0; i < 4; i++)
                if (dt.Find($"skd{i}") is { } se) SkidTextures[i] = Texture.FromFsh(dt, se, dpal);
            for (int i = 0; i < 8; i++)
                if (dt.Find($"lin{i}") is { } le) LaneTextures[i] = Texture.FromFsh(dt, le, dpal);
        }

        _texRec = Col.Textures;
        _tex = new Texture?[_texRec.Length];
        _texAnim = new Texture?[]?[_texRec.Length];
        // Software (8bpp) textures: GAMEDATA/TRACKS/PC/TRxx0.QFS = 0x7B + global '!pal', index 0xFF transparent.
        // SE/TRxx0.QFS holds the same shapes as 16-bit 0x78 (3Dfx build) — fallback only.
        string qfs = $"GAMEDATA/TRACKS/PC/TR{trackNo:00}0.QFS";
        if (!GameFiles.Exists(qfs, gameDir)) qfs = $"GAMEDATA/TRACKS/SE/TR{trackNo:00}0.QFS";
        QfsPath = qfs;
        if (GameFiles.Exists(qfs, gameDir))
        {
            var fsh = Fsh.Load(qfs, gameDir);
            var gpal = fsh.GlobalPalette();
            for (int i = 0; i < 8 && i < fsh.Entries.Count; i++) _skyTex[i] = Texture.FromFsh(fsh, fsh.Entries[i], gpal);
            var cache = new Dictionary<int, Texture?>();
            for (int i = 0; i < _texRec.Length; i++)
            {
                int s = _texRec[i].ShapeIndex;
                if (!cache.TryGetValue(s, out var t))
                    cache[s] = t = s < fsh.Entries.Count ? Texture.FromFsh(fsh, fsh.Entries[s], gpal) : null;
                // Fully transparent shapes (TR00 shape 150, grid lane at the start; XBID2 +2 = 0x20) stay invisible —
                // an XBID 7 object (grass) lies under them.
                _tex[i] = t?.Oriented(_texRec[i].Flags);
                if ((_texRec[i].Unknown_0x02 & 4) != 0 && _texRec[i].Frames > 1 && (_texRec[i].Unknown_0x08 & 0xFF) != 0)
                {
                    var frames = new Texture?[_texRec[i].Frames];
                    for (int f = 0; f < frames.Length; f++)
                    {
                        int sf = s + f;
                        if (!cache.TryGetValue(sf, out var tf))
                            cache[sf] = tf = sf < fsh.Entries.Count ? Texture.FromFsh(fsh, fsh.Entries[sf], gpal) : null;
                        frames[f] = tf?.Oriented(_texRec[i].Flags);
                    }
                    _texAnim[i] = frames;
                }
            }
        }
    }

    Vector3 Ref(int bi) => new(Trk.BlockRefs[bi * 3] / 65536f, Trk.BlockRefs[bi * 3 + 1] / 65536f, Trk.BlockRefs[bi * 3 + 2] / 65536f);

    public Texture? TextureFor(int texIndex) => (uint)texIndex < (uint)_tex.Length ? _tex[texIndex] : null;

    /// <summary>Texture at <paramref name="tick"/> : animated records (flag 0x04) show frame [ref:2668]
    /// (tick / period[+8]) % nFrames[+7], others their only frame. [ref:2669]</summary>
    public Texture? TextureFor(int texIndex, int tick)
    {
        if ((uint)texIndex < (uint)_texAnim.Length && _texAnim[texIndex] is { } fr)
            return fr[tick / (_texRec[texIndex].Unknown_0x08 & 0xFF) % fr.Length];
        return TextureFor(texIndex);
    }

    /// <summary>Nearest slice (XZ distance) — global search, simplified. [ref:2670]</summary>
    public int NearestSlice(Vector3 p)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < Slices.Length; i++)
        {
            var c = Camera.SliceCentre(Slices[i]);
            float d = (c.X - p.X) * (c.X - p.X) + (c.Z - p.Z) * (c.Z - p.Z);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    public Camera CameraAtSlice(int slice, float height = 1.5f, float back = 0f, int width = 640, int screenHeight = 480) =>
        Camera.FromSlice(Slices[((slice % Slices.Length) + Slices.Length) % Slices.Length], height, back, width, screenHeight);

    /// <summary>Slice of the followed car −8 (car+0x288 == 0) or +8 (≠ 0), wrapping; tunnel [ref:2671]
    /// when either nibble of the slice byte +0x1E is 2.</summary>
    public bool IsTunnelNear(int carSlice, int noAssist)
    {
        int n = Slices.Length;
        int i = noAssist == 0 ? carSlice - 8 : carSlice + 8;
        i = ((i % n) + n) % n;
        byte b = Slices[i].Surroundings;
        return b >> 4 == 2 || (b & 0xF) == 2;
    }

    /// <summary>Fog level (0..15) for a z-sort bucket: B-spline curve through the 4 HRZ depth-cue points, [ref:2672]
    /// via <see cref="FogCurve"/>. Defaults without HRZ (flag): (0,80,85,300)→(0,0,12,15). [ref:2673]</summary>
    public int FogLevel(int bucket)
    {
        int[] x = Hrz?.DepthCueDistances ?? [0, 80, 85, 300];
        int[] l = Hrz?.DepthCueLevels ?? [0, 0, 12, 15];
        _fogCurve ??= FogCurve.Evaluate(x, l);
        return FogCurve.Level(_fogCurve, x, l, bucket);
    }

    int[]? _fogCurve;
    FillRecorder? _recorder;

    // ---- per-frame allocation free state: parsed XBID data, scratch buffers (the render loop allocates nothing once warm) ----
    /// <summary>Parsed extrablocks of one TRK block (the accessors of <see cref="ExtraBlock"/> parse the bytes on every call), built on first use.</summary>
    sealed class BlockData
    {
        public ushort[] Neighbors = [];
        public TrackStructure[] Structs = [];
        public ObjectRef[][] Refs = [];   // XBID 7, 19, 18 (in draw order) [ref:2674]
        public bool HasLane;              // XBID 9 present [ref:2675]
        public Vector3[] LaneSlots = [];  // world positions, 4 per XBID 9 entry
        public byte[] LaneEntries = [];   // XBID 9 data (vertex, slice, type, -)
        public int[] LaneKeys = [];       // record +8: x + z of the entry's vertex
    }
    static readonly int[] ObjectXbids = [7, 19, 18];
    BlockData?[]? _blockData;
    ObjectRef[]? _colRefs;
    TrackStructure[]? _colStructs;
    readonly HashSet<int> _seen = new();
    Vector3[] _wvScratch = new Vector3[256];
    readonly CarPolyBuffer _carBuf = new();
    Vector3[] _carView = new Vector3[512 * 4];
    float[] _carLight = new float[512 * 4], _carKeys = new float[512];
    int[] _carOrder = new int[512];
    ulong[] _carSortKeys = [];

    /// <summary>Monotonic uint image of a float under <see cref="float.CompareTo(float)"/>: NaN lowest (all NaNs equal), -0 == +0.</summary>
    static uint OrderedBits(float f)
    {
        if (float.IsNaN(f)) return 0;
        if (f == 0) f = 0;
        uint b = BitConverter.SingleToUInt32Bits(f);
        return (b & 0x80000000u) != 0 ? ~b : b | 0x80000000u;
    }
    FillContext? _fillCtx, _fillCtxNoFog;

    BlockData Block(int bi)
    {
        _blockData ??= new BlockData?[Trk.Blocks.Count];
        if (_blockData[bi] is { } d) return d;
        var b = Trk.Blocks[bi];
        d = new BlockData { Neighbors = b.Xb(4)?.Neighbors() ?? [(ushort)bi], Structs = b.Xb(8)?.Structures() ?? [] };
        d.Refs = new ObjectRef[ObjectXbids.Length][];
        for (int i = 0; i < d.Refs.Length; i++) d.Refs[i] = b.Xb(ObjectXbids[i])?.ObjectRefs() ?? [];
        if (b.Xb(9) is { } lane)
        {
            int n = lane.Count, nb = Trk.Blocks.Count;
            var cur = Trk.BlockRefs.AsSpan(bi * 3, 3); var next = Trk.BlockRefs.AsSpan((bi + 1) % nb * 3, 3);
            var slots = LaneMarks.BuildSlots(lane.Data, n, b.Vertices, b.NumStickToNext, Slices, b.FirstSlice, cur, next);
            var origin = Ref(bi);
            d.HasLane = true; d.LaneEntries = lane.Data;
            d.LaneSlots = new Vector3[slots.Length / 3];
            for (int i = 0; i < d.LaneSlots.Length; i++) d.LaneSlots[i] = origin + new Vector3(slots[3 * i], slots[3 * i + 1], slots[3 * i + 2]) / 256f;
            d.LaneKeys = new int[n];
            for (int e = 0; e < n; e++) d.LaneKeys[e] = LaneMarks.Key(b.Vertices, lane.Data[4 * e]);
        }
        return _blockData[bi] = d;
    }

    // ---- road marking lines: (pending lists, bias 0x40000, cone cull on) ---- [ref:2676]
    const int LaneBias = 0x40000;
    const int MaxLaneLists = 10;   // the lists of further blocks are dropped once there are 10 [ref:2677]
    readonly int[] _laneBlocks = new int[MaxLaneLists];
    int _laneCount;

    /// <summary>One block's lane marks (records taken from the last to the first, as in the original). [ref:2678]</summary>
    int EmitLaneMarks(ZSort<DrawItem> zs, Camera cam, int bi)
    {
        var d = Block(bi);
        int n = 0, count = d.LaneEntries.Length / 4;
        Span<LaneMarks.Quad> q = stackalloc LaneMarks.Quad[2];
        float slope = (float)cam.CullSlope;
        for (int e = count - 2; e >= 0; e--)
        {
            int type = d.LaneEntries[4 * e + 2];
            if (type == 0xFF) continue;
            // record corners: vertex pointers +0xC..+0x18 = slots 1, 0, 2, 3
            var a = cam.ToView(d.LaneSlots[4 * e + 1]); var b = cam.ToView(d.LaneSlots[4 * e]);
            var c = cam.ToView(d.LaneSlots[4 * e + 2]); var dd = cam.ToView(d.LaneSlots[4 * e + 3]);
            int nq = LaneMarks.Split(type, d.LaneKeys[e], b.Z, a, b, c, dd, q);
            for (int k = 0; k < nq; k++)
            {
                var tex = LaneTextures[q[k].Texture];
                if (tex == null) continue;
                Vector3 va = q[k].A, vb = q[k].B, vc = q[k].C, vd = q[k].D;
                if (Math.Abs(va.X) > va.Z * slope && Math.Abs(vb.X) > vb.Z * slope && Math.Abs(vc.X) > vc.Z * slope && Math.Abs(vd.X) > vd.Z * slope) continue;   // cone cull [ref:2679]
                if (zs.Insert((va.Z + vb.Z + vc.Z + vd.Z) / 4, new DrawItem(va, vb, vc, vd, -1, false, false, 0, null, tex), LaneBias)) n++;
            }
        }
        return n;
    }
    /// <summary>Smallest frame height (pixels) that uses the parallel strip fill (<see cref="RenderOptions.ParallelFill"/>): 2x the original 480.</summary>
    public const int ParallelMinHeight = 960;

    public uint FogRgb => Hrz is { ColorSystem: 0 } h ? (uint)(h.FogColor[0] << 16 | h.FogColor[1] << 8 | h.FogColor[2]) : 0x0A0A0Au;

    readonly ZSort<DrawItem> _zsort = new();
    ZSort<DrawItem>? _zsortExtended;   // 2x the buckets, allocated on first use (Extended view range)
    readonly CarLighting _carLighting = new();
    /// <summary><see cref="DrawItem.Shadow"/> = the item is the shadow blob of <see cref="DrawItem.Car"/>, not the car.</summary>
    readonly record struct DrawItem(Vector3 V0, Vector3 V1, Vector3 V2, Vector3 V3, int Tex, bool Transparent, bool TwoSided, uint Flat, CarInstance? Car = null, Texture? Direct = null, bool Shadow = false, ushort Shade = NFS.Render.TrackShade.Neutral);

    static readonly float[] SubdivThreshold = [20, 10, 6]; // thresholds for class 0 (metres, diagonal z sum) [ref:2680]

    /// <summary>Enhanced (scene rendered at k× resolution, <see cref="Camera.Width"/> = 640k): the affine UV error of an undivided quad grows
    /// k× in pixels, so the subdivision thresholds are scaled by k and a 4th level is allowed. k = 1 (Vanilla, mirror, 640-wide views) = original.</summary>
    static int SubdivScale(Camera cam) => Math.Max(1, (cam.LayoutWidth + 160) / 640);
    static float SubdivThresholdAt(int level, int ks) => (level < 3 ? SubdivThreshold[level] : 3f) * ks;

    /// <param name="knocked">Knocked-over XBID 11 objects (a copy of the simulation state, read only): knocked-over objects are drawn tumbling / lying
    /// flat, as in the simulation state; null = every object stands. <paramref name="tick"/> (CAN animation clock). [ref:2681]</param>
    public void Render(FrameBuffer fb, Camera cam, RenderOptions? opt = null, IEnumerable<CarInstance>? cars = null,
        IReadOnlyDictionary<ushort, KnockedObject>? knocked = null, int tick = 0, SkidBuffer? skids = null, bool rearMirror = false,
        int targetSlice = -1, int targetWrongWayTicks = 0, int viewIndex = 0, ViewRange? range = null)
    {
        opt ??= new RenderOptions();
        var ctx = opt.EnhancedFill ? MakeContext(opt) : null;
        FillContext? skyCtx = null;
        if (ctx != null) { skyCtx = _fillCtxNoFog ??= new(); skyCtx.SetFrom(ctx); skyCtx.ClearFog(); }

        bool extended = opt.ExtendedRange && !rearMirror;
        var zs = extended ? _zsortExtended ??= new ZSort<DrawItem>(ZSort<DrawItem>.ExtendedBuckets) : _zsort;
        if (extended) range = range?.Extended();
        zs.Clear();   // reused between frames: a new ZSort allocated 1240 lists per frame
        // Parallel strip fill (Enhanced, large frames): the polygon fills of the frame - the sky first - are recorded in painter order and then executed by
        // horizontal strips on worker threads (each strip draws every polygon clipped to its rows), pixel-identical to the single-threaded order.
        FillRecorder? rec = null;
        // Split screen: each view [ref:2682]
        // retains its own locator. A global XZ nearest search jumps between
        // stacked roads and then selects the other road's visibility list.
        var view = rearMirror ? _mirrorView : viewIndex == 1 ? _secondView : _mainView;
        _viewLocator.Update(new Vec3((int)(cam.Position.X * 65536f),
            (int)(cam.Position.Y * 65536f), (int)(cam.Position.Z * 65536f)), view);
        int slice = view.Slice;
        // only view 1 (the dash mirror strip, 640x70) replaces [ref:2683]
        // the whole sky with black when a tunnel is near the followed car; the main view (view 0) always draws the sky.
        if (rearMirror && IsTunnelNear(targetSlice >= 0 ? targetSlice : slice, targetWrongWayTicks)) fb.Clear(0, 0, 0);
        else if (opt.Sky)
        {
            if (opt.ParallelFill && fb.Height >= ParallelMinHeight)
            {
                rec = _recorder ??= new FillRecorder(); rec.Begin(fb, ctx); rec.SkyCtx = skyCtx; FillRecorder.Active = rec;
            }
            try { DrawSky(fb, cam, opt.HorizonDisplay, skyCtx); }
            catch { FillRecorder.Active = null; throw; }
        }
        else fb.Clear(0, 0, 0);
        int block = Slices[slice].BlockNumber;
        bool forward = Vector3.Dot(cam.Forward, Camera.Vec(Slices[slice].Forward)) >= 0;
        var neigh = Block(block).Neighbors;
        int count = Math.Min(neigh.Length, Math.Max(1, (int)Dis.VisibleCount(slice, forward)));
        if (extended) count = Math.Min(neigh.Length, count + ExtendedExtraBlocks);
        var seen = _seen; seen.Clear();
        int emitted = 0;
        _laneCount = 0;
        float laneNear = rearMirror ? 40f : (range ?? ViewRange.Main).LodNear / 65536f;
        for (int ni = 0; ni < count; ni++)
        {
            int bi = neigh[ni];
            if (bi >= Trk.Blocks.Count || !seen.Add(bi)) continue;
            var b = Trk.Blocks[bi];
            var wv = _verts[bi];
            // (type3): range100, near40, middle80 metres. [ref:2684]
            float Dist2(int index)
            {
                var d = Ref(index) - cam.Position;
                return d.X * d.X + d.Z * d.Z;
            }
            // (block, camera) <= (view +0xA8)^2 – split: 440 m (type 1/2) [ref:2685]
            if (range is { } vr && !rearMirror && Dist2(bi) > vr.BlockRangeMetres * vr.BlockRangeMetres) continue;
            float dist = rearMirror ? Dist2(bi) : 0;
            if (rearMirror && dist > 100 * 100) continue;
            int Group(float d) => d <= 40 * 40 ? 4 : d <= 80 * 80 ? 2 : 0;
            for (int gi = 0; gi < 2; gi++) // the two surface lists [ref:2686]
            {
                int g = rearMirror ? (gi == 0 ? Group(dist) : Group(Dist2((bi + 1) % Trk.Blocks.Count)) + 1) : 4 + gi;
                // Surface list: bias 0; second list: bias 0x60000. [ref:2687]
                // Without the latter, nearby ground can overwrite the raised wall beside it.
                if (g < b.PolyGroups.Length) emitted += EmitPolys(zs, cam, wv, wv.Length, b.PolyGroups[g], (g & 1) != 0 ? ZSort<DrawItem>.SceneryBias : ZSort<DrawItem>.WorldBias);
            }
            // only blocks at LOD 2 (block distance ≤ view +0xAC [ref:2688]
            // 110 m main view, 40 m mirror); the list goes to the pending lists (max 10 per frame)
            if (opt.LaneMarks && _laneCount < MaxLaneLists && Block(bi).HasLane && Dist2(bi) <= laneNear * laneNear) _laneBlocks[_laneCount++] = bi;
            if (opt.Objects)
            {
                var bd = Block(bi);
                // passes range −1 for block objects: no distance cut (only the COL globals use view +0xA4) [ref:2689]
                for (int xi = 0; xi < bd.Refs.Length; xi++)
                    emitted += EmitObjects(zs, cam, bd.Refs[xi], bd.Structs, float.PositiveInfinity, _objIds[bi], knocked, tick);
            }
        }
        if (opt.Objects)
            emitted += EmitObjects(zs, cam, _colRefs ??= Col.ObjectRefs, _colStructs ??= Col.Structures, rearMirror ? 150 : range?.ObjectRangeMetres ?? opt.ObjectRange * (extended ? 2 : 1));
        // each car is ONE z-sort entry at the depth of its centre, bias 0xA0000 (no +10 m) [ref:2690]
        float carDepth = range?.CarDepthMetres ?? 200f;
        if (cars is IReadOnlyList<CarInstance> carList)
            for (int ci = 0; ci < carList.Count; ci++) emitted += InsertCar(zs, cam, carList[ci], carDepth, opt.CarShadow);
        else if (cars != null)
            foreach (var car in cars) emitted += InsertCar(zs, cam, car, carDepth, opt.CarShadow);
        if (skids != null && opt.Skids) emitted += EmitSkids(zs, cam, skids);
        // (after cars and skids) pops the pending lists from the last to the first [ref:2691]
        for (int li = _laneCount - 1; li >= 0; li--) emitted += EmitLaneMarks(zs, cam, _laneBlocks[li]);
        // Parallel strip fill (Enhanced, large frames): the polygon fills of the draw list are recorded in painter order and then executed by horizontal
        // strips on worker threads (each strip draws every polygon clipped to its rows), pixel-identical to the single-threaded order.
        if (opt.ParallelFill && fb.Height >= ParallelMinHeight)
        {
            if (rec == null) { rec = _recorder ??= new FillRecorder(); rec.Begin(fb, ctx); rec.SkyCtx = skyCtx; }
            FillRecorder.Active = rec;
            try { RenderList(fb, cam, zs, opt, tick, ctx); }
            finally { FillRecorder.Active = null; }
            rec.Flush();
        }
        else RenderList(fb, cam, zs, opt, tick, ctx);
        LastEmitted = emitted;
        LastFarDropped = zs.FarDropped;
    }

    /// <summary>Silhouette pass for the particle z-sort (<see cref="ParticleRenderer"/>): only <paramref name="cars"/>, no track, sky, objects, lines or
    /// skid marks, with the same projection, depth cull and far → near order as <see cref="Render"/>; <paramref name="fb"/> is cleared to transparent
    /// first, so the pixels with alpha != 0 afterwards are the cars. Flat fill (no fog, light or shadow): only the coverage matters.</summary>
    public void RenderCarsOnly(FrameBuffer fb, Camera cam, RenderOptions opt, IReadOnlyList<CarInstance> cars, ViewRange? range = null)
    {
        fb.ClearTransparent();
        var zs = _zsort;
        zs.Clear();
        float carDepth = range?.CarDepthMetres ?? 200f;
        for (int ci = 0; ci < cars.Count; ci++) InsertCar(zs, cam, cars[ci], carDepth, false);
        for (int k = zs.Capacity - 1; k >= 0; k--)
            for (int bi = zs.BucketCount(k) - 1; bi >= 0; bi--)
                if (zs.At(k, bi).Car is { } car) DrawCar(fb, cam, car, opt, 0, 0, null);
    }

    /// <summary>Car preview (CLI render-car): <paramref name="fb"/> cleared to <paramref name="background"/>, then only <paramref name="car"/>
    /// with the same <see cref="DrawCar"/> as the race (Enhanced fill and car light per <paramref name="opt"/>, no fog, track, sky or shadow).</summary>
    public int RenderCarPreview(FrameBuffer fb, Camera cam, RenderOptions opt, CarInstance car, uint background)
    {
        fb.Clear((byte)(background >> 16), (byte)(background >> 8), (byte)background);
        return DrawCar(fb, cam, car, opt, 0, 0, opt.EnhancedFill ? MakeContext(opt) : null);
    }

    static int InsertCar(ZSort<DrawItem> zs, Camera cam, CarInstance car, float depth, bool shadow)
    {
        var c = cam.ToView(car.Position);
        if (c.Z < -2 || c.Z > depth) return 0;   // 200 m, split 150 m [ref:2692]
        if (!zs.Insert(c.Z, new DrawItem(c, c, c, c, -1, false, false, 0, car), ZSort<DrawItem>.CarBias)) return 0;
        if (shadow && car.Lod == 0) zs.Insert(c.Z, new DrawItem(c, c, c, c, -1, false, false, 0, car, null, true), CarShadow.Bias);
        return 1;
    }

    /// <summary>Extended view range: this many more blocks of the visibility list are drawn than the track data asks for (the list of the original ends where its
    /// 500 m view ends).</summary>
    public static int ExtendedExtraBlocks { get; set; } = 8;

    int[]? _fogWeight, _fogMax;

    /// <summary>Per-frame fill settings of the Enhanced rasteriser (fog weight table per z-sort bucket, built once from the HRZ curve).</summary>
    FillContext MakeContext(RenderOptions opt)
    {
        var c = _fillCtx ??= new FillContext();
        c.Perspective = opt.PerspectiveCorrect; c.Filter = opt.Filter; c.LodBias = opt.LodBias; c.Shade = opt.CarLight || opt.TrackShade;
        c.FogWeight = null; c.FogMaxUpTo = null; c.FogBucketOffset = 0;
        if (opt.PixelFog && opt.Fog)
        {
            if (_fogWeight == null)
            {
                _fogWeight = new int[ZSort<DrawItem>.ExtendedBuckets]; _fogMax = new int[ZSort<DrawItem>.ExtendedBuckets];
                int mx = 0;
                for (int b = 0; b < _fogWeight.Length; b++) { _fogWeight[b] = FogLevel(b) * 16; mx = Math.Max(mx, _fogWeight[b]); _fogMax[b] = mx; }
            }
            c.FogWeight = _fogWeight; c.FogMaxUpTo = _fogMax;
        }
        return c;
    }

    /// <summary>Segments whose origin is within 50 m (XZ) of the camera [ref:2693]
    /// segments from the last to the first, quads in order; each quad is one z-sort entry (command vertex order 1,0,3,2 like the
    /// road polygons, texture skd0..3, colour index FF transparent, bias 0x60000 = +4 m). [ref:2694]</summary>
    int EmitSkids(ZSort<DrawItem> zs, Camera cam, SkidBuffer skids)
    {
        int n = 0;
        var camFx = new Vec3((int)(cam.Position.X * 65536f), (int)(cam.Position.Y * 65536f), (int)(cam.Position.Z * 65536f));
        for (int i = skids.Count - 1; i >= 0; i--)
        {
            var seg = skids.Segments[i];
            if (!SkidMarks.Within(new Vec3(seg.X, seg.Y, seg.Z), camFx, 0x320000)) continue;
            for (int q = 0; q < seg.Quads; q++)
            {
                Vector3 V(int k)
                {
                    var p = SkidBuffer.Vertex(seg, q, k);
                    return cam.ToView(new Vector3(p.X / 65536f, p.Y / 65536f, p.Z / 65536f));
                }
                var a = V(1); var b = V(0); var c = V(3); var d = V(2);
                var tex = SkidTextures[seg.Texture[q] & 3];
                float slope = (float)cam.CullSlope;   // 1 = the original 90° cone; wider in a panorama
                if (Math.Abs(a.X) > a.Z * slope && Math.Abs(b.X) > b.Z * slope && Math.Abs(c.X) > c.Z * slope && Math.Abs(d.X) > d.Z * slope) continue;   // cone cull [ref:2695]
                if (zs.Insert((a.Z + b.Z + c.Z + d.Z) / 4, new DrawItem(a, b, c, d, -1, true, true, 0, null, tex), ZSort<DrawItem>.SkidBias)) n++;
            }
        }
        return n;
    }

    /// <summary>XBID 7/18/19 refs → XBID 8 structures. Type 1 (static) and type 4 [ref:2696]
    /// (dynamic, not hit: state −1) are placed unrotated at the ref position (pos + v·256). [ref:2697]
    /// Type 3 = CAN-animated object (AnimatedObjectPose, mode 1). Range cut in XZ, applied to COL globals only; block objects pass range −1. [ref:2698]
    /// Knocked-over type-4 objects: <see cref="KnockedObjectPose"/>.</summary>
    int EmitObjects(ZSort<DrawItem> zs, Camera cam, ObjectRef[] refs, TrackStructure[] structs, float range,
        ushort[]? ids = null, IReadOnlyDictionary<ushort, KnockedObject>? knocked = null, int tick = 0)
    {
        int n = 0;
        foreach (var r in refs)
        {
            if (r.Type is not (1 or 3 or 4) || r.StructIndex >= structs.Length) continue;
            if (r.Type == 3) // animated object: inline CAN, looping on (mode 1) [ref:2699]
            {
                if (AnimatedObjectPose.Eval(r.Raw, tick) is not var (am, ax, ay, az)) continue;
                var apos = new Vector3(ax, ay, az) / 65536f;
                var ad = apos - cam.Position;
                if (ad.X * ad.X + ad.Z * ad.Z > range * range) continue;
                var ast = structs[r.StructIndex];
                var awv = Scratch(ast.NumVertices);
                for (int i = 0; i < ast.NumVertices; i++)
                {
                    float x = ast.Vertices[i * 3] / 256f, y = ast.Vertices[i * 3 + 1] / 256f, z = ast.Vertices[i * 3 + 2] / 256f;
                    awv[i] = apos + new Vector3(x * am[0] + y * am[3] + z * am[6], x * am[1] + y * am[4] + z * am[7], x * am[2] + y * am[5] + z * am[8]) / 65536f;
                }
                n += EmitPolys(zs, cam, awv, ast.NumVertices, ast.Polygons);
                continue;
            }
            var pos = new Vector3(r.X / 65536f, r.Y / 65536f, r.Z / 65536f);
            var d = pos - cam.Position;
            if (d.X * d.X + d.Z * d.Z > range * range) continue;
            var st = structs[r.StructIndex];
            var wv = Scratch(st.NumVertices);
            int nv = st.NumVertices;
            // type 4: state of objId (XBID 11 point +0xE, index in ref +0x10): 0xFF = intact → unrotated; [ref:2700]
            // knocked (first hit of a type-2 object) → CAN tumble, then lying flat (−2)
            if (r.Type == 4 && ids != null && r.Raw[16] < ids.Length && knocked != null && knocked.TryGetValue(ids[r.Raw[16]], out var k) && tick >= k.HitTick)
            {
                var (m, ox, oy, oz, _) = KnockedObjectPose.Pose(k.CanIndex < _hitCan.Length ? _hitCan[k.CanIndex] : null,
                    tick - k.HitTick, k.Angle, KnockedObjectPose.Heading(st));
                var p = pos + new Vector3(ox, oy, oz) / 65536f;
                for (int i = 0; i < nv; i++)
                {
                    float x = st.Vertices[i * 3] / 256f, y = st.Vertices[i * 3 + 1] / 256f, z = st.Vertices[i * 3 + 2] / 256f;   // v·M (row vector)
                    wv[i] = p + new Vector3(x * m[0] + y * m[3] + z * m[6], x * m[1] + y * m[4] + z * m[7], x * m[2] + y * m[5] + z * m[8]) / 65536f;
                }
            }
            else
                for (int i = 0; i < nv; i++)
                    wv[i] = pos + new Vector3(st.Vertices[i * 3], st.Vertices[i * 3 + 1], st.Vertices[i * 3 + 2]) / 256f;
            n += EmitPolys(zs, cam, wv, nv, st.Polygons);
        }
        return n;
    }

    /// <summary>(8-byte polygons) → z-sort. [ref:2701]</summary>
    Vector3[] Scratch(int n)
    {
        if (_wvScratch.Length < n) _wvScratch = new Vector3[Math.Max(n, _wvScratch.Length * 2)];
        return _wvScratch;
    }

    int EmitPolys(ZSort<DrawItem> zs, Camera cam, Vector3[] wv, int nv, TrackPolygon[] polys, int bias = ZSort<DrawItem>.WorldBias)
    {
        int emitted = 0;
        {
            {
                foreach (var p in polys)
                {
                    if (p.V0 >= nv || p.V1 >= nv || p.V2 >= nv || p.V3 >= nv) continue;
                    // command vertex order idx[1], idx[0], idx[3], idx[2] → texture corners (0,0),(1,0),(1,1),(0,1)
                    var a = cam.ToView(wv[p.V1]); var bb = cam.ToView(wv[p.V0]);
                    var c = cam.ToView(wv[p.V3]); var d = cam.ToView(wv[p.V2]);
                    // passes coneCull=0 to DrawQuad [ref:2702]
                    // A road quad may span the entire view [ref:2703]
                    // even when every corner is outside |x| <= z; let DrawQuad clip it.
                    int ti = p.Texture;
                    byte rf = (uint)ti < (uint)_texRec.Length ? _texRec[ti].Unknown_0x02 : (byte)0;
                    var item = new DrawItem(a, bb, c, d, ti, (rf & 0x20) != 0, (rf & 0x10) != 0, FlatColor(ti, wv[p.V0], wv[p.V1], wv[p.V2]), Shade: (ushort)p.Flags);
                    if (zs.Insert((a.Z + bb.Z + c.Z + d.Z) / 4, item, bias)) emitted++;
                }
            }
        }
        return emitted;
    }

    void RenderList(FrameBuffer fb, Camera cam, ZSort<DrawItem> zs, RenderOptions opt, int tick, FillContext? ctx = null)
    {
        int drawn = 0;
        uint fogRgb = FogRgb;
        for (int k = zs.Capacity - 1; k >= 0; k--)
        for (int bi = zs.BucketCount(k) - 1; bi >= 0; bi--)
        {
            var it = zs.At(k, bi);
            int fog = opt.Fog ? FogLevel(k) : 0;
            if (ctx != null) ctx.FogBucketOffset = k - 2.0 * (it.Car != null ? it.V0.Z : (it.V0.Z + it.V1.Z + it.V2.Z + it.V3.Z) / 4);
            if (it.Shadow) { drawn += CarShadow.Draw(fb, cam, it.Car!, fog); continue; }
            if (it.Car != null) { drawn += DrawCar(fb, cam, it.Car, opt, fog, fogRgb, ctx); continue; }
            var tex = opt.Textured ? it.Direct ?? TextureFor(it.Tex, tick) : null;
            double w = tex?.Width ?? 1, h = tex?.Height ?? 1;
            double l0 = 1.0, l1 = 1.0, l2 = 1.0, l3 = 1.0;
            if (opt.TrackShade && !NFS.Render.TrackShade.IsNeutral(it.Shade))
            {
                NFS.Render.TrackShade.Factors256(it.Shade, out int fa, out int fb2, out int fc, out int fd);
                l0 = fa / 256.0; l1 = fb2 / 256.0; l2 = fc / 256.0; l3 = fd / 256.0;
            }
            ReadOnlySpan<(Vector3, double, double, double)> q = [(it.V0, 0.0, 0.0, l0), (it.V1, w, 0.0, l1), (it.V2, w, h, l2), (it.V3, 0.0, h, l3)];
            drawn += DrawQuad(fb, cam, q, tex, it.Flat, it.Transparent, !it.TwoSided && opt.BackfaceCull, fog, fogRgb, 0, ctx);
        }
        LastDrawn = drawn;
    }

    /// <summary>Car polygons. [deviation] the original orders them by the fixed part draw groups 0..30 (angle-dependent
    /// swaps for classes 4/5); here they are depth-sorted far → near inside the car entry.</summary>
    static readonly int[] IdentityQuad = [0, 1, 2, 3], MirroredQuad = [0, 3, 2, 1], MirroredTriangle = [2, 1, 0, 0];

    int DrawCar(FrameBuffer fb, Camera cam, CarInstance car, RenderOptions opt, int fog, uint fogRgb, FillContext? ctx = null)
    {
        var buf = _carBuf; buf.Clear();
        car.AppendPolys(buf, cam.Position);
        int cnt = buf.Count;
        if (_carKeys.Length < cnt)
        {
            int cap = Math.Max(cnt, _carKeys.Length * 2);
            _carKeys = new float[cap]; _carOrder = new int[cap]; _carView = new Vector3[cap * 4]; _carLight = new float[cap * 4];
        }
        float[]? light = null;
        if (opt.CarLight && ctx is { Shade: true })
        {
            light = _carLight;
            _carLighting.Compute(buf.P, cnt, cam.Position, light);
        }
        for (int i = 0; i < cnt; i++)
        {
            double sum = 0;   // = Enumerable.Average of the four view depths (double sum, float result)
            for (int k = 0; k < 4; k++)
            {
                var v = cam.ToView(buf.P[i * 4 + k]);
                _carView[i * 4 + k] = v;
                sum += v.Z;
            }
            _carKeys[i] = (float)(sum / 4);
            _carOrder[i] = i;
        }
        // far → near, equal keys by polygon index (= the former Comparison: float.CompareTo descending, then index):
        // one primitive ulong sort, no delegate. The comparison is a total order, so the result is identical.
        if (_carSortKeys.Length < cnt) _carSortKeys = new ulong[_carKeys.Length];
        var sk = _carSortKeys.AsSpan(0, cnt);
        for (int i = 0; i < cnt; i++) sk[i] = (ulong)~OrderedBits(_carKeys[i]) << 32 | (uint)i;
        sk.Sort();
        for (int i = 0; i < cnt; i++) _carOrder[i] = (int)(uint)sk[i];
        Span<(Vector3 P, double U, double V, double L)> q = stackalloc (Vector3 P, double U, double V, double L)[4];
        int n = 0;
        for (int oi = 0; oi < cnt; oi++)
        {
            int pi = _carOrder[oi];
            int vi = pi * 4;
            if (_carView[vi].Z < 0.05f && _carView[vi + 1].Z < 0.05f && _carView[vi + 2].Z < 0.05f && _carView[vi + 3].Z < 0.05f) continue;
            ref readonly var m = ref buf.Meta[pi];
            var tex = opt.Textured ? m.Tex : null;
            // bit 2 reverses the command's vertex order. [ref:2704]
            // Keep the existing vertex/UV association [ref:2705]
            // The reversal also changes the texture ref for mirrored faces. Bit 4 alone disables backface culling.
            // Drawing the reverse of the underbody over [ref:2706]
            // the visible side creates a black wedge when a car passes beside the camera.
            bool triangle = buf.P[vi + 2] == buf.P[vi + 3];
            int[] indices = m.Mirrored ? (triangle ? MirroredTriangle : MirroredQuad) : IdentityQuad;
            for (int k = 0; k < 4; k++)
            {
                int id = indices[k];
                // explicit texture coordinates of vertex id (CarMesh; the .geo gives (0,h), (w,h), (w,0), (0,0))
                var uv = buf.Uv[vi + id];
                q[k] = (_carView[vi + id], uv.X * m.W, uv.Y * m.H, light == null ? 1.0 : (double)light[pi * 4 + id]);
            }
            n += DrawQuad(fb, cam, q, tex, tex?.Average ?? 0x808080u, m.Transparent, !m.TwoSided, fog, fogRgb, 0, ctx);
        }
        return n;
    }

    /// <summary>Flat colour per texture (average texel, else hashed id), with a slope shade so the shape reads.</summary>
    uint FlatColor(int tex, Vector3 a, Vector3 b, Vector3 c)
    {
        uint baseCol = TextureFor(tex)?.Average ?? ((uint)tex * 2654435761u >> 8 | 0x404040u);
        var n = Vector3.Cross(b - a, c - a);
        float s = n.LengthSquared() > 0 ? 0.55f + 0.45f * MathF.Abs(Vector3.Normalize(n).Y) : 1f;
        return (uint)((int)((baseCol >> 16 & 255) * s) << 16 | (int)((baseCol >> 8 & 255) * s) << 8 | (int)((baseCol & 255) * s));
    }

    /// <summary>Subdivide near quads 2×2 (≤ 3 levels) then near-clip [ref:2707]
    /// project and fill. Returns 1 when something was rasterised.</summary>
    static int DrawQuad(FrameBuffer fb, Camera cam, ReadOnlySpan<(Vector3 P, double U, double V, double L)> q, Texture? tex, uint flat,
        bool transparent, bool cull, int fog, uint fogRgb, int level, FillContext? ctx = null)
    {
        int ks = SubdivScale(cam);
        // perspective-correct UV needs no deep subdivision (only the bilinear-ish map of non-parallelogram quads is refined)
        int maxLevel = ctx is { Perspective: true } ? PerspectiveSubdivLevels : ks > 1 ? 4 : 3;
        if (level < maxLevel && Math.Min(q[0].P.Z + q[2].P.Z, q[1].P.Z + q[3].P.Z) <= SubdivThresholdAt(level, ks))
        {
            var m01 = Mid(q[0], q[1]); var m12 = Mid(q[1], q[2]); var m23 = Mid(q[2], q[3]); var m30 = Mid(q[3], q[0]);
            var mc = Mid(m01, m23);
            int r = 0;
            r |= DrawQuad(fb, cam, [q[0], m01, mc, m30], tex, flat, transparent, cull, fog, fogRgb, level + 1, ctx);
            r |= DrawQuad(fb, cam, [m01, q[1], m12, mc], tex, flat, transparent, cull, fog, fogRgb, level + 1, ctx);
            r |= DrawQuad(fb, cam, [mc, m12, q[2], m23], tex, flat, transparent, cull, fog, fogRgb, level + 1, ctx);
            r |= DrawQuad(fb, cam, [m30, mc, m23, q[3]], tex, flat, transparent, cull, fog, fogRgb, level + 1, ctx);
            return r;
        }
        // near clip (Sutherland–Hodgman on z ≥ nz)
        const float nz = 0.05f;
        Span<ScreenVertex> outp = stackalloc ScreenVertex[8];
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i]; var b = q[(i + 1) & 3];
            bool ina = a.P.Z >= nz, inb = b.P.Z >= nz;
            if (ina) outp[n++] = Proj(cam, a.P, a.U, a.V, a.L);
            if (ina != inb)
            {
                float t = (nz - a.P.Z) / (b.P.Z - a.P.Z);
                outp[n++] = Proj(cam, Vector3.Lerp(a.P, b.P, t), a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t, a.L + (b.L - a.L) * t);
            }
        }
        if (n < 3) return 0;
        var poly = outp[..n];
        // [deviation] original rejects |sx|,|sy| > 0x1000 after its own subdivision; we near-clip in 3D instead.
        // a quad is a back face only when BOTH fan triangles (1,2,3) and (1,3,4) face away; [ref:2708]
        // a twisted (bow-tie) quad has one of each and is drawn. It cannot fill such a quad directly [ref:2709]
        // (returns < 0) → the caller splits it into two triangles.
        double a1 = Rasterizer.Area2(in poly[0], in poly[1], in poly[2]) * BackfaceSign, a2 = n >= 4 ? Rasterizer.Area2(in poly[0], in poly[2], in poly[3]) * BackfaceSign : 0;
        for (int i = 4; i < n; i++) a2 = Math.Min(a2, Rasterizer.Area2(in poly[0], in poly[i - 1], in poly[i]) * BackfaceSign);
        if (cull && a1 > 0 && (n < 4 || a2 > 0)) return 0;
        if (n == 4 && (a1 > 0) != (a2 > 0) && a1 != 0 && a2 != 0)
        {
            Fill(fb, poly[..3], tex, flat, transparent, fog, fogRgb, ctx);
            Span<ScreenVertex> tri = stackalloc ScreenVertex[3];
            tri[0] = poly[0]; tri[1] = poly[2]; tri[2] = poly[3];
            Fill(fb, tri, tex, flat, transparent, fog, fogRgb, ctx);
            return 1;
        }
        Fill(fb, poly, tex, flat, transparent, fog, fogRgb, ctx);
        return 1;
    }

    /// <summary>Subdivision levels of the Enhanced path with perspective-correct UV (thresholds scaled like the original ones).</summary>
    public static int PerspectiveSubdivLevels { get; set; } = 1;

    /// <summary>Flat polygon by the original rasteriser (the sky cones), recorded for the strip fill when one is active.</summary>
    static void FillOriginal(FrameBuffer fb, ReadOnlySpan<ScreenVertex> poly, uint rgb)
    {
        if (FillRecorder.Active is { } rec && rec.Target == fb) rec.AddOriginalFill(poly, null, rgb, false, 0, 0);
        else Rasterizer.Fill(fb, poly, null, rgb);
    }

    static void Fill(FrameBuffer fb, ReadOnlySpan<ScreenVertex> poly, Texture? tex, uint flat, bool transparent, int fog, uint fogRgb, FillContext? ctx)
    {
        if (FillRecorder.Active is { } rec && rec.Target == fb) { rec.AddFill(poly, tex, flat, transparent, fog, fogRgb, ctx); return; }
        if (ctx == null) Rasterizer.Fill(fb, poly, tex, flat, transparent, fog, fogRgb);
        else EnhancedRasterizer.Fill(fb, poly, tex, flat, transparent, fog, fogRgb, ctx);
    }

    /// <summary>Draws one flat quad given in camera space (vertices in command order) through the road-quad path
    /// (subdivision, near clip, back-face test, fill). Returns 1 when something was rasterised.</summary>
    public static int DrawFlatQuad(FrameBuffer fb, Camera cam, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, uint rgb, bool cull = true) =>
        DrawQuad(fb, cam, [(v0, 0.0, 0.0, 1.0), (v1, 1.0, 0.0, 1.0), (v2, 1.0, 1.0, 1.0), (v3, 0.0, 1.0, 1.0)], null, rgb, false, cull, 0, 0, 0);

    static (Vector3 P, double U, double V, double L) Mid((Vector3 P, double U, double V, double L) a, (Vector3 P, double U, double V, double L) b) =>
        ((a.P + b.P) / 2, (a.U + b.U) / 2, (a.V + b.V) / 2, (a.L + b.L) / 2);

    /// <summary>Sign of the screen area that marks a back face. [hypothesis — chosen empirically]</summary>
    public static int BackfaceSign { get; set; } = -1;

    static ScreenVertex Proj(Camera cam, Vector3 p, double u, double v, double l = 1)
    {
        var (x, y) = cam.Project(p);
        return new ScreenVertex(x, y, u, v) { W = 1.0 / p.Z, L = l };
    }

    /// <summary>ring of 16 quads around the camera (rotation only, no translation). Ring points [ref:2710]
    /// angle = start + i/16 turn, x = R·sin, z = R·cos, bottom y = base, top y = base + height. [ref:2711]
    /// They are rotated into view space, their depth is replaced by the "flat distance" (HRZ field 5 [ref:2712]
    /// ) and projected. Pixmap = textures 0..7 of TRxx0.QFS, shown on [ref:2713]
    /// segments 0..7 and mirrored (15-j) on 8..15 when the mirror flag is set, between the top ring and the line at
    /// "pixmap base" % of the height. Above (from 98 % of the height) a cone of the sky colour to the zenith point
    /// (0, base+10·h, 0), below that line a cone of the horizon colour to the nadir (0, base-10·h, 0). Black-horizon
    /// flag = whole view black. Tunnel variant (rear mirror only): see Render / IsTunnelNear; HORIZONDISPLAY: horizonDisplay = false.</summary>
    void DrawSky(FrameBuffer fb, Camera cam, bool horizonDisplay = true, FillContext? ctx = null)
    {
        var hrz = Hrz;
        if (hrz == null) { fb.Clear(80, 120, 200); return; }
        if (hrz.BlackHorizon != 0) { fb.Clear(0, 0, 0); return; }
        uint skyC = (uint)(hrz.SkyRgb[0] << 16 | hrz.SkyRgb[1] << 8 | hrz.SkyRgb[2]);
        uint horC = (uint)(hrz.HorizonRgb[0] << 16 | hrz.HorizonRgb[1] << 8 | hrz.HorizonRgb[2]);
        fb.Clear((byte)(skyC >> 16), (byte)(skyC >> 8), (byte)skyC);
        if (!horizonDisplay) return; // otherwise: flat quad over the whole view (last branch) [ref:2714]

        const int N = 16;
        double startTurns = (hrz.AngleDegrees * 65536 / 360) / 65536.0; // = (deg<<16)/360 [ref:2715]
        double baseY = hrz.BaseHeight, h = hrz.Height;
        double pixPct = (hrz.PixmapBasePercent * 65536 / 100) / 65536.0;
        const double topPct = 0xfae1 / 65536.0;
        double flat = hrz.FlatDistance;

        Span<Vector3> top = stackalloc Vector3[N + 1], bot = stackalloc Vector3[N + 1];   // view space (rotation only)
        for (int i = 0; i <= N; i++)
        {
            double a = 2 * Math.PI * (startTurns + i / (double)N);
            float x = (float)Math.Round(Math.Sin(a) * hrz.Radius), z = (float)Math.Round(Math.Cos(a) * hrz.Radius);
            bot[i] = ViewRot(cam, new Vector3(x, (float)baseY, z));
            top[i] = ViewRot(cam, new Vector3(x, (float)(baseY + h), z));
        }
        var zen = ViewRot(cam, new Vector3(0, (float)(baseY + h * 10), 0));
        var nad = ViewRot(cam, new Vector3(0, (float)(baseY - h * 10), 0));

        (double X, double Y) Flat(Vector3 v) => (v.X * cam.Fx / flat + cam.Cx, v.Y * cam.Fy / flat + cam.Cy);
        static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        Span<(double X, double Y)> sTop = stackalloc (double X, double Y)[N + 1], sBot = stackalloc (double X, double Y)[N + 1];
        Span<(double X, double Y)> sMid = stackalloc (double X, double Y)[N + 1], sHi = stackalloc (double X, double Y)[N + 1];
        for (int i = 0; i <= N; i++)
        {
            sTop[i] = Flat(top[i]); sBot[i] = Flat(bot[i]);
            sMid[i] = Lerp(sBot[i], sTop[i], pixPct); sHi[i] = Lerp(sBot[i], sTop[i], topPct);
        }
        var sZen = Flat(zen); var sNad = Flat(nad);
        double near = hrz.Radius / 4.0;

        static ScreenVertex V((double X, double Y) p, double u = 0, double v = 0) => new(p.X, p.Y, u, v);
        Span<ScreenVertex> q = stackalloc ScreenVertex[4];
        for (int j = 0; j < N; j++)
        {
            bool fTop = top[j].Z >= near || top[j + 1].Z >= near, fBot = bot[j].Z >= near || bot[j + 1].Z >= near;
            if (fTop) FillOriginal(fb, [V(sHi[j + 1]), V(sHi[j]), V(sZen)], skyC);
            if (fBot) FillOriginal(fb, [V(sMid[j]), V(sMid[j + 1]), V(sNad)], horC);
            if (!(fTop || fBot)) continue;
            bool mirrored = hrz.MirrorFlag != 0 && j >= 8;
            var tex = _skyTex[(mirrored ? N - 1 - j : j) % 8];
            if (tex == null) continue;
            double w = tex.Width, th = tex.Height;
            if (mirrored) { q[0] = V(sTop[j + 1], 0, 0); q[1] = V(sTop[j], w, 0); q[2] = V(sMid[j], w, th); q[3] = V(sMid[j + 1], 0, th); }
            else { q[0] = V(sTop[j], 0, 0); q[1] = V(sTop[j + 1], w, 0); q[2] = V(sMid[j + 1], w, th); q[3] = V(sMid[j], 0, th); }
            Fill(fb, q, tex, 0, true, 0, 0, ctx);
        }
    }

    /// <summary>rotation into view space without translation. [ref:2716]</summary>
    static Vector3 ViewRot(Camera cam, Vector3 w) => new(Vector3.Dot(w, cam.Right), Vector3.Dot(w, cam.Up), Vector3.Dot(w, cam.Forward));
}
