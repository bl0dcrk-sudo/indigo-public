using NFS.Data.Track;

namespace NFS.Core;

/// <summary>
/// Road geometry of a loaded track as used by the locator (the ChunkList slot pointers of the original, all blocks
/// resident – no streaming): block reference points, hi-res vertices/polygons (group 4), XBID 5 (poly attributes),
/// XBID 6 (slice road records), XBID 13 (VROAD) and the COL slices (XBID 15).
/// </summary>
public sealed class TrackGeometry
{
    public TrackSlice[] Slices { get; }
    public int NumSlices => Slices.Length;
    public int NumBlocks { get; }
    public Vec3[] BlockRef { get; }
    public int[] FirstSlice { get; }
    public int[] NumStick { get; }
    public short[][] Verts { get; }
    public TrackPolygon[][] RoadPolys { get; }
    public PolyAttr[][] Attrs { get; }
    public SliceRoad[][] SliceRoads { get; }
    public short[][][] VRoads { get; }
    /// <summary>XBID 11 per block (hittable/dynamic object points, 0x14 B).</summary>
    public TrackPoint[][] DynPoints { get; }

    public TrackGeometry(Trk trk, TrackSlice[] slices)
    {
        Slices = slices;
        NumBlocks = trk.Blocks.Count;
        BlockRef = new Vec3[NumBlocks];
        FirstSlice = new int[NumBlocks];
        NumStick = new int[NumBlocks];
        Verts = new short[NumBlocks][];
        RoadPolys = new TrackPolygon[NumBlocks][];
        Attrs = new PolyAttr[NumBlocks][];
        SliceRoads = new SliceRoad[NumBlocks][];
        VRoads = new short[NumBlocks][][];
        DynPoints = new TrackPoint[NumBlocks][];
        for (int b = 0; b < NumBlocks; b++)
        {
            var blk = trk.Blocks[b];
            BlockRef[b] = new Vec3(trk.BlockRefs[b * 3], trk.BlockRefs[b * 3 + 1], trk.BlockRefs[b * 3 + 2]);
            FirstSlice[b] = blk.FirstSlice;
            NumStick[b] = blk.NumStickToNext;
            Verts[b] = blk.Vertices;
            RoadPolys[b] = blk.PolyGroups.Length > 4 ? blk.PolyGroups[4] : [];
            Attrs[b] = blk.Xb(5)?.PolyAttrs() ?? [];
            SliceRoads[b] = blk.Xb(6)?.SliceRoads() ?? [];
            VRoads[b] = blk.Xb(13)?.VRoad() ?? [];
            DynPoints[b] = blk.Xb(11)?.Points() ?? [];
        }
    }

    public Vec3 SliceCentre(int s) => new(Slices[s].X, Slices[s].Y, Slices[s].Z);

    /// <summary>XBID 6 record of a slice (block slot +0x1C, (slice − firstSlice)·8). [ref:0631]</summary>
    public SliceRoad SliceRoadOf(int slice)
    {
        int b = Slices[slice].BlockNumber;
        return SliceRoads[b][slice - FirstSlice[b]];
    }
}

/// <summary>
/// Car/camera position locator on the track, 1:1 with the original [ref:0632]
/// slice hill-climb over slice centres, global search every 8 slices [ref:0633]
/// point-in-quad on the hi-res road polygons of the slice (XBID 6 range), neighbour slice retries and branches.
/// </summary>
public sealed class TrackLocator(TrackGeometry g)
{
    /// <summary>Neighbour search offsets (8 × int32 from the original): +1, −2, +3, −4, +5, −6, +7, −8. [ref:0634]</summary>
    public static readonly int[] SearchSteps = [1, -2, 3, -4, 5, -6, 7, -8];

    public TrackGeometry Geometry => g;

    /// <summary><c>(dx&gt;&gt;9)² + (dz&gt;&gt;9)²</c>. [ref:0635]</summary>
    public static int Dist2Coarse(int sx, int sz, Vec3 p)
    {
        int dz = (p.Z - sz) >> 9, dx = (p.X - sx) >> 9;
        return dx * dx + dz * dz;
    }

