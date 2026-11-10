using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Layout of the network screens (change network-menu-look, design D2): a pure function of <see cref="NetworkMenuController"/>
/// state (and the session view it reads) that fills a <see cref="NetDrawList"/> with texts, panels and swatches in the order they
/// are drawn; a backend (<see cref="ClassicNetBackend"/>) turns the list into pixels. Coordinates, texts and the order are those of
/// the first network menu (dark chequered panels, <see cref="HudOverlay"/> metrics: the width of a text is measured with it), the
/// colour is a <see cref="NetRole"/>. With the fonts of the game (<c>fonts</c> of <see cref="Build"/>, the original look) the
/// connect, connecting, games, create and summary screens get the layout of design D6 (texts in lower case, widths measured with
/// the game font); the other screens keep the classic geometry until their own stage. Texts from players and the server pass <see cref="NetText.Safe"/>; every composed text comes
/// from the caches of <see cref="NetText"/>, so a warm frame allocates (almost) nothing. One instance per drawing thread.
/// </summary>
internal sealed partial class NetworkMenuLayout
{
    const int Left = 60, Right = 580;

    /// <summary>Key hints of the network screens (design D7): <c>KEY ACTION</c> items separated by three spaces, only the actions that
    /// work on the screen, <c>T CHAT</c> first where the chat can be opened.</summary>
    internal const string ReconnectHint = "CONNECTION LOST - RECONNECTING...   ESC GIVE UP", EditHint = "TYPE TEXT   ENTER DONE   ESC CANCEL",
        ChatHint = "ENTER SEND   ESC CANCEL", LobbyHint = "T CHAT   UP/DOWN SELECT   ENTER OK   ESC LEAVE",
        SettingsHint = "T CHAT   UP/DOWN SELECT   LEFT/RIGHT CHANGE   ESC BACK",
        CarHint = "T CHAT   UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK", CountdownHint = "T CHAT   ESC LEAVE", CountdownOperatorHint = "T CHAT   ESC CANCEL",
        SummaryHint = "T CHAT   UP/DOWN SELECT   ENTER OK   ESC EXIT", ConnectHint = "UP/DOWN SELECT   ENTER OK   ESC BACK",
        CreateHint = "UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK", ConnectingHint = "ESC CANCEL", WaitingHint = "ESC LEAVE",
        RaceBoxHint = "ESC PAUSE OR LEAVE", RaceBoxOutHint = "YOU ARE OUT - WATCHING   ESC LEAVE";

    /// <summary>Message line and footer: classic (left, 60) and original (centred at 320, 456; the original draws it at 465 and cuts the foot).</summary>
    internal const int MessageY = 436, FooterY = 450, OriginalFooterY = 456, OriginalFooterX = 320;

    // The original look (design D6): the data panels start right of the board; the left column is right-aligned at BoardX.
    internal const int BoardX = 168, PanelCentre = 432;

    /// <summary>Lobby layout: the SETTINGS title, the first option row and the chat lines; the actions stand in a column right of
    /// the players (see <see cref="ActionX"/>).</summary>
    internal const int SettingsY = 234, OptionsY = 258, OptionsPitch = 24, LobbyChatY = 352;

    /// <summary>Classic lobby: the vertical list of the actions (settings, ready, start, leave) right of the players table.</summary>
    internal const int ActionX = 470, ActionY = 100, ActionPitch = 24;

    /// <summary>First line of the chat plate over the race.</summary>
    internal const int RaceChatY = 25;

    /// <summary>The dialog window of the game (<c>MenuScene.DrawDialogFrame</c>) has its body line at y 225 (the box is 60 rows [ref:1308]
    /// high in the middle of 480); the answers of the confirmation stand there at the x of the original, the entries of the pause
    /// likewise.</summary>
    internal const int DialogBodyY = 225, ConfirmYesX = 0x118, ConfirmNoX = 0x168, StartAnywayX = 255, StartCancelX = 385, PauseResumeX = 270, PauseLeaveX = 370;

    /// <summary>Banner over the race picture (original look): rows 0..22.</summary>
    internal const int BannerHeight = 22;

    const int VisibleGames = 8, ChatChars = 80;

    static readonly (int Col, string Label)[] GameColumns = [(0, "NAME"), (13, "STATUS"), (23, "PLAYERS"), (31, "TRACK"), (47, "LAPS")];
    static readonly (int Col, string Label)[] PlayerColumns = [(0, "#"), (4, "NAME"), (17, "CAR"), (30, "COL"), (34, "STATE"), (43, "LINK")];
    static readonly (int Col, string Label)[] ResultColumns = [(0, "POS"), (4, "NAME"), (15, "CAR"), (28, "TIME"), (38, "BEST")];
    static readonly string[] SummaryLabels = ["RETURN TO LOBBY", "EXIT"];

    NetDrawList _list = null!;
    FrontEndFont? _fonts;
    int _width;
    NetLobbyMember? _me;
    bool _chatTyping;
    readonly ChatLine[] _chatLines = new ChatLine[NetworkChat.Visible];
    NetKnockoutState? _outSeries;
    string _outNames = "";

    /// <summary>Fills <paramref name="list"/> (not cleared here) with the current screen, the chat of the lobby, countdown and
    /// summary screens and the reconnect banner. <paramref name="caret"/> is the phase of the edit cursor, <paramref name="width"/>
    /// the width of the buffer.</summary>
    public void Build(NetDrawList list, NetworkMenuController menu, NetworkChat? chat, bool caret, int width, FrontEndFont? fonts = null)
    {
        Begin(list, menu, width, fonts);
        var screen = menu.Screen;
        // the line being typed takes the place of the footer (its panel is drawn over the foot of the screen)
        _chatTyping = chat is { Typing: true } && screen is NetScreen.Lobby or NetScreen.Countdown or NetScreen.Summary;
        if (screen is NetScreen.ConfirmExit or NetScreen.ConfirmStart && Original)
        {
            // only the window over the backdrop: the panels of the screen below would show through its 70% dim
            DrawConfirm(menu);
            Footer(menu, ConnectHint);
            if (menu.Reconnecting) MenuBanner(menu);
            return;
        }
        if (screen is NetScreen.ConfirmExit or NetScreen.ConfirmStart)
        {
            DrawScreen(menu, screen == NetScreen.ConfirmStart ? NetScreen.Lobby : menu.ConfirmOrigin, caret);
            DrawConfirm(menu);
        }
        else DrawScreen(menu, screen, caret);
        if (chat != null && screen is NetScreen.Lobby or NetScreen.Countdown or NetScreen.Summary)
        {
            if (Original && screen is NetScreen.Summary or NetScreen.Lobby or NetScreen.Countdown) DrawChatOriginal(chat, caret);
            else DrawChat(chat, screen, caret);
        }
        if (menu.Reconnecting) MenuBanner(menu);
    }

