using System.Net;
using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Net.Protocol;

namespace NFS.Desktop.Tests;

public sealed class NetworkLobbyLifecycleTests
{
    [Fact]
    public void BackOnTheConnectScreenLeavesTheNetworkMenuWithoutAnySession()
    {
        using var host = new NetworkMenuHost(null, () => -1, "Alice");
        host.Frame(new DesktopInput(default, [MenuKey.Escape], false), 0.016);
        Assert.True(host.ExitRequested);
        Assert.Null(host.Session);
        Assert.Null(host.Drive);
    }

    [Fact]
    public void ConnectScreenFieldsAreEditedByTypingAndConnectFailuresKeepThem()
    {
        using var host = new NetworkMenuHost(null, () => -1, "Alice", "10.0.0.1", 27100,
            (_, _, _) => Task.FromException<IPEndPoint>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        void Frame(MenuKey[] keys, char[]? typed = null) => host.Frame(new DesktopInput(default, keys, false, Typed: typed), 0.016);
        Frame([MenuKey.Down]);                                  // host row
        Frame([MenuKey.Enter]);
        Frame([], [(char)8, (char)8, 'x']);                     // backspace twice, then a letter
        Frame([MenuKey.Enter]);
        Assert.Equal("10.0.0x", host.Menu.Host);
        host.Menu.StartConnect();
        host.Pump();                                            // the injected lookup fails
        host.Pump();
        Assert.Equal(NetScreen.Connect, host.Menu.Screen);
        Assert.Equal("10.0.0x", host.Menu.Host);
        Assert.True(host.Menu.MessageIsError);
        Assert.Null(host.Session);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 27100)]
    [InlineData("192.168.1.20:5000", "192.168.1.20", 5000)]
    [InlineData("nfs.example.org:27101", "nfs.example.org", 27101)]
    [InlineData("nfs.example.org:notaport", "nfs.example.org:notaport", 27100)]
    [InlineData("::1", "::1", 27100)]
    public void ServerOptionFillsHostAndPortOfTheConnectScreen(string option, string host, int port) =>
        Assert.Equal((host, port), NetworkMenuHost.SplitServer(option));

