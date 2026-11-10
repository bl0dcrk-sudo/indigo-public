namespace NFS.Render;

/// <summary>
/// View draw ranges (view fields bw: +0xA4 global objects, +0xA8 blocks, +0xAC/+0xB0 LOD thresholds, +0xB4 polygon limit,
/// +0xB8 max. number of neighbours) – type 0 (full screen), 1/2 (split), 3 (rear mirror); [ref:2718]
/// scale (VIEWDISTANCE: 1.0 / 0.55 / 0.4, table) applies to blocks and LOD thresholds [ref:2719]
/// in split views also without a change of +0xA4. Car depth: (200 m, split and rear mirror 150 m). [ref:2720]
/// Values in 16.16 (as in the original); metres are <c>/ 65536f</c>.
/// </summary>
public readonly record struct ViewRange(int ObjectRange, int BlockRange, int LodNear, int LodMid, int CullLimit, int MaxNeighbours, int CarDepth)
{
    /// <summary>Type 0: 500 / 500 / 110 / 150 m, polygon limit 2000, 42 neighbours; cars up to 200 m.</summary>
    public static ViewRange Main => new(0x1F40000, 0x1F40000, 0x6E0000, 0x960000, 2000, 0x2A, 0xC80000);

    /// <summary>Type 1/2 (split screen): objects 400 m, blocks 440 m, LOD 60 / 92 m, polygon limit 700, 24 neighbours; cars up to 150 m.</summary>
    public static ViewRange Split => new(0x1900000, 0x1B80000, 0x3C0000, 0x5C0000, 700, 0x18, 0x960000);

    /// <summary>Type 3 (rear mirror): 150 / 100 / 40 / 80 m, polygon limit 250, 6 neighbours; cars up to 150 m.</summary>
    public static ViewRange Mirror => new(0x960000, 0x640000, 0x280000, 0x500000, 250, 6, 0x960000);

    /// <summary>VIEWDISTANCE scale: 1.0, 0.55, 0.4 (16.16). [ref:2721]</summary>
    public static readonly int[] DrawDistScale = [0x10000, 0x8CCC, 0x6666];

    /// <summary>Enhanced "Extended" view range: objects, blocks and cars are drawn up to <paramref name="factor"/> times as far (default 2 = 1000 m / 1000 m / 400 m);
    /// needs the wider z-sort (<see cref="ZSort{T}.ExtendedBuckets"/>). LOD thresholds and the polygon limit stay.</summary>
    public ViewRange Extended(double factor = 2) =>
        this with { ObjectRange = (int)(ObjectRange * factor), BlockRange = (int)(BlockRange * factor), CarDepth = (int)(CarDepth * factor) };

    public float ObjectRangeMetres => ObjectRange / 65536f;
    public float BlockRangeMetres => BlockRange / 65536f;
    public float CarDepthMetres => CarDepth / 65536f;

    /// <summary>Block range and LOD thresholds times the scale of level 0..2 (<c>(scale·value) &gt;&gt; 16</c>); [ref:2722]
    /// Object range (+0xA4), limits and car depth are unchanged. In the original this applies only to the main views (0/1), not the rear mirror.</summary>
    public ViewRange Scaled(int level)
    {
        long s = DrawDistScale[Math.Clamp(level, 0, 2)];
        return this with { BlockRange = (int)(s * BlockRange >> 16), LodNear = (int)(s * LodNear >> 16), LodMid = (int)(s * LodMid >> 16) };
    }
}
