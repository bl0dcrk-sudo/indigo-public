using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Track direction of a network game: the lobby options, the games list and the lobby state carry it, a game switched by its operator
/// backward starts its race backward, and the games list and lobby state report it.</summary>
public class SessionDirectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionRoundTripsThroughTheOptionsTheGameInfoAndTheLobbyState(bool backward)
    {
        const uint epoch = 5;
        var options = new NetLobbyOptions(7, epoch, 3, 2, backward);
        var ob = options.Encode();
        var or = new WireReader(ob);
        Assert.Equal(options, NetLobbyOptions.Decode(NetHeader.Read(ref or), ref or));

        var info = new NetGameInfo(7, "Game", NetGamePhase.Lobby, 1, 4, 3, 2, NetGameMode.SingleRace, backward);
        var list = new NetGameList([info]);
        var lb = list.Encode();
        var lr = new WireReader(lb);
        Assert.Equal(backward, NetGameList.Decode(NetHeader.Read(ref lr), ref lr).Games.Single().Backward);

        var state = new NetLobbyState(7, epoch, "Game", NetGamePhase.Lobby, 3, 2, 4, NetGameMode.SingleRace, 0,
            [new NetLobbyMember(0, "alice", false, NetProtocol.NoCarType, true, true)], null, backward);
        var sb = state.Encode();
        var sr = new WireReader(sb);
        var back = NetLobbyState.Decode(NetHeader.Read(ref sr), ref sr);
        Assert.Equal(backward, back.Backward);
        Assert.Equal((state.Track, state.Laps), (back.Track, back.Laps));
    }

    [Fact]
    public void DirectionByteOtherThanZeroOrOneIsMalformed()
    {
        var bytes = new NetLobbyOptions(7, 5, 3, 2, true).Encode();
        bytes[NetHeader.Size + 2] = 2; // track, laps, then the direction byte
        Assert.Throws<NetProtocolException>(() =>
        {
            var r = new WireReader(bytes);
            NetLobbyOptions.Decode(NetHeader.Read(ref r), ref r);
        });
    }

    static SessionHarness Lobby(int seed, bool backward, out SessionTestClient alice)
    {
        var h = new SessionHarness(seed, new SessionServerOptions { MaxRaceSteps = 30 });
        alice = h.Join("alice");
        var a = alice;
        Assert.True(h.RunUntil(() => a.List != null, 1_000_000));
        a.Create("game", 2, 1, 2, backward: backward);   // the operator then sets the options of the test in the lobby
        Assert.True(h.RunUntil(() => a.InLobby && a.Lobby!.Track == 1 && a.Lobby.Backward == backward, 1_000_000));
        return h;
    }

    [GameTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreatedGameStartsItsRaceInTheChosenDirection(bool backward)
    {
        using var h = Lobby(70, backward, out var alice);
        Assert.Equal(backward, alice.Lobby!.Backward);
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => bob.List != null, 1_000_000));
        Assert.Equal(backward, bob.List!.Games.Single().Backward);

        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Server.Games.Single().Round != null, 8_000_000));
        var game = h.Server.Games.Single();
        Assert.Equal(backward, game.Backward);
        Assert.Equal(backward, game.Round!.Host.Runner.Session.Backward);
    }

    [GameFact]
    public void OperatorSwitchingTheDirectionIsReflectedInLobbyListAndTheStartedRace()
    {
        using var h = Lobby(71, false, out var alice);
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => bob.List != null, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby && alice.Lobby!.Members.Count == 2, 1_000_000));
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => carol.List != null, 1_000_000));
        Assert.False(bob.Lobby!.Backward);

        bob.Send(new NetLobbyOptions(bob.GameId, bob.Epoch, 1, 2, true).Encode()); // not the operator: refused
        h.Run(200_000);
        Assert.False(alice.Lobby!.Backward);

        alice.Send(new NetLobbyOptions(alice.GameId, alice.Epoch, 1, 2, true).Encode());
        Assert.True(h.RunUntil(() => alice.Lobby!.Backward && bob.Lobby!.Backward, 1_000_000));
        Assert.True(h.RunUntil(() => carol.List?.Games.SingleOrDefault()?.Backward == true, 1_000_000));

        alice.Send(new NetLobbyOptions(alice.GameId, alice.Epoch, 1, 2, false).Encode());
        Assert.True(h.RunUntil(() => !bob.Lobby!.Backward && carol.List?.Games.Single().Backward == false, 1_000_000));

        alice.Send(new NetLobbyOptions(alice.GameId, alice.Epoch, 1, 2, true).Encode());
        Assert.True(h.RunUntil(() => bob.Lobby!.Backward, 1_000_000));
        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Server.Games.Single().Round != null, 8_000_000));
        Assert.True(h.Server.Games.Single().Round!.Host.Runner.Session.Backward);
    }
}
