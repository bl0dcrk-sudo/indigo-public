using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Session server: handshake, unique names, connection registry, routing of foreign headers, flood and slow-client
/// limits.</summary>
public class SessionServerHandshakeTests
{
    static byte[] Hello(string name, ushort version = NetProtocol.Version, ushort physics = NetProtocol.PhysicsVersion,
        byte[]? fingerprint = null, Version? app = null, int car = NetProtocol.NoCarType) =>
        new NetHello(version, physics, fingerprint ?? SessionHarness.Fingerprint, name, car, app).Encode();

    [GameFact]
    public void Handshake_RejectsWrongVersionsFingerprintAndDuplicateName()
    {
        using var h = new SessionHarness(10, new SessionServerOptions { MaxConnections = 16 });
        var alice = h.Join("alice");
        Assert.True(h.RunUntil(() => alice.Connected && alice.List != null, 1_000_000));

        var duplicate = h.Join("ALICE");
        var protocol = h.Join("protocol", hello: Hello("protocol", version: (ushort)(NetProtocol.Version + 1)));
        var physics = h.Join("physics", hello: Hello("physics", physics: (ushort)(NetProtocol.PhysicsVersion + 1)));
        var appVersion = h.Join("app", hello: Hello("app", app: new Version(99, 1, 2, 3)));
        var data = h.Join("data", hello: Hello("data", fingerprint: new byte[32]));
        var malformed = h.Join("bad", hello: [1, 2, 3]);
        var legacy = h.Join("legacy", hello: NetRobustnessTests.LegacyHello(7));

        Assert.True(h.RunUntil(() => duplicate.Disconnected && protocol.Disconnected && physics.Disconnected &&
            appVersion.Disconnected && data.Disconnected && malformed.Disconnected && legacy.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, duplicate.Reject?.Reason);
        Assert.Equal(NetRejectReason.ProtocolMismatch, protocol.Reject?.Reason);
        Assert.Equal(NetRejectReason.PhysicsMismatch, physics.Reject?.Reason);
        Assert.Equal(NetRejectReason.AppVersionMismatch, appVersion.Reject?.Reason);
        Assert.Equal(BuildInfo.AppVersion, appVersion.Reject?.ServerAppVersion);
        Assert.Equal(NetRejectReason.DataMismatch, data.Reject?.Reason);
        Assert.Equal(NetRejectReason.Malformed, malformed.Reject?.Reason);
        Assert.Equal(NetRejectReason.ProtocolMismatch, legacy.Reject?.Reason);
        Assert.Equal(1, h.Server.ConnectionCount);
        Assert.Equal(7, h.Server.Stats.Rejected);
    }

    [GameFact]
    public void LostLobbyMember_UpdatesTheGameListOfIdleClients()
    {
        using var h = new SessionHarness(12);
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null && carol.List != null, 1_000_000));
        alice.Create("game", 4, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => carol.List?.Games.SingleOrDefault()?.Players == 2, 1_000_000));

        bob.Disconnect();
        Assert.True(h.RunUntil(() => carol.List?.Games.Single().Players == 1, 1_000_000));
    }

    [GameFact]
    public void RefusedCreate_IsAnsweredWithTheCurrentList()
    {
        using var h = new SessionHarness(13);
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null, 1_000_000));
        alice.Create("game", 4, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby && bob.List?.Games.Count == 1, 1_000_000));
        int before = bob.Lists.Count;

        bob.Create("GAME", 4, 1, 1);
        Assert.True(h.RunUntil(() => bob.Lists.Count > before, 1_000_000));
        Assert.Null(bob.Lobby);
        Assert.Equal(1, h.Server.Stats.RefusedCreates);
        Assert.Single(bob.List!.Games);
    }

    [GameFact]
    public void Disconnect_ReleasesTheNameForANewConnection()
    {
        using var h = new SessionHarness(11);
        var first = h.Join("alice");
        Assert.True(h.RunUntil(() => first.Connected && first.List != null, 1_000_000));
        first.Disconnect();
        Assert.True(h.RunUntil(() => h.Server.ConnectionCount == 0, 1_000_000));

        var again = h.Join("alice");
        Assert.True(h.RunUntil(() => again.Connected && again.List != null, 1_000_000));
        Assert.Null(again.Reject);
        Assert.Equal(1, h.Server.ConnectionCount);
    }

    [GameFact]
    public void ForeignGameAndEpochMessages_AreCountedAndDoNotChangeState()
    {
        using var h = new SessionHarness(12, new SessionServerOptions { MaxGames = 4 });
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null && carol.List != null, 1_000_000));

        alice.Create("alpha", 4, 2, 2);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby, 1_000_000));
        carol.Create("beta", 4, 5, 3);
        Assert.True(h.RunUntil(() => carol.InLobby, 1_000_000));
        Assert.Equal(2, h.Server.GameCount);

        long wrongBefore = h.Server.Stats.WrongSession;
        bob.Send(new NetLobbyReady(alice.GameId, bob.Epoch ^ 0xFFFF_FFFFu, true).Encode());
        bob.Send(new NetLobbyReady(carol.GameId, bob.Epoch, true).Encode());
        h.Run(100_000);

        Assert.True(h.Server.Stats.WrongSession >= wrongBefore + 2);
        Assert.False(bob.Ready);
        Assert.False(alice.Lobby!.Members.First(m => m.Name == "alice").Ready);
        Assert.All(carol.Lobby!.Members, m => Assert.False(m.Ready));

        long refusedBefore = h.Server.Stats.RefusedLobby;
        bob.Send(new NetGameCreate("gamma", 4).Encode());
        h.Run(50_000);
        Assert.True(h.Server.Stats.RefusedLobby > refusedBefore);
        Assert.Equal(2, h.Server.GameCount);
        Assert.DoesNotContain(h.Server.Games, g => g.Name == "gamma");
    }

    [GameFact]
    public void FloodAboveThePerSecondLimit_IsDroppedWithoutChangingState()
    {
        using var h = new SessionHarness(13, new SessionServerOptions { MaxReliableMessagesPerSecond = 16 });
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null, 1_000_000));
        alice.Create("alpha", 4, 2, 2);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby, 1_000_000));

        bob.SetReady(true);
        h.Run(10_000);
        Assert.True(bob.Ready);
        long limitedBefore = h.Server.Stats.RateLimited;
        for (int i = 0; i < 50; i++) bob.SetReady(true);
        h.Run(200_000);

        Assert.True(h.Server.Stats.RateLimited >= limitedBefore + 30);
        Assert.False(bob.Disconnected);
        Assert.True(bob.Ready);
    }

    [GameFact]
    public void SlowClient_IsDisconnectedWithSlowClientAndOtherMembersStay()
    {
        using var h = new SessionHarness(14, new SessionServerOptions { MaxReliableBacklog = 3 });
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null, 1_000_000));
        alice.Create("alpha", 4, 2, 2);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby, 1_000_000));
        h.Run(100_000);

        bob.Link.SetProfiles(new LinkProfile(), new LinkProfile(BandwidthBytesPerSecond: 64));
        for (int i = 0; i < 10 && !bob.Disconnected; i++)
        {
            alice.SetReady(i % 2 == 0);
            h.Run(100_000);
        }
        Assert.True(h.RunUntil(() => bob.Disconnected, 5_000_000));

        Assert.Equal(NetRejectReason.SlowClient, bob.Reject?.Reason);
        Assert.False(alice.Disconnected);
        Assert.True(h.Server.Stats.SlowClientDrops >= 1);
        Assert.Equal(1, h.Server.ConnectionCount);
    }
}

