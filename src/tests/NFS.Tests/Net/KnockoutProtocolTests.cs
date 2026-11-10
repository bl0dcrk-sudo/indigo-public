using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Wire format and configuration of the knockout mode and the straggler limit (protocol v12), and the lineup of a
/// knockout round built by the server.</summary>
public class KnockoutProtocolTests
{
    const uint Epoch = 0xA1B2C3D4;

    static NetLobbyState ReadState(byte[] data)
    {
        var r = new WireReader(data);
        var h = NetHeader.Read(ref r);
        return NetLobbyState.Decode(h, ref r);
    }

    static NetKnockoutState Series(int completed = 1, bool finished = false, int winner = -1) => new(completed, finished, winner,
        Enumerable.Range(0, NetProtocol.KnockoutEntrants).Select(i => new NetKnockoutEntry(
            i < 2 ? (i == 0 ? "alice" : "bob") : $"BOT{i}".PadRight(NetProtocol.MaxNameLength, 'x')[..NetProtocol.MaxNameLength],
            i < 2 ? i : -1, i != 8, completed > 0 ? 9 - i : 0, i == 8 ? 0 : -1)).ToArray());

    static NetLobbyState State(NetKnockoutState? series, int cutoff = 0, NetGameMode mode = NetGameMode.Knockout) => new(
        3, Epoch, "ko", NetGamePhase.Race, 1, 2, 4, mode, 0,
        [new NetLobbyMember(0, "alice", false, 3, true, true), new NetLobbyMember(1, "bob", false, NetProtocol.NoCarType, false, true)],
        null, false, NetProtocol.AutoBots, false, cutoff, series);

    [Fact]
    public void TheVersionIsBumpedAndKnockoutIsAKnownMode()
    {
        Assert.Equal(19, NetProtocol.Version);
        Assert.True(NetProtocol.IsGameMode(NetGameMode.Knockout));
        Assert.True(NetProtocol.IsGameMode(NetGameMode.SingleRace));
        Assert.False(NetProtocol.IsGameMode((NetGameMode)3));
        var create = new NetGameCreate("ko", 4, NetGameMode.Knockout);
        var r = new WireReader(create.Encode());
        var h = NetHeader.Read(ref r);
        Assert.Equal(NetGameMode.Knockout, NetGameCreate.Decode(h, ref r).Mode);
    }

    [Fact]
    public void ASeriesStateAndTheStragglerCountdownSurviveTheWire()
    {
        var state = State(Series(completed: 3, finished: true, winner: 0), cutoff: 118_250);
        var back = ReadState(state.Encode());
        Assert.Equal(118_250, back.CutoffMillis);
        Assert.Equal(3, back.Series!.CompletedRounds);
        Assert.True(back.Series.Finished);
        Assert.Equal(0, back.Series.Winner);
        Assert.Equal(state.Series!.Entries, back.Series.Entries);
        Assert.Null(ReadState(State(null, mode: NetGameMode.SingleRace).Encode()).Series);
        Assert.Null(ReadState(State(null).Encode()).Series); // a knockout game before its first round
        Assert.Equal(0, ReadState(State(Series()).Encode()).CutoffMillis);
    }

    [Fact]
    public void AFullLobbyWithASeriesFitsTheReliableBudget()
    {
        var members = Enumerable.Range(0, NetProtocol.MaxSeats)
            .Select(i => new NetLobbyMember(i, ("p" + i.ToString("D2")).PadRight(NetProtocol.MaxNameLength, 'z'), true, i, i == 0, true)).ToArray();
        var series = new NetKnockoutState(8, true, 0, Enumerable.Range(0, NetProtocol.KnockoutEntrants)
            .Select(i => new NetKnockoutEntry(new string('n', NetProtocol.MaxNameLength), i < 8 ? i : -1, false, 9, 7)).ToArray());
        var state = new NetLobbyState(9, Epoch, new string('G', NetProtocol.MaxGameNameLength), NetGamePhase.Race, 7, 8, 8,
            NetGameMode.Knockout, 0, members, new byte[NetProtocol.ResumeTokenLength], true, 7, true, NetProtocol.MaxCutoffMillis, series);
        var bytes = state.Encode();
        Assert.True(bytes.Length < NetProtocol.MaxReliableMessage);
        Assert.Equal(series.Entries, ReadState(bytes).Series!.Entries);
    }

