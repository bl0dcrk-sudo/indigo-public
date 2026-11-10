using NFS.Core.Constants;

namespace NFS.Game.Race;

/// <summary>Race configuration tag tables (<see cref="RaceTagConstants"/>, copied from the original game):
/// <list type="bullet">
/// <item>– {char* name, int tag} keywords of the PREFS.txt files (8 B, null-terminated) [ref:2297]</item>
/// <item>– {char* name, int value} symbolic constants GameSetup_k*, SC_*, PSX_*, numCars (8 B) [ref:2298]</item>
/// <item>– {char* name, read, init} (12 B): Null, Analog, Button, Key, Intface [ref:2299]</item>
/// <item>– {int tag, int* ptr} parser targets (8 B, terminated by tag 0) [ref:2300]</item>
/// </list></summary>
public sealed class RaceTags
{
    // Tag ranges [ref:2301]
    public const int Import = 1;
    public const int Lineup = 0x22;
    public const int FirstKey = 0x23;
    public const int FirstCarField = 0x7B;

    public IReadOnlyList<(string Name, int Tag)> Keys { get; }
    public IReadOnlyList<(string Name, int Value)> Symbols { get; }
    public IReadOnlyList<string> Devices { get; }
    /// <summary>tag → byte offset in the config block (ptr −). Duplicates: last entry wins (as in the parser). [ref:2302]</summary>
    public IReadOnlyDictionary<int, int> ArgOffsets { get; }

    public RaceTags(IReadOnlyList<(string, int)> keys, IReadOnlyList<(string, int)> symbols,
        IReadOnlyList<string> devices, IReadOnlyDictionary<int, int> argOffsets)
    {
        Keys = keys; Symbols = symbols; Devices = devices; ArgOffsets = argOffsets;
    }

    /// <summary>The tag tables of the original (<see cref="RaceTagConstants"/>).</summary>
    public static RaceTags FromConstants()
    {
        var args = new Dictionary<int, int>();
        foreach (var (tag, offset) in RaceTagConstants.ArgTable) args[tag] = offset;
        return new RaceTags(RaceTagConstants.Keywords, RaceTagConstants.Symbols, RaceTagConstants.Devices, args);
    }

    /// <summary>Case-insensitive keyword lookup (first match, like the original's linear scan); null = unknown. [ref:2303]</summary>
    public int? TagOf(string name)
    {
        foreach (var (n, t) in Keys)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>First keyword with this tag (for display); null when the tag has no name (e.g. 0x1E units, 0x87).</summary>
    public string? NameOf(int tag) => Keys.FirstOrDefault(k => k.Tag == tag).Name;

    public int? SymbolValue(string name)
    {
        foreach (var (n, v) in Symbols)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    /// <summary>device index, 0 when unknown. [ref:2304]</summary>
    public int DeviceIndex(string name)
    {
        for (int i = 0; i < Devices.Count; i++)
            if (string.Equals(Devices[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }
}
