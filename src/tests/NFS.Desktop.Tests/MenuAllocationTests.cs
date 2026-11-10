using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Task 17 (allocations): once warm, drawing a menu frame must not allocate much (it used to allocate ~14 kB per frame:
/// formatted strings, glyph lists, ...). Measured on the calling thread, like <see cref="AllocationTests"/>.</summary>
[Collection("Allocations")]
public class MenuAllocationTests(ITestOutputHelper output)
{
    /// <summary>Bytes per menu frame allowed once warm (was 14-21 kB; now 40-150 B).</summary>
    const long Limit = 1024;

    /// <summary>The network screens (layout list + backend) allocate nothing once warm; a little slack for the runtime.</summary>
    const long NetworkLimit = 64;

    static MenuKey[] Keys(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<MenuKey>).ToArray();

    [GameDataTheory]
    [InlineData("main", "")]
    [InlineData("setup", "Down Enter")]
    [InlineData("car select", "Down Down Enter")]
    [InlineData("screen 3", "Down Down Down Enter")]
    [InlineData("screen 5", "Down Down Down Down Down Enter")]
    [InlineData("screen 5 sub", "Down Down Down Down Down Enter Down Enter")]
    public void MenuFrame_AllocatesLittleWhenWarm(string name, string keys)
    {
        var menu = MenuController.Load();
        foreach (var k in Keys(keys)) menu.HandleKey(k);
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        for (int i = 0; i < 4; i++) scene.Render(fb);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 8;
        for (int i = 0; i < frames; i++) scene.Render(fb);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        output.WriteLine($"{name}: {bytes} B/frame");
        Assert.True(bytes < Limit, $"{name}: {bytes} B per frame");
    }

    [GameDataFact]
    public void CurrentScreen_IsStableUntilTheMenuChanges()
    {
        var menu = MenuController.Load();
        var first = menu.CurrentScreen;
        Assert.Same(first, menu.CurrentScreen);
        menu.HandleKey(MenuKey.Down);
        var moved = menu.CurrentScreen;
        Assert.NotSame(first, moved);
        Assert.Equal(menu.SelectedIndex, moved.Selected);
    }

    sealed class View : INetSessionView
    {
        public SessionClientPhase Phase { get; set; } = SessionClientPhase.Connecting;
        public string Name { get; set; } = "Me";
        public IReadOnlyList<NetGameInfo> Games { get; set; } = [];
        public NetLobbyState? Lobby { get; set; }
        public bool Pending { get; set; }
        public SessionError? LastError { get; set; }
        public NetReject? LastReject { get; set; }
        public int ResultsId { get; set; }
        public NFS.Game.Race.RaceResults? Results { get; set; }
        public bool RequestGames() => true;
        public bool CreateGame(NetGameCreate request) => true;
        public bool JoinGame(int gameId) => true;
        public bool LeaveGame() => true;
        public bool SetReady(bool ready) => true;
        public bool SetCar(int carType, int colour) => true;
        public bool SetOptions(int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style) => true;
        public bool StartRace() => true;
        public bool CancelStart() => true;
        public bool ReturnToLobby() => true;
        public void Disconnect() { Phase = SessionClientPhase.Closed; }
    }

    static NetworkMenuController Network(string screen, out View view)
    {
        var menu = new NetworkMenuController("Me", "127.0.0.1");
        view = new View();
        if (screen == "connect") return menu;
        for (int i = 0; i < (int)ConnectRow.Connect; i++) menu.Key(MenuKey.Down);
        menu.Key(MenuKey.Enter);
        menu.Attach(view);
        if (screen == "connecting") return menu;
        view.Games = [new NetGameInfo(1, "Race A", NetGamePhase.Lobby, 1, 4, 1, 3, NetGameMode.SingleRace), new NetGameInfo(2, "Race B", NetGamePhase.Race, 3, 4, 2, 3, NetGameMode.SingleRace)];
        view.Phase = SessionClientPhase.Browsing;
        menu.Update(0);
        if (screen == "games") return menu;
        var members = new List<NetLobbyMember> { new(0, "Me", true, 1, true, true), new(1, "Bob", false, 2, false, true), new(2, "Eve", true, 3, false, true) };
        view.Lobby = new NetLobbyState(7, 1, "Race", NetGamePhase.Lobby, 2, 3, 8, NetGameMode.SingleRace, 0, members);
        view.Phase = SessionClientPhase.InGame;
        menu.Update(0);
        return menu;
    }

    /// <summary>A lobby frame with the full chat (and the typing line): the game's own limit of the network screens.</summary>
    [GameDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NetworkLobbyFrameWithChat_AllocatesLittleWhenWarm(bool typing)
    {
        var menu = Network("lobby", out _);
        var chat = new NetworkChat();
        for (int i = 0; i < NetworkChat.Visible; i++) chat.Add(new ChatLine(i == 2 ? NetProtocol.ServerChatName : "Bob", $"line {i}"), 0);
        if (typing) chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
        var fb = new FrameBuffer(640, 480);
        for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, menu, i, true, chat);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 8;
        for (int i = 0; i < frames; i++) NetworkMenuRenderer.Draw(fb, menu, 4 + i, true, chat);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        output.WriteLine($"net lobby chat typing={typing}: {bytes} B/frame");
        Assert.True(bytes < NetworkLimit, $"net lobby chat: {bytes} B per frame");
    }

    [GameDataTheory]
    [InlineData("connect")]
    [InlineData("connecting")]
    [InlineData("games")]
    [InlineData("lobby")]
    public void NetworkMenuFrame_AllocatesLittleWhenWarm(string screen)
    {
        var menu = Network(screen, out _);
        var fb = new FrameBuffer(640, 480);
        for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, menu, i, false);
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 8;
        for (int i = 0; i < frames; i++) NetworkMenuRenderer.Draw(fb, menu, 4 + i, false);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        output.WriteLine($"net {screen} {menu.Screen}: {bytes} B/frame");
        Assert.True(bytes < NetworkLimit, $"net {screen}: {bytes} B per frame");
    }
}
