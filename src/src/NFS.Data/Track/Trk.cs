namespace NFS.Data.Track;

/// <summary>TRK block (8 slices). Offsets relative to the block start; geometry (+0x40) as the original reads it
/// (polygons start at +0x58 + 6·(nStick + nHiV)). [ref:1051]</summary>
public sealed record TrkBlock(
    int Index,
    int Offset,               // file offset
    int Size,                 // +0
    int Size2,                // +4
    ushort NumExtraBlocks,    // +8
    ushort FirstSlice,        // +0xA
    uint Serial,              // +0xC (= block number)
    int[] ClipRect,           // +0x10 int32[4][3] clip rectangle corners (16.16)
    int XbTableOffset,        // +0x40 (table at block + 0x40 + value; entries relative to the block)
    ushort NumStickToNext,    // +0x44 vertices relative to the NEXT block's reference point (seams)
    ushort NumLoV,            // +0x46
    ushort NumMedV,           // +0x48
    ushort NumHiV,            // +0x4A
    ushort[] PolyCounts,      // +0x4C u16[6]: LOD pairs 0/1 = far, 2/3 = medium, 4/5 = near (surfaces/raised scenery)
    short[] Vertices,         // +0x58 s16 x,y,z × (nStick + nHiV); world = ref + v*256
    TrackPolygon[][] PolyGroups,
    IReadOnlyList<ExtraBlock> ExtraBlocks)
{
    public int NumVertices => Vertices.Length / 3;
    public ExtraBlock? Xb(int id) => ExtraBlocks.FirstOrDefault(x => x.Id == id);

    /// <summary>Vertex count used by LOD group 0/2/4: nStick + nLo/nMed/nHi. [ref:1052]</summary>
    public int LodVertexCount(int group) => NumStickToNext + group switch { 0 => NumLoV, 2 => NumMedV, _ => NumHiV };
}

/// <summary>TRK file ('TRAC', ver 0x16) — superblock stream. Header 0x20: +0 'TRAC', +4 version, +8..+0x14 4 dwords
/// (read by stmrd, ignored), +0x18 nSuperBlocks, +0x1C nBlocks; then u32 superOffs[nSuper] (absolute),
/// int32 blockRef[nBlocks][3] (16.16), u16 superOfBlock[nBlocks]. Superblock: +0 size, +4 nBlocks, +8 pad,
/// +0xC u32 blockOffs[] (relative to the superblock).</summary>
public sealed class Trk
{
    public const int SlicesPerBlock = 8;

    public uint Version { get; }                 // +4 (0x16)
    public uint[] Unknown_0x08 { get; }          // +8..+0x14
    public int[] SuperBlockOffsets { get; }
    public int[] SuperBlockSizes { get; }
    public int[] BlockRefs { get; }              // nBlocks × 3 (x,y,z 16.16) = StmCenter
    public ushort[] SuperOfBlock { get; }        // StmMetaI
    public IReadOnlyList<TrkBlock> Blocks { get; }

    public int NumSlices => Blocks.Count * SlicesPerBlock;

    private Trk(byte[] d)
    {
        Bin.Require(Bin.HasTag(d, 0, "TRAC"), "not a TRK (TRAC)");
        Version = Bin.U32(d, 4);
        Unknown_0x08 = [Bin.U32(d, 8), Bin.U32(d, 12), Bin.U32(d, 16), Bin.U32(d, 20)];
        int ns = Bin.S32(d, 0x18), nb = Bin.S32(d, 0x1C);
        int p = 0x20;
        SuperBlockOffsets = Bin.S32Array(d, p, ns); p += 4 * ns;
        BlockRefs = Bin.S32Array(d, p, 3 * nb); p += 12 * nb;
        SuperOfBlock = Bin.U16Array(d, p, nb);

        var blocks = new List<TrkBlock>(nb);
        SuperBlockSizes = new int[ns];
        for (int s = 0; s < ns; s++)
        {
            int so = SuperBlockOffsets[s];
            SuperBlockSizes[s] = Bin.S32(d, so);
            int n = Bin.S32(d, so + 4);
            for (int i = 0; i < n; i++)
                blocks.Add(ReadBlock(d, blocks.Count, so + Bin.S32(d, so + 12 + 4 * i)));
        }
        Bin.Require(blocks.Count == nb, $"TRK: {blocks.Count} blocks in superblocks, header says {nb}");
        Blocks = blocks;
    }

    public static Trk Parse(byte[] data) => new(data);
    public static Trk Load(string relative, string? gameDir = null) => new(GameFiles.Read(relative, gameDir));

    private static TrkBlock ReadBlock(byte[] d, int index, int b)
    {
        int nx = Bin.U16(d, b + 8);
        int xbTbl = Bin.S32(d, b + 0x40);
        int nStick = Bin.U16(d, b + 0x44), nHi = Bin.U16(d, b + 0x4A);
        // reads a pair per LOD, including count at +0x56. [ref:1053]
        var counts = Bin.U16Array(d, b + 0x4C, 6);
        int nv = nStick + nHi;
        var verts = Bin.S16Array(d, b + 0x58, 3 * nv);
        int pp = b + 0x58 + 6 * nv;
        var groups = new TrackPolygon[6][];
        for (int g = 0; g < 6; g++) { groups[g] = TrackPolygon.ReadMany(d, pp, counts[g]); pp += 8 * counts[g]; }
        var xbs = new ExtraBlock[nx];
        for (int i = 0; i < nx; i++) xbs[i] = ExtraBlock.Read(d, b + Bin.S32(d, b + 0x40 + xbTbl + 4 * i));
        var clip = Bin.S32Array(d, b + 0x10, 12);
        return new TrkBlock(index, b, Bin.S32(d, b), Bin.S32(d, b + 4), (ushort)nx, Bin.U16(d, b + 0xA), Bin.U32(d, b + 0xC),
            clip, xbTbl, (ushort)nStick, Bin.U16(d, b + 0x46), Bin.U16(d, b + 0x48), (ushort)nHi, counts, verts, groups, xbs);
    }
}