    [GameDataFact]
    public async Task BackspaceCancelsAPendingConnectionAndFreesTheSession()
    {
        string dir = GameFiles.FindGameDir(null);
        using var silent = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));   // bound, never answers
        using var host = new NetworkMenuHost(dir, () => -1, "Alice", "127.0.0.1", ((IPEndPoint)silent.Client.LocalEndPoint!).Port);
        host.Menu.StartConnect();
        host.Pump();
        Assert.True(await Until(() => { host.Pump(); return host.Session != null; }, 5_000));
        Assert.Equal(NetScreen.Connecting, host.Menu.Screen);
        host.Frame(new DesktopInput(default, [], false, Typed: [(char)8]), 0.016);
        Assert.Equal(NetScreen.Connect, host.Menu.Screen);
        host.Pump();
        Assert.Null(host.Session);
    }

    [GameDataFact]
    public async Task AnUnansweredHandshakeEndsOnTheConnectScreenWithAReasonInsteadOfHanging()
    {
        string dir = GameFiles.FindGameDir(null);
        using var silent = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));   // bound, never answers
        using var host = new NetworkMenuHost(dir, () => -1, "Alice", "127.0.0.1", ((IPEndPoint)silent.Client.LocalEndPoint!).Port)
        { HandshakeTimeoutMicros = 1_500_000 };
        host.Menu.StartConnect();
        Assert.True(await Until(() => { host.Pump(); return host.Menu.Screen == NetScreen.Connect; }, 20_000), host.Menu.Screen.ToString());
        Assert.True(host.Menu.MessageIsError);
        Assert.False(string.IsNullOrEmpty(host.Menu.Message));
        Assert.Null(host.Session);
    }

    [Fact]
    public void RosterRoundTripsSeatNames()
    {
        var source = new NetRoster(31, 7, new Dictionary<int, string> { [0] = "Alice", [1] = "Bob" }, [0]);
        var reader = new WireReader(source.Encode());
        var header = NetHeader.Read(ref reader);
        var decoded = NetRoster.Decode(header, ref reader);
        Assert.Equal(source.Players, decoded.Players);
        Assert.Equal(source.RaceId, decoded.RaceId);
        Assert.Equal(source.Epoch, decoded.Epoch);
        Assert.Equal(source.ReadySeats, decoded.ReadySeats);
    }

    [Fact]
    public void TypedInputIncludesColonForHostPortAddresses()
    {
        var mapping = new InputMapping();
        var input = mapping.Read(new Microsoft.Xna.Framework.Input.KeyboardState(Microsoft.Xna.Framework.Input.Keys.LeftShift,
            Microsoft.Xna.Framework.Input.Keys.OemSemicolon), default);
        Assert.Contains(':', input.Typed!);
    }

    [Fact]
    public void SharedRaceInputOffersPauseResumeLeaveAndReturnsAfterResults()
    {
        int selected = 0;
        Assert.Equal(NetworkRaceAction.Pause, NetworkRaceInput.Apply(new DesktopInput(default, [], true), false, false, ref selected));
        Assert.Equal(NetworkRaceAction.Resume, NetworkRaceInput.Apply(new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Escape], false), true, false, ref selected));
        NetworkRaceInput.Apply(new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Down], false), true, false, ref selected);
        Assert.Equal(NetworkRaceAction.Leave, NetworkRaceInput.Apply(new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), true, false, ref selected));
        Assert.Equal(NetworkRaceAction.Leave, NetworkRaceInput.Apply(new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), false, true, ref selected));
    }

    [Fact]
    public void NetworkPause_AcceptsHorizontalAndVerticalKeysForTheTwoEntries()
    {
        int selected = 0;
        var left = new[] { NFS.Game.FrontEnd.MenuKey.Up, NFS.Game.FrontEnd.MenuKey.Left };
        var right = new[] { NFS.Game.FrontEnd.MenuKey.Down, NFS.Game.FrontEnd.MenuKey.Right };
        for (int i = 0; i < 2; i++)
        {
            NetworkRaceInput.Apply(new DesktopInput(default, [right[i]], false), true, false, ref selected);
            Assert.Equal(1, selected);
            Assert.Equal(NetworkRaceAction.Leave, NetworkRaceInput.Apply(new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), true, false, ref selected));
            NetworkRaceInput.Apply(new DesktopInput(default, [left[i]], false), true, false, ref selected);
            Assert.Equal(0, selected);
            Assert.Equal(NetworkRaceAction.Resume, NetworkRaceInput.Apply(new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), true, false, ref selected));
        }
    }

    [GameDataFact]
    public void GameMenuNetworkTypeAndRaceActionOpensTheActualLobbyEntry()
    {
        var menu = MenuController.Load(GameFiles.FindGameDir(null));
        menu.Config.GameType = 2;
        while (menu.CurrentScreen.Items[menu.SelectedIndex].Target != "race") menu.HandleKey(NFS.Game.FrontEnd.MenuKey.Down);
        Assert.True(NetworkGameMenu.ShouldOpen(menu, new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), false, false));
        menu.Config.GameType = 1;
        Assert.False(NetworkGameMenu.ShouldOpen(menu, new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), false, false));
    }

    [GameDataFact]
    public void GameMenuNetworkTypeAndRaceActionOpensTheNetworkMenu()
    {
        var menu = MenuController.Load(GameFiles.FindGameDir(null));
        menu.Config.GameType = 2;
        while (menu.CurrentScreen.Items[menu.SelectedIndex].Target != "race") menu.HandleKey(NFS.Game.FrontEnd.MenuKey.Down);
        Assert.True(NetworkGameMenu.ShouldOpen(menu, new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), false, false));
        menu.Config.GameType = 1;
        Assert.False(NetworkGameMenu.ShouldOpen(menu, new DesktopInput(default, [NFS.Game.FrontEnd.MenuKey.Enter], false), false, false));
    }

    static async Task<bool> Until(Func<bool> condition, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(1);
        }
        return condition();
    }
}
