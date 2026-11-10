using System.Net;
using NFS.Data;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Desktop.Tests;

/// <summary>A whole knockout series over real UDP on localhost: a real <see cref="SessionServer"/> (<see cref="UdpSessionServer"/>)
/// and two real headless clients (the scenario of <c>net-client --mode ko</c> of the CLI tools), no windows. The server runs both rounds in
/// one game, counts every result once and eliminates exactly one entrant per round; the clients only follow the lobby state.</summary>
public sealed class NetworkKnockoutTests
{
    static Task<(HeadlessClientResult Result, List<string> Log)> Client(UdpSessionServer server, string dir, HeadlessOptions options) => Task.Run(() =>
    {
        var log = new List<string>();
        var result = HeadlessClient.Run(new IPEndPoint(IPAddress.Loopback, server.Port), SimDataFingerprint.Compute(dir), options,
            TimeSpan.FromSeconds(90), s => { lock (log) log.Add(s); });
        return (result, log);
    });

    [GameDataFact]
    public async Task TwoHumansPlayTwoKnockoutRoundsInOneGame_OneEntrantLeavesPerRound()
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
        Assert.All(results, r => Assert.Equal(2, r.SeriesRounds));
        Assert.All(results, r => Assert.Equal(0, r.Malformed));
        Assert.True(results[0].Operator);
        Assert.Equal(results[0].GameId, results[1].GameId);
        // Everybody who was still in drove the round: the first round both, the second only the one that was not eliminated.
        Assert.All(results, r => Assert.InRange(r.RoundsDriven, 1, 2));
        Assert.InRange(results.Sum(r => r.RoundsDriven), 3, 4);
        Assert.Equal(1, server.Server.Stats.GamesCreated);
        // The table both clients logged at the end of the series is the server's: seven entrants still in, two out (one per round).
        var tables = runs.Select(r => r.Log.Single(l => l.Contains("series: 2 rounds done"))).Select(l => l[l.IndexOf("table", StringComparison.Ordinal)..]).ToArray();
        Assert.Equal(tables[0], tables[1]);
        Assert.Equal(2, tables[0].Split(':').Count(t => t.StartsWith("out", StringComparison.Ordinal)));
        Assert.Equal(7, tables[0].Split(", ").Count(t => t.EndsWith(":in", StringComparison.Ordinal)));
        Assert.Equal(0, server.Server.Stats.Malformed);
        Assert.Equal(0, server.Server.Stats.WrongSession);
    }
}
