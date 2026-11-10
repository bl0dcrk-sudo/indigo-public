using NFS.Data;

namespace NFS.Audio;

/// <summary>Front-end speech (voice-over) archive <c>FEDATA/PC/AUDIO/&lt;LANGUAGE&gt;/&lt;LANGUAGE&gt;.VIV</c> (BIGF, entries named with 3 characters:
/// "c00", "h00", "t00", "050"…), each entry is a BNKl bank with one EA-XA mono sample (codec 7). Opening:
/// (path format "%s%s\%s.viv", language table English/German/French/Spanish/Italian) [ref:0011]
/// language 5 → 0). Clip selection: the name is compared with the archive directory, ignoring case [ref:0012]
/// Apart from the "c…" clips, the original increments the third character of the name when a clip ends
/// ("h00"→"h01"→…) and playback continues until the entry is missing or the character exceeds '9'; "c…" clips play singly [ref:0013]
/// (<c>name[0] != 'c'</c>). In this installation only ENGLISH.VIV exists – without a language file the speech is silent [ref:0014]
/// (original: <c>0</c> → returns zeros). [ref:0015]</summary>
public sealed class FeSpeech
{
    public const string Directory = "FEDATA/PC/AUDIO";
    /// <summary>Language names 0..4 (array); directories and files in the game are upper case. [ref:0016]</summary>
    public static IReadOnlyList<string> LanguageNames { get; } = ["ENGLISH", "GERMAN", "FRENCH", "SPANISH", "ITALIAN"];
    /// <summary>Last allowed variant character: the sequence ends when the third character is &gt; '8' before incrementing. [ref:0017]</summary>
    public const char LastVariant = '9';

    readonly Viv viv;
    readonly Dictionary<string, Sound> decoded = new(StringComparer.OrdinalIgnoreCase);

    public int Language { get; }
    public string Path { get; }

    FeSpeech(Viv viv, int language, string path) { this.viv = viv; Language = language; Path = path; }

    /// <summary>Language name for the configuration index (CONFIG +0x5B): 5 (Swedish) → 0 as in the original; outside the range → English. [ref:0018]</summary>
    public static string LanguageName(int language) =>
        language is > 0 and < 5 ? LanguageNames[language] : LanguageNames[0];

    public static string VivPath(int language) { var n = LanguageName(language); return $"{Directory}/{n}/{n}.VIV"; }

    /// <summary>Opens the speech archive for a language; <c>null</c> when the file is not in the game.</summary>
    public static FeSpeech? TryOpen(int language, string? gameDir = null)
    {
        var path = VivPath(language);
        if (!GameFiles.Exists(path, gameDir)) return null;
        return new FeSpeech(Viv.Parse(GameFiles.ReadRaw(path, gameDir)), language, path);
    }

    public static FeSpeech Open(byte[] vivData, int language = 0) => new(Viv.Parse(vivData), language, VivPath(language));

    public IEnumerable<string> Clips => viv.Entries.Select(e => e.Name);
    public bool Contains(string clip) => viv.Contains(clip);

    /// <summary>Clips played after <paramref name="clip"/>: the name itself and, for names not starting with "c", also
    /// variants with the third character +1, +2… for as long as the entry exists. Empty list when the clip is not in the archive.</summary>
    public IReadOnlyList<string> Sequence(string clip)
    {
        var list = new List<string>();
        if (clip.Length < 3 || !viv.Contains(clip)) return list;
        list.Add(clip);
        if (char.ToLowerInvariant(clip[0]) == 'c') return list;
        var chars = clip.ToCharArray();
        while (chars[2] <= '8')
        {
            chars[2]++;
            var next = new string(chars);
            if (!viv.Contains(next)) break;
            list.Add(next);
        }
        return list;
    }

    /// <summary>Decoded clip (EA-XA mono, sample rate from the PT header, 22050 Hz by default); <c>null</c> when the entry is missing.</summary>
    public Sound? Load(string clip)
    {
        if (decoded.TryGetValue(clip, out var cached)) return cached;
        if (viv.FindEntry(clip) is not { } e) return null;
        var bank = BnkBank.Parse(viv.Data, e.Offset);
        var sample = bank.Samples.FirstOrDefault() ?? throw new InvalidDataException($"clip {clip}: bank without samples");
        return decoded[clip] = Sound.FromBank(bank, sample);
    }

    public IReadOnlyList<Sound> LoadSequence(string clip) => Sequence(clip).Select(n => Load(n)!).ToList();
}
