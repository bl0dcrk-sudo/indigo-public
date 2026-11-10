namespace NFS.Data;

/// <summary>One block of an FSH entry chain (header 16 B). For palettes Width = colour count.</summary>
public sealed record FshBlock(
    int Offset,      // file offset of the block header
    byte Code,       // +0 low byte: pixel/palette/attachment code
    int NextOffset,  // +0 >> 8: offset of the next block relative to this one (0 = last) [ref:0934]
    ushort Width,    // +4
    ushort Height,   // +6
    short CenterX,   // +8 hotspot X (palettes: colour count)
    short CenterY,   // +0xA hotspot Y
    ushort RawPosX,  // +0xC screen X in bits 0..11
    ushort RawPosY)  // +0xE screen Y in bits 0..11, top 4 bits = flags [unknown]
{
    public int DataOffset => Offset + 16;
    public int PosX => RawPosX & 0xFFF;
    public int PosY => RawPosY & 0xFFF;
    public int PosFlags => RawPosY >> 12;
    public bool IsPalette => Fsh.IsPaletteCode(Code);
    public bool IsBitmap => Fsh.BitsPerPixel(Code) > 0;
}

/// <summary>Directory entry: 4-char name + chain of blocks (first = bitmap or palette).</summary>
public sealed record FshEntry(string Name, int Offset, IReadOnlyList<FshBlock> Blocks)
{
    public FshBlock Main => Blocks[0];
}

/// <summary>Decoded RGBA32 image (R,G,B,A per pixel) with its placement from the block header.</summary>
public sealed record FshImage(string Name, int Width, int Height, byte[] Rgba, int CenterX, int CenterY, int PosX, int PosY);

/// <summary>FSH / SHPI shape file (often inside QFS). Header: +0 'SHPI', +4 u32 file size, +8 u32 count,
/// +0xC dir id ('GIMX'), +0x10 N × {char name[4]; u32 offset}. Lookup (case-sensitive) [ref:0935]
/// Port of the reference implementation. [ref:0936] [ref:3374]</summary>
public sealed class Fsh
{
    public byte[] Data { get; }
    public uint DeclaredSize { get; }       // +4
    public string DirectoryId { get; }      // +0xC
    public IReadOnlyList<FshEntry> Entries { get; }

    private Fsh(byte[] data)
    {
        Data = data;
        Bin.Require(Bin.HasTag(data, 0, "SHPI"), "not an FSH (SHPI)");
        DeclaredSize = Bin.U32(data, 4);
        int n = Bin.S32(data, 8);
        DirectoryId = Bin.Tag4(data, 12);
        var entries = new FshEntry[n];
        for (int i = 0; i < n; i++)
        {
            int o = 16 + 8 * i;
            int off = Bin.S32(data, o + 4);
            entries[i] = new FshEntry(Bin.Tag4(data, o), off, ReadBlocks(data, off));
        }
        Entries = entries;
    }

    /// <summary>Parses an FSH; QFS (EA-compressed) input is unpacked first.</summary>
    public static Fsh Parse(byte[] data) => new(EaCompression.UnpackIfCompressed(data));

    public static Fsh Load(string relative, string? gameDir = null) => Parse(GameFiles.ReadRaw(relative, gameDir));

    private static List<FshBlock> ReadBlocks(byte[] d, int off)
    {
        var r = new List<FshBlock>();
        while (true)
        {
            uint hdr = Bin.U32(d, off);
            var b = new FshBlock(off, (byte)hdr, (int)(hdr >> 8), Bin.U16(d, off + 4), Bin.U16(d, off + 6),
                Bin.S16(d, off + 8), Bin.S16(d, off + 10), Bin.U16(d, off + 12), Bin.U16(d, off + 14));
            r.Add(b);
            if (b.NextOffset == 0 || r.Count > 64) return r;
            off += b.NextOffset;
        }
    }

    /// <summary>Exact name match (dword compare, case-sensitive). [ref:0937]</summary>
    public FshEntry? Find(string name) => Entries.FirstOrDefault(e => e.Name == name);

    /// <summary>Bits per pixel of a block code; 0 = not a bitmap. [ref:0938]</summary>
    public static int BitsPerPixel(int code) => code switch
    {
        0x78 or 0x7E or 0x6D => 16,
        0x7A => 4,
        0x7B => 8,
        0x7D => 32,
        0x7F => 24,
        _ => 0,
    };

    /// <summary>Codes that mark a palette block. [ref:0939]</summary>
    public static bool IsPaletteCode(int code) => code is 0x22 or 0x23 or 0x24 or 0x29 or 0x2A or 0x2C or 0x2D or 0x2E;

