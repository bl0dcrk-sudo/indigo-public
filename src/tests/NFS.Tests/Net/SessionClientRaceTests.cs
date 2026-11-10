using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Task 4.2: the <see cref="RaceClient"/> of every round lives inside the <see cref="SessionClient"/> on the shared
/// connection (no hello of its own). Full cycles against the real <see cref="SessionServer"/> on the simulated network:
/// lobby, race, results, lobby and a second round; a countdown that arrives while the results are still shown; leaving the
/// race and returning on the same connection; a reconnect with the token in the middle of the race.</summary>
public class SessionClientRaceTests
{
    /// <summary>Session client wired to the harness network; its current race client is driven like an application would
    /// (the owner updates it and sets the controls).</summary>
    sealed class Rig
    {
        readonly SessionHarness _h;
        public SessionClient Client { get; }
        public List<SimNetwork.SimClient> Links { get; } = [];
        public List<RaceClient?> Changes { get; } = [];
        public List<INetClientHandler> Handlers { get; } = [];
        public List<byte[]> WelcomeBytes { get; } = [];
        public List<int> PrepareCalls { get; } = [];
        /// <summary>Answer of the race-data hook (false = the data of the welcome cannot be loaded: the client leaves the race).</summary>
        public bool PrepareResult { get; set; } = true;
        public SimNetwork.SimClient Link => Links[^1];

        public Rig(SessionHarness h, string name)
        {
            _h = h;
            Client = new SessionClient(SessionHarness.Fingerprint, name, Open, () => h.Net.Now,
                new SessionClientOptions { PrepareRace = w => { PrepareCalls.Add(w.RaceId); return PrepareResult; } },
                s => h.Log.Add($"{h.Net.Now / 1000} {s}"));
            Client.RaceClientChanged += Changes.Add;
            Client.RaceMessage += (_, data) => { if (data[0] == (byte)NetMessageType.Welcome) WelcomeBytes.Add(data.ToArray()); };
        }

        INetClientTransport Open(INetClientHandler handler, byte[] hello)
        {
            var link = _h.Net.AddClient(handler, new LinkProfile(), new LinkProfile());
            Links.Add(link);
            Handlers.Add(handler);
            link.Connect(hello);
            return link;
        }

        public void Drive(long now)
        {
            Client.Update(now);
            if (Client.Race is { } race)
            {
                race.Controls = NetHarness.Weave(Client.Me?.Slot ?? 0, race.Latest);
                race.Update(now);
            }
        }
    }

    // A short race: the server ends the round at this step, so results and the return to the lobby come quickly.
    static readonly SessionServerOptions Short = new()
    {
        CountdownMicros = 500_000, MaxRaceSteps = 256 + 150, GraceMicros = 30_000_000,
    };

    static SessionHarness Server(int seed, SessionServerOptions? options = null)
    {
        var h = new SessionHarness(seed, options ?? Short);
        h.Net.DisconnectTimeoutMicros = 200_000;
        return h;
    }

    static bool Until(SessionHarness h, Func<bool> done, long micros, params Rig[] rigs) =>
        h.RunUntil(() =>
        {
            foreach (var rig in rigs) rig.Drive(h.Net.Now);
            return done();
        }, micros);

    static void Wait(SessionHarness h, Func<bool> done, string what, long micros, params Rig[] rigs) =>
        Assert.True(Until(h, done, micros, rigs), $"timed out: {what}\n{string.Join("\n", h.Log.TakeLast(25))}");

    /// <summary>alice creates a one-lap game on track 1, bob joins it.</summary>
    static (Rig Alice, Rig Bob) Lobby(SessionHarness h)
    {
        var alice = new Rig(h, "alice");
        alice.Client.Connect();
        Wait(h, () => alice.Client.Phase == SessionClientPhase.Browsing, "alice connected", 2_000_000, alice);
        alice.Client.CreateGame("game", 4);
        Wait(h, () => alice.Client.Phase == SessionClientPhase.InGame, "create", 2_000_000, alice);
        var bob = new Rig(h, "bob");
        bob.Client.Connect();
        Wait(h, () => bob.Client.Phase == SessionClientPhase.Browsing, "bob connected", 2_000_000, alice, bob);
        bob.Client.JoinGame(alice.Client.GameId);
        Wait(h, () => bob.Client.Phase == SessionClientPhase.InGame && alice.Client.Lobby!.Members.Count == 2, "join", 2_000_000, alice, bob);
        return (alice, bob);
    }

