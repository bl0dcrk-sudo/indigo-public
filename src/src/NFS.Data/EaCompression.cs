namespace NFS.Data;

/// <summary>EA compression codecs (QFS/QPS/QPL/QDA…) — dispatcher and size. [ref:0928]
/// Header: <c>u8 id0, u8 id1(0xFB), [u24 BE packed size if id0&amp;1], u24 BE unpacked size</c>.
/// 0x10 = RefPack, 0x30/0x32/0x34 = EA Huffman (delta 1st/2nd order). [ref:0929]
/// Port 1:1 of the reference implementation. [ref:3373]</summary>
public static class EaCompression
{
    /// <summary>True when the header names a codec the game data uses . [ref:0930]</summary>
    public static bool IsCompressed(ReadOnlySpan<byte> src) =>
        src.Length >= 5 && src[1] == 0xFB && (src[0] & 0xFE) is 0x10 or 0x30 or 0x32 or 0x34;

    /// <summary>Declared unpacked size (respects bit 0 of id0 like the decoders; itself always reads +2). [ref:0931]</summary>
    public static int UnpackedSize(ReadOnlySpan<byte> src)
    {
        if (src.Length < 5 || (src[1] != 0xFB && src[1] != 0x32)) return 0;
        int p = (src[0] & 1) != 0 ? 5 : 2;
        return Bin.U24BE(src, p);
    }

    public static byte[] UnpackIfCompressed(byte[] src) => IsCompressed(src) ? Unpack(src) : src;

    /// <summary>Decompresses; throws on unsupported codec or size mismatch.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> src)
    {
        if (!IsCompressed(src)) throw new InvalidDataException($"not an EA compressed stream ({src[0]:X2}{src[1]:X2})");
        int t = src[0] & 0xFE;
        var (out_, size) = t == 0x10 ? RefPack(src) : Huffman(src);
        if (out_.Length != size) throw new InvalidDataException($"size mismatch {out_.Length} != {size}");
        return out_;
    }

    private static (byte[] Data, int Size) RefPack(ReadOnlySpan<byte> src)
    {
        int p = (src[0] & 1) != 0 ? 5 : 2;
        int size = Bin.U24BE(src, p);
        p += 3;
        var o = new byte[size];
        int q = 0;
        while (true)
        {
            int b0 = src[p], lit, off, len;
            if (b0 < 0x80)
            {
                int b1 = src[p + 1]; p += 2;
                lit = b0 & 3;
                off = ((b0 & 0x60) << 3) + b1 + 1;
                len = ((b0 & 0x1C) >> 2) + 3;
            }
            else if (b0 < 0xC0)
            {
                int b1 = src[p + 1], b2 = src[p + 2]; p += 3;
                lit = b1 >> 6;
                off = ((b1 & 0x3F) << 8) + b2 + 1;
                len = (b0 & 0x3F) + 4;
            }
            else if (b0 < 0xE0)
            {
                int b1 = src[p + 1], b2 = src[p + 2], b3 = src[p + 3]; p += 4;
                lit = b0 & 3;
                off = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1;
                len = ((b0 & 0x0C) << 6) + b3 + 5;
            }
            else if (b0 < 0xFC)
            {
                p++;
                int n = ((b0 & 0x1F) + 1) * 4;
                Copy(src, ref p, o, ref q, n);
                continue;
            }
            else
            {
                p++;
                Copy(src, ref p, o, ref q, b0 & 3);
                break;
            }
            Copy(src, ref p, o, ref q, lit);
            int s = q - off;
            if (s < 0 || q + len > o.Length) throw new InvalidDataException("refpack: bad back-reference");
            for (int i = 0; i < len; i++) o[q++] = o[s + i]; // byte copy, overlap allowed
        }
        return (q == o.Length ? o : o[..q], size);
    }

