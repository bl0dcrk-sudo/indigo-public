using System.Buffers.Binary;

namespace NFS.Audio;

/// <summary>One SCDl block: file offset of the chunk, chunk size and frame count.</summary>
public readonly record struct AsfBlock(int Offset, int Size, int Frames);

/// <summary>EA ASF/MUS stream (one SCHl…SCEl section) — chunk dispatcher. [ref:0001]
/// Chunks: {char tag[4]; u32 LE size of the whole chunk; data}. SCHl = PT header, SCCl = u32 block count,
/// SCDl = u32 frames + per channel 2×s16 history (newest, older) + EA-XA data, SCLl = u32 loop target file offset,
/// SCEl = end. In MUS the sections are 4-byte aligned.</summary>
public sealed class AsfStream
{
    public PtHeader Header { get; }
    public int Start { get; }
    /// <summary>One past the SCEl chunk (or end of data when SCEl is missing).</summary>
    public int End { get; }
    /// <summary>SCCl value (declared number of SCDl blocks), -1 when absent.</summary>
    public int DeclaredBlocks { get; } = -1;
    public IReadOnlyList<AsfBlock> Blocks { get; }
    /// <summary>File offset of the SCLl chunk and the offset it jumps to (-1 when no loop).</summary>
    public int LoopChunkOffset { get; } = -1;
    public int LoopTarget { get; } = -1;
    public bool HasEnd { get; }

    public int Channels => Header.Channels;
    public int SampleRate => Header.SampleRate;
    public int TotalFrames => Blocks.Sum(b => b.Frames);

    readonly byte[] data;

    public static IEnumerable<(string Tag, int Offset, int Size)> Chunks(byte[] d, int start = 0)
    {
        int p = start;
        while (p + 8 <= d.Length)
        {
            var tag = System.Text.Encoding.ASCII.GetString(d, p, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
            yield return (tag, p, size);
            if (size < 8) yield break;
            p += size;
        }
    }

    AsfStream(byte[] d, int start)
    {
        data = d;
        Start = start;
        PtHeader? hdr = null;
        var blocks = new List<AsfBlock>();
        End = d.Length;
        foreach (var (tag, p, size) in Chunks(d, start))
        {
            switch (tag)
            {
                case "SCHl":
                    if (hdr != null) throw new InvalidDataException($"second SCHl at 0x{p:X} before SCEl");
                    hdr = PtHeader.Parse(d, p + 8);
                    break;
                case "SCCl":
                    DeclaredBlocks = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8));
                    break;
                case "SCDl":
                    blocks.Add(new AsfBlock(p, size, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8))));
                    break;
                case "SCLl":
                    LoopChunkOffset = p;
                    LoopTarget = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8));
                    break;
                case "SCEl":
                    HasEnd = true;
                    End = p + size;
                    goto done;
                default:
                    throw new InvalidDataException($"unknown chunk '{tag}' at 0x{p:X}");
            }
            if (p == start && hdr == null) throw new InvalidDataException("stream must start with SCHl");
        }
    done:
        Header = hdr ?? throw new InvalidDataException("no SCHl");
        Blocks = blocks;
    }

    public static AsfStream Parse(byte[] data, int offset = 0) => new(data, offset);

    /// <summary>All sections of a MUS file (each SCHl…SCEl, 4-byte aligned) or the single stream of an ASF.</summary>
    public static List<AsfStream> ParseAll(byte[] data)
    {
        var res = new List<AsfStream>();
        int p = 0;
        while (p + 8 <= data.Length)
        {
            var s = Parse(data, p);
            res.Add(s);
            if (!s.HasEnd) break;
            p = (s.End + 3) & ~3;
        }
        return res;
    }

    /// <summary>Index of the block starting at file offset <paramref name="offset"/> (SCLl target), or -1.</summary>
    public int BlockIndexAt(int offset)
    {
        for (int i = 0; i < Blocks.Count; i++) if (Blocks[i].Offset == offset) return i;
        return -1;
    }

    /// <summary>Decodes one SCDl block (interleaved PCM). History is taken from the block header
    /// (file order per channel: newest, older → decoder state old,new), as in the Python reference decoder.</summary>
    public short[] DecodeBlock(int index)
    {
        var b = Blocks[index];
        int ch = Channels;
        if (Header.Codec != PtHeader.CodecEaXa) throw new NotSupportedException($"codec {Header.Codec}");
        var hdr = data.AsSpan(b.Offset + 12, 4 * ch);
        Span<short> hist = stackalloc short[2 * ch];
        for (int i = 0; i < 2 * ch; i++) hist[i] = BinaryPrimitives.ReadInt16LittleEndian(hdr[(2 * (i ^ 1))..]);
        int q = b.Offset + 12 + 4 * ch;
        var src = data.AsSpan(q, b.Offset + b.Size - q);
        var o = new short[b.Frames * ch];
        if (ch == 2) EaXaDecoder.DecodeStereo(src, b.Frames, hist, o);
        else if (ch == 1) EaXaDecoder.DecodeMono(src, b.Frames, hist, o);
        else throw new NotSupportedException($"{ch} channels");
        return o;
    }

    /// <summary>Decodes the stream linearly (first <paramref name="maxBlocks"/> blocks, all when null).</summary>
    public short[] Decode(int? maxBlocks = null)
    {
        int n = Math.Min(Blocks.Count, maxBlocks ?? int.MaxValue);
        var o = new short[Blocks.Take(n).Sum(b => b.Frames) * Channels];
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            var blk = DecodeBlock(i);
            blk.CopyTo(o, pos);
            pos += blk.Length;
        }
        return o;
    }
}
