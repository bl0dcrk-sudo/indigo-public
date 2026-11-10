using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Operator track selection in the dedicated lobby (TASKS item 15): `track N` (0..7) in the console selects the track
/// of the next composed round. The lobby host is only a placeholder while the clients wait; `start`/`next` compose a fresh
/// host on the selected track and re-welcome the clients, which load the other race data before the countdown. The command is
/// a lobby operation (refused during a race and while a composed start waits for its clients), never touches the current
/// result and, for an unchanged track, composes exactly the same setup as the ordinary composition.</summary>
public class ServerTrackLobbyTests
{
    const int StartStep = 256;

    static RaceServerOptions Options(ServerRaceConfig race, int raceSteps) => new()
    {
        ManualStart = true,
        MaxRaceSteps = raceSteps,
        WaitTimeoutMicros = 60_000_000,
        LingerMicros = 0, // an interactive server keeps the result until `next` or `quit`
        BotCount = race.BotCount,
        BotSetting = race.BotSetting,
        MinPlayers = race.EffectiveMinimumPlayers,
    };

    /// <param name="composed">Every track the track-aware factory was asked for, in order.</param>
    static NetHarness Harness(int seed, ServerRaceConfig race, int raceSteps, out List<int> composed)
    {
        var tracks = new List<int>();
        composed = tracks;
        return new NetHarness(seed, Options(race, raceSteps), race,
            hostFactoryBots: (id, humans, bots) => race.CreateHost(GameData.Dir, id, humans, bots),
            hostFactoryTrack: (id, humans, bots, types, track, backward) =>
            {
                tracks.Add(track);
                return (race with { Track = track, Backward = backward }).CreateHost(GameData.Dir, id, humans, bots, types);
            });
    }

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    static bool Started(NetHarness h, params RaceClient[] clients) => h.RunUntil(() =>
        (h.Server.Phase is NetRacePhase.Countdown or NetRacePhase.Racing) &&
        clients.All(c => c.Welcome!.RaceId == h.Server.RaceId && c.Baseline!.RaceId == h.Server.RaceId),
        10_000_000);

    [GameFact]
    public void TrackSelectedInTheLobbyComposesTheNextRoundOnIt()
    {
        var race = new ServerRaceConfig(Track: 1, Players: 2) { MinimumPlayers = 1 };
        using var h = Harness(9600, race, StartStep + 40, out var composed);
        var welcomes = new List<NetWelcome>();
        var alice = h.Join("alice", LinkProfile.Rtt(20), LinkProfile.Rtt(20),
            prepare: welcome => { welcomes.Add(welcome); return true; });

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.Equal(1, h.Server.Track);
        Assert.True(h.Server.SupportsTrackChange);

        // Out of range: refused, the selection stays.
        Assert.False(h.Server.TrySetTrack(8, out string rangeError));
        Assert.Contains("0..7", rangeError);
        Assert.Equal(1, h.Server.Track);

        // `track N` uses the menu index of the configuration dialog; menu 3 loads the data track TR05.
        Assert.True(h.Server.TrySetTrack(3, out string error), error);
        Assert.Equal(3, h.Server.Track);
        Assert.Equal(5, h.Server.DataTrack);
        Assert.Equal(5, h.Server.Lobby.Track);
        Assert.Contains("track=TR05", ServerConsole.FormatLobby(h.Server.Lobby));
        Assert.Contains(h.Log, l => l.EndsWith("track: menu track 3 (TR05) selected for the next start"));

        int before = welcomes.Count;
        Assert.True(h.Server.TryOperatorStart(out error), error);
        // While the composed start waits for the clients' new welcome nothing may change the lobby.
        Assert.False(h.Server.TrySetTrack(0, out string pendingError));
        Assert.Contains("being prepared", pendingError);
        Assert.Equal([3], composed);
        Assert.Equal(5, h.Server.Host.Runner.Session.TrackNumber);
        Assert.Equal(5, h.Server.Host.Runner.Session.Setup.Track);

        Assert.True(Started(h, alice), Tail(h));
        // The client was re-welcomed with the setup of the selected track before the countdown.
        Assert.Equal(before + 1, welcomes.Count);
        var clientSetup = new NFS.Game.Race.RaceSetup();
        welcomes[^1].SetupBlock.CopyTo(clientSetup.Block, 0);
        Assert.Equal(5, clientSetup.Track);

        Assert.False(h.Server.TrySetTrack(0, out string racingError));
        Assert.Contains("cannot change the track while the race is", racingError);
        Assert.Equal(3, h.Server.Track);

        Assert.True(h.RunUntil(() => h.Server.Phase is NetRacePhase.Finished or NetRacePhase.Aborted, 40_000_000), Tail(h));
        Assert.True(h.Server.TrySetTrack(6, out error), error);
        Assert.Equal(6, h.Server.Track);
        Assert.Equal(7, h.Server.Lobby.Track);
    }

    [GameFact]
    public void NextRoundLoadsTheTrackSelectedAfterTheResult()
    {
        var race = new ServerRaceConfig(Track: 1, Players: 2) { MinimumPlayers = 1 };
        using var h = Harness(9601, race, StartStep + 30, out var composed);
        var alice = h.Join("alice", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));
        Assert.Equal(2, h.Server.Host.Runner.Session.TrackNumber); // menu track 1 = TR02

        Assert.True(h.Server.TrySetTrack(5, out error), error); // menu 5 = TR06
        Assert.True(h.Server.TryOperatorNext(out error), error);
        // The lobby placeholder itself already sits on the selected track.
        Assert.Equal(6, h.Server.Host.Runner.Session.TrackNumber);
        Assert.Equal(6, h.Server.Lobby.Track);
        // The start above already composed the race on the unchanged track 1; `next` composed the lobby on 5.
        Assert.Equal([1, 5], composed);

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.Equal(6, h.Server.Host.Runner.Session.TrackNumber);
        Assert.Equal(6, h.Server.Host.Runner.Session.Setup.Track);
        Assert.Equal([1, 5, 5], composed); // start on 1, `next` on 5, then the race itself on 5
    }

    [GameFact]
    public void AnUnchangedTrackComposesTheSameSetupByteForByte()
    {
        var race = new ServerRaceConfig(Track: 2, Players: 2) { MinimumPlayers = 1 };
        using var h = Harness(9602, race, StartStep + 30, out var composed);
        var alice = h.Join("alice", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TrySetTrack(2, out string error), error); // the track the lobby already runs on
        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.Equal([2], composed);
        Assert.Equal(4, h.Server.Host.Runner.Session.TrackNumber); // menu 2 = TR04

        // The track-aware composition is a superset of the ordinary one: with the same track it produces the very same
        // frozen setup block (the recordings and the byte-identical local setups stay valid).
        int bots = h.Server.Host.Runner.Session.Setup.NumOpponentCars;
        using var reference = race.CreateHost(GameData.Dir, 0x1234, 1, bots);
        Assert.Equal(reference.Runner.Session.Setup.Block, h.Server.Host.Runner.Session.Setup.Block);

        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));
    }

    [GameFact]
    public void ServerWithoutATrackFactoryRefusesTheCommand()
    {
        var race = new ServerRaceConfig(Track: 1, Players: 2) { MinimumPlayers = 1 };
        using var h = new NetHarness(9603, Options(race, StartStep), race);
        Assert.False(h.Server.SupportsTrackChange);
        Assert.False(h.Server.TrySetTrack(3, out string error));
        Assert.Contains("cannot compose a host on another track", error);
        Assert.Equal(1, h.Server.Track);
        Assert.Equal(2, h.Server.Lobby.Track); // data track of menu track 1
    }
}
