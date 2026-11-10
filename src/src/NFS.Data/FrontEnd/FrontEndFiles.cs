using System.Buffers.Binary;
using System.Text;

namespace NFS.Data.FrontEnd;

/// <summary>TEXT.&lt;lang&gt; record (12 B): also the menu layout (position/font/colour per text id).</summary>
public readonly record struct TextEntry(
    byte FontSize,        // +0 (13/14/16/18/36)
    byte Colour,          // +1 normal colour index [ref:0976]
    byte HighlightColour, // +2
    byte Align,           // +3 0 left, 1 right, 2 centre; +3 → with shadow (colour 8, offset 1,1)
    short X,              // +4
    short Y,              // +6
    int StringOffset,     // +8 from file start
    string Text);

/// <summary>FEDATA/PC/TEXT/TEXT.ENG/GER/FRE/SPA/ITA/SWE: array of 12 B records, then C strings; record count =
/// offset of the first string / 12 (ENG: 1498).</summary>
public sealed class TextFile
{
    public IReadOnlyList<TextEntry> Entries { get; }
    public string this[int id] => Entries[id].Text;

    private TextFile(byte[] d)
    {
        int n = Bin.S32(d, 8) / 12;
        var r = new TextEntry[n];
        for (int i = 0; i < n; i++)
        {
            int o = 12 * i, so = Bin.S32(d, o + 8);
            r[i] = new TextEntry(d[o], d[o + 1], d[o + 2], d[o + 3], Bin.S16(d, o + 4), Bin.S16(d, o + 6), so,
                so >= 0 && so < d.Length ? Bin.CString(d, so, out _) : "");
        }
        Entries = r;
    }

    public static TextFile Parse(byte[] data) => new(data);
    public static TextFile Load(string language = "ENG", string? gameDir = null) => new(GameFiles.ReadRaw($"FEDATA/PC/TEXT/TEXT.{language}", gameDir));

    /// <summary>File suffixes by language index (0..5; builds TEXT.&lt;code&gt;). [ref:0977]</summary>
    public static readonly IReadOnlyList<string> LanguageCodes = ["ENG", "GER", "FRE", "SPA", "ITA", "SWE"];
    /// <summary>Language names in the original's order, as install.win / spell them (english=0... swedish=5). [ref:0978]</summary>
    public static readonly IReadOnlyList<string> LanguageNames = ["english", "german", "french", "spanish", "italian", "swedish"];

    /// <summary>File suffix of <paramref name="language"/>; an out-of-range index = ENG (loads nothing for index &gt; 6; the port keeps English). [ref:0979]</summary>
    public static string CodeFor(int language) => language is >= 0 and < 6 ? LanguageCodes[language] : "ENG";

    /// <summary>Language index from a name (<c>german</c>, case-insensitive), a file code (<c>GER</c>) or a digit string (<c>1</c>); null = not recognised.</summary>
    public static int? ParseLanguage(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        for (int i = 0; i < 6; i++)
            if (text.Equals(LanguageNames[i], StringComparison.OrdinalIgnoreCase) || text.Equals(LanguageCodes[i], StringComparison.OrdinalIgnoreCase)) return i;
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n < 6 ? n : null;
    }

    /// <summary>The menu text of <paramref name="language"/>; a missing file falls back to TEXT.ENG. [ref:0980]</summary>
    public static TextFile ForLanguage(int language, string? gameDir = null)
    {
        string code = CodeFor(language);
        try { return Load(code, gameDir); }
        catch (Exception e) when (code != "ENG" && e is IOException or FileNotFoundException or DirectoryNotFoundException) { return Load("ENG", gameDir); }
    }
}

/// <summary>CONFIG.DAT (0x2AC B, saved 1:1 by the front-end; falls back to defaults when the size differs). [ref:0981]
/// Editable: setters write into <see cref="Raw"/>, <see cref="ToBytes"/>/<see cref="Save"/>
/// give the 0x2AC B image back. Offsets follow the original's layout. [ref:0982]
/// Correction: the lap count is at +0x18, not +0x19. [ref:0983]
/// +0x19 is the lap-menu index (laps = table[idx]). [ref:0984]</summary>
public sealed class FeConfig
{
    public const int Size = 0x2AC;
    public const string FilePath = "FEDATA/PC/CONFIG/CONFIG.DAT";

    public byte[] Raw { get; }

    public FeConfig(byte[] raw)
    {
        if (raw.Length != Size) throw new InvalidDataException($"CONFIG.DAT must be 0x{Size:X} B (got {raw.Length})");
        Raw = (byte[])raw.Clone();
    }

