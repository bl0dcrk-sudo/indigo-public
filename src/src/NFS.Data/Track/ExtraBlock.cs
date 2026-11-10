namespace NFS.Data.Track;

/// <summary>Extrablock shared by TRK blocks and COL: <c>+0 u32 size, +4 u16 id, +6 u16 count, data from +8</c>.
/// Lookup by id (linear scan of the offset table). [ref:1038]</summary>
public sealed record ExtraBlock(
    int Offset,     // file offset of the extrablock header
    int Size,       // +0
    ushort Id,      // +4 (XBID)
    ushort Count,   // +6
    byte[] Data)    // +8 .. Size
{
    internal static ExtraBlock Read(byte[] d, int off)
    {
        int size = Bin.S32(d, off);
        Bin.Require(size >= 8 && off + size <= d.Length, $"extrablock @0x{off:X}: bad size {size}");
        return new ExtraBlock(off, size, Bin.U16(d, off + 4), Bin.U16(d, off + 6), d.AsSpan(off + 8, size - 8).ToArray());
    }

    /// <summary>Fixed-size records (Count × recordSize B) as spans.</summary>
    public ReadOnlySpan<byte> Record(int index, int recordSize) => Data.AsSpan(index * recordSize, recordSize);

    public ushort[] U16s() => Bin.U16Array(Data, 0, Count);

    // --- typed views ---

    /// <summary>XBID 4: neighbour/visible block list (draw order), ≤ 100.</summary>
    public ushort[] Neighbors() => U16s();

    /// <summary>XBID 5: one entry per hi-res polygon.</summary>
    public PolyAttr[] PolyAttrs()
    {
        var r = new PolyAttr[Count];
        for (int i = 0; i < Count; i++) r[i] = new PolyAttr(Data[2 * i], Data[2 * i + 1]);
        return r;
    }

    /// <summary>XBID 6: road columns per slice (8 B).</summary>
    public SliceRoad[] SliceRoads()
    {
        var r = new SliceRoad[Count];
        for (int i = 0; i < Count; i++)
        {
            int o = 8 * i;
            r[i] = new SliceRoad(Bin.U16(Data, o), Data[o + 2], Data[o + 3], Bin.S16(Data, o + 4), Bin.S16(Data, o + 6));
        }
        return r;
    }

    /// <summary>XBID 7 / 18 / 19 (TRK) and 7 (COL): object references, variable length (u16 size at +0).</summary>
    public ObjectRef[] ObjectRefs()
    {
        var r = new ObjectRef[Count];
        int o = 0;
        for (int i = 0; i < Count; i++)
        {
            int size = Bin.U16(Data, o);
            Bin.Require(size >= 16 && o + size <= Data.Length, $"XBID {Id}: bad object ref size {size}");
            r[i] = new ObjectRef(size, Data[o + 2], Data[o + 3], Bin.S32(Data, o + 4), Bin.S32(Data, o + 8), Bin.S32(Data, o + 12),
                Data.AsSpan(o, size).ToArray());
            o += size;
        }
        return r;
    }

    /// <summary>XBID 8: structures (object models), variable length (u32 size at +0).</summary>
    public TrackStructure[] Structures()
    {
        var r = new TrackStructure[Count];
        int o = 0;
        for (int i = 0; i < Count; i++)
        {
            int size = Bin.S32(Data, o);
            int nv = Bin.U16(Data, o + 4), np = Bin.U16(Data, o + 6);
            Bin.Require(size >= 8 + 6 * nv + 8 * np && o + size <= Data.Length, $"XBID 8: bad structure size {size}");
            var verts = Bin.S16Array(Data, o + 8, 3 * nv);
            r[i] = new TrackStructure(size, verts, TrackPolygon.ReadMany(Data, o + 8 + 6 * nv, np));
            o += size;
        }
        return r;
    }

    /// <summary>XBID 11: points 0x14 B (dynamic/hittable objects).</summary>
    public TrackPoint[] Points()
    {
        var r = new TrackPoint[Count];
        for (int i = 0; i < Count; i++)
        {
            int o = 20 * i;
            r[i] = new TrackPoint(Bin.S32(Data, o), Bin.S32(Data, o + 4), Bin.S32(Data, o + 8), Bin.U16(Data, o + 12),
                Bin.U16(Data, o + 14), Data[o + 16], Data[o + 17], Data[o + 18], Data[o + 19]);
        }
        return r;
    }

    /// <summary>XBID 13: VROAD 12 B per entry (s16 vectors, [prawdopodobne] normal + forward).</summary>
    public short[][] VRoad()
    {
        var r = new short[Count][];
        for (int i = 0; i < Count; i++) r[i] = Bin.S16Array(Data, 12 * i, 6);
        return r;
    }

    /// <summary>XBID 9: lane marks, 4 B each {u8 vtx, u8 slice, u8, u8}.</summary>
    public byte[][] LaneMarks()
    {
        var r = new byte[Count][];
        for (int i = 0; i < Count; i++) r[i] = Data.AsSpan(4 * i, 4).ToArray();
        return r;
    }

    /// <summary>COL XBID 2: texture records 10 B.</summary>
    public TextureRecord[] Textures()
    {
        var r = new TextureRecord[Count];
        for (int i = 0; i < Count; i++)
        {
            int o = 10 * i;
            r[i] = new TextureRecord(Bin.U16(Data, o), Data[o + 2], Data[o + 3], Data[o + 4], Data[o + 5], Data[o + 6], Data[o + 7],
                Bin.U16(Data, o + 8));
        }
        return r;
    }

    /// <summary>COL XBID 15: track slices ("virtual road") 36 B.</summary>
    public TrackSlice[] Slices()
    {
        var r = new TrackSlice[Count];
        for (int i = 0; i < Count; i++) r[i] = TrackSlice.Read(Data, 36 * i);
        return r;
    }
}