    /// <summary>Only the reconnect banner (over the victory screen, which the host draws itself).</summary>
    public void BuildBanner(NetDrawList list, NetworkMenuController menu, int width, FrontEndFont? fonts = null)
    {
        Begin(list, menu, width, fonts);
        if (menu.Reconnecting) Banner(ReconnectText(menu), ReconnectRole);
    }

    /// <summary>What goes over the race picture (drawn by the host): the chat, the exit confirmation, the reconnect, straggler and
    /// spectator banners.</summary>
    public void BuildRaceOverlay(NetDrawList list, NetworkMenuController menu, NetworkChat? chat, long nowMicros, int width,
        FrontEndFont? fonts = null)
    {
        Begin(list, menu, width, fonts);
        if (chat != null && menu.Screen == NetScreen.Race) DrawRaceChat(chat, nowMicros);
        if (menu.Screen is NetScreen.ConfirmExit) DrawConfirm(menu);
        if (menu.Reconnecting) Banner(ReconnectText(menu), ReconnectRole);
        else if (menu.Screen == NetScreen.Race && menu.CutoffRemainingMillis is > 0 and var left)
            Banner(NetText.Join("FIRST CAR HOME - ", NetText.Int((left + 999) / 1000), " S LEFT FOR THE OTHERS"), NetRole.Warn);
        else if (menu.Screen == NetScreen.Race && menu.Spectating)
            Banner(menu.WatchedName is { Length: > 0 } name
                ? NetText.Join("YOU ARE OUT - WATCHING ", NetText.Safe(name), "   Q/E CAR   W LEADER") : "YOU ARE OUT - WATCHING THE RACE", NetRole.Warn);
    }

    /// <summary>The network pause over the race picture (design D8): the dialog window of the game with <c>resume</c> and
    /// <c>leave race</c> side by side (the original look), else the classic panel with the two entries one under the other.
    /// <paramref name="selection"/> 0 = resume, 1 = leave race.</summary>
    public void BuildPause(NetDrawList list, int selection, int width, FrontEndFont? fonts = null)
    {
        _list = list;
        _fonts = fonts;
        _width = width;
        if (Original)
        {
            list.Dialog("network paused", "");
            list.Text(PauseResumeX, DialogBodyY, NetFont.Normal, NetAlign.Center, selection == 0 ? NetRole.Selected : NetRole.Label, "resume");
            list.Text(PauseLeaveX, DialogBodyY, NetFont.Normal, NetAlign.Center, selection == 1 ? NetRole.Selected : NetRole.Label, "leave race");
            return;
        }
        list.Panel(130, 145, 510, 335, NetPanelKind.Data);
        list.Text(225, 175, NetFont.Normal, NetAlign.Left, NetRole.Value, "NETWORK PAUSED");
        list.Text(210, 235, NetFont.Normal, NetAlign.Left, selection == 0 ? NetRole.Selected : NetRole.Label, selection == 0 ? "> RESUME" : "  RESUME");
        list.Text(210, 275, NetFont.Normal, NetAlign.Left, selection == 1 ? NetRole.Selected : NetRole.Label, selection == 1 ? "> LEAVE RACE" : "  LEAVE RACE");
    }

    /// <summary>True for the layout of the original look (the fonts of the game were given).</summary>
    bool Original => _fonts != null;

    /// <summary>The lost-link banner is red in the original look (design D4), orange (a banner) in classic.</summary>
    NetRole ReconnectRole => Original ? NetRole.Bad : NetRole.Warn;

    void Begin(NetDrawList list, NetworkMenuController menu, int width, FrontEndFont? fonts)
    {
        _list = list;
        _fonts = fonts;
        _width = width;
        _chatTyping = false;
        _me = menu.Me;
    }

    void DrawScreen(NetworkMenuController menu, NetScreen screen, bool caret)
    {
        if (Original)
        {
            switch (screen)
            {
                case NetScreen.Connect: DrawConnectOriginal(menu, caret); return;
                case NetScreen.Connecting: DrawConnectingOriginal(menu); return;
                case NetScreen.Games: DrawGamesOriginal(menu); return;
                case NetScreen.CreateOptions: DrawCreateOriginal(menu, caret); return;
                case NetScreen.Summary: DrawSummaryOriginal(menu); return;
                case NetScreen.Lobby: DrawLobbyOriginal(menu); return;
                case NetScreen.Countdown: DrawCountdownOriginal(menu); return;
                case NetScreen.Race: DrawRaceBoxOriginal(menu); return;
            }
        }
        switch (screen)
        {
            case NetScreen.Connect: DrawConnect(menu, caret); break;
            case NetScreen.Connecting: DrawConnecting(menu); break;
            case NetScreen.Games: DrawGames(menu); break;
            case NetScreen.CreateOptions: DrawCreate(menu, caret); break;
            case NetScreen.Lobby: DrawLobby(menu); break;
            case NetScreen.Countdown: DrawCountdown(menu); break;
            case NetScreen.Race: DrawRaceBox(menu); break;
            case NetScreen.Summary: DrawSummary(menu); break;
        }
    }

    // ---------------- helpers ----------------

    /// <summary>Most characters of a text of <paramref name="size"/> that fit the panel.</summary>
    static int FitChars(int size) => (Right - Left) / (4 * NetFont.ClassicScale(size));

