using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>
/// The lobby and countdown screens of the original look (change network-menu-look, tasks 3.4 and 3.6, design D6). Left, on the yellow
/// board: the game, its mode and size, then the vertical list of the actions (car, settings, ready, RACE, leave game); right, three
/// panels that never touch: the players, the game options (two columns <c>label : value</c> in 16 px, without a title, labels in one
/// colour and values in another, the way the "game setup" screen of the game shows them; the selected row is told by colour only) and
/// the chat of <see cref="DrawChatOriginal"/>. In the settings mode the options panel is the active one and the left column dimmed.
/// The car screen (<see cref="DrawCarOriginal"/>) replaces the board and the players and options panels, like PLAYER ONE of the game.
/// </summary>
internal sealed partial class NetworkMenuLayout
{
    // the three panels on the right (the chat panel is that of DrawChatOriginal: 346..428)
    const int PlayersY0 = 20, PlayersY1 = 212, SettingsY0 = 222, SettingsY1 = 326;

    // players table: header y, first row y, pitch; left edges of the columns
    const int PlHeaderY = 25, PlRowY = 44, PlPitch = 18;
    const int PlNum = 246, PlName = 272, PlCar = 372, PlColour = 484, PlState = 516, PlLink = 580;

    // settings: the first row y and the pitch; two columns (label right edge, colon, value left edge, widest value)
    const int SetRowY = 228, SetPitch = 23;
    /// <summary>The line <c>set by &lt;operator&gt;</c> at the bottom of the options panel of a player (13 px; the panel is
    /// <see cref="SetByExtra"/> px taller for it).</summary>
    internal const int SetByY = 320, SetByExtra = 14;
    const int LabelX1 = 304, ColonX1 = 310, ValueX1 = 320, ValueW1 = 128;
    const int LabelX2 = 522, ColonX2 = 528, ValueX2 = 538, ValueW2 = 78;

    /// <summary>Lines of the board: the game, mode, players, series; then the actions.</summary>
    const int BoardGameY = 100, BoardLine = 24, BoardFirstY = 204, BoardPitch = 36, BoardLeaveOperatorY = 390, BoardWidth = 115;
    /// <summary>Leave game of the operator when the <c>n not ready</c> hint is hidden (54 under RACE; with the hint it is at <see cref="BoardLeaveOperatorY"/>).</summary>
    internal const int BoardLeaveCompactY = 366;

    /// <summary>The actions move down by this much when the board has four lines of text above them (a knockout), to keep 36 px from the
    /// last line; the hint of the operator under RACE (<c>n not ready</c>) is <see cref="NotReadyDy"/> under it.</summary>
    const int BoardShift = 10, NotReadyDy = 48;

    static readonly LobbyRow[] LeftSettings = [LobbyRow.Track, LobbyRow.Direction, LobbyRow.Laps, LobbyRow.Style];
    static readonly LobbyRow[] RightSettings = [LobbyRow.Bots, LobbyRow.Skill, LobbyRow.CatchUp, LobbyRow.Traffic];

    static string OriginalLabel(LobbyRow row) => row switch
    {
        LobbyRow.Track => "track", LobbyRow.Direction => "direction", LobbyRow.Laps => "laps",
        LobbyRow.Bots => "bots", LobbyRow.Skill => "bots skill", LobbyRow.CatchUp => "catch-up", LobbyRow.Traffic => "traffic", _ => "physics",
    };

    static string OnOffLower(bool on) => on ? "on" : "off";

    /// <summary>Value of a lobby setting in the lower case of the game's menus (<c>n/a</c> for a row without effect in the mode).</summary>
    string SettingValue(NetworkMenuController m, NetLobbyState lobby, LobbyRow row)
    {
        bool na = m.Access(row) == OptionAccess.NotApplicable;
        return row switch
        {
            LobbyRow.Track => NetText.Lower(TrackName(m, lobby.Track)),
            LobbyRow.Direction => lobby.Backward ? "backward" : "forward",
            LobbyRow.Laps => NetText.Int(lobby.Laps),
            LobbyRow.Bots => na ? "n/a" : lobby.Bots == NetProtocol.AutoBots ? "auto" : NetText.Int(lobby.Bots),
            LobbyRow.Skill => lobby.Skill == 0 ? "beginner" : "expert",
            LobbyRow.CatchUp => OnOffLower(lobby.CatchUp),
            LobbyRow.Traffic => na ? "n/a" : OnOffLower(lobby.Traffic),
            _ => lobby.Style switch { 0 => "simulation", 1 => "wild", _ => "arcade" },
        };
    }