    static bool Finished(Rig r) => r.Client.Race is { State: RaceClientState.Finished, Results: not null };

    /// <summary>Starts a round and waits until both clients have its results and are back in the lobby.</summary>
    static void PlayRound(SessionHarness h, Rig alice, Rig bob, RaceClient? previous)
    {
        Assert.True(alice.Client.StartRace());
        Wait(h, () => alice.Client.Race != previous && bob.Client.Race != previous && Finished(alice) && Finished(bob)
            && alice.Client.Lobby!.Phase == NetGamePhase.Lobby && bob.Client.Lobby!.Phase == NetGamePhase.Lobby,
            "round finished", 60_000_000, alice, bob);
    }

    [GameFact]
    public void LobbyRaceResultsLobbyAndASecondRoundEachGetTheirOwnRaceClientOnOneConnection()
    {
        using var h = Server(101);
        var (alice, bob) = Lobby(h);
        Assert.Null(alice.Client.Race);
        Assert.Empty(alice.Changes);

        PlayRound(h, alice, bob, null);
        var a1 = alice.Client.Race!;
        var b1 = bob.Client.Race!;
        Assert.Equal([a1], alice.Changes);
        Assert.Equal(a1.Welcome!.RaceId, b1.Welcome!.RaceId);
        Assert.NotEqual(a1.Welcome.Epoch, b1.Welcome.Epoch);
        Assert.False(a1.Welcome.Resume);
        Assert.Equal([a1.Welcome.RaceId], alice.PrepareCalls);
        Assert.True(a1.Stats.SnapshotsComplete > 10);
        Assert.Equal(a1.Welcome.RaceId, a1.Results!.RaceId);
        var results1 = a1.Results;
        var welcome1Bytes = alice.WelcomeBytes[0];
        Assert.Single(alice.Links);   // no new connection and no second hello for the race
        Assert.Equal(NetGamePhase.Lobby, alice.Client.Lobby!.Phase);
        Assert.True(alice.Client.IsOperator);

        // Second round on the same connection: a clean new instance; the old one keeps its own finished round.
        PlayRound(h, alice, bob, a1);
        var a2 = alice.Client.Race!;
        var b2 = bob.Client.Race!;
        Assert.NotSame(a1, a2);
        Assert.NotSame(b1, b2);
        Assert.NotEqual(a1.Welcome.RaceId, a2.Welcome!.RaceId);
        Assert.NotEqual(a1.Welcome.Epoch, a2.Welcome.Epoch);
        Assert.Equal([a1, a2], alice.Changes);
        Assert.Equal(2, alice.PrepareCalls.Count);
        Assert.Same(results1, a1.Results);
        Assert.Equal(RaceClientState.Finished, a1.State);
        Assert.NotEqual(a1.Results!.RaceId, a2.Results!.RaceId);
        Assert.Single(alice.Links);
        Assert.Single(bob.Links);

        // A replayed welcome of the first round never replaces the live one (the connection's epochs are unique).
        long stale = alice.Client.Stats.StaleWelcomes;
        alice.Handlers[0].OnReceive(NetChannel.ReliableOrdered, welcome1Bytes);
        Assert.Same(a2, alice.Client.Race);
        Assert.Equal(stale + 1, alice.Client.Stats.StaleWelcomes);
        Assert.Equal(0, alice.Client.Stats.Malformed);
        Assert.Equal(0, a2.Stats.Malformed);
    }

    [GameFact]
    public void ARoundWhoseDataCannotBeLoadedSendsTheClientBackToTheGamesListOnTheSameConnection()
    {
        using var h = Server(110);
        var (alice, bob) = Lobby(h);
        bob.PrepareResult = false;
        Assert.True(alice.Client.StartRace());
        // bob's race client leaves the round at its welcome; its session connection must survive that.
        Wait(h, () => bob.PrepareCalls.Count == 1 && bob.Client.Race is { State: RaceClientState.Closed }, "bob left the round", 10_000_000, alice, bob);
        h.Run(500_000);
        Assert.Single(bob.Links);
        Assert.Equal(0, h.Server.Stats.Disconnects);
        Assert.True(bob.Client.Connected);

        // The application reacts as the desktop does (OnSessionError): leave the game and show the list.
        Assert.True(bob.Client.LeaveGame());
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        h.Run(500_000);
        Assert.True(bob.Client.Connected);
        Assert.Single(bob.Links);
        Assert.Equal(0, h.Server.Stats.Disconnects);
        Assert.Equal(0, h.Server.Stats.WrongSession);
        Assert.Single(bob.Client.Games);

        // The remaining player's round is recomposed without bob and ends with a result.
        Wait(h, () => Finished(alice) && alice.Client.Lobby!.Phase == NetGamePhase.Lobby, "alice's round finished", 60_000_000, alice, bob);
        Assert.Equal(1, alice.Client.Race!.Results!.Results.Racers.Count(r => r.Human));
    }