/// <summary>XBID 5 entry: +0 u8 VROAD index, +1 u8 flags (0x0E = not road; surface byte for physics).</summary>
public readonly record struct PolyAttr(byte VRoadIndex, byte Flags);

/// <summary>XBID 6 entry (8 B): +0 first hi-res polygon of the slice, +2 polygon (column) count, +3 ?, +4/+6 alternate
/// slices of a fork (-1 = none).</summary>
public readonly record struct SliceRoad(ushort FirstPoly, byte NumPolys, byte Unknown_0x03, short AltSlice1, short AltSlice2);

/// <summary>Object reference (XBID 7/18/19): +0 u16 size, +2 u8 type (1 static, 3 animated, 4 dynamic), +3 u8 structure
/// index, +4 int32 pos[3] (16.16); type 4: +0x10 u8 index into XBID 11. <see cref="Raw"/> = whole record.</summary>
public sealed record ObjectRef(int Size, byte Type, byte StructIndex, int X, int Y, int Z, byte[] Raw);

/// <summary>XBID 8 structure: +0 u32 size, +4 u16 nVerts, +6 u16 nPolys, +8 s16 v[3]×nVerts (1/256 m wzgl. pos), polys 8 B.</summary>
public sealed record TrackStructure(int Size, short[] Vertices, TrackPolygon[] Polygons)
{
    public int NumVertices => Vertices.Length / 3;
}

/// <summary>XBID 11 point (0x14 B): +0 int32 pos[3], +0xC u16, +0xE u16 object id, +0x10..+0x13 bytes (+0x13 type: 2 = hittable).</summary>
public readonly record struct TrackPoint(int X, int Y, int Z, ushort Unknown_0x0C, ushort ObjectId,
    byte Unknown_0x10, byte Unknown_0x11, byte Unknown_0x12, byte Type);

/// <summary>COL XBID 2 texture record (10 B): +0 u16 shape index in track QFS, +3 flags (0x10/0x40 mirror,
/// 0x02/0x04/0x08 rotate 90/180/270), +7 animation frame count (0 treated as 1).</summary>
public readonly record struct TextureRecord(ushort ShapeIndex, byte Unknown_0x02, byte Flags, byte Unknown_0x04,
    byte Unknown_0x05, byte Unknown_0x06, byte AnimFrames, ushort Unknown_0x08)
{
    /// <summary>Frames actually built (do-while loop: at least 1). [ref:1039]</summary>
    public int Frames => Math.Max(1, (int)AnimFrames);
}

/// <summary>Polygon 8 B: +0 s16 texture, +2 s16 flags/other texture, +4 u8 v[4] (quad).</summary>
public readonly record struct TrackPolygon(short Texture, short Flags, byte V0, byte V1, byte V2, byte V3)
{
    internal static TrackPolygon[] ReadMany(byte[] d, int o, int n)
    {
        var r = new TrackPolygon[n];
        for (int i = 0; i < n; i++, o += 8)
            r[i] = new TrackPolygon(Bin.S16(d, o), Bin.S16(d, o + 2), d[o + 4], d[o + 5], d[o + 6], d[o + 7]);
        return r;
    }
}

/// <summary>COL XBID 15 slice (36 B). Vectors scaled 126 ≈ 1.0; edges ×256 → 16.16 relative to the centre.</summary>
public sealed record TrackSlice(
    int X, int Y, int Z,            // +0 centre, 16.16
    sbyte[] Up,                     // +0xC
    sbyte[] Forward,                // +0xF
    sbyte[] Right,                  // +0x12
    byte Unknown_0x15,              // +0x15
    ushort BlockNumber,             // +0x16
    ushort Mask,                    // +0x18 bit mask tested by AI/physics [prawdopodobne]
    ushort LeftEdge,                // +0x1A
    ushort RightEdge,               // +0x1C
    byte Surroundings,              // +0x1E 2 × nibble left/right (2 = tunnel)
    byte Lanes,                     // +0x1F 2 × nibble [prawdopodobne]
    // +0x20/+0x21: road half widths left/right of the centre, units 0.5 m. [ref:1040]
    // Grid x: −b20·0x8000/2 on the left, +b21·0x8000/2 on the right. Previously named TvCamera/Unknown_0x21 [hipoteza, OpenNFS].
    byte HalfWidthLeft,             // +0x20
    byte HalfWidthRight,            // +0x21
    byte Unknown_0x22, byte Unknown_0x23)
{
    internal static TrackSlice Read(byte[] d, int o) => new(
        Bin.S32(d, o), Bin.S32(d, o + 4), Bin.S32(d, o + 8),
        [(sbyte)d[o + 12], (sbyte)d[o + 13], (sbyte)d[o + 14]],
        [(sbyte)d[o + 15], (sbyte)d[o + 16], (sbyte)d[o + 17]],
        [(sbyte)d[o + 18], (sbyte)d[o + 19], (sbyte)d[o + 20]],
        d[o + 21], Bin.U16(d, o + 22), Bin.U16(d, o + 24), Bin.U16(d, o + 26), Bin.U16(d, o + 28),
        d[o + 30], d[o + 31], d[o + 32], d[o + 33], d[o + 34], d[o + 35]);
}
