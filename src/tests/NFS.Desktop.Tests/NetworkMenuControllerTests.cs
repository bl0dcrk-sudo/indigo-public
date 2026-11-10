using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop.Tests;

/// <summary>Task 5.1 (network-game-sessions): state and navigation of the network screens, driven by a fake session state.</summary>
public sealed class NetworkMenuControllerTests
{
    sealed class FakeView : INetSessionView
    {
        public SessionClientPhase Phase { get; set; } = SessionClientPhase.Connecting;
        public string Name { get; set; } = "Me";
        public IReadOnlyList<NetGameInfo> Games { get; set; } = [];
        public NetLobbyState? Lobby { get; set; }
        public bool Pending { get; set; }
        public SessionError? LastError { get; set; }
        public NetReject? LastReject { get; set; }
        public int ResultsId { get; set; }
        public List<string> Calls { get; } = [];
        public bool Accept { get; set; } = true;
        public long? ReconnectRemainingMicros { get; set; }

        bool Do(string call) { Calls.Add(call); return Accept; }
        public bool RequestGames() => Do("games");
        public bool CreateGame(NetGameCreate r) { Created = r; return Do("create"); }
        public bool JoinGame(int id) => Do($"join:{id}");
        public bool LeaveGame() => Do("leave");
        public bool SetReady(bool ready) => Do($"ready:{ready}");
        public bool SetCar(int car, int colour) { LastCar = (car, colour); return Do(colour == NetProtocol.NoColour ? $"car:{car}" : $"car:{car}:{colour}"); }
        public bool SetOptions(int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style)
        {
            LastOptions = (track, laps, backward, bots, traffic, skill, catchUp, style);
            return Do($"opts:{track}:{laps}:{backward}");
        }
        public (int Car, int Colour)? LastCar { get; private set; }
        public (int Track, int Laps, bool Backward, int Bots, bool Traffic, int Skill, bool CatchUp, int Style)? LastOptions { get; private set; }
        public bool StartRace() => Do("start");
        public bool CancelStart() => Do("cancelstart");
        public bool ReturnToLobby() => Do("return");
        public void Disconnect() { Calls.Add("disconnect"); Phase = SessionClientPhase.Closed; }
        public NetGameCreate? Created { get; private set; }

        public void InGame(NetGamePhase phase, bool op = true, int track = 2, int laps = 3, bool backward = false, int car = 1, bool ready = false,
            NetGameMode mode = NetGameMode.SingleRace, NetKnockoutState? series = null, int colour = NetProtocol.NoColour,
            int bots = NetProtocol.AutoBots, bool traffic = false, int skill = 1, bool catchUp = true, int style = 1)
        {
            Phase = SessionClientPhase.InGame;
            Lobby = new NetLobbyState(7, 1, "Race", phase, track, laps, 4, mode, phase == NetGamePhase.Countdown ? 5000 : 0,
                [new NetLobbyMember(0, op ? "Me" : "Op", op && ready, op ? car : 0, true, true, op ? colour : NetProtocol.NoColour),
                 new NetLobbyMember(1, op ? "Bob" : "Me", !op && ready, op ? 0 : car, false, true, op ? NetProtocol.NoColour : colour)],
                null, backward, bots, traffic, 0, series, skill, catchUp, style);
        }
    }

    static NetGameInfo Game(int id, string name, NetGamePhase phase, int players = 1, int max = 4) =>
        new(id, name, phase, players, max, 1, 3, NetGameMode.SingleRace);

