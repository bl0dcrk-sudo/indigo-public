using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Administrative configuration of the headless session server (server-config.json): defaults, old-field
/// compatibility, validation and mapping to the runtime options.</summary>
public class SessionServerConfigTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), $"nfs2se-session-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_ListenOnAllInterfacesPort27100_AndValidate()
    {
        var config = new SessionServerConfig();
        Assert.Equal(IPAddress.Any, config.Bind);
        Assert.Equal(SessionServerConfig.DefaultPort, config.Port);
        Assert.Equal(16, config.MaxGames);
        Assert.Equal(256, config.MaxConnections);
        Assert.Equal(900, config.LobbyIdleTimeoutSeconds);
        Assert.Equal(30, config.GraceSeconds);
        Assert.True(config.Validate(out string error), error);
    }

    [Fact]
    public void ServerOptions_CarryTheAdministrativeValuesInMicros()
    {
        var options = new SessionServerConfig
        {
            MaxGames = 3, MaxConnections = 7, LobbyIdleTimeoutSeconds = 2, GraceSeconds = 4, RaceWaitSeconds = 5, MaxRaceSteps = 6,
        }.ToServerOptions();
        Assert.Equal(3, options.MaxGames);
        Assert.Equal(7, options.MaxConnections);
        Assert.Equal(2_000_000, options.LobbyIdleTimeoutMicros);
        Assert.Equal(4_000_000, options.GraceMicros);
        Assert.Equal(5_000_000, options.RaceWaitTimeoutMicros);
        Assert.Equal(6, options.MaxRaceSteps);
    }

    [Fact]
    public void TrySaveAndLoad_RoundTripTheAdministrativeFields()
    {
        string path = TempFile();
        try
        {
            var config = new SessionServerConfig
            {
                Bind = IPAddress.Loopback, Port = 0, MaxGames = 5, MaxConnections = 42, LobbyIdleTimeoutSeconds = 60,
                GraceSeconds = 10, RaceWaitSeconds = 20, MaxRaceSteps = 30,
            };
            Assert.True(SessionServerConfigStore.TrySave(path, config, out string saveError), saveError);
            Assert.True(SessionServerConfigStore.TryLoad(path, out var loaded, out string loadError), loadError);
            Assert.Equal(config, loaded);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TryLoad_AcceptsTheOldPerRaceFile_AndIgnoresGameFlowFields()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, """
                {
                  "version": 1, "mode": "single", "bind": "127.0.0.1", "port": 28000,
                  "track": 3, "laps": 4, "minPlayers": 2, "maxPlayers": 4, "bots": -1, "traffic": false, "backward": true,
                  "maxGames": 7, "lobbyIdleTimeoutSeconds": 120
                }
                """);
            Assert.True(SessionServerConfigStore.TryLoad(path, out var config, out string error), error);
            Assert.Equal(IPAddress.Loopback, config!.Bind);
            Assert.Equal(28000, config.Port);
            Assert.Equal(7, config.MaxGames);
            Assert.Equal(120, config.LobbyIdleTimeoutSeconds);
            Assert.Equal(new SessionServerConfig().GraceSeconds, config.GraceSeconds);
            Assert.Equal(new SessionServerConfig().RaceWaitSeconds, config.RaceWaitSeconds);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void TryLoad_RejectsInvalidValues_AndReportsAMissingFile()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, """{ "port": 70000 }""");
            Assert.False(SessionServerConfigStore.TryLoad(path, out var config, out string error));
            Assert.Null(config);
            Assert.Contains("port", error);

            File.WriteAllText(path, """{ "bind": "not-an-address" }""");
            Assert.False(SessionServerConfigStore.TryLoad(path, out _, out error));
            Assert.Contains("bind", error);

            File.WriteAllText(path, """{ "maxGames": 0 }""");
            Assert.False(SessionServerConfigStore.TryLoad(path, out _, out error));
            Assert.Contains("max games", error);
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        Assert.False(SessionServerConfigStore.TryLoad(TempFile(), out _, out string missing));
        Assert.Contains("no configuration", missing);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeAdministrativeValues()
    {
        Assert.False(new SessionServerConfig { MaxGames = 0 }.Validate(out _));
        Assert.False(new SessionServerConfig { MaxGames = NetProtocol.MaxGameListEntries + 1 }.Validate(out _));
        Assert.False(new SessionServerConfig { MaxConnections = 0 }.Validate(out _));
        Assert.False(new SessionServerConfig { LobbyIdleTimeoutSeconds = 0 }.Validate(out _));
        Assert.False(new SessionServerConfig { GraceSeconds = -1 }.Validate(out _));
        Assert.False(new SessionServerConfig { MaxRaceSteps = -1 }.Validate(out _));
    }
}

