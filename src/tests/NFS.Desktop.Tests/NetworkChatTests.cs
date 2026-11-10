using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Desktop.Tests;

/// <summary>Chat of the network menu and race: the typed line, the history and the rule that typing never reaches the car or the menu.</summary>
public sealed class NetworkChatTests
{
    static DesktopInput Typed(string text, params MenuKey[] keys) =>
        new(default, keys, false, Typed: text.ToCharArray());

    static DesktopInput Keys(params MenuKey[] keys) => new(default, keys, false);

    static bool Apply(NetworkChat chat, DesktopInput input, List<string>? sent = null, bool canOpen = true) =>
        chat.Apply(input, canOpen, text => { sent?.Add(text); return true; });

    [Fact]
    public void TOpensTheLine_TheOpeningKeyIsNotTyped_EnterSendsAndCloses()
    {
        var chat = new NetworkChat();
        var sent = new List<string>();
        Assert.False(Apply(chat, Keys(), sent));
        Assert.False(chat.Typing);
        Assert.True(Apply(chat, Typed("t"), sent));
        Assert.True(chat.Typing);
        Assert.Equal("", chat.Text);
        Assert.True(Apply(chat, Typed("hi t"), sent));
        Assert.Equal("hi t", chat.Text);
        Assert.True(Apply(chat, Typed("\b!"), sent));
        Assert.Equal("hi !", chat.Text);
        Assert.True(Apply(chat, Keys(MenuKey.Enter), sent));
        Assert.Equal(["hi !"], sent);
        Assert.False(chat.Typing);
        Assert.Equal("", chat.Text);
    }

    [Fact]
    public void EscCancelsWithoutSending_AndEmptyLinesAreNotSent()
    {
        var chat = new NetworkChat();
        var sent = new List<string>();
        Apply(chat, Typed("T"), sent);
        Apply(chat, Typed("draft"), sent);
        Assert.True(Apply(chat, Keys(MenuKey.Escape), sent));
        Assert.False(chat.Typing);
        Apply(chat, Typed("t"), sent);
        Assert.True(Apply(chat, Keys(MenuKey.Enter), sent));
        Apply(chat, Typed("t"), sent);
        Apply(chat, Typed("   "), sent);
        Apply(chat, Keys(MenuKey.Enter), sent);
        Assert.Empty(sent);
    }

    [Fact]
    public void TheLineIsLimitedToPrintableAscii_AndTheProtocolMaximum()
    {
        var chat = new NetworkChat();
        Apply(chat, Typed("t"));
        Apply(chat, Typed(new string('a', 200) + "é\u0001"));
        Assert.Equal(NetProtocol.MaxChatLength, chat.Text.Length);
        Assert.All(chat.Text, c => Assert.Equal('a', c));
        chat.Close();
        Apply(chat, Typed("t"));
        Apply(chat, Typed("zoół"));
        Assert.Equal("zo", chat.Text);
    }

    [Fact]
    public void WhileTyping_EveryKeyIsTaken_SoNothingSteersTheCarOrReachesTheMenu()
    {
        var chat = new NetworkChat();
        Apply(chat, Typed("t"));
        var driving = new DesktopInput(new DriveKeys(Up: true, Down: false, Left: true, Right: false, GearUp: true, GearDown: false, Reset: true,
            Horn: true, Handbrake: true), [MenuKey.Up, MenuKey.Left], Pause: true, Typed: ['a', 'z']);
        Assert.True(Apply(chat, driving));
        Assert.True(chat.Typing);
        Assert.Equal("az", chat.Text);
        // The frame that ends the line is taken as well (Esc must not open the leave confirmation, Enter must not accept a row).
        Assert.True(Apply(chat, Keys(MenuKey.Escape)));
        Assert.False(chat.Typing);
        Assert.False(Apply(chat, driving));   // closed again: the game gets its keys back
    }

    [Fact]
    public void ALineCannotBeOpenedWhereChatIsNotAllowed()
    {
        var chat = new NetworkChat();
        Assert.False(Apply(chat, Typed("t"), canOpen: false));
        Assert.False(chat.Typing);
    }

