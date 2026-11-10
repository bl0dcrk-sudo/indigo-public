using System.Buffers.Binary;
using System.Text;

namespace NFS.Data;

/// <summary>Byte helpers shared by the loaders. Game data is little-endian except EA containers (VIV, EA compression,
/// PT audio headers, MAP offsets), which are big-endian.</summary>
public static class Bin
{
    public static sbyte S8(ReadOnlySpan<byte> b, int o) => (sbyte)b[o];
    public static short S16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b[o..]);
    public static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    public static int S32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    public static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    public static float F32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);

    public static ushort U16BE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
    public static int U24BE(ReadOnlySpan<byte> b, int o) => (b[o] << 16) | (b[o + 1] << 8) | b[o + 2];
    public static uint U32BE(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);

    public static short[] S16Array(ReadOnlySpan<byte> b, int o, int count)
    {
        var r = new short[count];
        for (int i = 0; i < count; i++) r[i] = S16(b, o + 2 * i);
        return r;
    }

    public static ushort[] U16Array(ReadOnlySpan<byte> b, int o, int count)
    {
        var r = new ushort[count];
        for (int i = 0; i < count; i++) r[i] = U16(b, o + 2 * i);
        return r;
    }

    public static int[] S32Array(ReadOnlySpan<byte> b, int o, int count)
    {
        var r = new int[count];
        for (int i = 0; i < count; i++) r[i] = S32(b, o + 4 * i);
        return r;
    }

    /// <summary>NUL-terminated Latin-1 string inside a fixed-size field.</summary>
    public static string CString(ReadOnlySpan<byte> b, int o, int size)
    {
        var f = b.Slice(o, Math.Min(size, b.Length - o));
        int nul = f.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? f : f[..nul]);
    }

    /// <summary>NUL-terminated string starting at <paramref name="o"/>; <paramref name="end"/> = index after the NUL.</summary>
    public static string CString(ReadOnlySpan<byte> b, int o, out int end)
    {
        int nul = b[o..].IndexOf((byte)0);
        if (nul < 0) nul = b.Length - o;
        end = o + nul + 1;
        return Encoding.Latin1.GetString(b.Slice(o, nul));
    }

    /// <summary>Four-character tag (e.g. FSH entry names, chunk ids) without trimming.</summary>
    public static string Tag4(ReadOnlySpan<byte> b, int o) => Encoding.Latin1.GetString(b.Slice(o, 4));

    public static bool HasTag(ReadOnlySpan<byte> b, int o, string tag) =>
        b.Length >= o + tag.Length && Tag4Like(b.Slice(o, tag.Length), tag);

    private static bool Tag4Like(ReadOnlySpan<byte> b, string tag)
    {
        for (int i = 0; i < tag.Length; i++) if (b[i] != tag[i]) return false;
        return true;
    }

    public static void Require(bool cond, string what)
    {
        if (!cond) throw new InvalidDataException(what);
    }
}

/// <summary>Sequential reader over a byte buffer (little-endian by default, BE variants for EA headers).</summary>
public sealed class BinReader(ReadOnlyMemory<byte> data, int position = 0)
{
    public ReadOnlyMemory<byte> Data { get; } = data;
    public int Position { get; set; } = position;
    public int Remaining => Data.Length - Position;
    public bool End => Position >= Data.Length;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Position + n > Data.Length) throw new EndOfStreamException($"read of {n} B at {Position} past {Data.Length}");
        var s = Data.Span.Slice(Position, n);
        Position += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public sbyte S8() => (sbyte)Take(1)[0];
    public short S16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int S32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ushort U16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int U24BE() { var s = Take(3); return (s[0] << 16) | (s[1] << 8) | s[2]; }
    public uint U32BE() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public byte[] Bytes(int n) => Take(n).ToArray();
    public void Skip(int n) => Take(n);
    public string Tag4() => Encoding.Latin1.GetString(Take(4));
    public string CString() { var s = Bin.CString(Data.Span, Position, out int end); Position = end; return s; }
}
