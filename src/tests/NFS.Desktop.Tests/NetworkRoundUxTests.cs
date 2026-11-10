using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop.Tests;

/// <summary>Round 2026-10-21 of change network-menu-look (design D12): the SERVER chat lines worked out from two lobby states and
/// the saved-settings flag of the Connect screen. The screens of the round are tested with the controller and the layout.</summary>
public sealed class NetworkRoundUxTests
{
    static NetLobbyMember Member(int slot, string name, bool connected = true, bool op = false) =>
        new(slot, name, false, 0, op, connected, NetProtocol.NoColour);

    static NetLobbyState Lobby(int game, params NetLobbyMember[] members) =>
        new(game, 1, "Race", NetGamePhase.Lobby, 2, 3, 4, NetGameMode.SingleRace, 0, members);

    [Fact]
    public void Notices_TellWhoJoinedLeftLostTheLinkAndCameBack()
    {
        var before = Lobby(1, Member(0, "Me", op: true), Member(1, "Bob"), Member(2, "Carol"));
        Assert.Equal(["Dave joined"], NetworkLobbyNotices.Diff(before, Lobby(1, Member(0, "Me", op: true), Member(1, "Bob"), Member(2, "Carol"), Member(3, "Dave")), "Me"));
        Assert.Equal(["Bob left"], NetworkLobbyNotices.Diff(before, Lobby(1, Member(0, "Me", op: true), Member(2, "Carol")), "Me"));
        Assert.Equal(["Carol lost connection"],
            NetworkLobbyNotices.Diff(before, Lobby(1, Member(0, "Me", op: true), Member(1, "Bob"), Member(2, "Carol", connected: false)), "Me"));
        var lost = Lobby(1, Member(0, "Me", op: true), Member(1, "Bob", connected: false), Member(2, "Carol"));
        Assert.Equal(["Bob is back"], NetworkLobbyNotices.Diff(lost, before, "Me"));
    }

    [Fact]
    public void Notices_SayNothingAboutThePlayerItselfOrWithoutAPreviousStateOfTheSameGame()
    {
        var alone = Lobby(1, Member(0, "Me", op: true));
        var two = Lobby(1, Member(0, "Op", op: true), Member(1, "ME"));
        Assert.Empty(NetworkLobbyNotices.Diff(null, two, "Me"));                                // the first state of a game
        Assert.Empty(NetworkLobbyNotices.Diff(alone, null, "Me"));
        Assert.Empty(NetworkLobbyNotices.Diff(Lobby(2, Member(0, "Op", op: true)), two, "Me"));   // another game
        Assert.Equal(["Op joined"], NetworkLobbyNotices.Diff(Lobby(1, Member(1, "ME")), two, "me"));   // names compare ignoring case: "ME" is the player itself
        Assert.Empty(NetworkLobbyNotices.Diff(two, two, "Me"));                                 // nothing changed
        Assert.Empty(NetworkLobbyNotices.Diff(two, two with { Laps = 5 }, "Me"));               // a changed option is not a roster change
    }

    [Fact]
    public void Notices_SeveralChangesComeOutInTheOrderOfTheRoster()
    {
        var a = Lobby(1, Member(0, "Me", op: true), Member(1, "Bob"), Member(2, "Carol"));
        var b = Lobby(1, Member(0, "Me", op: true), Member(2, "Carol", connected: false), Member(3, "Dave"));
        Assert.Equal(["Carol lost connection", "Dave joined", "Bob left"], NetworkLobbyNotices.Diff(a, b, "Me"));
    }

    [Fact]
    public void PortSettings_RememberTheNetworkOnlyAfterAnEarlierConnect()
    {
        var fresh = new PortSettings();
        Assert.False(fresh.HasSavedNetwork);
        fresh.RememberNetwork("Alice", "race.example.org", 27100);
        Assert.True(fresh.HasSavedNetwork);
        var halfway = new PortSettings { NetNick = "Alice" };
        Assert.False(halfway.HasSavedNetwork);                                                  // a nick without a server is not "set up"
    }
}
