using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>
/// The screens of the original look (change network-menu-look, design D6) that have their final layout: connect, connecting, games,
/// create and summary. Texts are in lower case (the game fonts have them), widths are measured with the game font, a text that
/// may not outgrow its column carries <c>maxWidth</c> and is cut by the backend. The left column is on the yellow board (right
/// edge <see cref="NetworkMenuLayout.BoardX"/>), the data in dark panels right of it.
/// </summary>
internal sealed partial class NetworkMenuLayout
{
    // connect / create forms: bare text on the map like the "game setup" screen of the game (label right edge 350, colon 362, value 372,
    // rows from y 165 every 26, DONE / CANCEL under them); the title is that of the car screen. The create form leaves 24 px more for the
    // < > of a selected value.
    internal const int FormLabelX = 350, FormColonX = 362, FormValueX = 372, FormCreateValueX = 396, FormRight = 612, ArrowGap = 6;
    internal const int FormTitleX = 346, ButtonsY = 262, NoteY = 306, NotePitch = 16, NoteCentre = 346;
    /// <summary>The title stands as far above the first row as the title of the car screen (50 over 84).</summary>
    internal const int FormTitleY = 165 - 34;
    internal static readonly int[] FormY = [165, 191, 217];

    // games / summary panels
    const int DataX0 = 236, DataX1 = 628, DataLeft = 246, DataRight = 616;

    /// <summary>Description under the create form, wrapped to 330 px at 13 px.</summary>
    static readonly string[] CreateNote =
        ["track, direction, laps, bots, bots skill,", "catch-up, traffic and physics are set", "by the operator in the lobby"];

    int Wide(int size, string text) => _fonts!.Font(OriginalNetBackend.FontSize(size)).Measure(text);

    /// <summary>Entry of the left column: right-aligned on the board, the selected one highlighted, an unavailable one dimmed.</summary>
    void Board(int y, string text, bool selected, bool disabled = false) =>
        _list.Text(BoardX, y, NetFont.Normal, NetAlign.Right, disabled ? NetRole.InkDisabled : selected ? NetRole.InkSelected : NetRole.Ink, text);

    /// <summary>One row of a form: <c>label : value</c>; the label of the selected row is white and its value bright, a field being
    /// edited has its value white and keeps its end (the caret) when it is too long. <paramref name="arrows"/> puts <c>&lt; &gt;</c>
    /// round the value of a selected row.</summary>
    void FormRow(int y, string label, string value, bool selected, bool editing, int valueX, bool arrows = false)
    {
        var labelRole = selected ? NetRole.Selected : NetRole.Label;
        _list.Text(FormLabelX, y, NetFont.Normal, NetAlign.Right, labelRole, label);
        _list.Text(FormColonX, y, NetFont.Normal, NetAlign.Left, labelRole, ":");
        var valueRole = editing ? NetRole.Selected : selected ? NetRole.ValueSelected : NetRole.Value;
        _list.Text(valueX, y, NetFont.Normal, NetAlign.Left, valueRole, value, FormRight - 10 - valueX, tail: editing);
        if (!arrows || !selected) return;
        _list.Text(valueX - Wide(NetFont.Normal, "<") - 4, y, NetFont.Normal, NetAlign.Left, NetRole.Muted, "<");
        _list.Text(valueX + Wide(NetFont.Normal, value) + ArrowGap, y, NetFont.Normal, NetAlign.Left, NetRole.Muted, ">");
    }

    /// <summary>Two buttons side by side in upper case like <c>DONE   CANCEL</c> of the game: the first ends at the label edge of the
    /// form, the second starts at its value edge; the selected one is white, the other light grey.</summary>
    void ButtonPair(int y, string first, bool firstSelected, string second, bool secondSelected, int x1 = FormLabelX, int x2 = FormValueX)
    {
        _list.Text(x1, y, NetFont.Normal, NetAlign.Right, firstSelected ? NetRole.Selected : NetRole.ButtonIdle, first);
        _list.Text(x2, y, NetFont.Normal, NetAlign.Left, secondSelected ? NetRole.Selected : NetRole.ButtonIdle, second);
    }

