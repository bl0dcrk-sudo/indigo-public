using System.Numerics;
using NFS.Data.Track;

namespace NFS.Render;

/// <summary>
/// Road marking lines (lane lines, centre dashes), called for the block's [ref:2540]
/// XBID 9 only at the near LOD, group 4; plus the five polygon splitters for (type 3, = type 4, = type 2 [ref:2541]
/// = type 1, = type 0; types 5..9 call splitters 0..4 with flag 1) and the textures lin0..7 of DTEXTURE.FSH. [ref:2542]
///
/// XBID 9 entry (4 B): u8 vertex index, u8 slice (+ the block's first slice, header +0xA), u8 type (0..9; 0xFF = end of a strip, no mark), u8 (unused here).
/// Entry e and e+1 make one stripe: slot 0 = A − w, slot 1 = A − w + dA, slot 2 = B + w, slot 3 = B + w + dB with w = the slice's forward vector / 2 (slice +0xF, 1/256 m
/// units: a quarter metre of overshoot) of the slice of A and d = the slice's right vector (+0x12, ≈ 0.5 m wide). All in s16 (1/256 m) with wrap-around like the original.
/// </summary>
public static class LaneMarks
{
    /// <summary>One textured polygon (corner order = the draw order of a record: the 4 vertex pointers +0xC..+0x18).</summary>
    public readonly record struct Quad(int Texture, Vector3 A, Vector3 B, Vector3 C, Vector3 D);

    /// <summary>(first 10 words): index = flag | (x+z of the entry's vertex &amp; 6) → texture lin(n); the second polygon of a pair uses [ref:2543]
    /// <c>Table[Table[i] + 2]</c> (n·4). [ref:2544]</summary>
    static readonly int[] Table = [2, 3, 4, 5, 6, 7, 4, 5, 2, 3];

    /// <summary>view z of the record's 2nd vertex ≤ 0x6E0000 (110 m; shift = 0) → split polygons, otherwise one flat quad. [ref:2545]</summary>
    public const float DetailZMetres = 110f;

    /// <summary>Slot vertices (4 per entry, s16 x y z; the last entry has no partner and stays 0). [ref:2546]</summary>
    /// <param name="entries">XBID 9 data (count × 4 B).</param>
    /// <param name="verts">Block vertices (s16 x y z), the first <paramref name="numStick"/> relative to the next block.</param>
    /// <param name="curRef">This block's reference point (16.16 x y z), <paramref name="nextRef"/> the next block's.</param>
    public static short[] BuildSlots(ReadOnlySpan<byte> entries, int count, short[] verts, int numStick, IReadOnlyList<TrackSlice> slices, int firstSlice,
        ReadOnlySpan<int> curRef, ReadOnlySpan<int> nextRef)
    {
        var slots = new short[count * 12];
        // (short)((next − cur) >> 8), then + (v >> 9): the original's reference delta in 1/256 m (a 0.2% overshoot – kept)
        int dx = (short)((nextRef[0] - curRef[0]) >> 8), dy = (short)((nextRef[1] - curRef[1]) >> 8), dz = (short)((nextRef[2] - curRef[2]) >> 8);
        dx += dx >> 9; dy += dy >> 9; dz += dz >> 9;
        for (int e = 0; e + 1 < count; e++)
        {
            int ia = entries[4 * e], ib = entries[4 * e + 4];
            var sa = Slice(slices, firstSlice + entries[4 * e + 1]); var sb = Slice(slices, firstSlice + entries[4 * e + 5]);
            if (sa == null || sb == null || (ia + 1) * 3 > verts.Length || (ib + 1) * 3 > verts.Length) continue;
            int wx = sa.Forward[0] >> 1, wy = sa.Forward[1] >> 1, wz = sa.Forward[2] >> 1;
            int ax = verts[3 * ia], ay = verts[3 * ia + 1], az = verts[3 * ia + 2];
            int bx = verts[3 * ib], by = verts[3 * ib + 1], bz = verts[3 * ib + 2];
            if (ia < numStick) { ax += dx; ay += dy; az += dz; }
            if (ib < numStick) { bx += dx; by += dy; bz += dz; }
            int o = 12 * e;
            slots[o] = (short)(ax - wx); slots[o + 1] = (short)(ay - wy); slots[o + 2] = (short)(az - wz);
            slots[o + 3] = (short)(ax + sa.Right[0] - wx); slots[o + 4] = (short)(ay + sa.Right[1] - wy); slots[o + 5] = (short)(az + sa.Right[2] - wz);
            slots[o + 6] = (short)(bx + wx); slots[o + 7] = (short)(by + wy); slots[o + 8] = (short)(bz + wz);
            slots[o + 9] = (short)(bx + sb.Right[0] + wx); slots[o + 10] = (short)(by + sb.Right[1] + wy); slots[o + 11] = (short)(bz + sb.Right[2] + wz);
        }
        return slots;
    }

