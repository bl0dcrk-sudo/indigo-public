using System.Diagnostics;
using System.Net;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Server;

/// <summary>
/// Entry point of the headless session server: opens the UDP transport, builds the <see cref="SessionServer"/>, reports a
/// readiness line with the actual port and runs the passive loop (poll, update, optional diagnostic console) until
/// <c>quit</c>, Ctrl+C or the optional run limit. The console is passive: no prompt, no automatic dump, and end of input
/// (EOF) does not stop the server.
/// </summary>
public static class SessionServerLoop
{
    /// <summary>Monotonic microseconds.</summary>
    public static long Micros(Stopwatch clock) => (long)clock.Elapsed.TotalMicroseconds;

    /// <summary>Runs a session server on the configured endpoint. Port 0 picks an ephemeral port, reported through
    /// <paramref name="started"/> and the readiness log. Returns 0 after a requested stop; transport/start failures throw.</summary>
    public static int Serve(SessionServerConfig config, string? gameDir, int? maxRunSeconds, Action<string> log,
        Action<int>? started = null, CancellationToken cancel = default, Func<string?>? nextLine = null)
    {
        if (!config.Validate(out string error)) throw new ArgumentException(error, nameof(config));
        var clock = Stopwatch.StartNew();
        var fingerprint = SimDataFingerprint.Compute(gameDir);
        using var net = new LiteNetServerHost();
        var server = new SessionServer(net, gameDir, fingerprint, Micros(clock), config.ToServerOptions() with { Mods = NetModSet.FromMods(NFS.Game.Mods.ModSession.Current.Mods), ModSources = NFS.Game.Mods.ModSession.Current.Mods, Cars = ServerLoop.ModCars(gameDir) }, log)
        {
            Clock = () => Micros(clock),
        };
        net.Handler = server;
        try
        {
            net.Start(config.Bind, config.Port);
            string endpoint = $"{SessionServerConfigStore.BindText(config.Bind)}:{net.Port}";
            log($"session server listening on {endpoint}; protocol {NetProtocol.Version} physics {NetProtocol.PhysicsVersion} " +
                $"data {Convert.ToHexString(fingerprint)[..16]}; max games {config.MaxGames}, max connections {config.MaxConnections}, " +
                $"lobby timeout {config.LobbyIdleTimeoutSeconds} s");
            started?.Invoke(net.Port);

            long deadline = maxRunSeconds is { } seconds ? (long)seconds * 1_000_000 : long.MaxValue;
            while (!server.Stopped)
            {
                if (cancel.IsCancellationRequested)
                {
                    log("cancelled");
                    server.Shutdown();
                }
                else if (maxRunSeconds is not null && Micros(clock) >= deadline)
                {
                    log($"run limit {maxRunSeconds} s reached");
                    server.Shutdown();
                }
                net.Poll();
                if (nextLine != null && !server.Stopped) ApplyConsole(server, nextLine, log);
                server.Update(Micros(clock));
                Thread.Sleep(ServerIdlePolicy.WaitMillis(server.NeedsFastLoop, Micros(clock), deadline));
            }
            // Let the reliable disconnect messages leave the socket.
            long flushUntil = Micros(clock) + 300_000;
            while (Micros(clock) < flushUntil) { net.Poll(); Thread.Sleep(5); }
            var s = server.Stats;
            log($"session server done: connections={s.Disconnects} messages={s.Messages} games={s.GamesCreated} " +
                $"joined={s.GamesJoined} closed={s.GamesClosed} rejected={s.Rejected} rateLimited={s.RateLimited} " +
                $"malformed={s.Malformed} wrongSession={s.WrongSession} slowDrops={s.SlowClientDrops} modsSent={s.ModsSent}");
            return 0;
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>Passive diagnostic console: <c>status</c>, <c>games</c> and <c>quit</c>. Empty input is ignored; EOF keeps the
    /// server running (the caller's line source returns null forever).</summary>
    public static void ApplyConsole(SessionServer server, Func<string?> nextLine, Action<string> log)
    {
        while (nextLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("msg", StringComparison.OrdinalIgnoreCase) || line.StartsWith("msg ", StringComparison.OrdinalIgnoreCase))
            {
                string text = line.Length > 3 ? line[4..].Trim() : "";
                int sent = text.Length == 0 ? -1 : server.BroadcastServerMessage(text);
                log(sent >= 0 ? $"console: message sent to {sent} player(s)"
                    : $"console: usage: msg <text> (1..{NetProtocol.MaxChatLength} printable ASCII characters)");
                continue;
            }
            switch (line.ToLowerInvariant())
            {
                case "status":
                    log($"status: connections={server.ConnectionCount}/{server.Options.MaxConnections} games={server.GameCount}/{server.Options.MaxGames} " +
                        $"messages={server.Stats.Messages} rejected={server.Stats.Rejected} malformed={server.Stats.Malformed} " +
                        $"wrongSession={server.Stats.WrongSession} rateLimited={server.Stats.RateLimited} slowDrops={server.Stats.SlowClientDrops}");
                    break;
                case "games":
                    if (server.GameCount == 0) log("games: (none)");
                    foreach (var game in server.Games)
                    {
                        string operatorName = game.Operator?.Name ?? "-";
                        log($"game {game.GameId:X8} '{game.Name}' phase={game.Phase} players={game.MemberCount}/{game.MaxPlayers} " +
                            $"track={game.Track} laps={game.Laps} operator='{operatorName}'");
                    }
                    break;
                case "quit":
                    log("console: quit");
                    server.Shutdown();
                    return;
                case "help":
                case "?":
                    log("console: commands: status, games, msg <text>, quit");
                    break;
                default:
                    log($"console: unknown command '{line}' (status, games, msg <text>, quit)");
                    break;
            }
        }
    }
}
