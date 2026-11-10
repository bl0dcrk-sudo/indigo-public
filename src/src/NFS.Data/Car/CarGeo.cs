using System.Text;

namespace NFS.Data.Car;

/// <summary>Car model polygon (12 B): +0 u8 flags (bit0 triangle, bit1 rim offset, bit2 mirrored, bit3 transparent,
/// bit4 two-sided, bits 5–7 animation frame count), +1 u8 frame (runtime), +2 u16 (runtime: ref index),
/// +4 u8 idx[4], +8 char[4] FSH shape name.</summary>
public readonly record struct GeoPolygon(byte Flags, byte Frame, ushort RefIndex, byte V0, byte V1, byte V2, byte V3, string Texture)
{
    public bool IsTriangle => (Flags & 1) != 0;
    public bool IsMirrored => (Flags & 4) != 0;
    public bool IsTransparent => (Flags & 8) != 0;
    public bool IsTwoSided => (Flags & 0x10) != 0;
    public int AnimFrames => Flags >> 5;
}

/// <summary>One of 32 parts (header 0x34 B, then verts, then polys). Parts: 0–8 body, 9 moving part, 10 extra,
/// 11 lights, 12–19 wheels (rim/tyre), 20–23 LOD1 body, 24–25 LOD2.</summary>
public sealed record GeoPart(
    int Index,
    int NumVerts,              // +0
    int NumPolys,              // +4
    int X, int Y, int Z,       // +8/+0xC/+0x10 part centre relative to the car, 16.16 m
    uint Unknown_0x14,         // +0x14 runtime ptr verts
    uint Unknown_0x18,         // +0x18 runtime ptr polys
    ushort Unknown_0x1C,       // +0x1C
    ushort Unknown_0x1E,       // +0x1E
    uint DrawGroup,            // +0x20
    ushort[] AngleRanges,      // +0x24..+0x2F u16[6] (F355 part 0: 1,0,0,0,1,0; overwritten at run time) [ref:0962]
    uint Unknown_0x30,         // +0x30
    short[] Vertices,          // NumVerts × s16[3], 1/256 m relative to the part position (storage padded to even count)
    GeoPolygon[] Polygons);

/// <summary>Car geometry <c>&lt;NAME&gt;.geo</c> from CARDATA.VIV. Header 0x8C: +0 u32 (2 for cars, 0/1 for objects like BOX/CART — meaning unknown) [ref:0963]
/// +4 u32[32] runtime part pointers, +0x84 u32 runtime ref base, +0x88 u32 runtime FSH; then 32 parts.</summary>
public sealed class CarGeo
{
    public const int NumParts = 32;
    public uint Unknown_0x00 { get; }
    public IReadOnlyList<GeoPart> Parts { get; }
    public int ParsedLength { get; }

    private CarGeo(byte[] d)
    {
        Unknown_0x00 = Bin.U32(d, 0);
        int p = 0x8C;
        var parts = new GeoPart[NumParts];
        for (int i = 0; i < NumParts; i++)
        {
            int nv = Bin.S32(d, p), np = Bin.S32(d, p + 4);
            Bin.Require(nv >= 0 && np >= 0 && nv < 4096 && np < 4096, $"geo part {i}: bad counts");
            var ranges = Bin.U16Array(d, p + 0x24, 6);
            int vo = p + 0x34;
            var verts = Bin.S16Array(d, vo, 3 * nv);
            int po = vo + 6 * ((nv + 1) & ~1);
            var polys = new GeoPolygon[np];
            for (int k = 0; k < np; k++, po += 12)
                polys[k] = new GeoPolygon(d[po], d[po + 1], Bin.U16(d, po + 2), d[po + 4], d[po + 5], d[po + 6], d[po + 7],
                    Encoding.Latin1.GetString(d, po + 8, 4));
            parts[i] = new GeoPart(i, nv, np, Bin.S32(d, p + 8), Bin.S32(d, p + 12), Bin.S32(d, p + 16),
                Bin.U32(d, p + 0x14), Bin.U32(d, p + 0x18), Bin.U16(d, p + 0x1C), Bin.U16(d, p + 0x1E), Bin.U32(d, p + 0x20),
                ranges, Bin.U32(d, p + 0x30), verts, polys);
            p = po;
        }
        Parts = parts;
        ParsedLength = p;
    }

    public static CarGeo Parse(byte[] data) => new(data);

    /// <summary>
    /// The wheel relocation (called for [ref:0964]
    /// car types &lt; 0x11): the freshly loaded copy of <c>&lt;name&gt;.geo</c> is patched in place before anything
    /// reads it — the original game keeps the patched copy in the per-type geo cache, so the body box
    /// and the renderer both see the widened wheels. Call once per parsed geo. [ref:0965]
    /// <list type="bullet">
    /// <item>parts 0xC/0xE (front wheels) shifted, only for car type 4 (IDGO); [ref:0966]</item>
    /// <item>parts 0x10/0x12 (rear wheels) shifted for every type &lt; 0xF, and only [ref:0967]
    /// when polygon 0 of the part has flags bit 1 set.</item>
    /// </list>
    /// Each shift adds ±5 (1/256 m) to the x (first s16) of the 4 vertex indices of polygon 0: +5 for the right
    /// part (0xC/0x10), −5 for the left one (0xE/0x12). The rear shift is what makes the JAGR body box 270 wide
    /// instead of 266 (trace of the original: +0x108 = 69120, +0x114 = 180508).
    /// </summary>
    public void ApplyWheelShift(int carType) => ApplyWheelShift(FrontWheelShift(carType), RearWheelShift(carType));

    /// <summary>Vanilla rule: front wheels are shifted only for car type 4 (IDGO). Single source for CarTraits.WheelShift.</summary>
    public static bool FrontWheelShift(int carType) => carType == 4;

    /// <summary>Vanilla rule: rear wheels are shifted for every type &lt; 0xF. Single source for CarTraits.RearWheelShift.</summary>
    public static bool RearWheelShift(int carType) => carType < 0xF;

    /// <summary>The shift of the traits (CarTraits.WheelShift / RearWheelShift) instead of the car-number rule.</summary>
    public void ApplyWheelShift(bool front, bool rear)
    {
        if (front) { ShiftWheelX(0xC, +5, false); ShiftWheelX(0xE, -5, false); }
        if (rear) { ShiftWheelX(0x10, +5, true); ShiftWheelX(0x12, -5, true); }
    }

    /// <param name="needPolyFlag">Rear wheels: the original only shifts the part when polygon 0 has flag bit 1 set.</param>
    private void ShiftWheelX(int partIndex, int dx, bool needPolyFlag)
    {
        var part = Parts[partIndex];
        // the original walks the polygon list of the part, so a part without polygons has nothing to walk
        if (part.Vertices.Length == 0 || part.Polygons.Length == 0) return;
        var poly = part.Polygons[0];
        if (needPolyFlag && (poly.Flags & 2) == 0) return;
        foreach (byte v in new[] { poly.V0, poly.V1, poly.V2, poly.V3 })
            if (v < part.NumVerts) part.Vertices[3 * v] += (short)dx;   // x, 1/256 m
    }
}
