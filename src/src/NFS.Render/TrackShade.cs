namespace NFS.Render;

/// <summary>
/// Decoder of the u16 track polygon "shade" (<c>TrackPolygon.Flags</c>, TRK and XBID 8 polygons) used by the Enhanced
/// "TrackShade" option. Bits 4-6, 7-9, 10-12, 13-15 = corner intensity 0..7 (0 = dark, 7 = full); [ref:2717]
/// bits 0-3 are unknown and ignored. Corner k matches the command vertex order of <c>TrackRenderer.EmitPolys</c>:
/// k=0 ↔ a = V1, k=1 ↔ bb = V0, k=2 ↔ c = V3, k=3 ↔ d = V2. No allocations.
/// </summary>
public static class TrackShade
{
    /// <summary>Default minimum brightness (corner 0) – an appearance choice, not an RE value.</summary>
    public const float DefaultLmin = 0.35f;
    /// <summary>Fixed-point one (factor for a fully lit corner).</summary>
    public const int One = 256;
    /// <summary>Default minimum factor in 0..256 (0.35 → 90).</summary>
    public const int DefaultLmin256 = 90;
    /// <summary>Shade value of a polygon without shading (all corners 7).</summary>
    public const ushort Neutral = 0xFFFF;

    /// <summary>Corner intensity 0..7 for corner k (0..3, EmitPolys order a, bb, c, d).</summary>
    public static int Corner(int shade, int k) => (shade >> (4 + 3 * (k & 3))) & 7;

    /// <summary>Decodes the 4 corner intensities (EmitPolys order a, bb, c, d).</summary>
    public static void Decode(int shade, out int a, out int bb, out int c, out int d)
    {
        a = (shade >> 4) & 7; bb = (shade >> 7) & 7; c = (shade >> 10) & 7; d = (shade >> 13) & 7;
    }

    /// <summary>True when all 4 corners are 7 (bits 0-3 ignored).</summary>
    public static bool IsNeutral(int shade) => (shade & 0xFFF0) == 0xFFF0;

    /// <summary>L(c) = Lmin + (1-Lmin)*c/7 as 8.8 fixed point (0..256); c is clamped to 0..7, c=7 gives exactly 256.</summary>
    public static int Factor256(int c, int lmin256 = DefaultLmin256)
    {
        c = c < 0 ? 0 : c > 7 ? 7 : c;
        lmin256 = lmin256 < 0 ? 0 : lmin256 > One ? One : lmin256;
        return lmin256 + ((One - lmin256) * c + 3) / 7;
    }

    /// <summary>Floating point L(c) = Lmin + (1-Lmin)*c/7.</summary>
    public static float Factor(int c, float lmin = DefaultLmin)
    {
        c = c < 0 ? 0 : c > 7 ? 7 : c;
        return lmin + (1f - lmin) * c / 7f;
    }

    /// <summary>Decodes a polygon shade straight to 4 factors (8.8) in EmitPolys order.</summary>
    public static void Factors256(int shade, out int a, out int bb, out int c, out int d, int lmin256 = DefaultLmin256)
    {
        Decode(shade, out int ia, out int ib, out int ic, out int id);
        a = Factor256(ia, lmin256); bb = Factor256(ib, lmin256); c = Factor256(ic, lmin256); d = Factor256(id, lmin256);
    }
}
