using System.Globalization;
using System.Text;

namespace NFS.Desktop;

/// <summary>
/// Texts of the network menu screens that the layout builds every frame (change network-menu-look): <see cref="Safe"/> maps the
/// characters the menu fonts cannot draw to ASCII, the other helpers (<see cref="Join(string, string)"/>, <see cref="Cut"/>,
/// <see cref="Upper"/>, <see cref="Int"/>, <see cref="Time"/>) return a cached string for the same inputs so that a warm frame
/// allocates nothing. The caches are per thread; a cache that grows past <see cref="MaxEntries"/> (typed text, changing counters) is simply dropped.
/// </summary>
internal static class NetText
{
    const int MaxEntries = 4096;
    // One set of caches per thread: the game draws on one thread, and the tests that draw in parallel no longer drop the caches of
    // each other (a dropped cache made a warm frame of another test allocate).
    sealed class Caches
    {
        public readonly Dictionary<string, string> Safes = new(), Uppers = new(), Lowers = new();
        public readonly Dictionary<(string, string, string), string> Joins = new();
        public readonly Dictionary<(string, int), string> Cuts = new();
        public readonly Dictionary<int, string> Times = new(), BigInts = new();
    }

    [ThreadStatic] static Caches? _caches;
    static Caches C => _caches ??= new Caches();
    static Dictionary<string, string> Safes => C.Safes;
    static Dictionary<string, string> Uppers => C.Uppers;
    static Dictionary<string, string> Lowers => C.Lowers;
    static Dictionary<(string, string, string), string> Joins => C.Joins;
    static Dictionary<(string, int), string> Cuts => C.Cuts;
    static Dictionary<int, string> Times => C.Times;
    static Dictionary<int, string> BigInts => C.BigInts;
    static readonly string[] SmallInts = Enumerable.Range(0, 1000).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <summary>The text with every character outside ASCII 32..126 replaced: letters with diacritics by their base letter
    /// (a, l, e, O for the Polish a-ogonek, l-stroke, e-acute, O-acute), anything else by <c>?</c> (one per code point). Text that is plain ASCII is returned as is.</summary>
    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (IsPlain(text)) return text;
        if (Safes.TryGetValue(text, out var cached)) return cached;
        var result = Convert(text);
        if (Safes.Count >= MaxEntries) Safes.Clear();
        Safes[text] = result;
        return result;
    }

    static bool IsPlain(string text)
    {
        foreach (char c in text)
            if (c is < ' ' or > '~') return false;
        return true;
    }

    static string Convert(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= ' ' and <= '~') sb.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append('?'); i++; }
            else sb.Append(Fold(c));
        }
        return sb.ToString();
    }

    /// <summary>Base letters of the Latin letters with diacritics (Latin-1 Supplement and Latin Extended-A; the runtime may have no
    /// normalization data, so the common ones are listed here): the Polish, Czech, German, French, Nordic... names of the players.</summary>
    static readonly Dictionary<char, string> Folds = BuildFolds();

    static Dictionary<char, string> BuildFolds()
    {
        var map = new Dictionary<char, string>();
        void Add(string letters, string to) { foreach (char c in letters) map[c] = to; }
        Add("\u00C0\u00C1\u00C2\u00C3\u00C4\u00C5\u0100\u0102\u0104\u01CD", "A");
        Add("\u00E0\u00E1\u00E2\u00E3\u00E4\u00E5\u0101\u0103\u0105\u01CE", "a");
        Add("\u00C6", "AE"); Add("\u00E6", "ae");
        Add("\u00C7\u0106\u0108\u010A\u010C", "C"); Add("\u00E7\u0107\u0109\u010B\u010D", "c");
        Add("\u010E\u0110\u00D0", "D"); Add("\u010F\u0111\u00F0", "d");
        Add("\u00C8\u00C9\u00CA\u00CB\u0112\u0114\u0116\u0118\u011A", "E"); Add("\u00E8\u00E9\u00EA\u00EB\u0113\u0115\u0117\u0119\u011B", "e");
        Add("\u011C\u011E\u0120\u0122", "G"); Add("\u011D\u011F\u0121\u0123", "g");
        Add("\u0124\u0126", "H"); Add("\u0125\u0127", "h");
        Add("\u00CC\u00CD\u00CE\u00CF\u0128\u012A\u012C\u012E\u0130", "I"); Add("\u00EC\u00ED\u00EE\u00EF\u0129\u012B\u012D\u012F\u0131", "i");
        Add("\u0132", "IJ"); Add("\u0133", "ij");
        Add("\u0134", "J"); Add("\u0135", "j");
        Add("\u0136", "K"); Add("\u0137\u0138", "k");
        Add("\u0139\u013B\u013D\u013F\u0141", "L"); Add("\u013A\u013C\u013E\u0140\u0142", "l");
        Add("\u00D1\u0143\u0145\u0147\u014A", "N"); Add("\u00F1\u0144\u0146\u0148\u0149\u014B", "n");
        Add("\u00D2\u00D3\u00D4\u00D5\u00D6\u00D8\u014C\u014E\u0150", "O"); Add("\u00F2\u00F3\u00F4\u00F5\u00F6\u00F8\u014D\u014F\u0151", "o");
        Add("\u0152", "OE"); Add("\u0153", "oe");
        Add("\u0154\u0156\u0158", "R"); Add("\u0155\u0157\u0159", "r");
        Add("\u015A\u015C\u015E\u0160", "S"); Add("\u015B\u015D\u015F\u0161\u017F", "s");
        Add("\u00DF", "ss");
        Add("\u0162\u0164\u0166", "T"); Add("\u0163\u0165\u0167", "t");
        Add("\u00DE", "TH"); Add("\u00FE", "th");
        Add("\u00D9\u00DA\u00DB\u00DC\u0168\u016A\u016C\u016E\u0170\u0172", "U"); Add("\u00F9\u00FA\u00FB\u00FC\u0169\u016B\u016D\u016F\u0171\u0173", "u");
        Add("\u0174", "W"); Add("\u0175", "w");
        Add("\u00DD\u0176\u0178", "Y"); Add("\u00FD\u00FF\u0177", "y");
        Add("\u0179\u017B\u017D", "Z"); Add("\u017A\u017C\u017E", "z");
        return map;
    }

    /// <summary>Replacement of one character outside printable ASCII: the letters of <see cref="Folds"/>, then (when the runtime has
    /// the data) the canonical decomposition without the combining marks, else <c>?</c>.</summary>
    static string Fold(char c)
    {
        if (Folds.TryGetValue(c, out var letter)) return letter;
        if (c < 0xC0 || char.IsSurrogate(c)) return "?";
        string d = c.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char x in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(x) == UnicodeCategory.NonSpacingMark) continue;
            if (x is < ' ' or > '~') return "?";
            sb.Append(x);
        }
        return sb.Length == 0 ? "?" : sb.ToString();
    }

    /// <summary><paramref name="a"/> + <paramref name="b"/>, cached.</summary>
    public static string Join(string a, string b) => Join(a, b, "");

    /// <summary><paramref name="a"/> + <paramref name="b"/> + <paramref name="c"/>, cached.</summary>
    public static string Join(string a, string b, string c)
    {
        if (b.Length == 0 && c.Length == 0) return a;
        if (a.Length == 0 && c.Length == 0) return b;
        if (Joins.TryGetValue((a, b, c), out var cached)) return cached;
        var result = string.Concat(a, b, c);
        if (Joins.Count >= MaxEntries) Joins.Clear();
        Joins[(a, b, c)] = result;
        return result;
    }

    /// <summary>The first <paramref name="max"/> characters of <paramref name="text"/> (the text itself when it is not longer).</summary>
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        if (Cuts.TryGetValue((text, max), out var cached)) return cached;
        var result = text[..max];
        if (Cuts.Count >= MaxEntries) Cuts.Clear();
        Cuts[(text, max)] = result;
        return result;
    }

    /// <summary><see cref="string.ToUpperInvariant"/>, cached.</summary>
    public static string Upper(string text)
    {
        if (!NeedsUpper(text)) return text;
        if (Uppers.TryGetValue(text, out var cached)) return cached;
        var result = text.ToUpperInvariant();
        if (Uppers.Count >= MaxEntries) Uppers.Clear();
        Uppers[text] = result;
        return result;
    }

    /// <summary><see cref="string.ToLowerInvariant"/>, cached (the data of the game has its names in capitals; the original menus
    /// show values in lower case).</summary>
    public static string Lower(string text)
    {
        if (!NeedsLower(text)) return text;
        if (Lowers.TryGetValue(text, out var cached)) return cached;
        var result = text.ToLowerInvariant();
        if (Lowers.Count >= MaxEntries) Lowers.Clear();
        Lowers[text] = result;
        return result;
    }

    static bool NeedsLower(string text)
    {
        foreach (char c in text)
            if (c is >= 'A' and <= 'Z' || c > '~') return true;
        return false;
    }

    static bool NeedsUpper(string text)
    {
        foreach (char c in text)
            if (c is >= 'a' and <= 'z' || c > '~') return true;
        return false;
    }

    /// <summary>Decimal text of <paramref name="value"/>, cached.</summary>
    public static string Int(int value)
    {
        if ((uint)value < SmallInts.Length) return SmallInts[value];
        if (BigInts.TryGetValue(value, out var cached)) return cached;
        var result = value.ToString(CultureInfo.InvariantCulture);
        if (BigInts.Count >= MaxEntries) BigInts.Clear();
        BigInts[value] = result;
        return result;
    }

    /// <summary>Lap / race time of <paramref name="ticks"/> (<see cref="NFS.Render.HudOverlay.TimeText"/>), cached.</summary>
    public static string Time(int ticks)
    {
        if (Times.TryGetValue(ticks, out var cached)) return cached;
        var result = NFS.Render.HudOverlay.TimeText(ticks);
        if (Times.Count >= MaxEntries) Times.Clear();
        Times[ticks] = result;
        return result;
    }
}
