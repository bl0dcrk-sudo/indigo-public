namespace NFS.Data.Track;

/// <summary>
/// TRK XBID 17: ambient sound source of a block (keeps the extrablock pointer in slot +0x2C). [ref:1033]
/// Data (after the 8-byte header) = 16 B: <c>+0 int32 x,y,z</c> absolute world position [ref:1034]
/// (16.16, checked against the block reference point in the data), <c>+0xC u16 sample</c> (slot in bank TRAMBnn = bank 5),
/// <c>+0xE u16 type</c> (1 = random one-shot, other = looped source). Every block of the original [ref:1035]
/// tracks has Count = 1 (size 24); the original reads only the first record [certain: the source dereferences the header]. [ref:1036]
/// </summary>
public readonly record struct AmbientSource(int X, int Y, int Z, int Sample, int Type)
{
    public const int RecordSize = 16;
    public const int ExtraBlockId = 17;

    /// <summary>Type 1 = fires random one-shots (voice 0xF); any other value = looping source (voice 0xE).</summary>
    public bool IsRandomShot => Type == 1;

    public static AmbientSource? Read(ExtraBlock? xb)
    {
        if (xb == null || xb.Id != ExtraBlockId || xb.Count < 1 || xb.Data.Length < RecordSize) return null;
        var d = xb.Data;
        return new AmbientSource(Bin.S32(d, 0), Bin.S32(d, 4), Bin.S32(d, 8), Bin.U16(d, 12), Bin.U16(d, 14));
    }

    /// <summary>The source of every block (null = block without XBID 17).</summary>
    public static AmbientSource?[] ForTrack(Trk trk)
    {
        var r = new AmbientSource?[trk.Blocks.Count];
        for (int i = 0; i < r.Length; i++) r[i] = Read(trk.Blocks[i].Xb(ExtraBlockId));
        return r;
    }

    /// <summary>Neighbour list of every block (XBID 4; max 100, empty when missing). [ref:1037]</summary>
    public static ushort[][] NeighborsForTrack(Trk trk) =>
        trk.Blocks.Select(b => b.Xb(4)?.Neighbors() ?? []).ToArray();
}