    /// <summary>Text cut to <paramref name="maxChars"/> characters (default: fits the panel); <paramref name="x"/> is the left,
    /// right or centre of the text.</summary>
    void Text(int x, int y, int size, NetAlign align, NetRole role, string text, int maxChars = 0) =>
        _list.Text(x, y, size, align, role, NetText.Cut(text, maxChars <= 0 ? FitChars(size) : maxChars));

    void Panel(string title, int y0 = 20, int y1 = 468)
    {
        _list.Panel(Left - 20, y0, Right + 20, y1, NetPanelKind.Data);
        Text(_width / 2, y0 + 14, NetFont.Normal, NetAlign.Center, NetRole.Value, title);
    }

    void Row(int y, string text, bool selected, NetRole role = NetRole.Label) =>
        Text(Left, y, NetFont.Normal, NetAlign.Left, selected ? NetRole.Selected : role, NetText.Join(selected ? "> " : "  ", text));

    static string Field(NetworkMenuController m, NetField field, string value, bool caret) =>
        m.Editing == field ? NetText.Join(NetText.Safe(value), caret ? "_" : " ") : NetText.Safe(value);

    /// <summary>The message line (above the footer) and the key hints. The classic look keeps the message at the left; with
    /// <paramref name="bar"/> (the original screens of design D6) it is centred on a dark bar and <paramref name="pending"/> stands in
    /// for it while the client waits for the server. A line being typed or the reconnect banner take the place of the footer.</summary>
    void Footer(NetworkMenuController m, string hint, bool bar = false, string? pending = null, bool message = true)
    {
        if (bar) MessageBar(m, pending);
        else if (message && m.Message != null) Text(Left, MessageY, NetFont.Small, NetAlign.Left, m.MessageIsError ? NetRole.Bad : NetRole.Good, NetText.Safe(m.Message));
        if (_chatTyping || m.Reconnecting) return;
        string text = m.Editable ? EditHint : hint;
        if (Original) _list.Text(OriginalFooterX, OriginalFooterY, NetFont.Small, NetAlign.Center, NetRole.Label, text);
        else Text(Left, FooterY, NetFont.Small, NetAlign.Left, NetRole.Label, text);
    }

    /// <summary>Message of the controller (an error in red, else in the value colour) centred on a dark bar above the footer.</summary>
    void MessageBar(NetworkMenuController m, string? pending)
    {
        string? text = m.Message != null ? NetText.Safe(m.Message) : pending;
        if (text == null || _chatTyping) return;
        _list.Panel(236, 432, 628, 452, NetPanelKind.ChatPlate);
        _list.Text(PanelCentre, MessageY, NetFont.Small, NetAlign.Center, m.Message != null && m.MessageIsError ? NetRole.Bad : NetRole.Value, text, 380);
    }

    static string BotsText(int bots) => bots == NetProtocol.AutoBots ? "AUTO" : NetText.Int(bots);
    static string OnOff(bool on) => on ? "ON" : "OFF";
    static string SkillText(int skill) => skill == 0 ? "BEGINNER" : "EXPERT";
    static string StyleText(int style) => style switch { 0 => "SIMULATION", 1 => "WILD", _ => "ARCADE" };
    static string ModeText(NetGameMode mode) => mode == NetGameMode.Knockout ? "KNOCKOUT" : "SINGLE RACE";

    static string CarName(NetworkMenuController m, int car) => NetText.Safe(m.CarName(car));
    static string TrackName(NetworkMenuController m, int track) => NetText.Safe(m.TrackName(track));

    /// <summary>Colour of a car as the lobby shows it when it has no swatch: "-" for no explicit choice, "?" for a colour that is
    /// not in the palette of the model.</summary>
    static string ColourShort(int colour) => colour == NetProtocol.NoColour ? "-" : "?";
    static string StatusText(NetGamePhase p) => p switch { NetGamePhase.Lobby => "LOBBY", NetGamePhase.Countdown => "COUNTDOWN", _ => "RACE" };
    static NetRole StatusRole(NetGamePhase p) => p switch { NetGamePhase.Lobby => NetRole.Good, NetGamePhase.Countdown => NetRole.Warn, _ => NetRole.Bad };

    void Banner(string text, NetRole role)
    {
        if (Original)
        {
            // a dim bar over the top of the picture (design D8): the caps of the 13 px font (14 rows) centred in 22 rows
            _list.Panel(0, 0, _width, BannerHeight, NetPanelKind.Banner);
            _list.Text(_width / 2, 0, NetFont.Small, NetAlign.Center, role, text, _width - 16);
            return;
        }
        _list.Panel(-2, -2, _width + 2, 18, NetPanelKind.Banner);
        Text(_width / 2, 6, NetFont.Small, NetAlign.Center, role, text, _width / 4);
    }

    /// <summary>The lost-link banner of a menu screen: a bar in the place of the footer (design D8), so it covers no logo and no panel.</summary>
    void MenuBanner(NetworkMenuController m)
    {
        string text = ReconnectText(m);
        if (Original)
        {
            _list.Panel(0, 448, _width, 470, NetPanelKind.Banner);
            _list.Text(OriginalFooterX, OriginalFooterY, NetFont.Small, NetAlign.Center, ReconnectRole, text);
        }
        else
        {
            _list.Panel(-2, 444, _width + 2, 468, NetPanelKind.Banner);
            Text(_width / 2, FooterY + 2, NetFont.Small, NetAlign.Center, ReconnectRole, text, _width / 4);
        }
    }

    /// <summary>The lost-link text: with the seconds left of the reconnect window when the session says them.</summary>
    static string ReconnectText(NetworkMenuController m) => m.ReconnectSecondsLeft is { } s
        ? NetText.Join(NetText.Join("CONNECTION LOST - RECONNECTING... ", NetText.Int(s), " S LEFT"), "   ESC GIVE UP") : ReconnectHint;

    // ---------------- screens ----------------

    void DrawConnect(NetworkMenuController m, bool caret)
    {
        Panel("NETWORK RACE", 60, 400);
        Row(120, NetText.Join("NICK: ", Field(m, NetField.Nick, m.Nick, caret)), m.Selected == 0);
        Row(150, NetText.Join("SERVER: ", Field(m, NetField.Host, m.Host, caret)), m.Selected == 1);
        Row(180, NetText.Join("PORT: ", Field(m, NetField.Port, m.PortText, caret)), m.Selected == 2);
        Row(210, "CONNECT", m.Selected == 3);
        Row(240, "BACK", m.Selected == 4);
        Footer(m, ConnectHint);
    }