/// <summary>Command line of the headless server: no-argument start, administrative overrides and accepted-but-ignored legacy
/// per-race flags.</summary>
public class SessionServerCommandLineTests
{
    static SessionServerConfig Parse(string[] args)
    {
        Assert.True(SessionServerCommandLine.TryParse(args, out var launch, out string error), error);
        return launch.Apply(new SessionServerConfig());
    }

    [Fact]
    public void NoArguments_ListenImmediatelyOnAllInterfacesPort27100()
    {
        var config = Parse([]);
        Assert.Equal(IPAddress.Any, config.Bind);
        Assert.Equal(27100, config.Port);
        Assert.Equal(new SessionServerConfig().MaxGames, config.MaxGames);
    }

    [Fact]
    public void AdministrativeFlags_OverrideTheFileConfiguration()
    {
        var file = new SessionServerConfig { Port = 1234, MaxGames = 9, GraceSeconds = 1 };
        Assert.True(SessionServerCommandLine.TryParse(
            ["--bind", "127.0.0.1", "--port", "0", "--max-games", "3", "--max-connections", "12",
             "--lobby-timeout-seconds", "60", "--grace-seconds", "5", "--race-wait-seconds", "30", "--max-race-steps", "25"],
            out var launch, out string error), error);
        var config = launch.Apply(file);
        Assert.Equal(IPAddress.Loopback, config.Bind);
        Assert.Equal(0, config.Port);
        Assert.Equal(3, config.MaxGames);
        Assert.Equal(12, config.MaxConnections);
        Assert.Equal(60, config.LobbyIdleTimeoutSeconds);
        Assert.Equal(5, config.GraceSeconds);
        Assert.Equal(30, config.RaceWaitSeconds);
        Assert.Equal(25, config.MaxRaceSteps);
        Assert.Equal(1, file.GraceSeconds);
    }

    [Fact]
    public void LegacyPerRaceFlags_AreAcceptedAndIgnored()
    {
        Assert.True(SessionServerCommandLine.TryParse(
            ["--track", "3", "--laps", "2", "--players", "4", "--opponents", "2", "--traffic",
             "--wait-seconds", "10", "--interactive", "--port", "0"],
            out var launch, out string error), error);
        Assert.Equal(["--interactive", "--laps", "--opponents", "--players", "--track", "--traffic", "--wait-seconds"],
            launch.Ignored.Order().ToArray());
        var config = launch.Apply(new SessionServerConfig());
        Assert.Equal(0, config.Port);
    }

    [Fact]
    public void StartupHelp_ShowsEffectiveAddressAndHowToChooseIpAndPort()
    {
        Assert.True(SessionServerCommandLine.TryParse(["--bind", "10.1.2.3", "--port", "4000"], out var launch, out string error), error);
        var config = launch.Apply(new SessionServerConfig());
        string text = string.Join(" ", SessionServerCommandLine.StartupHelp(config, "x/server-config.json", configFileUsed: false));
        Assert.Contains("10.1.2.3 port 4000", text);
        Assert.Contains("--bind <IP>", text);
        Assert.Contains("--port <N>", text);
        Assert.Contains("x/server-config.json", text);
        Assert.Contains("--help", text);
        Assert.Contains("no settings file", text);
        string withFile = string.Join(" ", SessionServerCommandLine.StartupHelp(config, "x/server-config.json", configFileUsed: true));
        Assert.DoesNotContain("no settings file", withFile);
    }

    [Fact]
    public void Usage_MentionsBindAndPort()
    {
        Assert.Contains("--bind <IP> --port <N>", SessionServerCommandLine.Usage);
        Assert.Contains("--max-games", SessionServerCommandLine.Usage);
    }

    [Fact]
    public void Help_NeedsNoOtherArguments()
    {
        Assert.True(SessionServerCommandLine.TryParse(["--help"], out var launch, out _));
        Assert.True(launch.Help);
    }