    [Fact]
    public void Recent_ShowsTheLastFiveLines_InRaceOldOnesFade_AndAnotherGameStartsEmpty()
    {
        var chat = new NetworkChat();
        chat.Follow(3);
        for (int i = 0; i < 8; i++) chat.Add(new ChatLine("bob", $"m{i}"), i * 1_000_000);
        Assert.Equal(["m3", "m4", "m5", "m6", "m7"], chat.Recent(7_000_000).Select(l => l.Text));
        var fading = chat.Recent(7_000_000 + NetworkChat.RaceFadeMicros - 500_000, NetworkChat.RaceFadeMicros);
        Assert.Equal(["m7"], fading.Select(l => l.Text));
        Assert.Empty(chat.Recent(60_000_000, NetworkChat.RaceFadeMicros));
        Apply(chat, Typed("t"));
        Assert.Equal(5, chat.Recent(60_000_000, NetworkChat.RaceFadeMicros).Count);   // typing shows the whole tail
        chat.Follow(3);
        Assert.True(chat.Typing);                                                       // the same game keeps everything
        chat.Follow(4);
        Assert.Empty(chat.Recent(0));
        Assert.False(chat.Typing);
    }

    // ---------------- 15b: remembered nick ----------------

    [Fact]
    public void Nick_AndLastServer_AreSavedAndComeBackOnTheNextRun()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-nick-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            var first = PortSettings.Load(path);
            Assert.Equal(("Fallback", "127.0.0.1", "27100"), first.NetworkDefaults("Fallback", "127.0.0.1", 27100));
            first.RememberNetwork("Alice", "race.example.org", 40000);
            Assert.Null(first.Save(path));
            var second = PortSettings.Load(path);
            Assert.Equal(("Alice", "race.example.org", "40000"), second.NetworkDefaults("Fallback", "127.0.0.1", 27100));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OldPortJsonWithoutTheNetworkFields_LoadsWithDefaults_AndKeepsItsOtherKeys()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-old-").FullName;
        var path = Path.Combine(dir, PortSettings.FileName);
        try
        {
            File.WriteAllText(path, """{ "fullScreen": false, "future": 5 }""");
            var p = PortSettings.Load(path);
            Assert.Null(p.Error);
            Assert.Equal(("PLAYER1", "127.0.0.1", "27100"), p.NetworkDefaults("PLAYER1", "127.0.0.1", 27100));
            p.RememberNetwork("Bob", "10.0.0.5", 27100);
            Assert.Null(p.Save(path));
            var back = PortSettings.Load(path);
            Assert.False(back.FullScreen);
            Assert.True(back.Extra.ContainsKey("future"));
            Assert.Equal("Bob", back.NetNick);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RememberNetwork_IgnoresBlankAndOutOfRangeValues_KeepingThePreviousOnes()
    {
        var p = new PortSettings();
        p.RememberNetwork("Alice", "host", 27100);
        p.RememberNetwork("  ", "", 0);
        p.RememberNetwork("Alice2", "host2", 70000);
        Assert.Equal(("Alice2", "host2", "27100"), p.NetworkDefaults("x", "y", 1));
    }

    [Fact]
    public void ConnectingFromTheMenu_ReportsNickHostAndPortForSaving()
    {
        using var host = new NetworkMenuHost(null, () => -1, "Alice", "example.invalid", 28000,
            (_, _, _) => Task.FromException<System.Net.IPEndPoint>(new System.Net.Sockets.SocketException(11001)));
        var seen = new List<(string, string, int)>();
        host.Connecting += (n, h, p) => seen.Add((n, h, p));
        host.Menu.StartConnect();
        host.Pump();
        Assert.Equal([("Alice", "example.invalid", 28000)], seen);
    }

    // ---------------- lobby chat through the real host and server ----------------

    [GameDataFact]
    public async Task ChatInTheLobby_ReachesTheOtherPlayer_AndEscWhileTypingDoesNotLeaveTheGame()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = new UdpSessionServer(dir, new SessionServerOptions());
        using var a = new MenuPlayer(dir, "Alice", server.Port);
        using var b = new MenuPlayer(dir, "Bob", server.Port);
        a.Connect();
        Assert.True(await MenuPlayers.Until(() => a.Host.Session?.Session.Phase == SessionClientPhase.Browsing, 10_000, a), MenuPlayers.Why(a));
        Assert.True(a.Host.Session!.Session.CreateGame(new NetGameCreate("Chat", 4)));
        Assert.True(await MenuPlayers.Until(() => a.Menu.Screen == NetScreen.Lobby, 10_000, a), MenuPlayers.Why(a));
        b.Connect();
        Assert.True(await MenuPlayers.Until(() => b.Host.Session?.Session.Games.Count == 1, 10_000, a, b), MenuPlayers.Why(b));
        Assert.True(b.Host.Session!.Session.JoinGame(b.Host.Session.Session.Games[0].GameId));
        Assert.True(await MenuPlayers.Until(() => b.Menu.Screen == NetScreen.Lobby && a.Menu.Lobby?.Members.Count == 2, 10_000, a, b), MenuPlayers.Why(a, b));

        void Frame(MenuPlayer p, string typed, params MenuKey[] keys) =>
            p.Host.Frame(new DesktopInput(default, keys, false, Typed: typed.ToCharArray()), 0.016);

        Frame(a, "t");
        Assert.True(a.Host.Chat.Typing);
        Frame(a, "hello bob");
        Frame(a, "", MenuKey.Escape);                    // cancels the line, must not open the leave confirmation
        Assert.False(a.Host.Chat.Typing);
        Assert.Equal(NetScreen.Lobby, a.Menu.Screen);
        Frame(a, "t");
        Frame(a, "hello bob");
        Frame(a, "", MenuKey.Enter);
        Assert.Equal(NetScreen.Lobby, a.Menu.Screen);
        // the join of Bob is told to Alice by a SERVER line (never to Bob himself); the typed line is the only other one
        static IEnumerable<ChatLine> Typed(MenuPlayer p) => p.Host.Chat.Recent(0).Where(l => l.Sender != NetProtocol.ServerChatName);
        Assert.True(await MenuPlayers.Until(() => Typed(b).Count() == 1, 10_000, a, b), MenuPlayers.Why(a, b));
        Assert.Contains(a.Host.Chat.Recent(0), l => l.Sender == NetProtocol.ServerChatName && l.Text == "Bob joined");
        Assert.DoesNotContain(b.Host.Chat.Recent(0), l => l.Sender == NetProtocol.ServerChatName);
        var line = Typed(b).Single();
        Assert.Equal("Alice", line.Sender);
        Assert.Equal("hello bob", line.Text);
        Assert.True(await MenuPlayers.Until(() => Typed(a).Count() == 1, 10_000, a, b));   // the sender sees it too
    }