    /// <summary>Palette block → 256 RGBA entries; missing entries are opaque black. [ref:0940]</summary>
    public byte[] PaletteRgba(FshBlock b)
    {
        var pal = new byte[256 * 4];
        for (int i = 0; i < 256; i++) pal[i * 4 + 3] = 255;
        int o = b.DataOffset, n = Math.Min((int)b.Width, 256);
        for (int i = 0; i < n; i++)
        {
            int r, g, bl, a = 255;
            switch (b.Code)
            {
                case 0x22: // (v*255+32)/63 [ref:0941]
                case 0x24: // [ref:0942]
                    r = Data[o + 3 * i]; g = Data[o + 3 * i + 1]; bl = Data[o + 3 * i + 2];
                    if (b.Code == 0x22) { r = (r * 255 + 0x20) / 0x3F; g = (g * 255 + 0x20) / 0x3F; bl = (bl * 255 + 0x20) / 0x3F; }
                    if (i == 255) a = 0; // index 255 transparent
                    break;
                case 0x2A: // B,G,R,A dword
                    bl = Data[o + 4 * i]; g = Data[o + 4 * i + 1]; r = Data[o + 4 * i + 2]; a = Data[o + 4 * i + 3];
                    break;
                case 0x29:
                case 0x2D:
                {
                    int v = Bin.U16(Data, o + 2 * i);
                    if (b.Code == 0x29) { r = (v >> 11) << 3; g = ((v >> 5) & 0x3F) << 2; bl = (v & 0x1F) << 3; }
                    else { r = ((v >> 10) & 0x1F) << 3; g = ((v >> 5) & 0x1F) << 3; bl = (v & 0x1F) << 3; }
                    break; // (the original makes the colour key transparent; kept opaque here, as in the Python reference parser) [ref:0943]
                }
                case 0x2C:
                {
                    int v = Bin.U16(Data, o + 4 * i);
                    r = (v >> 11) << 3; g = ((v >> 5) & 0x3F) << 2; bl = (v & 0x1F) << 3; a = Data[o + 4 * i + 3];
                    break;
                }
                default:
                    throw new NotSupportedException($"FSH palette code {b.Code:X2}");
            }
            pal[i * 4] = (byte)r; pal[i * 4 + 1] = (byte)g; pal[i * 4 + 2] = (byte)bl; pal[i * 4 + 3] = (byte)a;
        }
        return pal;
    }

    /// <summary>last directory entry whose first block is a palette (e.g. '!pal'). [ref:0944]</summary>
    public byte[]? GlobalPalette()
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Main.Code is 0x22 or 0x24 or 0x2A) return PaletteRgba(Entries[i].Main);
        return null;
    }

    /// <summary>Palette attached in the entry's chain, or null. [ref:0945]</summary>
    public byte[]? AttachedPalette(FshEntry e)
    {
        foreach (var b in e.Blocks.Skip(1))
            if (b.IsPalette) return PaletteRgba(b);
        return null;
    }

    /// <summary>Raw 8-bit indices of a 0x7B entry (rows unpadded), or 4-bit expanded for 0x7A.</summary>
    public byte[] Indices(FshEntry e)
    {
        var b = e.Main;
        int w = b.Width, h = b.Height, o = b.DataOffset;
        if (b.Code == 0x7B) return Data.AsSpan(o, w * h).ToArray();
        if (b.Code == 0x7A)
        {
            int stride = (w + 1) / 2;
            var r = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    r[y * w + x] = (byte)((Data[o + y * stride + x / 2] >> ((x & 1) == 0 ? 4 : 0)) & 15); // [hipoteza] high nibble = left
            return r;
        }
        throw new InvalidOperationException($"entry {e.Name} is not indexed ({b.Code:X2})");
    }

    /// <summary>Decodes the entry's bitmap to RGBA32; null when the first block is not a bitmap.
    /// Indexed images use the attached palette, else <paramref name="globalPalette"/>, else greyscale.</summary>
    public FshImage? Decode(FshEntry e, byte[]? globalPalette = null)
    {
        var b = e.Main;
        int w = b.Width, h = b.Height, o = b.DataOffset, n = w * h;
        if (BitsPerPixel(b.Code) == 0) return null;
        var px = new byte[n * 4];
        switch (b.Code)
        {
            case 0x7B:
            case 0x7A:
            {
                var pal = AttachedPalette(e) ?? globalPalette ?? Greyscale();
                var idx = Indices(e);
                for (int i = 0; i < n; i++) pal.AsSpan(idx[i] * 4, 4).CopyTo(px.AsSpan(i * 4));
                break;
            }
            case 0x78:
            case 0x7E:
            case 0x6D:
                for (int i = 0; i < n; i++)
                {
                    int v = Bin.U16(Data, o + 2 * i);
                    int r, g, bl, a;
                    if (b.Code == 0x78) { r = (v >> 11) << 3; g = ((v >> 5) & 0x3F) << 2; bl = (v & 0x1F) << 3; a = 255; }
                    else if (b.Code == 0x7E) { r = ((v >> 10) & 31) << 3; g = ((v >> 5) & 31) << 3; bl = (v & 31) << 3; a = (v & 0x8000) != 0 ? 255 : 0; }
                    else { r = ((v >> 8) & 15) * 17; g = ((v >> 4) & 15) * 17; bl = (v & 15) * 17; a = (v >> 12) * 17; } // 4444 [hipoteza]
                    px[i * 4] = (byte)r; px[i * 4 + 1] = (byte)g; px[i * 4 + 2] = (byte)bl; px[i * 4 + 3] = (byte)a;
                }
                break;
            case 0x7D: // B,G,R,A
                for (int i = 0; i < n; i++)
                {
                    px[i * 4] = Data[o + 4 * i + 2]; px[i * 4 + 1] = Data[o + 4 * i + 1];
                    px[i * 4 + 2] = Data[o + 4 * i]; px[i * 4 + 3] = Data[o + 4 * i + 3];
                }
                break;
            case 0x7F: // B,G,R [hipoteza]
                for (int i = 0; i < n; i++)
                {
                    px[i * 4] = Data[o + 3 * i + 2]; px[i * 4 + 1] = Data[o + 3 * i + 1];
                    px[i * 4 + 2] = Data[o + 3 * i]; px[i * 4 + 3] = 255;
                }
                break;
        }
        return new FshImage(e.Name, w, h, px, b.CenterX, b.CenterY, b.PosX, b.PosY);
    }

    private static byte[] Greyscale()
    {
        var p = new byte[1024];
        for (int i = 0; i < 256; i++) { p[i * 4] = p[i * 4 + 1] = p[i * 4 + 2] = (byte)i; p[i * 4 + 3] = 255; }
        return p;
    }
}