    /// <summary>The cyan upper-case title of a form without a panel, where the car screen has the nick.</summary>
    void FormTitle(string title) => _list.Text(FormTitleX, FormTitleY, NetFont.Normal, NetAlign.Center, NetRole.Header, title, 2 * (FormTitleX - 10));

    /// <summary>The 55% strip behind <paramref name="lines"/> lines of 13 px text of width <paramref name="width"/> centred at
    /// <see cref="NoteCentre"/> (the text of a bare form has no panel to read on).</summary>
    void NoteStrip(int y, int lines, int width) =>
        _list.Panel(NoteCentre - width / 2 - 8, y, NoteCentre + width / 2 + 8, y + (lines - 1) * NotePitch + 25, NetPanelKind.ChatPlate);

    void DrawConnectOriginal(NetworkMenuController m, bool caret)
    {
        FormTitle("NETWORK RACE");
        FormRow(FormY[0], "nick", Field(m, NetField.Nick, m.Nick, caret), m.Selected == 0, m.Editing == NetField.Nick, FormValueX);
        FormRow(FormY[1], "server", Field(m, NetField.Host, m.Host, caret), m.Selected == 1, m.Editing == NetField.Host, FormValueX);
        FormRow(FormY[2], "port", Field(m, NetField.Port, m.PortText, caret), m.Selected == 2, m.Editing == NetField.Port, FormValueX);
        ButtonPair(ButtonsY, "CONNECT", m.Selected == 3, "BACK", m.Selected == 4);
        // an error or a message goes on the bar over the footer, like on the other screens
        Footer(m, ConnectHint, bar: true);
    }

    void DrawConnectingOriginal(NetworkMenuController m)
    {
        _list.Dialog("network race", NetText.Join(NetText.Join("connecting to ", NetText.Cut(NetText.Safe(m.Host), 40)), ":", m.PortText));
        Footer(m, ConnectingHint);
    }

    void DrawCreateOriginal(NetworkMenuController m, bool caret)
    {
        FormTitle("CREATE GAME");
        FormRow(FormY[0], "game name", Field(m, NetField.CreateName, m.CreateName, caret), m.Selected == 0, m.Editing == NetField.CreateName, FormCreateValueX);
        FormRow(FormY[1], "max players", NetText.Int(m.MaxPlayers), m.Selected == 1, false, FormCreateValueX, arrows: true);
        FormRow(FormY[2], "mode", m.Mode == NetGameMode.Knockout ? "knockout" : "single race", m.Selected == 2, false, FormCreateValueX, arrows: true);
        ButtonPair(ButtonsY, "CREATE", m.Selected == 3, "BACK", m.Selected == 4);
        int widest = 0;
        for (int i = 0; i < CreateNote.Length; i++) widest = Math.Max(widest, Wide(NetFont.Small, CreateNote[i]));
        NoteStrip(NoteY, CreateNote.Length, widest);
        for (int i = 0; i < CreateNote.Length; i++)
            _list.Text(NoteCentre, NoteY + i * NotePitch, NetFont.Small, NetAlign.Center, NetRole.Label, CreateNote[i]);
        Footer(m, CreateHint, bar: true, pending: m.Pending ? "waiting for server..." : null);
    }

    // ---------------- games ----------------

    // columns of the games table: name, status, players, track (left edges) and laps (right edge)
    const int GName = 246, GStatus = 346, GPlayers = 436, GTrack = 500, GLapsHeader = 596;
    const int RowPitch = 26;   // the line pitch of the game's own menus (game setup: SWISS18 every 26 px)

