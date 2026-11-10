using NFS.Core.Constants;
using NFS.Data;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Desktop.Tests;

/// <summary>The car model chosen in the menu travels to the session server with <c>SetCar</c> and the server composes the
/// race with each player's model.</summary>
public sealed class NetworkCarSelectionTests
{
    [GameDataFact]
    public async Task SessionServerGivesBothClientsTheCarTheySelected()
    {
        string dir = GameFiles.FindGameDir(null);
        const int aliceType = 3, bobType = 9;
        Assert.NotEqual(aliceType, bobType);
        using var server = new UdpSessionServer(dir, new SessionServerOptions { CountdownMicros = 500_000, MaxRaceSteps = 256 + 100 });
        using var first = RemoteDesktopSession.Connect(dir, $"127.0.0.1:{server.Port}", "Alice");
        Assert.True(await Until(() => { first.Poll(); return first.Session.Phase == SessionClientPhase.Browsing; }, 10_000), $"{first.Session.Phase} {first.Error}");
        Assert.True(first.Session.CreateGame(new NetGameCreate("Cars", 2)));
        Assert.True(await Until(() => { first.Poll(); return first.Session.Phase == SessionClientPhase.InGame; }, 10_000), $"{first.Session.Phase} {first.Error}");
        Assert.True(first.Session.SetOptions(0, 1, bots: 0));   // the server's defaults are changed by the operator in the lobby
        Assert.True(first.Session.SetCar(aliceType));
        using var second = RemoteDesktopSession.Connect(dir, $"127.0.0.1:{server.Port}", "Bob");
        Assert.True(await Until(() => { first.Poll(); second.Poll(); return second.Session.Phase == SessionClientPhase.Browsing && second.Session.Games.Count == 1; }, 10_000));
        Assert.True(second.Session.JoinGame(second.Session.Games[0].GameId));
        Assert.True(await Until(() => { first.Poll(); second.Poll(); return second.Session.Phase == SessionClientPhase.InGame; }, 10_000));
        Assert.True(second.Session.SetCar(bobType));
        Assert.True(await Until(() =>
        {
            first.Poll(); second.Poll();
            return first.Session.Lobby is { } l && l.Members.Count == 2 && l.Members.Single(m => m.Name == "Alice").CarType == aliceType
                && l.Members.Single(m => m.Name == "Bob").CarType == bobType;
        }, 10_000));
        Assert.True(first.Session.SetReady(true));
        Assert.True(second.Session.SetReady(true));
        Assert.True(await Until(() => { first.Poll(); second.Poll(); return first.Session.Lobby!.Members.All(m => m.Ready); }, 10_000));
        Assert.True(first.Session.StartRace());
        Assert.True(await Until(() =>
        {
            first.Poll(); second.Poll();
            return first.Client?.State == RaceClientState.Started && second.Client?.State == RaceClientState.Started;
        }, 25_000), $"A:{first.Session.Phase}/{first.Session.Lobby?.Phase}/{first.Error}/{first.Client?.State} B:{second.Session.Phase}/{second.Error}/{second.Client?.State}");

        Assert.Equal(aliceType, first.Session.Lobby!.Members.Single(m => m.Name == "Alice").CarType);
        Assert.Equal(bobType, first.Session.Lobby.Members.Single(m => m.Name == "Bob").CarType);
        var firstWelcome = first.Client!.Welcome!;
        var secondWelcome = second.Client!.Welcome!;
        Assert.Equal(firstWelcome.RaceId, secondWelcome.RaceId);
        Assert.NotEqual(firstWelcome.CarId, secondWelcome.CarId);
        Assert.Equal(firstWelcome.SetupBlock, secondWelcome.SetupBlock);
        Assert.Equal(firstWelcome.SetupHash, secondWelcome.SetupHash);

        var setup = new RaceSetup();
        firstWelcome.SetupBlock.CopyTo(setup.Block, 0);
        Assert.Equal(aliceType, setup.Car(firstWelcome.CarId).CarType);
        Assert.Equal(bobType, setup.Car(secondWelcome.CarId).CarType);
        Assert.Equal(RaceConstants.CarNames[aliceType], first.Scene!.Session.Cars[firstWelcome.CarId].Model);
        Assert.Equal(RaceConstants.CarNames[bobType], second.Scene!.Session.Cars[secondWelcome.CarId].Model);
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
