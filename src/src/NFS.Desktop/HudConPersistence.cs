using NFS.Data.FrontEnd;

namespace NFS.Desktop;

/// <summary>User copy of HUDCON.DAT: the HUD, camera and mirror choices persist between [ref:1089]
/// races in <c>%APPDATA%\NFS2SE-port\HUDCON.DAT</c> (next to settings.json) instead of the game folder. A null directory = no
/// persistence (tests, autotests, replays, headless tools).</summary>
public static class HudConPersistence
{
    public const string FileName = "HUDCON.DAT";

    /// <summary>The saved file, or null when the directory is null, the file is missing or it is not 0x98 B.</summary>
    public static byte[]? Load(string? dir)
    {
        if (dir == null) return null;
        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;
            var d = File.ReadAllBytes(path);
            return d.Length == HudConfig.Size ? d : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes the snapshot; false on a null directory or an I/O error (never throws – a race must not fail on it).</summary>
    public static bool Save(string? dir, byte[] data)
    {
        if (dir == null || data.Length != HudConfig.Size) return false;
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Saved only when: not split screen (1), not a replay, not a network spectator. [ref:1090]</summary>
    public static bool ShouldSave(RaceDrive drive) => !drive.IsSplit && drive.PlaybackSource == null && !drive.Spectator;
}