/// <summary>Game registry: parallel games with isolated state, the configurable game limit, duplicate game names and cleanup
/// of empty or idle lobbies.</summary>
public class GameSessionRegistryTests
{
    static (SessionHarness H, SessionTestClient Alice, SessionTestClient Bob, SessionTestClient Carol) Three(
        int seed, SessionServerOptions? options = null)
    {
        var h = new SessionHarness(seed, options);
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null && carol.List != null, 1_000_000));
        return (h, alice, bob, carol);
    }

    [GameFact]
    public void TwoParallelGames_HaveIsolatedStateAndDifferentOptions()
    {
        var (h, alice, bob, carol) = Three(20, new SessionServerOptions { MaxGames = 4 });
        using (h)
        {
            var dave = h.Join("dave");
            Assert.True(h.RunUntil(() => dave.List != null, 1_000_000));
            alice.Create("alpha", 4, 2, 2);
            Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
            bob.Create("beta", 4, 5, 3);
            Assert.True(h.RunUntil(() => bob.InLobby, 1_000_000));
            Assert.True(h.RunUntil(() => carol.List?.Games.Count == 2, 1_000_000));

            carol.JoinGame(alice.GameId);
            Assert.True(h.RunUntil(() => carol.InLobby && alice.Lobby?.Members.Count == 2, 1_000_000));

            Assert.Single(bob.Lobby!.Members);
            Assert.Equal("bob", bob.Lobby.Members[0].Name);
            Assert.Equal(5, bob.Lobby.Track);
            Assert.Equal(3, bob.Lobby.Laps);
            Assert.Equal(2, alice.Lobby!.Track);
            Assert.Equal(NetGamePhase.Lobby, bob.Lobby.Phase);
            Assert.NotEqual(alice.GameId, bob.GameId);

            var listed = dave.List!.Games;
            Assert.Equal(2, listed.Count);
            Assert.Equal(2, listed.Single(g => g.GameId == alice.GameId).Players);
            Assert.Equal(1, listed.Single(g => g.GameId == bob.GameId).Players);
            Assert.Equal(5, listed.Single(g => g.GameId == bob.GameId).Track);
            Assert.Equal(2, listed.Single(g => g.GameId == alice.GameId).Track);
        }
    }

    [GameFact]
    public void GameLimitAndDuplicateNames_RefuseCreationWithoutAffectingExistingGames()
    {
        var (h, alice, bob, carol) = Three(21, new SessionServerOptions { MaxGames = 2 });
        using (h)
        {
            alice.Create("alpha", 4, 2, 2);
            Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
            bob.Create("beta", 4, 5, 3);
            Assert.True(h.RunUntil(() => bob.InLobby, 1_000_000));

            carol.Create("gamma", 4, 1, 1);
            h.Run(100_000);
            Assert.Equal(2, h.Server.GameCount);
            Assert.DoesNotContain(h.Server.Games, g => g.Name == "gamma");
            Assert.True(h.Server.Stats.RefusedCreates >= 1);

            carol.Create("ALPHA", 4, 1, 1);
            h.Run(100_000);
            Assert.Equal(2, h.Server.GameCount);
            Assert.True(h.Server.Stats.RefusedCreates >= 2);
            Assert.Null(carol.Lobby);
        }
    }

    [GameFact]
    public void EmptyGamesAreRemoved_AndTheListFollows()
    {
        var (h, alice, bob, carol) = Three(22, new SessionServerOptions { MaxGames = 2 });
        using (h)
        {
            alice.Create("alpha", 4, 2, 2);
            Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
            bob.Create("beta", 4, 5, 3);
            Assert.True(h.RunUntil(() => bob.InLobby && carol.List?.Games.Count == 2, 1_000_000));

            carol.JoinGame(alice.GameId);
            Assert.True(h.RunUntil(() => carol.InLobby && alice.Lobby?.Members.Count == 2, 1_000_000));

            alice.LeaveGame();
            Assert.True(h.RunUntil(() => carol.Lobby?.Members.Count == 1 && carol.InLobby, 1_000_000));
            Assert.Equal(2, h.Server.GameCount);

            carol.LeaveGame();
            Assert.True(h.RunUntil(() => h.Server.GameCount == 1 && h.Server.Games[0].Name == "beta", 1_000_000));
            Assert.True(h.RunUntil(() => carol.List?.Games.Count == 1, 1_000_000));

            bob.LeaveGame();
            Assert.True(h.RunUntil(() => h.Server.GameCount == 0, 1_000_000));
            Assert.True(h.RunUntil(() => carol.List?.Games.Count == 0, 1_000_000));
            Assert.True(h.Server.Stats.GamesClosed >= 2);
        }
    }

    [GameFact]
    public void IdleLobby_IsClosedWithReadableReasonAndFreesTheName()
    {
        using var h = new SessionHarness(23, new SessionServerOptions { MaxGames = 2, LobbyIdleTimeoutMicros = 1_000_000 });
        var alice = h.Join("alice");
        Assert.True(h.RunUntil(() => alice.List != null, 1_000_000));
        alice.Create("alpha", 4, 2, 2);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));

        Assert.True(h.RunUntil(() => alice.Disconnected, 3_000_000));
        Assert.Equal(NetRejectReason.RaceClosed, alice.Reject?.Reason);
        Assert.Equal(0, h.Server.GameCount);
        Assert.Equal(1, h.Server.Stats.GamesClosed);
        Assert.Equal(0, h.Server.ConnectionCount);

        var again = h.Join("alice");
        Assert.True(h.RunUntil(() => again.Connected && again.List != null, 1_000_000));
        Assert.Null(again.Reject);
    }

    [GameFact]
    public void FullGame_RefusesJoinWithoutChangingTheGame()
    {
        var (h, alice, bob, carol) = Three(24, new SessionServerOptions { MaxGames = 2 });
        using (h)
        {
            alice.Create("alpha", 2, 2, 2);
            Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
            bob.JoinGame(alice.GameId);
            Assert.True(h.RunUntil(() => bob.InLobby && carol.List?.Games.Single().Players == 2, 1_000_000));

            carol.JoinGame(alice.GameId);
            h.Run(100_000);
            Assert.Null(carol.Lobby);
            Assert.True(h.Server.Stats.RefusedJoins >= 1);
            Assert.Equal(2, alice.Lobby!.Members.Count);
            Assert.Equal(2, h.Server.Games.Single().MemberCount);
        }
    }
}

