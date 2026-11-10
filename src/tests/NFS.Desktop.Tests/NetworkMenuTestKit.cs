using System.Diagnostics;
using System.Net;
using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;

namespace NFS.Desktop.Tests;

/// <summary>A real <see cref="SessionServer"/> on a loopback UDP port with its own loop thread (what a dedicated session server
/// process runs), for windowless tests of the network menu host.</summary>
internal sealed class UdpSessionServer : IDisposable
{
    readonly LiteNetServerHost _net = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    public SessionServer Server { get; }
    public int Port { get; }

    public UdpSessionServer(string dir, SessionServerOptions options)
    {
        var clock = Stopwatch.StartNew();
        Server = new SessionServer(_net, dir, SimDataFingerprint.Compute(dir), 0, options)
        { Clock = () => (long)clock.Elapsed.TotalMicroseconds };
        _net.Handler = Server;
        _net.Start(IPAddress.Loopback, 0);
        Port = _net.Port;
        _loop = Task.Run(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                _net.Poll();
                Server.Update((long)clock.Elapsed.TotalMicroseconds);
                Thread.Sleep(1);
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _loop.Wait(TimeSpan.FromSeconds(5));
        Server.Dispose();
        _net.Dispose();
    }
}

/// <summary>A player of the network menu in a test: a <see cref="NetworkMenuHost"/> driven only by menu keys and typed characters,
/// exactly as <c>Game1</c> feeds it.</summary>
internal sealed class MenuPlayer(string dir, string nick, int port, int car = -1,
    Func<string, int, CancellationToken, Task<IPEndPoint>>? resolve = null) : IDisposable
{
    public NetworkMenuHost Host { get; } = new(dir, () => car, nick, "127.0.0.1", port, resolve);
    public NetworkMenuController Menu => Host.Menu;
    public NetScreen Screen => Menu.Screen;

    public void Step() => Host.Frame(new DesktopInput(default, [], false), 0.016);
    public void Press(params MenuKey[] keys) { foreach (var k in keys) Host.Frame(new DesktopInput(default, [k], false), 0.016); }
    public void Pause() => Host.Frame(new DesktopInput(default, [], true), 0.016);

    /// <summary>Moves the cursor down (wrapping) until <paramref name="at"/> holds.</summary>
    public void MoveTo(Func<bool> at)
    {
        for (int i = 0; i < 40 && !at(); i++) Press(MenuKey.Down);
        Assert.True(at(), $"cursor never reached the row ({Screen}, selected {Menu.Selected})");
    }

    public void MoveToConnect(ConnectRow row) => MoveTo(() => Menu.Selected == (int)row);
    public void MoveToGames(GamesRow row, string? game = null) =>
        MoveTo(() => Menu.GamesRowAt(Menu.Selected, out int gi) == row && (game == null || gi >= 0 && Menu.Games[gi].Name == game));
    public void MoveToCreate(CreateRow row) => MoveTo(() => NetworkMenuController.CreateRows[Menu.Selected] == row);
    /// <summary>A setting through the settings row (ENTER, then DOWN); an action, the car or the settings row on the top level (ESC out
    /// of the settings first, then up/down the action list).</summary>
    public void MoveToLobby(LobbyRow row)
    {
        bool at() => Menu.LobbyRows[Menu.Selected] == row;
        if (NetworkMenuController.IsSetting(row))
        {
            if (!Menu.InSettings)
            {
                for (int i = 0; i < 5 && Menu.LobbyRows[Menu.Selected] != LobbyRow.Settings; i++) Press(MenuKey.Up);
                Press(MenuKey.Enter);
            }
            MoveTo(at);
            return;
        }
        if (Menu.InSettings) Press(MenuKey.Escape);
        if (row == LobbyRow.Settings) { for (int i = 0; i < 6 && !at(); i++) Press(MenuKey.Up); }
        else
        {
            int target = Menu.LobbyRows.ToList().IndexOf(row);
            for (int i = 0; i < 6 && Menu.Selected != target; i++) Press(Menu.Selected < target ? MenuKey.Down : MenuKey.Up);
        }
        Assert.True(at(), $"cursor never reached the row ({Screen}, selected {Menu.Selected})");
    }
    public void MoveToSummary(SummaryRow row) => MoveTo(() => (SummaryRow)Menu.Selected == row);

    public void Connect()
    {
        MoveToConnect(ConnectRow.Connect);
        Press(MenuKey.Enter);
    }

    public void Dispose() => Host.Dispose();

    public string Describe() =>
        $"{Menu.Screen} msg='{Menu.Message}' phase={Host.Session?.Session.Phase} lobby={Host.Session?.Session.Lobby?.Phase} " +
        $"race={Host.Session?.Client?.State} drive={(Host.Drive != null)} err={Host.Session?.Error}";
}

internal static class MenuPlayers
{
    public static async Task<bool> Until(Func<bool> condition, int timeoutMs, params MenuPlayer[] pump)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            foreach (var p in pump) p.Step();
            if (condition()) return true;
            await Task.Delay(1);
        }
        return condition();
    }

    public static string Why(params MenuPlayer[] players) => string.Join("; ", players.Select(p => p.Describe()));
}
