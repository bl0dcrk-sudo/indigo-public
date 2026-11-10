using System.Text;

namespace NFS.Data.Track;

/// <summary>COL ('COLL', ver 0xB) — global track data. [ref:1041]
/// Header 0x10: 'COLL', version, size, nXBlocks; then u32 offs[n] relative to +0x10, extrablocks as in TRK.</summary>
public sealed class Col
{
    public uint Version { get; }        // +4 (0xB)
    public uint Size { get; }           // +8
    public IReadOnlyList<ExtraBlock> ExtraBlocks { get; }
    /// <summary>Reads slice-3 without wrapping negative indices; keeps the whole [ref:1042]
    /// COL contiguous, so these are forward bytes from the preceding COL data, not the last track slices.</summary>
    public IReadOnlyDictionary<int, sbyte[]> SlicePrefixForward { get; }

    private Col(byte[] d)
    {
        Bin.Require(Bin.HasTag(d, 0, "COLL"), "not a COL (COLL)");
        Version = Bin.U32(d, 4);
        Size = Bin.U32(d, 8);
        int n = Bin.S32(d, 12);
        var xbs = new ExtraBlock[n];
        for (int i = 0; i < n; i++) xbs[i] = ExtraBlock.Read(d, 0x10 + Bin.S32(d, 0x10 + 4 * i));
        ExtraBlocks = xbs;
        var prefix = new Dictionary<int, sbyte[]>();
        if (xbs.FirstOrDefault(x => x.Id == 15) is { } sl)
            for (int i = -3; i < 0; i++)
            {
                int o = sl.Offset + 8 + i * 36 + 15;
                if (o >= 0) prefix[i] = [(sbyte)d[o], (sbyte)d[o + 1], (sbyte)d[o + 2]];
            }
        SlicePrefixForward = prefix;
    }

    public static Col Parse(byte[] data) => new(data);
    public static Col Load(string relative, string? gameDir = null) => new(GameFiles.Read(relative, gameDir));

    public ExtraBlock? Xb(int id) => ExtraBlocks.FirstOrDefault(x => x.Id == id);

    /// <summary>XBID 2.</summary>
    public TextureRecord[] Textures => Xb(2)?.Textures() ?? [];
    /// <summary>XBID 15 (blocks × 8).</summary>
    public TrackSlice[] Slices => Xb(15)?.Slices() ?? [];
    /// <summary>XBID 7 global object references.</summary>
    public ObjectRef[] ObjectRefs => Xb(7)?.ObjectRefs() ?? [];
    /// <summary>XBID 8 global structures.</summary>
    public TrackStructure[] Structures => Xb(8)?.Structures() ?? [];
}

/// <summary>DIS ("BWDistFront"): file = 8 B per slice, the original uses only the first 2·nSlices: [ref:1043]
/// u8[nSlices] neighbours drawn when facing forward, then u8[nSlices] backward. [ref:1044]
/// Missing file → 100 for every slice.</summary>
public sealed class Dis
{
    public byte[] Raw { get; }
    public int NumSlices { get; }

    private Dis(byte[] raw, int numSlices) { Raw = raw; NumSlices = numSlices; }

    public static Dis Parse(byte[] data, int numSlices)
    {
        Bin.Require(data.Length >= 2 * numSlices, "DIS shorter than 2 × slices");
        return new Dis(data, numSlices);
    }

    public static Dis Default(int numSlices)
    {
        var b = new byte[numSlices * 8];
        Array.Fill(b, (byte)100, 0, numSlices * 2);
        return new Dis(b, numSlices);
    }

    public int VisibleCount(int slice, bool facingForward) => Raw[facingForward ? slice : NumSlices + slice];
}

/// <summary>CAN animation key (20 B): int32 pos[3] (16.16), s16 quaternion[4] (1.14).</summary>
public readonly record struct CanKey(int X, int Y, int Z, short Qx, short Qy, short Qz, short Qw);