    void DrawGamesOriginal(NetworkMenuController m)
    {
        var games = m.Games;
        var selRow = m.GamesRowAt(m.Selected, out int selGame);
        int first = selGame < 0 || selRow != GamesRow.Game ? 0 : Math.Clamp(selGame - VisibleGames + 1, 0, Math.Max(0, games.Count - VisibleGames));
        bool selectedUsable = selRow != GamesRow.Game || m.CanJoin(games[selGame]) is JoinAvailability.Available or JoinAvailability.Reserved;

        _list.Panel(DataX0, 60, DataX1, 108 + VisibleGames * RowPitch + 4, NetPanelKind.Data);
        _list.Text(GName, 64, NetFont.Normal, NetAlign.Left, NetRole.Value, "network games");
        if (games.Count > VisibleGames)
            _list.Text(DataRight, 66, NetFont.Small, NetAlign.Right, NetRole.Header,
                NetText.Join(NetText.Join(NetText.Int(first + 1), "-", NetText.Int(Math.Min(games.Count, first + VisibleGames))), " of ", NetText.Int(games.Count)));
        _list.Text(GName, 90, NetFont.Small, NetAlign.Left, NetRole.Header, "name");
        _list.Text(GStatus, 90, NetFont.Small, NetAlign.Left, NetRole.Header, "status");
        _list.Text(GPlayers, 90, NetFont.Small, NetAlign.Left, NetRole.Header, "players");
        _list.Text(GTrack, 90, NetFont.Small, NetAlign.Left, NetRole.Header, "track");
        _list.Text(GLapsHeader, 90, NetFont.Small, NetAlign.Left, NetRole.Header, "laps");
        if (games.Count == 0)
            _list.Text(PanelCentre, 160, NetFont.Normal, NetAlign.Center, NetRole.Label, m.Pending ? "waiting for server..." : "no games - create one");
        for (int i = 0; i < VisibleGames && first + i < games.Count; i++)
        {
            var g = games[first + i];
            var avail = m.CanJoin(g);
            bool sel = selRow == GamesRow.Game && selGame == first + i;
            bool usable = avail is JoinAvailability.Available or JoinAvailability.Reserved;
            var role = !usable ? NetRole.Disabled : sel ? NetRole.Selected : NetRole.Label;
            var nameRole = usable ? NetRole.Selected : NetRole.Disabled;   // a game one can join is white, one that cannot is grey
            int y = 108 + i * RowPitch;
            if (sel) _list.RowBar(DataX0 + 2, y - 3, DataX1 - 2, y + RowPitch - 3);
            _list.Text(GName, y, NetFont.Normal, NetAlign.Left, nameRole, NetText.Safe(g.Name), GStatus - GName - 6);
            _list.Text(GStatus, y, NetFont.Normal, NetAlign.Left, usable ? StatusRole(g.Phase) : NetRole.Disabled, StatusLower(g.Phase), GPlayers - GStatus - 4);
            string players = NetText.Join(NetText.Int(g.Players), "/", NetText.Int(g.MaxPlayers));
            _list.Text(GPlayers, y, NetFont.Normal, NetAlign.Left, role, players);
            if (avail == JoinAvailability.Reserved)
                _list.Text(GPlayers + Wide(NetFont.Normal, players) + 5, y + 4, NetFont.Small, NetAlign.Left, NetRole.Header, "yours");
            else if (avail == JoinAvailability.Full)
                _list.Text(GPlayers + Wide(NetFont.Normal, players) + 5, y + 4, NetFont.Small, NetAlign.Left, NetRole.Disabled, "full");
            _list.Text(GTrack, y, NetFont.Normal, NetAlign.Left, usable ? NetRole.Value : NetRole.Disabled, NetText.Lower(TrackName(m, g.Track)), GLapsHeader - GTrack - 6);
            _list.Text(DataRight, y, NetFont.Normal, NetAlign.Right, role, NetText.Int(g.Laps));
        }

        // the left column: ENTER on the selected game joins it (the line is a label, not a stop of the cursor)
        Board(200, "join", false, games.Count == 0 || !selectedUsable);
        Board(236, "create game", selRow == GamesRow.Create);
        Board(272, "refresh", selRow == GamesRow.Refresh);
        Board(308, "back", selRow == GamesRow.Back);
        Footer(m, ConnectHint, bar: true, pending: m.Pending && games.Count > 0 ? "waiting for server..." : null);
    }

