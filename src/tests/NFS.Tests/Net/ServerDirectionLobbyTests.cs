using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Operator track direction selection in the dedicated lobby: `direction forward|backward` in the console selects the
/// direction of the next composed round. The lobby host is only a placeholder while the clients wait; `start`/`next` compose a
/// fresh host in the selected direction and re-welcome the clients. The command is a lobby operation (refused during a race and
/// while a composed start waits for its clients), never touches the current result and, for an unchanged direction, composes
/// exactly the same setup as the ordinary composition.</summary>
public class ServerDirectionLobbyTests
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

    /// <param name="composed">Every direction the track-aware factory was asked for, in order.</param>
    static NetHarness Harness(int seed, ServerRaceConfig race, int raceSteps, out List<bool> composed)
    {
        var backwards = new List<bool>();
        composed = backwards;
        return new NetHarness(seed, Options(race, raceSteps), race,
            hostFactoryBots: (id, humans, bots) => race.CreateHost(GameData.Dir, id, humans, bots),
            hostFactoryTrack: (id, humans, bots, types, track, backward) =>
            {
                backwards.Add(backward);
                return (race with { Track = track, Backward = backward }).CreateHost(GameData.Dir, id, humans, bots, types);
            });
    }

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    static bool Started(NetHarness h, params RaceClient[] clients) => h.RunUntil(() =>
        (h.Server.Phase is NetRacePhase.Countdown or NetRacePhase.Racing) &&
        clients.All(c => c.Welcome!.RaceId == h.Server.RaceId && c.Baseline!.RaceId == h.Server.RaceId),
        10_000_000);

    [GameFact]
    public void DirectionSelectedInTheLobbyComposesTheNextRoundBackward()
    {
        var race = new ServerRaceConfig(Track: 1, Players: 2) { MinimumPlayers = 1 };
        using var h = Harness(9610, race, StartStep + 40, out var composed);
        var welcomes = new List<NetWelcome>();
        var alice = h.Join("alice", LinkProfile.Rtt(20), LinkProfile.Rtt(20),
            prepare: welcome => { welcomes.Add(welcome); return true; });

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.False(h.Server.Backward); // the menu default: forward
        Assert.True(h.Server.SupportsDirectionChange);

        Assert.True(h.Server.TrySetBackward(true, out string error), error);
        Assert.True(h.Server.Backward);
        Assert.Contains("direction=backward", ServerConsole.FormatLobby(h.Server.Lobby));
        Assert.Contains(h.Log, l => l.EndsWith("direction: backward selected for the next start"));

        int before = welcomes.Count;
        Assert.True(h.Server.TryOperatorStart(out error), error);
        // While the composed start waits for the clients' new welcome nothing may change the direction.
        Assert.False(h.Server.TrySetBackward(false, out string pendingError));
        Assert.Contains("being prepared", pendingError);
        Assert.Equal([true], composed);
        Assert.True(h.Server.Host.Runner.Session.Backward);
        Assert.True(h.Server.Host.Runner.Session.Setup.Backward);

        Assert.True(Started(h, alice), Tail(h));
        // The client was re-welcomed with the setup of the selected direction before the countdown.
        Assert.Equal(before + 1, welcomes.Count);
        var clientSetup = new NFS.Game.Race.RaceSetup();
        welcomes[^1].SetupBlock.CopyTo(clientSetup.Block, 0);
        Assert.True(clientSetup.Backward);

        Assert.False(h.Server.TrySetBackward(false, out string racingError));
        Assert.Contains("cannot change the direction while the race is", racingError);
        Assert.True(h.Server.Backward);

        Assert.True(h.RunUntil(() => h.Server.Phase is NetRacePhase.Finished or NetRacePhase.Aborted, 40_000_000), Tail(h));
        Assert.True(h.Server.TrySetBackward(false, out error), error);
        Assert.False(h.Server.Backward);
    }

    [GameFact]
    public void AnUnchangedDirectionComposesTheSameSetupByteForByte()
    {
        var race = new ServerRaceConfig(Track: 2, Players: 2) { MinimumPlayers = 1 };
        using var h = Harness(9611, race, StartStep + 30, out var composed);
        var alice = h.Join("alice", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.False(h.Server.Backward);
        Assert.True(h.Server.TrySetBackward(false, out string error), error); // the direction the lobby already runs in
        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.Equal([false], composed);

        // The direction-aware composition is a superset of the ordinary one: with the same direction it produces the very
        // same frozen setup block (the recordings and the byte-identical local setups stay valid).
        int bots = h.Server.Host.Runner.Session.Setup.NumOpponentCars;
        using var reference = race.CreateHost(GameData.Dir, 0x1234, 1, bots);
        Assert.Equal(reference.Runner.Session.Setup.Block, h.Server.Host.Runner.Session.Setup.Block);

        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));
    }

    [GameFact]
    public void ServerWithoutATrackFactoryRefusesTheCommand()
    {
        var race = new ServerRaceConfig(Track: 1, Players: 2) { MinimumPlayers = 1 };
        using var h = new NetHarness(9612, Options(race, StartStep), race);
        Assert.False(h.Server.SupportsDirectionChange);
        Assert.False(h.Server.TrySetBackward(true, out string error));
        Assert.Contains("cannot compose a host with a changed direction", error);
        Assert.False(h.Server.Backward);
    }
}
