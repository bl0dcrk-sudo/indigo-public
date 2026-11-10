using System.Net;
using NFS.Data;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Desktop.Tests;

/// <summary>An eliminated knockout player watches the next round over real UDP on localhost (protocol v13): a real
/// <see cref="SessionServer"/> and two real headless clients (the scenario of <c>net-client --mode ko</c> of the CLI tools), no windows. After
/// round one exactly one of them is out; in round two it receives the racers' stream (snapshots, durable state, result) without a seat and
/// ends with the same result as the one that drives.</summary>
public sealed class NetworkSpectatorTests
{
    static Task<(HeadlessClientResult Result, List<string> Log)> Client(UdpSessionServer server, string dir, HeadlessOptions options) => Task.Run(() =>
    {
        var log = new List<string>();
        var result = HeadlessClient.Run(new IPEndPoint(IPAddress.Loopback, server.Port), SimDataFingerprint.Compute(dir), options,
            TimeSpan.FromSeconds(90), s => { lock (log) log.Add(s); });
        return (result, log);
    });

    [GameDataFact]
    public async Task APlayerEliminatedInRoundOneWatchesRoundTwoAndSeesTheSameResultAsTheRacer()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = new UdpSessionServer(dir, new SessionServerOptions
        {
            CountdownMicros = 500_000, MaxRaceSteps = 160, RaceLingerMicros = 2_000_000,
        });
        var alice = Client(server, dir, new HeadlessOptions
        {
            Name = "alice", Create = "ko", MaxPlayers = 2, Laps = 2, Knockout = true, KoRounds = 2, Seconds = 90, Car = 0,
        });
        var bob = Client(server, dir, new HeadlessOptions { Name = "bob", Join = "ko", Knockout = true, KoRounds = 2, Seconds = 90, Car = 1 });
        var runs = await Task.WhenAll(alice, bob);
        var results = runs.Select(r => r.Result).ToArray();

        Assert.All(results, r => Assert.True(r.SeriesDone, HeadlessClient.Describe(r)));
        Assert.All(results, r => Assert.Equal(0, r.Malformed));
        // Round one: both drive. Round two: one drives, the one that was eliminated watches (the series goes on with one human).
        var watcher = Assert.Single(results, r => r.RoundsWatched > 0);
        var driver = Assert.Single(results, r => r.RoundsWatched == 0);
        Assert.Equal(1, watcher.RoundsWatched);
        Assert.Equal(1, watcher.RoundsDriven);
        Assert.Equal(2, driver.RoundsDriven);
        var watched = Assert.Single(watcher.Rounds!, r => r.Spectator);
        Assert.Equal(2, driver.Rounds!.Count);
        var same = driver.Rounds.Single(r => r.RaceId == watched.RaceId);
        Assert.False(same.Spectator);
        // The watcher received the racer's stream (not just the lobby state) and the very same result.
        Assert.InRange(watched.Snapshots, 20, int.MaxValue);
        Assert.NotEqual("none", watched.ResultDigest);
        Assert.Equal(same.ResultDigest, watched.ResultDigest);
        var watchLog = runs.Single(r => r.Result == watcher).Log;
        Assert.Contains(watchLog, l => l.Contains("spectator: watched race") && l.Contains(watched.ResultDigest));
        Assert.Contains(watchLog, l => l.Contains("(spectator)"));
        // The server saw no spectator input, no foreign or malformed message and ran exactly one game.
        Assert.Equal(1, server.Server.Stats.GamesCreated);
        Assert.Equal(0, server.Server.Stats.Malformed);
        Assert.Equal(0, server.Server.Stats.WrongSession);
    }
}
