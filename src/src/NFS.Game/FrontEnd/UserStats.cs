using NFS.Data;

namespace NFS.Game.FrontEnd;

/// <summary>User statistics (track records, personal statistics) in the user directory
/// (`%APPDATA%\NFS2SE-port\stats`) instead of writing to FEDATA/PC/STATS in the game installation (which may be read-only
/// and shared). The relative path layout is the same as in the game data (`FEDATA/PC/STATS/OVAL.STF`,
/// `FEDATA/PC/STATS/PRH/PRH01.STF`), so the user copy overrides exactly the game file it replaces.
/// <para>Reads take the user copy first, and if there is none – the game data file (behaviour unchanged).
/// Writes are atomic (temporary file + replace) and never touch the game directory.</para></summary>
public static class UserStats
{
    /// <summary>User subfolder next to `settings.json`/`saves` (<see cref="UserSettings.Folder"/>).</summary>
    public const string Folder = "stats";

    /// <summary>Relative path (as in the game data, "/") → full path in the user directory.</summary>
    public static string PathFor(string dir, string relative) =>
        Path.Combine(Path.GetFullPath(dir), relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>File bytes: the user copy (unpacked like game data), and if there is none – the game data;
    /// null when the file is in neither directory.</summary>
    public static byte[]? Read(string? dir, string relative, string? gameDir = null)
    {
        if (dir != null && GameFiles.ResolveIn(dir, relative) is { } user && File.Exists(user))
            return EaCompression.UnpackIfCompressed(File.ReadAllBytes(user));
        return GameFiles.Exists(relative, gameDir) ? GameFiles.Read(relative, gameDir) : null;
    }

    /// <summary>Writes the user copy (atomically: temporary file + replace), creating directories if needed.</summary>
    public static void Write(string dir, string relative, byte[] data)
    {
        string full = PathFor(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, full, overwrite: true);
    }
}