    void DrawConnecting(NetworkMenuController m)
    {
        Panel("NETWORK RACE", 120, 340);
        Text(_width / 2, 200, NetFont.Normal, NetAlign.Center, NetRole.Label, "CONNECTING...");
        Text(_width / 2, 230, NetFont.Small, NetAlign.Center, NetRole.Header, NetText.Cut(NetText.Join(NetText.Safe(m.Host), ":", m.PortText), 60));
        Text(_width / 2, 290, NetFont.Small, NetAlign.Center, NetRole.Label, ConnectingHint);
    }

    void DrawGames(NetworkMenuController m)
    {
        Panel("NETWORK GAMES");
        var games = m.Games;
        int count = m.GamesRowCount;
        var selRow = m.GamesRowAt(m.Selected, out int selGame);
        int first = selGame < 0 || selRow != GamesRow.Game ? 0 : Math.Clamp(selGame - VisibleGames + 1, 0, Math.Max(0, games.Count - VisibleGames));

        foreach (var (col, label) in GameColumns)
            Text(Left + 16 + col * 8, 62, NetFont.Small, NetAlign.Left, NetRole.Header, label);
        if (games.Count == 0)
            Text(Left + 16, 92, NetFont.Normal, NetAlign.Left, NetRole.Disabled, m.Pending ? "WAITING FOR SERVER..." : "NO GAMES - CREATE ONE");
        for (int i = 0; i < VisibleGames && first + i < games.Count; i++)
        {
            var g = games[first + i];
            var avail = m.CanJoin(g);
            bool sel = selRow == GamesRow.Game && selGame == first + i;
            bool usable = avail is JoinAvailability.Available or JoinAvailability.Reserved;
            var role = !usable ? NetRole.Disabled : sel ? NetRole.Selected : NetRole.Label;
            int y = 76 + i * 20;
            if (sel) Text(Left, y, NetFont.Normal, NetAlign.Left, NetRole.Selected, ">");
            Text(Left + 16, y, NetFont.Normal, NetAlign.Left, role, NetText.Safe(g.Name), 12);
            Text(Left + 16 + 13 * 8, y, NetFont.Normal, NetAlign.Left, usable ? StatusRole(g.Phase) : NetRole.Disabled, StatusText(g.Phase));
            Text(Left + 16 + 23 * 8, y, NetFont.Normal, NetAlign.Left, role, NetText.Join(NetText.Int(g.Players), "/", NetText.Int(g.MaxPlayers)));
            Text(Left + 16 + 31 * 8, y, NetFont.Small, NetAlign.Left, role, TrackName(m, g.Track), 15);
            Text(Left + 16 + 47 * 8, y, NetFont.Normal, NetAlign.Left, role, NetText.Int(g.Laps));
            if (avail == JoinAvailability.Reserved) Text(Left + 16 + 40 * 8, y, NetFont.Normal, NetAlign.Left, NetRole.Header, "YOURS");
            else if (avail == JoinAvailability.Full) Text(Left + 16 + 40 * 8, y, NetFont.Normal, NetAlign.Left, NetRole.Disabled, "FULL");
        }
        if (games.Count > VisibleGames)
            Text(Left + 16, 240, NetFont.Small, NetAlign.Left, NetRole.Disabled,
                NetText.Join(NetText.Join(NetText.Int(first + 1), "-", NetText.Int(Math.Min(games.Count, first + VisibleGames))), " OF ", NetText.Int(games.Count)));

        for (int i = count - 3; i < count; i++)
        {
            var row = m.GamesRowAt(i, out _);
            string label = row switch { GamesRow.Create => "CREATE GAME", GamesRow.Refresh => "REFRESH", _ => "BACK" };
            Row(280 + (i - (count - 3)) * 26, label, m.Selected == i);
        }
        if (m.Pending && games.Count > 0) Text(Left, 400, NetFont.Small, NetAlign.Left, NetRole.Warn, "WAITING FOR SERVER...");
        Footer(m, ConnectHint);
    }

    void DrawCreate(NetworkMenuController m, bool caret)
    {
        Panel("CREATE GAME");
        Row(90, NetText.Join("GAME NAME: ", Field(m, NetField.CreateName, m.CreateName, caret)), m.Selected == 0);
        Row(124, NetText.Join("MAX PLAYERS: ", NetText.Int(m.MaxPlayers)), m.Selected == 1);
        Row(158, NetText.Join("MODE: ", ModeText(m.Mode)), m.Selected == 2);
        Row(192, "CREATE", m.Selected == 3);
        Row(226, "BACK", m.Selected == 4);
        Text(Left, 280, NetFont.Small, NetAlign.Left, NetRole.Disabled, "TRACK, DIRECTION, LAPS, BOTS, BOTS SKILL, CATCH-UP, TRAFFIC AND");
        Text(Left, 292, NetFont.Small, NetAlign.Left, NetRole.Disabled, "PHYSICS ARE SET BY THE OPERATOR IN THE LOBBY");
        if (m.Pending) Text(Left, 424, NetFont.Small, NetAlign.Left, NetRole.Warn, "WAITING FOR SERVER...");
        Footer(m, CreateHint);
    }

