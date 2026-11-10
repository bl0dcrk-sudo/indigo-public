using System.Buffers.Binary;

namespace NFS.Audio;

/// <summary>Branch of a music section: go to <see cref="Next"/> when Min ≤ intensity ≤ Max (signed bytes).</summary>
public readonly record struct MusBranch(sbyte Min, sbyte Max, byte Next);

/// <summary>Section (28 B): +0 u8 row of the jump table, +1 u8 branch count (≤8), +2 u16 (0xFFFF), +4 branches 3 B each.</summary>
public sealed record MusSection(byte Row, ushort Field2, MusBranch[] Branches, uint MusOffset);

/// <summary>Interactive music map 'PFDx' (.MAP): [ref:0020]
/// +4 u8 version (0), +5 u8 start section, +6 u8 N sections, +7 u8 E events, +0xB u8 R jump-table rows;
/// +0xC N×28 B sections; R×E jump table; N × u32 BE section offsets in the MUS file.</summary>
public sealed class MusMap
{
    public const int SectionSize = 28;

    public int StartSection { get; }
    public int EventCount { get; }
    public int Rows { get; }
    public IReadOnlyList<MusSection> Sections { get; }
    readonly byte[] jumps;

    MusMap(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || !d[..4].SequenceEqual("PFDx"u8)) throw new InvalidDataException("not a PFDx map");
        if (d[4] != 0) throw new InvalidDataException($"PFDx version {d[4]}");   // unsupported version (original error code -11) [ref:0021]
        StartSection = d[5];
        int n = d[6];
        EventCount = d[7];
        Rows = d[0xB];
        int jt = 0xC + n * SectionSize, offs = jt + EventCount * Rows;
        if (offs + 4 * n > d.Length) throw new InvalidDataException("PFDx truncated");
        jumps = d.Slice(jt, EventCount * Rows).ToArray();
        var secs = new MusSection[n];
        for (int i = 0; i < n; i++)
        {
            var s = d.Slice(0xC + i * SectionSize, SectionSize);
            int nb = Math.Min((int)s[1], 8);
            var br = new MusBranch[nb];
            for (int j = 0; j < nb; j++) br[j] = new MusBranch((sbyte)s[4 + 3 * j], (sbyte)s[5 + 3 * j], s[6 + 3 * j]);
            secs[i] = new MusSection(s[0], BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), br,
                BinaryPrimitives.ReadUInt32BigEndian(d[(offs + 4 * i)..]));
        }
        Sections = secs;
    }

    public static MusMap Parse(ReadOnlySpan<byte> data) => new(data);

    /// <summary>Jump table entry [row][event] (index = E * row + event, E = number of events). [ref:0022]</summary>
    public int Jump(int row, int ev) => jumps[EventCount * row + ev];
}

/// <summary>Section sequencer state [ref:0023]
/// (current section, intensity, pending and queued events). [ref:0024]</summary>
public sealed class MusSequencer(MusMap map)
{
    public MusMap Map { get; } = map;
    public int Current { get; private set; } = map.StartSection;
    public sbyte Intensity { get; private set; }
    /// <summary>Number of section transitions so far. [ref:0025]</summary>
    public int Transitions { get; private set; }
    /// <summary>False once a section without branches was queued without a pending event (music ends after it).</summary>
    public bool Playing { get; private set; } = true;

    int pendingEvent = -1, pendingMode;     // pending event and its mode [ref:0026]
    int queuedEvent = -1, queuedMode;       // event queued behind a pending mode-1 event, and its mode [ref:0027]

    /// <summary>Accepts 0..127; otherwise rejected (original error code -8). [ref:0028]</summary>
    public bool SetIntensity(int v)
    {
        if (v < 0 || v >= 0x80) return false;
        Intensity = (sbyte)v;
        return true;
    }

    /// <summary>Mode 1 = replace the pending event and clear the queue; otherwise queue behind a pending mode-1 event [ref:0029]
    /// else replace pending.</summary>
    public bool PostEvent(int ev, int mode)
    {
        if (ev < 0 || ev >= Map.EventCount) return false;
        if (mode == 1) { pendingEvent = ev; pendingMode = mode; queuedEvent = -1; queuedMode = 0; }
        else if (pendingMode == 1) { queuedEvent = ev; queuedMode = mode; }
        else { pendingEvent = ev; pendingMode = mode; }
        return true;
    }

    /// <summary>Picks the section that follows <see cref="Current"/>. Returns its index. [ref:0030]</summary>
    public int Next()
    {
        var sec = Map.Sections[Current];
        if (pendingEvent < 0)
        {
            foreach (var b in sec.Branches)
                if (b.Min <= Intensity && Intensity <= b.Max) { Current = b.Next; break; }
        }
        else
        {
            Current = Map.Jump(sec.Row, pendingEvent);
            pendingEvent = queuedEvent; pendingMode = queuedMode;
            queuedEvent = -1; queuedMode = 0;
        }
        Transitions++;
        if (Map.Sections[Current].Branches.Length == 0 && pendingEvent < 0) Playing = false;
        return Current;
    }
}
