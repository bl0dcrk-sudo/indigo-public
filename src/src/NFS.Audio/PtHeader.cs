namespace NFS.Audio;

/// <summary>EA "PT" tag list (BNK sample headers and SCHl) — parser. [ref:0031]
/// 'P','T',x,flags (8-byte header when flags&amp;2), then {u8 tag, u8 len, BE value}; len 0xFF → 4-byte BE length;
/// 0xFC/0xFD = markers without length; 0xFE/0xFF = end.</summary>
public sealed class PtHeader
{
    public const int TagBits = 0x81, TagChannels = 0x82, TagCodec = 0x83, TagRate = 0x84, TagSamples = 0x85,
        TagLoopStart = 0x86, TagLoopEnd = 0x87, TagDataOffset = 0x88, TagDataPtr = 0x8A, TagVolume = 0x91, TagSpecial = 0x92;

    public const int CodecPcm = 0, CodecEaXa = 7;

    readonly Dictionary<int, long> values = [];
    readonly Dictionary<int, byte[]> blobs = [];

    /// <summary>Offset of the PT magic, and one past the end tag.</summary>
    public int Start { get; }
    public int End { get; }
    /// <summary>File offset of the tag 0x8A value (pointer slot filled in at load time), or -1. [ref:0032]</summary>
    public int DataPtrSlot { get; } = -1;

    public IReadOnlyDictionary<int, long> Values => values;
    public IReadOnlyDictionary<int, byte[]> Blobs => blobs;

    public int Bits => Get(TagBits, 16);
    public int Channels => Get(TagChannels, 1);
    public int Codec => Get(TagCodec, 0);
    public int SampleRate => Get(TagRate, 22050);
    public int Samples => Get(TagSamples, 0);
    public int LoopStart => Get(TagLoopStart, -1);
    public int LoopEnd => Get(TagLoopEnd, -1);
    public int DataOffset => Get(TagDataOffset, 0);
    public bool HasLoop => LoopStart >= 0 && LoopEnd > LoopStart;

    public bool Has(int tag) => values.ContainsKey(tag) || blobs.ContainsKey(tag);
    public int Get(int tag, int def) => values.TryGetValue(tag, out var v) ? (int)v : def;

    /// <summary>big-endian, sign-extended for 1..3 bytes. [ref:0033]</summary>
    public static long ReadBe(ReadOnlySpan<byte> b)
    {
        long v = 0;
        foreach (var x in b) v = v << 8 | x;
        int n = b.Length;
        if (n is >= 1 and <= 3 && v >= 1L << (8 * n - 1)) v -= 1L << (8 * n);
        return v;
    }

    PtHeader(ReadOnlySpan<byte> d, int p)
    {
        if (p + 4 > d.Length || d[p] != (byte)'P' || d[p + 1] != (byte)'T')
            throw new InvalidDataException($"PT header expected at 0x{p:X}");
        Start = p;
        int q = p + ((d[p + 3] & 2) != 0 ? 8 : 4);   // 8-byte header variant [ref:0034]
        while (true)
        {
            int t = d[q];
            if (t is 0xFF or 0xFE) { End = q + 1; return; }
            if (t is 0xFC or 0xFD) { q++; continue; }
            int len = d[q + 1];
            q += 2;
            if (len == 0xFF) { len = (int)ReadBe(d.Slice(q, 4)); q += 4; }
            if (t == TagDataPtr) DataPtrSlot = q;
            var val = d.Slice(q, len);
            if (!Has(t))   // first occurrence wins (like a dict setdefault)
            {
                if (len <= 4) values[t] = ReadBe(val);
                else blobs[t] = val.ToArray();
            }
            q += len;
        }
    }

    public static PtHeader Parse(ReadOnlySpan<byte> data, int offset) => new(data, offset);

    public override string ToString() =>
        $"PT bits={Bits} ch={Channels} codec={Codec} rate={SampleRate} samples={Samples} loop={LoopStart}..{LoopEnd}";
}
