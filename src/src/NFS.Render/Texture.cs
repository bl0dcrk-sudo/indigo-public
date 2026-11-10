using NFS.Data;

namespace NFS.Render;

/// <summary>Decoded texture: 0xRRGGBB texels plus a transparency mask (index 0xFF = transparent, rast span modes 1/5).</summary>
public sealed class Texture
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Rgb { get; }
    public bool[] Transparent { get; }
    public uint Average { get; }

    public Texture(int w, int h, uint[] rgb, bool[] transparent)
    {
        Width = w; Height = h; Rgb = rgb; Transparent = transparent;
        long r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < rgb.Length; i++)
        {
            if (transparent[i]) continue;
            r += rgb[i] >> 16 & 255; g += rgb[i] >> 8 & 255; b += rgb[i] & 255; n++;
        }
        Average = n == 0 ? 0x808080u : (uint)(r / n << 16 | g / n << 8 | b / n);
    }

    /// <summary>Indexed FSH entry (0x7B/0x7A) with its attached palette (else the file's global palette).</summary>
    public static Texture? FromFsh(Fsh fsh, FshEntry e, byte[]? globalPalette)
    {
        if (e.Main.Code is not (0x7B or 0x7A))
        {
            var img = fsh.Decode(e, globalPalette);
            if (img == null) return null;
            var c = new uint[img.Width * img.Height];
            var t = new bool[c.Length];
            for (int i = 0; i < c.Length; i++)
            {
                c[i] = (uint)(img.Rgba[i * 4] << 16 | img.Rgba[i * 4 + 1] << 8 | img.Rgba[i * 4 + 2]);
                // 16-bit 0x78 (SE/3Dfx shapes): pure green 565 0x07E0 used as colour key [hypothesis; unconfirmed] [ref:2652]
                t[i] = img.Rgba[i * 4 + 3] < 128 || e.Main.Code == 0x78 && c[i] == 0x00FC00;
            }
            return new Texture(img.Width, img.Height, c, t);
        }
        var pal = fsh.AttachedPalette(e) ?? globalPalette;
        var idx = fsh.Indices(e);
        var rgb = new uint[idx.Length];
        var tr = new bool[idx.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            int k = idx[i];
            tr[i] = k == 0xFF;
            rgb[i] = pal == null ? (uint)(k * 0x010101) : (uint)(pal[k * 4] << 16 | pal[k * 4 + 1] << 8 | pal[k * 4 + 2]);
        }
        return new Texture(e.Main.Width, e.Main.Height, rgb, tr);
    }

    MipChain? _mipsOpaque, _mipsAlpha;
    bool? _hasMask;

    /// <summary>Mip chain for the Enhanced filters (built on first use). <paramref name="withAlpha"/>: honour <see cref="Transparent"/> (premultiplied
    /// colour + alpha, so masks of trees / fences filter without halos; the caller thresholds the filtered alpha); otherwise every texel is opaque
    /// (the original draws the palette colour of index 0xFF when the polygon is not marked transparent).</summary>
    public MipChain Mips(bool withAlpha)
    {
        if (withAlpha && !(_hasMask ??= Array.IndexOf(Transparent, true) >= 0)) withAlpha = false;   // nothing to mask: same chain (the scan is done once)
        return withAlpha ? _mipsAlpha ??= MipChain.Build(this, true) : _mipsOpaque ??= MipChain.Build(this, false);
    }

    public static Texture Solid(uint rgb) => new(1, 1, [rgb], [false]);

    /// <summary>XBID 2 orientation flags: 0x10 mirror horizontally, 0x40 vertically [ref:2653]
    /// 0x02/0x04/0x08 rotate 90/180/270. The original permutes the corner→UV assignment; rotating the image is
    /// equivalent. [hypothesis] rotation direction = clockwise, mirrors applied before rotation.</summary>
    public Texture Oriented(byte flags)
    {
        if ((flags & 0x5E) == 0) return this;
        int w = Width, h = Height;
        var src = (uint[])Rgb.Clone();
        var st = (bool[])Transparent.Clone();
        if ((flags & 0x10) != 0) Flip(src, st, w, h, true);
        if ((flags & 0x40) != 0) Flip(src, st, w, h, false);
        int turns = (flags & 0x02) != 0 ? 1 : (flags & 0x04) != 0 ? 2 : (flags & 0x08) != 0 ? 3 : 0;
        for (int t = 0; t < turns; t++)
        {
            var d = new uint[src.Length]; var dt = new bool[src.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int nx = h - 1 - y, ny = x; // clockwise
                    d[ny * h + nx] = src[y * w + x]; dt[ny * h + nx] = st[y * w + x];
                }
            src = d; st = dt; (w, h) = (h, w);
        }
        return new Texture(w, h, src, st);
    }

    static void Flip(uint[] c, bool[] t, int w, int h, bool horizontal)
    {
        var c2 = (uint[])c.Clone(); var t2 = (bool[])t.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = horizontal ? y * w + (w - 1 - x) : (h - 1 - y) * w + x;
                c[y * w + x] = c2[s]; t[y * w + x] = t2[s];
            }
    }
}

/// <summary>Mip levels of a <see cref="Texture"/> in framebuffer byte order (<c>r | g&lt;&lt;8 | b&lt;&lt;16 | a&lt;&lt;24</c>, colour premultiplied by alpha when
/// the chain carries a mask). Level n+1 = 2×2 box average of level n (edge texels repeat for odd sizes), down to 1×1.</summary>
public sealed class MipChain
{
    public uint[][] Levels { get; }
    public int[] Widths { get; }
    public int[] Heights { get; }
    public bool HasAlpha { get; }
    public int Count => Levels.Length;

    MipChain(uint[][] levels, int[] w, int[] h, bool alpha) { Levels = levels; Widths = w; Heights = h; HasAlpha = alpha; }

    internal static MipChain Build(Texture t, bool alpha)
    {
        var lv = new List<uint[]>(); var ws = new List<int>(); var hs = new List<int>();
        var b = new uint[t.Width * t.Height];
        for (int i = 0; i < b.Length; i++)
        {
            uint c = t.Rgb[i];
            b[i] = alpha && t.Transparent[i] ? 0u : (c >> 16 & 255) | (c >> 8 & 255) << 8 | (c & 255) << 16 | 0xFF000000u;
        }
        lv.Add(b); ws.Add(t.Width); hs.Add(t.Height);
        int w = t.Width, h = t.Height;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, (w + 1) / 2), nh = Math.Max(1, (h + 1) / 2);
            var src = lv[^1]; var dst = new uint[nw * nh];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1), y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                    uint a = src[y0 * w + x0], bb = src[y0 * w + x1], c = src[y1 * w + x0], d = src[y1 * w + x1];
                    uint o = 0;
                    for (int sh = 0; sh < 32; sh += 8)
                        o |= (uint)(((a >> sh & 255) + (bb >> sh & 255) + (c >> sh & 255) + (d >> sh & 255) + 2) >> 2) << sh;
                    dst[y * nw + x] = o;
                }
            lv.Add(dst); ws.Add(nw); hs.Add(nh);
            w = nw; h = nh;
        }
        return new MipChain(lv.ToArray(), ws.ToArray(), hs.ToArray(), alpha);
    }
}
