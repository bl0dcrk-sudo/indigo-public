using System.Buffers.Binary;
using System.Text;

namespace NFS.Net.Protocol;

/// <summary>A received message that cannot be decoded (truncated, overlong varint, value out of range, trailing bytes).
/// Receivers count and drop it; it never reaches the race host.</summary>
public sealed class NetProtocolException(string message) : Exception(message);

/// <summary>Bounded little-endian writer: varints (LEB128), zig-zag signed varints and fixed fields. Writing past
/// <see cref="Capacity"/> throws, so an encoder cannot silently exceed its datagram budget.</summary>
public sealed class WireWriter(int capacity)
{
    readonly byte[] _buf = new byte[capacity];

    public int Capacity => _buf.Length;
    public int Length { get; private set; }
    public ReadOnlySpan<byte> Written => _buf.AsSpan(0, Length);
    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
    public void Reset() => Length = 0;

    Span<byte> Take(int n)
    {
        if (Length + n > _buf.Length) throw new InvalidOperationException($"message exceeds its budget of {_buf.Length} bytes");
        var s = _buf.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public bool Fits(int n) => Length + n <= _buf.Length;
    public void U8(int v) => Take(1)[0] = checked((byte)v);
    public void I8(int v) => Take(1)[0] = (byte)checked((sbyte)v);
    public void Bool(bool v) => U8(v ? 1 : 0);
    public void U16(int v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), checked((ushort)v));
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);

    public void VarU(ulong v)
    {
        while (v >= 0x80) { U8((int)(v & 0x7F) | 0x80); v >>= 7; }
        U8((int)v);
    }

    public void VarU(long v)
    {
        if (v < 0) throw new ArgumentOutOfRangeException(nameof(v));
        VarU((ulong)v);
    }

    public void VarI(long v) => VarU((ulong)((v << 1) ^ (v >> 63)));

    public void Bytes(ReadOnlySpan<byte> b) => b.CopyTo(Take(b.Length));

    /// <summary>Length-prefixed byte block.</summary>
    public void Block(ReadOnlySpan<byte> b)
    {
        VarU((ulong)b.Length);
        Bytes(b);
    }

    /// <summary>ASCII string of at most <paramref name="max"/> characters (longer input is truncated, other chars become '?').</summary>
    public void Ascii(string s, int max)
    {
        var text = s.Length > max ? s[..max] : s;
        U8(text.Length);
        foreach (char c in text) U8(c is >= ' ' and < (char)0x7F ? c : '?');
    }

    public static int VarUSize(ulong v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }
}

/// <summary>Bounds-checked reader over a received datagram; every malformed input throws <see cref="NetProtocolException"/>.</summary>
public ref struct WireReader(ReadOnlySpan<byte> data)
{
    readonly ReadOnlySpan<byte> _data = data;
    int _pos;

    public int Remaining => _data.Length - _pos;
    public int Position => _pos;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _data.Length) throw new NetProtocolException("truncated message");
        var s = _data.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];
    public bool Bool() => U8() switch { 0 => false, 1 => true, _ => throw new NetProtocolException("bad bool") };
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public ulong VarU()
    {
        ulong v = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = U8();
            if (shift == 63 && b > 1) throw new NetProtocolException("varint overflow");
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new NetProtocolException("overlong varint");
    }

    public long VarI()
    {
        ulong z = VarU();
        return (long)(z >> 1) ^ -(long)(z & 1);
    }

    /// <summary>Unsigned varint that must fit <paramref name="max"/>.</summary>
    public long VarU(long max)
    {
        ulong v = VarU();
        if (v > (ulong)max) throw new NetProtocolException($"value {v} above {max}");
        return (long)v;
    }

    /// <summary>Signed varint that must fit Int32.</summary>
    public int VarI32()
    {
        long v = VarI();
        if (v is < int.MinValue or > int.MaxValue) throw new NetProtocolException("value outside int32");
        return (int)v;
    }

    public ReadOnlySpan<byte> Bytes(int n) => Take(n);

    public byte[] Block(int max)
    {
        int n = (int)VarU(max);
        return Take(n).ToArray();
    }

    public string Ascii(int max)
    {
        int n = U8();
        if (n > max) throw new NetProtocolException("string too long");
        var b = Take(n);
        foreach (var c in b) if (c is < 0x20 or >= 0x7F) throw new NetProtocolException("non-printable string");
        return Encoding.ASCII.GetString(b);
    }

    /// <summary>Every message is decoded completely; trailing bytes mean a malformed or foreign message.</summary>
    public readonly void End()
    {
        if (_pos != _data.Length) throw new NetProtocolException("trailing bytes");
    }
}