    /// <summary>A line of the board in a chosen font size (the left column of the original look).</summary>
    void BoardLine_(int y, int size, string text, NetRole role, int maxWidth = 0) =>
        _list.Text(BoardX, y, size, NetAlign.Right, role, text, maxWidth);

    /// <summary>Name of the game in the black of the board, under it in the lighter brown (<see cref="NetRole.InkSoft"/>): the mode (not
    /// when it is the name itself), the number of players and, in a knockout, the state of the series. Returns how far the actions
    /// below must move down: <see cref="BoardShift"/> when there are four lines, else 0.</summary>
    int BoardInfo(NetworkMenuController m, NetLobbyState lobby)
    {
        string name = NetText.Safe(lobby.GameName);
        BoardLine_(BoardGameY, NetFont.Normal, name, NetRole.Ink, BoardWidth);
        bool ko = lobby.Mode == NetGameMode.Knockout;
        string mode = ko ? "knockout" : "single race";
        int line = 1;
        if (!string.Equals(name, mode, StringComparison.OrdinalIgnoreCase))
            BoardLine_(BoardGameY + line++ * BoardLine, NetFont.Normal, mode, NetRole.InkSoft, BoardWidth);
        BoardLine_(BoardGameY + line++ * BoardLine, NetFont.Normal,
            NetText.Join(NetText.Join(NetText.Int(lobby.Members.Count), "/", NetText.Int(lobby.MaxPlayers)), " players"), NetRole.InkSoft, BoardWidth);
        if (ko)
        {
            var s = lobby.Series;
            string state = s is { Finished: true } ? "series over" : m.SeriesFrozen ? "options locked"
                : NetText.Join(NetText.Join("round ", NetText.Int(s == null ? 1 : Math.Min(s.CompletedRounds + 1, NetProtocol.KnockoutRounds)), "/"),
                    NetText.Int(NetProtocol.KnockoutRounds));
            BoardLine_(BoardGameY + line++ * BoardLine, NetFont.Normal, state, NetRole.InkSoft, BoardWidth);
        }
        return line >= 4 ? BoardShift : 0;
    }

    void DrawWaitingOriginal(NetworkMenuController m)
    {
        _list.Dialog("network race", "waiting for server...");
        Footer(m, WaitingHint);
    }

    void DrawLobbyOriginal(NetworkMenuController m)
    {
        var lobby = m.Lobby;
        if (lobby == null) { DrawWaitingOriginal(m); return; }
        var rows = m.LobbyRows;
        bool op = _me?.Operator == true, inSettings = m.InSettings;
        if (m.InCar) { DrawCarOriginal(m); return; }

        int shift = BoardInfo(m, lobby);
        // the vertical list of the actions; in the settings mode the whole column is dimmed
        for (int i = 0, slot = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (NetworkMenuController.IsSetting(row)) continue;
            bool usable = !inSettings && m.Access(row) == OptionAccess.Editable;
            bool selected = usable && m.Selected == i;
            var role = !usable ? NetRole.InkDisabled : selected ? NetRole.InkSelected : NetRole.Ink;
            // car, settings (operator), ready, RACE one under the other; leave game last (further down under the big RACE)
            int y = BoardFirstY + shift + slot++ * BoardPitch;
            switch (row)
            {
                case LobbyRow.Car:
                    // the model on the line itself: LEFT/RIGHT change it, ENTER opens the car screen
                    BoardLine_(y, NetFont.Normal, _me == null ? "car" : NetText.Join("car : ", NetText.Lower(m.CarShort(m.CarOf(_me)))), role, BoardWidth);
                    break;
                case LobbyRow.Settings: BoardLine_(y, NetFont.Normal, "settings", role); break;
                case LobbyRow.Ready: BoardLine_(y, NetFont.Normal, _me?.Ready == true ? "ready : yes" : "ready : no", role); break;
                case LobbyRow.Start:
                    BoardLine_(y, NetFont.Digit, "RACE", role);
                    // the operator sees how many players hold the start back (the same count as the warning of ENTER)
                    if (m.NotReadyCount is > 0 and var waiting)
                        BoardLine_(y + NotReadyDy, NetFont.Small, NetText.Join(NetText.Int(waiting), " not ready"), NetRole.InkWarn); break;
                default:
                    // the operator's leave stands further down under the big RACE, and one more line down when the hint takes the room
                    BoardLine_(op ? (m.NotReadyCount > 0 ? BoardLeaveOperatorY : BoardLeaveCompactY) + shift : y, NetFont.Normal, "leave game", role);
                    break;
            }
        }

        DrawPlayersOriginal(m, lobby, countdown: false);
        DrawSettingsOriginal(m, lobby, op ? SettingsMode.Operator : SettingsMode.Show);
        if (!op && Operator(lobby) is { } setter)
            _list.Text(DataLeft, SetByY, NetFont.Small, NetAlign.Left, NetRole.Disabled, NetText.Join("set by ", NetText.Safe(setter.Name)), DataRight - DataLeft);
        Footer(m, inSettings ? SettingsHint : LobbyHint, bar: true);
    }