    void DrawPlayers(NetworkMenuController m, NetLobbyState lobby, int y0, int spacing)
    {
        foreach (var (col, label) in PlayerColumns)
            Text(Left + 16 + col * 8, y0 - 14, NetFont.Small, NetAlign.Left, NetRole.Header, label);
        if (lobby.Members.Count == 0) { Text(Left + 16, y0 + 4, NetFont.Normal, NetAlign.Left, NetRole.Disabled, "NO PLAYERS"); return; }
        for (int i = 0; i < lobby.Members.Count && i < NetworkMenuController.GridCars; i++)
        {
            var p = lobby.Members[i];
            int y = y0 + i * spacing;
            var role = !p.Connected ? NetRole.Disabled : p == _me ? NetRole.Selected : NetRole.Label;
            Text(Left + 16, y, NetFont.Normal, NetAlign.Left, role, NetText.Join(p.Operator ? "*" : " ", NetText.Int(i + 1)));
            Text(Left + 16 + 4 * 8, y, NetFont.Normal, NetAlign.Left, role, NetText.Safe(p.Name), 12);
            Text(Left + 16 + 17 * 8, y + 3, NetFont.Small, NetAlign.Left, role, CarName(m, m.CarOf(p)), 24);
            if (NetworkMenuController.PaletteIndex(m.CarOf(p), p.Colour) >= 0) Swatch(Left + 16 + 30 * 8, y, p.Colour);
            else Text(Left + 16 + 30 * 8, y, NetFont.Normal, NetAlign.Left, role, ColourShort(p.Colour));
            Text(Left + 16 + 34 * 8, y, NetFont.Normal, NetAlign.Left, p.Ready ? NetRole.Good : NetRole.Disabled, p.Ready ? "READY" : "WAITING");
            Text(Left + 16 + 43 * 8, y, NetFont.Normal, NetAlign.Left, p.Connected ? NetRole.Good : NetRole.Bad, p.Connected ? "OK" : "LOST");
        }
    }

    /// <summary>Text of one lobby row: the label and the value; a frozen row says so, a row without effect in the mode is
    /// <c>N/A</c>.</summary>
    string LobbyRowText(NetworkMenuController m, NetLobbyState lobby, LobbyRow row)
    {
        var access = m.Access(row);
        string locked = access == OptionAccess.ReadOnly ? " LOCKED" : "";
        bool na = access == OptionAccess.NotApplicable;
        return row switch
        {
            LobbyRow.Ready => NetText.Join("READY: ", _me?.Ready == true ? "YES" : "NO"),
            LobbyRow.Car => _me == null ? "CAR" : NetText.Join("CAR: ", m.CarShort(m.CarOf(_me))),
            LobbyRow.Track => NetText.Join("TRACK: ", TrackName(m, lobby.Track), locked),
            LobbyRow.Direction => NetText.Join("DIRECTION: ", lobby.Backward ? "BACKWARD" : "FORWARD", locked),
            LobbyRow.Laps => NetText.Join("LAPS: ", NetText.Int(lobby.Laps), locked),
            LobbyRow.Bots => NetText.Join("BOTS: ", na ? "N/A" : BotsText(lobby.Bots)),
            LobbyRow.Skill => NetText.Join("BOTS SKILL: ", SkillText(lobby.Skill), locked),
            LobbyRow.CatchUp => NetText.Join("CATCH-UP: ", OnOff(lobby.CatchUp), locked),
            LobbyRow.Traffic => NetText.Join("TRAFFIC: ", na ? "N/A" : OnOff(lobby.Traffic)),
            LobbyRow.Style => NetText.Join("PHYSICS: ", StyleText(lobby.Style), locked),
            LobbyRow.Start => "START RACE",
            _ => "LEAVE GAME",
        };
    }

    void DrawLobby(NetworkMenuController m)
    {
        var lobby = m.Lobby;
        if (lobby == null) { DrawWaiting(m); return; }
        if (m.InCar) { DrawCar(m); return; }
        Panel(NetText.Cut(NetText.Safe(lobby.GameName), 24));
        NetLobbyMember? op = null;
        for (int i = 0; i < lobby.Members.Count; i++)
            if (lobby.Members[i].Operator) { op = lobby.Members[i]; break; }
        Text(Left, 50, NetFont.Small, NetAlign.Left, NetRole.Header,
            NetText.Join(NetText.Join("OPERATOR: ", NetText.Cut(op == null ? "-" : NetText.Safe(op.Name), 20)), "   STATUS: ", StatusText(lobby.Phase)));
        DrawModeLine(m, lobby, 62);
        DrawOutLine(lobby, 71);
        DrawPlayers(m, lobby, 94, 18);
        var rows = m.LobbyRows;
        // The SETTINGS title and under it the options in two columns (the operator's; a player gets the options as text), the chat;
        // the actions (car, settings, ready, start, leave) in a column at the right.
        int options = 0, action = 0;
        for (int i = 0; i < rows.Count; i++)
            if (NetworkMenuController.IsSetting(rows[i])) options++;
        bool isOperator = _me?.Operator == true;
        int perColumn = isOperator ? (options + 1) / 2 : options;
        for (int i = 0, o = 0; i < rows.Count; i++)
        {
            if (rows[i] == LobbyRow.Settings) DrawSettingsHeader(m, i, ActionY + action++ * ActionPitch);
            else if (rows[i] == LobbyRow.Car || NetworkMenuController.IsAction(rows[i]))
                DrawLobbyRow(m, lobby, rows[i], i, ActionX, ActionY + action++ * ActionPitch, 14);
            else
            {
                bool right = o >= perColumn;
                DrawLobbyRow(m, lobby, rows[i], i, right ? Left + 280 : Left, OptionsY + (right ? o - perColumn : o) * OptionsPitch, right ? 30 : 34);
                o++;
            }
        }
        if (!isOperator)
        {
            DrawOptionSummary(m, lobby, 300);
            if (op != null) Text(Left + 16, 336, NetFont.Small, NetAlign.Left, NetRole.Disabled, NetText.Join("SET BY ", NetText.Safe(op.Name)), 40);
        }
        Footer(m, m.InSettings ? SettingsHint : LobbyHint);
    }

    /// <summary>The SETTINGS header: a cursor stop of the top level (ENTER opens the settings), lit while the settings are open,
    /// dimmed when nothing can be changed.</summary>
    void DrawSettingsHeader(NetworkMenuController m, int index, int y)
    {
        bool usable = m.SettingsAvailable;
        bool selected = usable && !m.InSettings && m.Selected == index;
        var role = !usable ? NetRole.Disabled : selected ? NetRole.Selected : m.InSettings ? NetRole.Header : NetRole.Label;
        Text(ActionX, y, NetFont.Normal, NetAlign.Left, role, selected ? "> SETTINGS" : "  SETTINGS");
        Text(Left, SettingsY, NetFont.Small, NetAlign.Left, usable ? NetRole.Header : NetRole.Disabled, "SETTINGS");
    }