    [GameDataFact]
    public async Task ThePlayersThatComeAndGo_AreToldByServerLinesInTheOthersChat()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = new UdpSessionServer(dir, new SessionServerOptions());
        using var a = new MenuPlayer(dir, "Alice", server.Port);
        using var b = new MenuPlayer(dir, "Bob", server.Port);
        a.Connect();
        Assert.True(await MenuPlayers.Until(() => a.Host.Session?.Session.Phase == SessionClientPhase.Browsing, 10_000, a), MenuPlayers.Why(a));
        Assert.True(a.Host.Session!.Session.CreateGame(new NetGameCreate("Notices", 4)));
        Assert.True(await MenuPlayers.Until(() => a.Menu.Screen == NetScreen.Lobby, 10_000, a), MenuPlayers.Why(a));
        Assert.Empty(a.Host.Chat.Recent(0));                                  // the first state of the game (the own join) says nothing
        b.Connect();
        Assert.True(await MenuPlayers.Until(() => b.Host.Session?.Session.Games.Count == 1, 10_000, a, b), MenuPlayers.Why(b));
        Assert.True(b.Host.Session!.Session.JoinGame(b.Host.Session.Session.Games[0].GameId));
        Assert.True(await MenuPlayers.Until(() => a.Host.Chat.Recent(0).Any(l => l.Text == "Bob joined") && b.Menu.Screen == NetScreen.Lobby, 10_000, a, b), MenuPlayers.Why(a, b));
        Assert.Empty(b.Host.Chat.Recent(0));                                  // nothing about himself
        Assert.True(b.Host.Session.Session.LeaveGame());
        Assert.True(await MenuPlayers.Until(() => a.Host.Chat.Recent(0).Any(l => l.Text == "Bob left"), 10_000, a, b), MenuPlayers.Why(a, b));
        Assert.All(a.Host.Chat.Recent(0), l => Assert.Equal(NetProtocol.ServerChatName, l.Sender));
    }

    [GameDataFact]
    public async Task ChatLineOpenInTheRace_GivesTheCarNoKeys_ClosingReturnsThem_AndTheMessageReachesTheOtherPlayer()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = new UdpSessionServer(dir, new SessionServerOptions
        {
            CountdownMicros = 500_000, MaxRaceSteps = 256 + 3000, RaceLingerMicros = 2_000_000,
        });
        using var a = new MenuPlayer(dir, "Alice", server.Port);
        using var b = new MenuPlayer(dir, "Bob", server.Port);
        a.Connect();
        Assert.True(await MenuPlayers.Until(() => a.Host.Session?.Session.Phase == SessionClientPhase.Browsing, 10_000, a), MenuPlayers.Why(a));
        Assert.True(a.Host.Session!.Session.CreateGame(new NetGameCreate("RaceChat", 2)));
        Assert.True(await MenuPlayers.Until(() => a.Menu.Screen == NetScreen.Lobby, 10_000, a), MenuPlayers.Why(a));
        Assert.True(a.Host.Session.Session.SetOptions(0, 1, bots: 0));
        b.Connect();
        Assert.True(await MenuPlayers.Until(() => b.Host.Session?.Session.Games.Count == 1, 10_000, a, b), MenuPlayers.Why(b));
        Assert.True(b.Host.Session!.Session.JoinGame(b.Host.Session.Session.Games[0].GameId));
        Assert.True(await MenuPlayers.Until(() => b.Menu.Screen == NetScreen.Lobby && a.Menu.Lobby?.Members.Count == 2, 10_000, a, b), MenuPlayers.Why(a, b));
        Assert.True(a.Host.Session.Session.SetReady(true));
        Assert.True(b.Host.Session.Session.SetReady(true));
        Assert.True(await MenuPlayers.Until(() => a.Menu.Lobby!.Members.All(m => m.Ready), 10_000, a, b), MenuPlayers.Why(a, b));
        Assert.True(a.Host.Session.Session.StartRace());
        Assert.True(await MenuPlayers.Until(() => a.Host.Drive != null && b.Host.Drive != null, 30_000, a, b), MenuPlayers.Why(a, b));
        Assert.Equal(NetScreen.Race, a.Screen);

        var gas = new DriveKeys(true, false, false, false, false, false, false);
        int Gas(MenuPlayer p) => p.Host.Session!.Client!.Controls.Gas;
        void Frame(MenuPlayer p, DriveKeys keys, string typed = "", params MenuKey[] menu) =>
            p.Host.Frame(new DesktopInput(keys, menu, false, Typed: typed.Length == 0 ? null : typed.ToCharArray()), 0.016);

        // Held gas reaches the car while the line is closed.
        for (int i = 0; i < 5; i++) Frame(a, gas);
        Assert.True(Gas(a) > 0, "gas must reach the client controls before chatting");

        // T opens the line: from this frame on every held key is dropped on the way to the car, also while text is typed.
        Frame(a, gas, "t");
        Assert.True(a.Host.Chat.Typing);
        Assert.Equal(0, Gas(a));
        Frame(a, gas, "gg");
        Assert.Equal("gg", a.Host.Chat.Text);
        Assert.Equal(0, Gas(a));
        Frame(a, gas, "", MenuKey.Escape);               // Esc closes the line; it must not open the pause or the leave question
        Assert.False(a.Host.Chat.Typing);
        Assert.Equal(NetScreen.Race, a.Screen);
        Assert.False(a.Host.Drive!.RemotePaused);
        Frame(a, gas);
        Assert.True(Gas(a) > 0, "closing the line gives the keys back");

        // Open again and send: the line goes to Bob and the keys return after Enter.
        Frame(a, gas, "t");
        Assert.Equal(0, Gas(a));
        Frame(a, gas, "go go");
        Frame(a, gas, "", MenuKey.Enter);
        Assert.False(a.Host.Chat.Typing);
        Assert.Equal(NetScreen.Race, a.Screen);
        Assert.True(await MenuPlayers.Until(() => b.Host.Chat.Recent(0).Count == 1, 10_000, a, b), MenuPlayers.Why(a, b));
        var line = b.Host.Chat.Recent(0)[0];
        Assert.Equal("Alice", line.Sender);
        Assert.Equal("go go", line.Text);
        Frame(a, gas);
        Assert.True(Gas(a) > 0, "sending the line gives the keys back");
        Assert.Equal(0, Gas(b));   // Bob never pressed anything
    }
}