    static NetLobbyMember? Operator(NetLobbyState lobby)
    {
        for (int i = 0; i < lobby.Members.Count; i++)
            if (lobby.Members[i].Operator) return lobby.Members[i];
        return null;
    }

    // ---------------- players ----------------

    void DrawPlayersOriginal(NetworkMenuController m, NetLobbyState lobby, bool countdown)
    {
        _list.Panel(DataX0, PlayersY0, DataX1, PlayersY1, NetPanelKind.Data);
        if (countdown) { DrawCountdownHead(m, lobby); return; }
        _list.Text(PlNum, PlHeaderY, NetFont.Small, NetAlign.Left, NetRole.Header, "#");
        _list.Text(PlName, PlHeaderY, NetFont.Small, NetAlign.Left, NetRole.Header, "name");
        _list.Text(PlCar, PlHeaderY, NetFont.Small, NetAlign.Left, NetRole.Header, "car");
        _list.Text(PlColour, PlHeaderY, NetFont.Small, NetAlign.Left, NetRole.Header, "col");
        _list.Text(PlState, PlHeaderY, NetFont.Small, NetAlign.Left, NetRole.Header, "state");
        _list.Text(PlLink, PlHeaderY, NetFont.Small, NetAlign.Left, NetRole.Header, "link");
        if (lobby.Members.Count == 0)
            _list.Text(PanelCentre, 90, NetFont.Normal, NetAlign.Center, NetRole.Label, "no players");
        for (int i = 0; i < lobby.Members.Count && i < NetworkMenuController.GridCars; i++)
        {
            var p = lobby.Members[i];
            int y = PlRowY + i * PlPitch;
            bool me = p == _me, gone = !p.Connected;
            var role = gone ? NetRole.Disabled : me ? NetRole.ValueSelected : NetRole.Selected;   // "me" by colour only: no bar, the table has no cursor
            _list.Text(PlNum, y, NetFont.Medium, NetAlign.Left, gone ? NetRole.Disabled : NetRole.Value,
                NetText.Join(p.Operator ? "*" : "", NetText.Int(i + 1)));
            _list.Text(PlName, y, NetFont.Medium, NetAlign.Left, role, NetText.Safe(p.Name), PlCar - PlName - 6);
            _list.Text(PlCar, y + 3, NetFont.Small, NetAlign.Left, gone ? NetRole.Disabled : NetRole.Label,
                m.CarShort(m.CarOf(p)), PlColour - PlCar - 8);
            if (NetworkMenuController.PaletteIndex(m.CarOf(p), p.Colour) >= 0) _list.Swatch(PlColour + 2, y + 3, 20, 10, MenuScene.SwatchColour(p.Colour));
            else _list.Text(PlColour + 2, y, NetFont.Medium, NetAlign.Left, gone ? NetRole.Disabled : NetRole.Label, ColourShort(p.Colour));
            _list.Text(PlState, y, NetFont.Medium, NetAlign.Left, gone ? NetRole.Disabled : p.Ready ? NetRole.Good : NetRole.Label,
                p.Ready ? "ready" : "waiting", PlLink - PlState - 4);
            _list.Text(PlLink, y, NetFont.Medium, NetAlign.Left, p.Connected ? NetRole.Muted : NetRole.Bad, p.Connected ? "ok" : "LOST", DataRight - PlLink);
        }
        // knockout: who is out, or that the player is
        if (lobby.Series is { } s && (m.Spectating && !s.Finished || AnyOut(s)))
        {
            bool out_ = m.Spectating && !s.Finished;
            _list.Text(PlNum, PlRowY + NetworkMenuController.GridCars * PlPitch + 2, NetFont.Small, NetAlign.Left,
                out_ ? NetRole.Disabled : NetRole.Value, out_ ? "you are out - watching" : NetText.Join("out: ", OutNames(s)), DataRight - PlNum);
        }
    }