    void DrawLobbyRow(NetworkMenuController m, NetLobbyState lobby, LobbyRow row, int index, int x, int y, int maxChars)
    {
        bool usable = m.Access(row) == OptionAccess.Editable;
        bool selected = m.Selected == index && usable;
        var role = !usable ? NetRole.Disabled : row == LobbyRow.Start ? NetRole.Good : NetRole.Label;
        string text = NetText.Cut(NetText.Join(selected ? "> " : "  ", LobbyRowText(m, lobby, row)), maxChars);
        _list.Text(x, y, NetFont.Normal, NetAlign.Left, selected ? NetRole.Selected : role, text);
    }

    /// <summary>Classic car screen (the PLAYER ONE screen of the original, without the picture): the nick as title, the model and
    /// the colour (with its swatch), done and cancel.</summary>
    void DrawCar(NetworkMenuController m)
    {
        int car = Math.Max(0, m.CarOf(_me)), colour = _me?.Colour ?? NetProtocol.NoColour;
        Panel(NetText.Cut(NetText.Upper(NetText.Safe(_me?.Name ?? m.Nick)), 24), 60, 300);
        Row(110, NetText.Join("CAR: ", CarName(m, car)), m.CarCursor == CarRow.Car);
        bool fixedColour = m.CarAccess(CarRow.Colour) != OptionAccess.Editable;
        int index = NetworkMenuController.PaletteIndex(car, colour);
        string text = NetText.Join("COLOUR: ", NetworkMenuController.PaletteSize(car) <= 1 ? "FIXED"
            : index >= 0 ? NetText.Join(NetText.Int(index + 1), "/", NetText.Int(NetworkMenuController.PaletteSize(car))) : "DEFAULT");
        Row(150, text, m.CarCursor == CarRow.Colour, fixedColour ? NetRole.Disabled : NetRole.Label);
        if (index >= 0 || NetworkMenuController.PaletteSize(car) <= 1)
            Swatch(Left + HudOverlay.TextWidth(NetText.Join("  ", text), 2) + 10, 150, index >= 0 ? colour : NetworkMenuController.PaletteFirst(car));
        Row(210, "DONE", m.CarCursor == CarRow.Done);
        Row(250, "CANCEL", m.CarCursor == CarRow.Cancel);
        Footer(m, CarHint);
    }

    /// <summary>Small rectangle in the paint of <paramref name="colour"/> (the swatch of the car menu), as high as a line of text.</summary>
    void Swatch(int x, int y, int colour) => _list.Swatch(x, y, 20, 10, MenuScene.SwatchColour(colour));

    /// <summary>Mode, series state and the player limit of the game, under the operator line.</summary>
    void DrawModeLine(NetworkMenuController m, NetLobbyState lobby, int y)
    {
        if (lobby.Mode == NetGameMode.Knockout)
        {
            // The series is the server's: the table is shown as received, nothing is derived here.
            var s = lobby.Series;
            int round = s == null ? 1 : Math.Min(s.CompletedRounds + (s.Finished ? 0 : 1), NetProtocol.KnockoutRounds);
            bool out_ = m.Spectating && s is { Finished: false };
            string text = s is { Finished: true }
                ? NetText.Join("KNOCKOUT   SERIES OVER", s.Winner >= 0
                    ? NetText.Join(" - WINNER ", NetText.Cut(NetText.Upper(NetText.Safe(s.Entries[s.Winner].Name)), 12)) : "")
                : NetText.Join(NetText.Join("KNOCKOUT   ROUND ", NetText.Int(round), "/"), NetText.Join(NetText.Int(NetProtocol.KnockoutRounds), "   "),
                    out_ ? "YOU ARE OUT - WATCHING" : m.SeriesFrozen ? "OPTIONS LOCKED" : "NEXT: START");
            Text(Left, y, NetFont.Small, NetAlign.Left, out_ ? NetRole.Warn : NetRole.Disabled, text);
        }
        else Text(Left, y, NetFont.Small, NetAlign.Left, NetRole.Disabled, "SINGLE RACE");
        Text(Left + 380, y, NetFont.Small, NetAlign.Left, NetRole.Disabled,
            NetText.Join(NetText.Join(NetText.Int(lobby.Members.Count), "/", NetText.Int(lobby.MaxPlayers)), " PLAYERS"));
    }

    /// <summary>The options as plain text (a player that is not the operator sees them but changes none).</summary>
    void DrawOptionSummary(NetworkMenuController m, NetLobbyState lobby, int y)
    {
        bool ko = lobby.Mode == NetGameMode.Knockout;
        Text(Left + 16, y, NetFont.Small, NetAlign.Left, NetRole.Label,
            NetText.Join(NetText.Join(TrackName(m, lobby.Track), "   ", lobby.Backward ? "BACKWARD" : "FORWARD"), "   ", NetText.Join(NetText.Int(lobby.Laps), " LAPS")));
        Text(Left + 16, y + 12, NetFont.Small, NetAlign.Left, NetRole.Label,
            NetText.Join(NetText.Join("BOTS ", ko ? "N/A" : BotsText(lobby.Bots)), "   TRAFFIC ", ko ? "N/A" : OnOff(lobby.Traffic)));
        Text(Left + 16, y + 24, NetFont.Small, NetAlign.Left, NetRole.Label,
            NetText.Join(NetText.Join("BOTS SKILL ", SkillText(lobby.Skill), "   CATCH-UP "), NetText.Join(OnOff(lobby.CatchUp), "   PHYSICS ", StyleText(lobby.Style))));
    }

    /// <summary>The names of the eliminated entrants of the knockout series, upper case, in the order of their elimination
    /// (empty when none): rebuilt only when the series changes.</summary>
    string OutNames(NetKnockoutState s)
    {
        if (ReferenceEquals(s, _outSeries)) return _outNames;
        var names = new List<(int Round, string Name)>();
        foreach (var e in s.Entries)
        {
            if (e.Active) continue;
            // stable: entries eliminated in the same round stay in the order of the series
            int at = names.Count;
            while (at > 0 && names[at - 1].Round > e.EliminatedRound) at--;
            names.Insert(at, (e.EliminatedRound, NetText.Upper(NetText.Safe(e.Name))));
        }
        _outSeries = s;
        _outNames = string.Join(" ", names.Select(n => n.Name));
        return _outNames;
    }

