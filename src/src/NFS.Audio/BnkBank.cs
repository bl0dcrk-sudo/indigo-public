using System.Buffers.Binary;

namespace NFS.Audio;

/// <summary>One BNK slot: PT header + absolute data offset in the bank (bank + tag 0x88). [ref:0002]</summary>
public sealed record BnkSample(int Slot, PtHeader Header, int DataOffset)
{
    public int Channels => Header.Channels;
    public int SampleRate => Header.SampleRate;
    public int Samples => Header.Samples;
    public int Codec => Header.Codec;
}

/// <summary>Sound-effect bank 'BNKl' (loads the whole file): [ref:0003]
/// +4 u16 version (2), +6 u16 slot count, +8 u32 (start of sample data), +0xC u32[count] offsets of PT headers relative
/// to the offset field itself (0 = empty slot).</summary>
public sealed class BnkBank
{
    public byte[] Data { get; }
    public int Base { get; }
    public int Version { get; }
    public int Field8 { get; }
    /// <summary>Slots in file order; null = empty.</summary>
    public IReadOnlyList<BnkSample?> Slots { get; }

    BnkBank(byte[] d, int bas)
    {
        if (bas + 12 > d.Length || !d.AsSpan(bas, 4).SequenceEqual("BNKl"u8))
            throw new InvalidDataException("not a BNKl bank");
        Data = d;
        Base = bas;
        Version = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(bas + 4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(bas + 6));
        Field8 = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(bas + 8));
        var slots = new BnkSample?[count];
        for (int i = 0; i < count; i++)
        {
            int fo = bas + 12 + 4 * i;
            int rel = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(fo));
            if (rel == 0) continue;
            var h = PtHeader.Parse(d, fo + rel);
            slots[i] = new BnkSample(i, h, bas + h.DataOffset);   // absolute data offset = bank base + tag 0x88 value [ref:0004]
        }
        Slots = slots;
    }

    public static BnkBank Parse(byte[] data, int offset = 0) => new(data, offset);

    public IEnumerable<BnkSample> Samples => Slots.OfType<BnkSample>();

    /// <summary>Decodes a sample to interleaved 16-bit PCM: codec 7 mono (EA-XA from data offset, history 0) or
    /// 16-bit PCM little-endian.</summary>
    public short[] Decode(BnkSample s)
    {
        var src = Data.AsSpan(s.DataOffset);
        if (s.Codec == PtHeader.CodecEaXa && s.Channels == 1)
            return EaXaDecoder.DecodeMono(src, s.Samples);
        if (s.Codec == PtHeader.CodecEaXa && s.Channels == 2)
            return EaXaDecoder.DecodeStereo(src, s.Samples, stackalloc short[4]);
        if (s.Codec == PtHeader.CodecPcm && s.Header.Bits == 16)
        {
            var o = new short[s.Samples * s.Channels];
            for (int i = 0; i < o.Length; i++) o[i] = BinaryPrimitives.ReadInt16LittleEndian(src[(2 * i)..]);
            return o;
        }
        throw new NotSupportedException($"slot {s.Slot}: codec {s.Codec}, {s.Channels} ch, {s.Header.Bits} bit");
    }

    public short[] Decode(int slot) => Decode(Slots[slot] ?? throw new ArgumentException($"slot {slot} empty"));
}