    static NetworkMenuController Connected(out FakeView view, params NetGameInfo[] games)
    {
        var menu = new NetworkMenuController("Me");
        view = new FakeView();
        PressTo(menu, ConnectRow.Connect);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Connecting, menu.Screen);
        menu.Attach(view);
        view.Games = games;
        view.Phase = SessionClientPhase.Browsing;
        menu.Update(0);
        Assert.Equal(NetScreen.Games, menu.Screen);
        menu.DrainActions();
        return menu;
    }

    static void PressTo(NetworkMenuController menu, ConnectRow row)
    {
        for (int i = 0; i < 10 && menu.Selected != (int)row; i++) menu.Key(MenuKey.Down);
    }

    static void Down(NetworkMenuController menu, int n) { for (int i = 0; i < n; i++) menu.Key(MenuKey.Down); }

    static void Select(NetworkMenuController menu, GamesRow row)
    {
        for (int i = 0; i < menu.GamesRowCount; i++)
        {
            if (menu.GamesRowAt(menu.Selected, out _) == row) return;
            menu.Key(MenuKey.Down);
        }
        Assert.Fail($"row {row} not found");
    }

    /// <summary>Moves the cursor (wrapping) onto game number <paramref name="index"/>; the default focus skips games that cannot be joined.</summary>
    static void SelectGame(NetworkMenuController menu, int index)
    {
        for (int i = 0; i < menu.GamesRowCount; i++)
        {
            if (menu.GamesRowAt(menu.Selected, out int game) == GamesRow.Game && game == index) return;
            menu.Key(MenuKey.Down);
        }
        Assert.Fail($"game {index} not found");
    }

    static void SelectCreate(NetworkMenuController menu, CreateRow row)
    {
        for (int i = 0; i < 8 && NetworkMenuController.CreateRows[menu.Selected] != row; i++) menu.Key(MenuKey.Down);
        Assert.Equal(row, NetworkMenuController.CreateRows[menu.Selected]);
    }

    /// <summary>Opens the car screen (the car row of the lobby, ENTER).</summary>
    static void OpenCar(NetworkMenuController menu)
    {
        SelectLobby(menu, LobbyRow.Car);
        menu.Key(MenuKey.Enter);
        Assert.True(menu.InCar);
    }

    /// <summary>Moves the cursor of the car screen to <paramref name="row"/>.</summary>
    static void SelectCar(NetworkMenuController menu, CarRow row)
    {
        for (int i = 0; i < 6 && menu.CarCursor != row; i++) menu.Key(MenuKey.Down);
        Assert.Equal(row, menu.CarCursor);
    }

    /// <summary>Puts the cursor on <paramref name="row"/>: a setting through the settings row (ENTER, then DOWN), an action
    /// or the car / settings row on the top level (ESC out of the settings first).</summary>
    static void SelectLobby(NetworkMenuController menu, LobbyRow row)
    {
        if (NetworkMenuController.IsSetting(row))
        {
            if (!menu.InSettings)
            {
                for (int i = 0; i < 5 && menu.LobbyRows[menu.Selected] != LobbyRow.Settings; i++) menu.Key(MenuKey.Up);
                menu.Key(MenuKey.Enter);
                Assert.True(menu.InSettings);
            }
            for (int i = 0; i < 20 && menu.LobbyRows[menu.Selected] != row; i++) menu.Key(MenuKey.Down);
        }
        else
        {
            if (menu.InSettings) menu.Key(MenuKey.Escape);
            Assert.False(menu.InSettings);
            // the vertical list of the actions (no wrap): the way to the row is up or down
            int target = menu.LobbyRows.ToList().IndexOf(row);
            for (int i = 0; i < 6 && menu.Selected != target; i++) menu.Key(menu.Selected < target ? MenuKey.Down : MenuKey.Up);
        }
        Assert.Equal(row, menu.LobbyRows[menu.Selected]);
    }

    static NetworkMenuController InLobby(out FakeView view, bool op = true)
    {
        var menu = Connected(out view);
        view.InGame(NetGamePhase.Lobby, op);
        menu.Update(0);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        return menu;
    }

    /// <summary>A knockout game the player is operator of: <paramref name="completedRounds"/> = rounds of the series that are over
    /// (null = no series yet, the lobby before the first race).</summary>
    static NetworkMenuController KnockoutLobby(out FakeView view, int? completedRounds, bool op = true, bool finished = false, int car = 1, int colour = NetProtocol.NoColour)
    {
        var menu = Connected(out view);
        var series = completedRounds is { } rounds
            ? new NetKnockoutState(rounds, finished, -1, Enumerable.Range(0, NetProtocol.KnockoutEntrants)
                .Select(i => new NetKnockoutEntry(i == 0 ? "Me" : $"BOT {i}", i < 2 ? i : -1, true, 0, -1)).ToArray())
            : null;
        view.InGame(NetGamePhase.Lobby, op, mode: NetGameMode.Knockout, series: series, car: car, colour: colour);
        menu.Update(0);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        return menu;
    }

    static void Type(NetworkMenuController menu, string text) { foreach (char c in text) menu.Type(c); }

    // ---------------- connect ----------------

    [Fact]
    public void ConnectScreen_EditsFieldsAndRequestsConnect()
    {
        var menu = new NetworkMenuController("Driver");
        Assert.Equal(NetScreen.Connect, menu.Screen);
        menu.Key(MenuKey.Enter);                          // edit nick
        Assert.Equal(NetField.Nick, menu.Editing);
        menu.Type((char)8); Type(menu, "x");
        menu.Key(MenuKey.Enter);
        Assert.Equal("Drivex", menu.Nick);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);  // host
        for (int i = 0; i < 20; i++) menu.Type((char)8);
        Type(menu, "10.0.0.5");
        menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);  // port (digits only)
        for (int i = 0; i < 6; i++) menu.Type((char)8);
        Type(menu, "x2y7z");
        Type(menu, "00");
        menu.Key(MenuKey.Enter);
        Assert.Equal("2700", menu.PortText);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);  // connect
        Assert.Equal(NetScreen.Connecting, menu.Screen);
        Assert.Equal([new NetMenuAction.Connect("Drivex", "10.0.0.5", 2700)], menu.DrainActions());
    }

    [Theory]
    [InlineData("", "host", "27100")]
    [InlineData("Me", "  ", "27100")]
    [InlineData("Me", "host", "")]
    [InlineData("Me", "host", "70000")]
    [InlineData("Me", "host", "0")]
    public void ConnectScreen_InvalidInputShowsMessageAndSendsNothing(string nick, string host, string port)
    {
        var menu = new NetworkMenuController(nick, host, 1);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);
        for (int i = 0; i < 6; i++) menu.Type((char)8);
        Type(menu, port);
        menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        Assert.True(menu.MessageIsError);
        Assert.NotNull(menu.Message);
        Assert.Empty(menu.DrainActions());
    }

    [Fact]
    public void ConnectScreen_BackLeavesToMainMenu()
    {
        var menu = new NetworkMenuController("Me");
        menu.Key(MenuKey.Escape);
        Assert.Equal([new NetMenuAction.ExitToMainMenu()], menu.DrainActions());
        var menu2 = new NetworkMenuController("Me");
        PressTo(menu2, ConnectRow.Back);
        menu2.Key(MenuKey.Enter);
        Assert.Equal([new NetMenuAction.ExitToMainMenu()], menu2.DrainActions());
    }

    [Fact]
    public void Connecting_EscapeAndBackspaceCancel()
    {
        foreach (bool backspace in new[] { false, true })
        {
            var menu = new NetworkMenuController("Me");
            PressTo(menu, ConnectRow.Connect); menu.Key(MenuKey.Enter);
            var view = new FakeView();
            menu.Attach(view);
            menu.DrainActions();
            if (backspace) NetworkMenuInput.Apply(menu, new DesktopInput(default, [], false, Typed: [(char)8]));
            else NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Escape], false));
            Assert.Equal(NetScreen.Connect, menu.Screen);
            Assert.Contains("disconnect", view.Calls);
            Assert.Equal([new NetMenuAction.CancelConnect()], menu.DrainActions());
            menu.Update(1);   // the cancelled client is no longer followed
            Assert.Equal(NetScreen.Connect, menu.Screen);
        }
    }

    [Theory]
    [InlineData(NetRejectReason.NameTaken, "nick")]
    [InlineData(NetRejectReason.ResumeTokenInvalid, "seat")]
    [InlineData(NetRejectReason.AppVersionMismatch, "version")]
    [InlineData(NetRejectReason.ProtocolMismatch, "protocol")]
    [InlineData(NetRejectReason.PhysicsMismatch, "physics")]
    [InlineData(NetRejectReason.DataMismatch, "game data")]
    [InlineData(NetRejectReason.Timeout, "unreachable")]
    public void Handshake_RejectionShowsReasonAndAllowsRetry(NetRejectReason reason, string fragment)
    {
        var menu = new NetworkMenuController("Me");
        PressTo(menu, ConnectRow.Connect); menu.Key(MenuKey.Enter);
        var view = new FakeView { Phase = SessionClientPhase.Rejected, LastReject = new NetReject(reason) };
        menu.Attach(view);
        menu.DrainActions();
        menu.Update(0);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        Assert.True(menu.MessageIsError);
        Assert.Contains(fragment, menu.Message, StringComparison.OrdinalIgnoreCase);
        // the cursor stands on the entry to correct (the nick for a taken nick, the server for an unreachable one), else on CONNECT
        Assert.Equal(reason switch { NetRejectReason.NameTaken => ConnectRow.Nick, NetRejectReason.Timeout => ConnectRow.Host, _ => ConnectRow.Connect },
            (ConnectRow)menu.Selected);
        // retry with the same data
        PressTo(menu, ConnectRow.Connect);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Connecting, menu.Screen);
        Assert.Single(menu.DrainActions().OfType<NetMenuAction.Connect>());
    }

    [Fact]
    public void VersionRejections_EachNameWhatDiffers_WithBothSidesValues()
    {
        // four checks of the handshake, four texts: a player with another install learns it is the game data, not the port
        var app = new Version(0, 2, 9, 0);
        var texts = new[]
        {
            NetworkMenuController.DescribeReject(new NetReject(NetRejectReason.ProtocolMismatch, ServerVersion: 7)),
            NetworkMenuController.DescribeReject(new NetReject(NetRejectReason.AppVersionMismatch, ServerAppVersion: app)),
            NetworkMenuController.DescribeReject(new NetReject(NetRejectReason.PhysicsMismatch, ServerPhysics: 99)),
            NetworkMenuController.DescribeReject(new NetReject(NetRejectReason.DataMismatch)),
        };
        Assert.Equal(4, texts.Distinct().Count());
        Assert.Equal($"Other network protocol: server 7, you {NetProtocol.Version}", texts[0]);
        Assert.Equal($"Other version: server 0.2.9.0, you {BuildInfo.AppVersion}", texts[1]);
        Assert.Equal($"Other physics: server 99, you {NetProtocol.PhysicsVersion}", texts[2]);
        Assert.Contains("game data", texts[3], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version", texts[3], StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- games list ----------------

    [Fact]
    public void GamesList_HasNoNameFieldAndNoJoinButton_OnlyGamesCreateRefreshAndBack()
    {
        var menu = Connected(out _, Game(1, "Alpha", NetGamePhase.Lobby), Game(2, "Beta", NetGamePhase.Race));
        Assert.Equal(NetGamePhase.Race, menu.Games[1].Phase);
        Assert.Equal([GamesRow.Game, GamesRow.Game, GamesRow.Create, GamesRow.Refresh, GamesRow.Back],
            Enumerable.Range(0, menu.GamesRowCount).Select(i => menu.GamesRowAt(i, out _)));
        Assert.Equal(GamesRow.Game, menu.GamesRowAt(menu.Selected, out int first));   // the cursor starts on the first game
        Assert.Equal(0, first);
        Assert.DoesNotContain(Enum.GetNames<GamesRow>(), n => n.Contains("Join", StringComparison.Ordinal));
        Assert.DoesNotContain(Enum.GetNames<NetField>(), n => n.Contains("Join", StringComparison.Ordinal));
        // typing on the list edits nothing
        menu.Type('x');
        Assert.False(menu.Editable);
    }

    [Fact]
    public void GamesList_EnterOnAGameJoinsItWithoutTypingItsName()
    {
        var menu = Connected(out var view, Game(1, "Alpha", NetGamePhase.Lobby), Game(2, "Beta", NetGamePhase.Lobby));
        menu.Key(MenuKey.Enter);
        Assert.Equal(["join:1"], view.Calls);
        view.Calls.Clear();
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);
        Assert.Equal(["join:2"], view.Calls);
        Assert.False(menu.Editable);
    }

    [Theory]
    [InlineData(NetGamePhase.Race)]
    [InlineData(NetGamePhase.Countdown)]
    public void GamesList_RunningGameWithoutReservationCannotBeJoined(NetGamePhase phase)
    {
        var menu = Connected(out var view, Game(1, "Run", phase));
        Assert.Equal(JoinAvailability.InProgress, menu.CanJoin(menu.Games[0]));
        SelectGame(menu, 0);
        menu.Key(MenuKey.Enter);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("join"));
        Assert.True(menu.MessageIsError);
    }

    [Fact]
    public void GamesList_FullGameCannotBeJoined()
    {
        var menu = Connected(out var view, Game(1, "Full", NetGamePhase.Lobby, players: 4, max: 4));
        Assert.Equal(JoinAvailability.Full, menu.CanJoin(menu.Games[0]));
        SelectGame(menu, 0);
        menu.Key(MenuKey.Enter);
        Assert.Empty(view.Calls);
        Assert.Contains("full", menu.Message);
    }

    [Fact]
    public void GamesList_DefaultFocusIsTheFirstJoinableGame()
    {
        var menu = Connected(out _, Game(1, "Run", NetGamePhase.Race), Game(2, "Full", NetGamePhase.Lobby, players: 4, max: 4),
            Game(3, "Open", NetGamePhase.Lobby, players: 1, max: 4), Game(4, "Open too", NetGamePhase.Lobby));
        Assert.Equal(GamesRow.Game, menu.GamesRowAt(menu.Selected, out int game));
        Assert.Equal(2, game);
        // the cursor still moves over every row: UP goes to the game that is full
        menu.Key(MenuKey.Up);
        Assert.Equal(GamesRow.Game, menu.GamesRowAt(menu.Selected, out game));
        Assert.Equal(1, game);
        // and once moved, a refreshed list does not pull it back
        menu.Update(1);
        Assert.Equal(1, menu.Selected);
    }

    [Fact]
    public void GamesList_DefaultFocusIsCreateGameWhenNothingCanBeJoined()
    {
        var menu = Connected(out _, Game(1, "Run", NetGamePhase.Race), Game(2, "Full", NetGamePhase.Lobby, players: 4, max: 4));
        Assert.Equal(GamesRow.Create, menu.GamesRowAt(menu.Selected, out _));
        var empty = Connected(out _);
        Assert.Equal(GamesRow.Create, empty.GamesRowAt(empty.Selected, out _));
    }

    [Fact]
    public void GamesList_DefaultFocusFollowsTheListUntilTheCursorIsMoved()
    {
        var menu = Connected(out var view);
        Assert.Equal(GamesRow.Create, menu.GamesRowAt(menu.Selected, out _));
        // the list arrives (or is refreshed) while the cursor is still on the default row: it goes to the first joinable game
        view.Games = [Game(1, "Run", NetGamePhase.Race), Game(2, "Open", NetGamePhase.Lobby)];
        menu.Update(1);
        Assert.Equal(GamesRow.Game, menu.GamesRowAt(menu.Selected, out int game));
        Assert.Equal(1, game);
        view.Games = [Game(2, "Open", NetGamePhase.Lobby), Game(3, "More", NetGamePhase.Lobby)];
        menu.Update(2);
        menu.GamesRowAt(menu.Selected, out game);
        Assert.Equal(0, game);
        // a player who moved the cursor keeps it on the same game (by id) when the list changes
        menu.Key(MenuKey.Down);
        menu.GamesRowAt(menu.Selected, out int movedGame);
        int movedId = menu.Games[movedGame].GameId;
        view.Games = [Game(9, "Other", NetGamePhase.Lobby), .. view.Games];
        menu.Update(3);
        menu.GamesRowAt(menu.Selected, out int now);
        Assert.Equal(movedId, menu.Games[now].GameId);
    }

    [Fact]
    public void GamesList_DefaultFocusStartsAgainWhenTheScreenIsReopened()
    {
        var menu = Connected(out _, Game(1, "Open", NetGamePhase.Lobby));
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Down);
        Assert.Equal(GamesRow.Refresh, menu.GamesRowAt(menu.Selected, out _));
        SelectCreateGame(menu);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.CreateOptions, menu.Screen);
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.Games, menu.Screen);
        menu.Update(5);
        Assert.Equal(GamesRow.Game, menu.GamesRowAt(menu.Selected, out int game));
        Assert.Equal(0, game);
    }

    static void SelectCreateGame(NetworkMenuController menu) => Select(menu, GamesRow.Create);

    [Fact]
    public void GamesList_RefreshAndBack()
    {
        var menu = Connected(out var view);
        Select(menu, GamesRow.Refresh); menu.Key(MenuKey.Enter);
        Assert.Equal(["games"], view.Calls);
        menu.Key(MenuKey.Escape);
        Assert.Equal([new NetMenuAction.ExitToMainMenu()], menu.DrainActions());
        Assert.Contains("disconnect", view.Calls);
    }

    [Fact]
    public void GamesList_RefusedJoinShowsErrorAndInputIsBlockedWhilePending()
    {
        var menu = Connected(out var view, Game(1, "Alpha", NetGamePhase.Lobby));
        menu.Key(MenuKey.Enter);
        view.Pending = true;
        menu.Key(MenuKey.Escape);                     // ignored while waiting for the answer
        Assert.Empty(menu.DrainActions());
        view.Pending = false;
        view.LastError = new SessionError(SessionErrorKind.JoinRefused, "x");
        menu.Update(1);
        Assert.Equal(NetScreen.Games, menu.Screen);
        Assert.True(menu.MessageIsError);
    }

    // ---------------- create options ----------------

    [Fact]
    public void CreateOptions_OnlyNameModeAndPlayerLimitAreOffered()
    {
        Assert.Equal([CreateRow.Name, CreateRow.MaxPlayers, CreateRow.Mode, CreateRow.Create, CreateRow.Back], NetworkMenuController.CreateRows);
    }

    [Fact]
    public void CreateOptions_DefaultsCreateGameAndEnterLobbyAsOperator()
    {
        var menu = Connected(out var view);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.CreateOptions, menu.Screen);
        Assert.Equal("Me's game", menu.CreateName);
        SelectCreate(menu, CreateRow.Create); menu.Key(MenuKey.Enter);
        Assert.Equal(["create"], view.Calls);
        var r = view.Created!;
        Assert.Equal(new NetGameCreate("Me's game", NetworkMenuController.GridCars, NetGameMode.SingleRace), r);
        view.Pending = true; menu.Update(1);
        Assert.Equal(NetScreen.CreateOptions, menu.Screen);
        view.Pending = false;
        view.InGame(NetGamePhase.Lobby, op: true);
        menu.Update(2);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.True(menu.IsOperator);
    }

    [Fact]
    public void CreateOptions_ChangesNameLimitAndModeAndSendsThem()
    {
        var menu = Connected(out var view);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Enter); Type(menu, "!"); menu.Key(MenuKey.Enter);   // name edit
        SelectCreate(menu, CreateRow.MaxPlayers); menu.Key(MenuKey.Left); menu.Key(MenuKey.Left); menu.Key(MenuKey.Left);   // 8 -> 5
        SelectCreate(menu, CreateRow.Mode); menu.Key(MenuKey.Right);
        SelectCreate(menu, CreateRow.Create); menu.Key(MenuKey.Enter);
        Assert.Equal(new NetGameCreate("Me's game!", 5, NetGameMode.Knockout), view.Created);
    }

    [Fact]
    public void CreateOptions_PlayerLimitStaysWithinOneToEight()
    {
        var menu = Connected(out _);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        SelectCreate(menu, CreateRow.MaxPlayers);
        for (int i = 0; i < 10; i++) menu.Key(MenuKey.Left);
        Assert.Equal(NetProtocol.MinSeats, menu.MaxPlayers);
        for (int i = 0; i < 10; i++) menu.Key(MenuKey.Right);
        Assert.Equal(NetProtocol.MaxSeats, menu.MaxPlayers);
    }

    [Fact]
    public void CreateOptions_BackReturnsToListWithoutCreating()
    {
        var menu = Connected(out var view);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.Games, menu.Screen);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        SelectCreate(menu, CreateRow.Back); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Games, menu.Screen);
        Assert.DoesNotContain("create", view.Calls);
    }

    [Fact]
    public void CreateOptions_EmptyNameIsRefusedLocally_ServerRefusalShowsMessage()
    {
        var menu = Connected(out var view);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Enter);
        for (int i = 0; i < 30; i++) menu.Type((char)8);
        menu.Key(MenuKey.Enter);
        SelectCreate(menu, CreateRow.Create); menu.Key(MenuKey.Enter);
        Assert.Empty(view.Calls);
        Assert.True(menu.MessageIsError);
        SelectCreate(menu, CreateRow.Name); menu.Key(MenuKey.Enter); Type(menu, "Dup"); menu.Key(MenuKey.Enter);
        SelectCreate(menu, CreateRow.Create); menu.Key(MenuKey.Enter);
        Assert.Equal(["create"], view.Calls);
        view.LastError = new SessionError(SessionErrorKind.CreateRefused, "x");
        menu.Update(1);
        Assert.Equal(NetScreen.CreateOptions, menu.Screen);
        Assert.True(menu.MessageIsError);
    }

    // ---------------- lobby ----------------

    [Fact]
    public void Lobby_NonOperatorHasNoStartAndNoOptionRows()
    {
        var menu = InLobby(out var view, op: false);
        Assert.DoesNotContain(LobbyRow.Start, menu.LobbyRows);
        Assert.DoesNotContain(LobbyRow.Track, menu.LobbyRows);
        Assert.DoesNotContain(LobbyRow.Style, menu.LobbyRows);
        Assert.Equal([LobbyRow.Car, LobbyRow.Ready, LobbyRow.Leave], menu.LobbyRows);
        Assert.False(menu.SettingsAvailable);
        Assert.False(menu.IsOperator);
        SelectLobby(menu, LobbyRow.Ready); menu.Key(MenuKey.Enter);
        OpenCar(menu); menu.Key(MenuKey.Right); menu.Key(MenuKey.Left);
        Assert.Equal(["ready:True", "car:2", "car:0"], view.Calls);
    }

    [Fact]
    public void Lobby_OperatorChangesOptionsAndStarts()
    {
        var menu = InLobby(out var view);
        Assert.Equal(LobbyRow.Start, menu.LobbyRows[^2]);
        SelectLobby(menu, LobbyRow.Track); menu.Key(MenuKey.Right);
        SelectLobby(menu, LobbyRow.Direction); menu.Key(MenuKey.Right);
        SelectLobby(menu, LobbyRow.Laps); menu.Key(MenuKey.Left);
        SelectLobby(menu, LobbyRow.Start); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmStart, menu.Screen);          // Bob is not ready
        menu.Key(MenuKey.Right); menu.Key(MenuKey.Enter);
        Assert.Equal(["opts:3:3:False", "opts:2:3:True", "opts:2:2:False", "start"], view.Calls);
    }

    [Fact]
    public void Lobby_OperatorOfASingleRaceCanChangeEveryOptionBeforeEachStart()
    {
        var menu = InLobby(out var view);   // track 2, laps 3, forward, bots auto, no traffic, expert, catch-up on, Wild
        foreach (var row in new[] { LobbyRow.Track, LobbyRow.Direction, LobbyRow.Laps, LobbyRow.Bots, LobbyRow.Skill, LobbyRow.CatchUp,
                     LobbyRow.Traffic, LobbyRow.Style })
            Assert.Equal(OptionAccess.Editable, menu.Access(row));
        SelectLobby(menu, LobbyRow.Bots); menu.Key(MenuKey.Right);
        Assert.Equal(0, view.LastOptions!.Value.Bots);              // auto -> 0
        SelectLobby(menu, LobbyRow.Skill); menu.Key(MenuKey.Right);
        Assert.Equal(0, view.LastOptions!.Value.Skill);             // expert -> beginner (wraps)
        SelectLobby(menu, LobbyRow.CatchUp); menu.Key(MenuKey.Enter);
        Assert.False(view.LastOptions!.Value.CatchUp);
        SelectLobby(menu, LobbyRow.Traffic); menu.Key(MenuKey.Enter);
        Assert.True(view.LastOptions!.Value.Traffic);
        SelectLobby(menu, LobbyRow.Style); menu.Key(MenuKey.Right);
        Assert.Equal(2, view.LastOptions!.Value.Style);             // Wild -> Arcade
        // after a race the lobby is editable again: nothing is frozen in a single race
        view.InGame(NetGamePhase.Race); menu.Update(1); view.ResultsId = 3;
        view.InGame(NetGamePhase.Lobby); menu.Update(2);
        menu.Key(MenuKey.Enter);                                    // acknowledge the summary
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.Equal(OptionAccess.Editable, menu.Access(LobbyRow.Track));
        Assert.Equal(OptionAccess.Editable, menu.Access(LobbyRow.Style));
    }

    [Fact]
    public void Lobby_EachSendCarriesTheOtherOptionsOfTheLobbyUnchanged()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, track: 4, laps: 5, backward: true, bots: 3, traffic: true, skill: 0, catchUp: false, style: 2);
        menu.Update(0);
        SelectLobby(menu, LobbyRow.Laps); menu.Key(MenuKey.Right);
        Assert.Equal((4, 6, true, 3, true, 0, false, 2), view.LastOptions);
    }

    [Fact]
    public void Lobby_PhysicsStyleCyclesThroughSimulationWildAndArcade()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, style: 0);
        menu.Update(0);
        SelectLobby(menu, LobbyRow.Style);
        var sent = new List<int>();
        foreach (int style in new[] { 0, 1, 2 })
        {
            view.InGame(NetGamePhase.Lobby, style: style);
            menu.Update(0);
            menu.Key(MenuKey.Right);
            sent.Add(view.LastOptions!.Value.Style);
        }
        Assert.Equal([1, 2, 0], sent);                              // the three modes, wrapping around
        menu.Key(MenuKey.Left);
        Assert.Equal(1, view.LastOptions!.Value.Style);
    }

    [Fact]
    public void Lobby_BotsCycleThroughAutoAndTheCountThatFitsTheGrid()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby);                            // four players max: 0..4 bots, or auto
        menu.Update(0);
        SelectLobby(menu, LobbyRow.Bots);
        var seen = new List<int>();
        for (int i = 0; i < 7; i++)
        {
            menu.Key(MenuKey.Right);
            int bots = view.LastOptions!.Value.Bots;
            seen.Add(bots);
            view.InGame(NetGamePhase.Lobby, bots: bots);
            menu.Update(0);
        }
        Assert.Equal([0, 1, 2, 3, 4, -1, 0], seen);
    }

    [Fact]
    public void Lobby_KnockoutBeforeTheFirstRaceTrackIsReadOnlyBotsAndTrafficAreNotApplicable()
    {
        var menu = KnockoutLobby(out var view, completedRounds: null);
        Assert.False(menu.SeriesFrozen);
        Assert.Equal(OptionAccess.ReadOnly, menu.Access(LobbyRow.Track));
        Assert.Equal(OptionAccess.NotApplicable, menu.Access(LobbyRow.Bots));
        Assert.Equal(OptionAccess.NotApplicable, menu.Access(LobbyRow.Traffic));
        foreach (var row in new[] { LobbyRow.Direction, LobbyRow.Laps, LobbyRow.Skill, LobbyRow.CatchUp, LobbyRow.Style, LobbyRow.Car })
            Assert.Equal(OptionAccess.Editable, menu.Access(row));
        // the read-only and n/a rows are skipped by the cursor and cannot be changed; in the settings it stays among them
        SelectLobby(menu, LobbyRow.Direction);
        var visited = new List<LobbyRow>();
        for (int i = 0; i < menu.LobbyRows.Count; i++) { visited.Add(menu.LobbyRows[menu.Selected]); menu.Key(MenuKey.Down); }
        Assert.All(visited, row => Assert.True(NetworkMenuController.IsSetting(row), row.ToString()));
        Assert.DoesNotContain(LobbyRow.Track, visited);
        Assert.DoesNotContain(LobbyRow.Bots, visited);
        Assert.DoesNotContain(LobbyRow.Traffic, visited);
        Assert.Contains(LobbyRow.Style, visited);
        SelectLobby(menu, LobbyRow.Skill); menu.Key(MenuKey.Right);
        Assert.Equal(0, view.LastOptions!.Value.Skill);
    }

    [Fact]
    public void Lobby_KnockoutAfterTheSeriesStartedEverythingIsReadOnly_AlsoCarAndColour()
    {
        var menu = KnockoutLobby(out var view, completedRounds: 1);
        Assert.True(menu.SeriesFrozen);
        foreach (var row in new[] { LobbyRow.Track, LobbyRow.Direction, LobbyRow.Laps, LobbyRow.Skill, LobbyRow.CatchUp, LobbyRow.Style,
                     LobbyRow.Car })
            Assert.Equal(OptionAccess.ReadOnly, menu.Access(row));
        Assert.Equal(OptionAccess.ReadOnly, menu.CarAccess(CarRow.Car));
        Assert.Equal(OptionAccess.ReadOnly, menu.CarAccess(CarRow.Colour));
        Assert.Equal(OptionAccess.NotApplicable, menu.Access(LobbyRow.Bots));
        Assert.Equal(OptionAccess.NotApplicable, menu.Access(LobbyRow.Traffic));
        // only ready, start and leave remain reachable: the car and settings rows are dimmed and skipped, ENTER on them does nothing
        Assert.False(menu.SettingsAvailable);
        Assert.Equal(OptionAccess.ReadOnly, menu.Access(LobbyRow.Settings));
        var visited = new List<LobbyRow>();
        for (int i = 0; i < 4; i++)
        {
            menu.Key(MenuKey.Up); visited.Add(menu.LobbyRows[menu.Selected]);
            menu.Key(MenuKey.Right); visited.Add(menu.LobbyRows[menu.Selected]);
        }
        for (int i = 0; i < 4; i++) { menu.Key(MenuKey.Down); visited.Add(menu.LobbyRows[menu.Selected]); }
        Assert.Equal([LobbyRow.Ready, LobbyRow.Start, LobbyRow.Leave], visited.Distinct().Order());
        Assert.False(menu.InSettings);
        Assert.False(menu.InCar);
        menu.Key(MenuKey.Left); menu.Key(MenuKey.Right);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("opts") || c.StartsWith("car"));
        // a finished series is a new start: everything but the track and the n/a rows is open again
        var over = KnockoutLobby(out _, completedRounds: 8, finished: true);
        Assert.False(over.SeriesFrozen);
        Assert.Equal(OptionAccess.Editable, over.Access(LobbyRow.Style));
        Assert.Equal(OptionAccess.Editable, over.Access(LobbyRow.Car));
    }

    [Fact]
    public void Lobby_KnockoutPlayerThatIsNotTheOperatorAlsoSeesTheCarFrozenAfterTheStart()
    {
        var menu = KnockoutLobby(out var view, completedRounds: 2, op: false);
        Assert.Equal(OptionAccess.ReadOnly, menu.Access(LobbyRow.Car));
        Assert.Equal(OptionAccess.ReadOnly, menu.CarAccess(CarRow.Colour));
        menu.Key(MenuKey.Right);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Right);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("car"));
    }

    [Fact]
    public void Lobby_ACursorOnARowThatGetsFrozenLeavesItOnTheNextKey()
    {
        var menu = KnockoutLobby(out var view, completedRounds: null);
        SelectLobby(menu, LobbyRow.Style);
        view.InGame(NetGamePhase.Lobby, mode: NetGameMode.Knockout, series: new NetKnockoutState(1, false, -1,
            Enumerable.Range(0, NetProtocol.KnockoutEntrants).Select(i => new NetKnockoutEntry("E" + i, i < 2 ? i : -1, true, 0, -1)).ToArray()));
        menu.Update(1);
        Assert.False(menu.InSettings);                                   // nothing left to change: back on the top level
        Assert.Equal(LobbyRow.Start, menu.LobbyRows[menu.Selected]);     // after the settings: on RACE
        view.Calls.Clear();
        menu.Key(MenuKey.Right);
        Assert.Empty(view.Calls);
        Assert.True(menu.Access(menu.LobbyRows[menu.Selected]) == OptionAccess.Editable);
    }

    [Fact]
    public void Lobby_VerticalActionListStartsOnReadyAndDoesNotWrap()
    {
        var menu = InLobby(out _);
        Assert.False(menu.InSettings);
        LobbyRow At() => menu.LobbyRows[menu.Selected];
        Assert.Equal(LobbyRow.Ready, At());
        menu.Key(MenuKey.Up);
        Assert.Equal(LobbyRow.Settings, At());
        menu.Key(MenuKey.Up);
        Assert.Equal(LobbyRow.Car, At());                                // car above settings
        menu.Key(MenuKey.Up);                                            // no wrap at the top
        Assert.Equal(LobbyRow.Car, At());
        menu.Key(MenuKey.Down); Assert.Equal(LobbyRow.Settings, At());
        menu.Key(MenuKey.Down); Assert.Equal(LobbyRow.Ready, At());
        menu.Key(MenuKey.Down); Assert.Equal(LobbyRow.Start, At());
        menu.Key(MenuKey.Down); Assert.Equal(LobbyRow.Leave, At());
        menu.Key(MenuKey.Down);                                          // nor at the bottom
        Assert.Equal(LobbyRow.Leave, At());
        menu.Key(MenuKey.Left); Assert.Equal(LobbyRow.Start, At());      // LEFT/RIGHT as before: along the actions
        menu.Key(MenuKey.Right); Assert.Equal(LobbyRow.Leave, At());
        menu.Key(MenuKey.Right); Assert.Equal(LobbyRow.Leave, At());
    }

    [Fact]
    public void Lobby_PlayerThatIsNotTheOperatorHasTheListWithoutStart()
    {
        var menu = InLobby(out _, op: false);
        LobbyRow At() => menu.LobbyRows[menu.Selected];
        Assert.Equal(LobbyRow.Ready, At());
        menu.Key(MenuKey.Down); Assert.Equal(LobbyRow.Leave, At());
        menu.Key(MenuKey.Up); menu.Key(MenuKey.Up); Assert.Equal(LobbyRow.Car, At());   // no settings row for a player
        Assert.DoesNotContain(LobbyRow.Settings, menu.LobbyRows);
    }

    [Fact]
    public void Lobby_AfterChangingTheSettingsStartRaceIsOneKeyAway()
    {
        var menu = InLobby(out var view);
        menu.Key(MenuKey.Up); menu.Key(MenuKey.Enter);
        Assert.True(menu.InSettings);
        Assert.Equal(LobbyRow.Track, menu.LobbyRows[menu.Selected]);     // the first option; the car is not among them
        SelectLobby(menu, LobbyRow.Style);
        menu.Key(MenuKey.Right);
        Assert.Single(view.Calls, c => c.StartsWith("opts"));
        // UP/DOWN in the settings never leave them
        for (int i = 0; i < menu.LobbyRows.Count; i++)
        {
            menu.Key(MenuKey.Down);
            Assert.True(NetworkMenuController.IsSetting(menu.LobbyRows[menu.Selected]));
        }
        menu.Key(MenuKey.Escape);                                        // back on RACE, no exit question
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.False(menu.InSettings);
        Assert.Equal(LobbyRow.Start, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Right); menu.Key(MenuKey.Enter);            // Bob is not ready: "start anyway"
        Assert.Equal("start", view.Calls[^1]);
    }

    [Fact]
    public void Lobby_EscOnTheTopLevelAsksToLeave()
    {
        var menu = InLobby(out _);
        menu.Key(MenuKey.Up);
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
    }

    [Fact]
    public void Lobby_APlayerHasNoSettingsToEditAndStillOpensTheCarScreen()
    {
        var menu = InLobby(out var view, op: false);
        Assert.False(menu.SettingsAvailable);
        Assert.DoesNotContain(menu.LobbyRows, NetworkMenuController.IsSetting);
        menu.Key(MenuKey.Up); Assert.Equal(LobbyRow.Car, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Enter);
        Assert.True(menu.InCar);
        Assert.False(menu.InSettings);
        menu.Key(MenuKey.Escape);
        Assert.False(menu.InCar);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.Equal(LobbyRow.Car, menu.LobbyRows[menu.Selected]);        // back on the car row, no exit question
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);
        Assert.Equal("ready:True", view.Calls[^1]);
    }

    [Fact]
    public void Lobby_LeavingTheLobbyClosesTheSettings()
    {
        var menu = InLobby(out var view);
        SelectLobby(menu, LobbyRow.Laps);
        view.InGame(NetGamePhase.Countdown, true);
        menu.Update(1);
        Assert.False(menu.InSettings);
    }

    // ---------------- car and colour ----------------

    [Fact]
    public void Lobby_CarCycleStaysWithinTheShowcaseCars()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, car: NetProtocol.MaxCarType);
        menu.Update(0);
        OpenCar(menu); menu.Key(MenuKey.Right);
        Assert.Equal(0, view.LastCar!.Value.Car);                   // wraps to the first model, never 12..14
        view.InGame(NetGamePhase.Lobby, car: 0);
        menu.Update(0);
        menu.Key(MenuKey.Left);
        Assert.Equal(11, view.LastCar!.Value.Car);
        var seen = new HashSet<int>();
        for (int car = 0; car <= NetProtocol.MaxCarType; car++)
        {
            view.InGame(NetGamePhase.Lobby, car: car);
            menu.Update(0);
            menu.Key(MenuKey.Right);
            seen.Add(view.LastCar!.Value.Car);
        }
        Assert.All(seen, c => Assert.InRange(c, 0, 11));
        Assert.Equal(12, seen.Count);
    }

    [Fact]
    public void Lobby_ColourRowCyclesThroughDefaultAndThePaletteOfTheModel()
    {
        const int car = 3;     // six colours from 0x42
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, car: car);
        menu.Update(0);
        Assert.Equal(6, NetworkMenuController.PaletteSize(car));
        Assert.Equal(0x42, NetworkMenuController.PaletteFirst(car));
        OpenCar(menu); SelectCar(menu, CarRow.Colour);
        var seen = new List<int>();
        int colour = NetProtocol.NoColour;
        for (int i = 0; i < 8; i++)
        {
            menu.Key(MenuKey.Right);
            Assert.Equal(car, view.LastCar!.Value.Car);
            colour = view.LastCar.Value.Colour;
            seen.Add(colour);
            view.InGame(NetGamePhase.Lobby, car: car, colour: colour);
            menu.Update(0);
        }
        Assert.Equal([0x42, 0x43, 0x44, 0x45, 0x46, 0x47, NetProtocol.NoColour, 0x42], seen);   // default, then the palette, wrapping
        menu.Key(MenuKey.Left);
        Assert.Equal(NetProtocol.NoColour, view.LastCar!.Value.Colour);
    }

    [Fact]
    public void Lobby_ModelWithASingleColourShowsItFixedAndSendsNoColour()
    {
        const int car = 4;     // table count 0: one colour
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, car: car);
        menu.Update(0);
        Assert.Equal(1, NetworkMenuController.PaletteSize(car));
        Assert.Equal(OptionAccess.ReadOnly, menu.CarAccess(CarRow.Colour));
        OpenCar(menu);
        var visited = new List<CarRow>();
        for (int i = 0; i < NetworkMenuController.CarRows.Count; i++) { visited.Add(menu.CarCursor); menu.Key(MenuKey.Down); }
        Assert.DoesNotContain(CarRow.Colour, visited);
        Assert.Equal([CarRow.Car, CarRow.Done, CarRow.Cancel, CarRow.Car], visited);
        SelectCar(menu, CarRow.Car);
        menu.Key(MenuKey.Left);                                     // to model 3: its colour is not asked for
        Assert.Equal((3, NetProtocol.NoColour), view.LastCar);
    }

    [Fact]
    public void Lobby_ChangingTheModelKeepsAColourThatTheNewModelAlsoHasOtherwiseTheDefault()
    {
        var menu = Connected(out var view);
        // model 1 (palette 0x1E..0x22) and model 4 (single colour 0x1E) share the colour 0x1E
        view.InGame(NetGamePhase.Lobby, car: 1, colour: 0x1E);
        menu.Update(0);
        OpenCar(menu);
        for (int i = 0; i < 3; i++) menu.Key(MenuKey.Right);       // the view stays on model 1: each press asks for model 2
        Assert.Equal((2, NetProtocol.NoColour), view.LastCar);      // 0x1E is not a colour of model 2
        view.InGame(NetGamePhase.Lobby, car: 3, colour: 0x1E);
        menu.Update(0);
        menu.Key(MenuKey.Right);
        Assert.Equal((4, 0x1E), view.LastCar);                      // but it is the one colour of model 4
    }

    [Fact]
    public void CarScreen_OpensFromTheCarRowAboveSettingsAndStartsOnTheCar()
    {
        var menu = InLobby(out var view);
        Assert.Equal([LobbyRow.Car, LobbyRow.Settings], menu.LobbyRows.Take(2));
        Assert.Equal(LobbyRow.Ready, menu.LobbyRows[menu.Selected]);     // the lobby still starts on ready
        OpenCar(menu);
        Assert.Equal(CarRow.Car, menu.CarCursor);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("car"));   // opening sends nothing
        menu.Key(MenuKey.Escape);
        Assert.False(menu.InCar);
        Assert.Equal(LobbyRow.Car, menu.LobbyRows[menu.Selected]);
        Assert.Equal(NetScreen.Lobby, menu.Screen);                       // ESC closes the car screen, no exit question
    }

    [Fact]
    public void CarScreen_UpDownVisitCarColourAndTheDoneCancelLineAndLeftRightSwitchBetweenThem()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, car: 3);                          // six colours
        menu.Update(0);
        OpenCar(menu);
        var seen = new List<CarRow>();
        for (int i = 0; i < 4; i++) { seen.Add(menu.CarCursor); menu.Key(MenuKey.Down); }
        Assert.Equal([CarRow.Car, CarRow.Colour, CarRow.Done, CarRow.Cancel], seen);
        Assert.Equal(CarRow.Car, menu.CarCursor);                         // wraps
        menu.Key(MenuKey.Up); Assert.Equal(CarRow.Cancel, menu.CarCursor);
        menu.Key(MenuKey.Left); Assert.Equal(CarRow.Done, menu.CarCursor);
        menu.Key(MenuKey.Right); Assert.Equal(CarRow.Cancel, menu.CarCursor);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("car"));     // done and cancel send nothing on LEFT/RIGHT
    }

    [Fact]
    public void CarScreen_DoneKeepsTheChoiceAndReturnsToTheCarRow()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, car: 3);
        menu.Update(0);
        OpenCar(menu);
        menu.Key(MenuKey.Right);                                          // model 4
        SelectCar(menu, CarRow.Done);
        menu.Key(MenuKey.Enter);
        Assert.False(menu.InCar);
        Assert.Equal(LobbyRow.Car, menu.LobbyRows[menu.Selected]);
        Assert.Equal(["car:4"], view.Calls);
    }

    [Fact]
    public void CarScreen_CancelAndEscRestoreTheCarAndColourFromBeforeTheScreen()
    {
        foreach (bool byEsc in new[] { false, true })
        {
            var menu = Connected(out var view);
            view.InGame(NetGamePhase.Lobby, car: 3, colour: 0x44);
            menu.Update(0);
            OpenCar(menu);
            menu.Key(MenuKey.Right);                                      // asks for model 4
            view.InGame(NetGamePhase.Lobby, car: 4, colour: NetProtocol.NoColour);
            menu.Update(0);
            if (byEsc) menu.Key(MenuKey.Escape);
            else { SelectCar(menu, CarRow.Cancel); menu.Key(MenuKey.Enter); }
            Assert.False(menu.InCar);
            Assert.Equal(LobbyRow.Car, menu.LobbyRows[menu.Selected]);
            Assert.Equal((3, 0x44), view.LastCar);                        // the old model and colour are sent back
        }
    }

    [Fact]
    public void CarScreen_CancelWithoutAChangeSendsNothing()
    {
        var menu = InLobby(out var view);
        OpenCar(menu);
        menu.Key(MenuKey.Escape);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("car"));
    }

    [Fact]
    public void CarScreen_EnterOnTheCarOrColourRowAdvancesLikeRight()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, car: 3);
        menu.Update(0);
        OpenCar(menu);
        menu.Key(MenuKey.Enter);
        Assert.Equal((4, NetProtocol.NoColour), view.LastCar);
        view.InGame(NetGamePhase.Lobby, car: 3);
        menu.Update(0);
        SelectCar(menu, CarRow.Colour); menu.Key(MenuKey.Enter);
        Assert.Equal((3, 0x42), view.LastCar);
    }

    [Fact]
    public void CarScreen_FrozenKnockoutHasNoCarScreenAndClosesWhenTheSeriesStarts()
    {
        var frozen = KnockoutLobby(out var view, completedRounds: 1);
        frozen.Key(MenuKey.Enter);                                        // on ready: toggles ready, no car screen
        Assert.False(frozen.InCar);
        var menu = KnockoutLobby(out var view2, completedRounds: null);
        OpenCar(menu);
        view2.InGame(NetGamePhase.Lobby, mode: NetGameMode.Knockout, series: new NetKnockoutState(1, false, -1,
            Enumerable.Range(0, NetProtocol.KnockoutEntrants).Select(i => new NetKnockoutEntry("E" + i, i < 2 ? i : -1, true, 0, -1)).ToArray()));
        menu.Update(1);
        Assert.False(menu.InCar);
        Assert.Equal(LobbyRow.Ready, menu.LobbyRows[menu.Selected]);
    }

    [Fact]
    public void CarScreen_IsClosedWhenTheCountdownStarts()
    {
        var menu = InLobby(out var view);
        OpenCar(menu);
        view.InGame(NetGamePhase.Countdown, true);
        menu.Update(1);
        Assert.False(menu.InCar);
        Assert.Equal(NetScreen.Countdown, menu.Screen);
    }

    [Fact]
    public void Lobby_ReadyReflectsServerStateAndTogglesBack()
    {
        var menu = Connected(out var view);
        view.InGame(NetGamePhase.Lobby, ready: true);
        menu.Update(0);
        SelectLobby(menu, LobbyRow.Ready); menu.Key(MenuKey.Enter);
        Assert.Equal(["ready:False"], view.Calls);
    }

    [Fact]
    public void Lobby_RefusedOperationShowsTheClientsReason()
    {
        var menu = InLobby(out var view);
        view.Accept = false;
        view.LastError = new SessionError(SessionErrorKind.NotOperator, "only the operator can do this");
        SelectLobby(menu, LobbyRow.Start); menu.Key(MenuKey.Enter);
        menu.Key(MenuKey.Right); menu.Key(MenuKey.Enter);            // Bob is not ready: "start anyway"
        Assert.Equal("only the operator can do this", menu.Message);
        Assert.True(menu.MessageIsError);
    }

    [Fact]
    public void Lobby_ExitNeedsConfirmationAndLeavesToTheList()
    {
        var menu = InLobby(out var view);
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        Assert.False(menu.ConfirmYes);
        menu.Key(MenuKey.Enter);                      // default "no"
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain("leave", view.Calls);
        SelectLobby(menu, LobbyRow.Leave); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        menu.Key(MenuKey.Escape);                     // escape = no
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        menu.Key(MenuKey.Escape); menu.Key(MenuKey.Left); menu.Key(MenuKey.Enter);
        Assert.Contains("leave", view.Calls);
        Assert.Equal(NetScreen.Games, menu.Screen);
        // leaving a lobby frees the seat: no reservation
        view.Phase = SessionClientPhase.Browsing; view.Lobby = null; view.Games = [Game(7, "Race", NetGamePhase.Lobby)];
        menu.Update(1);
        Assert.False(menu.ReservedFor(7));
    }

    [Fact]
    public void ExitConfirmation_DefaultsToNoAndEveryArrowSwitchesTheAnswer()
    {
        foreach (var key in new[] { MenuKey.Up, MenuKey.Down, MenuKey.Left, MenuKey.Right })
        {
            var menu = InLobby(out var view);
            menu.Key(MenuKey.Escape);
            Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
            Assert.False(menu.ConfirmYes);               // the answer is "no" until the player says otherwise
            menu.Key(key);
            Assert.True(menu.ConfirmYes);                // vertical and horizontal keys alike
            menu.Key(key);
            Assert.False(menu.ConfirmYes);               // and back
            menu.Key(key);
            menu.Key(MenuKey.Enter);
            Assert.Contains("leave", view.Calls);
            Assert.Equal(NetScreen.Games, menu.Screen);
        }
    }

    [Fact]
    public void ExitConfirmation_NoOrEscapeStaysAndMixedKeysEndOnTheLastAnswer()
    {
        var menu = InLobby(out var view);
        menu.Key(MenuKey.Escape);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Right);   // yes, then no again
        Assert.False(menu.ConfirmYes);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain("leave", view.Calls);
        menu.Key(MenuKey.Escape);
        Assert.False(menu.ConfirmYes);                     // a new window starts at "no" again
        menu.Key(MenuKey.Up);
        Assert.True(menu.ConfirmYes);
        menu.Key(MenuKey.Escape);                          // escape = no, whatever was selected
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain("leave", view.Calls);
        menu.Key(MenuKey.Escape);
        Assert.False(menu.ConfirmYes);
    }

    [Fact]
    public void Lobby_FollowsOperatorHandoverLive()
    {
        var menu = InLobby(out var view, op: false);
        Assert.DoesNotContain(LobbyRow.Start, menu.LobbyRows);
        view.Lobby = view.Lobby! with
        {
            Members = [new NetLobbyMember(0, "Op", false, 0, false, false), new NetLobbyMember(1, "Me", false, 1, true, true)],
        };
        menu.Update(1);
        Assert.Contains(LobbyRow.Start, menu.LobbyRows);
    }

    // ---------------- countdown, race, summary ----------------

    [Fact]
    public void Start_GoesThroughCountdownToRaceAndEmitsEnterRace()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Countdown);
        menu.Update(1);
        Assert.Equal(NetScreen.Countdown, menu.Screen);
        Assert.Equal(5000, menu.Lobby!.CountdownMillis);
        menu.Key(MenuKey.Right);                      // last car change
        Assert.Equal(["car:2"], view.Calls);
        Assert.Empty(menu.DrainActions());
        view.InGame(NetGamePhase.Race);
        menu.Update(2);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.Equal([new NetMenuAction.EnterRace()], menu.DrainActions());
    }

    [Fact]
    public void Race_FinishShowsSummaryAndReturnGoesToLobby()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1); menu.DrainActions();
        view.ResultsId = 5;
        view.InGame(NetGamePhase.Lobby);              // the server is already back in the lobby
        menu.Update(2);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        Assert.Equal([new NetMenuAction.LeaveRace()], menu.DrainActions());
        Assert.False(menu.Me!.Ready);                 // not ready on the summary
        menu.Key(MenuKey.Enter);                      // return to lobby
        Assert.Equal(["return"], view.Calls);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("ready"));
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        menu.Update(3);
        Assert.Equal(NetScreen.Lobby, menu.Screen);   // acknowledged results do not bring the summary back
    }

    [Fact]
    public void Summary_ExitNeedsConfirmationAndLeaves()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1);
        view.ResultsId = 5; view.InGame(NetGamePhase.Lobby); menu.Update(2);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        menu.Key(MenuKey.Right); menu.Key(MenuKey.Enter);
        Assert.Equal(["leave"], view.Calls);
        Assert.Equal(NetScreen.Games, menu.Screen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Summary_OperatorStartForcesCountdownWithoutAsking(bool confirmOpen)
    {
        var menu = InLobby(out var view, op: false);
        view.InGame(NetGamePhase.Race, op: false); menu.Update(1);
        view.ResultsId = 5; view.InGame(NetGamePhase.Lobby, op: false); menu.Update(2);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        if (confirmOpen) { menu.Key(MenuKey.Escape); Assert.Equal(NetScreen.ConfirmExit, menu.Screen); }
        view.InGame(NetGamePhase.Countdown, op: false);   // the operator started the next race
        menu.Update(3);
        Assert.Equal(NetScreen.Countdown, menu.Screen);
        Assert.Empty(view.Calls);                         // nothing was answered or sent by the client
        view.ResultsId = 0;
        view.InGame(NetGamePhase.Race, op: false);
        menu.Update(4);
        Assert.Equal(NetScreen.Race, menu.Screen);
    }

    [Fact]
    public void RaceWithoutResultsThatEndsReturnsStraightToLobby()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1); menu.DrainActions();
        view.InGame(NetGamePhase.Lobby); menu.Update(2);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.Equal([new NetMenuAction.LeaveRace()], menu.DrainActions());
    }

    // ---------------- leaving a race and coming back ----------------

    [Theory]
    [InlineData(NetGamePhase.Race)]
    [InlineData(NetGamePhase.Countdown)]
    public void LeavingARace_GoesToTheListAndTheGameCanBeRejoinedInTheGraceWindow(NetGamePhase phase)
    {
        var menu = InLobby(out var view);
        view.InGame(phase, op: phase != NetGamePhase.Countdown); menu.Update(10);   // the operator's ESC cancels the countdown
        menu.DrainActions();
        menu.Key(MenuKey.Escape);                     // pause/exit request
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        menu.Key(MenuKey.Left); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Games, menu.Screen);
        Assert.Contains("leave", view.Calls);
        if (phase == NetGamePhase.Race) Assert.Equal([new NetMenuAction.LeaveRace()], menu.DrainActions());
        view.Phase = SessionClientPhase.Browsing; view.Lobby = null;
        view.Games = [Game(7, "Race", phase)];
        menu.Update(11);
        Assert.Equal(JoinAvailability.Reserved, menu.CanJoin(menu.Games[0]));
        view.Calls.Clear();
        menu.Key(MenuKey.Enter);
        Assert.Equal(["join:7"], view.Calls);
        view.InGame(phase, op: phase != NetGamePhase.Countdown);
        menu.Update(12);
        Assert.Equal(phase == NetGamePhase.Race ? NetScreen.Race : NetScreen.Countdown, menu.Screen);
        if (phase == NetGamePhase.Race) Assert.Equal([new NetMenuAction.EnterRace()], menu.DrainActions());
    }

    [Fact]
    public void LeavingARace_ReservationExpiresWithTheWindow()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(0);
        menu.RequestExit(); menu.Key(MenuKey.Left); menu.Key(MenuKey.Enter);
        view.Phase = SessionClientPhase.Browsing; view.Lobby = null; view.Games = [Game(7, "Race", NetGamePhase.Race)];
        menu.Update(menu.ReservationMicros - 1);
        Assert.Equal(JoinAvailability.Reserved, menu.CanJoin(menu.Games[0]));
        menu.Update(menu.ReservationMicros);
        Assert.Equal(JoinAvailability.InProgress, menu.CanJoin(menu.Games[0]));
        menu.Key(MenuKey.Enter);
        Assert.DoesNotContain(view.Calls, c => c.StartsWith("join"));
    }

    [Fact]
    public void ReservationIsOnlyForTheGameThatWasLeft()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(0);
        menu.RequestExit(); menu.Key(MenuKey.Left); menu.Key(MenuKey.Enter);
        view.Phase = SessionClientPhase.Browsing; view.Lobby = null;
        view.Games = [Game(7, "Race", NetGamePhase.Race), Game(8, "Other", NetGamePhase.Race)];
        menu.Update(1);
        Assert.Equal(JoinAvailability.Reserved, menu.CanJoin(menu.Games[0]));
        Assert.Equal(JoinAvailability.InProgress, menu.CanJoin(menu.Games[1]));
    }

    [Fact]
    public void RaceScreen_HostRequestOpensTheExitConfirmationAndCancelKeepsTheRace()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1); menu.DrainActions();
        menu.RequestExit();
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        Assert.Empty(menu.DrainActions());            // the race is still shown behind the dialog
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.Empty(menu.DrainActions());
    }

    [Fact]
    public void ReconnectResumingARaceSeatOpensTheRunningRaceFromTheConnectingScreen()
    {
        var menu = new NetworkMenuController("Me");
        PressTo(menu, ConnectRow.Connect); menu.Key(MenuKey.Enter); menu.DrainActions();
        var view = new FakeView();
        menu.Attach(view);
        view.InGame(NetGamePhase.Race);               // handshake resumed the reserved seat straight into the race
        menu.Update(0);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.Equal([new NetMenuAction.EnterRace()], menu.DrainActions());
    }

    // ---------------- connection loss ----------------

    [Fact]
    public void ConnectionLoss_ReconnectingKeepsScreenAndOnlyAllowsGivingUp()
    {
        var menu = InLobby(out var view);
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(1);
        Assert.True(menu.Reconnecting);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        menu.Key(MenuKey.Enter);
        Assert.Empty(view.Calls);                     // no operations while the link is down
        view.Phase = SessionClientPhase.InGame;       // resumed
        menu.Update(2);
        Assert.False(menu.Reconnecting);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        view.Phase = SessionClientPhase.Reconnecting; menu.Update(3);
        menu.Key(MenuKey.Escape);
        Assert.False(menu.Reconnecting);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        Assert.Contains("disconnect", view.Calls);
    }

    [Fact]
    public void ConnectionLoss_RaceReconnectKeepsTheRaceAndStopsWhenGivingUp()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1); menu.DrainActions();
        view.Phase = SessionClientPhase.Reconnecting; menu.Update(2);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.Empty(menu.DrainActions());
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        Assert.Equal([new NetMenuAction.LeaveRace()], menu.DrainActions());
    }

    [Fact]
    public void ConnectionLoss_ReconnectThatLostTheLobbySeatLandsOnTheList()
    {
        var menu = InLobby(out var view);
        view.Phase = SessionClientPhase.Reconnecting; menu.Update(1);
        view.Phase = SessionClientPhase.Browsing; view.Lobby = null;
        view.Games = [Game(7, "Race", NetGamePhase.Lobby)];
        menu.Update(2);
        Assert.False(menu.Reconnecting);
        Assert.Equal(NetScreen.Games, menu.Screen);
        Assert.NotNull(menu.Message);
    }

    [Theory]
    [InlineData(SessionErrorKind.ReconnectExpired, NetRejectReason.Timeout, "reconnect")]
    [InlineData(SessionErrorKind.ConnectionLost, NetRejectReason.Timeout, "lost")]
    [InlineData(SessionErrorKind.Rejected, NetRejectReason.ServerShutdown, "shutting")]
    [InlineData(SessionErrorKind.Rejected, NetRejectReason.RaceClosed, "closed")]
    [InlineData(SessionErrorKind.Rejected, NetRejectReason.ResumeTokenInvalid, "seat")]
    public void ConnectionLoss_ClosedSessionExplainsAndReturnsToConnect(SessionErrorKind kind, NetRejectReason reason, string fragment)
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1); menu.DrainActions();
        view.Phase = SessionClientPhase.Closed;
        view.LastError = new SessionError(kind, "x", reason);
        view.LastReject = new NetReject(reason);
        menu.Update(2);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        Assert.Contains(fragment, menu.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(menu.MessageIsError);
        Assert.Equal([new NetMenuAction.LeaveRace()], menu.DrainActions());
        menu.Update(3);                               // stays put, no repeated handling
        Assert.Equal(NetScreen.Connect, menu.Screen);
        menu.Key(MenuKey.Escape);
        Assert.Equal([new NetMenuAction.ExitToMainMenu()], menu.DrainActions());
    }

    // ---------------- shared input dispatcher ----------------

    [Fact]
    public void InputDispatcher_TypedCharactersGoToTheEditedFieldOnly()
    {
        var menu = new NetworkMenuController("Me");
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Enter], false));
        Assert.True(menu.Editable);
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Down], false, Typed: ['a', 'b']));   // Down ignored while editing
        Assert.Equal("Meab", menu.Nick);
        Assert.Equal((int)ConnectRow.Nick, menu.Selected);
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Enter], false));
        Assert.False(menu.Editable);
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Down], false, Typed: ['z']));
        Assert.Equal("Meab", menu.Nick);
        Assert.Equal((int)ConnectRow.Host, menu.Selected);
    }

    [Fact]
    public void NickIsLimitedToTwelveCharacters_GameNameToTwentyFour()
    {
        var menu = new NetworkMenuController("Me");
        menu.Key(MenuKey.Enter); Type(menu, new string('x', 30)); menu.Key(MenuKey.Enter);
        Assert.Equal(NetProtocol.MaxNameLength, menu.Nick.Length);
        var m2 = Connected(out _);
        Select(m2, GamesRow.Create); m2.Key(MenuKey.Enter);
        m2.Key(MenuKey.Enter); Type(m2, new string('y', 40)); m2.Key(MenuKey.Enter);
        Assert.Equal(NetProtocol.MaxGameNameLength, m2.CreateName.Length);
    }

    [Fact]
    public void GamePadKeysNavigate_UpDownLeftRightAcceptBack()
    {
        // the pad reaches the menu as the same MenuKey commands (d-pad = arrows, A = Enter, B = Escape)
        var menu = InLobby(out var view);
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Enter], false));   // ready
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Up, MenuKey.Up, MenuKey.Enter, MenuKey.Right], false));   // settings, car, car screen: +1
        Assert.True(menu.InCar);
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Escape], false));   // back to the car row
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.False(menu.InCar);
        NetworkMenuInput.Apply(menu, new DesktopInput(default, [MenuKey.Escape], false));
        Assert.Equal(["ready:True", "car:2"], view.Calls);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
    }

    [Fact]
    public void CreateOptions_ModeSwitchesBetweenSingleRaceAndKnockout()
    {
        var menu = Connected(out var view);
        Select(menu, GamesRow.Create); menu.Key(MenuKey.Enter);
        Assert.Equal(NetGameMode.SingleRace, menu.Mode);
        SelectCreate(menu, CreateRow.Mode); menu.Key(MenuKey.Right);
        Assert.Equal(NetGameMode.Knockout, menu.Mode);
        SelectCreate(menu, CreateRow.Create); menu.Key(MenuKey.Enter);
        Assert.Equal(NetGameMode.Knockout, view.Created!.Mode);
        menu.Key(MenuKey.Escape);
    }

    [Fact]
    public void Lobby_CursorStartsOnReadyAndLeftRightWalkTheActionLine()
    {
        var menu = InLobby(out var view);
        Assert.Equal(LobbyRow.Ready, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Right);
        Assert.Equal(LobbyRow.Start, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Right);
        Assert.Equal(LobbyRow.Leave, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Right);                                     // the end of the line: stays
        Assert.Equal(LobbyRow.Leave, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Left); menu.Key(MenuKey.Left);
        Assert.Equal(LobbyRow.Ready, menu.LobbyRows[menu.Selected]);
        menu.Key(MenuKey.Left);                                      // never into the options
        Assert.Equal(LobbyRow.Ready, menu.LobbyRows[menu.Selected]);
        Assert.Empty(view.Calls);
    }

    // ---------------- countdown, cancel, not-ready warning, sounds (network-menu-look 3.7 / 3.8) ----------------

    static void SetMembers(FakeView view, Func<NetLobbyMember, NetLobbyMember> change) =>
        view.Lobby = view.Lobby! with { Members = [.. view.Lobby.Members.Select(change)] };

    [Fact]
    public void Countdown_CountsDownFromTheArrivalOfTheState()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Countdown);                          // the server says 5000 once
        menu.Update(10_000_000);
        Assert.Equal(5000, menu.CountdownRemainingMillis);
        var seconds = new List<int>();
        for (int t = 0; t <= 5; t++)
        {
            menu.Update(10_000_000 + t * 1_000_000L);
            seconds.Add((menu.CountdownRemainingMillis + 999) / 1000);
        }
        Assert.Equal([5, 4, 3, 2, 1, 0], seconds);
        menu.Update(10_000_000 + 9_000_000L);                         // never below zero
        Assert.Equal(0, menu.CountdownRemainingMillis);
    }

    [Fact]
    public void Countdown_ANewStateReanchorsTheClock()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Countdown);
        menu.Update(0);
        menu.Update(2_000_000);
        Assert.Equal(3000, menu.CountdownRemainingMillis);
        view.InGame(NetGamePhase.Countdown);                          // a changed lobby: the server states the time left again
        view.Lobby = view.Lobby! with { CountdownMillis = 2500 };
        menu.Update(3_000_000);
        Assert.Equal(2500, menu.CountdownRemainingMillis);
        menu.Update(3_500_000);
        Assert.Equal(2000, menu.CountdownRemainingMillis);
    }

    [Fact]
    public void Countdown_OperatorEscCancelsAtOnceAndTheScreenFollowsTheLobbyState()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Countdown); menu.Update(1);
        view.Calls.Clear();
        menu.Key(MenuKey.Escape);
        Assert.Equal(["cancelstart"], view.Calls);
        Assert.Equal(NetScreen.Countdown, menu.Screen);               // no question, the state has not come back yet
        view.InGame(NetGamePhase.Lobby); menu.Update(2);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain("leave", view.Calls);
    }

    [Fact]
    public void Countdown_OperatorEscRefusedShowsTheReason()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Countdown); menu.Update(1);
        view.Accept = false;
        view.LastError = new SessionError(SessionErrorKind.NotOperator, "only the operator can do this");
        menu.Key(MenuKey.Escape);
        Assert.Equal("only the operator can do this", menu.Message);
        Assert.Equal(NetScreen.Countdown, menu.Screen);
    }

    [Fact]
    public void Countdown_PlayerEscStillAsksToLeave()
    {
        var menu = InLobby(out var view, op: false);
        view.InGame(NetGamePhase.Countdown, op: false); menu.Update(1);
        view.Calls.Clear();
        menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        Assert.Empty(view.Calls);
    }

    static NetworkMenuController StartOnLobby(out FakeView view, bool bobReady, bool bobConnected = true, bool meReady = false)
    {
        var menu = InLobby(out view);
        SetMembers(view, m => m.Name == "Bob" ? m with { Ready = bobReady, Connected = bobConnected } : m with { Ready = meReady });
        menu.Update(1);
        SelectLobby(menu, LobbyRow.Start);
        return menu;
    }

    [Fact]
    public void Start_WithAPlayerNotReadyAsksFirstAndDefaultsToCancel()
    {
        var menu = StartOnLobby(out var view, bobReady: false);
        Assert.True(menu.NotAllReady);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmStart, menu.Screen);
        Assert.False(menu.ConfirmYes);
        Assert.DoesNotContain("start", view.Calls);
        menu.Key(MenuKey.Enter);                                      // default: cancel
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain("start", view.Calls);
        menu.Key(MenuKey.Enter); menu.Key(MenuKey.Escape);            // ESC = cancel
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.DoesNotContain("start", view.Calls);
    }

    [Fact]
    public void Start_ConfirmationMovesWithEveryArrowAndStartsOnlyOnStartAnyway()
    {
        foreach (var key in new[] { MenuKey.Up, MenuKey.Down, MenuKey.Left, MenuKey.Right })
        {
            var menu = StartOnLobby(out var view, bobReady: false);
            menu.Key(MenuKey.Enter);
            menu.Key(key);
            Assert.True(menu.ConfirmYes);
            menu.Key(key);
            Assert.False(menu.ConfirmYes);
            menu.Key(key);
            menu.Key(MenuKey.Enter);
            Assert.Equal(["start"], view.Calls);
            Assert.Equal(NetScreen.Lobby, menu.Screen);               // the countdown arrives with the next state
            view.InGame(NetGamePhase.Countdown); menu.Update(2);
            Assert.Equal(NetScreen.Countdown, menu.Screen);
        }
    }

    [Fact]
    public void Start_WhenEveryoneIsReadyStartsAtOnce()
    {
        var menu = StartOnLobby(out var view, bobReady: true);
        Assert.False(menu.NotAllReady);                               // the operator's own flag does not count (it is not ready here)
        Assert.False(menu.Me!.Ready);
        menu.Key(MenuKey.Enter);
        Assert.Equal(["start"], view.Calls);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
    }

    [Fact]
    public void Start_IgnoresDisconnectedMembersAndAnOperatorAlone()
    {
        var menu = StartOnLobby(out var view, bobReady: false, bobConnected: false);
        Assert.False(menu.NotAllReady);
        menu.Key(MenuKey.Enter);
        Assert.Equal(["start"], view.Calls);
        var alone = InLobby(out var soloView);
        soloView.Lobby = soloView.Lobby! with { Members = [soloView.Lobby.Members[0]] };
        alone.Update(1);
        SelectLobby(alone, LobbyRow.Start); alone.Key(MenuKey.Enter);
        Assert.Equal(["start"], soloView.Calls);
    }

    [Fact]
    public void Start_ConfirmationSurvivesLobbyUpdatesAndGivesWayToTheCountdown()
    {
        var menu = StartOnLobby(out var view, bobReady: false);
        menu.Key(MenuKey.Enter);
        view.Lobby = view.Lobby! with { GameName = "Renamed" }; menu.Update(2);   // a change of the lobby under the window
        Assert.Equal(NetScreen.ConfirmStart, menu.Screen);
        view.InGame(NetGamePhase.Countdown); menu.Update(3);            // e.g. the start was accepted anyway
        Assert.Equal(NetScreen.Countdown, menu.Screen);
        Assert.False(menu.ConfirmYes);
    }

    // sounds: the front-end UI sounds of the original menu

    static List<MenuSound> Record(NetworkMenuController menu)
    {
        var list = new List<MenuSound>();
        menu.Sound += list.Add;
        return list;
    }

    [Fact]
    public void Sounds_ConnectScreenMovesSelectsAndGoesBack()
    {
        var menu = new NetworkMenuController("Me");
        var sounds = Record(menu);
        menu.Key(MenuKey.Down);
        Assert.Equal([MenuSound.Move], sounds); sounds.Clear();
        menu.Key(MenuKey.Enter);                                      // the host field starts editing
        menu.Type('x');                                               // typing is silent
        menu.Key(MenuKey.Enter);
        Assert.Equal([MenuSound.Select, MenuSound.Select], sounds); sounds.Clear();
        menu.Key(MenuKey.Enter); menu.Key(MenuKey.Escape);
        Assert.Equal([MenuSound.Select, MenuSound.Back], sounds); sounds.Clear();
        menu.Key(MenuKey.Left);                                       // nothing happens on a text row
        Assert.Empty(sounds);
        menu.Key(MenuKey.Escape);
        Assert.Equal([MenuSound.Back], sounds);
    }

    [Fact]
    public void Sounds_LobbyMoveChangeSelectAndBack()
    {
        var menu = InLobby(out var view);
        SetMembers(view, m => m with { Ready = true }); menu.Update(1);
        var sounds = Record(menu);
        menu.Key(MenuKey.Right);                                      // READY -> RACE
        menu.Key(MenuKey.Right);                                      // -> LEAVE
        menu.Key(MenuKey.Right);                                      // the end of the line: nothing changes, nothing sounds
        Assert.Equal([MenuSound.Move, MenuSound.Move], sounds); sounds.Clear();
        SelectLobby(menu, LobbyRow.Ready);
        sounds.Clear();
        menu.Key(MenuKey.Enter);
        Assert.Equal([MenuSound.Select], sounds); sounds.Clear();
        SelectLobby(menu, LobbyRow.Track);
        sounds.Clear();
        menu.Key(MenuKey.Right);
        Assert.Equal([MenuSound.Change], sounds); sounds.Clear();
        menu.Key(MenuKey.Escape);                                     // out of the settings
        Assert.Equal([MenuSound.Back], sounds); sounds.Clear();
        SelectLobby(menu, LobbyRow.Start); sounds.Clear();
        menu.Key(MenuKey.Enter);                                      // everybody ready: the start goes out
        Assert.Equal([MenuSound.StartRace], sounds); sounds.Clear();
        view.Accept = false;
        menu.Key(MenuKey.Enter);                                      // refused: silent
        Assert.Empty(sounds);
    }

    [Fact]
    public void Sounds_LapsAtTheLimitAreSilent()
    {
        var menu = InLobby(out var view, op: true);
        view.InGame(NetGamePhase.Lobby, op: true, laps: NetProtocol.MaxLaps); menu.Update(1);
        SelectLobby(menu, LobbyRow.Laps);
        var sounds = Record(menu);
        menu.Key(MenuKey.Right);                                      // already at the maximum
        Assert.Empty(sounds);
        menu.Key(MenuKey.Left);
        Assert.Equal([MenuSound.Change], sounds);
    }

    [Fact]
    public void Sounds_ConfirmationsAndTheCountdownCancel()
    {
        var menu = StartOnLobby(out var view, bobReady: false);
        var sounds = Record(menu);
        menu.Key(MenuKey.Enter);                                      // opens the warning
        menu.Key(MenuKey.Right);                                      // start anyway
        menu.Key(MenuKey.Escape);                                     // cancel
        Assert.Equal([MenuSound.Select, MenuSound.Move, MenuSound.Back], sounds); sounds.Clear();
        menu.Key(MenuKey.Enter); menu.Key(MenuKey.Left); menu.Key(MenuKey.Enter);
        Assert.Equal([MenuSound.Select, MenuSound.Move, MenuSound.StartRace], sounds); sounds.Clear();
        view.InGame(NetGamePhase.Countdown); menu.Update(5);
        menu.Key(MenuKey.Escape);                                     // the operator cancels the countdown
        Assert.Equal([MenuSound.Back], sounds); sounds.Clear();
        view.InGame(NetGamePhase.Lobby); menu.Update(6);
        menu.Key(MenuKey.Escape); menu.Key(MenuKey.Right); menu.Key(MenuKey.Enter);   // leave: ask, yes, confirm
        Assert.Equal([MenuSound.Back, MenuSound.Move, MenuSound.Select], sounds);
    }

    [Fact]
    public void Sounds_CarScreenChangesAndDone()
    {
        var menu = InLobby(out _);
        SelectLobby(menu, LobbyRow.Car);
        var sounds = Record(menu);
        menu.Key(MenuKey.Enter);                                      // opens the car screen
        menu.Key(MenuKey.Right);                                      // next car
        menu.Key(MenuKey.Down);                                       // the colour row
        menu.Key(MenuKey.Escape);
        Assert.Equal([MenuSound.Select, MenuSound.Change, MenuSound.Move, MenuSound.Back], sounds);
    }

    [Fact]
    public void Sounds_NoSubscriberIsSilentAndHarmless()
    {
        var menu = InLobby(out _);
        menu.Key(MenuKey.Right); menu.Key(MenuKey.Escape);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
    }

    // ---------------- round 2026-10-21 (change network-menu-look, D12) ----------------

    [Fact]
    public void Connect_RememberedSettingsPutTheCursorOnConnectSoJoiningIsOneEnter()
    {
        var first = new NetworkMenuController("Me");
        Assert.Equal((int)ConnectRow.Nick, first.Selected);                  // the very first run: the player looks at the nick
        var saved = new NetworkMenuController("Me", "race.example.org", 4000, remembered: true);
        Assert.Equal((int)ConnectRow.Connect, saved.Selected);
        saved.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Connecting, saved.Screen);
        Assert.Equal([new NetMenuAction.Connect("Me", "race.example.org", 4000)], saved.DrainActions());
    }

    [Fact]
    public void Connect_ALookupOrSocketFailureFocusesTheServerEntry()
    {
        var menu = new NetworkMenuController("Me", remembered: true);
        menu.Key(MenuKey.Enter);
        menu.ConnectFailed("Address lookup failed: no such host");
        Assert.Equal(NetScreen.Connect, menu.Screen);
        Assert.Equal((int)ConnectRow.Host, menu.Selected);
        Assert.True(menu.MessageIsError);
    }

    [Fact]
    public void Connect_AVersionMismatchLeavesTheCursorOnConnect()
    {
        var menu = new NetworkMenuController("Me");
        PressTo(menu, ConnectRow.Connect); menu.Key(MenuKey.Enter);
        menu.Attach(new FakeView { Phase = SessionClientPhase.Rejected, LastReject = new NetReject(NetRejectReason.ProtocolMismatch) });
        menu.Update(0);
        Assert.Equal((int)ConnectRow.Connect, menu.Selected);                // nothing the player can correct
    }

    [Fact]
    public void GamesList_IsRequestedEveryThreeSecondsWhileItIsOpen()
    {
        var menu = Connected(out var view, Game(1, "Open", NetGamePhase.Lobby));
        view.Calls.Clear();
        menu.Update(NetworkMenuController.GamesRefreshMicros - 1);
        Assert.Empty(view.Calls);
        menu.Update(NetworkMenuController.GamesRefreshMicros);
        Assert.Equal(["games"], view.Calls);
        menu.Update(NetworkMenuController.GamesRefreshMicros + 1_000_000);
        Assert.Equal(["games"], view.Calls);                                 // not again before the next three seconds
        menu.Update(2 * NetworkMenuController.GamesRefreshMicros);
        Assert.Equal(["games", "games"], view.Calls);
    }

    [Fact]
    public void GamesList_AManualRefreshRestartsTheTimerAndAPendingRequestHoldsItBack()
    {
        var menu = Connected(out var view, Game(1, "Open", NetGamePhase.Lobby));
        view.Calls.Clear();
        Select(menu, GamesRow.Refresh);
        menu.Update(2_500_000);
        menu.Key(MenuKey.Enter);
        Assert.Equal(["games"], view.Calls);
        menu.Update(2_500_000 + NetworkMenuController.GamesRefreshMicros - 1);
        Assert.Equal(["games"], view.Calls);
        view.Pending = true;
        menu.Update(2_500_000 + NetworkMenuController.GamesRefreshMicros + 500);
        Assert.Equal(["games"], view.Calls);                                 // a create / join waits for the server: no polling
        view.Pending = false;
        menu.Update(2_500_000 + NetworkMenuController.GamesRefreshMicros + 1000);
        Assert.Equal(["games", "games"], view.Calls);
    }

    [Fact]
    public void GamesList_IsNotRequestedOutsideTheGamesScreen()
    {
        var menu = Connected(out var view, Game(1, "Open", NetGamePhase.Lobby));
        SelectCreateGame(menu);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.CreateOptions, menu.Screen);
        view.Calls.Clear();
        menu.Update(10_000_000);
        Assert.Empty(view.Calls);
        menu.Key(MenuKey.Escape);                                            // back on the list: the clock starts from there
        menu.Update(10_000_001);
        Assert.Empty(view.Calls);
        menu.Update(10_000_000 + NetworkMenuController.GamesRefreshMicros);
        Assert.Equal(["games"], view.Calls);
    }

    [Fact]
    public void GamesList_AutoRefreshKeepsTheCursorOnTheSameGameAndFallsBackToTheDefaultWhenItVanishes()
    {
        var menu = Connected(out var view, Game(1, "A", NetGamePhase.Lobby), Game(2, "B", NetGamePhase.Lobby), Game(3, "C", NetGamePhase.Lobby));
        menu.Key(MenuKey.Down);
        SelectGame(menu, 2);                                                 // game id 3
        view.Games = [Game(7, "New", NetGamePhase.Lobby), Game(1, "A", NetGamePhase.Lobby), Game(3, "C", NetGamePhase.Lobby)];
        menu.Update(1);
        menu.GamesRowAt(menu.Selected, out int at);
        Assert.Equal(3, menu.Games[at].GameId);
        // the cursor on a button stays on that button when the list grows or shrinks
        Select(menu, GamesRow.Refresh);
        view.Games = [Game(1, "A", NetGamePhase.Lobby)];
        menu.Update(2);
        Assert.Equal(GamesRow.Refresh, menu.GamesRowAt(menu.Selected, out _));
        // the game under the cursor is gone: the default focus (first joinable game) applies again
        SelectGame(menu, 0);
        view.Games = [Game(8, "Full", NetGamePhase.Lobby, 4, 4), Game(9, "Free", NetGamePhase.Lobby)];
        menu.Update(3);
        menu.GamesRowAt(menu.Selected, out at);
        Assert.Equal(9, menu.Games[at].GameId);
    }

    [Fact]
    public void Lobby_LeftAndRightOnTheCarRowCycleTheModelWithoutOpeningTheCarScreen()
    {
        var menu = InLobby(out var view);
        SelectLobby(menu, LobbyRow.Car);
        var sounds = Record(menu);
        view.Calls.Clear();
        menu.Key(MenuKey.Right);
        Assert.Equal(["car:2"], view.Calls);                                 // the car of the fake lobby is 1
        Assert.False(menu.InCar);
        menu.Key(MenuKey.Left);
        Assert.Equal(["car:2", "car:0"], view.Calls);                        // the fake lobby does not move the model itself
        Assert.Equal([MenuSound.Change, MenuSound.Change], sounds);
        sounds.Clear();
        menu.Key(MenuKey.Enter);
        Assert.True(menu.InCar);                                             // ENTER still opens the car screen
        Assert.Equal([MenuSound.Select], sounds);
    }

    [Fact]
    public void Lobby_ThePlayerAlsoCyclesTheCarOnTheCarRow_AndAFrozenCarStaysPut()
    {
        var menu = InLobby(out var view, op: false);
        SelectLobby(menu, LobbyRow.Car);
        menu.Key(MenuKey.Right);
        Assert.Equal(["car:2"], view.Calls);
        // the series begins while the cursor stands on the car row: the row is frozen, LEFT/RIGHT/ENTER do nothing and say nothing
        var frozen = KnockoutLobby(out var fview, completedRounds: null);
        SelectLobby(frozen, LobbyRow.Car);
        fview.InGame(NetGamePhase.Lobby, mode: NetGameMode.Knockout, series: new NetKnockoutState(1, false, -1,
            Enumerable.Range(0, NetProtocol.KnockoutEntrants).Select(i => new NetKnockoutEntry(i == 0 ? "Me" : $"BOT {i}", i < 2 ? i : -1, true, 0, -1)).ToArray()));
        frozen.Update(1);
        Assert.Equal(OptionAccess.ReadOnly, frozen.Access(LobbyRow.Car));
        var sounds = Record(frozen);
        fview.Calls.Clear();
        frozen.Key(MenuKey.Right); frozen.Key(MenuKey.Left); frozen.Key(MenuKey.Enter);
        Assert.DoesNotContain(fview.Calls, c => c.StartsWith("car"));
        Assert.DoesNotContain(MenuSound.Change, sounds);
        Assert.False(frozen.InCar);
    }

    [Fact]
    public void Lobby_ARefusedCarChangeShowsTheReasonAndStaysSilent()
    {
        var menu = InLobby(out var view);
        SelectLobby(menu, LobbyRow.Car);
        var sounds = Record(menu);
        view.Accept = false;
        view.LastError = new SessionError(SessionErrorKind.NotConnected, "not connected");
        menu.Key(MenuKey.Right);
        Assert.Equal("not connected", menu.Message);
        Assert.Empty(sounds);
    }

    [Fact]
    public void Lobby_EscOutOfTheSettingsLandsOnRaceAndEnterStartsWithoutFurtherKeys()
    {
        var menu = InLobby(out var view);
        view.Lobby = view.Lobby! with { Members = [.. view.Lobby.Members.Select(m => m with { Ready = true })] };
        menu.Update(1);
        SelectLobby(menu, LobbyRow.Laps);
        var sounds = Record(menu);
        menu.Key(MenuKey.Right);                                             // a setting changed
        menu.Key(MenuKey.Escape);
        Assert.Equal(LobbyRow.Start, menu.LobbyRows[menu.Selected]);
        Assert.False(menu.InSettings);
        view.Calls.Clear(); sounds.Clear();
        menu.Key(MenuKey.Enter);
        Assert.Equal(["start"], view.Calls);
        Assert.Equal([MenuSound.StartRace], sounds);
    }

    [Fact]
    public void Summary_ShowsACountdownAndReturnsToTheLobbyByItselfAfterThirtySeconds()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1_000_000);
        view.ResultsId = 5; view.InGame(NetGamePhase.Lobby); menu.Update(2_000_000);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        Assert.Equal(30, menu.SummaryRemainingSeconds);
        menu.Update(7_000_000);
        Assert.Equal(25, menu.SummaryRemainingSeconds);
        menu.Update(31_999_999);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        Assert.Equal(1, menu.SummaryRemainingSeconds);
        Assert.DoesNotContain("return", view.Calls);
        var sounds = Record(menu);
        menu.Update(32_000_000);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        Assert.Equal(["return"], view.Calls);                                // the server is already in the lobby; the client only acknowledges
        Assert.Equal(LobbyRow.Ready, menu.LobbyRows[menu.Selected]);
        Assert.Empty(sounds);                                                // not a key press: silent
        Assert.Equal(0, menu.SummaryRemainingSeconds);
        menu.Update(70_000_000);
        Assert.Equal(["return"], view.Calls);                                // once; the acknowledged results do not bring the summary back
        Assert.Equal(NetScreen.Lobby, menu.Screen);
    }

    [Fact]
    public void Summary_EnterOnReturnStillWorksAndTheExitGoesThroughTheConfirmation()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1);
        view.ResultsId = 5; view.InGame(NetGamePhase.Lobby); menu.Update(2);
        menu.Update(10_000_000);
        menu.Key(MenuKey.Enter);
        Assert.Equal(["return"], view.Calls);
        Assert.Equal(NetScreen.Lobby, menu.Screen);
        // exit: the question comes first and the timer waits behind it
        view.Calls.Clear();
        view.InGame(NetGamePhase.Race); menu.Update(11_000_000);
        view.ResultsId = 6; view.InGame(NetGamePhase.Lobby); menu.Update(12_000_000);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        menu.Update(60_000_000);                                             // the question is open for 48 s: no automatic return
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        Assert.Empty(view.Calls);
        menu.Key(MenuKey.Escape);                                            // "no": back on the summary with the time it had left
        Assert.Equal(NetScreen.Summary, menu.Screen);
        menu.Update(60_000_001);
        Assert.Equal(30, menu.SummaryRemainingSeconds);
        Assert.Equal(NetScreen.Summary, menu.Screen);
    }

    [Fact]
    public void Summary_TheTimerWaitsWhileTheLinkIsDown()
    {
        var menu = InLobby(out var view);
        view.InGame(NetGamePhase.Race); menu.Update(1);
        view.ResultsId = 5; view.InGame(NetGamePhase.Lobby); menu.Update(2);
        menu.Update(10_000_000);
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(50_000_000);
        Assert.True(menu.Reconnecting);
        view.Phase = SessionClientPhase.InGame;
        menu.Update(50_000_001);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        Assert.InRange(menu.SummaryRemainingSeconds, 21, 22);                // 10 s of the 30 were used before the loss
    }

    [Fact]
    public void Reconnect_SaysHowManySecondsAreLeftWhenTheSessionKnowsThem()
    {
        var menu = InLobby(out var view);
        Assert.Null(menu.ReconnectSecondsLeft);
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(1);
        Assert.True(menu.Reconnecting);
        Assert.Null(menu.ReconnectSecondsLeft);                              // a view without the figure: nothing to tell
        view.ReconnectRemainingMicros = 21_400_000;
        Assert.Equal(22, menu.ReconnectSecondsLeft);
        view.ReconnectRemainingMicros = 0;
        Assert.Equal(0, menu.ReconnectSecondsLeft);
    }
}