    // ---------------- settings ----------------

    /// <summary>Who may change the options: the operator what the mode allows; anybody else (a player, the countdown) only sees
    /// them.</summary>
    enum SettingsMode { Operator, Show }

    /// <summary>The panel of the game options in two columns <c>label : value</c> (track, direction, laps, physics; bots, bots
    /// skill, catch-up, traffic), without a title: the labels in the colour of the labels, the values in the colour of the values; a
    /// row of the operator that cannot be changed is dimmed (a row that only shows the game to a player is not).</summary>
    void DrawSettingsOriginal(NetworkMenuController m, NetLobbyState lobby, SettingsMode mode)
    {
        int bottom = mode == SettingsMode.Show && m.Screen == NetScreen.Lobby ? SettingsY1 + SetByExtra : SettingsY1;
        _list.Panel(DataX0, SettingsY0, DataX1, bottom, m.InSettings ? NetPanelKind.DataActive : NetPanelKind.Data);
        var rows = m.LobbyRows;
        var selected = m.InSettings && m.Selected < rows.Count ? rows[m.Selected] : LobbyRow.Settings;
        for (int i = 0; i < LeftSettings.Length; i++)
            SettingRow(m, lobby, LeftSettings[i], selected, mode, SetRowY + i * SetPitch, LabelX1, ColonX1, ValueX1, ValueW1);
        for (int i = 0; i < RightSettings.Length; i++)
            SettingRow(m, lobby, RightSettings[i], selected, mode, SetRowY + i * SetPitch, LabelX2, ColonX2, ValueX2, ValueW2);
    }

    void SettingRow(NetworkMenuController m, NetLobbyState lobby, LobbyRow row, LobbyRow selected, SettingsMode mode, int y,
        int labelX, int colonX, int valueX, int valueW)
    {
        var access = m.Access(row);
        bool mine = mode == SettingsMode.Operator;
        // dimmed: cannot be changed although it is the player's to change, or has no effect in the mode
        bool dim = access == OptionAccess.NotApplicable || mine && access != OptionAccess.Editable;
        bool sel = mine && !dim && selected == row;
        var labelRole = dim ? NetRole.Disabled : sel ? NetRole.Selected : NetRole.Label;
        var valueRole = dim ? NetRole.Disabled : sel ? NetRole.ValueSelected : NetRole.Value;
        string value = SettingValue(m, lobby, row);
        _list.Text(labelX, y, NetFont.Medium, NetAlign.Right, labelRole, OriginalLabel(row));
        _list.Text(colonX, y, NetFont.Medium, NetAlign.Left, labelRole, ":");
        _list.Text(valueX, y, NetFont.Medium, NetAlign.Left, valueRole, value, valueW);
    }

    // ---------------- car screen ----------------

    // the PLAYER ONE screen of the game (TEXT entries 72, 74, 76, 82, 83): title centred at 346, rows right-aligned labels at 346,
    // the colon 12 px and the value 19 px right of it, pitch 26 (the transmission row is missing), done / cancel on one line
    const int CarTitleX = 346, CarTitleY = 50, CarLabelX = 346, CarColonX = 358, CarValueX = 365, CarRowY = 84, CarPitch = 26;
    const int CarButtonsY = 150, CarCancelX = 366, CarPreviewY = 196, CarValueW = 255;
    /// <summary>The colour bar of the game (0x54 x 0x15 at the value, 1 px right and 5 px down).</summary>
    const int BarW = 0x54, BarH = 0x15;