    static string StatusLower(NetGamePhase p) => p switch { NetGamePhase.Lobby => "lobby", NetGamePhase.Countdown => "countdown", _ => "race" };

    // ---------------- summary ----------------

    // columns of the results table: left edges of pos, name and car; the right edges of the numbers (time, best); the star of an
    // estimated time has a slot of its own after the time so that the digits of all rows stay under each other
    internal const int SummaryTimerY = 290;
    const int RPos = 246, RName = 284, RCar = 366, RTimeRight = 538, RStarX = 541, RBestRight = 616, RCarRoom = 96;
    internal const string EstimatedLegend = "* estimated";

    void DrawSummaryOriginal(NetworkMenuController m)
    {
        var results = m.View?.Results;
        _list.Panel(DataX0, 40, DataX1, 96 + NetworkMenuController.GridCars * RowPitch + 28, NetPanelKind.Data);
        _list.Text(RPos, 44, NetFont.Normal, NetAlign.Left, NetRole.Value, "race results");
        _list.Text(RPos, 76, NetFont.Small, NetAlign.Left, NetRole.Header, "pos");
        _list.Text(RName, 76, NetFont.Small, NetAlign.Left, NetRole.Header, "name");
        _list.Text(RCar, 76, NetFont.Small, NetAlign.Left, NetRole.Header, "car");
        _list.Text(RTimeRight, 76, NetFont.Small, NetAlign.Right, NetRole.Header, "time");
        _list.Text(RBestRight, 76, NetFont.Small, NetAlign.Right, NetRole.Header, "best");
        bool anyEstimated = false;
        if (results == null || results.Racers.Count == 0)
            _list.Text(PanelCentre, 140, NetFont.Normal, NetAlign.Center, NetRole.Label, "waiting for results...");
        else
        {
            for (int i = 0; i < results.Racers.Count && i < NetworkMenuController.GridCars; i++)
            {
                var r = results.Racers[i];
                bool me = m.View?.LocalCarIndex is { } local ? r.CarIndex == local
                    : r.Human && string.Equals(r.Driver, m.View?.Name, StringComparison.OrdinalIgnoreCase);
                bool dnf = r.RaceState == 1;
                var role = me ? NetRole.ValueSelected : dnf ? NetRole.Disabled : NetRole.Selected;
                var carRole = me ? NetRole.Value : dnf ? NetRole.Disabled : NetRole.Label;
                int y = 96 + i * RowPitch;
                string time = dnf ? "DNF" : NetText.Time(r.RaceTicks);
                string best = r.BestLapTicks is { } b ? NetText.Time(b) : "-";
                _list.Text(RPos, y, NetFont.Normal, NetAlign.Left, role, NetText.Int(r.Place));
                _list.Text(RName, y, NetFont.Normal, NetAlign.Left, role,
                    string.IsNullOrEmpty(r.Driver) ? NetText.Join("car ", NetText.Int(r.CarIndex + 1)) : NetText.Safe(r.Driver), RCar - RName - 6);
                _list.Text(RCar, y + 4, NetFont.Small, NetAlign.Left, carRole, ResultCar(m, r, RCarRoom), RCarRoom);
                _list.Text(RTimeRight, y, NetFont.Normal, NetAlign.Right, role, time);
                if (!dnf && r.Estimated) { _list.Text(RStarX, y, NetFont.Normal, NetAlign.Left, role, "*"); anyEstimated = true; }
                _list.Text(RBestRight, y, NetFont.Normal, NetAlign.Right, role, best);
            }
        }
        // the foot of the panel: the state of a knockout series, and the meaning of the star when a row has one
        int footY = 96 + NetworkMenuController.GridCars * RowPitch + 4;
        int legendW = anyEstimated ? Wide(NetFont.Small, EstimatedLegend) + 16 : 0;
        if (m.Series is { } series)
            _list.Text(RPos, footY, NetFont.Small, NetAlign.Left, NetRole.Label,
                NetText.Join(NetText.Join("round ", NetText.Int(Math.Min(series.CompletedRounds, NetProtocol.KnockoutRounds)), "/"),
                    NetText.Join(NetText.Int(NetProtocol.KnockoutRounds), "   out: "), OutNames(series)), DataRight - RPos - legendW);
        if (anyEstimated) _list.Text(DataRight, footY, NetFont.Small, NetAlign.Right, NetRole.Label, EstimatedLegend);
        Board(214, "return to lobby", m.Selected == 0);
        Board(250, "exit", m.Selected == 1);
        if (m.SummaryRemainingSeconds is > 0 and var secs)
            BoardLine_(SummaryTimerY, NetFont.Small, NetText.Join("back to lobby in ", NetText.Int(secs)), NetRole.InkSoft, BoardWidth);
        Footer(m, SummaryHint, bar: true);
    }

