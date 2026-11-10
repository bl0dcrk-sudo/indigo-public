using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Interactive lobby of the dedicated server (TASKS item 15, first stage): seats becoming connected and ready do
/// not start the race; only the operator `start` does, and only with both human seats ready.</summary>
public class ServerManualStartTests
{
    [GameFact]
    public void UnattendedLegacyRaceWithNoOpponentsStillStarts()
    {
        using var h = new NetHarness(503, race: new ServerRaceConfig(Opponents: 0));
        h.Join("alice", LinkProfile.Rtt(10));
        h.Join("bob", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Racing, 20_000_000),
            string.Join("\n", h.Log.TakeLast(10)));
        Assert.Equal(0, h.Server.Host.Runner.Session.Setup.NumOpponentCars);
    }

    static RaceServerOptions Manual(int raceSteps = 600) => new()
    {
        ManualStart = true,
        MaxRaceSteps = raceSteps,
        WaitTimeoutMicros = 60_000_000,
        LingerMicros = 3_000_000,
        BotCount = 2,
    };

    [GameFact]
    public void FilledAndReadyLobbyWaitsForTheOperator()
    {
        using var h = new NetHarness(501, Manual());
        var a = h.Join("alice", LinkProfile.Rtt(10));
        var b = h.Join("bob", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), string.Join("\n", h.Log.TakeLast(10)));

        var lobby = h.Server.Lobby;
        Assert.Equal(NetRacePhase.Loading, lobby.Phase);
        Assert.Equal(2, lobby.Laps);
        Assert.Equal(h.Host.Runner.Session.TrackNumber, lobby.Track); // menu track 1 = TR02, as in the original
        Assert.Equal(6, lobby.Bots); // auto fills the grid: two humans + six AI
        Assert.Equal(["alice", "bob"], lobby.Seats.Select(s => s.Name).Order());
        Assert.Equal(new[] { a.Welcome!.CarId, b.Welcome!.CarId }.Order(), lobby.Seats.Select(s => s.CarId).Order());
        Assert.All(lobby.Seats, seat => { Assert.True(seat.Connected); Assert.True(seat.Ready); });

        string text = ServerConsole.FormatLobby(lobby);
        Assert.Contains("alice", text);
        Assert.Contains("bob", text);
        Assert.Contains("ready", text);
        Assert.Contains("bots=6 (auto, cap 6)", text);

        // Waiting longer must not start the race: readiness alone is not a start command.
        h.Run(5_000_000);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);
        Assert.Equal(0, h.Host.Steps);
        Assert.Null(a.Results);
        Assert.Null(b.Results);
    }

    [GameFact]
    public void OperatorStartNeedsBothSeatsAndStartsTheRaceExactlyOnce()
    {
        using var h = new NetHarness(502, Manual());
        var a = h.Join("alice", LinkProfile.Rtt(10));
        h.Run(500_000);
        Assert.False(h.Server.TryOperatorStart(out string why));
        Assert.Contains("waiting for players", why);
        Assert.Equal(0, h.Host.Steps);

        var b = h.Join("bob", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), string.Join("\n", h.Log.TakeLast(10)));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.Equal("", error);
        Assert.Equal(NetRacePhase.Countdown, h.Server.Phase);
        Assert.False(h.Server.TryOperatorStart(out _)); // a second operator start is refused

        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Racing, 20_000_000), string.Join("\n", h.Log.TakeLast(10)));
        Assert.True(h.Host.Steps > 0);
        Assert.Equal(RaceClientState.Started, a.State);
        Assert.Equal(RaceClientState.Started, b.State);
    }
}
