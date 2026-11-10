using System.Buffers.Binary;
using System.Numerics;
using NFS.Data.FrontEnd;

namespace NFS.Game.Race;

/// <summary>Tournament save file `.tou`, 0x2C8 B, 1:1 with the original: write and read (series state [ref:2388]
/// and pointer). Layout: +0 u16 length tag 0x2C8; +2 2 B with no known use (kept byte for byte); [ref:2389]
/// +4 0x2AC B copy of the FE config (refreshed on save); +0x2B0 s16[8] points total per identity; [ref:2390]
/// +0x2C0 u16 number of completed tracks (popcount of the mask); +0x2C2 2 B unused; +0x2C4 track mask (u8) + 3 B.
/// Loading requires the 0x2C8 tag; otherwise the original clears the series and shows message 0x2F0.</summary>
public sealed class TouFile
{
    public const int Size = 0x2C8;
    public const int Marker = 0x2C8;
    public const int ConfigOffset = 4;

    public ushort Length { get; set; } = Marker;
    public byte[] Unused2 { get; } = new byte[2];
    public byte[] Config { get; } = new byte[FeConfig.Size];
    public short[] Points { get; } = new short[8];
    public ushort TrackCount { get; set; }
    public byte[] Unused2C2 { get; } = new byte[2];
    public byte[] MaskBytes { get; } = new byte[4];
    public int Mask { get => MaskBytes[0]; set => MaskBytes[0] = (byte)value; }

    /// <summary>Parses a file image. Excess bytes (the original reads only 0x2C8) are ignored.</summary>
    public static TouFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size) throw new InvalidDataException($".tou too short: {data.Length} < 0x{Size:X}");
        var f = new TouFile { Length = BinaryPrimitives.ReadUInt16LittleEndian(data) };
        if (f.Length != Marker) throw new InvalidDataException($".tou length marker 0x{f.Length:X} != 0x{Marker:X}");
        data.Slice(2, 2).CopyTo(f.Unused2);
        data.Slice(ConfigOffset, FeConfig.Size).CopyTo(f.Config);
        for (int i = 0; i < 8; i++) f.Points[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(0x2B0 + 2 * i)..]);
        f.TrackCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x2C0..]);
        data.Slice(0x2C2, 2).CopyTo(f.Unused2C2);
        data.Slice(0x2C4, 4).CopyTo(f.MaskBytes);
        return f;
    }

    public byte[] ToBytes()
    {
        var d = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(d, Length);
        Unused2.CopyTo(d, 2);
        Config.CopyTo(d, ConfigOffset);
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(0x2B0 + 2 * i), Points[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x2C0), TrackCount);
        Unused2C2.CopyTo(d, 0x2C2);
        MaskBytes.CopyTo(d, 0x2C4);
        return d;
    }

    /// <summary>Track mask 0..0x7F and a consistent counter; otherwise the file is corrupt.</summary>
    public bool IsConsistent => (MaskBytes[0] & 0x80) == 0 && TrackCount == BitOperations.PopCount((uint)MaskBytes[0]);
}

/// <summary>Tournament saves in the user directory (not in the game directory). The original asks for a name (8 characters) and writes
/// `%s%s.tou` (directory + name); the selection list is the `*.tou` files in the directory. [ref:2391]
/// Atomic write (temporary file + replace).</summary>
public static class TournamentSaves
{
    public const string Extension = ".tou";

    /// <summary>Path of the file named <paramref name="name"/> (no extension, no separators or dots).</summary>
    public static string PathFor(string dir, string name) => SaveFiles.PathFor(dir, name, Extension);

    public static bool Exists(string dir, string name) => File.Exists(PathFor(dir, name));

    /// <summary>Names of the `*.tou` files without extension, sorted. [ref:2392]</summary>
    public static IReadOnlyList<string> List(string dir) => SaveFiles.List(dir, Extension);

    public static void Save(string dir, string name, TouFile file)
    {
        var full = Path.GetFullPath(PathFor(dir, name));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp";
        File.WriteAllBytes(tmp, file.ToBytes());
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Missing file, bad size/tag or an inconsistent mask throws (<see cref="IOException"/> or
    /// <see cref="InvalidDataException"/>); the caller shows a message and leaves the state unchanged.</summary>
    public static TouFile Load(string dir, string name)
    {
        var f = TouFile.Parse(File.ReadAllBytes(PathFor(dir, name)));
        if (!f.IsConsistent || f.Mask == 0x7F) throw new InvalidDataException(".tou holds an invalid or finished series");
        return f;
    }

    public static bool Delete(string dir, string name) => SaveFiles.Delete(dir, name, Extension);
}

/// <summary>User files addressed by name (tournament saves `*.tou`, replays `*.rpy`): listing, path, deletion.</summary>
public static class SaveFiles
{
    /// <summary>Name table has 0xFA entries. [ref:2393]</summary>
    public const int MaxEntries = 0xFA;

    public static string PathFor(string dir, string name, string extension)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('.'))
            throw new ArgumentException("invalid save name", nameof(name));
        return Path.Combine(dir, name + extension);
    }

    /// <summary>Names of files with extension <paramref name="extension"/>, without the extension, sorted case-insensitively.</summary>
    public static IReadOnlyList<string> List(string dir, string extension)
    {
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*" + extension)
            .Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToList();
    }

    public static bool Delete(string dir, string name, string extension)
    {
        var p = PathFor(dir, name, extension);
        if (!File.Exists(p)) return false;
        File.Delete(p); return true;
    }
}
