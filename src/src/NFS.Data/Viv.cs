namespace NFS.Data;

public sealed record VivEntry(string Name, int Offset, int Size);

/// <summary>EA archive. Port of the reference implementation. [ref:0948] [ref:3376]
/// BIGF: +4 u32 BE archive size, +8 u32 BE count, +0xC u32 BE end of directory, from +0x10
/// {u32 BE offset, u32 BE size, name\0} until end of directory (the original ignores the count).
/// C0FB: +2 u16 BE (+4 = end of directory), from +6 {u24 BE offset, u24 BE size, name\0}.
/// Names compared case-insensitively (stricmp). [ref:0949]</summary>
public sealed class Viv
{
    public byte[] Data { get; }
    public bool IsBigF { get; }
    public uint DeclaredSize { get; }   // BIGF +4 (BE)
    public uint DeclaredCount { get; }  // BIGF +8 (BE)
    public int DirectoryEnd { get; }
    public IReadOnlyList<VivEntry> Entries { get; }

    private Viv(byte[] d)
    {
        Data = d;
        var list = new List<VivEntry>();
        if (Bin.HasTag(d, 0, "BIGF"))
        {
            IsBigF = true;
            DeclaredSize = Bin.U32BE(d, 4);
            DeclaredCount = Bin.U32BE(d, 8);
            DirectoryEnd = (int)Bin.U32BE(d, 12);
            int p = 16;
            while (p < DirectoryEnd)
            {
                int off = (int)Bin.U32BE(d, p), size = (int)Bin.U32BE(d, p + 4);
                var name = Bin.CString(d, p + 8, out p);
                if (name.Length > 0) list.Add(new VivEntry(name, off, size)); // directory padding yields a nameless junk entry
            }
        }
        else if (d.Length >= 6 && d[0] == 0xC0 && d[1] == 0xFB)
        {
            DirectoryEnd = Bin.U16BE(d, 2) + 4;
            int p = 6;
            while (p < DirectoryEnd)
            {
                int off = Bin.U24BE(d, p), size = Bin.U24BE(d, p + 3);
                var name = Bin.CString(d, p + 6, out p);
                if (name.Length > 0) list.Add(new VivEntry(name, off, size));
            }
            DeclaredCount = (uint)list.Count;
            DeclaredSize = (uint)d.Length;
        }
        else throw new InvalidDataException("not an EA archive (BIGF/C0FB)");
        Entries = list;
    }

    public static Viv Parse(byte[] data) => new(data);
    public static Viv Load(string relative, string? gameDir = null) => new(GameFiles.ReadRaw(relative, gameDir));

    public VivEntry? FindEntry(string name) =>
        Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string name) => FindEntry(name) != null;

    /// <summary>Raw bytes of the named file (not unpacked — files may themselves be QFS), or null.</summary>
    public byte[]? Find(string name) => FindEntry(name) is { } e ? Data.AsSpan(e.Offset, e.Size).ToArray() : null;

    public byte[] Get(string name) => Find(name) ?? throw new FileNotFoundException($"'{name}' not in archive");
}
