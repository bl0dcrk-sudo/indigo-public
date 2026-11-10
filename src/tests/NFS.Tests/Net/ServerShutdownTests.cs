using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Graceful stop (operator `quit`, Ctrl+C, EOF): a running race gets its ServerStop result delivered before the
/// connection closes, no physics runs after the stop, joining is refused, and a lobby close has no result. All over the
/// deterministic in-memory transport.</summary>
public class ServerShutdownTests
{
    const int StartStep = 256;

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    /// <summary>Wraps the real client and records whether a result reached it before the transport disconnect.</summary>
    sealed class ResultOrderProbe(INetClientHandler inner) : INetClientHandler
    {
        public bool ResultsSeen, Disconnected, ResultsBeforeDisconnect;
        public void OnConnected() => inner.OnConnected();
        public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data)
        {
            if (NetHeader.Peek(data) == NetMessageType.Results) ResultsSeen = true;
            inner.OnReceive(channel, data);
        }
        public void OnDisconnected(byte[]? reason)
        {
            Disconnected = true;
            ResultsBeforeDisconnect = ResultsSeen;
            inner.OnDisconnected(reason);
        }
    }

    [GameFact]
    public void ShutdownDuringRace_DeliversTheServerStopResultBeforeClosingAndFreezesTheHost()
    {
        var race = new ServerRaceConfig();
        using var h = new NetHarness(9301,
            new RaceServerOptions { ManualStart = true, WaitTimeoutMicros = 60_000_000, LingerMicros = 0, BotCount = race.BotCount },
            race);

        // Alice is observed through a probe; Bob is a plain harness client.
        var alice = new RaceClient(null, NetHarness.Fingerprint, "alice") { Clock = () => h.Net.Now };
        var probe = new ResultOrderProbe(alice);
        var link = h.Net.AddClient(probe, LinkProfile.Rtt(20), LinkProfile.Rtt(20));
        alice.Transport = link;
        h.Samples.Add([]);
        h.Received.Add([]);
        h.Taken.Add([]);
        h.Clients.Add((alice, link));
        link.Connect(alice.Hello());
        var bob = h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Racing && h.Server.Host.Steps > StartStep + 20,
            30_000_000), Tail(h));
        Assert.True(h.RunUntil(() => alice.Latest?.Steps > 5 && bob.Latest?.Steps > 5, 2_000_000), Tail(h));

        long steps = h.Server.Host.Steps;
        h.Server.Shutdown();
        Assert.True(h.Server.Stopping); // draining, not closed yet

        // A new join is refused while the last reliable messages drain.
        byte[]? reject = h.Server.OnConnectRequest(new RaceClient(null, NetHarness.Fingerprint, "late").Hello());
        Assert.NotNull(reject);
        Assert.Equal(NetRejectReason.ServerShutdown, NetReject.TryDecode(reject!)!.Reason);

        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Closed, 5_000_000), Tail(h));
        h.Run(200_000); // deliver the transport disconnect that follows the drained result
        // no physics ran after the stop (the drain only waits for acknowledgements)
        Assert.Equal(steps, h.Server.Host.Steps);
        Assert.Equal(NetFinishReason.ServerStop, h.Server.FinishReason);
        Assert.Equal(NetFinishReason.ServerStop, alice.Results!.Reason);
        Assert.Equal(NetFinishReason.ServerStop, bob.Results!.Reason);
        Assert.True(probe.ResultsBeforeDisconnect, "the result must reach the client before the connection closes");
        Assert.True(alice.Disconnected);
        Assert.Equal(NetRejectReason.ServerShutdown, alice.RejectReason!.Reason);
        Assert.Equal(NetRacePhase.Closed, h.Server.Phase);
        Assert.False(h.Server.Stopping);
    }

    [GameFact]
    public void ShutdownInTheLobby_ClosesWithoutARaceResult()
    {
        var race = new ServerRaceConfig();
        using var h = new NetHarness(9302,
            new RaceServerOptions { ManualStart = true, WaitTimeoutMicros = 60_000_000, LingerMicros = 0, BotCount = race.BotCount },
            race);
        var alice = h.Join("alice", LinkProfile.Rtt(20));
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Ready, 5_000_000), Tail(h));

        h.Server.Shutdown();
        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Closed, 5_000_000), Tail(h));
        h.Run(100_000); // deliver the transport disconnect
        Assert.Null(h.Server.FinishReason);
        Assert.Null(alice.Results);
        Assert.True(alice.Disconnected, string.Join("\n", h.Log.TakeLast(10)));
        Assert.Equal(NetRejectReason.ServerShutdown, alice.RejectReason!.Reason);
        Assert.Equal(0, h.Server.Host.Steps);
    }

    [GameFact]
    public void GracefulStopOverRealLoopback_DeliversTheResultBeforeTheTransportCloses()
    {
        // Real UDP sockets (LiteNetLib) and the real server loop in its own thread; the operator commands travel through
        // ServerInteraction like the console. The LiteNetLib queue count does not cover unacknowledged in-flight packets,
        // so this also exercises the client-side result acknowledgement that gates the close.
        var race = new ServerRaceConfig(Players: 1) { MinimumPlayers = 1 };
        var host = race.CreateHost(GameData.Dir, 0x5EED4242);
        var net = new LiteNetServerHost();
        var clock = Stopwatch.StartNew();
        var server = new RaceServer(host, NetHarness.Fingerprint, net, ServerLoop.Micros(clock),
            new RaceServerOptions { ManualStart = true, WaitTimeoutMicros = long.MaxValue, BotCount = race.BotCount },
            null, 4242) { Clock = () => ServerLoop.Micros(clock) };
        net.Handler = server;
        net.Start(IPAddress.Loopback, 0);

        var client = new RaceClient(null, NetHarness.Fingerprint, "alice") { Clock = () => ServerLoop.Micros(clock) };
        var probe = new ResultOrderProbe(client);
        var clientNet = new LiteNetClientHost(probe);
        client.Transport = clientNet;
        clientNet.Connect(new IPEndPoint(IPAddress.Loopback, net.Port), client.Hello());

        var lines = new ConcurrentQueue<string>();
        var interaction = new ServerInteraction
        {
            ManualStart = true,
            NextLine = () => lines.TryDequeue(out string? line) ? line : null,
        };
        var cancel = new CancellationTokenSource();
        int exit = -1;
        var thread = new Thread(() => exit = ServerLoop.Run(server, net, clock, null, _ => { }, cancel.Token, interaction))
            { IsBackground = true, Name = "loopback-server" };
        thread.Start();

        bool Pump(Func<bool> done, int millis)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < millis)
            {
                clientNet.Poll();
                client.Update(ServerLoop.Micros(clock));
                if (done()) return true;
                Thread.Sleep(5);
            }
            return done();
        }

        try
        {
            Assert.True(Pump(() => client.State == RaceClientState.Ready, 5_000), "client did not become ready");
            // The READY message needs one more uplink; repeat `start` until the operator command is accepted.
            long lastStart = 0;
            Assert.True(Pump(() =>
            {
                long now = ServerLoop.Micros(clock);
                if (client.Latest is not { RaceStarted: true } && now - lastStart > 200_000)
                {
                    lines.Enqueue("start");
                    lastStart = now;
                }
                return client.Latest is { RaceStarted: true, Steps: > 20 };
            }, 15_000), "race did not run");
            lines.Enqueue("quit");
            Assert.True(Pump(() => probe.Disconnected && client.Results != null, 10_000), "graceful stop did not finish");
            Assert.True(thread.Join(10_000), "server loop did not exit");
        }
        finally
        {
            cancel.Cancel();
            if (thread.IsAlive) thread.Join(8_000);
            if (!thread.IsAlive)
            {
                clientNet.Dispose();
                net.Dispose();
                server.Dispose();
            }
        }

        Assert.Equal(0, exit);
        Assert.Equal(NetFinishReason.ServerStop, client.Results!.Reason);
        Assert.True(probe.ResultsBeforeDisconnect, "the result must arrive before the transport disconnect");
        Assert.True(client.Disconnected);
        Assert.Equal(NetRejectReason.ServerShutdown, client.RejectReason!.Reason);
    }

    [GameFact]
    public void ShutdownAfterTheResult_KeepsTheDeliveredResultAndCloses()
    {
        const int raceSteps = StartStep + 30;
        var race = new ServerRaceConfig();
        using var h = new NetHarness(9303,
            new RaceServerOptions { ManualStart = true, MaxRaceSteps = raceSteps, WaitTimeoutMicros = 60_000_000, LingerMicros = 60_000_000, BotCount = race.BotCount },
            race);
        var alice = h.Join("alice", LinkProfile.Rtt(20));
        h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));

        // The interactive-style linger keeps the server open; an operator quit closes it gracefully with the same result.
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        h.Server.Shutdown();
        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Closed, 5_000_000), Tail(h));
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        Assert.Equal(NetFinishReason.TimeLimit, alice.Results!.Reason);
        Assert.Equal(NetRacePhase.Closed, h.Server.Phase);
    }
}