/// <summary>D9: the mods fingerprint in the hello (same set joins, missing or other version is rejected with id and version, no mods = as before).</summary>
public class ModsHandshakeTests
{
    static NetModSet Mods(params (string Id, string Version, byte Seed)[] m) =>
        new(m.Select(x => new NetModEntry(x.Id, x.Version, Enumerable.Repeat(x.Seed, 32).ToArray())));

    static byte[] Hello(NetModSet mods) =>
        (new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, SessionHarness.Fingerprint, "carol") { Mods = mods }).Encode();

    [Fact]
    public void Hello_RoundTripsTheModsAndRejectsAFingerprintThatDoesNotMatchTheList()
    {
        var mods = Mods(("indigo.duster", "1.0.0", 7), ("a.b", "2.1.0", 9));
        var back = NetHello.Decode(Hello(mods)).Mods;
        Assert.True(mods.SameAs(back));
        Assert.Equal(["a.b", "indigo.duster"], back.Entries.Select(e => e.Id));
        Assert.False(NetModSet.Empty.SameAs(mods));
        Assert.Equal(32, NetModSet.Empty.Fingerprint.Length);
        var bytes = Hello(mods);
        int at = bytes.Length - (32 + 1 + (1 + 3 + 1 + 5 + 32) + (1 + 13 + 1 + 5 + 32)); // first byte of the mods fingerprint
        bytes[at] ^= 0xFF;
        Assert.Throws<NetProtocolException>(() => NetHello.Decode(bytes));
    }

    [Fact]
    public void Diff_NamesIdAndVersionOnBothSides()
    {
        var server = Mods(("indigo.duster", "1.1.0", 1), ("only.server", "1.0.0", 2));
        var client = Mods(("indigo.duster", "1.0.0", 1), ("only.client", "3.0.0", 3));
        string onServer = server.Diff(client, "server", "client");
        Assert.Contains("indigo.duster 1.0.0 missing on server", onServer);
        Assert.Contains("only.client 3.0.0 missing on server", onServer);
        string onClient = client.Diff(server, "you", "the server");
        Assert.Contains("indigo.duster 1.1.0 missing on you", onClient);
        Assert.Contains("only.server 1.0.0 missing on you", onClient);
        Assert.Contains("only.client 3.0.0 not on the server", onClient);
        Assert.Equal("", server.Diff(server, "a", "b"));
    }

    [GameFact]
    public void ServerWithModsAdmitsTheSameSetAndRejectsMissingOrOtherVersionWithTheServerList()
    {
        var serverMods = Mods(("indigo.duster", "1.1.0", 5));
        using var h = new SessionHarness(10, new SessionServerOptions { Mods = serverMods });
        var same = h.Join("same", hello: Hello(serverMods));
        var none = h.Join("none", hello: Hello(NetModSet.Empty));
        var older = h.Join("older", hello: Hello(Mods(("indigo.duster", "1.0.0", 5))));
        Assert.True(h.RunUntil(() => same.Connected && none.Disconnected && older.Disconnected, 1_000_000));
        Assert.Null(same.Reject);
        foreach (var rejected in new[] { none, older })
        {
            Assert.Equal(NetRejectReason.ModsMismatch, rejected.Reject?.Reason);
            Assert.True(serverMods.SameAs(rejected.Reject!.ServerMods));
        }
        Assert.Contains("indigo.duster 1.1.0 missing", NetModSet.Empty.Diff(none.Reject!.ServerMods, "you", "the server"));
    }

    [GameFact]
    public void ServerWithoutModsRejectsAClientWithAMod_AndNoModsOnBothSidesBehavesAsBefore()
    {
        using var h = new SessionHarness(10);
        var plain = h.Join("plain");
        var modded = h.Join("modded", hello: Hello(Mods(("indigo.duster", "1.0.0", 5))));
        Assert.True(h.RunUntil(() => plain.Connected && modded.Disconnected, 1_000_000));
        Assert.Null(plain.Reject);
        Assert.Equal(NetRejectReason.ModsMismatch, modded.Reject?.Reason);
        Assert.Contains("indigo.duster 1.0.0 missing on you", NetModSet.Empty.Diff(Mods(("indigo.duster", "1.0.0", 5)), "you", "the server"));
    }
}