    public static FeConfig Parse(byte[] d) => new(d);
    public static FeConfig Load(string? gameDir = null) => new(GameFiles.ReadRaw(FilePath, gameDir));
    public byte[] ToBytes() => (byte[])Raw.Clone();
    public void Save(string path) => File.WriteAllBytes(path, Raw);

    public byte this[int off] { get => Raw[off]; set => Raw[off] = value; }
    public ushort U16(int off) => Bin.U16(Raw, off);
    public int I32(int off) => Bin.S32(Raw, off);
    private void W16(int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Raw.AsSpan(off), v);

    public ushort Seed { get => U16(0x00); set => W16(0x00, value); }          // (read as>>16) [ref:0985]
    public int TournamentOrder => I32(0x04);                                    // [ref:0986]
    /// <summary>Game type = index into the GAME TYPE value list: 0 one player, 1 split screen, <see cref="NetworkGameType"/> network.
    /// The original CONFIG.DAT values 2 modem / 3 serial / 4 network are legacy; see <see cref="SanitizeGameType"/>.</summary>
    public byte GameType { get => Raw[0x09]; set => Raw[0x09] = value; }        // [ref:0987]
    public const byte NetworkGameType = 2;
    /// <summary>Legacy compatibility: a stored game type above <see cref="NetworkGameType"/> (original 3 serial / 4 network)
    /// is not offered any more and falls back to one player; it never throws.</summary>
    public static byte SanitizeGameType(byte value) => value <= NetworkGameType ? value : (byte)0;
    public byte RaceType { get => Raw[0x0A]; set => Raw[0x0A] = value; }        // [ref:0988]
    public byte Style { get => Raw[0x0B]; set => Raw[0x0B] = value; }           // 0 sim, 1 wild, 2 arcade [ref:0989]
    public byte Skill { get => Raw[0x0D]; set => Raw[0x0D] = value; }           // AI skill [ref:0990]
    public byte CatchUp { get => Raw[0x0E]; set => Raw[0x0E] = value; }         // [ref:0991]
    public byte PersonalStats => Raw[0x0F];                                     // [ref:0992]
    public ushort Track { get => U16(0x10); set => W16(0x10, value); }          // [ref:0993]
    public uint TrackRecordTime => Bin.U32(Raw, 0x14);                          // [ref:0994]
    public byte Laps { get => Raw[0x18]; set => Raw[0x18] = value; }            // (not +0x19) [ref:0995]
    public byte LapsMenuIndex { get => Raw[0x19]; set => Raw[0x19] = value; }   // [ref:0996]
    public byte Backward { get => Raw[0x1A]; set => Raw[0x1A] = value; }        // [ref:0997]
    public byte Mirrored { get => Raw[0x1B]; set => Raw[0x1B] = value; }        // [ref:0998]
    public byte P1Car { get => Raw[0x1F]; set => Raw[0x1F] = value; }           // [ref:0999]
    public byte P1Transmission { get => Raw[0x20]; set => Raw[0x20] = value; }  // [ref:1000]
    public byte P1Colour(int car) => Raw[0x21 + car];                           // [16] [ref:1001]
    public ReadOnlySpan<byte> P1Colours => Raw.AsSpan(0x21, 16);
    public byte P1SlotCar => Raw[0x30];                                         // [ref:1002]
    public byte P2Car { get => Raw[0x3A]; set => Raw[0x3A] = value; }           // [ref:1003]
    public byte P2Transmission => Raw[0x3B];                                    // [ref:1004]
    public ReadOnlySpan<byte> P2Colours => Raw.AsSpan(0x3C, 16);                // [16] [ref:1005]
    public byte OpponentCar { get => Raw[0x55]; set => Raw[0x55] = value; }     // 0..11 car, 12/13/14 class A/B/C [ref:1006]
    public byte OpponentCount { get => Raw[0x56]; set => Raw[0x56] = value; }   // 0 none, 1 one, 2 full grid [ref:1007]
    public byte TrafficOff { get => Raw[0x58]; set => Raw[0x58] = value; }      // (traffic emitted only when 0) [ref:1008]
    public byte Units => Raw[0x5A];                                             // [ref:1009]
    public byte Language { get => Raw[0x5B]; set => Raw[0x5B] = value; }        // [ref:1010]
    /// <summary>P1 keys: ←,→,gas,brake,look behind,camera,handbrake,horn,gear up,gear down. [ref:1011]</summary>
    public int P1Key(int i) => I32(0x6C + 4 * i);
    /// <summary>Split-screen keys: <c>+0x94 + 8·i</c> = player 1, <c>+0x98 + 8·i</c> = player 2 for the same ten [ref:1012]
    /// actions as <see cref="P1Key"/> (case 1; the one-player table +0x6C is not used in split). [ref:1013]</summary>
    public int SplitKey(int player, int i) => I32(0x94 + 8 * i + 4 * player);
    /// <summary>Name of player <paramref name="player"/> (0/1) stored in the profile: 9-byte fields at +0x1AC / +0x1B5. [ref:1014]
    /// The original edits it and copies it into the race names. [ref:1015]</summary>
    public string ProfileName(int player) => Bin.CString(Raw, ProfileNameOffset(player), NameSize);
    /// <summary>Name of player in the race: fields at +0xE4 / +0xED, from which the results and HUD read it. [ref:1016]</summary>
    public string RaceName(int player) => Bin.CString(Raw, RaceNameOffset(player), NameSize);
    public const int NameSize = 9;
    public static int ProfileNameOffset(int player) => 0x1AC + NameSize * player;
    public static int RaceNameOffset(int player) => 0xE4 + NameSize * player;
    /// <summary>Writes the name (Latin-1, truncated to 8 characters, rest of the field zeroed).</summary>
    public void SetName(int offset, string name)
    {
        Array.Clear(Raw, offset, NameSize);
        var bytes = Encoding.Latin1.GetBytes(name.Length > NameSize - 1 ? name[..(NameSize - 1)] : name);
        bytes.CopyTo(Raw, offset);
    }
}