/// <summary>CAN camera/object animation (TRxxNN.CAN): +0 u32 ?, +4 u16 nKeys, +6 u16 ticksPerKey (0 or &gt;400 → 6
/// at run time), keys 20 B from +8.</summary>
public sealed class Can
{
    public uint Unknown_0x00 { get; }
    public ushort TicksPerKeyRaw { get; }
    public IReadOnlyList<CanKey> Keys { get; }

    /// <summary>Ticks per key as used by the player (0 or &gt; 400 → 6).</summary>
    public int TicksPerKey => TicksPerKeyRaw is 0 or > 400 ? 6 : TicksPerKeyRaw;

    private Can(byte[] d)
    {
        Unknown_0x00 = Bin.U32(d, 0);
        int n = Bin.U16(d, 4);
        TicksPerKeyRaw = Bin.U16(d, 6);
        Bin.Require(d.Length >= 8 + 20 * n, "CAN truncated");
        var k = new CanKey[n];
        for (int i = 0; i < n; i++)
        {
            int o = 8 + 20 * i;
            k[i] = new CanKey(Bin.S32(d, o), Bin.S32(d, o + 4), Bin.S32(d, o + 8),
                Bin.S16(d, o + 12), Bin.S16(d, o + 14), Bin.S16(d, o + 16), Bin.S16(d, o + 18));
        }
        Keys = k;
    }

    public static Can Parse(byte[] data) => new(data);
    public static Can Load(string relative, string? gameDir = null) => new(GameFiles.Read(relative, gameDir));
}

/// <summary>HRZ sky/fog text file: 26 integers read by the original's loader. [ref:1045]
/// (skips '/…/' comments, token = [0-9+-]+, strtol). Field names follow the comments in the files.</summary>
public sealed record Hrz(
    int BlackHorizon,          // 1 [ref:3377]
    int MirrorFlag,            // 2 [ref:3378]
    int Radius,                // 3 [ref:1046]
    int AngleDegrees,          // 4  (→ ·65536/360)
    int FlatDistance,          // 5 [ref:3379]
    int BaseHeight,            // 6 [ref:3380]
    int Height,                // 7 [ref:3381]
    int PixmapBasePercent,     // 8 (value × 65536 / 100) [ref:3382]
    byte[] SkyRgb,             // 9-11
    byte[] HorizonRgb,         // 12-14
    int[] DepthCueDistances,   // 15-18 (0..1100) [ref:1047]
    int[] DepthCueLevels,      // 19-22 (0..15) [ref:1048]
    int ColorSystem,           // 23 fog mode: 0 RGB, 1 HSV
    byte[] FogColor)           // 24-26 [ref:1049]
{
    public const int NumberCount = 26;

    /// <summary>Integer tokens exactly as the original scans them (missing trailing values → 0). [ref:1050]</summary>
    public static int[] ReadInts(string text, int count)
    {
        var r = new int[count];
        int p = 0;
        for (int i = 0; i < count; i++)
        {
            while (p < text.Length && !IsNum(text[p]))
            {
                char c = text[p++];
                if (c == '/') { while (p < text.Length && text[p++] != '/') { } }
            }
            int start = p;
            while (p < text.Length && IsNum(text[p])) p++;
            r[i] = Strtol(text.AsSpan(start, p - start));
        }
        return r;
    }

    private static bool IsNum(char c) => c is >= '0' and <= '9' or '-' or '+';

    private static int Strtol(ReadOnlySpan<char> s)
    {
        int i = 0, sign = 1;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { if (s[i] == '-') sign = -1; i++; }
        long v = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) v = v * 10 + (s[i++] - '0');
        return (int)(sign * v);
    }

    public static Hrz Parse(byte[] data)
    {
        var v = ReadInts(Encoding.Latin1.GetString(data), NumberCount);
        return new Hrz(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
            [(byte)v[8], (byte)v[9], (byte)v[10]], [(byte)v[11], (byte)v[12], (byte)v[13]],
            v[14..18], v[18..22], v[22], [(byte)v[23], (byte)v[24], (byte)v[25]]);
    }

    public static Hrz Load(string relative, string? gameDir = null) => Parse(GameFiles.ReadRaw(relative, gameDir));
}