    [Fact]
    public void InvalidSeriesStatesAreRefusedByTheEncoderAndTheDecoder()
    {
        var good = Series();
        Assert.Throws<ArgumentException>(() => State(good with { Entries = good.Entries.Take(8).ToArray() }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => State(good with { CompletedRounds = 9 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => State(good with { Winner = 9 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => State(good with { Entries = good.Entries.Select(e => e with { Place = 10 }).ToArray() }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => State(good, cutoff: NetProtocol.MaxCutoffMillis + 1).Encode());
        Assert.Throws<ArgumentException>(() => State(good, mode: NetGameMode.SingleRace).Encode()); // only a knockout game has a series
        // The decoder is as strict as the encoder: a series in a single-race state and trailing bytes are protocol errors.
        var bytes = State(good).Encode();
        bytes[NetHeader.Size + 1 + "ko".Length + 4] = (byte)NetGameMode.SingleRace; // the mode byte after name, phase, track, laps, max players
        Assert.Throws<NetProtocolException>(() => ReadState(bytes));
        Assert.Throws<NetProtocolException>(() => ReadState([.. State(good).Encode(), 0]));
        Assert.Throws<NetProtocolException>(() => ReadState(State(good).Encode().AsSpan(0, State(good).Encode().Length - 3).ToArray()));
    }

    [Fact]
    public void TheStragglerLimitIsAdministrativeConfiguration()
    {
        var defaults = new SessionServerConfig();
        Assert.Equal(120, defaults.StragglerSeconds);
        Assert.Equal(120_000_000, defaults.ToServerOptions().StragglerLimitMicros);
        Assert.Equal(120_000_000, new SessionServerOptions().StragglerLimitMicros);
        Assert.True((defaults with { StragglerSeconds = 0 }).Validate(out _)); // 0 = unlimited
        Assert.False((defaults with { StragglerSeconds = -1 }).Validate(out string error));
        Assert.Contains("straggler", error);
        string path = Path.Combine(Path.GetTempPath(), $"nfs-straggler-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(SessionServerConfigStore.TrySave(path, defaults with { StragglerSeconds = 45 }, out error), error);
            Assert.True(SessionServerConfigStore.TryLoad(path, out var loaded, out error), error);
            Assert.Equal(45, loaded!.StragglerSeconds);
            File.WriteAllText(path, "{ \"port\": 1234 }");
            Assert.True(SessionServerConfigStore.TryLoad(path, out loaded, out error), error);
            Assert.Equal(120, loaded!.StragglerSeconds); // an older file keeps the default
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheHeadlessClientParsesTheKnockoutMode()
    {
        Assert.True(HeadlessOptions.TryParse(["--server", "127.0.0.1:1", "--create", "g", "--mode", "ko", "--ko-rounds", "3"], out var o, out string error), error);
        Assert.True(o.Knockout);
        Assert.Equal(3, o.KoRounds);
        Assert.True(HeadlessOptions.TryParse(["--server", "127.0.0.1:1", "--create", "g"], out o, out _));
        Assert.False(o.Knockout);
        Assert.Equal(2, o.KoRounds);
        Assert.False(HeadlessOptions.TryParse(["--server", "127.0.0.1:1", "--create", "g", "--mode", "league"], out _, out error));
        Assert.Contains("--mode", error);
        Assert.False(HeadlessOptions.TryParse(["--server", "127.0.0.1:1", "--create", "g", "--ko-rounds", "9"], out _, out error));
    }

    [GameFact]
    public void ARoundOfTheSeriesHasTheEntrantsOfTheStageWithTheHumansFirstAndNoGapInTheGrid()
    {
        var config = new ServerRaceConfig(Laps: 2, Players: 3);
        var ko = KnockoutSeries.ForNetwork(3);
        var first = config.BuildKnockoutSetup(GameData.Dir, ko, [NetProtocol.NoCarType, 4, NetProtocol.NoCarType]);
        Assert.Equal((9, 3, 6), (first.NumCars, first.NumPlayerCars, first.NumOpponentCars));
        Assert.Equal(2, first.NumLaps);
        Assert.Equal(Enumerable.Range(0, 9), Enumerable.Range(0, 9).Select(first.GridSlot));
        Assert.Equal([1, 1, 1, 2, 2, 2, 2, 2, 2], first.Cars.Select(c => c.CarClass));
        Assert.Equal(4, first.Car(1).CarType); // the chosen model of the second human

        // human 1 is out and the last place (identity 8) is gone: seven entrants, humans 0 and 2 first
        ko.Complete(new RaceResults(Enumerable.Range(0, 9).Select(i => new RacerResult(i + 1, i, "M", i < 3, false, 1000 + i, [], null)).ToArray()));
        ko.Forfeit(1);
        ko.NextRace();
        var next = config.BuildKnockoutSetup(GameData.Dir, ko, [NetProtocol.NoCarType, NetProtocol.NoCarType]);
        Assert.Equal((7, 2, 5), (next.NumCars, next.NumPlayerCars, next.NumOpponentCars));
        Assert.Equal([1, 1, 2, 2, 2, 2, 2], next.Cars.Select(c => c.CarClass));
        Assert.Equal(Enumerable.Range(0, 7).Reverse(), Enumerable.Range(0, 7).Select(next.GridSlot)); // best of the last round in front
        Assert.Equal(first.Car(2).Personality, next.Car(1).Personality); // identity 2 kept its record when records were packed
        Assert.Equal(first.Car(3).Personality, next.Car(2).Personality);

        using var host = config.CreateKnockoutHost(GameData.Dir, 77, ko, null);
        Assert.Equal(2, host.Seats);
        Assert.Equal(7, host.Runner.Cars.Count);
    }
}