    static bool AnyOut(NetKnockoutState s)
    {
        for (int i = 0; i < s.Entries.Count; i++)
            if (!s.Entries[i].Active) return true;
        return false;
    }

    /// <summary>The eliminated entrants of the knockout series, in the order of their elimination.</summary>
    void DrawOutLine(NetLobbyState lobby, int y)
    {
        if (lobby.Series is { } s && AnyOut(s))
            Text(Left, y, NetFont.Small, NetAlign.Left, NetRole.Disabled, NetText.Join("OUT: ", OutNames(s)), 70);
    }

    void DrawWaiting(NetworkMenuController m)
    {
        Panel("NETWORK RACE", 120, 340);
        Text(_width / 2, 220, NetFont.Normal, NetAlign.Center, NetRole.Label, "WAITING FOR SERVER...");
        Footer(m, WaitingHint);
    }

    void DrawCountdown(NetworkMenuController m)
    {
        var lobby = m.Lobby;
        if (lobby == null) { DrawWaiting(m); return; }
        Panel(NetText.Cut(NetText.Safe(lobby.GameName), 24));
        Text(_width / 2, 70, NetFont.Normal, NetAlign.Center, NetRole.Label, "RACE STARTS IN");
        int secs = (m.CountdownRemainingMillis + 999) / 1000;
        Text(_width / 2, 110, NetFont.Digit, NetAlign.Center, NetRole.ValueSelected, NetText.Int(secs));
        DrawPlayers(m, lobby, 190, 18);
        DrawModeLine(m, lobby, 334);
        DrawOptionSummary(m, lobby, 346);
        DrawOutLine(lobby, 382);
        Footer(m, m.IsOperator ? CountdownOperatorHint : CountdownHint);
    }

    void DrawRaceBox(NetworkMenuController m)
    {
        _list.Dialog("RACE IN PROGRESS", m.Lobby is { } l ? NetText.Cut(NetText.Safe(l.GameName), 24) : "");
        Text(_width / 2, 270, NetFont.Small, NetAlign.Center, m.Spectating ? NetRole.Warn : NetRole.Label,
            m.Spectating ? RaceBoxOutHint : RaceBoxHint);
    }

    void DrawSummary(NetworkMenuController m)
    {
        Panel("RACE RESULTS");
        var results = m.View?.Results;
        foreach (var (col, label) in ResultColumns)
            Text(Left + 16 + col * 8, 56, NetFont.Small, NetAlign.Left, NetRole.Header, label);
        if (results == null || results.Racers.Count == 0)
            Text(Left + 16, 100, NetFont.Normal, NetAlign.Left, NetRole.Disabled, "WAITING FOR RESULTS...");
        else
        {
            for (int i = 0; i < results.Racers.Count && i < NetworkMenuController.GridCars; i++)
            {
                var r = results.Racers[i];
                bool me = m.View?.LocalCarIndex is { } local ? r.CarIndex == local
                    : r.Human && string.Equals(r.Driver, m.View?.Name, StringComparison.OrdinalIgnoreCase);
                var role = me ? NetRole.Selected : r.RaceState == 1 ? NetRole.Disabled : NetRole.Label;
                int y = 70 + i * 22;
                string time = r.RaceState == 1 ? "DNF" : NetText.Join(NetText.Time(r.RaceTicks), r.Estimated ? "*" : "");
                string best = r.BestLapTicks is { } b ? NetText.Time(b) : "-";
                Text(Left + 16, y, NetFont.Normal, NetAlign.Left, role, NetText.Int(r.Place));
                Text(Left + 16 + 4 * 8, y, NetFont.Normal, NetAlign.Left, role,
                    string.IsNullOrEmpty(r.Driver) ? NetText.Join("CAR ", NetText.Int(r.CarIndex + 1)) : NetText.Safe(r.Driver), 10);
                Text(Left + 16 + 15 * 8, y + 3, NetFont.Small, NetAlign.Left, role, ResultCarName(m, r), 25);
                Text(Left + 16 + 28 * 8, y, NetFont.Normal, NetAlign.Left, role, time);
                Text(Left + 16 + 38 * 8, y, NetFont.Normal, NetAlign.Left, role, best);
            }
        }
        if (m.Series is { } series)
            Text(Left + 16, 272, NetFont.Small, NetAlign.Left, NetRole.Disabled,
                NetText.Join(NetText.Join("ROUND ", NetText.Int(Math.Min(series.CompletedRounds, NetProtocol.KnockoutRounds)), "/"),
                    NetText.Join(NetText.Int(NetProtocol.KnockoutRounds), "   OUT: "), OutNames(series)), 70);
        for (int i = 0; i < SummaryLabels.Length; i++) Row(290 + i * 26, SummaryLabels[i], m.Selected == i);
        if (m.SummaryRemainingSeconds is > 0 and var secs)
            Text(Left, 338, NetFont.Small, NetAlign.Left, NetRole.Disabled, NetText.Join("BACK TO LOBBY IN ", NetText.Int(secs)));
        Footer(m, SummaryHint);
    }

    /// <summary>Full name of the model of a result row (the short name when the row carries no selectable model).</summary>
    static string ResultCarName(NetworkMenuController m, RacerResult r) =>
        NetProtocol.IsCarType(r.CarType) ? CarName(m, r.CarType) : NetText.Upper(NetText.Safe(r.Model));

    // ---------------- chat ----------------

    static string ChatText(ChatLine line) =>
        NetText.Upper(NetText.Join(NetText.Join(NetText.Safe(line.Sender), ": "), NetText.Safe(line.Text)));

    /// <summary>One chat text (<see cref="NetDrawItem.Chat"/>): cut to <paramref name="maxChars"/> characters for the bitmap font fallback and to the
    /// edge of the buffer (or <paramref name="maxWidth"/> pixels) by the chat font.</summary>
    void ChatItem(int x, int y, NetRole role, string text, int maxChars, int maxWidth = 0, NetAlign align = NetAlign.Left) =>
        _list.Text(x, y, NetFont.Small, align, role, NetText.Cut(text, maxChars), maxWidth, chat: true);

