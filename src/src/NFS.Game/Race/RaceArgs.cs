using System.Buffers.Binary;
using System.Text;

namespace NFS.Game.Race;

/// <summary>Text (GAMEDATA/PREFS/PC/*.TXT) → tag/value int stream. [ref:2058]
/// Emitted forms: [tag, v] (tags 2..0x21), [0x22, n, v0..vn-1] (LINEUP), [tag, dev | code&lt;&lt;8] (keys 0x23..0x7A,
/// key bindings), [tag, car, v] (car[a to b].field, a..b inclusive, idx &lt; 16). Tag 1 IMPORT/CONTROL [ref:2059]
/// includes "&lt;name&gt;.txt" through <paramref name="include"/>. Unknown keys are skipped.</summary>
public sealed class RaceArgsCompiler(RaceTags tags, Func<string, byte[]?>? include = null)
{
    public List<int> Stream { get; } = [];

    public RaceArgsCompiler AddText(byte[] data, int depth = 0)
    {
        foreach (var raw in Encoding.Latin1.GetString(data).Split('\n'))
        {
            var line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            AddEntry(line[..eq].Trim(), line[(eq + 1)..].Trim(), depth);
        }
        return this;
    }

    /// <summary>One "key = value" entry. [ref:2060]</summary>
    public void AddEntry(string key, string value, int depth = 0)
    {
        int br = key.IndexOf('[');
        string head = br >= 0 ? key[..br].Trim() : key;
        int? tag = tags.TagOf(head);
        if (tag is null) return;
        int t = tag.Value;
        if (t == RaceTags.FirstCarField) // "car[a to b].field"
        {
            int close = key.IndexOf(']');
            if (br < 0 || close < br) return;
            string idx = key[(br + 1)..close];
            int ti = idx.IndexOfAny(['t', 'T']); // the original NULs every 't' and parses the rest after "to"
            int lo = ParseNumber(ti >= 0 ? idx[..ti] : idx), hi = ti >= 0 ? ParseNumber(idx[(ti + 2)..]) : lo;
            int? field = tags.TagOf(key[(close + 1)..].Trim());
            if (field is null) return;
            int v = ParseNumber(value);
            for (int i = lo; i <= hi && i < 0x10; i++)
                if (i >= 0) Stream.AddRange([field.Value, i, v]);
        }
        else if (t > RaceTags.Lineup) // key binding "device,code"
        {
            int comma = value.IndexOf(',');
            string dev = comma >= 0 ? value[..comma] : value, code = comma >= 0 ? value[(comma + 1)..] : "";
            Stream.AddRange([t, ParseNumber(code) << 8 | tags.DeviceIndex(dev.Trim())]);
        }
        else if (t == RaceTags.Lineup)
        {
            var vals = value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(ParseNumber).ToList();
            Stream.AddRange([t, vals.Count]);
            Stream.AddRange(vals);
        }
        else if (t > RaceTags.Import)
            Stream.AddRange([t, ParseNumber(value)]);
        else if (include != null && depth < 8) // IMPORT/CONTROL: "%s%s.txt"
        {
            var data = include(value + ".txt");
            if (data != null) AddText(data, depth + 1);
        }
    }

    /// <summary>Terminated stream (tag 0) as ints.</summary>
    public int[] ToArray() => [.. Stream, 0, 0];

    /// <summary>(simplified: one value per token; the original also packs "a,b,c" lists): [ref:2061]
    /// decimal, "0x" hex, leading '-', or a symbolic constant (unknown symbol → 0). [ref:2062]</summary>
    public int ParseNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        if (char.IsLetter(s[0]))
            return tags.SymbolValue(s) ?? 0;
        bool neg = s[0] == '-';
        if (neg) s = s[1..].Trim();
        int v = 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            foreach (char c in s[2..])
                if (Uri.IsHexDigit(c)) v = v * 16 + Convert.ToInt32(c.ToString(), 16); else break;
        }
        else
            foreach (char c in s)
                if (c is >= '0' and <= '9') v = v * 10 + (c - '0'); else break;
        return neg ? -v : v;
    }
}

/// <summary>Applies a tag/value stream to a <see cref="RaceSetup"/>. [ref:2063]
/// Not reproduced: joystick init, device init calls, car names (cfg+0x34), joystick calibration, [ref:2064]
/// and network setup. [ref:2065]</summary>
public static class RaceArgsParser
{
    public static RaceSetup Parse(RaceTags tags, ReadOnlySpan<int> s, RaceSetup? into = null)
    {
        var r = into ?? new RaceSetup();
        int p = 0;
        while (p < s.Length && s[p] != 0)
        {
            int tag = s[p];
            if (tag < RaceTags.FirstKey)
            {
                // tag < 0x22: *ptr = v; 0x22: copy n values; not in → ignored [ref:2066]
                if (tags.ArgOffsets.TryGetValue(tag, out int off))
                {
                    if (tag < RaceTags.Lineup) Write(r, off, s[p + 1]);
                    else for (int i = 0; i < s[p + 1]; i++) Write(r, off + 4 * i, s[p + 2 + i]);
                }
                if (tag > 0x21) p += s[p + 1];
                p += 2;
            }
            else if (tag < RaceTags.FirstCarField)
            {
                r.KeyBindings[tag - RaceTags.FirstKey] = s[p + 1]; // [ref:2067]
                p += 2;
            }
            else
            {
                if (tags.ArgOffsets.TryGetValue(tag, out int off))
                {
                    int car = s[p + 1];
                    if ((uint)car < RaceSetup.MaxCars) Write(r, off + car * 0x40, s[p + 2]);
                }
                p += 3;
            }
        }
        if (r.RaceType != 0) r.Set(0x08, 1); // non-single race → SKILL = expert
        return r;
    }

    private static void Write(RaceSetup r, int off, int v)
    {
        if (off >= 0 && off + 4 <= r.Block.Length) BinaryPrimitives.WriteInt32LittleEndian(r.Block.AsSpan(off), v);
    }

    /// <summary>Builds the stream of a race config block (inverse direction, for tests/tools): every tag
    /// with the current value, car records for <c>NumCars</c> cars, LINEUP with 13 entries. [ref:2068]</summary>
    public static int[] StreamFromBlock(RaceTags tags, byte[] block)
    {
        var o = new List<int>();
        int I(int off) => BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(off));
        int numCars = Math.Clamp(I(0x68), 0, RaceSetup.MaxCars);
        foreach (var (tag, off) in tags.ArgOffsets)
        {
            if (tag < RaceTags.Lineup) o.AddRange([tag, I(off)]);
            else if (tag == RaceTags.Lineup) { o.AddRange([tag, 13]); for (int i = 0; i < 13; i++) o.Add(I(off + 4 * i)); }
            else for (int c = 0; c < numCars; c++) o.AddRange([tag, c, I(off + c * 0x40)]);
        }
        o.AddRange([0, 0]);
        return [.. o];
    }
}
