using System.Diagnostics;
using System.Net;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Run limits of the dedicated server: the interactive operator lobby is persistent (no hard deadline, no waiting
/// timeout) and ends only on `quit`/Ctrl+C/EOF, while an unattended command-line run stays bounded.</summary>
public class ServerRunLimitsTests
{
    [Fact]
    public void InteractiveLobbyHasNoHardDeadlineOrWaitingTimeout()
    {
        var limits = ServerRunLimits.Interactive(17);
        Assert.Null(limits.MaxRun);
        Assert.Null(limits.WaitForPlayers);
        Assert.Equal(17, limits.MaxRaceSteps);
        Assert.Equal(0, ServerRunLimits.Interactive(-5).MaxRaceSteps);
    }

    [Fact]
    public void NonInteractiveRunStaysBoundedAndClamped()
    {
        var limits = ServerRunLimits.NonInteractive(900, 120, 5);
        Assert.Equal(TimeSpan.FromSeconds(900), limits.MaxRun);
        Assert.Equal(TimeSpan.FromSeconds(120), limits.WaitForPlayers);
        Assert.Equal(5, limits.MaxRaceSteps);

        Assert.Equal(TimeSpan.FromSeconds(1), ServerRunLimits.NonInteractive(0, -3).MaxRun);
        Assert.Equal(TimeSpan.FromSeconds(1), ServerRunLimits.NonInteractive(0, -3).WaitForPlayers);
        Assert.Equal(TimeSpan.FromSeconds(3600), ServerRunLimits.NonInteractive(999_999, 999_999).MaxRun);
        Assert.Equal(0, ServerRunLimits.NonInteractive(900, 120, -1).MaxRaceSteps);
    }

    static (RaceServer Server, LiteNetServerHost Net, Stopwatch Clock) StartServer(int seed)
    {
        var race = new ServerRaceConfig();
        var host = race.CreateHost(GameData.Dir, seed);
        var net = new LiteNetServerHost();
        var clock = Stopwatch.StartNew();
        var server = new RaceServer(host, NetHarness.Fingerprint, net, ServerLoop.Micros(clock),
            new RaceServerOptions { ManualStart = true, WaitTimeoutMicros = long.MaxValue, BotCount = race.BotCount },
            null, seed + 1, hostFactoryBots: (id, humans, bots) => race.CreateHost(GameData.Dir, id, humans, bots),
            hostFactoryTrack: (id, humans, bots, types, track, backward) =>
                (race with { Track = track, Backward = backward }).CreateHost(GameData.Dir, id, humans, bots, types));
        net.Start(IPAddress.Loopback, 0);
        return (server, net, clock);
    }

    [GameFact]
    public void PersistentLobbyRunsUntilQuitInsteadOfAHardDeadline()
    {
        var (server, net, clock) = StartServer(901);
        using (server)
        using (net)
        {
            var log = new List<string>();
            int polls = 0;
            var interaction = new ServerInteraction
            {
                ManualStart = true,
                NextLine = () => ++polls < 5 ? null : "quit",
            };
            int code = ServerLoop.Run(server, net, clock, ServerRunLimits.Interactive().MaxRun, log.Add,
                CancellationToken.None, interaction);
            Assert.Equal(2, code); // closed by the operator without a race
            Assert.Equal(NetRacePhase.Closed, server.Phase);
            Assert.Contains(log, l => l.Contains("console: quit"));
            Assert.DoesNotContain(log, l => l.Contains("run limit"));
        }
    }

    [GameFact]
    public void ConsolePrintsNoPeriodicHeartbeat()
    {
        var (server, net, clock) = StartServer(904);
        using (server)
        using (net)
        {
            var log = new List<string>();
            var wait = Stopwatch.StartNew();
            var interaction = new ServerInteraction
            {
                ManualStart = true,
                // Idle for longer than the old one-second heartbeat; the console must stay silent until `quit`.
                NextLine = () => wait.Elapsed < TimeSpan.FromMilliseconds(1200) ? null : "quit",
            };
            int code = ServerLoop.Run(server, net, clock, null, log.Add, CancellationToken.None, interaction);
            Assert.Equal(2, code);
            Assert.Equal(["console: quit"], log);
        }
    }

    [GameFact]
    public void StatusPrintsTheLobbyOnDemand()
    {
        var (server, net, clock) = StartServer(905);
        using (server)
        using (net)
        {
            var log = new List<string>();
            var lines = new Queue<string>(["status", "quit"]);
            var interaction = new ServerInteraction
            {
                ManualStart = true,
                NextLine = () => lines.TryDequeue(out string? line) ? line : null,
            };
            ServerLoop.Run(server, net, clock, null, log.Add, CancellationToken.None, interaction);
            Assert.Contains(log, l => l.StartsWith("lobby phase=WaitingForPlayers"));
            Assert.Contains(log, l => l.Contains("seat 0"));
            Assert.Contains(log, l => l.Contains("seat 1"));
            // Nothing else is printed between the status request and the quit.
            Assert.DoesNotContain(log, l => l.Contains("t=") && l.Contains("backlog="));
        }
    }

    [GameFact]
    public void NonInteractiveRunStillClosesOnTheHardDeadline()
    {
        var (server, net, clock) = StartServer(902);
        using (server)
        using (net)
        {
            var log = new List<string>();
            int code = ServerLoop.Run(server, net, clock, TimeSpan.FromMilliseconds(40), log.Add, CancellationToken.None);
            Assert.Equal(2, code);
            Assert.Equal(NetRacePhase.Closed, server.Phase);
            Assert.Contains(log, l => l.Contains("run limit"));
        }
    }

    [GameFact]
    public void OperatorBotChangesNotifyPersistenceOnlyWhenAccepted()
    {
        var (server, net, clock) = StartServer(903);
        using (server)
        using (net)
        {
            var lines = new Queue<string>(["bots 3", "bot +", "bots 8", "quit"]);
            var saved = new List<int>();
            var interaction = new ServerInteraction
            {
                ManualStart = true,
                NextLine = () => lines.TryDequeue(out string? line) ? line : null,
                BotSettingChanged = saved.Add,
            };
            ServerLoop.Run(server, net, clock, null, _ => { }, CancellationToken.None, interaction);
            Assert.Equal([3, 4], saved);
        }
    }

    [GameFact]
    public void OperatorTrackChangesNotifyPersistenceOnlyWhenAccepted()
    {
        var (server, net, clock) = StartServer(906);
        using (server)
        using (net)
        {
            var log = new List<string>();
            var lines = new Queue<string>(["track 3", "track 9", "track 5", "quit"]);
            var saved = new List<int>();
            var interaction = new ServerInteraction
            {
                ManualStart = true,
                NextLine = () => lines.TryDequeue(out string? line) ? line : null,
                TrackChanged = saved.Add,
            };
            ServerLoop.Run(server, net, clock, null, log.Add, CancellationToken.None, interaction);
            Assert.Equal([3, 5], saved); // 9 is out of range: refused, nothing persisted
            Assert.Contains(log, l => l == "console: track must be in 0..7");
            Assert.Contains(log, l => l == "console: track set to menu track 5 (TR06) for the next start");
        }
    }
}
