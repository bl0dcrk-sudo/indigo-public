using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Core;

/// <summary>Race sessions on real track data for the track physics tests (REPLAY.RPY = track 4, PREFS DEFAULT.TXT).</summary>
internal static class TrackSessions
{
    private const string Prefs = "GAMEDATA/PREFS/PC/";
    private static readonly Lazy<RaceTags> Tags = new(() => RaceTags.FromConstants());

    public static readonly Lazy<RaceSession> Replay = new(FreshReplay);

    /// <summary>New session (cars are mutable – stepping tests must not share them).</summary>
    public static RaceSession FreshReplay()
    {
        var rpy = NFS.Data.FrontEnd.Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        return Load(RaceArgsParser.Parse(Tags.Value, RaceArgsParser.StreamFromBlock(Tags.Value, rpy.Config.Raw)));
    }

    /// <summary>The replay's race config switched to the SIMULATION style (STYLE tag 5 = 0, WILDARCADE tag 0x21 = 0, as the
    /// front-end emits for style "simulation"): 0. [ref:3103]</summary>
    public static RaceSession FreshReplaySim(Action<byte[]>? patch = null)
    {
        var rpy = NFS.Data.FrontEnd.Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var block = (byte[])rpy.Config.Raw.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(0x0C), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(0x38), 0);
        patch?.Invoke(block);
        return Load(RaceArgsParser.Parse(Tags.Value, RaceArgsParser.StreamFromBlock(Tags.Value, block)));
    }

    public static readonly Lazy<RaceSession> Default = new(() =>
    {
        var c = new RaceArgsCompiler(Tags.Value, f => GameFiles.Exists(Prefs + f, GameData.Dir) ? GameData.ReadRaw(Prefs + f) : null);
        c.AddText(GameData.ReadRaw(Prefs + "DEFAULT.TXT"));
        return Load(RaceArgsParser.Parse(Tags.Value, c.ToArray()));
    });

    private static RaceSession Load(RaceSetup s)
    {
        return new RaceLoader(GameData.Dir).Load(s);
    }

    public static TrackLocator Locator(RaceSession r) => new(new TrackGeometry(r.Trk, r.Slices));
}