    [GameFact]
    public void ACountdownThatArrivesWhileTheResultsAreShownMovesTheClientOnToTheNextRound()
    {
        using var h = Server(102);
        var (alice, bob) = Lobby(h);
        PlayRound(h, alice, bob, null);
        var a1 = alice.Client.Race!;
        var b1 = bob.Client.Race!;

        // The operator restarts at once; bob is still looking at the finished round when the countdown state reaches him.
        Assert.True(alice.Client.StartRace());
        Wait(h, () => bob.Client.Lobby!.Phase == NetGamePhase.Countdown, "countdown", 5_000_000, alice, bob);
        Assert.Same(b1, bob.Client.Race);
        Assert.Equal(RaceClientState.Finished, b1.State);
        Assert.NotNull(b1.Results);
        Assert.Equal(SessionClientPhase.InGame, bob.Client.Phase);

        Wait(h, () => bob.Client.Race != b1 && alice.Client.Race != a1, "new round client", 10_000_000, alice, bob);
        var b2 = bob.Client.Race!;
        Assert.NotEqual(b1.Welcome!.RaceId, b2.Welcome!.RaceId);
        Assert.Null(b2.Results);
        Wait(h, () => b2.State == RaceClientState.Started && b2.Latest != null, "round running", 10_000_000, alice, bob);
        Assert.Equal(NetGamePhase.Race, bob.Client.Lobby!.Phase);
        Wait(h, () => Finished(alice) && Finished(bob), "second results", 60_000_000, alice, bob);
        Assert.Equal(b2.Welcome.RaceId, bob.Client.Race!.Results!.RaceId);
        // the first round's client never saw a message of the second one
        Assert.Equal(b1.Welcome.RaceId, b1.Results!.RaceId);
        Assert.Equal(b1.Welcome.RaceId, b1.Latest!.RaceId);
    }

    [GameFact]
    public void LeavingTheRaceAndRejoiningFromTheListUsesTheSameConnectionAndAFreshRaceClient()
    {
        using var h = Server(103, new SessionServerOptions { CountdownMicros = 500_000, MaxRaceSteps = 256 + 400, GraceMicros = 30_000_000 });
        var (alice, bob) = Lobby(h);
        Assert.True(alice.Client.StartRace());
        Wait(h, () => bob.Client.Race is { State: RaceClientState.Started, Latest: not null }, "bob racing", 15_000_000, alice, bob);
        var old = bob.Client.Race!;
        int raceId = old.Welcome!.RaceId;
        uint oldEpoch = old.Welcome.Epoch;

        Assert.True(bob.Client.LeaveGame());
        Assert.Null(bob.Client.Race);
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        Assert.Equal([old, null], bob.Changes);
        Assert.NotNull(bob.Client.ResumeToken);
        Wait(h, () => bob.Client.Games.Count == 1 && bob.Client.Games[0].Phase == NetGamePhase.Race, "list", 5_000_000, alice, bob);

        Assert.True(bob.Client.JoinGame(bob.Client.Games[0].GameId));
        Wait(h, () => bob.Client.Phase == SessionClientPhase.InGame && bob.Client.Race is { Latest: not null }, "resumed", 10_000_000, alice, bob);
        var fresh = bob.Client.Race!;
        Assert.NotSame(old, fresh);
        Assert.True(fresh.Welcome!.Resume);
        Assert.Equal(raceId, fresh.Welcome.RaceId);
        Assert.NotEqual(oldEpoch, fresh.Welcome.Epoch);
        Assert.Single(bob.Links);                       // same connection, no new hello
        Assert.Equal(0, bob.Client.Stats.Malformed);
        Assert.True(bob.Client.Stats.BufferedRaceMessages > 0);   // the welcome precedes the lobby state
        Assert.Equal(RaceClientState.Started, fresh.State);
        Assert.Equal(1, fresh.Stats.Resumes);

        // The resumed client plays on to the results and back to the lobby.
        Wait(h, () => Finished(alice) && Finished(bob) && bob.Client.Lobby!.Phase == NetGamePhase.Lobby, "results", 60_000_000, alice, bob);
        Assert.Equal(raceId, bob.Client.Race!.Results!.RaceId);
        Assert.Same(fresh, bob.Client.Race);
        // the dropped client was not fed anything of the resumed round
        Assert.Equal(oldEpoch, old.Welcome.Epoch);
        Assert.Null(old.Results);
    }

