using System.Buffers.Binary;
using System.IO.Compression;

namespace NFS.Data;

/// <summary>Minimal PNG decoder for mod textures (openspec modding-api-cars D6): colour types 0, 2, 3, 4, 6 at bit depth 8 (16 keeps the high byte),
/// palette transparency (tRNS), not interlaced. Output: RGBA, rows top to bottom. Anything else, a bad CRC-free structure or a size over
/// <paramref name="maxSide"/> throws <see cref="InvalidDataException"/>.</summary>
public static class PngReader
{
    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static (int Width, int Height, byte[] Rgba) Decode(ReadOnlySpan<byte> png, int maxSide = 1024)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature)) throw new InvalidDataException("PNG: bad signature");
        int pos = 8, width = 0, height = 0, depth = 0, colour = -1;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        bool end = false;
        while (!end)
        {
            if (pos + 8 > png.Length) throw new InvalidDataException("PNG: truncated");
            int len = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            var type = png.Slice(pos + 4, 4);
            if (len < 0 || pos + 12 + (long)len > png.Length) throw new InvalidDataException("PNG: truncated chunk");
            var data = png.Slice(pos + 8, len);
            pos += 12 + len;
            if (type.SequenceEqual("IHDR"u8))
            {
                if (len < 13) throw new InvalidDataException("PNG: bad IHDR");
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                depth = data[8]; colour = data[9];
                if (data[10] != 0 || data[11] != 0) throw new InvalidDataException("PNG: unknown compression/filter method");
                if (data[12] != 0) throw new InvalidDataException("PNG: interlaced images are not supported");
                if (width <= 0 || height <= 0 || width > maxSide || height > maxSide)
                    throw new InvalidDataException($"PNG: {width}x{height} outside 1..{maxSide}");
                if (colour is not (0 or 2 or 3 or 4 or 6) || depth is not (8 or 16) || colour == 3 && depth != 8)
                    throw new InvalidDataException($"PNG: colour type {colour} / depth {depth} not supported");
            }
            else if (type.SequenceEqual("PLTE"u8)) palette = data.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) trns = data.ToArray();
            else if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
            else if (type.SequenceEqual("IEND"u8)) end = true;
        }
        if (colour < 0) throw new InvalidDataException("PNG: no IHDR");
        if (colour == 3 && palette == null) throw new InvalidDataException("PNG: palette image without PLTE");

        int channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bpp = channels * depth / 8, stride = width * bpp;
        var raw = new byte[(long)(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length) { int n = z.Read(raw, got, raw.Length - got); if (n == 0) break; got += n; }
            if (got < raw.Length) throw new InvalidDataException("PNG: image data too short");
        }

        var prev = new byte[stride];
        var cur = new byte[stride];
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int rowAt = y * (stride + 1);
            byte filter = raw[rowAt];
            Array.Copy(raw, rowAt + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = filter switch
                {
                    0 => cur[i],
                    1 => (byte)(cur[i] + a),
                    2 => (byte)(cur[i] + b),
                    3 => (byte)(cur[i] + (a + b) / 2),
                    4 => (byte)(cur[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"PNG: bad filter {filter}"),
                };
            }
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4, s = x * bpp, step = depth / 8;   // 16-bit: high byte of each sample
                byte r, g, bl, al = 255;
                switch (colour)
                {
                    case 0: r = g = bl = cur[s]; break;
                    case 2: r = cur[s]; g = cur[s + step]; bl = cur[s + 2 * step]; break;
                    case 3:
                        int idx = cur[s];
                        if (idx * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG: palette index out of range");
                        r = palette[idx * 3]; g = palette[idx * 3 + 1]; bl = palette[idx * 3 + 2];
                        if (trns != null && idx < trns.Length) al = trns[idx];
                        break;
                    case 4: r = g = bl = cur[s]; al = cur[s + step]; break;
                    default: r = cur[s]; g = cur[s + step]; bl = cur[s + 2 * step]; al = cur[s + 3 * step]; break;
                }
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = bl; rgba[o + 3] = al;
            }
            (prev, cur) = (cur, prev);
        }
        return (width, height, rgba);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