    private static void Copy(ReadOnlySpan<byte> src, ref int p, byte[] o, ref int q, int n)
    {
        if (q + n > o.Length) throw new InvalidDataException("refpack: output overflow");
        src.Slice(p, n).CopyTo(o.AsSpan(q));
        p += n; q += n;
    }

    /// <summary>MSB-first bit reader (the original refills 16 bits big-endian at a time; equivalent).</summary>
    private ref struct Bits(ReadOnlySpan<byte> d)
    {
        private readonly ReadOnlySpan<byte> _d = d;
        private long _pos;

        public uint Peek(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                long q = _pos + i;
                int by = (q >> 3) < _d.Length ? _d[(int)(q >> 3)] : 0;
                v = (v << 1) | (uint)((by >> (7 - (int)(q & 7))) & 1);
            }
            return v;
        }

        public uint Get(int n) { var v = Peek(n); _pos += n; return v; }

        /// <summary>Gamma-like code: k zeros, a one, then k+2 bits; value = (1&lt;&lt;(k+2)) + bits (min. 4).</summary>
        public int GetNum()
        {
            int k = 0;
            while (Get(1) == 0) k++;
            int n = k + 2;
            return (1 << n) + (int)Get(n);
        }
    }

    // (+0x32 running sum, +0x34 second order) [ref:0933]
    private static (byte[] Data, int Size) Huffman(ReadOnlySpan<byte> src)
    {
        var br = new Bits(src);
        int typ = (int)br.Get(16);
        if ((typ & 0x100) != 0) br.Get(24); // packed size
        int size = (int)br.Get(24);
        int clue = (int)br.Get(8);

        var dbase = new List<long> { 0 };
        var cmp = new List<long> { 0 };
        long code = 0, total = 0;
        int L = 0;
        while (true)
        {
            L++;
            dbase.Add(code * 2 - total);
            int n = br.GetNum() - 4;
            code = code * 2 + n;
            total += n;
            long c = n != 0 ? (code << (16 - L)) & 0xFFFF : 0;
            cmp.Add(c);
            if (n != 0 && c == 0) break;
            if (L > 32) throw new InvalidDataException("huffman: bad code length table");
        }
        int maxbits = L;
        cmp[maxbits] = 1L << 32;

        var used = new bool[256];
        var syms = new byte[total];
        int s = 0xFF;
        for (int i = 0; i < total; i++)
        {
            int leap = br.GetNum() - 3;
            while (true)
            {
                s = (s + 1) & 0xFF;
                if (!used[s] && --leap == 0) break;
            }
            used[s] = true;
            syms[i] = (byte)s;
        }

        var o = new byte[size];
        int q = 0;
        while (true)
        {
            long top = br.Peek(16);
            int ln = 1;
            while (!(top < cmp[ln])) ln++;
            long v = br.Get(ln);
            int sym = syms[v - dbase[ln]];
            if (sym != clue)
            {
                Put(o, ref q, (byte)sym);
                continue;
            }
            int run = br.GetNum() - 4;
            if (run != 0)
            {
                byte last = o[q - 1];
                for (int i = 0; i < run; i++) Put(o, ref q, last);
                continue;
            }
            if (br.Get(1) == 0) { Put(o, ref q, (byte)br.Get(8)); continue; } // escaped literal
            break; // end of stream
        }

        int t = typ & 0xFEFF;
        if (t == 0x32FB)
        {
            byte acc = 0;
            for (int i = 0; i < q; i++) { acc += o[i]; o[i] = acc; }
        }
        else if (t == 0x34FB)
        {
            byte d = 0, acc = 0;
            for (int i = 0; i < q; i++) { d += o[i]; acc += d; o[i] = acc; }
        }
        return (q == o.Length ? o : o[..q], size);
    }

    private static void Put(byte[] o, ref int q, byte v)
    {
        if (q >= o.Length) throw new InvalidDataException("huffman: output overflow");
        o[q++] = v;
    }
}
