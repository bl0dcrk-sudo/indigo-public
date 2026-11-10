using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NFS.Data;

/// <summary>Minimal PNG encoder (no external dependencies): 8-bit indexed with palette, or 8-bit RGBA.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Indexed PNG. <paramref name="rgb24"/> = up to 768 B RGB; <paramref name="transparentIndex"/> gets alpha 0.</summary>
    public static byte[] EncodeIndexed(int width, int height, ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> rgb24, int? transparentIndex = null)
    {
        Check(width, height, pixels.Length, 1);
        int colors = Math.Min(256, rgb24.Length / 3);
        if (colors == 0) throw new ArgumentException("palette is empty", nameof(rgb24));
        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", Ihdr(width, height, 3));
        WriteChunk(ms, "PLTE", rgb24[..(colors * 3)]);
        if (transparentIndex is int t and >= 0 and < 256)
        {
            var trns = new byte[t + 1];
            Array.Fill(trns, (byte)255);
            trns[t] = 0;
            WriteChunk(ms, "tRNS", trns);
        }
        WriteChunk(ms, "IDAT", Deflate(width, height, pixels, 1));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }


    /// <summary>RGBA PNG, 4 bytes per pixel (R, G, B, A).</summary>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        Check(width, height, rgba.Length, 4);
        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", Ihdr(width, height, 6));
        WriteChunk(ms, "IDAT", Deflate(width, height, rgba, 4));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }


    public static void WriteIndexed(string path, int width, int height, ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> rgb24, int? transparentIndex = null) =>
        Save(path, EncodeIndexed(width, height, pixels, rgb24, transparentIndex));

    public static void WriteRgba(string path, int width, int height, ReadOnlySpan<byte> rgba) => Save(path, EncodeRgba(width, height, rgba));

    private static void Save(string path, byte[] png)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, png);
    }

    private static void Check(int w, int h, int len, int bpp)
    {
        if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "image dimensions must be positive");
        if (len < (long)w * h * bpp) throw new ArgumentException($"pixel buffer too short: {len} < {w}x{h}x{bpp}");
    }

    private static byte[] Ihdr(int w, int h, byte colorType)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(b, w);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), h);
        b[8] = 8; b[9] = colorType; // bit depth 8; compression, filter, interlace = 0
        return b;
    }

    private static byte[] Deflate(int w, int h, ReadOnlySpan<byte> px, int bpp)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = w * bpp;
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(px.Slice(y * stride, stride));
            }
        }
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>CRC-32 (IEEE) update without the final xor; start with 0xFFFFFFFF.</summary>
    internal static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