    /// <summary>Car of a result row: the full name of the model when it fits <paramref name="room"/>, else its short code.</summary>
    string ResultCar(NetworkMenuController m, RacerResult r, int room)
    {
        if (!NetProtocol.IsCarType(r.CarType)) return NetText.Upper(NetText.Safe(r.Model));
        string full = CarName(m, r.CarType);
        return Wide(NetFont.Small, full) <= room ? full : NetworkCarNames.Short(r.CarType);
    }

    // ---------------- chat of the original look ----------------

    const int ChatX0 = DataX0, ChatX1 = DataX1, ChatY0 = 346, ChatY1 = 428, ChatLineY = 352, ChatPitch = 15;

    /// <summary>The chat in its own panel under the data panel; the line being typed has a bar of its own in the place of the
    /// footer, with the hint at its right end.</summary>
    void DrawChatOriginal(NetworkChat chat, bool caret)
    {
        int count = chat.CopyRecent(_chatLines, 0);
        _list.Panel(ChatX0, ChatY0, ChatX1, ChatY1, NetPanelKind.Data);
        if (count == 0 && !chat.Typing)
            _list.Text(DataLeft, ChatLineY, NetFont.Small, NetAlign.Left, NetRole.Disabled, ChatPlaceholder);
        int first = Math.Max(0, count - NetworkChat.Visible);
        for (int i = first; i < count; i++)
        {
            var line = _chatLines[i];
            int y = ChatLineY + (i - first) * ChatPitch;
            string nick = NetText.Join(NetText.Safe(line.Sender), ": ");
            if (line.Sender == NetProtocol.ServerChatName)
            {
                _list.Text(DataLeft, y, NetFont.Small, NetAlign.Left, NetRole.Value, NetText.Join(nick, NetText.Safe(line.Text)), ChatX1 - 10 - DataLeft, chat: true);
                continue;
            }
            int w = Math.Min(ChatWide(nick), 120);
            _list.Text(DataLeft, y, NetFont.Small, NetAlign.Left, NetRole.Header, nick, 120, chat: true);
            _list.Text(DataLeft + w, y, NetFont.Small, NetAlign.Left, NetRole.Label, NetText.Safe(line.Text), ChatX1 - 10 - DataLeft - w, chat: true);
        }
        if (!chat.Typing) return;
        _list.Panel(ChatX0, 438, ChatX1, 468, NetPanelKind.ChatInput);
        int hintW = ChatWide(ChatHint);
        string input = NetText.Join("> ", NetText.Safe(chat.Text), caret ? "_" : " ");
        // a long line shows its end (where the caret is)
        int room = ChatX1 - 10 - DataLeft - hintW - 16;
        for (int cut = 2; cut < input.Length && ChatWide(input) > room; cut++)
            input = string.Concat("> ", input.AsSpan(cut));
        _list.Text(DataLeft, 446, NetFont.Small, NetAlign.Left, NetRole.Selected, input, chat: true);
        _list.Text(ChatX1 - 10, 446, NetFont.Small, NetAlign.Right, NetRole.Label, ChatHint, chat: true);
    }