    [GameFact]
    public void ALostLinkInTheRaceReconnectsWithTheTokenAndGetsARaceClientOfTheNewEpoch()
    {
        using var h = Server(104, new SessionServerOptions { CountdownMicros = 500_000, MaxRaceSteps = 256 + 500, GraceMicros = 30_000_000 });
        var (alice, bob) = Lobby(h);
        Assert.True(alice.Client.StartRace());
        Wait(h, () => bob.Client.Race is { State: RaceClientState.Started, Latest: not null }, "bob racing", 15_000_000, alice, bob);
        var old = bob.Client.Race!;
        int raceId = old.Welcome!.RaceId;
        long oldParts = old.Stats.SnapshotParts;

        bob.Link.Cut();
        Wait(h, () => bob.Client.Phase == SessionClientPhase.Reconnecting, "loss noticed", 5_000_000, alice, bob);
        Wait(h, () => bob.Client.Phase == SessionClientPhase.InGame && bob.Client.Race != old && bob.Client.Race!.Latest != null,
            "resumed", 10_000_000, alice, bob);
        var fresh = bob.Client.Race!;
        Assert.Equal(2, bob.Links.Count);
        Assert.True(fresh.Welcome!.Resume);
        Assert.Equal(raceId, fresh.Welcome.RaceId);
        Assert.NotEqual(old.Welcome.Epoch, fresh.Welcome.Epoch);
        Assert.Equal(NetGamePhase.Race, bob.Client.Lobby!.Phase);
        Assert.Equal(RaceClientState.Started, fresh.State);
        Assert.Equal(0, bob.Client.Stats.Malformed);
        Assert.Equal(0, fresh.Stats.Malformed);
        Assert.Equal(0, fresh.Stats.WrongSession);
        // the old client never receives a message of the new connection
        Assert.Equal(oldParts, old.Stats.SnapshotParts);

        Wait(h, () => Finished(bob) && Finished(alice), "results after the resume", 60_000_000, alice, bob);
        Assert.Equal(raceId, bob.Client.Race!.Results!.RaceId);
        Assert.Same(fresh, bob.Client.Race);
        Assert.Equal(2, bob.PrepareCalls.Count);

        // and the next round after the reconnect is a normal new round of the new connection
        Wait(h, () => bob.Client.Lobby!.Phase == NetGamePhase.Lobby && alice.Client.Lobby!.Phase == NetGamePhase.Lobby, "lobby", 10_000_000, alice, bob);
        var last = bob.Client.Race;
        PlayRound(h, alice, bob, last);
        Assert.NotEqual(raceId, bob.Client.Race!.Welcome!.RaceId);
        Assert.False(bob.Client.Race.Welcome.Resume);
    }

    [GameFact]
    public void ReconnectingToTheListDropsTheRaceClientAndTheEndOfTheSessionClosesIt()
    {
        using var h = Server(105);
        var (alice, bob) = Lobby(h);
        PlayRound(h, alice, bob, null);
        var round = bob.Client.Race!;
        // lobby seats are not reserved: after a lost link bob lands on the list without a seat and without a race client
        bob.Link.Cut();
        Wait(h, () => bob.Client.Phase == SessionClientPhase.Browsing && bob.Client.Stats.Reconnects == 1, "back on the list", 10_000_000, alice, bob);
        Assert.Null(bob.Client.Race);
        Assert.Equal([round, null], bob.Changes);

        // a finished round's client reports the end of the session as closed only while it is not finished
        PlayRound2(h, alice);
    }

    /// <summary>Alice alone plays a round, then disconnects while racing: her race client is told the session ended.</summary>
    static void PlayRound2(SessionHarness h, Rig alice)
    {
        Wait(h, () => alice.Client.Lobby!.Members.Count == 1 && alice.Client.Lobby.Phase == NetGamePhase.Lobby, "alone", 5_000_000, alice);
        Assert.True(alice.Client.StartRace());
        Wait(h, () => alice.Client.Race is { State: RaceClientState.Started }, "racing", 15_000_000, alice);
        var race = alice.Client.Race!;
        alice.Client.Disconnect();
        Assert.Equal(SessionClientPhase.Closed, alice.Client.Phase);
        Assert.True(race.Disconnected);
        Assert.Equal(RaceClientState.Closed, race.State);
    }
}