    static TrackSlice? Slice(IReadOnlyList<TrackSlice> slices, int i) => (uint)i < (uint)slices.Count ? slices[i] : null;

    /// <summary>Sort key of an entry (record +8): x + z of its block vertex (raw s16); bits 1..2 pick the texture variant.</summary>
    public static int Key(short[] verts, int vertexIndex) =>
        (uint)(vertexIndex * 3 + 2) < (uint)verts.Length ? verts[3 * vertexIndex] + verts[3 * vertexIndex + 2] : 0;

    /// <summary>The polygons of one record split by type (→ the handler for the type); corners a,b,c,d = slot 1, 0, 2, 3 in view space. [ref:2547]
    /// Writes 1 or 2 quads to <paramref name="outQuads"/> and returns the count. <paramref name="viewZ"/> = view z (m) of corner b.</summary>
    public static int Split(int type, int key, float viewZ, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Span<Quad> outQuads)
    {
        if (viewZ > DetailZMetres)
        {
            // beyond 110 m: one flat quad, lin0 (types 0..4) or lin1 (5..7)
            outQuads[0] = new Quad(type < 5 ? 0 : 1, a, b, c, d);
            return 1;
        }
        int flag = type >= 5 ? 1 : 0, handler = type >= 5 ? type - 5 : type;
        int t = Table[flag | (key & 6)], t2 = Table[t + 2];
        // 5/16 = (x >> 4) + (x >> 2) of the original (16.16 integers; float here)
        const float F = 5f / 16f;
        Vector3 d34 = (b - a) * F, d56 = (c - d) * F;
        Vector3 e3 = d + d56, e2 = c - d56;      // on the edge d→c, 5/16 from either end
        Vector3 e0 = a + d34, e1 = b - d34;      // on the edge a→b
        switch (handler)
        {
            case 0:   // the middle part [ref:2548]
                outQuads[0] = new Quad(t, e0, e1, e2, e3); return 1;
            case 1:   // from the middle of the strip to the far end [ref:2549]
                outQuads[0] = new Quad(t, (e0 + e3) * 0.5f, (e1 + e2) * 0.5f, e2, e3); return 1;
            case 2:   // the two 5/16 end strips [ref:2550]
                outQuads[0] = new Quad(t, a, e0, e3, d); outQuads[1] = new Quad(t2, e1, b, c, e2); return 2;
            case 3:   // [ref:2551]
                outQuads[0] = new Quad(t, a, e0, e3, d); outQuads[1] = new Quad(t2, (e1 + e2) * 0.5f, (b + c) * 0.5f, c, e2); return 2;
            default:  // (type 4) [ref:2552]
                outQuads[0] = new Quad(t, (a + d) * 0.5f, (e0 + e3) * 0.5f, e3, d); outQuads[1] = new Quad(t2, e1, b, c, e2); return 2;
        }
    }
}
