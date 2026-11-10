using System.Text;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;

namespace NFS.Game.Race;

/// <summary>Builds a RaceSetup from a source (CONFIG.DAT, a PREFS.txt file, or REPLAY.RPY) [ref:2111]
/// and formats it with the loaded session (used by <c>race-info</c> of the CLI tools).</summary>
public static class RaceInfo
{
    public const string Prefs = "GAMEDATA/PREFS/PC/";

    /// <param name="source">"config", "replay" or a PREFS file name (e.g. DEFAULT.TXT)</param>
    public static RaceSetup Setup(string source, string? gameDir = null)
    {
        var tags = RaceTags.FromConstants();
        switch (source.ToLowerInvariant())
        {
            case "config":
                return RaceArgsParser.Parse(tags, new FeRaceArgsBuilder().Build(FeConfig.Load(gameDir)));
            case "replay":
                return FromReplay(Replay.Parse(GameFiles.ReadRaw("REPLAY.RPY", gameDir)));
            default:
                var c = new RaceArgsCompiler(tags, f => GameFiles.Exists(Prefs + f, gameDir) ? GameFiles.ReadRaw(Prefs + f, gameDir) : null);
                c.AddText(GameFiles.ReadRaw(Prefs + source, gameDir));
                return RaceArgsParser.Parse(tags, c.ToArray());
        }
    }

    /// <summary>The race a replay was recorded in (its config block).</summary>
    public static RaceSetup FromReplay(Replay rpy)
    {
        var tags = RaceTags.FromConstants();
        return RaceArgsParser.Parse(tags, RaceArgsParser.StreamFromBlock(tags, rpy.Config.Raw));
    }

    public static string Format(RaceSession r)
    {
        var s = r.Setup;
        var sb = new StringBuilder();
        sb.AppendLine($"race type {s.RaceType}, style {s.Style}, skill {s.Skill}, laps {s.NumLaps}, track {s.Track}" +
                      $"{(s.Backward ? " backward" : "")}{(s.Mirrored ? " mirrored" : "")}");
        sb.AppendLine($"cars {s.NumCars} (humans {s.NumPlayerCars}, AI {s.NumOpponentCars}), slices {r.Slices.Length}, " +
                      $"smackables {r.Smackables?.Cars.Count ?? 0}");
        foreach (var c in r.Cars)
            sb.AppendLine($"  #{c.Index} {c.Model,-4} class {c.Setup.CarClass} flags {c.Obj.Flags} grid {c.GridSlot} " +
                          $"slice {c.StartSlice} pos ({c.Obj.Pos.X / 65536.0:F1}, {c.Obj.Pos.Y / 65536.0:F1}, {c.Obj.Pos.Z / 65536.0:F1})");
        return sb.ToString();
    }
}