    [Fact]
    public void BadArguments_AreReportedWithAReadableReason()
    {
        Assert.False(SessionServerCommandLine.TryParse(["--port", "70000"], out _, out string error));
        Assert.Contains("--port", error);
        Assert.False(SessionServerCommandLine.TryParse(["--max-games", "0"], out _, out error));
        Assert.False(SessionServerCommandLine.TryParse(["--max-games"], out _, out error));
        Assert.False(SessionServerCommandLine.TryParse(["--bind", "nope"], out _, out error));
        Assert.False(SessionServerCommandLine.TryParse(["--unknown"], out _, out error));
        Assert.False(SessionServerCommandLine.TryParse(["nonsense"], out _, out error));
        Assert.False(SessionServerCommandLine.TryParse(["--track", "99"], out _, out error));
    }
}

/// <summary>Headless run: readiness log with the actual port, passive console commands and a clean stop.</summary>
public class SessionServerLoopTests
{
    [GameFact]
    public void HeadlessServer_ReportsReadiness_ServesPassiveConsoleAndStopsOnQuit()
    {
        var config = new SessionServerConfig { Bind = IPAddress.Loopback, Port = 0, MaxGames = 3 };
        var log = new ConcurrentQueue<string>();
        var input = new ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        int port = -1;
        var task = Task.Run(() => SessionServerLoop.Serve(config, GameData.Dir, null, line => log.Enqueue(line),
            p => { port = p; started.Set(); }, default, () => input.TryDequeue(out var line) ? line : null));

        Assert.True(started.Wait(TimeSpan.FromSeconds(20)), "server did not start");
        Assert.InRange(port, 1, 65535);
        Assert.Contains(log, line => line.Contains($"listening on 127.0.0.1:{port}") &&
            line.Contains($"protocol {NetProtocol.Version}") && line.Contains("max games 3"));

        input.Enqueue("status");
        input.Enqueue("games");
        input.Enqueue("quit");
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "server did not stop after quit");
        Assert.Equal(0, task.Result);
        Assert.Contains(log, line => line.Contains("console: quit"));
        Assert.Contains(log, line => line.Contains("status: connections="));
        Assert.Contains(log, line => line.Contains("games:"));
        Assert.Contains(log, line => line.Contains("session server done:"));
    }

    /// <summary>Launches the real NFS.Server executable on loopback with an ephemeral port, waits for the readiness line and
    /// port file, then terminates the process. No window, no real game process.</summary>
    [GameFact]
    public void ServerProcess_WithPortZero_WritesPortFileAndReadinessLog_AndCanBeTerminated()
    {
        string? found = FindServerDll();
        Assert.True(found != null, "Indigo-Server.dll not found (build the solution first)");
        string dll = found!;
        string dir = Path.Combine(Path.GetTempPath(), $"nfs2se-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string portFile = Path.Combine(dir, "port.txt");
        string logFile = Path.Combine(dir, "server.log");
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string a in new[]
                     {
                         dll, "--bind", "127.0.0.1", "--port", "0", "--game", GameData.Dir!, "--port-file", portFile,
                         "--log", logFile, "--max-run-seconds", "60", "--max-games", "4",
                     })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            try
            {
                var watch = Stopwatch.StartNew();
                string portText = "";
                while (watch.Elapsed < TimeSpan.FromSeconds(25))
                {
                    portText = ReadShared(portFile).Trim();
                    if (portText.Length > 0) break;
                    Thread.Sleep(50);
                }
                Assert.True(portText.Length > 0, "server did not write its port file");
                Assert.True(int.TryParse(portText, out int port));
                Assert.InRange(port, 1, 65535);

                string log = "";
                watch.Restart();
                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    log = ReadShared(logFile);
                    if (log.Contains("listening on 127.0.0.1:")) break;
                    Thread.Sleep(50);
                }
                Assert.Contains("session server listening on 127.0.0.1:" + port, log);
                Assert.Contains($"protocol {NetProtocol.Version} physics {NetProtocol.PhysicsVersion}", log);
                Assert.Contains("max games 4", log);

                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(10_000), "server process did not terminate");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    static string ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    static string? FindServerDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NFS2SE.sln"))) dir = dir.Parent;
        if (dir == null) return null;
        foreach (string configuration in new[] { "Release", "Debug" })
        {
            string candidate = Path.Combine(dir.FullName, "src", "NFS.Server", "bin", configuration, "net10.0", "Indigo-Server.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>Two simultaneous games with different tracks and several humans each: countdown, race, results and return to the
/// lobby of one game do not disturb the other.</summary>
public class SessionServerIntegrationTests
{
    [GameFact]
    public void TwoGamesWithDifferentTracks_RunSimultaneouslyWithoutInterference()
    {
        using var h = new SessionHarness(70, new SessionServerOptions { MaxGames = 2, MaxRaceSteps = 25 });
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        var carol = h.Join("carol");
        var dave = h.Join("dave");
        var erin = h.Join("erin");
        var frank = h.Join("frank");
        Assert.True(h.RunUntil(() => h.Clients.All(c => c.List != null), 1_000_000));

        alice.Create("alpha", 4, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        dave.Create("beta", 3, 5, 2);
        Assert.True(h.RunUntil(() => dave.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        carol.JoinGame(alice.GameId);
        erin.JoinGame(dave.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby && carol.InLobby && erin.InLobby && alice.Lobby?.Members.Count == 3 &&
            dave.Lobby?.Members.Count == 2, 1_000_000));
        Assert.Equal(2, h.Server.GameCount);

        alice.SetCar(1);
        bob.SetCar(2);
        carol.SetCar(3);
        dave.SetCar(4);
        erin.SetCar(5);
        Assert.True(h.RunUntil(() => alice.Lobby!.Members.All(m => m.CarType >= 0) &&
            dave.Lobby!.Members.All(m => m.CarType >= 0), 1_000_000));

        alice.RequestStart();
        dave.RequestStart();
        Assert.True(h.RunUntil(() => alice.InCountdown && dave.InCountdown, 1_000_000));
        Assert.True(h.RunUntil(() => new[] { alice, bob, carol, dave, erin }.All(c => c.Welcomes.Count > 0), 10_000_000));

        int raceA = alice.Welcome!.RaceId, raceB = dave.Welcome!.RaceId;
        Assert.NotEqual(raceA, raceB);
        foreach (var member in new[] { alice, bob, carol })
            Assert.All(member.Welcomes, w => Assert.Equal(raceA, w.RaceId));
        foreach (var member in new[] { dave, erin })
            Assert.All(member.Welcomes, w => Assert.Equal(raceB, w.RaceId));
        Assert.Empty(frank.Welcomes);

        Assert.True(h.RunUntil(() => new[] { alice, bob, carol, dave, erin }.All(c => c.Results != null), 10_000_000));
        foreach (var member in new[] { alice, bob, carol })
        {
            Assert.Equal(raceA, member.Results!.RaceId);
            Assert.Equal(8, member.Results.Results.Racers.Count);
            Assert.Equal(3, member.Results.Results.Racers.Count(x => x.Human));
        }
        foreach (var member in new[] { dave, erin })
        {
            Assert.Equal(raceB, member.Results!.RaceId);
            Assert.Equal(8, member.Results.Results.Racers.Count);
            Assert.Equal(2, member.Results.Results.Racers.Count(x => x.Human));
        }

        Assert.True(h.RunUntil(() => new[] { alice, bob, carol, dave, erin }.All(c => c.InLobby), 5_000_000));
        Assert.Equal("alpha", alice.Lobby!.GameName);
        Assert.Equal(1, alice.Lobby.Track);
        Assert.Equal(1, alice.Lobby.Laps);
        Assert.Equal(3, alice.Lobby.Members.Count);
        Assert.Equal(["alice", "bob", "carol"], alice.Lobby.Members.Select(m => m.Name));
        Assert.True(alice.Lobby.Members[0].Operator);

        Assert.Equal("beta", dave.Lobby!.GameName);
        Assert.Equal(5, dave.Lobby.Track);
        Assert.Equal(2, dave.Lobby.Laps);
        Assert.Equal(2, dave.Lobby.Members.Count);
        Assert.Equal(["dave", "erin"], dave.Lobby.Members.Select(m => m.Name));
        Assert.True(dave.Lobby.Members[0].Operator);

        Assert.NotEqual(alice.GameId, dave.GameId);
        Assert.True(h.RunUntil(() => frank.List?.Games.Count == 2, 1_000_000));
        Assert.All(frank.List!.Games, g => Assert.Equal(NetGamePhase.Lobby, g.Phase));
        Assert.Equal(3, frank.List.Games.Single(g => g.GameId == alice.GameId).Players);
        Assert.Equal(2, frank.List.Games.Single(g => g.GameId == dave.GameId).Players);
        Assert.Equal(1, frank.List.Games.Single(g => g.GameId == alice.GameId).Track);
        Assert.Equal(5, frank.List.Games.Single(g => g.GameId == dave.GameId).Track);
        Assert.Equal(2L, h.Server.Stats.GamesCreated);
        Assert.Equal(0L, h.Server.Stats.GamesClosed);
    }
}
