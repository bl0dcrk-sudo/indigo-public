using NFS.Data;

namespace NFS.Tests.Data;

/// <summary>Shared access to the original game data for the data tests (read-only). Tests needing the data use
/// <see cref="GameFactAttribute"/>/<see cref="GameTheoryAttribute"/> and are skipped when the game is not found.</summary>
internal static class GameData
{
    public static readonly string? Dir = GameFiles.TryFindGameDir();
    public static bool Available => Dir != null;
    public const string SkipReason = "game data (game_src/NFS2SE or %NFS2SE_GAME_DIR%) not found";

    public static string Sha256(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));

    public static byte[] Read(string rel) => GameFiles.Read(rel, Dir);
    public static byte[] ReadRaw(string rel) => GameFiles.ReadRaw(rel, Dir);
    public static IEnumerable<string> Files(string? sub, params string[] ext) => GameFiles.Enumerate(sub, Dir, ext);

    /// <summary>Project root (directory containing NFS2SE.sln), for reference files under re/.</summary>
    public static string ProjectRoot
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "NFS2SE.sln"))) return d.FullName;
            throw new DirectoryNotFoundException("NFS2SE.sln not found");
        }
    }

    public static string OutDir(string name = "")
    {
        var p = Path.Combine(ProjectRoot, "out", "tests", name);
        Directory.CreateDirectory(p);
        return p;
    }
}

public sealed class GameFactAttribute : FactAttribute
{
    public GameFactAttribute() { if (!GameData.Available) Skip = GameData.SkipReason; }
}

public sealed class GameTheoryAttribute : TheoryAttribute
{
    public GameTheoryAttribute() { if (!GameData.Available) Skip = GameData.SkipReason; }
}
