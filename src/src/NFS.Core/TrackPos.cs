namespace NFS.Core;

/// <summary>Marks a field with its byte offset and size in the original structure (for documentation and the
/// layout consistency tests; the C# objects are not byte-packed).</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class OrigAttribute(int offset, int size) : Attribute
{
    public int Offset { get; } = offset;
    public int Size { get; } = size;
}

/// <summary>
/// Track locator (0x80 B; in a car at <c>car+8</c>, camera <c>cam+0x54</c>, view <c>view+4</c>). [ref:0648]
/// Filled by <see cref="TrackLocator"/>. Pointers into [ref:0649]
/// track data hold the NFS.Data records: RoadPoly = <c>TrackPolygon</c>, Xbid5Entry = <c>PolyAttr</c>,
/// VRoad = <c>short[6]</c>, Xbid6Slice = <c>SliceRoad</c>; Slot = block number (all blocks resident, no ChunkList).
/// </summary>
public sealed class TrackPos
{
    public const int Size = 0x80;

    [Orig(0x00, 12)] public Vec3 Pos;             // copy of the query position (16.16)
    [Orig(0x0C, 4)] public int Slice;             // current slice (car+0x14)
    [Orig(0x10, 4)] public object? RoadPoly;      // ptr road polygon (8 B) or null (car+0x18; "trk.poly == 0" tests)
    [Orig(0x14, 4)] public object? Xbid5Entry;    // ptr XBID5 entry (surface)
    [Orig(0x18, 4)] public object? VRoad;         // ptr VROAD record (car+0x20)
    [Orig(0x1C, 4)] public object? Xbid6Slice;    // ptr XBID6 slice record (alt1 s16 +4, alt2 s16 +6) (car+0x24)
    [Orig(0x20, 48)] public Vec3[] Corners = new Vec3[4];   // 4 quad corners in world space 16.16 (car+0x28)
    [Orig(0x50, 24)] public short[] CornersS16 = new short[12]; // same 4 corners as s16 relative to block ref
    [Orig(0x68, 4)] public int State;             // 1 = off road / unknown, 2 = on a polygon
    [Orig(0x6C, 2)] public ushort SliceChanged;   // slice changed this update
    [Orig(0x6E, 2)] public ushort Unk6E;          // ≠0 → runs (car+0x76) [meaning unknown] [ref:0650]
    [Orig(0x70, 4)] public int Column;            // polygon index within the slice (−1 none)
    [Orig(0x74, 4)] public int Block;
    [Orig(0x78, 4)] public int Slot;              // chunk-list slot of the block (car+0x80)
    [Orig(0x7C, 4)] public int Mode;
}