    /// <summary>wrapped distance between two slices (<c>min(|a−b|, a + n−b)</c>). [ref:0636]</summary>
    public static int SliceDistance(int a, int b, int numSlices)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        int wrap = lo + (numSlices - hi);
        return Math.Min(wrap, hi - lo);
    }

    /// <summary><c>((dx&gt;&gt;12)²&gt;&gt;6) + ((dz&gt;&gt;12)²&gt;&gt;6)</c>. [ref:0637]</summary>
    public static int Dist2(int sx, int sz, Vec3 p)
    {
        int dz = (p.Z - sz) >> 12, dx = (p.X - sx) >> 12;
        return (dx * dx >> 6) + (dz * dz >> 6);
    }

    private int D2c(int s, Vec3 p) => Dist2Coarse(g.Slices[s].X, g.Slices[s].Z, p);
    private int D2(int s, Vec3 p) => Dist2(g.Slices[s].X, g.Slices[s].Z, p);

    /// <summary>Hill-climb over neighbouring slices (the loop wraps; −1 stays −1). [ref:0638]</summary>
    public void HillClimb(Vec3 p, TrackPos tp)
    {
        int n = g.NumSlices;
        int cur = tp.Slice, res = cur;
        if (cur != -1)
        {
            int i = cur;
            while (true)
            {
                int s = i >= n - 1 ? i % n : i;
                int d = D2c(s, p);
                int next;
                bool tryPrev;
                if (i < n - 2)
                {
                    tryPrev = !(D2c(i + 1, p) < d);
                    next = i + 1;
                }
                else
                {
                    tryPrev = d <= D2c((i + 1) % n, p);
                    next = (i + 1) % n;
                }
                if (tryPrev)
                {
                    if (i < 1)
                    {
                        int k = i + n;
                        if (D2c((k - 1) % n, p) < d) k--;
                        next = (k + n) % n;
                    }
                    else next = D2c(i - 1, p) < d ? i - 1 : i;
                }
                res = next;
                if (next == i) break;
                i = next;
            }
        }
        tp.Slice = res;
    }

    /// <summary>nearest of every 8th slice (one per block). [ref:0639]</summary>
    public void GlobalSearch(Vec3 p, TrackPos tp)
    {
        int best = -1, bd = int.MaxValue;
        for (int s = 0; s < g.NumSlices; s += 8)
        {
            int d = D2(s, p);
            if (d < bd) { best = s; bd = d; }
        }
        tp.Slice = best;
    }

    /// <summary>hill-climb, global search when dist² &gt; 0x10000; returns 1 if the [ref:0640]
    /// slice changed.</summary>
    public int UpdateSlice(Vec3 p, TrackPos tp)
    {
        int prev = tp.Slice;
        if (tp.Slice == -1) GlobalSearch(p, tp); // (original: hill-climb is a no-op and the slice −1 record is read)
        HillClimb(p, tp);
        if (D2(tp.Slice, p) > 0x10000)
        {
            GlobalSearch(p, tp);
            HillClimb(p, tp);
        }
        tp.Block = g.Slices[tp.Slice].BlockNumber;
        ushort ch = (ushort)(prev != tp.Slice ? 1 : 0);
        tp.SliceChanged = ch;
        tp.Unk6E = ch;
        return ch;
    }

    /// <summary>Position update: full locate, or slice only (when <paramref name="full"/> is false). [ref:0641]</summary>
    public int Update(Vec3 p, TrackPos tp, bool full = true)
    {
        tp.Mode = full ? 1 : 0;
        if (full) return Locate(p, tp);
        tp.State = 1;
        tp.Column = -1;
        tp.Xbid6Slice = null;
        tp.RoadPoly = null;
        tp.Xbid5Entry = null;
        return UpdateSlice(p, tp);
    }

    /// <summary>Finds the slice and polygon: branches (XBID 6 alt slices), retry 1/8 toward the slice centre. [ref:0642]</summary>
    public int Locate(Vec3 p, TrackPos tp)
    {
        int prev = tp.Slice;
        UpdateSlice(p, tp);
        FindRoadPoly(p, tp, true);
        if (tp.RoadPoly == null && tp.Xbid6Slice is SliceRoad sr && (sr.AltSlice1 != -1 || sr.AltSlice2 != -1))
        {
            var backup = Clone(tp);
            uint d1 = sr.AltSlice1 != -1 ? (uint)D2(sr.AltSlice1, p) : uint.MaxValue;
            uint d2 = sr.AltSlice2 != -1 ? (uint)D2(sr.AltSlice2, p) : uint.MaxValue;
            tp.Slice = d1 < d2 ? sr.AltSlice1 : sr.AltSlice2;
            UpdateSlice(p, tp);
            FindRoadPoly(p, tp, true);
            if (tp.Column != -1) goto done;
            CopyTo(backup, tp);
            tp.Xbid6Slice = null;
        }
        if (tp.RoadPoly == null)
        {
            var c = g.Slices[tp.Slice];
            var q = new Vec3(p.X + ((c.X - p.X) >> 3), p.Y, p.Z + ((c.Z - p.Z) >> 3));
            FindRoadPoly(q, tp, false);
        }
    done:
        tp.SliceChanged = (ushort)(prev != tp.Slice ? 1 : 0);
        return tp.Unk6E;
    }

    private void SetSliceInfo(TrackPos tp)
    {
        tp.Block = g.Slices[tp.Slice].BlockNumber;
        tp.Slot = tp.Block;
        tp.Xbid6Slice = g.SliceRoadOf(tp.Slice);
    }

    /// <summary>Point-in-quad search over the slice's road columns (early: stop once the distance starts to grow), forward [ref:0643]
    /// from the last column then backward, then neighbour slices per <see cref="SearchSteps"/>.</summary>
    public void FindRoadPoly(Vec3 p, TrackPos tp, bool early)
    {
        tp.Xbid5Entry = null;
        tp.RoadPoly = null;
        tp.Pos = p;
        tp.Unk6E = 1;
        int total = 0;
        for (int step = 0; ; step++)
        {
            SetSliceInfo(tp);
            int b = tp.Block;
            int nb = b + 1 > g.NumBlocks - 1 ? 0 : b + 1;
            Vec3 rb = g.BlockRef[b], rn = g.BlockRef[nb];
            short px = (short)((p.X - rb.X) >> 8), pz = (short)((p.Z - rb.Z) >> 8);
            short dx = (short)((rn.X - rb.X) >> 8), dz = (short)((rn.Z - rb.Z) >> 8);
            var sr = (SliceRoad)tp.Xbid6Slice!;
            int nPolys = sr.NumPolys;
            int first = nPolys - 1;
            int col0 = tp.Column < 0 ? 0 : tp.Column;
            if (col0 < first) first = tp.Column < 0 ? 0 : tp.Column;
            int count = nPolys - first;
            int last = int.MaxValue, it = 0;
            for (; it < count; it++)
            {
                int col = first + it;
                if (TestColumn(tp, b, sr.FirstPoly + col, px, pz, dx, dz, out int cx, out int cz)) { Hit(tp, b, nb, sr.FirstPoly + col, col); return; }
                int d = Math.Abs(pz - cz) + Math.Abs(px - cx);
                if (early && last < d) break;
                last = d;
            }
            if (tp.Column == -1) tp.Column = first + it - 1;
            int start = Math.Max(first - 1, -1);
            last = int.MaxValue;
            for (int k = 0; k < start + 1; k++)
            {
                int col = start - k;
                if (TestColumn(tp, b, sr.FirstPoly + col, px, pz, dx, dz, out int cx, out int cz)) { Hit(tp, b, nb, sr.FirstPoly + col, col); return; }
                int d = Math.Abs(pz - cz) + Math.Abs(px - cx);
                if (early && last < d) break;
                last = d;
            }
            Move(tp, SearchSteps[step]);
            total += SearchSteps[step];
            if (step == SearchSteps.Length - 1)
            {
                Move(tp, -total);
                SetSliceInfo(tp);
                tp.RoadPoly = null;
                tp.Xbid5Entry = null;
                tp.State = 1;
                tp.Column = -1;
                return;
            }
        }
    }

    private void Move(TrackPos tp, int delta)
    {
        int n = g.NumSlices;
        tp.Slice = ((tp.Slice + delta) % n + n) % n;
    }

    /// <summary>Tests the column with the inline XZ cross-product test (coordinates s16 &gt;&gt; 2; v0/v1 get the next-block [ref:0644]
    /// offset when they are stick-to-next vertices, v2/v3 never, as in the original). Polygons with XBID 5 flags 0x0E [ref:0645]
    /// do not count as a hit.</summary>
    private bool TestColumn(TrackPos tp, int b, int poly, short px, short pz, short dx, short dz, out int c0x, out int c0z)
    {
        var polys = g.RoadPolys[b];
        if ((uint)poly >= (uint)polys.Length) { c0x = c0z = 0; return false; }
        var q = polys[poly];
        var v = g.Verts[b];
        int ns = g.NumStick[b];
        short Vx(int i, bool off) => (short)(v[i * 3] + (off && i < ns ? dx : 0));
        short Vz(int i, bool off) => (short)(v[i * 3 + 2] + (off && i < ns ? dz : 0));
        short x0 = Vx(q.V0, true), z0 = Vz(q.V0, true), x1 = Vx(q.V1, true), z1 = Vz(q.V1, true);
        short x2 = Vx(q.V2, false), z2 = Vz(q.V2, false), x3 = Vx(q.V3, false), z3 = Vz(q.V3, false);
        tp.CornersS16[0] = x0; tp.CornersS16[2] = z0; tp.CornersS16[3] = x1; tp.CornersS16[5] = z1;
        tp.CornersS16[6] = x2; tp.CornersS16[8] = z2; tp.CornersS16[9] = x3; tp.CornersS16[11] = z3;
        c0x = x0; c0z = z0;
        int X = px >> 2, Z = pz >> 2;
        static bool Le(int ax, int az, int bx, int bz, int x, int z) => (bx - ax) * (z - az) - (x - ax) * (bz - az) <= 0;
        if (Le(x2 >> 2, z2 >> 2, x1 >> 2, z1 >> 2, X, Z) && Le(x1 >> 2, z1 >> 2, x0 >> 2, z0 >> 2, X, Z)
            && Le(x3 >> 2, z3 >> 2, x2 >> 2, z2 >> 2, X, Z) && Le(x0 >> 2, z0 >> 2, x3 >> 2, z3 >> 2, X, Z))
        {
            var attrs = g.Attrs[b];
            return poly < attrs.Length && attrs[poly].Flags != 0x0E;
        }
        return false;
    }

    private void Hit(TrackPos tp, int b, int nb, int poly, int col)
    {
        tp.Column = col;
        tp.State = 2;
        tp.RoadPoly = g.RoadPolys[b][poly];
        var a = g.Attrs[b][poly];
        tp.Xbid5Entry = a;
        var vr = g.VRoads[b];
        tp.VRoad = a.VRoadIndex < vr.Length ? vr[a.VRoadIndex] : null;
        // v*256 + ref of this block, or of the next one for stick-to-next vertices [ref:0646]
        var q = (TrackPolygon)tp.RoadPoly;
        var v = g.Verts[b];
        int ns = g.NumStick[b];
        Vec3 W(int i) { var r = i < ns ? g.BlockRef[nb] : g.BlockRef[b]; return new(r.X + v[i * 3] * 256, r.Y + v[i * 3 + 1] * 256, r.Z + v[i * 3 + 2] * 256); }
        tp.Corners[0] = W(q.V0); tp.Corners[1] = W(q.V1); tp.Corners[2] = W(q.V2); tp.Corners[3] = W(q.V3);
    }

    /// <summary>Surface flags byte under the locator (strict): 1 when no polygon. [ref:0647]</summary>
    public static int SurfaceFlags(TrackPos tp, bool strict = true)
    {
        if (strict && tp.RoadPoly == null) { tp.Mode = -1; return 1; }
        return tp.Xbid5Entry is PolyAttr a ? a.Flags : 1;
    }

    public static TrackPos Clone(TrackPos s)
    {
        var d = new TrackPos();
        CopyTo(s, d);
        return d;
    }

    public static void CopyTo(TrackPos s, TrackPos d)
    {
        d.Pos = s.Pos; d.Slice = s.Slice; d.RoadPoly = s.RoadPoly; d.Xbid5Entry = s.Xbid5Entry; d.VRoad = s.VRoad;
        d.Xbid6Slice = s.Xbid6Slice; s.Corners.CopyTo(d.Corners, 0); s.CornersS16.CopyTo(d.CornersS16, 0);
        d.State = s.State; d.SliceChanged = s.SliceChanged; d.Unk6E = s.Unk6E; d.Column = s.Column; d.Block = s.Block;
        d.Slot = s.Slot; d.Mode = s.Mode;
    }
}