    // ---------------- over the race picture ----------------

    /// <summary>The box of a race the menu does not draw (design D6): the dialog window of the game with the name of the game; the
    /// key hint in the footer.</summary>
    void DrawRaceBoxOriginal(NetworkMenuController m)
    {
        _list.Dialog("race in progress", m.Lobby is { } l ? NetText.Cut(NetText.Safe(l.GameName), 24) : "");
        Footer(m, m.Spectating ? RaceBoxOutHint : RaceBoxHint);
    }

    /// <summary>Chat plate of the race: lines of the 13 px font (ink rows <see cref="InkTop"/>..<see cref="InkBottom"/> of the text
    /// box, so a line pitch of <see cref="RaceChatPitch"/> keeps the descenders of a line clear of the next one) on one dimmed plate
    /// without a frame, <see cref="RacePad"/> px between the text and every edge; the line being typed has a bar of its own.</summary>
    internal const string ChatPlaceholder = "press T to chat";

    internal const int RaceChatPitch = 18, RacePlateX = 2, RacePlateY = 26, RacePad = 6, InkTop = 3, InkBottom = 22,
        RaceInputY0 = 430, RaceInputY1 = 464;

    void DrawRaceChatOriginal(NetworkChat chat, int count)
    {
        int textX = RacePlateX + RacePad, room = _width - 2 * RacePad - textX;
        if (count > 0)
        {
            int widest = 0;
            for (int i = 0; i < count; i++)
                widest = Math.Max(widest, Math.Min(room, ChatWide(ChatFull(_chatLines[i]))));
            int lastY = RacePlateY + 2 + (count - 1) * RaceChatPitch;
            _list.Panel(RacePlateX, RacePlateY, textX + widest + RacePad, lastY + InkBottom + RacePad - 1, NetPanelKind.ChatPlate);
            for (int i = 0; i < count; i++)
            {
                var line = _chatLines[i];
                int y = RacePlateY + 2 + i * RaceChatPitch;
                if (line.Sender == NetProtocol.ServerChatName)
                {
                    _list.Text(textX, y, NetFont.Small, NetAlign.Left, NetRole.Value, ChatFull(line), room, chat: true);
                    continue;
                }
                string nick = NetText.Join(NetText.Safe(line.Sender), ": ");
                int w = Math.Min(ChatWide(nick), 120);
                _list.Text(textX, y, NetFont.Small, NetAlign.Left, NetRole.Header, nick, 120, chat: true);
                _list.Text(textX + w, y, NetFont.Small, NetAlign.Left, NetRole.Selected, NetText.Safe(line.Text), room - w, chat: true);
            }
        }
        if (!chat.Typing) return;
        _list.Panel(0, RaceInputY0, _width, RaceInputY1, NetPanelKind.ChatInput);
        int y2 = RaceInputY0 + (RaceInputY1 - RaceInputY0 - (InkBottom - InkTop)) / 2 - InkTop;
        int hintW = ChatWide(ChatHint);
        string input = NetText.Join("> ", NetText.Safe(chat.Text), "_");
        int inputRoom = _width - 2 * RacePad - textX - hintW - 16;
        for (int cut = 2; cut < input.Length && ChatWide(input) > inputRoom; cut++)
            input = string.Concat("> ", input.AsSpan(cut));
        _list.Text(textX, y2, NetFont.Small, NetAlign.Left, NetRole.Selected, input, chat: true);
        _list.Text(_width - RacePad - 2, y2, NetFont.Small, NetAlign.Right, NetRole.Label, ChatHint, chat: true);
    }

    /// <summary>The whole text of a chat line (nick, colon, text) as the original look draws a server line and measures a plate.</summary>
    static string ChatFull(ChatLine line) => NetText.Join(NetText.Join(NetText.Safe(line.Sender), ": "), NetText.Safe(line.Text));
}