    /// <summary>Width of a chat text in pixels: in the chat font, else in the font of the look (as before the chat font).</summary>
    int ChatWide(string text) =>
        NetChatText.Measure(text) is var w and >= 0 ? w : Original ? Wide(NetFont.Small, text) : HudOverlay.TextWidth(text, 1);

    static NetRole ChatRole(ChatLine line, NetRole other) => line.Sender == NetProtocol.ServerChatName ? NetRole.Warn : other;

    /// <summary>Chat of the lobby, countdown and summary screens: the last lines in a free corner of the panel and, while typing,
    /// the input line in place of the footer.</summary>
    void DrawChat(NetworkChat chat, NetScreen screen, bool caret)
    {
        int count = chat.CopyRecent(_chatLines, 0);
        // Lobby: between the settings and the actions; countdown / summary: under the game options. Only as many of the last
        // lines as fit above the message line.
        (int x, int y, int fit) = screen switch
        {
            NetScreen.Lobby => (Left + 16, LobbyChatY, NetworkChat.Visible),
            NetScreen.Countdown => (Left + 16, 394, 4),
            _ => (Left + 16, 350, NetworkChat.Visible),
        };
        int first = Math.Max(0, count - fit);
        if (count == 0 && !chat.Typing) Text(x, y, NetFont.Small, NetAlign.Left, NetRole.Disabled, "PRESS T TO CHAT");
        for (int i = first; i < count; i++)
            ChatItem(x, y + (i - first) * 10, ChatRole(_chatLines[i], NetRole.Header), ChatText(_chatLines[i]), 120, Right - x);
        if (chat.Typing)
        {
            _list.Panel(Left - 20, 444, Right + 20, 468, NetPanelKind.ChatInput);
            ChatItem(Left, 448, NetRole.Selected, NetText.Join("> ", NetText.Safe(chat.Text), caret ? "_" : " "), 90, Right - Left);
            ChatItem(Left, 458, NetRole.Label, ChatHint, ChatHint.Length);
        }
    }

    /// <summary>Chat over the race picture: the recent lines fade after a while; the input line sits at the bottom.</summary>
    void DrawRaceChat(NetworkChat chat, long nowMicros)
    {
        int count = chat.CopyRecent(_chatLines, nowMicros, NetworkChat.RaceFadeMicros);
        if (Original) { DrawRaceChatOriginal(chat, count); return; }
        int plate = 0;
        for (int i = 0; i < count; i++)
            plate = Math.Max(plate, Math.Min(_width - 32, ChatWide(NetText.Cut(ChatText(_chatLines[i]), ChatChars + 16))));
        // one framed plate behind the lines, with a few pixels of the fill between the text and the 3-pixel frame
        if (count > 0) _list.Panel(2, 19, 16 + plate, RaceChatY + 2 + count * 10, NetPanelKind.ChatPlate);
        for (int i = 0; i < count; i++)
            ChatItem(8, RaceChatY + i * 10, ChatRole(_chatLines[i], NetRole.Label), ChatText(_chatLines[i]), ChatChars + 16, _width - 32);
        if (chat.Typing)
        {
            _list.Panel(-2, 428, _width + 2, 456, NetPanelKind.ChatPlate);
            ChatItem(8, 434, NetRole.Selected, NetText.Join("> ", NetText.Safe(chat.Text), "_"), 90, _width - 16);
            ChatItem(8, 444, NetRole.Label, ChatHint, ChatHint.Length);
        }
    }

    void DrawConfirm(NetworkMenuController m)
    {
        if (m.Screen == NetScreen.ConfirmStart)
        {
            // the warning of a start with players who are not ready: "start anyway" or "cancel" (default), side by side like yes / no
            if (Original)
            {
                _list.Dialog("not all players are ready", "");
                _list.Text(StartAnywayX, DialogBodyY, NetFont.Normal, NetAlign.Center, m.ConfirmYes ? NetRole.Selected : NetRole.Label, "start anyway");
                _list.Text(StartCancelX, DialogBodyY, NetFont.Normal, NetAlign.Center, m.ConfirmYes ? NetRole.Label : NetRole.Selected, "cancel");
                return;
            }
            _list.Panel(130, 175, 510, 305, NetPanelKind.Data);
            Text(_width / 2, 195, NetFont.Normal, NetAlign.Center, NetRole.Warn, "NOT ALL PLAYERS ARE READY");
            Text(210, 245, NetFont.Normal, NetAlign.Left, m.ConfirmYes ? NetRole.Selected : NetRole.Label, m.ConfirmYes ? "> START ANYWAY" : "  START ANYWAY");
            Text(210, 270, NetFont.Normal, NetAlign.Left, m.ConfirmYes ? NetRole.Label : NetRole.Selected, m.ConfirmYes ? "  CANCEL" : "> CANCEL");
            return;
        }
        if (Original)
        {
            // the confirm window of the original : the question on the bar, yes at x 0x118 and no at x 0x168, the chosen one lit [ref:1309]
            _list.Dialog("leave the game?", "");
            _list.Text(ConfirmYesX, DialogBodyY, NetFont.Normal, NetAlign.Center, m.ConfirmYes ? NetRole.Selected : NetRole.Label, "yes");
            _list.Text(ConfirmNoX, DialogBodyY, NetFont.Normal, NetAlign.Center, m.ConfirmYes ? NetRole.Label : NetRole.Selected, "no");
            return;
        }
        _list.Panel(130, 175, 510, 305, NetPanelKind.Data);
        Text(_width / 2, 195, NetFont.Normal, NetAlign.Center, NetRole.Value, "LEAVE THE GAME?");
        Text(210, 245, NetFont.Normal, NetAlign.Left, m.ConfirmYes ? NetRole.Selected : NetRole.Label, m.ConfirmYes ? "> YES" : "  YES");
        Text(210, 270, NetFont.Normal, NetAlign.Left, m.ConfirmYes ? NetRole.Label : NetRole.Selected, m.ConfirmYes ? "  NO" : "> NO");
    }
}
