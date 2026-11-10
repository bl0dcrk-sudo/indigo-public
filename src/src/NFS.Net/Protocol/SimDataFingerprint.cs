using System.Security.Cryptography;
using System.Text;
using NFS.Data;

namespace NFS.Net.Protocol;

/// <summary>
/// SHA-256 over the game files the authoritative simulation reads (car data, AI tables, SIMTUNE, track geometry/collision,
/// AI speed lines, parked cars): sorted upper-case relative path, length and content of each file. Server and client compare it
/// in the handshake; graphics, sound and front-end files do not take part (presentation only, never sent by the server).
/// </summary>
public static class SimDataFingerprint
{
    static readonly (string Dir, string[] Ext)[] Manifest =
    [
        ("GAMEDATA/SIM", []),
        ("GAMEDATA/CARMODEL/PC", ["SIMTUNE.DAT"]),
        ("GAMEDATA/TRACKS/SE", [".TRK", ".COL", ".DIS"]),
        ("GAMEDATA/TRACKS/PC", [".TRK", ".COL", ".DIS"]),
        ("GAMEDATA/TRACKS/SPEEDS", []),
        ("GAMEDATA/TRACKS/DATA/PC", [".DAT"]),
    ];

    /// <summary>Relative paths that take part (diagnostics).</summary>
    public static IReadOnlyList<string> Files(string? gameDir) =>
        Manifest.SelectMany(m => GameFiles.Enumerate(m.Dir, gameDir, m.Ext))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.ToUpperInvariant(), StringComparer.Ordinal).ToList();

    public static byte[] Compute(string? gameDir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = GameFiles.FindGameDir(gameDir);
        foreach (var rel in Files(gameDir))
        {
            var data = File.ReadAllBytes(Path.Combine(root, rel));
            sha.AppendData(Encoding.ASCII.GetBytes(rel.ToUpperInvariant()));
            sha.AppendData(BitConverter.GetBytes((long)data.Length));
            sha.AppendData(data);
        }
        return sha.GetHashAndReset();
    }
}
