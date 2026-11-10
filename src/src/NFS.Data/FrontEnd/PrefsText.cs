using System.Text;

namespace NFS.Data.FrontEnd;

/// <summary>One "Name = v1, v2, …" line of a GAMEDATA/PREFS/PC/*.TXT file.</summary>
public sealed record PrefsEntry(int Line, string Name, string[] Values);

/// <summary>Race setup text (DEFAULT.TXT, KEYBOARD.TXT, …): the textual form of the tag/value stream parsed by the game
/// (tag names match the names used here, case-insensitive). '#' starts a comment. [ref:1017]
/// "CONTROL"/"IMPORT" = include another file (tag 1). Mapping names → tag numbers / symbolic values
/// (GameSetup_k…) is done by the game layer.</summary>
public sealed class PrefsText
{
    public IReadOnlyList<PrefsEntry> Entries { get; }

    private PrefsText(IReadOnlyList<PrefsEntry> e) => Entries = e;

    public static PrefsText Parse(byte[] data)
    {
        var r = new List<PrefsEntry>();
        var lines = Encoding.Latin1.GetString(data).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            int hash = l.IndexOf('#');
            if (hash >= 0) l = l[..hash];
            int eq = l.IndexOf('=');
            if (eq < 0) continue;
            var name = l[..eq].Trim();
            if (name.Length == 0) continue;
            var vals = l[(eq + 1)..].Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
            r.Add(new PrefsEntry(i + 1, name, vals));
        }
        return new PrefsText(r);
    }

    public static PrefsText Load(string relative, string? gameDir = null) => Parse(GameFiles.ReadRaw(relative, gameDir));

    public PrefsEntry? Find(string name) => Entries.LastOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>FEDATA/PC/CONFIG/HUDCON.DAT (0x98 B) = raw copy of the HUD state. [ref:1018]
/// The original restores five dwords from it (only when the value is != 1 and == 0): [ref:1019]
/// +0x0C map of player 1, +0x1C camera view, +0x3C HUD text of player 1 [ref:1020]
/// +0x40 HUD text of player 2 and +0x80 mirror; +0x00 is the split flag (0 in a saved file). [ref:1021]
/// The other bytes are transient state dumped along (rival name, best lap...) and are carried over unchanged.</summary>
public sealed record HudConfig(byte[] Raw)
{
    public const int Size = 0x98;
    public const int MapOffset = 0x0C, CameraOffset = 0x1C, TextOffset = 0x3C, Text2Offset = 0x40, MirrorOffset = 0x80;

    public static HudConfig Parse(byte[] d)
    {
        Bin.Require(d.Length == Size, $"HUDCON.DAT must be 0x{Size:X} B");
        return new HudConfig(d.ToArray());
    }

    public int Dword(int index) => Bin.S32(Raw, 4 * index);

    /// <summary>Used when there is no file: map, both texts and mirror on, camera view 0, the rest zero. [ref:1022]</summary>
    public static byte[] Defaults() => Compose([], true, true, 0, true);

    /// <summary>The snapshot of a finished race: <paramref name="baseline"/> (the file the race started from, when it is [ref:1023]
    /// 0x98 B; otherwise zeros, player 2 text on) with the five restored fields replaced by the live values and (+0x00) = 0. [ref:1024]</summary>
    public static byte[] Compose(ReadOnlySpan<byte> baseline, bool map, bool text, int cameraMode, bool mirror)
    {
        var d = new byte[Size];
        if (baseline.Length == Size) baseline.CopyTo(d);
        else System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(Text2Offset), 1);
        void W(int off, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(off), v);
        W(0, 0);
        W(MapOffset, map ? 1 : 0);
        W(CameraOffset, cameraMode is >= 0 and <= 3 ? cameraMode : 0);
        W(TextOffset, text ? 1 : 0);
        W(MirrorOffset, mirror ? 1 : 0);
        return d;
    }
}