    /// <summary>The car screen of the lobby, modelled on PLAYER ONE: the nick in the colour of the headers, <c>car : name</c>,
    /// <c>color : bar</c>, <c>done   cancel</c> and the picture of the car under them (the board and the panels are not drawn; the
    /// chat is).</summary>
    void DrawCarOriginal(NetworkMenuController m)
    {
        int car = Math.Max(0, m.CarOf(_me)), colour = _me?.Colour ?? NetProtocol.NoColour;
        _list.Text(CarTitleX, CarTitleY, NetFont.Normal, NetAlign.Center, NetRole.Header, NetText.Upper(NetText.Safe(_me?.Name ?? m.Nick)), 2 * (CarTitleX - 10));
        CarLine(CarRowY, "car", m.CarCursor == CarRow.Car, false);
        _list.Text(CarValueX, CarRowY, NetFont.Normal, NetAlign.Left, m.CarCursor == CarRow.Car ? NetRole.ValueSelected : NetRole.Value,
            CarScreenName(m, car), CarValueW);
        bool locked = m.CarAccess(CarRow.Colour) != OptionAccess.Editable;
        int y = CarRowY + CarPitch;
        CarLine(y, "color", m.CarCursor == CarRow.Colour, locked);
        int paint = NetworkMenuController.PaletteIndex(car, colour) >= 0 ? colour : NetworkMenuController.PaletteSize(car) <= 1 ? NetworkMenuController.PaletteFirst(car) : -1;
        if (paint >= 0) _list.Swatch(CarValueX + 1, y + 5, BarW, BarH, MenuScene.SwatchColour(paint));
        else _list.Text(CarValueX, y, NetFont.Normal, NetAlign.Left, m.CarCursor == CarRow.Colour ? NetRole.ValueSelected : NetRole.Value, "default");
        ButtonPair(CarButtonsY, "DONE", m.CarCursor == CarRow.Done, "CANCEL", m.CarCursor == CarRow.Cancel, CarLabelX, CarCancelX);
        _list.Preview(MenuScene.CarPreviewX, CarPreviewY, car);
        Footer(m, CarHint, bar: true);
    }

    /// <summary>The model on the car screen: the full name when it fits the value column, else the short code.</summary>
    string CarScreenName(NetworkMenuController m, int car)
    {
        string full = NetText.Lower(CarName(m, car));
        return Wide(NetFont.Normal, full) <= CarValueW ? full : NetText.Lower(NetworkCarNames.Short(car));
    }

    /// <summary>The label and the colon of a row of the car screen.</summary>
    void CarLine(int y, string label, bool selected, bool dim)
    {
        var role = dim ? NetRole.Disabled : selected ? NetRole.Selected : NetRole.Label;
        _list.Text(CarLabelX, y, NetFont.Normal, NetAlign.Right, role, label);
        _list.Text(CarColonX, y, NetFont.Normal, NetAlign.Left, role, ":");
    }

    // ---------------- countdown ----------------

    const int ProgressY = 98, ProgressW = 18, ProgressH = 6, ProgressPitch = 26, ProgressCount = 5;
    const int CountPlayersY = 120, CountPitch = 21, CountColumn = 186;

    void DrawCountdownOriginal(NetworkMenuController m)
    {
        var lobby = m.Lobby;
        if (lobby == null) { DrawWaitingOriginal(m); return; }
        int shift = BoardInfo(m, lobby);
        BoardLine_(BoardFirstY + shift, NetFont.Normal, m.IsOperator ? "cancel start" : "leave game", NetRole.Ink);
        DrawPlayersOriginal(m, lobby, countdown: true);
        DrawSettingsOriginal(m, lobby, SettingsMode.Show);
        Footer(m, m.IsOperator ? CountdownOperatorHint : CountdownHint, bar: true);
    }

    /// <summary>Title, the digit, the bars of the seconds and the compact list of the players in two columns.</summary>
    void DrawCountdownHead(NetworkMenuController m, NetLobbyState lobby)
    {
        _list.Text(PanelCentre, 28, NetFont.Normal, NetAlign.Center, NetRole.Label, "race starts in");
        int secs = (m.CountdownRemainingMillis + 999) / 1000;
        _list.Text(PanelCentre, 50, NetFont.Digit, NetAlign.Center, NetRole.ValueSelected, NetText.Int(secs));
        int x0 = PanelCentre - (ProgressCount * ProgressPitch - (ProgressPitch - ProgressW)) / 2;
        for (int i = 0; i < ProgressCount; i++)
            _list.Swatch(x0 + i * ProgressPitch, ProgressY, ProgressW, ProgressH, i < secs ? OriginalNetBackend.Colour(NetRole.Value) : 0x3A3028);
        for (int i = 0; i < lobby.Members.Count && i < NetworkMenuController.GridCars; i++)
        {
            var p = lobby.Members[i];
            int x = DataLeft + i / 4 * CountColumn + (i / 4 > 0 ? 10 : 0), y = CountPlayersY + i % 4 * CountPitch;
            bool me = p == _me, gone = !p.Connected;
            _list.Text(x, y, NetFont.Medium, NetAlign.Left, gone ? NetRole.Disabled : NetRole.Value, NetText.Join(p.Operator ? "*" : "", NetText.Int(i + 1)));
            _list.Text(x + 26, y, NetFont.Medium, NetAlign.Left, gone ? NetRole.Disabled : me ? NetRole.ValueSelected : NetRole.Selected,
                NetText.Safe(p.Name), CountColumn - 40);
        }
    }
}
