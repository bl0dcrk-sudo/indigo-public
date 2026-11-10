using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Chat (protocol v11): broadcast inside one game, the sender is set by the server, text and rate limits, the server
/// console `msg`, and the session client's history.</summary>
public class ChatTests
{
    static SessionHarness Start(SessionServerOptions? options = null) =>
        new(7, options ?? new SessionServerOptions { CountdownMicros = 500_000, MaxRaceSteps = 200_000 });

    /// <summary>Two games: ana+bob in "one", cid alone in "two".</summary>
    static (SessionHarness H, SessionTestClient Ana, SessionTestClient Bob, SessionTestClient Cid) TwoGames(SessionServerOptions? options = null)
    {
        var h = Start(options);
        var ana = h.Join("ana");
        var cid = h.Join("cid");
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => ana.List != null && bob.List != null && cid.List != null, 1_000_000));
        ana.Create("one", 4, 1, 2);
        Assert.True(h.RunUntil(() => ana.Lobby != null, 1_000_000));
        cid.Create("two", 4, 1, 2);
        Assert.True(h.RunUntil(() => cid.Lobby != null && bob.List!.Games.Count == 2, 1_000_000));
        bob.JoinGame(ana.GameId);
        Assert.True(h.RunUntil(() => bob.Lobby != null, 1_000_000));
        return (h, ana, bob, cid);
    }

    [Fact]
    public void Message_ReachesEveryMemberOfTheSameGame_WithTheSenderSetByTheServer_AndNoOtherGame()
    {
        var (h, ana, bob, cid) = TwoGames();
        using var _ = h;
        ana.Chat("hello there");
        Assert.True(h.RunUntil(() => ana.Chats.Count == 1 && bob.Chats.Count == 1, 1_000_000));
        Assert.Equal("ana", bob.Chats[0].Sender);
        Assert.Equal("hello there", bob.Chats[0].Text);
        Assert.Equal("ana", ana.Chats[0].Sender);
        Assert.Equal(bob.Epoch, bob.Chats[0].Epoch);
        Assert.Equal(bob.GameId, bob.Chats[0].GameId);
        h.Run(200_000);
        Assert.Empty(cid.Chats);
    }

    [GameFact]
    public void Message_IsDeliveredDuringCountdownAndTheRace()
    {
        var (h, ana, bob, _) = TwoGames();
        using var _h = h;
        ana.SetReady(true);
        bob.SetReady(true);
        h.Run(100_000);
        ana.RequestStart();
        Assert.True(h.RunUntil(() => bob.InCountdown || bob.InRace, 2_000_000));
        bob.Chat("in the countdown");
        Assert.True(h.RunUntil(() => ana.Chats.Count == 1, 1_000_000));
        Assert.True(h.RunUntil(() => ana.InRace && bob.InRace, 5_000_000));
        ana.Chat("in the race");
        Assert.True(h.RunUntil(() => bob.Chats.Count == 2, 1_000_000));
        Assert.Equal("ana", bob.Chats[1].Sender);
    }

    [Fact]
    public void ClientCannotImpersonateAnotherPlayer_TheMessageCarriesNoSender()
    {
        var (h, ana, bob, _) = TwoGames();
        using var _h = h;
        bob.Chat("ana: I am ana");
        Assert.True(h.RunUntil(() => ana.Chats.Count == 1, 1_000_000));
        Assert.Equal("bob", ana.Chats[0].Sender);
    }

    [Fact]
    public void PlayerCannotTakeTheReservedServerName()
    {
        using var h = Start();
        var fake = h.Join("Server");
        Assert.True(h.RunUntil(() => fake.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, fake.Reject?.Reason);
    }

    [Fact]
    public void TooLongOrEmptyText_IsRefusedByTheEncoder_AndTheServerDropsAnOverlongDatagram()
    {
        var (h, ana, bob, _) = TwoGames();
        using var _h = h;
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetChatSend(1, 1, new string('x', NetProtocol.MaxChatLength + 1)).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetChatSend(1, 1, "").Encode());
        var w = new WireWriter(128);
        NetSessionProtocol.GameHeader(NetMessageType.ChatSend, ana.GameId, ana.Epoch).Write(w);
        w.U8(NetProtocol.MaxChatLength + 1);
        for (int i = 0; i <= NetProtocol.MaxChatLength; i++) w.U8('x');
        ana.Send(w.ToArray());
        h.Run(300_000);
        Assert.Equal(1, h.Server.Stats.Malformed);
        Assert.Empty(bob.Chats);
        ana.Chat(new string('y', NetProtocol.MaxChatLength));
        Assert.True(h.RunUntil(() => bob.Chats.Count == 1, 1_000_000));
        Assert.Equal(NetProtocol.MaxChatLength, bob.Chats[0].Text.Length);
    }

    [Fact]
    public void RateLimit_DropsTheExcess_PerSender_AndAllowsAgainAfterTheWindow()
    {
        var (h, ana, bob, _) = TwoGames(new SessionServerOptions { ChatMaxPerWindow = 3, ChatWindowMicros = 2_000_000 });
        using var _h = h;
        for (int i = 0; i < 6; i++) ana.Chat($"msg {i}");
        h.Run(500_000);
        Assert.Equal(3, bob.Chats.Count);
        Assert.Equal(3, h.Server.Stats.ChatRateLimited);
        bob.Chat("mine");
        Assert.True(h.RunUntil(() => ana.Chats.Any(c => c.Sender == "bob"), 1_000_000));
        h.Run(2_500_000);
        ana.Chat("later");
        Assert.True(h.RunUntil(() => bob.Chats.Any(c => c.Text == "later"), 1_000_000));
    }

    [Fact]
    public void ChatFromAStaleEpoch_IsNotBroadcast()
    {
        var (h, ana, bob, _) = TwoGames();
        using var _h = h;
        ana.Send(new NetChatSend(ana.GameId, ana.Epoch + 1, "forged epoch").Encode());
        h.Run(300_000);
        Assert.Empty(bob.Chats);
        Assert.Equal(1, h.Server.Stats.WrongSession);
    }

    [Fact]
    public void ConsoleMsg_SendsFromServerToEveryPlayerInEveryGame()
    {
        var (h, ana, bob, cid) = TwoGames();
        using var _h = h;
        var output = new List<string>();
        var lines = new Queue<string>(["msg Server restarts in 5 min"]);
        SessionServerLoop.ApplyConsole(h.Server, () => lines.Count > 0 ? lines.Dequeue() : null, output.Add);
        Assert.True(h.RunUntil(() => ana.Chats.Count == 1 && bob.Chats.Count == 1 && cid.Chats.Count == 1, 1_000_000));
        foreach (var c in new[] { ana, bob, cid })
        {
            Assert.Equal("SERVER", c.Chats[0].Sender);
            Assert.Equal("Server restarts in 5 min", c.Chats[0].Text);
        }
        Assert.Contains(output, l => l.Contains("3 player"));
    }

    [Fact]
    public void ConsoleMsg_WithoutOrWithInvalidText_PrintsUsageAndSendsNothing()
    {
        var (h, ana, _, _) = TwoGames();
        using var _h = h;
        var output = new List<string>();
        var lines = new Queue<string>(["msg", "msg   ", "msg " + new string('z', 81)]);
        SessionServerLoop.ApplyConsole(h.Server, () => lines.Count > 0 ? lines.Dequeue() : null, output.Add);
        h.Run(300_000);
        Assert.Empty(ana.Chats);
        Assert.Equal(3, output.Count(l => l.Contains("usage: msg")));
    }

    [Fact]
    public void SessionClient_KeepsTheLinesOfItsGame_SanitizesWhatItSends_AndClearsOnLeave()
    {
        var (h, ana, bob, _) = TwoGames();
        using var _h = h;
        var client = new SessionClient(SessionHarness.Fingerprint, "dan", (handler, hello) =>
        {
            var link = h.Net.AddClient(handler, new LinkProfile(), new LinkProfile());
            link.Connect(hello);
            return link;
        }, () => h.Net.Now);
        var got = new List<ChatLine>();
        client.ChatReceived += got.Add;
        client.Connect();
        Assert.True(h.RunUntil(() => { client.Update(h.Net.Now); return client.Phase == SessionClientPhase.Browsing; }, 1_000_000));
        client.JoinGame(ana.GameId);
        Assert.True(h.RunUntil(() => client.Phase == SessionClientPhase.InGame, 1_000_000));
        bob.Chat("first");
        bob.Chat("second");
        Assert.True(h.RunUntil(() => client.Chat.Count == 2, 1_000_000));
        Assert.Equal(["first", "second"], client.Chat.Select(c => c.Text));
        Assert.Equal(got.Count, client.Chat.Count);
        Assert.True(client.SendChat("  hi from dan éè "));
        Assert.True(h.RunUntil(() => bob.Chats.Any(c => c.Sender == "dan"), 1_000_000));
        Assert.Equal("hi from dan", bob.Chats.First(c => c.Sender == "dan").Text);
        Assert.False(client.SendChat("   "));
        client.LeaveGame();
        Assert.Empty(client.Chat);
        Assert.False(client.SendChat("nobody listens"));
    }

    [Fact]
    public void SessionClient_HistoryIsBounded()
    {
        var (h, ana, bob, _) = TwoGames(new SessionServerOptions { ChatMaxPerWindow = 1000 });
        using var _h = h;
        var client = new SessionClient(SessionHarness.Fingerprint, "dan", (handler, hello) =>
        {
            var link = h.Net.AddClient(handler, new LinkProfile(), new LinkProfile());
            link.Connect(hello);
            return link;
        }, () => h.Net.Now);
        client.Connect();
        Assert.True(h.RunUntil(() => { client.Update(h.Net.Now); return client.Phase == SessionClientPhase.Browsing; }, 1_000_000));
        client.JoinGame(ana.GameId);
        Assert.True(h.RunUntil(() => client.Phase == SessionClientPhase.InGame, 1_000_000));
        for (int i = 0; i < SessionClient.ChatHistoryLimit + 7; i++) bob.Chat($"line {i}");
        h.Run(500_000);
        Assert.Equal(SessionClient.ChatHistoryLimit, client.Chat.Count);
        Assert.Equal($"line {SessionClient.ChatHistoryLimit + 6}", client.Chat[^1].Text);
    }

    [Fact]
    public void SanitizeChat_DropsNonAsciiTrimsAndCuts()
    {
        Assert.Equal("", NetProtocol.SanitizeChat(null));
        Assert.Equal("", NetProtocol.SanitizeChat("  \t\n "));
        Assert.Equal("abc", NetProtocol.SanitizeChat(" a\u0001béc "));
        Assert.Equal(NetProtocol.MaxChatLength, NetProtocol.SanitizeChat(new string('q', 200)).Length);
    }

    [Fact]
    public void Messages_RoundTripThroughTheCodec()
    {
        var send = new NetChatSend(5, 9u, "hi there");
        var bytes = send.Encode();
        var r = new WireReader(bytes);
        Assert.Equal(send, NetChatSend.Decode(NetHeader.Read(ref r), ref r));
        var msg = new NetChatMessage(5, 9u, "SERVER", "hello");
        bytes = msg.Encode();
        r = new WireReader(bytes);
        Assert.Equal(msg, NetChatMessage.Decode(NetHeader.Read(ref r), ref r));
    }
}
