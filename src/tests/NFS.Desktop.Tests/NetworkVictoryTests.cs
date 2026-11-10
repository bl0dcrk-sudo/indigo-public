using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Network summary: when the local player wins a single race, the victory screen of the original (network: the [ref:2853]
/// winner must be the local player) shows for 0x500 ticks of 128 Hz or until a key, then the summary table; a loser, a spectator and a
/// knockout series get the table at once. The "me" row follows the local car index.</summary>
public sealed class NetworkVictoryTests
{
    sealed class View : INetSessionView
    {
        public SessionClientPhase Phase { get; set; } = SessionClientPhase.Connecting;
        public string Name => "Me";
        public IReadOnlyList<NetGameInfo> Games { get; set; } = [];
        public NetLobbyState? Lobby { get; set; }
        public bool Pending => false;
        public SessionError? LastError => null;
        public NetReject? LastReject => null;
        public int ResultsId { get; set; }
        public RaceResults? Results { get; set; }
        public RacerResult? LocalWinner { get; set; }
        public int? LocalCarIndex { get; set; }
        public List<string> Calls { get; } = [];
        public bool RequestGames() => true;
        public bool CreateGame(NetGameCreate request) => true;
        public bool JoinGame(int gameId) => true;
        public bool LeaveGame() => true;
        public bool SetReady(bool ready) => true;
        public bool SetCar(int carType, int colour) => true;
        public bool SetOptions(int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style) => true;
        public bool StartRace() => true;
        public bool CancelStart() => true;
        public bool ReturnToLobby() { Calls.Add("return"); return true; }
        public void Disconnect() { }

        public void InGame(NetGamePhase phase, NetGameMode mode = NetGameMode.SingleRace)
        {
            Phase = SessionClientPhase.InGame;
            Lobby = new NetLobbyState(7, 1, "Race", phase, 4, 3, 4, mode, 0,
                [new NetLobbyMember(0, "Me", false, 1, true, true), new NetLobbyMember(1, "Bob", false, 0, false, true)], null, false);
        }
    }

    static RaceResults Results() => new(
    [
        new RacerResult(1, 1, "MCF1", true, false, 64 * 95, [64 * 31, 64 * 32, 64 * 32], 64 * 31, 2, "Me", 100 << 16, 4),
        new RacerResult(2, 0, "F355", true, true, 64 * 99, [64 * 33, 64 * 33, 64 * 33], 64 * 33, 2, "Bob", 90 << 16, 1),
        new RacerResult(3, 2, "JAGR", false, false, 0, [], null, 1, "Beefy", 0, 2),
    ]);

    static readonly Lazy<VictoryScreen> Art = new(() => new VictoryScreen());

    /// <summary>The menu in a race that has just finished; <paramref name="meWon"/> = the local player (car 1) is the winner.</summary>
    static NetworkMenuController Finished(out View view, bool meWon = true, NetGameMode mode = NetGameMode.SingleRace, bool spectator = false)
    {
        var menu = new NetworkMenuController("Me") { VictoryArt = () => Art.Value };
        view = new View();
        menu.Attach(view);
        view.Phase = SessionClientPhase.Browsing;
        menu.Update(0);
        view.InGame(NetGamePhase.Race, mode);
        menu.Update(0);
        Assert.Equal(NetScreen.Race, menu.Screen);
        var results = Results();
        view.Results = results;
        view.ResultsId = 1;
        view.LocalCarIndex = spectator ? null : meWon ? 1 : 0;
        view.LocalWinner = spectator || !meWon ? null : results.Racers[0];
        view.InGame(NetGamePhase.Lobby, mode);
        menu.Update(1_000_000);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        return menu;
    }

    [Fact]
    public void TheLocalWinnerSeesTheVictoryScreenBeforeTheSummary()
    {
        var menu = Finished(out _);
        Assert.NotNull(menu.VictoryWinner);
        Assert.Equal(4, menu.VictoryWinner!.CarType);
    }

    [Fact]
    public void TheVictoryScreenEndsAfter0x500TicksOf128Hz()
    {
        var menu = Finished(out _);
        long start = 1_000_000;
        menu.Update(start + VictoryRules.DurationTicks * 1_000_000 / 128 - 100_000);
        Assert.NotNull(menu.VictoryWinner);
        menu.Update(start + (VictoryRules.DurationTicks + 2) * 1_000_000 / 128);
        Assert.Null(menu.VictoryWinner);
        Assert.Equal(NetScreen.Summary, menu.Screen);
    }

    [Fact]
    public void AnyKeyEndsTheVictoryScreenAndTheNextOneUsesTheSummary()
    {
        var menu = Finished(out var view);
        menu.Key(MenuKey.Left);
        Assert.Null(menu.VictoryWinner);
        Assert.Empty(view.Calls);              // the key was consumed by the victory screen
        menu.Update(2_000_000);
        Assert.Null(menu.VictoryWinner);       // it does not come back for the same results
        menu.Key(MenuKey.Enter);
        Assert.Equal(["return"], view.Calls);
    }

    [Fact]
    public void TheLoserTheSpectatorAndAKnockoutSeriesGetTheSummaryAtOnce()
    {
        Assert.Null(Finished(out _, meWon: false).VictoryWinner);
        Assert.Null(Finished(out _, spectator: true).VictoryWinner);
        Assert.Null(Finished(out _, mode: NetGameMode.Knockout).VictoryWinner);
    }

    [GameDataFact]
    public void TheVictoryScreenDrawsTheArtAndTheSummaryTableFollows()
    {
        var menu = Finished(out _);
        var victory = new FrameBuffer();
        NetworkMenuRenderer.Draw(victory, menu, background: true);
        var expected = new FrameBuffer();
        Art.Value.Draw(expected, menu.VictoryWinner!, NetworkMenuController.TrackIdOfMenuTrack(4), 0, animated: true);
        Assert.Equal(expected.Rgba, victory.Rgba);
        Review(victory, "victory.png");
        menu.Key(MenuKey.Enter);
        var table = new FrameBuffer();
        NetworkMenuRenderer.Draw(table, menu, background: true);
        Assert.NotEqual(victory.Rgba, table.Rgba);
        Review(table, "summary.png");
    }

    [Fact]
    public void WithoutVictoryArtTheSummaryIsNotBlocked()
    {
        var menu = Finished(out _);
        menu.VictoryArt = () => null;
        NetworkMenuRenderer.Draw(new FrameBuffer(), menu, background: true);
        Assert.Null(menu.VictoryWinner);
    }

    [Fact]
    public void TheMenuTrackMapsBackToTheTrackIdOfTheOriginal()
    {
        for (int t = 0; t < 8; t++) Assert.Equal(t, VictoryRules.MenuTrack(NetworkMenuController.TrackIdOfMenuTrack(t)));
    }

    static void Review(FrameBuffer fb, string name)
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "out", "gfx_check", "net_victory"));
        if (Environment.GetEnvironmentVariable("NFS_NET_VICTORY_REVIEW") != "1") return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }
}