/// <summary>Track record (20 B): char name[10], s16 car (0..11), s32 time (1/100 s, max 0x38400), s16 skill (0..2), 2 B pad.</summary>
public readonly record struct StatRecord(string Name, short Car, int Time, short Skill, ushort Unknown_0x12);

/// <summary>FEDATA/PC/STATS/*.STF — N × 20 B (track: 31 = best lap + 3 × 10 for 2/4/8 laps; PRH: 9020 B = 451 records, doc says 450 [?]).
/// .SSF = text source (// comments, fields name/car/time/skill separated by tabs/CRLF).</summary>
public sealed record StatsFile(IReadOnlyList<StatRecord> Records)
{
    public const int RecordSize = 20;

    public static StatsFile Parse(byte[] d)
    {
        var r = new StatRecord[d.Length / RecordSize];
        for (int i = 0; i < r.Length; i++)
        {
            int o = RecordSize * i;
            r[i] = new StatRecord(Bin.CString(d, o, 10), Bin.S16(d, o + 10), Bin.S32(d, o + 12), Bin.S16(d, o + 16), Bin.U16(d, o + 18));
        }
        return new StatsFile(r);
    }

    /// <summary>Inverse of <see cref="Parse"/>: name as Latin-1 C string in 10 B (at most 9 chars + NUL), then car, time, skill, pad.</summary>
    public byte[] ToBytes()
    {
        var d = new byte[Records.Count * RecordSize];
        for (int i = 0; i < Records.Count; i++)
        {
            var rec = Records[i];
            var span = d.AsSpan(RecordSize * i, RecordSize);
            var name = Encoding.Latin1.GetBytes(rec.Name.Length > 9 ? rec.Name[..9] : rec.Name);
            name.CopyTo(span);
            BinaryPrimitives.WriteInt16LittleEndian(span[10..], rec.Car);
            BinaryPrimitives.WriteInt32LittleEndian(span[12..], rec.Time);
            BinaryPrimitives.WriteInt16LittleEndian(span[16..], rec.Skill);
            BinaryPrimitives.WriteUInt16LittleEndian(span[18..], rec.Unknown_0x12);
        }
        return d;
    }

    /// <summary>Non-comment tokens of an .SSF (first token = quoted title line, then the track number, then records).</summary>
    public static List<string> SsfTokens(byte[] data)
    {
        var r = new List<string>();
        foreach (var line in Encoding.Latin1.GetString(data).Split('\n'))
        {
            var t = line.Trim('\r', '\t', ' ');
            if (t.Length == 0 || t.StartsWith("//")) continue;
            foreach (var f in t.Split('\t', StringSplitOptions.RemoveEmptyEntries)) r.Add(f.Trim());
        }
        return r;
    }

    /// <summary>Track .SSF → records (title and track number skipped; groups of name, car, time, skill).</summary>
    public static StatsFile FromTrackSsf(byte[] data)
    {
        var t = SsfTokens(data);
        var r = new List<StatRecord>();
        for (int i = 2; i + 3 < t.Count + 0; i += 4)
            r.Add(new StatRecord(t[i], short.Parse(t[i + 1]), int.Parse(t[i + 2]), short.Parse(t[i + 3]), 0));
        return new StatsFile(r);
    }
}
