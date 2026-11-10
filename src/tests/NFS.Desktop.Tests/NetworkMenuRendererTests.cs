using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Task 5.2 (network-game-sessions) / change network-menu-look: headless drawing of the network screens. Most tests read the
/// list of elements the layout builds (<see cref="NetDrawList"/>: texts, roles, rectangles); a few check the pixels of the classic
/// backend (glyph colours, <c>PopupBox</c> frames) with the real bitmap font; no fixed pixel snapshots.</summary>
public sealed class NetworkMenuRendererTests
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
        public RaceResults? Results { get; set; }
        public long? ReconnectRemainingMicros { get; set; }
        public bool RequestGames() => true;
        public bool CreateGame(NetGameCreate request) => true;
        public bool JoinGame(int gameId) => true;
        public bool LeaveGame() => true;
        public bool SetReady(bool ready) => true;
        public bool SetCar(int carType, int colour) => true;
        public bool SetOptions(int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style) => true;
        public bool StartRace() => true;
        public bool CancelStart() => true;
        public bool ReturnToLobby() => true;
        public void Disconnect() { Phase = SessionClientPhase.Closed; }

        /// <summary>Changes the lobby state the view serves (options, series, the roster).</summary>
        public void Edit(Func<NetLobbyState, NetLobbyState> change) => Lobby = change(Lobby!);

        public void InGame(NetGamePhase phase, bool op = true, string game = "Race", int countdownMs = 0, params string[] others)
        {
            Phase = SessionClientPhase.InGame;
            var members = new List<NetLobbyMember>();
            if (op) members.Add(new NetLobbyMember(0, Name, true, 1, true, true));
            else members.Add(new NetLobbyMember(0, "Operator", true, 0, true, true));
            if (!op) members.Add(new NetLobbyMember(1, Name, false, 2, false, true));
            for (int i = 0; i < others.Length; i++) members.Add(new NetLobbyMember(members.Count, others[i], i % 2 == 0, 3 + i, false, i != 1));
            Lobby = new NetLobbyState(7, 1, game, phase, 2, 3, 8, NetGameMode.SingleRace, countdownMs, members);
        }
    }

    static NetGameInfo Game(int id, string name, NetGamePhase phase, int players = 1, int max = 4) =>
        new(id, name, phase, players, max, 1, 3, NetGameMode.SingleRace);

    static NetworkMenuController Connecting(out FakeView view, string nick = "Me", string host = "127.0.0.1")
    {
        var menu = new NetworkMenuController(nick, host);
        view = new FakeView { Name = nick };
        for (int i = 0; i < (int)ConnectRow.Connect; i++) menu.Key(MenuKey.Down);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.Connecting, menu.Screen);
        menu.Attach(view);
        return menu;
    }

    static NetworkMenuController Browsing(out FakeView view, params NetGameInfo[] games)
    {
        var menu = Connecting(out view);
        view.Games = games;
        view.Phase = SessionClientPhase.Browsing;
        menu.Update(0);
        Assert.Equal(NetScreen.Games, menu.Screen);
        return menu;
    }

    static NetworkMenuController InGame(out FakeView view, NetGamePhase phase, bool op = true, int countdownMs = 0, string game = "Race",
        string nick = "Me", params string[] others)
    {
        var menu = Connecting(out view, nick);
        view.Phase = SessionClientPhase.Browsing;
        menu.Update(0);
        view.InGame(phase, op, game, countdownMs, others);
        menu.Update(0);
        return menu;
    }

    /// <summary>Stand-in for the full names of the game's TEXT file.</summary>
    static readonly string[] FullNames =
        ["MCLAREN F1", "FERRARI F355", "JAGUAR XJ220", "PORSCHE 911 TURBO", "LOTUS ESPRIT V8", "NISSAN 300ZX", "TOYOTA SUPRA", "ISDERA 112I",
         "ASTON MARTIN DB7", "CHEVROLET CORVETTE", "DODGE VIPER", "FORD GT90"];

    static NetworkMenuController CreateScreen(out FakeView view)
    {
        var menu = Browsing(out view);
        for (int i = 0; i < 10 && menu.GamesRowAt(menu.Selected, out _) != GamesRow.Create; i++) menu.Key(MenuKey.Down);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.CreateOptions, menu.Screen);
        return menu;
    }

    static NetworkMenuController SummaryScreen(out FakeView view, RaceResults? results)
    {
        var menu = InGame(out view, NetGamePhase.Race, others: "Bob");
        Assert.Equal(NetScreen.Race, menu.Screen);
        view.ResultsId = 1;
        view.Results = results;
        view.InGame(NetGamePhase.Lobby, true, "Race", 0, "Bob");
        menu.Update(1);
        Assert.Equal(NetScreen.Summary, menu.Screen);
        return menu;
    }

    static RaceResults SomeResults() => new(
    [
        new RacerResult(1, 0, "MCF1", true, false, 64 * 95, [64 * 31, 64 * 32, 64 * 32], 64 * 31, 2, "Me", 100 << 16, 0),
        new RacerResult(2, 1, "F355", true, true, 64 * 99, [64 * 33, 64 * 33, 64 * 33], 64 * 33, 2, "Bob", 90 << 16, 1),
        new RacerResult(3, 2, "JAGR", false, false, 0, [], null, 1, "Beefy", 0, 2),
    ]);

    // ---------------- the list of elements and the classic pixels ----------------

    /// <summary>Layout of the menu as the list of elements (the caret blinks with <paramref name="tick"/>, like the renderer's).</summary>
    static NetDrawItem[] Items(NetworkMenuController menu, NetworkChat? chat = null, int tick = 0, FrontEndFont? fonts = null)
    {
        var list = new NetDrawList();
        new NetworkMenuLayout().Build(list, menu, chat, tick / 30 % 2 == 0, 640, fonts);
        return list.Items.ToArray();
    }

    /// <summary>Layout of what goes over the race picture.</summary>
    static NetDrawItem[] RaceItems(NetworkMenuController menu, NetworkChat? chat = null, long nowMicros = 0)
    {
        var list = new NetDrawList();
        new NetworkMenuLayout().BuildRaceOverlay(list, menu, chat, nowMicros, 640);
        return list.Items.ToArray();
    }

    /// <summary>The fonts of the game (the original layout needs them for the widths) or null without the game data.</summary>
    static FrontEndFont? OriginalFonts() => NFS.Data.GameFiles.TryFindGameDir() == null ? null : new FrontEndFont();

    static int Size(int scale) => scale switch { 1 => NetFont.Small, 2 => NetFont.Normal, _ => NetFont.Digit };

    static bool IsText(NetDrawItem i, int scale) => i.Op == NetOp.Text && NetFont.ClassicScale(i.Size) == scale;

    /// <summary>The text element showing <paramref name="text"/> (whole text first, else a text that contains it; the font has only
    /// capitals, so the case does not matter) at font <paramref name="scale"/> (1, 2 or 12).</summary>
    static NetDrawItem? Find(NetDrawItem[] items, string text, int scale = 2)
    {
        foreach (var i in items)
            if (IsText(i, scale) && string.Equals(i.Text, text, StringComparison.OrdinalIgnoreCase)) return i;
        foreach (var i in items)
            if (IsText(i, scale) && i.Text!.Contains(text, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    static bool Has(NetDrawItem[] items, string text, int scale = 2) => Find(items, text, scale) != null;

    static NetRole? RoleOf(NetDrawItem[] items, string text, int scale = 2) => Find(items, text, scale)?.Role;

    /// <summary>Position of the first text element showing <paramref name="text"/>.</summary>
    static NetDrawItem At(NetDrawItem[] items, string text, int scale = 2) => Find(items, text, scale) ?? throw new Xunit.Sdk.XunitException($"no text '{text}' at scale {scale}");

    static bool SameItems(NetDrawItem[] a, NetDrawItem[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Op != y.Op || x.Align != y.Align || x.Role != y.Role || x.Kind != y.Kind || x.Size != y.Size || x.X != y.X || x.Y != y.Y
                || x.X1 != y.X1 || x.Y1 != y.Y1 || x.Rgb != y.Rgb || x.Text != y.Text || x.Text2 != y.Text2) return false;
        }
        return true;
    }

    /// <summary>Rectangle a text element covers (classic metrics, shadow included): x0, y0, x1, y1 (exclusive).</summary>
    static (int X0, int Y0, int X1, int Y1) Rect(NetDrawItem t)
    {
        int scale = NetFont.ClassicScale(t.Size), w = HudOverlay.TextWidth(t.Text!, scale) + 1;
        int x = t.Align == NetAlign.Center ? (2 * t.X - (w - 1)) / 2 : t.Align == NetAlign.Right ? t.X - (w - 1) : t.X;
        return (x, t.Y, x + w, t.Y + 5 * scale + 1);
    }

    static bool Overlap((int X0, int Y0, int X1, int Y1) a, (int X0, int Y0, int X1, int Y1) b) =>
        a.X0 < b.X1 && b.X0 < a.X1 && a.Y0 < b.Y1 && b.Y0 < a.Y1;

    static bool Inside((int X0, int Y0, int X1, int Y1) r, int x0, int y0, int x1, int y1) => r.X0 >= x0 && r.Y0 >= y0 && r.X1 <= x1 && r.Y1 <= y1;

    static FrameBuffer Render(NetworkMenuController menu, int tick = 0, bool background = false, NetSkin? skin = null)
    {
        var fb = new FrameBuffer(640, 480);
        NetworkMenuRenderer.Draw(fb, menu, tick, background, skin: skin);
        return fb;
    }

    static int Painted(FrameBuffer fb)
    {
        int n = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4) if (fb.Rgba[i] != 0 || fb.Rgba[i + 1] != 0 || fb.Rgba[i + 2] != 0) n++;
        return n;
    }

    /// <summary>Finds <paramref name="text"/> drawn with the real bitmap font at <paramref name="scale"/>: every glyph pixel of the
    /// reference rendering must carry one common non-panel colour in the frame. Returns that colour or null when absent (the
    /// classic pixel tests).</summary>
    static uint? FindText(FrameBuffer fb, string text, int scale) => FindTextAt(fb, text, scale)?.C;

    /// <summary><see cref="FindText"/> with the top-left corner of the first match.</summary>
    static (uint C, int X, int Y)? FindTextAt(FrameBuffer fb, string text, int scale = 2)
    {
        const uint probe = 0xFFFFFF;
        var sprite = new FrameBuffer(HudOverlay.TextWidth(text, scale) + 4, 5 * scale + 4);
        HudOverlay.DrawText(sprite, 0, 0, text, probe, scale);
        var mask = new List<(int dx, int dy)>();
        for (int y = 0; y < sprite.Height; y++)
            for (int x = 0; x < sprite.Width; x++)
                if (sprite.Get(x, y) == probe) mask.Add((x, y));
        Assert.NotEmpty(mask);
        for (int oy = 0; oy + sprite.Height <= fb.Height; oy++)
            for (int ox = 0; ox + sprite.Width <= fb.Width; ox++)
            {
                uint c = fb.Get(ox + mask[0].dx, oy + mask[0].dy);
                if (c == 0 || c == 0x101820 || c == 0x141E28 || c == 0x0A0F14 || c == 0x0C1218) continue;
                bool all = true;
                foreach (var (dx, dy) in mask)
                    if (fb.Get(ox + dx, oy + dy) != c) { all = false; break; }
                if (all) return (c, ox, oy);
            }
        return null;
    }

    // ---------------- every screen ----------------

    /// <summary>The real track and full car names of the game's data when it is installed (for the PNGs), the stand-ins otherwise.</summary>
    static NetworkMenuController WithNames(NetworkMenuController menu)
    {
        IReadOnlyList<string>? cars = null, tracks = null;
        try
        {
            string dir = NFS.Data.GameFiles.FindGameDir(null);
            cars = NetworkCarNames.Load(dir);
            tracks = NetworkTrackNames.Load(dir);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { }
        menu.CarNames = cars ?? FullNames;
        menu.TrackNames = tracks;
        return menu;
    }

    static IEnumerable<(string name, NetworkMenuController menu)> AllScreens()
    {
        yield return ("connect", new NetworkMenuController("Driver"));
        yield return ("connecting", Connecting(out _));
        var refused = Connecting(out var refusedView);   // another install of the original game: the data, not the version, differ
        refusedView.Phase = SessionClientPhase.Rejected; refusedView.LastReject = new NetReject(NetRejectReason.DataMismatch);
        refused.Update(0);
        yield return ("connect_data_mismatch", refused);
        yield return ("games_empty", Browsing(out _));
        yield return ("games", Browsing(out _, Game(1, "Alpha", NetGamePhase.Lobby, 2, 4), Game(2, "Beta", NetGamePhase.Countdown, 3, 4),
            Game(3, "Gamma", NetGamePhase.Race, 4, 4), Game(4, "Delta", NetGamePhase.Lobby, 4, 4)));
        yield return ("create", CreateScreen(out _));
        yield return ("lobby_operator", InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol", "Dave"]));
        yield return ("lobby_player", InGame(out _, NetGamePhase.Lobby, false, others: ["Bob", "Carol", "Dave"]));
        // network-game options (multiplayer-fixes): single race, knockout before and after the first race, the colour palette
        var single = InGame(out var singleView, NetGamePhase.Lobby, true, 0, "Friday night", "Me", "Bob", "Carol", "Dave");
        singleView.Edit(l => l with
        {
            Track = 3, Laps = 4, Backward = true, Bots = 2, Traffic = true, Skill = 0, CatchUp = false, Style = 2,
            Members = [.. l.Members.Select((m, i) => m with { CarType = i * 3 % 12, Colour = i == 0 ? 0x3E : i == 2 ? 0x1A : NetProtocol.NoColour })],
        });
        single.Update(0);
        yield return ("lobby_single_race", WithNames(single));
        var palette = InGame(out var paletteView, NetGamePhase.Lobby, true, 0, "Friday night", "Me", "Bob");
        paletteView.Edit(l => l with { Members = [.. l.Members.Select(m => m.Name == "Me" ? m with { CarType = 3, Colour = 0x44 } : m)] });
        palette.Update(0);
        for (int i = 0; i < 2; i++) palette.Key(MenuKey.Down);   // the colour row
        yield return ("lobby_car_palette", WithNames(palette));
        var fixedColour = InGame(out var fixedView, NetGamePhase.Lobby, true, 0, "Friday night", "Me", "Bob");
        fixedView.Edit(l => l with { Members = [.. l.Members.Select(m => m.Name == "Me" ? m with { CarType = 4 } : m)] });
        fixedColour.Update(0);
        yield return ("lobby_car_single_colour", WithNames(fixedColour));
        var before = InGame(out var beforeView, NetGamePhase.Lobby, true, 0, "Knockout cup", "Me", "Bob");
        beforeView.Edit(l => l with { Mode = NetGameMode.Knockout, Skill = 0, Style = 0, Laps = 3, Members = [.. l.Members.Select(m => m.Name == "Me" ? m with { Colour = 0x20 } : m)] });
        before.Update(0);
        yield return ("lobby_knockout_before", WithNames(before));
        var after = InGame(out var afterView, NetGamePhase.Lobby, true, 0, "Knockout cup", "Me", "Bob");
        afterView.Edit(l => l with { Mode = NetGameMode.Knockout, Skill = 0, Style = 0, Laps = 3, Series = Series(2, me0Out: false), Members = [.. l.Members.Select(m => m.Name == "Me" ? m with { Colour = 0x20 } : m)] });
        after.Update(0);
        yield return ("lobby_knockout_after", WithNames(after));
        yield return ("countdown", InGame(out _, NetGamePhase.Countdown, true, 3200, others: ["Bob"]));
        yield return ("race", InGame(out _, NetGamePhase.Race, true, others: "Bob"));
        yield return ("summary", WithNames(SummaryScreen(out _, SomeResults())));
        yield return ("summary_empty", SummaryScreen(out _, null));
        var confirm = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        confirm.RequestExit();
        yield return ("confirm", confirm);
        var lost = InGame(out var lostView, NetGamePhase.Lobby, true, others: "Bob");
        lostView.Phase = SessionClientPhase.Reconnecting;
        lost.Update(2);
        yield return ("reconnecting", lost);
        var failed = Connecting(out var failedView);
        failedView.Phase = SessionClientPhase.Rejected;
        failedView.LastReject = new NetReject(NetRejectReason.NameTaken);
        failed.Update(1);
        yield return ("connect_error", failed);
    }

    /// <summary>A scene of <see cref="ExtraScenes"/>: the menu with a chat, or the overlay over the race picture.</summary>
    sealed record Scene(string Name, NetworkMenuController Menu, NetworkChat? Chat = null, bool Overlay = false, int Tick = 0)
    {
        public NetDrawItem[] Items() => Overlay ? RaceItems(Menu, Chat) : NetworkMenuRendererTests.Items(Menu, Chat, Tick);

        public FrameBuffer Draw(NetSkin? skin = null)
        {
            var fb = new FrameBuffer(640, 480);
            if (Overlay)
            {
                for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) fb.Set(x, y, 0x506070);   // the race picture
                NetworkMenuRenderer.DrawRaceOverlay(fb, Menu, Chat, 0, skin);
            }
            else NetworkMenuRenderer.Draw(fb, Menu, Tick, true, Chat, skin: skin);
            return fb;
        }
    }

    /// <summary>Scenes with the chat, the race overlays and long texts (not covered by <see cref="AllScreens"/>).</summary>
    /// <summary>The operator pressed RACE while Eve is connected and not ready: the warning window.</summary>
    static NetworkMenuController NotReadyStart()
    {
        var menu = NotReadyLobby();
        menu.Key(MenuKey.Enter);
        return menu;
    }

    static IEnumerable<Scene> ExtraScenes()
    {
        static NetworkChat Chat(bool typing)
        {
            var chat = ChatWith(4);
            chat.Add(new ChatLine(NetProtocol.ServerChatName, "Game starts soon"), 0);
            if (typing) chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
            return chat;
        }
        var lobby = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal"]);
        yield return new("x_lobby_chat", lobby, Chat(false));
        yield return new("x_lobby_chat_typing", lobby, Chat(true));
        // the chat panel of the lobby is empty: a faint hint says how to open it; a player sees who set the game up
        yield return new("x_lobby_chat_empty", InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol"]), new NetworkChat());
        yield return new("x_lobby_player_setby", WithNames(InGame(out _, NetGamePhase.Lobby, false, others: ["Bob", "Carol", "Dave"])), new NetworkChat());
        var joined = new NetworkChat();
        joined.Add(new ChatLine(NetProtocol.ServerChatName, "Bob joined"), 0);
        joined.Add(new ChatLine("Bob", "hi all"), 0);
        joined.Add(new ChatLine(NetProtocol.ServerChatName, "Carol lost connection"), 0);
        joined.Add(new ChatLine(NetProtocol.ServerChatName, "Carol is back"), 0);
        joined.Add(new ChatLine(NetProtocol.ServerChatName, "Dave left"), 0);
        yield return new("x_lobby_server_lines", InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol"]), joined);
        var timer = SummaryScreen(out _, SomeResults());
        timer.Update(5_000_001);   // 5 s into the 30 s of the summary
        yield return new("x_summary_timer", WithNames(timer), new NetworkChat());
        var grace = InGame(out var gv, NetGamePhase.Lobby, true, others: "Bob");
        gv.Phase = SessionClientPhase.Reconnecting;
        gv.ReconnectRemainingMicros = 21_400_000;
        grace.Update(2);
        yield return new("x_reconnecting_seconds", grace);
        var inSettings = WithNames(InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol", "Dave"]));
        inSettings.Key(MenuKey.Up); inSettings.Key(MenuKey.Enter); inSettings.Key(MenuKey.Down); inSettings.Key(MenuKey.Down);
        yield return new("x_lobby_settings", inSettings, Chat(false));
        yield return new("x_lobby_car", CarScreenLobby(3, 0x44), Chat(false));
        var carColour = CarScreenLobby(3, NetProtocol.NoColour, op: false);
        carColour.Key(MenuKey.Down);
        yield return new("x_lobby_car_player", carColour);
        var playerSettings = WithNames(InGame(out _, NetGamePhase.Lobby, false, others: ["Bob", "Carol", "Dave"]));
        playerSettings.Key(MenuKey.Up); playerSettings.Key(MenuKey.Enter);
        yield return new("x_lobby_player_settings", playerSettings);
        var countdown = InGame(out _, NetGamePhase.Countdown, true, 3000, others: ["Bob", "Carol"]);
        yield return new("x_countdown_chat", countdown, Chat(true));
        yield return new("x_lobby_not_ready", NotReadyStart());
        yield return new("x_lobby_not_ready_hint", NotReadyLobby());
        yield return new("x_summary_chat", WithNames(SummaryScreen(out _, SomeResults())), Chat(false));
        var knock = InGame(out var kv, NetGamePhase.Lobby, true, 0, "Knockout cup", "Me", "Bob");
        kv.Edit(l => l with { Mode = NetGameMode.Knockout, Series = Series(2, me0Out: true) });
        knock.Update(1);
        yield return new("x_lobby_knockout_out", knock);
        var race = InGame(out _, NetGamePhase.Race, true, others: "Bob");
        yield return new("x_race_chat", race, Chat(false), Overlay: true);
        yield return new("x_race_chat_typing", race, Chat(true), Overlay: true);
        var cutoff = InGame(out var cv, NetGamePhase.Race);
        cv.Lobby = cv.Lobby! with { CutoffMillis = 9000 };
        cutoff.Update(1_000_000);
        yield return new("x_race_cutoff", cutoff, Overlay: true);
        var spectator = InGame(out var sv, NetGamePhase.Race);
        sv.Lobby = sv.Lobby! with { Mode = NetGameMode.Knockout, Series = Series(1, me0Out: true) };
        spectator.Update(2);
        spectator.WatchedName = "BOB";
        yield return new("x_race_spectator", spectator, Overlay: true);
        yield return new("x_race_spectator_box", spectator);
        var lostRace = InGame(out var lrv, NetGamePhase.Race);
        lrv.Phase = SessionClientPhase.Reconnecting;
        lostRace.Update(2);
        yield return new("x_race_reconnecting", lostRace, Overlay: true);
        var raceConfirm = InGame(out _, NetGamePhase.Race);
        raceConfirm.RequestExit();
        yield return new("x_race_confirm", raceConfirm, Overlay: true);
        var confirmYes = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        confirmYes.RequestExit();
        confirmYes.Key(MenuKey.Left);
        yield return new("x_confirm_yes", confirmYes);
        var longConnect = new NetworkMenuController(Long, Long, 27100);
        longConnect.Key(MenuKey.Down); longConnect.Key(MenuKey.Enter);
        yield return new("x_connect_long_editing", longConnect);
        var longGames = Browsing(out var lgv, Game(1, Long[..24], NetGamePhase.Countdown, 4, 4));
        lgv.Games = [.. Enumerable.Range(1, 12).Select(i => Game(i, Long[..24], (NetGamePhase)(i % 3), i % 5, 4))];
        longGames.Update(1);
        for (int i = 0; i < 9; i++) longGames.Key(MenuKey.Down);
        yield return new("x_games_long_scrolled", longGames);
        var pending = Browsing(out var pv, Game(1, "Alpha", NetGamePhase.Lobby));
        pv.Pending = true;
        yield return new("x_games_pending", pending);
        var longLobby = InGame(out var llv, NetGamePhase.Lobby, false, 0, Long, Long[..24], Long[..24], Long[..24]);
        llv.Lobby = llv.Lobby! with { Members = [.. llv.Lobby.Members.Select(m => m with { Name = m.Operator ? Long[..24] : m.Name })] };
        yield return new("x_lobby_long", longLobby);
        var editing = CreateScreen(out _);
        editing.Key(MenuKey.Enter);
        yield return new("x_create_editing", editing);
        yield return new("x_create_editing_blink", editing, Tick: 30);
        var waiting = InGame(out var wv, NetGamePhase.Lobby);
        wv.Lobby = null;
        waiting.Update(3);
        yield return new("x_waiting", waiting);
    }

    [Fact]
    public void EveryScreen_RendersWithoutExceptionAndDrawsSomething()
    {
        foreach (var (name, menu) in AllScreens())
        {
            var fb = Render(menu);
            Assert.True(Painted(fb) > 800, $"{name}: nothing drawn");
            // with the background option the whole frame is covered
            var full = Render(menu, background: true);
            Assert.True(Painted(full) > Painted(fb), $"{name}: background missing");
        }
    }

    /// <summary>The original look when the game data is there (the menu scene owns the fonts and the art), else null.</summary>
    static NetSkin? OriginalSkin() => NFS.Data.GameFiles.TryFindGameDir() == null
        ? null : new NetSkin(new MenuScene(MenuController.Load()), NetMenuLook.Original);

    [Fact]
    public void WithoutGameResources_TheOriginalLookFallsBackToClassicWithoutAnException()
    {
        // no menu scene (no fonts, no art), or classic chosen: the very pixels of the classic backend, whatever the screen
        NetSkin[] skins = [new NetSkin(null, NetMenuLook.Original), new NetSkin(null, NetMenuLook.Classic)];
        foreach (var skin in skins)
        {
            Assert.False(skin.UsesOriginal);
            foreach (var (name, menu) in AllScreens())
                Assert.True(Render(menu, 3, background: true).Rgba.AsSpan().SequenceEqual(Render(menu, 3, background: true, skin: skin).Rgba), name);
            foreach (var scene in ExtraScenes())
                Assert.True(scene.Draw().Rgba.AsSpan().SequenceEqual(scene.Draw(skin).Rgba), scene.Name);
        }
    }

    [GameDataFact]
    public void ClassicChosen_IgnoresTheGameResourcesEvenWhenTheyAreThere()
    {
        var scene = new MenuScene(MenuController.Load());
        var classic = new NetSkin(scene, NetMenuLook.Classic);
        Assert.False(classic.UsesOriginal);
        Assert.True(new NetSkin(scene, NetMenuLook.Original).UsesOriginal);
        var (_, menu) = AllScreens().First();
        Assert.True(Render(menu, 0, true).Rgba.AsSpan().SequenceEqual(Render(menu, 0, true, classic).Rgba));
    }

    [GameDataFact]
    public void OriginalLook_DrawsTheBackdropAndReadableTextOnEveryScreen()
    {
        var skin = OriginalSkin()!;
        foreach (var (name, menu) in AllScreens())
        {
            var original = Render(menu, 0, background: true, skin: skin);
            var classic = Render(menu, 0, background: true);
            Assert.False(original.Rgba.AsSpan().SequenceEqual(classic.Rgba), name);
            // the logo of the game is there (the classic backdrop is a dark chequer without it)
            int logo = 0;
            for (int y = 20; y < 90; y++) for (int x = 10; x < 200; x++) if (original.Get(x, y) > 0x404040) logo++;
            Assert.True(logo > 500, $"{name}: logo");
            // and so are texts: pixels of the label / value / selected colours (coverage scales them down, so only the brightest count)
            int text = 0;
            for (int y = 0; y < 480; y++) for (int x = 220; x < 640; x++) if ((original.Get(x, y) >> 8 & 255) > 0x90) text++;
            Assert.True(text > 100, $"{name}: text");
        }
    }

    [GameDataFact]
    public void OriginalLook_DrawsTheRaceOverlayWithoutBackdrop()
    {
        var skin = OriginalSkin()!;
        foreach (var scene in ExtraScenes().Where(s => s.Overlay))
        {
            var fb = scene.Draw(skin);
            Assert.Equal(0x506070u, fb.Get(630, 470));    // the race picture is left alone
            Assert.False(fb.Rgba.AsSpan().SequenceEqual(scene.Draw().Rgba), scene.Name);
        }
    }

    [GameDataFact]
    public void OriginalLook_LobbyFrameAllocatesNothingOnceWarm()
    {
        var skin = OriginalSkin()!;
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol"]);
        var chat = ChatWith(3);
        var fb = new FrameBuffer(640, 480);
        for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, menu, i, true, chat, skin: skin);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) NetworkMenuRenderer.Draw(fb, menu, 4 + i, true, chat, skin: skin);
        Assert.True((GC.GetAllocatedBytesForCurrentThread() - before) / 8 < 64);
    }

    [Fact]
    public void EveryScreen_CanBeWrittenAsPng()
    {
        string? env = Environment.GetEnvironmentVariable("NFS_NETMENU_PNG_DIR");
        string dir = env is { Length: > 0 } ? env : Path.Combine(Path.GetTempPath(), "nfs2se_netmenu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // with the game data also the original look: orig_<name>.png (without it the classic look only)
        var skin = OriginalSkin();
        try
        {
            foreach (var (name, menu) in AllScreens())
            {
                string path = Path.Combine(dir, name + ".png");
                Render(menu, background: true).SavePng(path);
                var bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 200);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
                if (skin == null) continue;
                path = Path.Combine(dir, "orig_" + name + ".png");
                Render(menu, background: true, skin: skin).SavePng(path);
                Assert.True(File.ReadAllBytes(path).Length > 200);
            }
            foreach (var scene in ExtraScenes())
            {
                string path = Path.Combine(dir, scene.Name + ".png");
                scene.Draw().SavePng(path);
                Assert.True(File.ReadAllBytes(path).Length > 200);
                if (skin == null) continue;
                path = Path.Combine(dir, "orig_" + scene.Name + ".png");
                scene.Draw(skin).SavePng(path);
                Assert.True(File.ReadAllBytes(path).Length > 200);
            }
            // the network pause over the race picture (not a scene of the menu): classic and the original look
            for (int selection = 0; selection < 2; selection++)
                foreach (var look in new[] { ("x_pause", (NetSkin?)null), ("orig_x_pause", skin) })
                {
                    if (look.Item2 == null && look.Item1.StartsWith("orig_")) continue;
                    var fb = RacePicture();
                    NetworkMenuRenderer.DrawPause(fb, selection, look.Item2);
                    string path = Path.Combine(dir, look.Item1 + selection + ".png");
                    fb.SavePng(path);
                    Assert.True(File.ReadAllBytes(path).Length > 200);
                }
        }
        finally { if (env is not { Length: > 0 }) Directory.Delete(dir, true); }
    }

    [Fact]
    public void EveryScreen_ListHasKnownSizesAndNoEmptyOrBackdropItems()
    {
        var all = AllScreens().Select(s => (s.name, items: Items(s.menu))).Concat(ExtraScenes().Select(s => (s.Name, items: s.Items())));
        foreach (var (name, items) in all)
        {
            Assert.NotEmpty(items);
            foreach (var i in items)
            {
                Assert.NotEqual(NetOp.Backdrop, i.Op);       // the renderer adds the backdrop, the layout never does
                if (i.Op != NetOp.Text) continue;
                Assert.True(i.Size is NetFont.Small or NetFont.Medium or NetFont.Normal or NetFont.Digit, $"{name}: size {i.Size}");
                Assert.False(string.IsNullOrEmpty(i.Text), $"{name}: empty text at {i.X},{i.Y}");
                Assert.All(i.Text!, c => Assert.InRange(c, ' ', '~'));      // only characters the fonts can draw
            }
        }
    }

    [Fact]
    public void EveryScreen_TextsDoNotOverlapEachOther()
    {
        var all = AllScreens().Select(s => (s.name, items: Items(s.menu))).Concat(ExtraScenes().Select(s => (s.Name, items: s.Items())));
        foreach (var (name, items) in all)
        {
            var texts = items.Where(i => i.Op == NetOp.Text).ToArray();
            for (int a = 0; a < texts.Length; a++)
                for (int b = a + 1; b < texts.Length; b++)
                    Assert.False(Overlap(Rect(texts[a]), Rect(texts[b])),
                        $"{name}: '{texts[a].Text}' {Rect(texts[a])} overlaps '{texts[b].Text}' {Rect(texts[b])}");
        }
    }

    [Fact]
    public void EveryScreen_TextsKeepInsideTheirPanelOrTheBuffer()
    {
        var all = AllScreens().Select(s => (s.name, items: Items(s.menu))).Concat(ExtraScenes().Select(s => (s.Name, items: s.Items())));
        foreach (var (name, items) in all)
            foreach (var t in items.Where(i => i.Op == NetOp.Text))
            {
                var r = Rect(t);
                Assert.True(Inside(r, 0, 0, 640, 480), $"{name}: '{t.Text}' {r} leaves the buffer");
                if (t.Y >= 20 && !name.StartsWith("x_race_"))      // over the race picture there is no panel under the chat lines
                    Assert.True(Inside(r, 40, 0, 600, 480), $"{name}: '{t.Text}' {r} leaves the panel");
            }
    }

    // ---------------- connect / connecting ----------------

    [Fact]
    public void ConnectScreen_ShowsFieldsAndEditCursor()
    {
        var menu = new NetworkMenuController("Driver", "example.org", 4242);
        var items = Items(menu);
        Assert.True(Has(items, "NICK: DRIVER"));
        Assert.True(Has(items, "SERVER: EXAMPLE.ORG"));
        Assert.True(Has(items, "PORT: 4242"));
        Assert.True(Has(items, "CONNECT"));
        Assert.True(Has(items, "BACK"));
        // the cursor follows the selection and moves the highlight
        Assert.Equal(NetRole.Selected, RoleOf(items, "> NICK: DRIVER"));
        Assert.Equal(NetRole.Label, RoleOf(items, "SERVER: EXAMPLE.ORG"));
        menu.Key(MenuKey.Down);
        Assert.False(SameItems(items, Items(menu)));
        Assert.Equal(NetRole.Selected, RoleOf(Items(menu), "> SERVER: EXAMPLE.ORG"));
        // an edit cursor blinks only while editing
        Assert.True(SameItems(Items(menu, tick: 0), Items(menu, tick: 30)));
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetField.Host, menu.Editing);
        Assert.False(SameItems(Items(menu, tick: 0), Items(menu, tick: 30)));
        Assert.True(Has(Items(menu, tick: 0), "SERVER: EXAMPLE.ORG_"));
        Assert.True(Has(Items(menu, tick: 30), "SERVER: EXAMPLE.ORG "));
        Assert.False(Same(Render(menu, 0), Render(menu, 30)));         // and the pixels follow
    }

    static bool Same(FrameBuffer a, FrameBuffer b) => a.Rgba.AsSpan().SequenceEqual(b.Rgba);

    [Fact]
    public void ConnectingScreen_ShowsTargetAndDiffersFromConnect()
    {
        var menu = Connecting(out _, host: "10.1.2.3");
        var items = Items(menu);
        Assert.True(Has(items, "CONNECTING..."));
        Assert.True(Has(items, "10.1.2.3:27100", 1));
        Assert.False(SameItems(items, Items(new NetworkMenuController("Me"))));
    }

    [Fact]
    public void ConnectionError_IsShownAndDependsOnTheReason()
    {
        var menu = Connecting(out var view);
        view.Phase = SessionClientPhase.Rejected;
        view.LastReject = new NetReject(NetRejectReason.NameTaken);
        menu.Update(1);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        var nameTaken = Items(menu);
        Assert.True(Has(nameTaken, "THAT NICK IS ALREADY USED ON THIS SERVER", 1));
        Assert.Equal(NetRole.Bad, RoleOf(nameTaken, "THAT NICK IS ALREADY USED ON THIS SERVER", 1));
        Assert.Equal(0xFF6666u, FindText(Render(menu), "THAT NICK IS ALREADY USED ON THIS SERVER", 1));    // classic pixels

        var other = Connecting(out var view2);
        view2.Phase = SessionClientPhase.Rejected;
        view2.LastReject = new NetReject(NetRejectReason.ServerShutdown);
        other.Update(1);
        Assert.False(SameItems(nameTaken, Items(other)));
        Assert.True(Has(Items(other), "THE SERVER IS SHUTTING DOWN", 1));
    }

    // ---------------- games list ----------------

    [Fact]
    public void GamesList_EmptyListShowsHintAndAllActions()
    {
        var menu = Browsing(out _);
        var items = Items(menu);
        Assert.True(Has(items, "NO GAMES - CREATE ONE"));
        foreach (var label in new[] { "CREATE GAME", "REFRESH", "BACK" }) Assert.True(Has(items, label), label);
        foreach (var label in new[] { "JOIN GAME", "GAME NAME: " }) Assert.False(Has(items, label), label + " is gone: Enter on a game joins it");
    }

    [Fact]
    public void GamesList_ShowsNamesStatusesAndPlayers()
    {
        var menu = Browsing(out _, Game(1, "Alpha", NetGamePhase.Lobby, 2, 4), Game(2, "Beta", NetGamePhase.Countdown, 3, 6),
            Game(3, "Gamma", NetGamePhase.Race, 4, 8));
        var items = Items(menu);
        foreach (var t in new[] { "ALPHA", "BETA", "GAMMA", "LOBBY", "COUNTDOWN", "RACE", "2/4", "3/6", "4/8" }) Assert.True(Has(items, t), t);
        Assert.Equal(NetRole.Good, RoleOf(items, "LOBBY"));
        Assert.Equal(NetRole.Disabled, RoleOf(items, "COUNTDOWN"));     // only a game in its lobby can be joined: the others are greyed
        Assert.Equal(NetRole.Disabled, RoleOf(items, "RACE"));
        Assert.False(Has(Items(Browsing(out _, Game(1, "Alpha", NetGamePhase.Lobby))), "COUNTDOWN"));
        // a row reads left to right: name, status, players
        Assert.True(At(items, "ALPHA").X < At(items, "LOBBY").X && At(items, "LOBBY").X < At(items, "2/4").X);
        Assert.Equal(At(items, "ALPHA").Y, At(items, "2/4").Y);
        Assert.True(At(items, "ALPHA").Y < At(items, "BETA").Y && At(items, "BETA").Y < At(items, "GAMMA").Y);
    }

    [Fact]
    public void GamesList_UnavailableGamesAreGreyedAndReservedOneIsMarked()
    {
        var games = new[]
        {
            Game(1, "Open", NetGamePhase.Lobby, 1, 4), Game(2, "Running", NetGamePhase.Race, 2, 4), Game(3, "Packed", NetGamePhase.Lobby, 4, 4),
        };
        var menu = Browsing(out _, games);
        var items = Items(menu);
        var open = RoleOf(items, "OPEN", 2);
        var running = RoleOf(items, "RUNNING", 2);
        var packed = RoleOf(items, "PACKED", 2);
        Assert.NotNull(open); Assert.NotNull(running); Assert.NotNull(packed);
        Assert.NotEqual(open, running);
        Assert.Equal(running, packed);                  // both greyed the same way
        Assert.Equal(NetRole.Disabled, running);
        Assert.Equal(NetRole.Disabled, RoleOf(items, "FULL"));
        Assert.False(Has(items, "YOURS"));

        // leaving a running game deliberately reserves the seat: the game becomes available again and is marked
        var lobby = InGame(out var view, NetGamePhase.Race, others: "Bob", game: "Running");
        view.Games = games;
        lobby.Key(MenuKey.Escape);                      // confirm
        lobby.Key(MenuKey.Left);                        // yes
        lobby.Key(MenuKey.Enter);
        view.Phase = SessionClientPhase.Browsing;
        lobby.Update(10);
        Assert.Equal(NetScreen.Games, lobby.Screen);
        var reserved = lobby.Games.Any(g => lobby.ReservedFor(g.GameId));
        if (reserved)
        {
            var items2 = Items(lobby);
            Assert.True(Has(items2, "YOURS"));
            Assert.NotEqual(running, RoleOf(items2, "RUNNING", 2));
        }
    }

    [Fact]
    public void GamesList_ScrollsToTheSelectedGameAndShowsPosition()
    {
        var games = Enumerable.Range(1, 14).Select(i => Game(i, $"G{i:00}", NetGamePhase.Lobby)).ToArray();
        var menu = Browsing(out _, games);
        var top = Items(menu);
        Assert.True(Has(top, "G01"));
        Assert.False(Has(top, "G14"));
        for (int i = 0; i < 13; i++) menu.Key(MenuKey.Down);      // first game → last game
        Assert.Equal(GamesRow.Game, menu.GamesRowAt(menu.Selected, out int gi));
        Assert.Equal(13, gi);
        var bottom = Items(menu);
        Assert.True(Has(bottom, "G14"));
        Assert.False(Has(bottom, "G01"));
        Assert.True(Has(bottom, "7-14 OF 14", 1));
        Assert.Equal(NetRole.Selected, RoleOf(bottom, "G14"));       // the selected game is lit
    }

    [Fact]
    public void GamesList_WaitingForServerIsShownWhilePending()
    {
        var menu = Browsing(out var view, Game(1, "Alpha", NetGamePhase.Lobby));
        var idle = Items(menu);
        view.Pending = true;
        var pending = Items(menu);
        Assert.False(SameItems(idle, pending));
        Assert.True(Has(pending, "WAITING FOR SERVER...", 1));
        Assert.False(Has(idle, "WAITING FOR SERVER...", 1));
    }

    // ---------------- network game options in the lobby ----------------

    static NetworkMenuController OptionsLobby(out FakeView view, bool op = true, Func<NetLobbyState, NetLobbyState>? change = null)
    {
        var menu = InGame(out view, NetGamePhase.Lobby, op, 0, "Cup", "Me", "Bob", "Carol");
        menu.CarNames = FullNames;
        if (change != null) { view.Edit(change); menu.Update(0); }
        return menu;
    }

    [Fact]
    public void Lobby_OperatorSeesEveryOptionAsARowWithItsValue()
    {
        var menu = OptionsLobby(out _, change: l => l with { Laps = 5, Bots = 3, Traffic = true, Skill = 0, CatchUp = false, Style = 2, Backward = true });
        var items = Items(menu);
        foreach (var row in new[] { "TRACK: TRACK 3", "DIRECTION: BACKWARD", "LAPS: 5", "BOTS: 3", "BOTS SKILL: BEGINNER", "CATCH-UP: OFF", "TRAFFIC: ON",
                     "PHYSICS: ARCADE", "START RACE", "LEAVE GAME" })
            Assert.True(Has(items, row), row);
        foreach (var row in new[] { "LOCKED", "N/A" }) Assert.False(Has(items, row, 2), row + " appears only in a knockout series");
        Assert.Equal(NetRole.Good, RoleOf(items, "START RACE"));
    }

    [Theory]
    [InlineData(0, "SIMULATION")]
    [InlineData(1, "WILD")]
    [InlineData(2, "ARCADE")]
    public void Lobby_ThePhysicsStyleIsShownWithItsFullLabel(int style, string label)
    {
        var items = Items(OptionsLobby(out _, change: l => l with { Style = style }));
        Assert.True(Has(items, "PHYSICS: " + label));
        foreach (var other in new[] { "SIMULATION", "WILD", "ARCADE" }.Where(x => x != label))
            Assert.False(Has(items, "PHYSICS: " + other), other);
    }

    [Fact]
    public void Lobby_KnockoutBeforeTheFirstRaceShowsTheTrackReadOnlyAndBotsAndTrafficAsNotApplicable()
    {
        var items = Items(OptionsLobby(out _, change: l => l with { Mode = NetGameMode.Knockout }));
        Assert.True(Has(items, "TRACK: TRACK 3 LOCKED"));
        Assert.True(Has(items, "BOTS: N/A"));
        Assert.True(Has(items, "TRAFFIC: N/A"));
        Assert.Equal(NetRole.Disabled, RoleOf(items, "TRACK: TRACK 3 LOCKED"));
        Assert.Equal(NetRole.Disabled, RoleOf(items, "BOTS: N/A"));
        // the other options stay editable: no lock mark, the value is shown as usual
        foreach (var row in new[] { "DIRECTION: FORWARD", "LAPS: 3", "BOTS SKILL: EXPERT", "CATCH-UP: ON", "PHYSICS: WILD" }) Assert.True(Has(items, row), row);
        Assert.False(Has(items, "DIRECTION: FORWARD LOCKED"));
        Assert.False(Has(items, "PHYSICS: WILD LOCKED"));
    }

    [Fact]
    public void Lobby_KnockoutAfterTheSeriesStartedShowsEveryOptionCarAndColourLocked()
    {
        var menu = OptionsLobby(out _, change: l => l with { Mode = NetGameMode.Knockout, Series = Series(1, me0Out: false) });
        var items = Items(menu);
        foreach (var row in new[] { "TRACK: TRACK 3 LOCKED", "DIRECTION: FORWARD LOCKED", "LAPS: 3 LOCKED", "BOTS SKILL: EXPERT LOCKED", "CATCH-UP: ON LOCKED",
                     "PHYSICS: WILD LOCKED", "BOTS: N/A", "TRAFFIC: N/A" })
            Assert.True(Has(items, row), row);
        Assert.Equal(NetRole.Disabled, RoleOf(items, "  CAR"));            // the car row is dimmed, the car screen cannot be opened
        Assert.True(Has(items, "KNOCKOUT   ROUND 2/8   OPTIONS LOCKED", 1));
        // the same game before the series: the rows are open again
        var open = Items(OptionsLobby(out _, change: l => l with { Mode = NetGameMode.Knockout }));
        Assert.NotEqual(NetRole.Disabled, RoleOf(open, "  CAR"));
    }

    [Fact]
    public void Lobby_ASingleRaceHasNothingLockedAfterARace()
    {
        var items = Items(OptionsLobby(out _, change: l => l with { Series = null }));
        Assert.False(Has(items, "LOCKED"));
    }

    [Fact]
    public void Lobby_PlayersSeeTheFullNamesOfTheCarsInTheRosterAndTheirOwnRow()
    {
        var menu = OptionsLobby(out _, op: false, change: l => l with
        {
            Members = [l.Members[0] with { CarType = 0 }, l.Members[1] with { CarType = 7 }, l.Members[2] with { CarType = 11 }],
        });
        var items = Items(menu);
        Assert.True(Has(items, "MCLAREN F1", 1));
        Assert.True(Has(items, "ISDERA 112I", 1));
        Assert.True(Has(items, "FORD GT90", 1));
        Assert.False(Has(items, "MCF1", 1));
        Assert.False(Has(items, "ISDR", 1));
    }

    /// <summary>Opens the car screen of a lobby menu (UP to the car row, ENTER).</summary>
    static NetworkMenuController CarScreenOf(NetworkMenuController menu)
    {
        for (int i = 0; i < 8 && menu.LobbyRows[menu.Selected] != LobbyRow.Car; i++) menu.Key(MenuKey.Up);
        menu.Key(MenuKey.Enter);
        Assert.True(menu.InCar);
        return menu;
    }

    [Fact]
    public void ClassicCarScreen_ShowsTheNickTheCarTheColourRowDoneAndCancelAndNoLobbyPanels()
    {
        var menu = CarScreenOf(OptionsLobby(out _, change: l => l with { Members = [l.Members[0] with { CarType = 3 }, l.Members[1], l.Members[2]] }));
        var items = Items(menu);
        Assert.True(Has(items, "ME"), "the title is the own nick");                // Me is member 0 of the operator's lobby
        Assert.Equal(NetRole.Value, RoleOf(items, "ME"));
        Assert.Equal(NetRole.Selected, RoleOf(items, "> CAR: ", 2));                 // the cursor starts on the car row
        Assert.True(Has(items, "  COLOUR: DEFAULT"));
        Assert.True(Has(items, "  DONE"));
        Assert.True(Has(items, "  CANCEL"));
        Assert.True(Has(items, NetworkMenuLayout.CarHint, 1));
        Assert.Equal(1, items.Count(i => i.Op == NetOp.Panel && i.Kind == NetPanelKind.Data));   // one panel, the lobby's are not drawn
        Assert.DoesNotContain(items, i => i.Op == NetOp.Preview);
        Assert.False(Has(items, "  SETTINGS"));
        menu.Key(MenuKey.Down);
        Assert.Equal(NetRole.Selected, RoleOf(Items(menu), "> COLOUR: DEFAULT"));
    }

    [Fact]
    public void Lobby_ColourRowShowsDefaultThePositionInThePaletteOrFixedForASingleColour()
    {
        // model 3 has six colours from 0x42 (the colour row is on the car screen)
        Assert.True(Has(Items(CarScreenOf(OptionsLobby(out _, change: l => l with { Members = [l.Members[0] with { CarType = 3 }, l.Members[1], l.Members[2]] }))), "COLOUR: DEFAULT"));
        var chosen = CarScreenOf(OptionsLobby(out _, change: l => l with { Members = [l.Members[0] with { CarType = 3, Colour = 0x44 }, l.Members[1], l.Members[2]] }));
        var items = Items(chosen);
        Assert.True(Has(items, "COLOUR: 3/6"));
        Assert.False(Has(items, "PALETTE:"));                         // no strip: the colour row shows a swatch
        // the colour row of the car screen carries a swatch of the paint, 20 x 10 (the roster is not on this screen)
        var swatches = items.Where(i => i.Op == NetOp.Swatch && i.Rgb == MenuScene.SwatchColour(0x44)).ToArray();
        Assert.Single(swatches);
        Assert.All(swatches, s => Assert.Equal((20, 10), (s.X1, s.Y1)));
        Assert.True(swatches.Any(s => s.X > At(items, "COLOUR: 3/6").X + 100), "the swatch follows the colour text");
        Assert.True(SwatchPixels(Render(chosen), 0x44) >= 20 * 10);      // classic pixels
        // the roster row of the player carries a swatch of the paint too
        var roster = Items(OptionsLobby(out _, change: l => l with { Members = [l.Members[0] with { CarType = 3, Colour = 0x44 }, l.Members[1], l.Members[2]] }));
        Assert.Single(roster, i => i.Op == NetOp.Swatch && i.Rgb == MenuScene.SwatchColour(0x44));
        // model 4 has a single colour: shown as fixed, no strip of entries
        var singleMenu = CarScreenOf(OptionsLobby(out _, change: l => l with { Members = [l.Members[0] with { CarType = 4 }, l.Members[1], l.Members[2]] }));
        var single = Items(singleMenu);
        Assert.True(Has(single, "COLOUR: FIXED"));
        Assert.DoesNotContain(single, i => i.Op == NetOp.Swatch && i.Rgb == MenuScene.SwatchColour(0x42));
        Assert.Equal(0, SwatchPixels(Render(singleMenu), 0x42));
    }

    static int SwatchPixels(FrameBuffer fb, int colour)
    {
        uint rgb = MenuScene.SwatchColour(colour);
        int n = 0;
        for (int y = 0; y < fb.Height; y++)
            for (int x = 0; x < fb.Width; x++)
                if (fb.Get(x, y) == rgb) n++;
        return n;
    }

    [Fact]
    public void Lobby_PlayerThatIsNotTheOperatorHasNoOptionRowsButSeesTheValues()
    {
        var items = Items(OptionsLobby(out _, op: false, change: l => l with { Skill = 0, CatchUp = false, Style = 2 }));
        foreach (var row in new[] { "BOTS SKILL: ", "CATCH-UP: ", "PHYSICS: " }) Assert.False(Has(items, row), row);
        Assert.True(Has(items, "BOTS SKILL BEGINNER   CATCH-UP OFF   PHYSICS ARCADE", 1));
        Assert.False(Has(items, "COLOUR: "));                      // the colour is on the car screen
        Assert.True(Has(items, "READY: NO"));
        Assert.True(Has(items, "LEAVE GAME"));
    }

    [Fact]
    public void Lobby_ActionsAreAVerticalColumnRightOfThePlayersInTheOrderOfTheCursor()
    {
        var items = Items(OptionsLobby(out _));
        var settings = At(items, "  SETTINGS");
        var ready = At(items, "READY:");
        var start = At(items, "START RACE");
        var leave = At(items, "LEAVE GAME");
        Assert.Equal(NetworkMenuLayout.ActionX, settings.X);
        Assert.Equal([NetworkMenuLayout.ActionX, NetworkMenuLayout.ActionX, NetworkMenuLayout.ActionX], new[] { ready.X, start.X, leave.X });
        Assert.True(settings.Y < ready.Y && ready.Y < start.Y && start.Y < leave.Y);          // UP/DOWN goes through them in this order
        // right of the players table (its last column, LINK, ends before them)
        var link = At(items, "LINK", 1);
        Assert.True(link.X + HudOverlay.TextWidth("LOST", 2) < ready.X);
    }

    [GameDataFact]
    public void CarNames_ComeFromTheTextFileOfTheGameAndAreTheFullNames()
    {
        string dir = NFS.Data.GameFiles.FindGameDir(null);
        var names = NetworkCarNames.Load(dir);
        Assert.NotNull(names);
        Assert.Equal(NetProtocol.MaxCarType + 1, names!.Count);
        Assert.Equal("MCLAREN F1", names[0]);
        Assert.All(names, n => Assert.True(n.Length > 4, n));      // never the 4-letter codes of the port
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain(NFS.Core.Constants.RaceConstants.CarNames.Take(12).Select(c => c.ToUpperInvariant()), names.Contains);
    }

    [Fact]
    public void CarNames_FallBackToTheShortNamesWithoutAList()
    {
        Assert.Equal("MCF1", NetworkCarNames.Name(null, 0));
        Assert.Equal("MCF1", NetworkCarNames.Name([], 0));
        Assert.Equal("FULL", NetworkCarNames.Name(["FULL"], 0));
        Assert.Equal("CAR 99", NetworkCarNames.Name(null, 99));
    }

    // ---------------- track names ----------------

    [Fact]
    public void Lobby_ShowsTrackNamesWhenKnownAndNumberedFallbackOtherwise()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby);
        menu.TrackNames = ["ALPHA RUN", "BETA BAY"];
        view.Edit(l => l with { Track = 0 }); menu.Update(0);
        var items = Items(menu);
        Assert.True(Has(items, "TRACK: ALPHA RUN"));
        Assert.False(Has(items, "TRACK: TRACK 1"));
        view.Edit(l => l with { Track = 1 }); menu.Update(0);
        Assert.True(Has(Items(menu), "TRACK: BETA BAY"));
        view.Edit(l => l with { Track = 2 }); menu.Update(0);
        Assert.True(Has(Items(menu), "TRACK: TRACK 3"), "a track beyond the list keeps the numbered text");
    }

    [Fact]
    public void GamesList_ShowsTrackNameInsteadOfNumber()
    {
        var menu = Browsing(out _, Game(1, "Alpha", NetGamePhase.Lobby));
        Assert.True(Has(Items(menu), "TRACK 2", 1));
        menu.TrackNames = ["ONE", "SECOND TRACK"];
        Assert.True(Has(Items(menu), "SECOND TRACK", 1));
    }

    [Fact]
    public void TrackNames_FallbackWithoutList()
    {
        Assert.Equal("TRACK 1", NetworkTrackNames.Name(null, 0));
        Assert.Equal("TRACK 4", NetworkTrackNames.Name([], 3));
        Assert.Equal("A", NetworkTrackNames.Name(["A"], 0));
        Assert.Equal("TRACK 1", NetworkTrackNames.Name([""], 0));
    }

    [GameDataFact]
    public void TrackNames_ComeFromTheSameTextsAsTheLocalRaceMenu()
    {
        string dir = NFS.Data.GameFiles.FindGameDir(null);
        var names = NetworkTrackNames.Load(dir);
        Assert.NotNull(names);
        Assert.True(names!.Count >= 7);
        Assert.All(names.Take(7), n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.DoesNotContain("TRACK 1", names);
        var tree = MenuTables.Load(NFS.Data.FrontEnd.TextFile.Load("ENG", dir));
        var local = tree.Screens.SelectMany(s => s.Items).First(i => i.ConfigOffset == 0x10).ValueTexts!;
        Assert.Equal(local[0].ToUpperInvariant(), names[0]);
    }

    // ---------------- create options ----------------

    [Fact]
    public void CreateOptions_ShowOnlyNameLimitAndModeAndFollowChanges()
    {
        var menu = CreateScreen(out _);
        var items = Items(menu);
        foreach (var t in new[] { "GAME NAME: ", "MAX PLAYERS: 8", "MODE: SINGLE RACE", "CREATE", "BACK" }) Assert.True(Has(items, t), t);
        foreach (var t in new[] { "TRACK:", "DIRECTION:", "LAPS:", "BOTS:", "TRAFFIC:", "BOTS SKILL:", "CATCH-UP:", "PHYSICS:" })
            Assert.False(Has(items, "  " + t), t + " is set in the lobby, not on the create screen");
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Left);
        Assert.True(Has(Items(menu), "MAX PLAYERS: 7"));
        Assert.False(SameItems(items, Items(menu)));
        menu.Key(MenuKey.Down); menu.Key(MenuKey.Right);
        Assert.True(Has(Items(menu), "MODE: KNOCKOUT"));
    }

    [Fact]
    public void CreateOptions_NameCursorAppearsWhileEditing()
    {
        var menu = CreateScreen(out _);
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetField.CreateName, menu.Editing);
        for (int i = 0; i < 30; i++) menu.Type((char)8);
        foreach (char c in "Fast") menu.Type(c);
        Assert.True(Has(Items(menu, tick: 0), "GAME NAME: FAST_"));
        Assert.True(Has(Items(menu, tick: 30), "GAME NAME: FAST "));
        Assert.True(Has(Items(menu), NetworkMenuLayout.EditHint, 1));      // editing replaces the hint
    }

    // ---------------- lobby ----------------

    [Fact]
    public void Lobby_StartIsVisibleOnlyForTheOperator()
    {
        var op = Items(InGame(out _, NetGamePhase.Lobby, true, others: "Bob"));
        var player = Items(InGame(out _, NetGamePhase.Lobby, false, others: "Bob"));
        Assert.True(Has(op, "START RACE"));
        Assert.False(Has(player, "START RACE"));
        Assert.False(SameItems(op, player));
        // everyone sees leave, ready and car rows; only the operator has track/direction/laps rows
        foreach (var f in new[] { op, player })
        {
            Assert.True(Has(f, "LEAVE GAME"));
            Assert.True(Has(f, "READY: "));
            Assert.True(Has(f, "  CAR"));
        }
        foreach (var row in new[] { "DIRECTION: FORWARD", "TRACK: TRACK 3", "LAPS: 3", "BOTS: AUTO", "BOTS SKILL: EXPERT", "CATCH-UP: ON", "TRAFFIC: OFF", "PHYSICS: WILD" })
        {
            Assert.True(Has(op, row), row);
            Assert.False(Has(player, row), row + " is only a row of the operator");
        }
        // the options are shown to the player too (scale 1 summary), without being rows
        Assert.True(Has(player, "TRACK 3   FORWARD   3 LAPS", 1));
        Assert.True(Has(player, "BOTS AUTO   TRAFFIC OFF", 1));
        Assert.True(Has(player, "BOTS SKILL EXPERT   CATCH-UP ON   PHYSICS WILD", 1));
        Assert.True(Has(player, "3/8 PLAYERS", 1));
    }

    [Fact]
    public void Lobby_ListsPlayersWithReadinessCarsAndConnection()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, false, 0, "Cup", "Me", "Bob", "Carol", "Dave");
        var items = Items(menu);
        Assert.True(Has(items, "CUP"));
        Assert.True(Has(items, "OPERATOR: OPERATOR", 1));
        foreach (var n in new[] { "OPERATOR", "ME", "BOB", "CAROL", "DAVE" }) Assert.True(Has(items, n), n);
        Assert.Contains(Items(menu), i => i.Text == "READY" && i.Role == NetRole.Good);
        Assert.Contains(Items(menu), i => i.Text == "WAITING" && i.Role == NetRole.Disabled);
        Assert.Contains(Items(menu), i => i.Text == "LOST" && i.Role == NetRole.Bad);       // Carol (index 1 of the others) is disconnected
        Assert.Contains(Items(menu), i => i.Text == "OK" && i.Role == NetRole.Good);
        Assert.True(Has(items, "F355", 1));        // car type 2 of Me
        // the disconnected player's whole row is dimmed; the local player's is lit
        Assert.Equal(NetRole.Disabled, RoleOf(items, "CAROL"));
        Assert.Equal(NetRole.Selected, RoleOf(items, "ME"));
        Assert.Equal(NetRole.Label, RoleOf(items, "BOB"));
    }

    [Fact]
    public void Lobby_ReadinessAndConnectionChangeTheFrame()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, true, 0, "Race", "Me", "Bob");
        var before = Items(menu);
        var l = view.Lobby!;
        view.Lobby = l with { Members = [.. l.Members.Select(m => m.Name == "Bob" ? m with { Ready = !m.Ready } : m)] };
        Assert.False(SameItems(before, Items(menu)));
        var ready = Items(menu);
        view.Lobby = view.Lobby with { Members = [.. view.Lobby.Members.Select(m => m.Name == "Bob" ? m with { Connected = false } : m)] };
        Assert.False(SameItems(ready, Items(menu)));
        Assert.True(Has(Items(menu), "LOST"));
    }

    [Fact]
    public void Lobby_EmptyMemberListDoesNotThrow()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, true);
        view.Lobby = view.Lobby! with { Members = [] };
        Assert.True(Painted(Render(menu)) > 800);
        Assert.True(Has(Items(menu), "NO PLAYERS"));
    }

    [Fact]
    public void Lobby_PanelsHoldTheirTexts_AndTheRowsFollowTheRosterInOrder()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal"]);
        var items = Items(menu);
        var panel = items.Single(i => i.Op == NetOp.Panel);
        Assert.Equal((40, 20, 600, 468), (panel.X, panel.Y, panel.X1, panel.Y1));
        Assert.Equal(NetPanelKind.Data, panel.Kind);
        foreach (var t in items.Where(i => i.Op == NetOp.Text))
            Assert.True(Inside(Rect(t), panel.X, panel.Y, panel.X1, panel.Y1), $"'{t.Text}' leaves the panel");
        var names = new[] { "ME", "BOB", "CAROL", "DAVE", "EVE", "FRANK", "GINA", "HAL" };
        for (int i = 1; i < names.Length; i++) Assert.True(At(items, names[i - 1]).Y < At(items, names[i]).Y, names[i]);
    }

    // ---------------- countdown / race ----------------

    [Fact]
    public void Countdown_ShowsRemainingSecondsRoundedUp()
    {
        var menu = InGame(out var view, NetGamePhase.Countdown, true, 5000);
        Assert.True(Has(Items(menu), "5", 12));
        view.InGame(NetGamePhase.Countdown, true, "Race", 3001);
        var four = Items(menu);
        Assert.True(Has(four, "4", 12));
        Assert.False(Has(four, "5", 12));
        view.InGame(NetGamePhase.Countdown, true, "Race", 400);
        Assert.True(Has(Items(menu), "1", 12));
        Assert.Equal(NetRole.ValueSelected, RoleOf(Items(menu), "1", 12));
        Assert.Equal(NetAlign.Center, At(Items(menu), "1", 12).Align);
        Assert.Equal(NetFont.Digit, At(Items(menu), "1", 12).Size);
        Assert.True(Has(Items(menu), "RACE STARTS IN"));
    }

    [Fact]
    public void RaceScreen_DrawsOnlyAStatusBox()
    {
        var menu = InGame(out _, NetGamePhase.Race, true, 0, "Cup", "Me", "Bob");
        var items = Items(menu);
        var dialog = items.Single(i => i.Op == NetOp.Dialog);
        Assert.Equal("RACE IN PROGRESS", dialog.Text);
        Assert.Equal("Cup", dialog.Text2);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Panel);
        Assert.True(items.Length < Items(InGame(out _, NetGamePhase.Lobby, true, 0, "Cup", "Me", "Bob")).Length);
        Assert.True(Has(items, "ESC PAUSE OR LEAVE", 1));
        var fb = Render(menu);                                                    // classic pixels: the dialog box and its head
        Assert.True(Painted(fb) > 800);
        Assert.NotNull(FindText(fb, "RACE IN PROGRESS", 2));
        Assert.NotNull(FindText(fb, "CUP", 2));
        Assert.True(Painted(fb) < Painted(Render(InGame(out _, NetGamePhase.Lobby, true, 0, "Cup", "Me", "Bob"))));
    }

    // ---------------- summary ----------------

    [Fact]
    public void Summary_ShowsResultsAndActions()
    {
        var summary = SummaryScreen(out _, SomeResults());
        summary.CarNames = FullNames;
        var withResults = Items(summary);
        foreach (var t in new[] { "RACE RESULTS", "ME", "BOB", "BEEFY", "1:35.00", "DNF", "RETURN TO LOBBY", "EXIT" })
            Assert.True(Has(withResults, t), t);
        // the full names of the models, not the short codes (MCF1, JAGR; F355 is part of the full name)
        foreach (var t in new[] { "MCLAREN F1", "FERRARI F355", "JAGUAR XJ220" }) Assert.True(Has(withResults, t, 1), t);
        foreach (var t in new[] { "MCF1", "JAGR" }) Assert.False(Has(withResults, t, 1), t);
        Assert.Equal(NetRole.Selected, RoleOf(withResults, "ME"));        // the row of the local player is lit
        Assert.Equal(NetRole.Disabled, RoleOf(withResults, "BEEFY"));     // a driver that did not finish is dimmed
        Assert.Equal(NetRole.Label, RoleOf(withResults, "BOB"));
        var none = Items(SummaryScreen(out _, null));
        Assert.True(Has(none, "WAITING FOR RESULTS...", 2));
        Assert.False(SameItems(withResults, none));
        Assert.False(Has(none, "BEEFY"));
    }

    // ---------------- confirmation / reconnect ----------------

    [Fact]
    public void ConfirmExit_OverlaysTheOriginScreenAndShowsTheChoice()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        var plain = Items(menu);
        menu.RequestExit();
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        var no = Items(menu);
        Assert.True(Has(no, "LEAVE THE GAME?"));
        Assert.True(Has(no, "BOB"));                     // origin still visible around the box
        Assert.False(SameItems(plain, no));
        Assert.True(plain.Length < no.Length);
        Assert.Equal(NetRole.Selected, RoleOf(no, "> NO"));
        menu.Key(MenuKey.Left);
        Assert.True(menu.ConfirmYes);
        var yes = Items(menu);
        Assert.False(SameItems(no, yes));
        Assert.Equal(NetRole.Selected, RoleOf(yes, "> YES"));
        Assert.Equal(NetRole.Label, RoleOf(yes, "  NO"));
        // classic pixels
        Assert.Equal(0xFFFF00u, FindText(Render(menu), "> YES", 2));
        Assert.NotEqual(0xFFFF00u, FindText(Render(menu), "NO", 2));
    }

    [Fact]
    public void ConfirmExit_OverTheRaceDrawsNoLobbyUnderneath()
    {
        var menu = InGame(out _, NetGamePhase.Race, true, others: "Bob");
        menu.RequestExit();
        Assert.Equal(NetScreen.ConfirmExit, menu.Screen);
        var items = Items(menu);
        Assert.True(Has(items, "LEAVE THE GAME?"));
        Assert.False(Has(items, "LEAVE GAME"));
        // over the picture only the box itself is drawn
        var overlay = RaceItems(menu);
        Assert.True(Has(overlay, "LEAVE THE GAME?"));
        Assert.Equal(1, overlay.Count(i => i.Op == NetOp.Panel));
    }

    /// <summary>A lobby of the operator in which Eve is connected and not ready, the cursor on RACE.</summary>
    static NetworkMenuController NotReadyLobby()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol", "Dave", "Eve"]);
        Assert.True(menu.NotAllReady);
        menu.Key(MenuKey.Right);
        Assert.Equal(LobbyRow.Start, menu.LobbyRows[menu.Selected]);
        return menu;
    }

    [Fact]
    public void ConfirmStart_ClassicShowsTheWarningWithCancelLitFirst()
    {
        var menu = NotReadyLobby();
        menu.Key(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmStart, menu.Screen);
        var items = Items(menu);
        Assert.True(Has(items, "NOT ALL PLAYERS ARE READY"));
        Assert.True(Has(items, "EVE"));                                  // the lobby stays visible around the box
        Assert.Equal(NetRole.Selected, RoleOf(items, "> CANCEL"));
        Assert.Equal(NetRole.Label, RoleOf(items, "  START ANYWAY"));
        menu.Key(MenuKey.Left);
        var anyway = Items(menu);
        Assert.Equal(NetRole.Selected, RoleOf(anyway, "> START ANYWAY"));
        Assert.Equal(NetRole.Label, RoleOf(anyway, "  CANCEL"));
        Assert.False(SameItems(items, anyway));
    }

    [GameDataFact]
    public void ConfirmStart_OriginalIsAGameDialogWithTheLitOption()
    {
        var fonts = new FrontEndFont();
        var menu = NotReadyLobby();
        menu.Key(MenuKey.Enter);
        var items = OrigItems(menu, fonts);
        Assert.Contains(items, i => i.Op == NetOp.Dialog && i.Text == "not all players are ready");
        Assert.Equal(NetRole.Selected, TextAt(items, "cancel").Role);
        Assert.Equal(NetRole.Label, TextAt(items, "start anyway").Role);
        Assert.Equal((NetworkMenuLayout.DialogBodyY, NetworkMenuLayout.DialogBodyY), (TextAt(items, "cancel").Y, TextAt(items, "start anyway").Y));
        Assert.True(TextAt(items, "start anyway").X < TextAt(items, "cancel").X);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "eve");   // only the window over the backdrop
        menu.Key(MenuKey.Right);
        var anyway = OrigItems(menu, fonts);
        Assert.Equal(NetRole.Selected, TextAt(anyway, "start anyway").Role);
        Assert.Equal(NetRole.Label, TextAt(anyway, "cancel").Role);
    }

    [Fact]
    public void Countdown_DigitAndBarsFollowTheClockOfTheController()
    {
        var menu = InGame(out _, NetGamePhase.Countdown, true, 5000, others: "Bob");
        Assert.True(Has(Items(menu), "5", 12));
        menu.Update(2_000_000);                                          // two seconds on, no new state from the server
        var items = Items(menu);
        Assert.True(Has(items, "3", 12));
        Assert.False(Has(items, "5", 12));
        menu.Update(4_500_000);
        Assert.True(Has(Items(menu), "1", 12));
        menu.Update(9_000_000);
        Assert.True(Has(Items(menu), "0", 12));                              // clamped, never negative
    }

    [GameDataFact]
    public void OriginalCountdown_DigitAndBarsFollowTheClockOfTheController()
    {
        var fonts = new FrontEndFont();
        var menu = WithNames(InGame(out _, NetGamePhase.Countdown, true, 5000, "Race", "Me", "Bob"));
        Assert.Equal(5, OrigItems(menu, fonts).Count(i => i.Op == NetOp.Swatch && i.Rgb == OriginalNetBackend.Colour(NetRole.Value)));
        menu.Update(2_500_000);
        var items = OrigItems(menu, fonts);
        Assert.Equal(NetFont.Digit, TextAt(items, "3").Size);            // 2.5 s left
        Assert.Equal(3, items.Count(i => i.Op == NetOp.Swatch && i.Rgb == OriginalNetBackend.Colour(NetRole.Value)));
    }

    static int Lum(uint rgb) => (int)((rgb >> 16 & 0xFF) * 3 + (rgb >> 8 & 0xFF) * 6 + (rgb & 0xFF));

    /// <summary>The frame of a popup rectangle: the outermost ring is darker than the fill (every interior chequer colour) and than
    /// the background around it, the ring inside it is lighter than the fill, on all four sides.</summary>
    static void AssertFramed(FrameBuffer fb, int x0, int y0, int x1, int y1, string what)
    {
        int fillMin = int.MaxValue;
        for (int y = y0 + 4; y < y0 + 8; y++)   // clean fill rows under the border, away from text
            for (int x = x0 + 4; x < x1 - 4; x++) fillMin = Math.Min(fillMin, Lum(fb.Get(x, y)));
        int fillMax = 0;
        for (int y = y0 + 4; y < y0 + 8; y++)   // clean fill rows under the border, away from text
            for (int x = x0 + 4; x < x1 - 4; x++) fillMax = Math.Max(fillMax, Lum(fb.Get(x, y)));
        var edge = new List<(int X, int Y)>();
        for (int x = x0 + 8; x < x1 - 8; x += 7)
            { edge.Add((x, y0)); edge.Add((x, y1 - 1)); }
        for (int y = y0 + 8; y < y1 - 8; y += 7) { edge.Add((x0, y)); edge.Add((x1 - 1, y)); }
        foreach (var (x, y) in edge)
        {
            int e = Lum(fb.Get(x, y));
            Assert.True(e < fillMin, $"{what}: edge pixel ({x},{y}) {fb.Get(x, y):X6} is not darker than the fill ({fillMin})");
        }
        // outside neighbours (background) are brighter than the border
        Assert.True(Lum(fb.Get(x0 - 1, y0 + 20)) > Lum(fb.Get(x0, y0 + 20)), $"{what}: left border not darker than the background");
        Assert.True(Lum(fb.Get(x1, y0 + 20)) > Lum(fb.Get(x1 - 1, y0 + 20)), $"{what}: right border not darker than the background");
        Assert.True(Lum(fb.Get(x0 + 20, y0 - 1)) > Lum(fb.Get(x0 + 20, y0)), $"{what}: top border not darker than the background");
        Assert.True(Lum(fb.Get(x0 + 20, y1)) > Lum(fb.Get(x0 + 20, y1 - 1)), $"{what}: bottom border not darker than the background");
        // the inner ring is a lighter bevel
        Assert.True(Lum(fb.Get(x0 + 20, y0 + 2)) > fillMax, $"{what}: no lighter inner edge");
    }

    [Fact]
    public void ConfirmExit_HasADarkerBorderThanFillAndBackground()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        menu.RequestExit();
        var fb = Render(menu, background: true);
        AssertFramed(fb, 130, 175, 510, 305, "LEAVE THE GAME?");
        var panel = Items(menu).Last(i => i.Op == NetOp.Panel);                  // the list says where the frame is
        Assert.Equal((130, 175, 510, 305), (panel.X, panel.Y, panel.X1, panel.Y1));
    }

    [Fact]
    public void RaceBoxAndMainPanel_HaveADarkerBorder()
    {
        var race = Render(InGame(out _, NetGamePhase.Race, true, others: "Bob"), background: true);
        AssertFramed(race, 130, 190, 510, 290, "RACE IN PROGRESS");
        var lobbyMenu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        var lobby = Render(lobbyMenu, background: true);
        AssertFramed(lobby, 40, 20, 600, 468, "lobby panel");
        var panel = Items(lobbyMenu).Single(i => i.Op == NetOp.Panel);
        Assert.Equal((40, 20, 600, 468), (panel.X, panel.Y, panel.X1, panel.Y1));
    }

    /// <summary>The footer lines of every screen in the D7 format: key and action separated by one space, items by three, no
    /// <c>=</c> or comma, only the actions that work (the chat key where a chat can be opened).</summary>
    [Fact]
    public void Footers_FollowTheKeyActionFormatOnEveryScreenAndOfBothLooks()
    {
        var typing = new NetworkMenuController("Driver");
        typing.Key(MenuKey.Enter);
        var settings = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        settings.Key(MenuKey.Up);
        settings.Key(MenuKey.Enter);
        Assert.True(settings.InSettings);
        var cases = new (NetworkMenuController Menu, string Hint)[]
        {
            (new NetworkMenuController("Driver"), "UP/DOWN SELECT   ENTER OK   ESC BACK"),
            (Browsing(out _), "UP/DOWN SELECT   ENTER OK   ESC BACK"),
            (CreateScreen(out _), "UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK"),
            (typing, "TYPE TEXT   ENTER DONE   ESC CANCEL"),
            (InGame(out _, NetGamePhase.Lobby, true, others: "Bob"), "T CHAT   UP/DOWN SELECT   ENTER OK   ESC LEAVE"),
            (settings, "T CHAT   UP/DOWN SELECT   LEFT/RIGHT CHANGE   ESC BACK"),
            (InGame(out _, NetGamePhase.Countdown, true, 3000), "T CHAT   ESC CANCEL"),
            (InGame(out _, NetGamePhase.Countdown, false, 3000, others: "Bob"), "T CHAT   ESC LEAVE"),
            (SummaryScreen(out _, SomeResults()), "T CHAT   UP/DOWN SELECT   ENTER OK   ESC EXIT"),
        };
        var fonts = OriginalFonts();
        foreach (var (menu, hint) in cases)
        {
            Assert.DoesNotContain('=', hint);
            Assert.DoesNotContain(',', hint);
            Assert.True(Has(Items(menu), hint, 1), $"classic {menu.Screen}: {hint}");
            var at = At(Items(menu), hint, 1);
            Assert.Equal((NetworkMenuLayout.FooterY, 60, NetAlign.Left), (at.Y, at.X, at.Align));
            if (fonts == null) continue;
            var orig = Items(menu, fonts: fonts).Single(i => i.Op == NetOp.Text && i.Text == hint);
            Assert.Equal((NetworkMenuLayout.OriginalFooterY, 320, NetAlign.Center, NetFont.Small, NetRole.Label), (orig.Y, orig.X, orig.Align, orig.Size, orig.Role));
        }
        // the chat line being typed and the other hints of the table
        Assert.Equal("ENTER SEND   ESC CANCEL", NetworkMenuLayout.ChatHint);
        Assert.Equal("CONNECTION LOST - RECONNECTING...   ESC GIVE UP", NetworkMenuLayout.ReconnectHint);
        Assert.True(Has(Items(InGame(out _, NetGamePhase.Race, true)), "ESC PAUSE OR LEAVE", 1));
    }

    [Fact]
    public void Connecting_ShowsOnlyCancelInBothLooks()
    {
        var fonts = OriginalFonts();
        var classic = Items(Connecting(out _));
        Assert.True(Has(classic, "ESC CANCEL", 1));
        Assert.DoesNotContain(classic, i => i.Text != null && i.Text.Contains('=') );
        if (fonts == null) return;
        var orig = Items(Connecting(out _), fonts: fonts);
        Assert.Contains(orig, i => i.Op == NetOp.Dialog && i.Text == "network race" && i.Text2 == "connecting to 127.0.0.1:27100");
        Assert.Contains(orig, i => i.Op == NetOp.Text && i.Text == "ESC CANCEL" && i.Y == NetworkMenuLayout.OriginalFooterY);
    }

    [Fact]
    public void Reconnecting_TheBannerTakesThePlaceOfTheFooterInTheMenu()
    {
        foreach (var f in new FrontEndFont?[] { null, OriginalFonts() })
        {
            var menu = InGame(out var view, NetGamePhase.Lobby, true, others: "Bob");
            view.Phase = SessionClientPhase.Reconnecting;
            menu.Update(5);
            var items = Items(menu, fonts: f);
            Assert.True(Has(items, NetworkMenuLayout.ReconnectHint, 1));
            Assert.False(Has(items, NetworkMenuLayout.LobbyHint, 1), "the footer is gone");
            var bar = items.Last(i => i.Op == NetOp.Panel);
            Assert.Equal(NetPanelKind.Banner, bar.Kind);
            Assert.True(bar.Y >= 440 && bar.Y1 <= 470, $"{bar.Y}..{bar.Y1}");   // at the foot, not over the logo
            var text = At(items, NetworkMenuLayout.ReconnectHint, 1);
            Assert.True(text.Y >= bar.Y && text.Y < bar.Y1);
        }
    }

    [Fact]
    public void Reconnecting_ShowsBannerOverTheCurrentScreen()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, true, others: "Bob");
        var normal = Items(menu);
        Assert.False(Has(normal, NetworkMenuLayout.ReconnectHint, 1));
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(5);
        Assert.True(menu.Reconnecting);
        var banner = Items(menu);
        Assert.True(Has(banner, NetworkMenuLayout.ReconnectHint, 1));
        Assert.Equal(NetRole.Warn, RoleOf(banner, NetworkMenuLayout.ReconnectHint, 1));
        Assert.True(Has(banner, "START RACE"));
        // the bar is the last panel (over the screen) and the text sits inside it, centred
        var bar = banner.Last(i => i.Op == NetOp.Panel);
        Assert.Equal(NetPanelKind.Banner, bar.Kind);
        var text = banner.Last(i => i.Op == NetOp.Text);
        Assert.Equal(NetAlign.Center, text.Align);
        Assert.True(Inside(Rect(text), bar.X, bar.Y, bar.X1, bar.Y1));
    }

    // ---------------- long and odd input ----------------

    const string Long = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>No glyph may leave the panel (x outside 40..600) – the unfilled buffer shows overflow directly.</summary>
    static void AssertWithinPanel(string name, FrameBuffer fb)
    {
        for (int y = 24; y < fb.Height; y++)     // the banner row (y &lt; 18) spans the whole width by design
            for (int x = 0; x < fb.Width; x++)
            {
                if (x >= 40 && x < 600) continue;
                Assert.True(fb.Get(x, y) == 0, $"{name}: pixel outside the panel at {x},{y}");
            }
    }

    /// <summary>The same on the list: no text below the banner row leaves x 40..600.</summary>
    static void AssertItemsWithinPanel(string name, NetDrawItem[] items)
    {
        foreach (var t in items.Where(i => i.Op == NetOp.Text && i.Y >= 24))
            Assert.True(Inside(Rect(t), 40, 0, 600, 480), $"{name}: '{t.Text}' {Rect(t)} leaves the panel");
    }

    [Fact]
    public void LongNamesAreClippedInsideThePanel()
    {
        var connect = new NetworkMenuController(Long, Long, 27100);
        AssertWithinPanel("connect", Render(connect));
        AssertItemsWithinPanel("connect", Items(connect));
        connect.Key(MenuKey.Down); connect.Key(MenuKey.Enter);
        AssertWithinPanel("connect-editing", Render(connect));
        AssertItemsWithinPanel("connect-editing", Items(connect));

        var games = Browsing(out var view, Game(1, Long[..24], NetGamePhase.Countdown, 4, 4));
        view.Games = [Game(1, Long[..24], NetGamePhase.Countdown, 4, 4), Game(2, Long[..24], NetGamePhase.Lobby, 1, 8)];
        games.Key(MenuKey.Enter);
        for (int i = 0; i < 30; i++) games.Type('W');
        AssertWithinPanel("games", Render(games));
        AssertItemsWithinPanel("games", Items(games));

        var create = CreateScreen(out _);
        create.Key(MenuKey.Enter);
        for (int i = 0; i < 30; i++) create.Type('W');
        AssertWithinPanel("create", Render(create));
        AssertItemsWithinPanel("create", Items(create));

        var lobby = InGame(out var lv, NetGamePhase.Lobby, false, 0, Long, Long[..24], Long[..24], Long[..24], Long[..24]);
        lv.Lobby = lv.Lobby! with { Members = [.. lv.Lobby.Members.Select(m => m with { Name = m.Operator ? Long[..24] : m.Name })] };
        AssertWithinPanel("lobby", Render(lobby));
        AssertItemsWithinPanel("lobby", Items(lobby));

        var summary = SummaryScreen(out var sv, new RaceResults(
        [
            new RacerResult(1, 0, "A-VERY-LONG-MODEL", true, false, 64 * 95, [], 64 * 31, 2, Long, 0),
        ]));
        AssertWithinPanel("summary", Render(summary));
        AssertItemsWithinPanel("summary", Items(summary));
        Assert.NotNull(sv);
    }

    [Fact]
    public void GameOptionsAtExtremesDoNotThrow()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, true, 0, "x", "Me", "B", "C", "D", "E", "F", "G", "H");   // more members than seats
        view.Lobby = view.Lobby! with { Track = NetProtocol.MaxTrack, Laps = NetProtocol.MaxLaps, Backward = true, Bots = 7, Traffic = true, Skill = 0, CatchUp = false, Style = 2 };
        view.Lobby = view.Lobby with { Members = [.. view.Lobby.Members.Select(m => m with { CarType = NetProtocol.MaxCarType })] };
        var items = Items(menu);
        Assert.True(items.Length > 20);
        foreach (var row in new[] { "TRACK: TRACK 8", "DIRECTION: BACKWARD", "LAPS: 8", "BOTS: 7", "BOTS SKILL: BEGINNER", "CATCH-UP: OFF", "TRAFFIC: ON", "PHYSICS: ARCADE" })
            Assert.True(Has(items, row), row);
        Assert.True(Painted(Render(menu)) > 800);
        view.Lobby = view.Lobby with { Phase = NetGamePhase.Countdown, CountdownMillis = int.MaxValue };
        menu.Update(1);
        Assert.True(Painted(Render(menu)) > 800);
        Assert.True(Has(Items(menu), "RACE STARTS IN"));
    }

    static NetKnockoutState Series(int completed, bool me0Out, bool finished = false, int winner = -1) => new(completed, finished, winner,
        Enumerable.Range(0, NetProtocol.KnockoutEntrants).Select(i => new NetKnockoutEntry(i == 0 ? "Me" : i == 1 ? "Bob" : $"Bot{i}",
            i < 2 ? i : -1, i != 8 && !(i == 0 && me0Out), 1 + i, i == 8 ? 0 : i == 0 && me0Out ? Math.Max(0, completed - 1) : -1)).ToArray());

    [Fact]
    public void AKnockoutLobbyShowsTheSeriesAndTellsAnEliminatedPlayerItIsWatching()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby);
        var single = Items(menu);
        view.Lobby = view.Lobby! with { Mode = NetGameMode.Knockout, Series = Series(2, me0Out: false) };
        menu.Update(1);
        var ko = Items(menu);
        Assert.False(menu.Spectating);
        Assert.False(SameItems(single, ko));
        Assert.True(Has(ko, "KNOCKOUT   ROUND 3/8   OPTIONS LOCKED", 1));   // the series has begun: nothing can change any more
        Assert.True(Has(ko, "OUT: BOT8", 1));
        Assert.False(Has(ko, "KNOCKOUT   ROUND 3/8   YOU ARE OUT - WATCHING", 1));
        Assert.Equal(NetRole.Disabled, RoleOf(ko, "KNOCKOUT   ROUND 3/8", 1));

        view.Lobby = view.Lobby with { Series = Series(2, me0Out: true) };
        menu.Update(2);
        Assert.True(menu.Spectating);
        Assert.True(Has(Items(menu), "KNOCKOUT   ROUND 3/8   YOU ARE OUT - WATCHING", 1));
        Assert.Equal(NetRole.Warn, RoleOf(Items(menu), "KNOCKOUT   ROUND 3/8   YOU ARE OUT - WATCHING", 1));
        Assert.True(Has(Items(menu), "OUT: BOT8 ME", 1));                 // in the order of the elimination (BOT8 from the start, ME in round 1)
        view.Lobby = view.Lobby with { Series = null };   // before the first race nothing is locked yet
        menu.Update(2);
        Assert.True(Has(Items(menu), "KNOCKOUT   ROUND 1/8   NEXT: START", 1));

        view.Lobby = view.Lobby with { Series = Series(8, me0Out: true, finished: true, winner: 1) };
        menu.Update(3);
        Assert.True(Has(Items(menu), "KNOCKOUT   SERIES OVER - WINNER BOB", 1));
    }

    [Fact]
    public void TheStragglerCountdownOfARunningRoundIsShownOverTheRaceAndCountsDown()
    {
        var menu = InGame(out var view, NetGamePhase.Race);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.Equal(0, menu.CutoffRemainingMillis);
        Assert.False(Has(RaceItems(menu), "FIRST CAR HOME - 9 S LEFT FOR THE OTHERS", 1));
        view.Lobby = view.Lobby! with { CutoffMillis = 9000 };
        menu.Update(1_000_000);
        Assert.Equal(9000, menu.CutoffRemainingMillis);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.True(Has(RaceItems(menu), "FIRST CAR HOME - 9 S LEFT FOR THE OTHERS", 1));
        Assert.Equal(NetRole.Warn, RoleOf(RaceItems(menu), "FIRST CAR HOME - 9 S LEFT FOR THE OTHERS", 1));
        menu.Update(3_500_000);   // 2.5 s later
        Assert.Equal(6500, menu.CutoffRemainingMillis);
        Assert.True(Has(RaceItems(menu), "FIRST CAR HOME - 7 S LEFT FOR THE OTHERS", 1));
        view.Lobby = view.Lobby with { CutoffMillis = 0 };
        menu.Update(3_600_000);
        Assert.Equal(0, menu.CutoffRemainingMillis);
        Assert.False(Has(RaceItems(menu), "FIRST CAR HOME - 7 S LEFT FOR THE OTHERS", 1));
        // the same through the public entry point: with no banner nothing is drawn over the picture
        var fb = new FrameBuffer(640, 480);
        NetworkMenuRenderer.DrawRaceOverlay(fb, menu);
        Assert.Equal(0, Painted(fb));
    }

    [Fact]
    public void AnEliminatedPlayerWatchingARoundIsToldSoOverTheRacePicture()
    {
        var menu = InGame(out var view, NetGamePhase.Race);
        view.Lobby = view.Lobby! with { Mode = NetGameMode.Knockout, Series = Series(1, me0Out: false) };
        menu.Update(1);
        Assert.False(menu.Spectating);
        Assert.False(Has(RaceItems(menu), "YOU ARE OUT - WATCHING THE RACE", 1));
        view.Lobby = view.Lobby with { Series = Series(1, me0Out: true) };
        menu.Update(2);
        Assert.True(menu.Spectating);
        Assert.Equal(NetScreen.Race, menu.Screen);
        Assert.True(Has(RaceItems(menu), "YOU ARE OUT - WATCHING THE RACE", 1));
        menu.WatchedName = "BOB";   // the host names the car the picture follows
        Assert.True(Has(RaceItems(menu), "YOU ARE OUT - WATCHING BOB   Q/E CAR   W LEADER", 1));
        Assert.False(Has(RaceItems(menu), "YOU ARE OUT - WATCHING THE RACE", 1));
        var fb = new FrameBuffer(640, 480);                          // classic pixels: the banner is drawn
        NetworkMenuRenderer.DrawRaceOverlay(fb, menu);
        Assert.NotNull(FindText(fb, "YOU ARE OUT - WATCHING BOB   Q/E CAR   W LEADER", 1));
    }

    // ---------------- lobby layout, key hints, chat (change network-lobby-layout) ----------------

    static NetworkChat ChatWith(int lines)
    {
        var chat = new NetworkChat();
        for (int i = 0; i < lines; i++) chat.Add(new ChatLine("Bob", $"line {i}"), 0);
        return chat;
    }

    static FrameBuffer Render(NetworkMenuController menu, NetworkChat chat)
    {
        var fb = new FrameBuffer(640, 480);
        NetworkMenuRenderer.Draw(fb, menu, 0, false, chat);
        return fb;
    }

    [Fact]
    public void Lobby_FullChatSitsBetweenTheSettingsAndTheActionsWithoutCoveringAnything()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal"]);
        Assert.Equal(8, menu.Lobby!.Members.Count);
        var items = Items(menu, ChatWith(NetworkChat.Visible));
        foreach (var row in new[] { "  SETTINGS", "  CAR", "TRACK: TRACK 3", "DIRECTION: FORWARD", "LAPS: 3", "BOTS: AUTO", "BOTS SKILL: EXPERT",
                     "CATCH-UP: ON", "TRAFFIC: OFF", "PHYSICS: WILD", "READY: ", "START RACE", "LEAVE GAME" })
            Assert.True(Has(items, row), row);
        Assert.True(Has(items, NetworkMenuLayout.LobbyHint, 1));
        int lastOption = Math.Max(At(items, "PHYSICS: WILD").Y, At(items, "LAPS: 3").Y) + 10;
        const int lastPlayer = 94 + 7 * 18 + 10;                       // 8th roster row
        Assert.True(At(items, "SETTINGS", 1).Y >= lastPlayer, "title under the players");
        for (int i = 0; i < NetworkChat.Visible; i++)
        {
            var line = At(items, $"BOB: LINE {i}", 1);
            Assert.True(line.Y > lastOption, $"chat line {i} over the settings");
            Assert.Equal(NetRole.Header, line.Role);                   // the nick colour of the chat
        }
        Assert.True(At(items, "LEAVE GAME").Y + 10 < At(items, "SETTINGS", 1).Y, "the column of the actions ends over the settings");
        Assert.False(Has(items, "T = CHAT", 1));
        // nothing of the full lobby covers anything else (the whole list, chat included)
        var texts = items.Where(i => i.Op == NetOp.Text).ToArray();
        for (int a = 0; a < texts.Length; a++)
            for (int b = a + 1; b < texts.Length; b++)
                Assert.False(Overlap(Rect(texts[a]), Rect(texts[b])), $"'{texts[a].Text}' overlaps '{texts[b].Text}'");
        // and it is drawn whole in one colour in the classic pixels too
        using var noChatFont = ChatFont.Override(null);              // the bitmap font finder below needs the fallback font
        var fb = Render(menu, ChatWith(NetworkChat.Visible));
        foreach (var row in new[] { "SETTINGS", "START RACE", "LEAVE GAME" }) Assert.NotNull(FindText(fb, row, 2));
        Assert.NotNull(FindText(fb, "BOB: LINE 4", 1));
    }

    [Fact]
    public void Lobby_ChatTellsTheServerFromThePlayersAndStaysInsideThePanel()
    {
        var chat = ChatWith(2);
        chat.Add(new ChatLine(NetProtocol.ServerChatName, "Game starts soon"), 0);
        var items = Items(InGame(out _, NetGamePhase.Lobby, true, others: "Bob"), chat);
        Assert.Equal(NetRole.Header, RoleOf(items, "BOB: LINE 0", 1));
        Assert.Equal(NetRole.Warn, RoleOf(items, $"{NetProtocol.ServerChatName.ToUpperInvariant()}: GAME STARTS SOON", 1));
        foreach (var t in items.Where(i => i.Op == NetOp.Text && (i.Text!.Contains("LINE", StringComparison.Ordinal) || i.Text.Contains("GAME STARTS"))))
            Assert.True(Inside(Rect(t), 40, 0, 600, 480), t.Text);
    }

    [Fact]
    public void Lobby_SettingsHeaderIsSelectedFromTheActionsAndTheHintFollowsTheLevel()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        var top = Items(menu);
        Assert.True(Has(top, "  SETTINGS"));
        Assert.True(Has(top, NetworkMenuLayout.LobbyHint, 1));
        menu.Key(MenuKey.Up);
        var header = Items(menu);
        Assert.Equal(NetRole.Selected, RoleOf(header, "> SETTINGS"));
        Assert.Equal(0xFFFF00u, FindText(Render(menu), "> SETTINGS", 2));      // classic pixels
        menu.Key(MenuKey.Enter);
        var settings = Items(menu);
        Assert.False(Has(settings, "> SETTINGS"));
        Assert.True(Has(settings, "> TRACK: "));
        Assert.Equal(NetRole.Header, RoleOf(settings, "SETTINGS"));            // lit while the settings are open
        Assert.True(Has(settings, NetworkMenuLayout.SettingsHint, 1));
        Assert.False(Has(settings, NetworkMenuLayout.LobbyHint, 1));
    }

    [Fact]
    public void Lobby_SettingsHeaderIsDimmedWhenNothingCanBeChanged()
    {
        var open = OptionsLobby(out _, change: l => l with { Mode = NetGameMode.Knockout });
        var frozen = OptionsLobby(out _, change: l => l with { Mode = NetGameMode.Knockout, Series = Series(1, me0Out: false) });
        Assert.Equal(NetRole.Label, RoleOf(Items(open), "SETTINGS"));
        Assert.Equal(NetRole.Disabled, RoleOf(Items(frozen), "SETTINGS"));
        Assert.Equal(0xFFFFFFu, FindText(Render(open), "SETTINGS", 2));        // classic pixels
        Assert.Equal(0x707070u, FindText(Render(frozen), "SETTINGS", 2));
    }

    [Fact]
    public void EveryNetworkScreen_UsesKeyActionHints()
    {
        Assert.True(Has(Items(new NetworkMenuController("Driver")), "UP/DOWN SELECT   ENTER OK   ESC BACK", 1));
        Assert.True(Has(Items(Browsing(out _, Game(1, "Alpha", NetGamePhase.Lobby))), "UP/DOWN SELECT   ENTER OK   ESC BACK", 1));
        Assert.True(Has(Items(CreateScreen(out _)), "UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK", 1));
        Assert.True(Has(Items(InGame(out _, NetGamePhase.Countdown, true, 3000)), "T CHAT   ESC CANCEL", 1));
        Assert.True(Has(Items(InGame(out _, NetGamePhase.Countdown, false, 3000, others: "Bob")), "T CHAT   ESC LEAVE", 1));
        Assert.True(Has(Items(SummaryScreen(out _, SomeResults())), "T CHAT   UP/DOWN SELECT   ENTER OK   ESC EXIT", 1));
        Assert.True(Has(Items(InGame(out _, NetGamePhase.Lobby, true)), "T CHAT   UP/DOWN SELECT   ENTER OK   ESC LEAVE", 1));
        Assert.True(Has(Items(Connecting(out _)), "ESC CANCEL", 1));
        var editing = new NetworkMenuController("Driver");
        editing.Key(MenuKey.Enter);
        Assert.True(Has(Items(editing), NetworkMenuLayout.EditHint, 1));
        var chat = ChatWith(0);
        chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
        Assert.True(chat.Typing);
        var typing = Items(InGame(out _, NetGamePhase.Lobby, true, others: "Bob"), chat);
        Assert.True(Has(typing, NetworkMenuLayout.ChatHint, 1));
        Assert.True(Has(typing, "> _", 1));
        Assert.Equal(NetPanelKind.ChatInput, typing.Last(i => i.Op == NetOp.Panel).Kind);
        // every hint is a footer line at the foot of the screen, in the plain colour
        foreach (var hint in new[] { NetworkMenuLayout.CountdownOperatorHint, NetworkMenuLayout.SummaryHint })
            Assert.Equal(NetworkMenuLayout.FooterY, hint == NetworkMenuLayout.CountdownOperatorHint
                ? At(Items(InGame(out _, NetGamePhase.Countdown, true, 3000)), hint, 1).Y : At(Items(SummaryScreen(out _, SomeResults())), hint, 1).Y);
        using var noChatFont = ChatFont.Override(null);              // the bitmap font finder needs the fallback font
        Assert.NotNull(FindText(Render(InGame(out _, NetGamePhase.Lobby, true, others: "Bob"), chat), NetworkMenuLayout.ChatHint, 1));
    }

    [Fact]
    public void Countdown_ChatStaysAboveTheMessageLine()
    {
        var menu = InGame(out _, NetGamePhase.Countdown, true, 3000, others: ["Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal"]);
        var items = Items(menu, ChatWith(NetworkChat.Visible));
        Assert.True(Has(items, NetworkMenuLayout.CountdownOperatorHint, 1));
        Assert.False(Has(items, "BOB: LINE 0", 1));                       // the oldest line gives way
        var last = At(items, $"BOB: LINE {NetworkChat.Visible - 1}", 1);
        Assert.True(last.Y + 5 < NetworkMenuLayout.MessageY);
        Assert.True(Has(items, "BOTS SKILL EXPERT   CATCH-UP ON   PHYSICS WILD", 1));
        // the chat lines do not run into the option lines above them
        Assert.True(At(items, "BOB: LINE 1", 1).Y > At(items, "BOTS SKILL EXPERT   CATCH-UP ON   PHYSICS WILD", 1).Y + 5);
    }

    [Fact]
    public void RaceChat_TextKeepsAStripOfTheFillFromTheFrame()
    {
        var menu = InGame(out _, NetGamePhase.Race);
        var chat = ChatWith(2);
        // the list: one plate behind the lines, every line at least 4 px inside its edges
        var items = RaceItems(menu, chat, 0);
        var plate = items.Single(i => i.Op == NetOp.Panel);
        Assert.Equal(NetPanelKind.ChatPlate, plate.Kind);
        foreach (var line in items.Where(i => i.Op == NetOp.Text))
        {
            var r = Rect(line);
            Assert.True(Inside(r, plate.X + 4, plate.Y + 4, plate.X1 - 4, plate.Y1 - 4), $"'{line.Text}' {r} too near the plate {plate.X},{plate.Y},{plate.X1},{plate.Y1}");
        }
        // the typing line sits in its own plate, text 4 px from every edge
        chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
        var typingItems = RaceItems(menu, chat, 0);
        var input = typingItems.Last(i => i.Op == NetOp.Panel);
        foreach (var line in typingItems.SkipWhile(i => i.Op != NetOp.Panel || i.Y != input.Y).Where(i => i.Op == NetOp.Text))
            Assert.True(Inside(Rect(line), input.X + 4, input.Y + 4, input.X1 - 4, input.Y1 - 4), $"'{line.Text}' too near the input plate");

        // the pixels (classic): two clear rows and columns of the fill around the text
        chat = ChatWith(2);
        using var noChatFont = ChatFont.Override(null);              // the bitmap font finder needs the fallback font
        var fb = new FrameBuffer(640, 480);
        NetworkMenuRenderer.DrawRaceOverlay(fb, menu, chat, 0);
        var first = FindTextAt(fb, "BOB: LINE 0", 1)!.Value;
        var second = FindTextAt(fb, "BOB: LINE 1", 1)!.Value;
        int width = HudOverlay.TextWidth("BOB: LINE 0", 1);
        static bool IsFill(uint c) => c is 0x000000 or 0x080808;
        // two clear rows above the first line and under the last one, two clear columns left and right of the text
        for (int x = first.X; x < first.X + width; x++)
            for (int d = 1; d <= 2; d++)
            {
                Assert.True(IsFill(fb.Get(x, first.Y - d)), $"above ({x},{first.Y - d})");
                Assert.True(IsFill(fb.Get(x, second.Y + 5 + d - 1)), $"below ({x},{second.Y + 4 + d})");
            }
        for (int y = first.Y; y < second.Y + 5; y++)
            for (int d = 1; d <= 2; d++)
            {
                Assert.True(IsFill(fb.Get(first.X - d, y)), $"left ({first.X - d},{y})");
                Assert.True(IsFill(fb.Get(first.X + width + d - 1, y)), $"right ({first.X + width + d - 1},{y})");
            }
        // the typing line too
        chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
        var typing = new FrameBuffer(640, 480);
        NetworkMenuRenderer.DrawRaceOverlay(typing, menu, chat, 0);
        var hint = FindTextAt(typing, NetworkMenuLayout.ChatHint, 1)!.Value;
        var inputAt = FindTextAt(typing, "> _", 1)!.Value;
        for (int x = hint.X; x < hint.X + 40; x++)
            for (int d = 1; d <= 2; d++)
            {
                Assert.True(IsFill(typing.Get(x, inputAt.Y - d)), $"input above ({x},{inputAt.Y - d})");
                Assert.True(IsFill(typing.Get(x, hint.Y + 4 + d)), $"input below ({x},{hint.Y + 4 + d})");
            }
    }

    // ---------------- text safe for the fonts (change network-menu-look, design D9) ----------------

    [Fact]
    public void TextsFromPlayersAreMadeSafeForTheFonts()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, 0, "Wyścig Łódź", "Masło", "Żaneta");
        var chat = new NetworkChat();
        chat.Add(new ChatLine("Żaneta", "cześć 😀 世界"), 0);
        var items = Items(menu, chat);
        string shown = string.Join(" | ", items.Where(i => i.Op == NetOp.Text).Select(i => i.Text));
        Assert.True(Has(items, "WYSCIG LODZ"), shown);              // the name of the game
        Assert.True(Has(items, "MASLO"), shown);
        Assert.True(Has(items, "ZANETA"), shown);
        Assert.True(Has(items, "ZANETA: CZESC ? ??", 1), shown);    // an emoji is one '?', every other character outside the font too
        foreach (var t in items.Where(i => i.Op == NetOp.Text)) Assert.All(t.Text!, c => Assert.InRange(c, ' ', '~'));
    }

    // ---------------- the list and the classic backend ----------------

    [Fact]
    public void DrawList_IsReusedAndGrowsPastItsStartingSize()
    {
        var list = new NetDrawList();
        for (int i = 0; i < 300; i++) list.Text(i, 0, NetFont.Small, NetAlign.Left, NetRole.Label, "x");
        Assert.Equal(300, list.Count);
        Assert.Equal(299, list.Items[299].X);
        list.Clear();
        Assert.Equal(0, list.Count);
        list.Panel(1, 2, 3, 4, NetPanelKind.Data);
        list.RowBar(5, 6, 7, 8);
        list.Swatch(9, 10, 20, 10, 0x123456);
        list.Dialog("head", "body");
        list.Backdrop();
        Assert.Equal([NetOp.Panel, NetOp.RowBar, NetOp.Swatch, NetOp.Dialog, NetOp.Backdrop], list.Items.ToArray().Select(i => i.Op));
        Assert.Equal(0x123456u, list.Items[2].Rgb);
        Assert.Equal("body", list.Items[3].Text2);
        Assert.Null(list.Items[0].Text);                             // a reused record carries nothing of the previous frame
    }

    [Fact]
    public void ClassicBackend_MapsSizesToScalesAndRolesToTheColoursOfTheFirstMenu()
    {
        Assert.Equal(1, NetFont.ClassicScale(NetFont.Small));
        Assert.Equal(2, NetFont.ClassicScale(NetFont.Medium));
        Assert.Equal(2, NetFont.ClassicScale(NetFont.Normal));
        Assert.Equal(12, NetFont.ClassicScale(NetFont.Digit));
        Assert.Equal(0xFFFFFFu, ClassicNetBackend.Colour(NetRole.Label));
        Assert.Equal(0xFFFF00u, ClassicNetBackend.Colour(NetRole.Selected));
        Assert.Equal(0x707070u, ClassicNetBackend.Colour(NetRole.Disabled));
        Assert.Equal(0xFF6666u, ClassicNetBackend.Colour(NetRole.Bad));
        Assert.Equal(0x66FF66u, ClassicNetBackend.Colour(NetRole.Good));
        Assert.Equal(0xFFAA33u, ClassicNetBackend.Colour(NetRole.Warn));
        Assert.Equal(0x66CCFFu, ClassicNetBackend.Colour(NetRole.Header));
        foreach (var role in Enum.GetValues<NetRole>()) Assert.NotEqual(0u, ClassicNetBackend.Colour(role));
    }

    [Fact]
    public void ClassicBackend_AlignsTextAndDrawsSwatchesAndDialogs()
    {
        var list = new NetDrawList();
        list.Text(100, 50, NetFont.Normal, NetAlign.Right, NetRole.Good, "AB");
        list.Text(320, 100, NetFont.Normal, NetAlign.Center, NetRole.Bad, "AB");
        list.Text(10, 150, NetFont.Normal, NetAlign.Left, NetRole.Warn, "AB");
        list.Swatch(200, 200, 20, 10, 0x336699);
        var fb = new FrameBuffer(640, 480);
        ClassicNetBackend.Draw(fb, list);
        int width = HudOverlay.TextWidth("AB", 2);
        Assert.NotNull(FindText(fb, "AB", 2));
        Assert.Equal(0x66FF66u, FindTextAt(fb, "AB", 2)!.Value.C);
        Assert.Equal(100 - width, FindTextAt(fb, "AB", 2)!.Value.X);
        Assert.Equal(0x336699u, fb.Get(200, 200));
        Assert.Equal(0x336699u, fb.Get(219, 209));
        Assert.NotEqual(0x336699u, fb.Get(220, 200));
        Assert.Equal(0x707070u, fb.Get(199, 200));                 // the grey frame around the swatch
        // the centred text starts where the centre minus half of the width says; the left one at its x
        bool centreRow = false, leftRow = false;
        for (int x = 0; x < 640; x++)
        {
            if (fb.Get(x, 100 + 2) == 0xFF6666u && !centreRow) { Assert.InRange(x, 320 - width / 2 - 1, 320 - width / 2 + 2); centreRow = true; }
            if (fb.Get(x, 150 + 2) == 0xFFAA33u && !leftRow) { Assert.InRange(x, 10, 12); leftRow = true; }
        }
        Assert.True(centreRow && leftRow);

        var dialog = new NetDrawList();
        dialog.Backdrop();
        dialog.Dialog("RACE IN PROGRESS", "Cup");
        var fb2 = new FrameBuffer(640, 480);
        ClassicNetBackend.Draw(fb2, dialog);
        AssertFramed(fb2, 130, 190, 510, 290, "dialog");
        Assert.Equal(0xFFFF00u, FindText(fb2, "RACE IN PROGRESS", 2));
        Assert.Equal(0xFFFFFFu, FindText(fb2, "CUP", 2));
    }

    // ---------------- the original layout (change network-menu-look, task 3.2 / 3.3) ----------------

    /// <summary>Rectangle of a text in the game font (widths measured with it, limited by the text's own maximum width; the height
    /// is the line the glyphs of that font fill): x0, y0, x1, y1.</summary>
    static (int X0, int Y0, int X1, int Y1) OrigRect(NetDrawItem t, FrontEndFont fonts)
    {
        var font = fonts.Font(OriginalNetBackend.FontSize(t.Size));
        int w = font.Measure(t.Text!);
        if (t.X1 > 0) w = Math.Min(w, t.X1);
        int x = t.Align == NetAlign.Center ? t.X - w / 2 : t.Align == NetAlign.Right ? t.X - w : t.X;
        return (x, t.Y, x + w, t.Y + font.TextHeight / 2);
    }

    static NetDrawItem[] OrigItems(NetworkMenuController menu, FrontEndFont fonts, NetworkChat? chat = null, int tick = 0) => Items(menu, chat, tick, fonts);

    /// <summary>The <c>car</c> line of the board (<c>car : f355</c>, or just <c>car</c> without a member).</summary>
    static NetDrawItem BoardCar(NetDrawItem[] items) =>
        items.First(i => i.Op == NetOp.Text && i.Align == NetAlign.Right && i.X == NetworkMenuLayout.BoardX && (i.Text == "car" || i.Text!.StartsWith("car : ")));

    static NetDrawItem TextAt(NetDrawItem[] items, string text)
    {
        foreach (var i in items)
            if (i.Op == NetOp.Text && i.Text == text) return i;
        throw new Xunit.Sdk.XunitException($"no text '{text}'");
    }

    /// <summary>No two texts overlap, every text lies inside one of the given panels (or the footer / left column strips), no
    /// text starts with the classic cursor, the data panels are disjoint.</summary>
    static void AssertOrigClean(string name, NetDrawItem[] items, FrontEndFont fonts, params (int X0, int Y0, int X1, int Y1)[] panels)
    {
        var texts = items.Where(i => i.Op == NetOp.Text).ToArray();
        for (int a = 0; a < texts.Length; a++)
        {
            Assert.False(texts[a].Text!.StartsWith("> ") && texts[a].Size != NetFont.Small, $"{name}: cursor in '{texts[a].Text}'");   // (a typed chat line starts with one)
            var ra = OrigRect(texts[a], fonts);
            Assert.True(Inside(ra, 0, 0, 640, 480), $"{name}: '{texts[a].Text}' {ra} leaves the buffer");
            for (int b = a + 1; b < texts.Length; b++)
                Assert.False(Overlap(ra, OrigRect(texts[b], fonts)), $"{name}: '{texts[a].Text}' {ra} overlaps '{texts[b].Text}' {OrigRect(texts[b], fonts)}");
            bool left = texts[a].Align == NetAlign.Right && texts[a].X == NetworkMenuLayout.BoardX;
            bool footer = texts[a].Y >= NetworkMenuLayout.OriginalFooterY;
            Assert.True(left || footer || panels.Any(p => Inside(ra, p.X0, p.Y0, p.X1, p.Y1)), $"{name}: '{texts[a].Text}' {ra} is outside every panel");
        }
        var panelItems = items.Where(i => i.Op == NetOp.Panel && i.Kind is NetPanelKind.Data or NetPanelKind.DataActive).ToArray();
        for (int a = 0; a < panelItems.Length; a++)
            for (int b = a + 1; b < panelItems.Length; b++)
                Assert.False(Overlap((panelItems[a].X, panelItems[a].Y, panelItems[a].X1, panelItems[a].Y1),
                    (panelItems[b].X, panelItems[b].Y, panelItems[b].X1, panelItems[b].Y1)), $"{name}: panels overlap");
    }

    static (int X0, int Y0, int X1, int Y1) PanelOf(NetDrawItem[] items)
    {
        var p = items.First(i => i.Op == NetOp.Panel && i.Kind is NetPanelKind.Data or NetPanelKind.DataActive);
        return (p.X, p.Y, p.X1, p.Y1);
    }

    [GameDataFact]
    public void OriginalConnect_IsBareTextOnTheMapLikeGameSetupWithUpperCaseButtons()
    {
        var fonts = new FrontEndFont();
        var onConnect = new NetworkMenuController("Driver");
        for (int i = 0; i < 3; i++) onConnect.Key(MenuKey.Down);
        var items = OrigItems(onConnect, fonts);
        // no panel at all (S1): the text lies on the map, like "game setup" of the game
        Assert.DoesNotContain(items, i => i.Op == NetOp.Panel);
        // the cyan upper-case title of the car screen: same centre and row
        var title = TextAt(items, "NETWORK RACE");
        Assert.Equal((NetAlign.Center, NetFont.Normal, NetRole.Header, 346, NetworkMenuLayout.FormTitleY), (title.Align, title.Size, title.Role, title.X, title.Y));
        Assert.Equal(34, TextAt(items, "nick").Y - title.Y);   // as far over the first row as the title of the car screen (50 over 84)
        Assert.Equal((NetworkMenuLayout.FormTitleX, NetworkMenuLayout.FormTitleY), (title.X, title.Y));
        int[] ys = [165, 191, 217];
        string[] labels = ["nick", "server", "port"], values = ["Driver", "127.0.0.1", "27100"];
        for (int i = 0; i < 3; i++)
        {
            var l = TextAt(items, labels[i]);
            Assert.Equal((350, ys[i], NetAlign.Right), (l.X, l.Y, l.Align));
            var colon = items.First(t => t.Op == NetOp.Text && t.Text == ":" && t.Y == ys[i]);
            Assert.Equal(362, colon.X);
            var v = items.First(t => t.Op == NetOp.Text && t.Text!.TrimEnd() == values[i]);
            Assert.Equal((372, ys[i]), (v.X, v.Y));
        }
        // the cursor starts on CONNECT (here: moved there): upper-case buttons on one line, the lit one white, the other light grey
        var connect = TextAt(items, "CONNECT"); var back = TextAt(items, "BACK");
        Assert.Equal((262, 262), (connect.Y, back.Y));
        Assert.Equal((NetRole.Selected, NetRole.ButtonIdle), (connect.Role, back.Role));
        Assert.Equal((350, NetAlign.Right, 372, NetAlign.Left), (connect.X, connect.Align, back.X, back.Align));   // like DONE   CANCEL of the game
        Assert.True(OrigRect(connect, fonts).X1 < OrigRect(back, fonts).X0);
        onConnect.Key(MenuKey.Right);
        var onBack = OrigItems(onConnect, fonts);
        Assert.Equal((NetRole.ButtonIdle, NetRole.Selected), (TextAt(onBack, "CONNECT").Role, TextAt(onBack, "BACK").Role));
        // the idle button is lighter than a label (and than a disabled text), the lit one is white
        Assert.True(Lum(OriginalNetBackend.Colour(NetRole.ButtonIdle)) > Lum(OriginalNetBackend.Colour(NetRole.Label)));
        Assert.True(Lum(OriginalNetBackend.Colour(NetRole.ButtonIdle)) > Lum(OriginalNetBackend.Colour(NetRole.Disabled)));
        Assert.True(Lum(OriginalNetBackend.Colour(NetRole.Selected)) > Lum(OriginalNetBackend.Colour(NetRole.ButtonIdle)));
        AssertOrigClean("connect", items, fonts, (236, 0, 628, 432));
    }


    [GameDataFact]
    public void OriginalConnect_SelectionIsColourEditingKeepsTheEndAndErrorsGoOnTheBarOverTheFooter()
    {
        var fonts = new FrontEndFont();
        var menu = new NetworkMenuController("Driver");
        Assert.Equal(0, menu.Selected);   // the cursor starts on the nick
        var items = OrigItems(menu, fonts);
        Assert.Equal(NetRole.Selected, TextAt(items, "nick").Role);
        Assert.Equal(NetRole.Label, TextAt(items, "server").Role);
        Assert.Equal(NetRole.ValueSelected, TextAt(items, "Driver").Role);
        Assert.Equal(NetRole.Value, TextAt(items, "127.0.0.1").Role);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Panel);
        Assert.All(items.Where(i => i.Op == NetOp.Text), t => Assert.False(t.Tail));
        menu.Key(MenuKey.Enter);
        var editing = OrigItems(menu, fonts);
        Assert.DoesNotContain(editing, i => i.Op == NetOp.Panel);   // no panel to light up: the white value and the footer tell the edit
        var edited = TextAt(editing, "Driver_");
        Assert.Equal(NetRole.Selected, edited.Role);
        Assert.True(edited.Tail);   // a long value keeps its end and the caret (S9)
        Assert.Contains(OrigItems(menu, fonts, tick: 30), i => i.Op == NetOp.Text && i.Text == "Driver ");
        Assert.Contains(editing, i => i.Op == NetOp.Text && i.Text == "TYPE TEXT   ENTER DONE   ESC CANCEL");
        // an error: red on the bar over the footer (as everywhere), not inside a panel
        var failed = Connecting(out var view);
        view.Phase = SessionClientPhase.Rejected;
        view.LastReject = new NetReject(NetRejectReason.NameTaken);
        failed.Update(1);
        var err = OrigItems(failed, fonts);
        var msg = err.Single(i => i.Op == NetOp.Text && i.Role == NetRole.Bad);
        Assert.Equal((NetworkMenuLayout.MessageY, NetFont.Small, NetAlign.Center, 432), (msg.Y, msg.Size, msg.Align, msg.X));
        Assert.Contains(err, i => i.Op == NetOp.Panel && i.Kind == NetPanelKind.ChatPlate && i.Y < msg.Y && i.Y1 > msg.Y + 14);
        Assert.DoesNotContain(err, i => i.Op == NetOp.Panel && i.Kind is NetPanelKind.Data or NetPanelKind.DataActive);
    }

    [GameDataFact]
    public void OriginalConnecting_IsAGameDialogWithTheTargetAndACancelFooter()
    {
        var fonts = new FrontEndFont();
        var items = OrigItems(Connecting(out _, host: "example.org"), fonts);
        var dialog = items.Single(i => i.Op == NetOp.Dialog);
        Assert.Equal("network race", dialog.Text);
        Assert.Equal("connecting to example.org:27100", dialog.Text2);
        var footer = items.Single(i => i.Op == NetOp.Text);
        Assert.Equal(("ESC CANCEL", 320, NetworkMenuLayout.OriginalFooterY), (footer.Text, footer.X, footer.Y));
        Assert.DoesNotContain(items, i => i.Op == NetOp.Panel);
    }

    [GameDataFact]
    public void OriginalCreate_IsBareTextWithTheArrowsOfTheChangedValueAndTheNoteOnAStrip()
    {
        var fonts = new FrontEndFont();
        var menu = CreateScreen(out _);
        var items = OrigItems(menu, fonts);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Panel && i.Kind is NetPanelKind.Data or NetPanelKind.DataActive);
        var title = TextAt(items, "CREATE GAME");
        Assert.Equal((NetAlign.Center, NetRole.Header, 346, NetworkMenuLayout.FormTitleY), (title.Align, title.Role, title.X, title.Y));
        Assert.Equal(34, TextAt(items, "game name").Y - title.Y);   // as far over the first row as the title of the car screen (50 over 84)
        foreach (var (label, y) in new[] { ("game name", 165), ("max players", 191), ("mode", 217) })
            Assert.Equal((350, y, NetAlign.Right), (TextAt(items, label).X, TextAt(items, label).Y, TextAt(items, label).Align));
        Assert.Equal(NetRole.Selected, TextAt(items, "game name").Role);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && (i.Text == "<" || i.Text == ">"));   // arrows only on the changed row
        menu.Key(MenuKey.Down);
        var onPlayers = OrigItems(menu, fonts);
        var lt = TextAt(onPlayers, "<"); var gt = TextAt(onPlayers, ">"); var value = TextAt(onPlayers, "8");
        Assert.Equal((NetRole.Muted, NetRole.Muted, NetRole.ValueSelected), (lt.Role, gt.Role, value.Role));
        Assert.True(lt.X < value.X && value.X < gt.X);
        var colon = onPlayers.First(t => t.Op == NetOp.Text && t.Text == ":" && t.Y == value.Y);
        Assert.True(OrigRect(colon, fonts).X1 + 4 <= OrigRect(lt, fonts).X0, $"the arrow {OrigRect(lt, fonts)} keeps clear of the colon {OrigRect(colon, fonts)}");
        Assert.Equal(value.Y, lt.Y);
        menu.Key(MenuKey.Down);
        Assert.Contains(OrigItems(menu, fonts), i => i.Op == NetOp.Text && i.Text == "single race");
        menu.Key(MenuKey.Right);
        Assert.Contains(OrigItems(menu, fonts), i => i.Op == NetOp.Text && i.Text == "knockout");
        // upper-case buttons under the rows, the note below them (every line at most 330 px) on a 55% strip that holds only those lines
        var create = TextAt(items, "CREATE"); var back = TextAt(items, "BACK");
        Assert.Equal((262, 262, NetRole.ButtonIdle, NetRole.ButtonIdle), (create.Y, back.Y, create.Role, back.Role));
        var note = items.Where(i => i.Op == NetOp.Text && i.Size == NetFont.Small && i.Role == NetRole.Label && i.Y >= 290 && i.Y < 340).ToArray();
        Assert.Equal(3, note.Length);
        foreach (var n in note) Assert.InRange(fonts.Font(13).Measure(n.Text!), 1, 330);
        var strip = items.Single(i => i.Op == NetOp.Panel && i.Kind == NetPanelKind.ChatPlate && i.Y < 340);
        foreach (var n in note)
        {
            var r = OrigRect(n, fonts);
            Assert.True(r.X0 >= strip.X && r.X1 <= strip.X1 && r.Y0 >= strip.Y && r.Y1 <= strip.Y1, $"'{n.Text}' {r} is on the strip");
        }
        Assert.True(strip.Y > create.Y + 20 && strip.Y1 - strip.Y < 70, "the strip is under the buttons and as high as the three lines");
        AssertOrigClean("create", items, fonts, (236, 0, 628, 432));
        AssertOrigClean("create_arrows", onPlayers, fonts, (236, 0, 628, 432));
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == "UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK" && i.Y == 456);
        var editing = CreateScreen(out _);
        editing.Key(MenuKey.Enter);
        var editItems = OrigItems(editing, fonts);
        Assert.DoesNotContain(editItems, i => i.Op == NetOp.Panel && i.Kind is NetPanelKind.Data or NetPanelKind.DataActive);
        Assert.Contains(editItems, i => i.Op == NetOp.Text && i.Text!.EndsWith('_') && i.Role == NetRole.Selected && i.Tail);
    }

    [Fact]
    public void LeftAndRight_MoveBetweenConnectAndBackAndBetweenCreateAndBack()
    {
        var menu = new NetworkMenuController("Driver");
        for (int i = 0; i < 3; i++) menu.Key(MenuKey.Down);
        Assert.Equal((int)ConnectRow.Connect, menu.Selected);
        menu.Key(MenuKey.Right);
        Assert.Equal((int)ConnectRow.Back, menu.Selected);
        menu.Key(MenuKey.Right);
        Assert.Equal((int)ConnectRow.Connect, menu.Selected);
        menu.Key(MenuKey.Left);
        Assert.Equal((int)ConnectRow.Back, menu.Selected);
        Assert.Equal(NetScreen.Connect, menu.Screen);
        // the fields ignore LEFT/RIGHT as before
        menu.Key(MenuKey.Up); menu.Key(MenuKey.Up); menu.Key(MenuKey.Up);
        Assert.Equal((int)ConnectRow.Host, menu.Selected);
        menu.Key(MenuKey.Right); menu.Key(MenuKey.Left);
        Assert.Equal((int)ConnectRow.Host, menu.Selected);
        Assert.Equal("127.0.0.1", menu.Host);

        var create = CreateScreen(out _);
        for (int i = 0; i < 3; i++) create.Key(MenuKey.Down);
        Assert.Equal(CreateRow.Create, NetworkMenuController.CreateRows[create.Selected]);
        create.Key(MenuKey.Left);
        Assert.Equal(CreateRow.Back, NetworkMenuController.CreateRows[create.Selected]);
        create.Key(MenuKey.Right);
        Assert.Equal(CreateRow.Create, NetworkMenuController.CreateRows[create.Selected]);
        Assert.Equal(NetScreen.CreateOptions, create.Screen);
        create.Key(MenuKey.Up);   // the values still change with LEFT/RIGHT
        create.Key(MenuKey.Up);
        Assert.Equal(CreateRow.MaxPlayers, NetworkMenuController.CreateRows[create.Selected]);
        create.Key(MenuKey.Left);
        Assert.Equal(7, create.MaxPlayers);
    }

    static NetGameInfo GameAt(int id, string name, NetGamePhase phase, int players, int max, int track = 1, int laps = 3) =>
        new(id, name, phase, players, max, track, laps, NetGameMode.SingleRace);

    const int GamesPanelBottom = 108 + 8 * 26 + 4;

    [GameDataFact]
    public void OriginalGames_EmptyListShowsTheHintAndDimsJoin()
    {
        var fonts = new FrontEndFont();
        var items = OrigItems(Browsing(out _), fonts);
        Assert.Equal((236, 60, 628, GamesPanelBottom), PanelOf(items));
        var hint = TextAt(items, "no games - create one");
        Assert.Equal((432, 160, NetAlign.Center, NetFont.Normal, NetRole.Label), (hint.X, hint.Y, hint.Align, hint.Size, hint.Role));
        Assert.Equal(NetRole.InkDisabled, TextAt(items, "join").Role);
        Assert.Equal(200, TextAt(items, "join").Y);
        Assert.Equal(new[] { 236, 272, 308 }, new[] { "create game", "refresh", "back" }.Select(t => TextAt(items, t).Y));
        Assert.Equal(NetRole.InkSelected, TextAt(items, "create game").Role);   // the cursor starts on the first row: create
        Assert.Equal(NetRole.Ink, TextAt(items, "back").Role);
        Assert.All(new[] { "join", "create game", "refresh", "back" }, t => Assert.Equal((168, NetAlign.Right), (TextAt(items, t).X, TextAt(items, t).Align)));
        foreach (var (h, x) in new[] { ("name", 246), ("status", 346), ("players", 436), ("track", 500), ("laps", 596) })
            Assert.Equal((x, 90, NetFont.Small, NetRole.Header), (TextAt(items, h).X, TextAt(items, h).Y, TextAt(items, h).Size, TextAt(items, h).Role));
        Assert.DoesNotContain(items, i => i.Op == NetOp.RowBar);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.Contains(" of "));
        AssertOrigClean("games_empty", items, fonts, (236, 60, 628, GamesPanelBottom));
        var pending = Browsing(out var view);
        view.Pending = true;
        Assert.Contains(OrigItems(pending, fonts), i => i.Op == NetOp.Text && i.Text == "waiting for server..." && i.Y == 160);
    }

    [GameDataFact]
    public void OriginalGames_RowsStatusesFullAndSelection()
    {
        var fonts = new FrontEndFont();
        var games = new[]
        {
            GameAt(1, "Alpha", NetGamePhase.Lobby, 2, 4), GameAt(2, "Beta", NetGamePhase.Countdown, 3, 4),
            GameAt(3, "Gamma", NetGamePhase.Race, 4, 4), GameAt(4, "Delta", NetGamePhase.Lobby, 4, 4),
        };
        var menu = Browsing(out _, games);
        for (int i = 0; i < 20 && !(menu.GamesRowAt(menu.Selected, out int g) == GamesRow.Game && g == 0); i++) menu.Key(MenuKey.Down);
        var items = OrigItems(menu, fonts);
        var bar = items.Single(i => i.Op == NetOp.RowBar);
        var alpha = TextAt(items, "Alpha");
        Assert.Equal(NetRole.Selected, alpha.Role);
        Assert.True(bar.Y < alpha.Y && bar.Y1 > alpha.Y + 18 && bar.X >= 236 && bar.X1 <= 628, "the bar holds the row");
        Assert.Equal(108, alpha.Y);
        Assert.Equal(NetRole.Good, TextAt(items, "lobby").Role);
        // the others are not joinable: whole row dimmed, "full" marks the packed lobby
        foreach (var t in new[] { "Beta", "Gamma", "countdown", "race", "Delta" }) Assert.Equal(NetRole.Disabled, TextAt(items, t).Role);
        var full = TextAt(items, "full");
        Assert.Equal((NetRole.Disabled, NetFont.Small), (full.Role, full.Size));
        Assert.Equal(NetRole.Ink, TextAt(items, "join").Role);   // the selected game can be joined
        // the name of a game that can be joined is white, the track is the value gold in lower case; an unjoinable row is grey all over (S5, S11)
        Assert.Equal(NetRole.Selected, TextAt(items, "Alpha").Role);
        var tracks = items.Where(i => i.Op == NetOp.Text && i.Text!.StartsWith("track", StringComparison.Ordinal) && i.Y >= 108).ToArray();
        Assert.Equal(4, tracks.Length);
        Assert.All(tracks, t => Assert.Equal(t.Text, t.Text!.ToLowerInvariant()));
        Assert.Equal(NetRole.Value, tracks[0].Role);
        Assert.All(tracks.Skip(1), t => Assert.Equal(NetRole.Disabled, t.Role));
        Assert.Equal(new[] { 108, 134, 160, 186 }, new[] { "Alpha", "Beta", "Gamma", "Delta" }.Select(t => TextAt(items, t).Y));   // pitch 26 like the setup menu
        // the cursor on an unavailable game dims "join"
        menu.Key(MenuKey.Down);
        var onBeta = OrigItems(menu, fonts);
        Assert.Equal(NetRole.InkDisabled, TextAt(onBeta, "join").Role);
        Assert.Single(onBeta, i => i.Op == NetOp.RowBar);
        AssertOrigClean("games", items, fonts, (236, 60, 628, GamesPanelBottom));
        AssertOrigClean("games_beta", onBeta, fonts, (236, 60, 628, GamesPanelBottom));
    }

    [GameDataFact]
    public void OriginalGames_EightRowsAndAScrolledListWithItsPosition()
    {
        var fonts = new FrontEndFont();
        var many = Enumerable.Range(1, 12).Select(i => GameAt(i, $"Game number {i}", NetGamePhase.Lobby, 1, 4, i % 5, 1 + i % 9)).ToArray();
        var menu = Browsing(out var view, many[..8]);
        var eight = OrigItems(menu, fonts);
        Assert.Equal(8, eight.Count(i => i.Op == NetOp.Text && i.Text!.StartsWith("Game number")));
        Assert.DoesNotContain(eight, i => i.Op == NetOp.Text && i.Text!.Contains(" of "));   // everything fits: no position
        var panel = PanelOf(eight);
        var lastRow = eight.Where(i => i.Op == NetOp.Text && i.Text!.StartsWith("Game number")).Max(i => i.Y);
        Assert.True(lastRow + 20 <= panel.Y1, "the 8th row is inside the panel");
        AssertOrigClean("games_8", eight, fonts, (236, 60, 628, GamesPanelBottom));
        view.Games = many;
        menu.Update(1);
        for (int i = 0; i < 30 && !(menu.GamesRowAt(menu.Selected, out int g) == GamesRow.Game && g == 11); i++) menu.Key(MenuKey.Up);
        var scrolled = OrigItems(menu, fonts);
        Assert.Equal(8, scrolled.Count(i => i.Op == NetOp.Text && i.Text!.StartsWith("Game number")));
        var counter = TextAt(scrolled, "5-12 of 12");
        Assert.Equal((616, NetAlign.Right, NetRole.Header), (counter.X, counter.Align, counter.Role));
        Assert.Contains(scrolled, i => i.Op == NetOp.Text && i.Text == "Game number 12" && i.Role == NetRole.Selected);
        Assert.Single(scrolled, i => i.Op == NetOp.RowBar);
        AssertOrigClean("games_scrolled", scrolled, fonts, (236, 60, 628, GamesPanelBottom));
        // long names are cut to their column by the backend (the maximum width), never run into the next one
        var longGame = Browsing(out var lv, GameAt(1, "A very long game name that does not fit", NetGamePhase.Countdown, 4, 4));
        longGame.Update(1);
        var longItems = OrigItems(longGame, fonts);
        Assert.InRange(TextAt(longItems, "A very long game name that does not fit").X1, 1, 100);
        AssertOrigClean("games_long", longItems, fonts, (236, 60, 628, GamesPanelBottom));
    }

    [GameDataFact]
    public void OriginalGames_AReservedGameIsMarkedAndNotDimmed()
    {
        var fonts = new FrontEndFont();
        var games = new[] { GameAt(1, "Open", NetGamePhase.Lobby, 1, 4), GameAt(7, "Running", NetGamePhase.Race, 2, 4) };   // 7 = the game of the fake lobby
        var lobby = InGame(out var view, NetGamePhase.Race, others: "Bob", game: "Running");
        view.Games = games;
        lobby.Key(MenuKey.Escape);                      // confirm
        lobby.Key(MenuKey.Left);                        // yes
        lobby.Key(MenuKey.Enter);
        view.Phase = SessionClientPhase.Browsing;
        view.Lobby = null;
        lobby.Update(10);
        Assert.Equal(NetScreen.Games, lobby.Screen);
        Assert.Contains(lobby.Games, g => lobby.ReservedFor(g.GameId));
        var items = OrigItems(lobby, fonts);
        var yours = TextAt(items, "yours");
        Assert.Equal((NetRole.Header, NetFont.Small), (yours.Role, yours.Size));
        Assert.NotEqual(NetRole.Disabled, TextAt(items, "Running").Role);
        Assert.Equal(NetRole.Bad, TextAt(items, "race").Role);   // a joinable game in the race keeps its colour
        Assert.Contains(items, i => i.Op == NetOp.Panel && i.Kind == NetPanelKind.ChatPlate && i.Y == 432);   // the message of leaving the race sits on its bar
        AssertOrigClean("games_reserved", items, fonts, (236, 60, 628, GamesPanelBottom), (236, 432, 628, 452));
    }

    static RaceResults EightResults() => new(
        Enumerable.Range(0, 8).Select(i => new RacerResult(i + 1, i, "MCF1", i < 2, i == 1, i == 6 ? 0 : 64 * (95 + i), [64 * 31], i == 6 ? null : 64 * 31 + i,
            i == 6 ? 1 : 2, i == 0 ? "Me" : i == 1 ? "Bob" : $"Racer{i}", 100 << 16, i)).ToArray());

    [GameDataFact]
    public void OriginalSummary_HasTheTableTheLeftColumnAndTheRowOfThePlayer()
    {
        var fonts = new FrontEndFont();
        var menu = SummaryScreen(out _, SomeResults());
        menu.CarNames = FullNames;
        var items = OrigItems(menu, fonts);
        var panel = PanelOf(items);
        Assert.Equal((236, 40, 628), (panel.X0, panel.Y0, panel.X1));
        Assert.Equal((NetRole.Value, NetFont.Normal), (TextAt(items, "race results").Role, TextAt(items, "race results").Size));
        foreach (var (h, x, al) in new[] { ("pos", 246, NetAlign.Left), ("name", 284, NetAlign.Left), ("car", 366, NetAlign.Left), ("time", 538, NetAlign.Right), ("best", 616, NetAlign.Right) })
            Assert.Equal((x, al, 76, NetFont.Small, NetRole.Header), (TextAt(items, h).X, TextAt(items, h).Align, TextAt(items, h).Y, TextAt(items, h).Size, TextAt(items, h).Role));
        var me = TextAt(items, "Me");
        Assert.Equal((NetRole.ValueSelected, 96), (me.Role, me.Y));
        Assert.DoesNotContain(items, i => i.Op == NetOp.RowBar);   // the table has no cursor: "me" is told by colour only (S12)
        // time and best are right-aligned (digits under digits); the star of an estimated time has a slot of its own after the time (S10)
        var times = items.Where(i => i.Op == NetOp.Text && i.Y >= 96 && i.Y < 200 && i.Align == NetAlign.Right && i.X == 538).ToArray();
        Assert.Equal(3, times.Length);   // two times and DNF
        Assert.Equal(3, items.Count(i => i.Op == NetOp.Text && i.Y >= 96 && i.Y < 200 && i.Align == NetAlign.Right && i.X == 616));
        var star = items.Single(i => i.Op == NetOp.Text && i.Text == "*");
        Assert.Equal((NetAlign.Left, TextAt(items, "Bob").Y), (star.Align, star.Y));
        Assert.True(star.X > 538 && star.X < 616 - 62, "the star stands between the time and the best lap");
        Assert.Equal(times.Where(t => t.Y == star.Y).Single().Text, NetText.Time(64 * 99));   // the star is not part of the time
        var legend = TextAt(items, NetworkMenuLayout.EstimatedLegend);
        Assert.Equal((616, NetAlign.Right, NetFont.Small, NetRole.Label), (legend.X, legend.Align, legend.Size, legend.Role));
        Assert.True(legend.Y >= 96 + 8 * 26 && legend.Y < panel.Y1 - 14, "the legend is at the foot of the panel");
        Assert.Equal(NetRole.Selected, TextAt(items, "Bob").Role);
        Assert.Equal(NetRole.Disabled, TextAt(items, "Beefy").Role);
        Assert.Equal(NetRole.Disabled, TextAt(items, "DNF").Role);
        Assert.Equal(NetRole.Label, TextAt(items, "FERRARI F355").Role);
        Assert.Equal(NetRole.Value, TextAt(items, "MCLAREN F1").Role);
        Assert.Equal(new[] { 96, 122, 148 }, new[] { "Me", "Bob", "Beefy" }.Select(t => TextAt(items, t).Y));
        Assert.Equal(NetFont.Small, TextAt(items, "MCLAREN F1").Size);
        Assert.Equal(214, TextAt(items, "return to lobby").Y);
        Assert.Equal((NetRole.InkSelected, NetRole.Ink), (TextAt(items, "return to lobby").Role, TextAt(items, "exit").Role));
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.StartsWith("round"));
        AssertOrigClean("summary", items, fonts, (236, 40, 628, panel.Y1));
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == "T CHAT   UP/DOWN SELECT   ENTER OK   ESC EXIT" && i.Y == 456);
        var empty = OrigItems(SummaryScreen(out _, null), fonts);
        Assert.Contains(empty, i => i.Op == NetOp.Text && i.Text == "waiting for results...");
        AssertOrigClean("summary_empty", empty, fonts, (236, 40, 628, 340));
    }

    [GameDataFact]
    public void OriginalSummary_EightRowsAndAKnockoutLineFitTheirPanelAndTheChatBelowIt()
    {
        var fonts = new FrontEndFont();
        var menu = SummaryScreen(out var view, EightResults());
        view.Edit(l => l with { Mode = NetGameMode.Knockout, Series = Series(2, me0Out: false) });
        menu.Update(2);
        var chat = ChatWith(NetworkChat.Visible);
        var items = OrigItems(menu, fonts, chat);
        var panels = items.Where(i => i.Op == NetOp.Panel && i.Kind == NetPanelKind.Data).ToArray();
        Assert.Equal(2, panels.Length);
        var table = panels[0]; var chatPanel = panels[1];
        Assert.True(table.Y1 < chatPanel.Y, "the panels do not touch");
        var racers = items.Where(i => i.Op == NetOp.Text && i.X == 284 && i.Y >= 96).ToArray();
        Assert.Equal(8, racers.Length);
        Assert.Equal(7 * 26, racers.Max(r => r.Y) - racers.Min(r => r.Y));
        var ko = items.Single(i => i.Op == NetOp.Text && i.Text!.StartsWith("round "));
        Assert.StartsWith("round 2/8   out: ", ko.Text);
        Assert.Equal(NetRole.Label, ko.Role);
        Assert.True(ko.Y >= racers.Max(r => r.Y) + 22, "under the last row");
        Assert.Single(items, i => i.Op == NetOp.Text && i.Text == "DNF");
        (int, int, int, int) R(NetDrawItem p) => (p.X, p.Y, p.X1, p.Y1);
        AssertOrigClean("summary_8_knockout", items, fonts, R(table), R(chatPanel));
        // the chat: nick in the header colour, the text plain, and the line being typed in the place of the footer
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Role == NetRole.Header && i.Text!.EndsWith(": ") && i.Y >= chatPanel.Y);
        chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
        var typing = OrigItems(menu, fonts, chat);
        Assert.Equal(NetPanelKind.ChatInput, typing.Last(i => i.Op == NetOp.Panel).Kind);
        Assert.Contains(typing, i => i.Op == NetOp.Text && i.Text == "ENTER SEND   ESC CANCEL");
        Assert.DoesNotContain(typing, i => i.Op == NetOp.Text && i.Text == "T CHAT   UP/DOWN SELECT   ENTER OK   ESC EXIT");
        AssertOrigClean("summary_typing", typing, fonts, R(table), R(chatPanel), (236, 438, 628, 468));
    }

    [GameDataFact]
    public void OriginalLook_ReconnectBannerReplacesTheFooterOnTheNewScreens()
    {
        var fonts = new FrontEndFont();
        var menu = Browsing(out var view);
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(3);
        var items = OrigItems(menu, fonts);
        var bar = items.Last(i => i.Op == NetOp.Panel);
        Assert.Equal((NetPanelKind.Banner, 448, 470), (bar.Kind, bar.Y, bar.Y1));
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ReconnectHint && i.Y == 456);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "UP/DOWN SELECT   ENTER OK   ESC BACK");
    }

    // ---------------- the original lobby and countdown (change network-menu-look, task 3.4) ----------------

    static (int X0, int Y0, int X1, int Y1) PanelRect(NetDrawItem p) => (p.X, p.Y, p.X1, p.Y1);

    static NetDrawItem[] DataPanels(NetDrawItem[] items) =>
        items.Where(i => i.Op == NetOp.Panel && i.Kind is NetPanelKind.Data or NetPanelKind.DataActive).ToArray();

    static NetworkMenuController EightPlayerLobby(bool op = true) =>
        WithNames(InGame(out _, NetGamePhase.Lobby, op, 0, "Friday night", "Me", "Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal"));

    /// <summary>The label, colon and value texts of a settings row (same y, label right-aligned before the colon).</summary>
    static (NetDrawItem Label, NetDrawItem Colon, NetDrawItem Value) SettingsRow(NetDrawItem[] items, string label)
    {
        var l = items.First(i => i.Op == NetOp.Text && i.Text == label && i.Align == NetAlign.Right && i.Size == NetFont.Medium);
        var colon = items.First(i => i.Op == NetOp.Text && i.Text == ":" && i.Y == l.Y && i.X > l.X);
        var v = items.First(i => i.Op == NetOp.Text && i.Y == l.Y && i.X > colon.X && i.Size == NetFont.Medium && i.Text != ":");
        return (l, colon, v);
    }

    [GameDataFact]
    public void OriginalLobby_OperatorWithEightPlayersAndFiveChatLinesHasThreeSeparatePanels()
    {
        var fonts = new FrontEndFont();
        var menu = EightPlayerLobby();
        var chat = ChatWith(NetworkChat.Visible);
        var items = OrigItems(menu, fonts, chat);
        var panels = DataPanels(items);
        Assert.Equal(3, panels.Length);
        var (players, settings, chatPanel) = (panels[0], panels[1], panels[2]);
        Assert.Equal((236, 628), (players.X, players.X1));
        Assert.True(players.Y1 < settings.Y && settings.Y1 < chatPanel.Y, "the panels do not touch");
        Assert.Equal(NetPanelKind.Data, settings.Kind);
        AssertOrigClean("lobby_operator_8", items, fonts, PanelRect(players), PanelRect(settings), PanelRect(chatPanel));
        // all eight players, the number of the operator with its star, the local one on a bar
        foreach (var name in new[] { "Me", "Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal" })
            Assert.Equal(NetFont.Medium, TextAt(items, name).Size);
        Assert.Equal(NetRole.ValueSelected, TextAt(items, "Me").Role);
        Assert.Equal(NetRole.Selected, TextAt(items, "Bob").Role);
        Assert.Equal(NetRole.Disabled, TextAt(items, "Carol").Role);                   // the lost link dims the row
        Assert.Equal(NetRole.Bad, TextAt(items, "LOST").Role);
        Assert.Equal(NetRole.Value, TextAt(items, "*1").Role);
        Assert.DoesNotContain(items, i => i.Op == NetOp.RowBar);   // a table without a cursor has no bar: "me" is told by colour (S12)
        Assert.Equal(7 * 18, TextAt(items, "Hal").Y - TextAt(items, "Me").Y);
        // the chat in its own panel: five lines inside it, none over the settings
        var chatLines = items.Where(i => i.Op == NetOp.Text && i.Text!.StartsWith("line ")).ToArray();
        Assert.Equal(NetworkChat.Visible, chatLines.Length);
        Assert.All(chatLines, l => Assert.True(Inside(OrigRect(l, fonts), chatPanel.X, chatPanel.Y, chatPanel.X1, chatPanel.Y1)));
        // the left column: the actions in a vertical list, RACE in the large font, the cursor on ready (by colour)
        var actions = new[] { BoardCar(items), TextAt(items, "settings"), TextAt(items, "ready : yes"), TextAt(items, "RACE"), TextAt(items, "leave game") };
        Assert.Equal([NetFont.Normal, NetFont.Normal, NetFont.Normal, NetFont.Digit, NetFont.Normal], actions.Select(t => t.Size));
        Assert.True(actions[0].Y < actions[1].Y && actions[1].Y < actions[2].Y && actions[2].Y < actions[3].Y && actions[3].Y < actions[4].Y);
        Assert.All(actions, t => Assert.Equal((NetworkMenuLayout.BoardX, NetAlign.Right), (t.X, t.Align)));
        Assert.Equal([NetRole.Ink, NetRole.Ink, NetRole.InkSelected, NetRole.Ink, NetRole.Ink], actions.Select(t => t.Role));
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.Contains('>'));      // no cursor, the colour tells
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == "T CHAT   UP/DOWN SELECT   ENTER OK   ESC LEAVE" && i.Y == 456);
    }

    [GameDataFact]
    public void OriginalLobby_SettingsAreTwoCleanColumnsOfLabelAndValue()
    {
        var fonts = new FrontEndFont();
        var menu = EightPlayerLobby();
        var items = OrigItems(menu, fonts);
        var settings = DataPanels(items)[1];
        string[] left = ["track", "direction", "laps", "physics"], right = ["bots", "bots skill", "catch-up", "traffic"];
        var leftRows = left.Select(l => SettingsRow(items, l)).ToArray();
        var rightRows = right.Select(l => SettingsRow(items, l)).ToArray();
        // labels in one colour, values in another, the same x in a column, the same pitch, the colon between them
        foreach (var rows in new[] { leftRows, rightRows })
        {
            Assert.All(rows, r => Assert.Equal(NetRole.Label, r.Label.Role));
            Assert.All(rows, r => Assert.Equal(NetRole.Label, r.Colon.Role));
            Assert.All(rows, r => Assert.Equal(NetRole.Value, r.Value.Role));
            Assert.Single(rows.Select(r => r.Label.X).Distinct());
            Assert.Single(rows.Select(r => r.Colon.X).Distinct());
            Assert.Single(rows.Select(r => r.Value.X).Distinct());
            Assert.All(rows, r => Assert.Equal(NetAlign.Left, r.Value.Align));
            Assert.Equal(new[] { 0, 23, 46, 69 }, rows.Select(r => r.Label.Y - rows[0].Label.Y));
        }
        Assert.Equal(leftRows.Select(r => r.Label.Y), rightRows.Select(r => r.Label.Y));
        // a clear gap between the columns: the widest left value ends well before the widest right label starts
        int leftEnd = leftRows.Max(r => OrigRect(r.Value, fonts).X1), rightStart = rightRows.Min(r => OrigRect(r.Label, fonts).X0);
        Assert.True(rightStart - leftEnd >= 12, $"gap {rightStart - leftEnd}");
        // a gap between a label and its value, the font of the game's own menus
        foreach (var r in leftRows.Concat(rightRows))
        {
            Assert.True(OrigRect(r.Value, fonts).X0 - OrigRect(r.Label, fonts).X1 >= 10);
            Assert.Equal(NetFont.Medium, r.Label.Size);
            Assert.True(Inside(OrigRect(r.Value, fonts), settings.X, settings.Y, settings.X1, settings.Y1));
        }
        Assert.Equal("forward", SettingsRow(items, "direction").Value.Text);
        Assert.Equal("auto", SettingsRow(items, "bots").Value.Text);
        Assert.Equal("expert", SettingsRow(items, "bots skill").Value.Text);
        // the panel holds only the game options: no title, no car and colour (those are on the car screen)
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Role == NetRole.Header && i.Y >= settings.Y && i.Y < settings.Y1);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && (i.Text == "car" || i.Text == "colour") && i.Size == NetFont.Medium);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.Contains("your car"));
    }

    [GameDataFact]
    public void OriginalLobby_InSettingsTheSettingsPanelIsActiveTheColumnDimmedAndTheSelectedRowBright()
    {
        var fonts = new FrontEndFont();
        var menu = EightPlayerLobby();
        menu.Key(MenuKey.Up); menu.Key(MenuKey.Enter);
        Assert.True(menu.InSettings);
        var items = OrigItems(menu, fonts);
        Assert.Equal(NetPanelKind.DataActive, DataPanels(items)[1].Kind);
        Assert.Equal(NetPanelKind.Data, DataPanels(items)[0].Kind);
        // the dimmed board keeps the dim brown of the game (the grey Disabled is for the dark panels)
        foreach (var t in new[] { "ready : yes", "RACE", "leave game" })
            Assert.Equal(NetRole.InkDisabled, items.First(i => i.Op == NetOp.Text && i.Text == t).Role);
        Assert.Equal(NetRole.InkDisabled, items.First(i => i.Op == NetOp.Text && i.Text == "settings" && i.Align == NetAlign.Right).Role);
        Assert.Equal(NetRole.InkDisabled, BoardCar(items).Role);
        var track = SettingsRow(items, "track");
        Assert.Equal((NetRole.Selected, NetRole.Selected, NetRole.ValueSelected), (track.Label.Role, track.Colon.Role, track.Value.Role));
        Assert.Equal(NetRole.Label, SettingsRow(items, "direction").Label.Role);
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.SettingsHint && i.Y == 456);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.Contains('>'));
        // down the settings the selection moves by colour only
        menu.Key(MenuKey.Down);
        var moved = OrigItems(menu, fonts);
        Assert.Equal(NetRole.Label, SettingsRow(moved, "track").Label.Role);
        Assert.Equal(NetRole.Selected, SettingsRow(moved, "direction").Label.Role);
        // back with ESC: the cursor is on RACE (start is one ENTER away), the panel is the ordinary one again
        menu.Key(MenuKey.Escape);
        var back = OrigItems(menu, fonts);
        Assert.Equal(NetPanelKind.Data, DataPanels(back)[1].Kind);
        Assert.Equal(NetRole.InkSelected, TextAt(back, "RACE").Role);
        Assert.Equal(NetRole.Ink, back.First(i => i.Op == NetOp.Text && i.Text == "settings" && i.Align == NetAlign.Right).Role);
        Assert.Equal(NetRole.Ink, TextAt(back, "ready : yes").Role);
    }

    [GameDataFact]
    public void OriginalLobby_PlayerSeesHisCarAndTheGameButNoRace()
    {
        var fonts = new FrontEndFont();
        var menu = EightPlayerLobby(op: false);
        var chat = ChatWith(NetworkChat.Visible);
        var items = OrigItems(menu, fonts, chat);
        var panels = DataPanels(items);
        Assert.Equal(3, panels.Length);
        AssertOrigClean("lobby_player_8", items, fonts, PanelRect(panels[0]), PanelRect(panels[1]), PanelRect(panels[2]));
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.StartsWith("your car"));
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "RACE");
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "settings");               // nothing for a player to edit
        Assert.True(BoardCar(items).Y < TextAt(items, "ready : no").Y);
        Assert.True(TextAt(items, "ready : no").Y < TextAt(items, "leave game").Y);
        Assert.Equal(NetRole.InkSelected, TextAt(items, "ready : no").Role);
        // the options of the game in the same two columns (not dimmed), the car and colour of the player on his own screen
        Assert.Equal(NetRole.Value, SettingsRow(items, "track").Value.Role);
        Assert.Equal(NetRole.Value, SettingsRow(items, "physics").Value.Role);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "car" && i.Size == NetFont.Medium);
        menu.Key(MenuKey.Up); menu.Key(MenuKey.Enter);
        Assert.False(menu.InSettings);
        Assert.True(menu.InCar);
    }

    /// <summary>A lobby whose local player has the given car and colour, with the car screen open.</summary>
    static NetworkMenuController CarScreenLobby(int car, int colour, bool op = true)
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, op, 0, "Friday night", "Me", "Bob", "Carol");
        view.Edit(l => l with { Members = [.. l.Members.Select(m => m.Name == "Me" ? m with { CarType = car, Colour = colour } : m)] });
        menu.Update(0);
        menu.CarNames = FullNames;
        return CarScreenOf(WithNames(menu));
    }

    [GameDataFact]
    public void OriginalCarScreen_IsLikePlayerOne_NickCarColourDoneCancelAndThePictureUnderThem()
    {
        var fonts = new FrontEndFont();
        var menu = CarScreenLobby(car: 3, colour: 0x44);
        var chat = ChatWith(NetworkChat.Visible);
        var items = OrigItems(menu, fonts, chat);
        // the title: the own nick in the colour of the headers, centred like PLAYER ONE (TEXT entry 72: 346, 50)
        var title = TextAt(items, "ME");
        Assert.Equal((NetFont.Normal, NetRole.Header, NetAlign.Center, 346, 50), (title.Size, title.Role, title.Align, title.X, title.Y));
        // car : name (right-aligned label at 346, the colon at 358, the value at 365; rows 26 apart, SWISS18)
        var car = items.First(i => i.Op == NetOp.Text && i.Text == "car" && i.Align == NetAlign.Right);
        var colour = items.First(i => i.Op == NetOp.Text && i.Text == "color" && i.Align == NetAlign.Right);
        Assert.Equal((346, 84, NetFont.Normal, NetRole.Selected), (car.X, car.Y, car.Size, car.Role));      // the cursor starts on the car
        Assert.Equal((346, 110, NetRole.Label), (colour.X, colour.Y, colour.Role));
        Assert.Equal(26, colour.Y - car.Y);
        Assert.All(new[] { car, colour }, l => Assert.Equal(NetFont.Normal, l.Size));
        Assert.Equal(2, items.Count(i => i.Op == NetOp.Text && i.Text == ":" && i.X == 358 && i.Size == NetFont.Normal));
        var name = items.First(i => i.Op == NetOp.Text && i.X == 365 && i.Y == car.Y);
        Assert.Equal((NetText.Lower(menu.CarName(3)), NetRole.ValueSelected, NetAlign.Left), (name.Text, name.Role, name.Align));
        // color : bar (0x54 x 0x15 in the paint of the colour, 1 px right and 5 px down)
        var bar = Assert.Single(items, i => i.Op == NetOp.Swatch && i.Rgb == MenuScene.SwatchColour(0x44));
        Assert.Equal((366, 115, 0x54, 0x15), (bar.X, bar.Y, bar.X1, bar.Y1));
        // done   cancel on one line under the rows
        var done = items.First(i => i.Op == NetOp.Text && i.Text == "DONE");
        var cancel = items.First(i => i.Op == NetOp.Text && i.Text == "CANCEL");
        Assert.Equal((NetRole.ButtonIdle, NetRole.ButtonIdle), (done.Role, cancel.Role));   // upper case; the cursor is on the car, so both idle: light grey (S2)
        Assert.Equal((346, NetAlign.Right, 366, NetAlign.Left), (done.X, done.Align, cancel.X, cancel.Align));
        Assert.Equal(done.Y, cancel.Y);
        Assert.True(done.Y > colour.Y + 26);
        // the picture of the car of the menu under them, clear of the chat panel
        var picture = Assert.Single(items, i => i.Op == NetOp.Preview);
        Assert.Equal((MenuScene.CarPreviewX, 3), (picture.X, picture.X1));
        Assert.Equal((280, 128), (MenuScene.CarPreviewWidth, MenuScene.CarPreviewHeight));
        var chatPanel = Assert.Single(DataPanels(items));
        Assert.True(picture.Y > done.Y + 20, "the picture is under done / cancel");
        Assert.True(picture.Y + MenuScene.CarPreviewHeight < chatPanel.Y, "the picture does not touch the chat panel");
        Assert.True(picture.X + MenuScene.CarPreviewWidth <= 640);
        // nothing of the lobby: no board, no players panel, no settings panel, no cursor sign
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Align == NetAlign.Right && i.X == NetworkMenuLayout.BoardX);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.Contains('>') && i.Size != NetFont.Small);
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.CarHint && i.Y == 456);
        Assert.Equal("T CHAT   UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK", NetworkMenuLayout.CarHint);
        // no text overlaps another, all of them inside the buffer
        var texts = items.Where(i => i.Op == NetOp.Text).ToArray();
        for (int a = 0; a < texts.Length; a++)
        {
            Assert.True(Inside(OrigRect(texts[a], fonts), 0, 0, 640, 480), texts[a].Text);
            for (int b = a + 1; b < texts.Length; b++)
                Assert.False(Overlap(OrigRect(texts[a], fonts), OrigRect(texts[b], fonts)), $"'{texts[a].Text}' overlaps '{texts[b].Text}'");
        }
    }

    [GameDataFact]
    public void OriginalCarScreen_TheCursorIsToldByColourAndAColourWithoutAChoiceReadsDefault()
    {
        var fonts = new FrontEndFont();
        var menu = CarScreenLobby(car: 3, colour: NetProtocol.NoColour);
        var items = OrigItems(menu, fonts);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Swatch);
        Assert.Equal((NetRole.Value, NetAlign.Left), (TextAt(items, "default").Role, TextAt(items, "default").Align));
        menu.Key(MenuKey.Down);
        items = OrigItems(menu, fonts);
        Assert.Equal(NetRole.Selected, items.First(i => i.Op == NetOp.Text && i.Text == "color" && i.Align == NetAlign.Right).Role);
        Assert.Equal(NetRole.Label, items.First(i => i.Op == NetOp.Text && i.Text == "car" && i.Align == NetAlign.Right).Role);
        Assert.Equal(NetRole.ValueSelected, TextAt(items, "default").Role);
        menu.Key(MenuKey.Down);
        items = OrigItems(menu, fonts);
        Assert.Equal((NetRole.Selected, NetRole.ButtonIdle), (TextAt(items, "DONE").Role, TextAt(items, "CANCEL").Role));
        menu.Key(MenuKey.Right);
        items = OrigItems(menu, fonts);
        Assert.Equal((NetRole.ButtonIdle, NetRole.Selected), (TextAt(items, "DONE").Role, TextAt(items, "CANCEL").Role));
    }

    [GameDataFact]
    public void OriginalCarScreen_AModelWithOneColourShowsItsBarDimmedAndAPlayerGetsTheSameScreen()
    {
        var fonts = new FrontEndFont();
        var menu = CarScreenLobby(car: 4, colour: NetProtocol.NoColour, op: false);
        var items = OrigItems(menu, fonts);
        Assert.Equal(NetRole.Disabled, items.First(i => i.Op == NetOp.Text && i.Text == "color" && i.Align == NetAlign.Right).Role);
        var bar = Assert.Single(items, i => i.Op == NetOp.Swatch);
        Assert.Equal(MenuScene.SwatchColour(NetworkMenuController.PaletteFirst(4)), bar.Rgb);
        Assert.Single(items, i => i.Op == NetOp.Preview);
        Assert.Equal("ME", TextAt(items, "ME").Text);
    }

    [GameDataFact]
    public void OriginalCarScreen_DrawsThePictureOfTheCarOfTheMenuAndItChangesWithTheModel()
    {
        var skin = OriginalSkin()!;
        var a = Render(CarScreenLobby(car: 0, colour: NetProtocol.NoColour), 0, true, skin);
        var b = Render(CarScreenLobby(car: 5, colour: NetProtocol.NoColour), 0, true, skin);
        int x0 = MenuScene.CarPreviewX, y0 = 196;
        var colours = new HashSet<uint>();
        int differing = 0;
        for (int y = y0; y < y0 + MenuScene.CarPreviewHeight; y++)
            for (int x = x0; x < x0 + MenuScene.CarPreviewWidth; x++)
            {
                colours.Add(a.Get(x, y));
                if (a.Get(x, y) != b.Get(x, y)) differing++;
            }
        Assert.True(colours.Count > 200, $"the picture has {colours.Count} colours");
        Assert.True(differing > 2000, $"{differing} pixels differ between two models");
        // the classic look has no picture: the area keeps the dark chequer
        var classic = Render(CarScreenLobby(car: 0, colour: NetProtocol.NoColour), 0, true);
        var classicColours = new HashSet<uint>();
        for (int y = y0; y < y0 + MenuScene.CarPreviewHeight; y++)
            for (int x = x0; x < x0 + MenuScene.CarPreviewWidth; x++) classicColours.Add(classic.Get(x, y));
        Assert.True(classicColours.Count < 20);
    }

    [GameDataFact]
    public void OriginalCarScreen_FrameAllocatesNothingOnceWarm()
    {
        var skin = OriginalSkin()!;
        var menu = CarScreenLobby(car: 3, colour: 0x44);
        var chat = ChatWith(3);
        var fb = new FrameBuffer(640, 480);
        for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, menu, i, true, chat, skin: skin);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) NetworkMenuRenderer.Draw(fb, menu, 4 + i, true, chat, skin: skin);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
        Assert.True(bytes < 64, $"{bytes} B per frame");
    }

    [GameDataFact]
    public void OriginalLobby_SettingsPanelHoldsOnlyTheGameOptionsWithoutATitleAndKeepsClearOfTheOthers()
    {
        var fonts = new FrontEndFont();
        foreach (bool op in new[] { true, false })
        {
            var menu = EightPlayerLobby(op);
            var items = OrigItems(menu, fonts, ChatWith(NetworkChat.Visible));
            var panels = DataPanels(items);
            var settings = panels[1];
            Assert.True(panels[0].Y1 < settings.Y && settings.Y1 < panels[2].Y, "the three panels are disjoint");
            var inside = items.Where(i => i.Op == NetOp.Text && Inside(OrigRect(i, fonts), settings.X, settings.Y, settings.X1, settings.Y1)).ToArray();
            Assert.Equal(8 * 3 + (op ? 0 : 1), inside.Length);                           // eight rows: label, colon, value (+ "set by" for a player)
            Assert.Equal(op ? 0 : 1, inside.Count(t => t.Text!.StartsWith("set by ")));
            Assert.DoesNotContain(inside, t => t.Role == NetRole.Header);                // no title
            Assert.DoesNotContain(inside, t => t.Text is "car" or "colour" or "color");  // no car and colour
        }
    }

    static NetworkMenuController KnockoutLobbyScreen(int? completedRounds)
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, true, 0, "Knockout cup", "Me", "Bob");
        view.Edit(l => l with { Mode = NetGameMode.Knockout, Skill = 0, Style = 0, Laps = 3, Series = completedRounds is { } r ? Series(r, me0Out: false) : null });
        menu.Update(0);
        return WithNames(menu);
    }

    [GameDataFact]
    public void OriginalLobby_KnockoutBeforeAndAfterTheStartOfTheSeries()
    {
        var fonts = new FrontEndFont();
        var before = OrigItems(KnockoutLobbyScreen(null), fonts);
        // the name of the game is in the black of the board, the lines under it in the lighter brown (S7)
        Assert.Equal(NetRole.Ink, TextAt(before, "Knockout cup").Role);
        Assert.Equal(NetRole.InkSoft, TextAt(before, "knockout").Role);
        Assert.Equal(NetRole.InkSoft, TextAt(before, "2/8 players").Role);
        Assert.Equal(NetRole.InkSoft, TextAt(before, "round 1/8").Role);
        // the track follows the series and bots / traffic have no effect: dimmed ("n/a"), the rest still editable
        Assert.Equal((NetRole.Disabled, NetRole.Disabled), (SettingsRow(before, "track").Label.Role, SettingsRow(before, "track").Value.Role));
        Assert.Equal("n/a", SettingsRow(before, "bots").Value.Text);
        Assert.Equal((NetRole.Disabled, NetRole.Disabled), (SettingsRow(before, "bots").Label.Role, SettingsRow(before, "bots").Value.Role));
        Assert.Equal("n/a", SettingsRow(before, "traffic").Value.Text);
        Assert.Equal((NetRole.Label, NetRole.Value), (SettingsRow(before, "laps").Label.Role, SettingsRow(before, "laps").Value.Role));
        Assert.Equal(NetRole.Ink, BoardCar(before).Role);
        Assert.Equal(NetRole.Ink, before.First(i => i.Op == NetOp.Text && i.Text == "settings" && i.Align == NetAlign.Right).Role);
        Assert.DoesNotContain(before, i => i.Op == NetOp.Text && i.Text == "options locked");
        var panels = DataPanels(before);
        AssertOrigClean("lobby_knockout_before", before, fonts, PanelRect(panels[0]), PanelRect(panels[1]));

        var afterMenu = KnockoutLobbyScreen(2);
        var after = OrigItems(afterMenu, fonts);
        Assert.Equal(NetRole.InkSoft, TextAt(after, "options locked").Role);
        foreach (var label in new[] { "track", "direction", "laps", "bots", "bots skill", "catch-up", "traffic", "physics" })
        {
            var row = SettingsRow(after, label);
            Assert.Equal((NetRole.Disabled, NetRole.Disabled, NetRole.Disabled), (row.Label.Role, row.Colon.Role, row.Value.Role));
        }
        Assert.Equal(NetRole.InkDisabled, after.First(i => i.Op == NetOp.Text && i.Text == "settings" && i.Align == NetAlign.Right).Role);   // nothing to change: dimmed
        Assert.Equal(NetRole.InkDisabled, BoardCar(after).Role);                                  // the car is frozen with the series
        Assert.Equal(NetRole.InkSelected, TextAt(after, "ready : yes").Role);
        Assert.Contains(after, i => i.Op == NetOp.Text && i.Text!.StartsWith("out: "));
        panels = DataPanels(after);
        AssertOrigClean("lobby_knockout_after", after, fonts, PanelRect(panels[0]), PanelRect(panels[1]));
        // ... and a player that is out
        var outMenu = InGame(out var outView, NetGamePhase.Lobby, true, 0, "Knockout cup", "Me", "Bob");
        outView.Edit(l => l with { Mode = NetGameMode.Knockout, Series = Series(2, me0Out: true) });
        outMenu.Update(1);
        var outItems = OrigItems(WithNames(outMenu), fonts);
        Assert.Equal(NetRole.Disabled, TextAt(outItems, "you are out - watching").Role);
    }

    // ---------------- second round of the style review (2026-10-21) ----------------

    [GameDataFact]
    public void OriginalLobby_TheBoardHasTheNameInBlackAndTheLinesUnderItInTheLighterBrownWithinALogoSafeWidth()
    {
        var fonts = new FrontEndFont();
        var items = OrigItems(EightPlayerLobby(), fonts);
        var name = TextAt(items, "Friday night");
        var mode = TextAt(items, "single race");
        var players = TextAt(items, "8/8 players");
        Assert.Equal((NetRole.Ink, NetRole.InkSoft, NetRole.InkSoft), (name.Role, mode.Role, players.Role));
        Assert.All(new[] { name, mode, players }, t => Assert.Equal((NetworkMenuLayout.BoardX, NetAlign.Right, 115), (t.X, t.Align, t.X1)));   // S8: stays clear of the shadow of the logo
        Assert.Equal(new[] { 100, 124, 148 }, new[] { name.Y, mode.Y, players.Y });
        // three lines: the actions keep their place
        Assert.Equal(204, BoardCar(items).Y);
        // a long name is cut to that width by the backend
        var longName = OrigItems(WithNames(InGame(out _, NetGamePhase.Lobby, true, 0, "A rather long name of a game", "Me", "Bob")), fonts);
        Assert.Equal(115, TextAt(longName, "A rather long name of a game").X1);
        // a game named like its mode shows the mode only once
        var same = OrigItems(WithNames(InGame(out _, NetGamePhase.Lobby, true, 0, "Single Race", "Me", "Bob")), fonts);
        Assert.Single(same, i => i.Op == NetOp.Text && string.Equals(i.Text, "single race", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(NetRole.Ink, same.First(i => i.Op == NetOp.Text && string.Equals(i.Text, "single race", StringComparison.OrdinalIgnoreCase)).Role);
        Assert.Equal(124, TextAt(same, "2/8 players").Y);
    }

    [GameDataFact]
    public void OriginalLobby_AFourthLineOfTheBoardMovesTheActionsDownToKeepTheirDistance()
    {
        var fonts = new FrontEndFont();
        var knockout = OrigItems(KnockoutLobbyScreen(null), fonts);
        var lines = new[] { "Knockout cup", "knockout", "2/8 players", "round 1/8" }.Select(t => TextAt(knockout, t)).ToArray();
        Assert.Equal(new[] { 100, 124, 148, 172 }, lines.Select(l => l.Y));
        var firstAction = BoardCar(knockout);
        Assert.True(firstAction.Y - lines[3].Y >= 36, $"{firstAction.Y - lines[3].Y} px under the last line");
        // the same name as the mode: three lines again, the actions back at their usual place
        var menu = InGame(out var view, NetGamePhase.Lobby, true, 0, "Knockout", "Me", "Bob");
        view.Edit(l => l with { Mode = NetGameMode.Knockout });
        menu.Update(0);
        var three = OrigItems(WithNames(menu), fonts);
        Assert.Equal(204, BoardCar(three).Y);
        var panels = DataPanels(knockout);
        AssertOrigClean("lobby_knockout_four_lines", knockout, fonts, PanelRect(panels[0]), PanelRect(panels[1]));
    }

    [GameDataFact]
    public void OriginalLobby_TheOperatorSeesHowManyPlayersAreNotReadyUnderRace()
    {
        var fonts = new FrontEndFont();
        var menu = EightPlayerLobby();
        int waiting = menu.NotReadyCount;
        Assert.InRange(waiting, 1, 7);
        var items = OrigItems(menu, fonts, ChatWith(NetworkChat.Visible));
        var hint = TextAt(items, $"{waiting} not ready");
        var race = TextAt(items, "RACE"); var leave = TextAt(items, "leave game");
        Assert.Equal((NetFont.Small, NetRole.InkWarn, NetAlign.Right, NetworkMenuLayout.BoardX), (hint.Size, hint.Role, hint.Align, hint.X));
        // between RACE and leave game, clear of both (the overlap check of AssertOrigClean covers the rectangles)
        Assert.True(race.Y < hint.Y && hint.Y < leave.Y);
        var (_, hy0, _, hy1) = OrigRect(hint, fonts);
        Assert.True(hy0 >= OrigRect(race, fonts).Y1 && hy1 <= OrigRect(leave, fonts).Y0, "the hint fits between RACE and leave game");
        var panels = DataPanels(items);
        AssertOrigClean("lobby_not_ready", items, fonts, PanelRect(panels[0]), PanelRect(panels[1]), PanelRect(panels[2]));
        // a disconnected player and the operator do not count; nobody to wait for: no line; a player has no RACE and no line
        Assert.Equal(0, InGame(out _, NetGamePhase.Lobby, true, 0, "Race", "Me").NotReadyCount);
        Assert.DoesNotContain(OrigItems(InGame(out _, NetGamePhase.Lobby, true, 0, "Race", "Me"), fonts), i => i.Op == NetOp.Text && i.Text!.EndsWith("not ready"));
        Assert.DoesNotContain(OrigItems(EightPlayerLobby(op: false), fonts), i => i.Op == NetOp.Text && i.Text!.EndsWith("not ready"));
        // it is the number behind the warning of ENTER on RACE
        Assert.True(menu.NotAllReady);
        // the hint follows the readiness: when the last player gets ready it goes
        var one = InGame(out var view, NetGamePhase.Lobby, true, 0, "Race", "Me", "Bob");
        view.Edit(l => l with { Members = [.. l.Members.Select(m => m.Operator ? m : m with { Ready = false })] });
        one.Update(1);
        Assert.Contains(OrigItems(one, fonts), i => i.Op == NetOp.Text && i.Text == "1 not ready");
        view.Edit(l => l with { Members = [.. l.Members.Select(m => m with { Ready = true })] });
        one.Update(2);
        Assert.DoesNotContain(OrigItems(one, fonts), i => i.Op == NetOp.Text && i.Text!.EndsWith("not ready"));
    }

    [GameDataFact]
    public void OriginalLobby_SettingsPanelHasEvenPaddingAndTheCountdownListNoBar()
    {
        var fonts = new FrontEndFont();
        var items = OrigItems(EightPlayerLobby(), fonts, ChatWith(NetworkChat.Visible));
        var panels = DataPanels(items);
        var settings = panels[1];
        Assert.Equal(23, SettingsRow(items, "laps").Label.Y - SettingsRow(items, "direction").Label.Y);
        // the rows (13 px caps to the baseline) have the same room above and under them inside the panel (within 2 px)
        int firstTop = SettingsRow(items, "track").Label.Y, lastBottom = SettingsRow(items, "physics").Label.Y + 19;
        Assert.InRange(Math.Abs(firstTop + 5 - settings.Y - (settings.Y1 - (lastBottom + 1))), 0, 3);
        Assert.True(settings.Y1 <= 330 && panels[2].Y > settings.Y1 + 10, "the chat panel stays clear");
        // the countdown players are a list without a cursor: no bar
        var countdown = OrigItems(WithNames(InGame(out _, NetGamePhase.Countdown, true, 3000, "Race", "Me", "Bob")), fonts);
        Assert.DoesNotContain(countdown, i => i.Op == NetOp.RowBar);
    }

    [GameDataFact]
    public void OriginalSummary_TheLegendOfTheStarOnlyWhenARowHasOneAndTheCarIsTheFullNameOnlyWhenItFits()
    {
        var fonts = new FrontEndFont();
        var none = new RaceResults([new RacerResult(1, 0, "MCF1", true, false, 64 * 95, [64 * 31], 64 * 31, 2, "Me", 100 << 16, 0)]);
        var plain = OrigItems(SummaryScreen(out _, none), fonts);
        Assert.DoesNotContain(plain, i => i.Op == NetOp.Text && i.Text == "*");
        Assert.DoesNotContain(plain, i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.EstimatedLegend);
        // an estimated DNF row has no star (the DNF says it)
        var dnf = new RaceResults([new RacerResult(1, 0, "MCF1", true, true, 0, [], null, 1, "Me", 0, 0)]);
        Assert.DoesNotContain(OrigItems(SummaryScreen(out _, dnf), fonts), i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.EstimatedLegend);
        // the full name when it fits the column, else the short code
        var menu = SummaryScreen(out _, SomeResults());
        menu.CarNames = [.. FullNames.Select((n, i) => i == 1 ? "A MODEL WITH A NAME TOO LONG FOR THE COLUMN" : n)];
        var items = OrigItems(menu, fonts);
        Assert.Equal(NetRole.Value, TextAt(items, NetText.Upper(menu.CarName(0))).Role);
        Assert.Equal("FF50", TextAt(items, "FF50").Text);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.StartsWith("A MODEL"));
        // without the names of the game's data the short code, in the summary as in the tables
        var bare = OrigItems(SummaryScreen(out _, SomeResults()), fonts);
        Assert.Equal(NetRole.Value, TextAt(bare, "MCF1").Role);
    }

    [GameDataFact]
    public void OriginalLobby_PlayersTableShowsTheShortCodeOfTheCar()
    {
        var fonts = new FrontEndFont();
        var menu = EightPlayerLobby();
        var items = OrigItems(menu, fonts);
        var me = items.First(i => i.Op == NetOp.Text && i.Size == NetFont.Small && i.X == 372 && i.Y > 40);
        Assert.Equal(NetworkCarNames.Short(menu.Me!.CarType), me.Text);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Size == NetFont.Small && i.X == 372 && i.Text!.Contains(' ') && i.Y > 40);
    }

    [GameDataFact]
    public void OriginalLook_TheMenuBannerHasTheColourOfTheRaceBanner()
    {
        var fonts = new FrontEndFont();
        var menu = Browsing(out var view);
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(3);
        var inMenu = OrigItems(menu, fonts).Single(i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ReconnectHint);
        var list = new NetDrawList();
        new NetworkMenuLayout().BuildRaceOverlay(list, menu, null, 0, 640, fonts);
        var overRace = list.Items.ToArray().Single(i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ReconnectHint);
        Assert.Equal((NetRole.Bad, NetRole.Bad), (inMenu.Role, overRace.Role));
        // classic keeps its orange banner
        var classic = Items(menu).Single(i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ReconnectHint);
        Assert.Equal(NetRole.Warn, classic.Role);
    }

    [GameDataFact]
    public void OriginalForms_AndTheNewScreensAllocateNothingOnceWarm()
    {
        var skin = OriginalSkin()!;
        var longConnect = new NetworkMenuController(Long, Long, 27100);
        longConnect.Key(MenuKey.Down); longConnect.Key(MenuKey.Enter);           // editing a long host: the tail is kept
        var create = CreateScreen(out _);
        var games = Browsing(out var gv, Game(1, "Alpha", NetGamePhase.Lobby), Game(2, "Beta", NetGamePhase.Race, 4, 4));
        gv.Pending = true;
        var summary = WithNames(SummaryScreen(out _, SomeResults()));
        var lobby = NotReadyLobby();
        foreach (var (name, menu) in new[] { ("connect_long", longConnect), ("create", create), ("games", games), ("summary", summary), ("lobby_hint", lobby) })
        {
            var fb = new FrameBuffer(640, 480);
            for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, menu, i * 30, true, skin: skin);   // both phases of the caret
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 8; i++) NetworkMenuRenderer.Draw(fb, menu, i * 15, true, skin: skin);
            long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
            Assert.True(bytes < 64, $"{name}: {bytes} B per frame");
        }
    }

    [GameDataFact]
    public void OriginalCountdown_ShowsTheDigitTheBarsThePlayersTheGameAndTheChat()
    {
        var fonts = new FrontEndFont();
        var menu = WithNames(InGame(out _, NetGamePhase.Countdown, true, 3200, "Friday night", "Me", "Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal"));
        var chat = ChatWith(NetworkChat.Visible);
        var items = OrigItems(menu, fonts, chat);
        var panels = DataPanels(items);
        Assert.Equal(3, panels.Length);
        Assert.True(panels[0].Y1 < panels[1].Y && panels[1].Y1 < panels[2].Y);
        AssertOrigClean("countdown_8", items, fonts, PanelRect(panels[0]), PanelRect(panels[1]), PanelRect(panels[2]));
        Assert.Equal((NetFont.Normal, NetRole.Label), (TextAt(items, "race starts in").Size, TextAt(items, "race starts in").Role));
        var digit = TextAt(items, "4");
        Assert.Equal((NetFont.Digit, NetRole.ValueSelected, 432), (digit.Size, digit.Role, digit.X));
        var bars = items.Where(i => i.Op == NetOp.Swatch).ToArray();
        Assert.Equal(5, bars.Length);
        Assert.Equal(4, bars.Count(b => b.Rgb == OriginalNetBackend.Colour(NetRole.Value)));    // the seconds left are bright
        Assert.Equal((18, 6), (bars[0].X1, bars[0].Y1));
        Assert.Single(bars.Select(b => b.Y).Distinct());
        foreach (var name in new[] { "Me", "Bob", "Carol", "Dave", "Eve", "Frank", "Gina", "Hal" }) Assert.Equal(NetFont.Medium, TextAt(items, name).Size);
        Assert.Equal(NetRole.ValueSelected, TextAt(items, "Me").Role);
        Assert.Equal(NetRole.Ink, TextAt(items, "cancel start").Role);                              // the operator cancels, the others leave
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "RACE");
        Assert.Equal(NetRole.Value, SettingsRow(items, "track").Value.Role);                       // the game, shown not edited
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == "T CHAT   ESC CANCEL" && i.Y == 456);
        Assert.Equal(NetworkChat.Visible, items.Count(i => i.Op == NetOp.Text && i.Text!.StartsWith("line ")));
        // the digit follows the countdown, the last second leaves one bar
        var last = OrigItems(WithNames(InGame(out _, NetGamePhase.Countdown, true, 900, "Race", "Me", "Bob")), fonts);
        Assert.Equal(1, last.Count(i => i.Op == NetOp.Swatch && i.Rgb == OriginalNetBackend.Colour(NetRole.Value)));
        Assert.Equal(NetFont.Digit, TextAt(last, "1").Size);
    }

    [GameDataFact]
    public void OriginalLobby_WaitingForTheServerIsAGameDialog()
    {
        var fonts = new FrontEndFont();
        var menu = InGame(out var view, NetGamePhase.Lobby);
        view.Lobby = null;
        menu.Update(3);
        var items = OrigItems(menu, fonts);
        Assert.Contains(items, i => i.Op == NetOp.Dialog && i.Text2 == "waiting for server...");
        Assert.Contains(items, i => i.Op == NetOp.Text && i.Text == "ESC LEAVE" && i.Y == 456);
    }

    // ---------------- windows and overlays of the original look (change network-menu-look, task 3.5) ----------------

    /// <summary>Rectangle of the dialog window of the game for a head and a body (as <c>MenuScene.DrawDialogFrame</c>). [ref:2851]</summary>
    static (int X0, int Y0, int X1, int Y1) DialogRect(FrontEndFont fonts, string head, string body)
    {
        var font = fonts.Font(18);
        int widest = Math.Max(font.Measure(head) + 0x3C, font.Measure(body)), width = widest + 0x1E;
        return ((640 - width) / 2, 210, (640 - width) / 2 + width, 270);
    }

    /// <summary>Horizontal extent of a text of the list at its anchor.</summary>
    static (int X0, int X1) Extent(FrontEndFont fonts, NetDrawItem t)
    {
        int w = fonts.Font(OriginalNetBackend.FontSize(t.Size)).Measure(t.Text!);
        int x = t.Align == NetAlign.Center ? t.X - w / 2 : t.Align == NetAlign.Right ? t.X - w : t.X;
        return (x, x + w);
    }

    [GameDataFact]
    public void OriginalConfirm_IsTheGameDialogWithYesAndNoDefaultingToNoAndNoCursor()
    {
        var fonts = new FrontEndFont();
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        menu.RequestExit();
        var no = OrigItems(menu, fonts);
        var dialog = Assert.Single(no, i => i.Op == NetOp.Dialog);
        Assert.Equal(("leave the game?", ""), (dialog.Text, dialog.Text2));
        var yes = TextAt(no, "yes"); var nope = TextAt(no, "no");
        Assert.Equal((NetworkMenuLayout.ConfirmYesX, NetworkMenuLayout.ConfirmNoX), (yes.X, nope.X));      // 0x118 and 0x168 (the Yes and No button X positions) [ref:2852]
        Assert.Equal((NetworkMenuLayout.DialogBodyY, NetworkMenuLayout.DialogBodyY), (yes.Y, nope.Y));
        Assert.Equal((NetFont.Normal, NetAlign.Center), (yes.Size, yes.Align));
        Assert.Equal((NetRole.Label, NetRole.Selected), (yes.Role, nope.Role));                            // default: no
        Assert.DoesNotContain(no, i => i.Op == NetOp.Text && i.Text!.Contains('>'));
        // both answers stand inside the window and clear of each other; the window is drawn before them
        var rect = DialogRect(fonts, "leave the game?", "");
        foreach (var t in new[] { yes, nope })
        {
            var (x0, x1) = Extent(fonts, t);
            Assert.True(x0 >= rect.X0 + 8 && x1 <= rect.X1 - 8, $"{t.Text} {x0}..{x1} in {rect}");
        }
        Assert.True(Extent(fonts, yes).X1 + 8 <= Extent(fonts, nope).X0);
        Assert.True(Array.IndexOf(no, dialog) < Array.IndexOf(no, yes));
        foreach (var key in new[] { MenuKey.Left, MenuKey.Right, MenuKey.Up, MenuKey.Down })
        {
            menu.Key(key);
            var now = OrigItems(menu, fonts);
            bool answerYes = menu.ConfirmYes;
            Assert.Equal(answerYes ? NetRole.Selected : NetRole.Label, TextAt(now, "yes").Role);
            Assert.Equal(answerYes ? NetRole.Label : NetRole.Selected, TextAt(now, "no").Role);
        }
    }

    [GameDataFact]
    public void OriginalConfirm_PixelsShowTheLitAnswerInsideTheWindowOverTheBackdropOnly()
    {
        var skin = OriginalSkin()!;
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        var plain = Render(menu, 0, true, skin);
        menu.RequestExit();
        var no = Render(menu, 0, true, skin);
        menu.Key(MenuKey.Left);
        var yes = Render(menu, 0, true, skin);
        var rect = DialogRect(skin.Scene!.Fonts!, "leave the game?", "");
        Assert.False(no.Rgba.AsSpan().SequenceEqual(plain.Rgba));
        // outside the window only the backdrop (the lobby panels would show through the 70% dim of the window) and the footer
        var backdrop = new FrameBuffer(640, 480);
        Assert.True(skin.Scene!.DrawNetworkBackdrop(backdrop));
        for (int y = 0; y < 446; y++)
            for (int x = 0; x < 640; x++)
            {
                if (x >= rect.X0 - 200 && x < rect.X1 + 200 && y >= rect.Y0 - 40 && y < rect.Y1) continue;
                Assert.Equal(backdrop.Get(x, y), no.Get(x, y));
            }
        // the white answer is the chosen one
        static int WhiteIn(FrameBuffer fb, int x0, int x1)
        {
            int n = 0;
            for (int y = 225; y < 262; y++) for (int x = x0; x < x1; x++) if ((fb.Get(x, y) & 0xE0E0E0) == 0xE0E0E0) n++;   // white at full coverage is 0xEFEFEF
            return n;
        }
        Assert.True(WhiteIn(yes, 255, 305) > 30 && WhiteIn(no, 255, 305) == 0, $"{WhiteIn(yes, 255, 305)} {WhiteIn(no, 255, 305)}");
        Assert.True(WhiteIn(no, 340, 385) > 30 && WhiteIn(yes, 340, 385) == 0, $"{WhiteIn(no, 340, 385)} {WhiteIn(yes, 340, 385)}");
    }

    [GameDataFact]
    public void OriginalRaceBox_IsTheGameDialogWithTheGameNameAndTheKeyHintInTheFooter()
    {
        var fonts = new FrontEndFont();
        var menu = InGame(out _, NetGamePhase.Race, true, 0, "Friday night", "Me", "Bob");
        Assert.Equal(NetScreen.Race, menu.Screen);
        var items = OrigItems(menu, fonts);
        var dialog = Assert.Single(items, i => i.Op == NetOp.Dialog);
        Assert.Equal(("race in progress", "Friday night"), (dialog.Text, dialog.Text2));
        var hint = TextAt(items, NetworkMenuLayout.RaceBoxHint);
        Assert.Equal((NetworkMenuLayout.OriginalFooterX, NetworkMenuLayout.OriginalFooterY, NetFont.Small, NetRole.Label),
            (hint.X, hint.Y, hint.Size, hint.Role));
        Assert.DoesNotContain(items, i => i.Op == NetOp.Panel);           // the dialog is the only window
    }

    [GameDataFact]
    public void OriginalPause_IsTheGameDialogWithResumeAndLeaveRaceSideBySide()
    {
        var fonts = new FrontEndFont();
        for (int selection = 0; selection < 2; selection++)
        {
            var list = new NetDrawList();
            new NetworkMenuLayout().BuildPause(list, selection, 640, fonts);
            var items = list.Items.ToArray();
            var dialog = Assert.Single(items, i => i.Op == NetOp.Dialog);
            Assert.Equal(("network paused", ""), (dialog.Text, dialog.Text2));
            var resume = TextAt(items, "resume"); var leave = TextAt(items, "leave race");
            Assert.Equal((NetworkMenuLayout.DialogBodyY, NetworkMenuLayout.DialogBodyY), (resume.Y, leave.Y));
            Assert.Equal(selection == 0 ? NetRole.Selected : NetRole.Label, resume.Role);
            Assert.Equal(selection == 1 ? NetRole.Selected : NetRole.Label, leave.Role);
            Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text!.Contains('>'));
            var rect = DialogRect(fonts, "network paused", "");
            foreach (var t in new[] { resume, leave })
            {
                var (x0, x1) = Extent(fonts, t);
                Assert.True(x0 >= rect.X0 + 8 && x1 <= rect.X1 - 8, $"{t.Text} {x0}..{x1} in {rect}");
            }
            Assert.True(Extent(fonts, resume).X1 + 16 <= Extent(fonts, leave).X0);
        }
    }

    [Fact]
    public void ClassicPause_KeepsThePanelAndTheEntriesOfBefore()
    {
        foreach (var skin in new NetSkin?[] { null, new NetSkin(null, NetMenuLook.Original), new NetSkin(null, NetMenuLook.Classic) })
            for (int selection = 0; selection < 2; selection++)
            {
                var fb = new FrameBuffer(640, 480);
                NetworkMenuRenderer.DrawPause(fb, selection, skin);
                Assert.Equal(0xFFFF00u, FindText(fb, "NETWORK PAUSED", 2));
                // the panel and the texts the host drew itself before the pause moved into the renderer
                var reference = new FrameBuffer(640, 480);
                PopupBox.Panel(reference, 130, 145, 510, 335, 0x101820u, 0x141E28u);
                HudOverlay.DrawText(reference, 225, 175, "NETWORK PAUSED", 0xFFFF00, 2);
                HudOverlay.DrawText(reference, 210, 235, (selection == 0 ? "> " : "  ") + "RESUME", selection == 0 ? 0xFFFF00u : 0xFFFFFFu, 2);
                HudOverlay.DrawText(reference, 210, 275, (selection == 1 ? "> " : "  ") + "LEAVE RACE", selection == 1 ? 0xFFFF00u : 0xFFFFFFu, 2);
                Assert.True(fb.Rgba.AsSpan().SequenceEqual(reference.Rgba));
            }
    }

    [GameDataFact]
    public void OriginalPause_DrawsTheDialogOverThePictureAndLeavesTheRestAlone()
    {
        var skin = OriginalSkin()!;
        var fb = RacePicture();
        NetworkMenuRenderer.DrawPause(fb, 1, skin);
        Assert.Equal(0x506070u, fb.Get(5, 5));
        Assert.Equal(0x506070u, fb.Get(630, 470));
        Assert.NotEqual(0x506070u, fb.Get(320, 240));
        var classic = new FrameBuffer(640, 480);
        NetworkMenuRenderer.DrawPause(classic, 1);
        Assert.False(fb.Rgba.AsSpan().SequenceEqual(classic.Rgba));
    }

    /// <summary>Bounding box of the pixels that differ from <paramref name="back"/> and, when given, from <paramref name="not"/>,
    /// within the rows y0..y1 (exclusive); null when there are none.</summary>
    static (int X0, int Y0, int X1, int Y1)? Bounds(FrameBuffer fb, uint back, int y0, int y1, uint? not = null)
    {
        int minX = 640, minY = 480, maxX = -1, maxY = -1;
        for (int y = y0; y < y1; y++)
            for (int x = 0; x < 640; x++)
            {
                uint c = fb.Get(x, y);
                if (c == back || c == not) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? null : (minX, minY, maxX + 1, maxY + 1);
    }

    static FrameBuffer RacePicture()
    {
        var fb = new FrameBuffer(640, 480);
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++) fb.Set(x, y, 0x506070);
        return fb;
    }

    [GameDataFact]
    public void OriginalRaceChat_PlateIsDimmedWithoutFrameAndTheLinePitchFitsTheFont()
    {
        var fonts = new FrontEndFont();
        var menu = InGame(out _, NetGamePhase.Race, true, others: "Bob");
        var chat = ChatWith(4);
        chat.Add(new ChatLine(NetProtocol.ServerChatName, "Game starts soon"), 0);
        var list = new NetDrawList();
        new NetworkMenuLayout().BuildRaceOverlay(list, menu, chat, 0, 640, fonts);
        var items = list.Items.ToArray();
        var plate = Assert.Single(items, i => i.Op == NetOp.Panel);
        Assert.Equal(NetPanelKind.ChatPlate, plate.Kind);                           // 55% dim, no frame (NetPanel)
        var texts = items.Where(i => i.Op == NetOp.Text).ToArray();
        Assert.All(texts, t => Assert.Equal(NetFont.Small, t.Size));
        Assert.Contains(texts, t => t.Role == NetRole.Header && t.Text == "Bob: ");     // nick in the header colour
        Assert.Contains(texts, t => t.Role == NetRole.Selected && t.Text == "line 0");  // text in the plain colour
        Assert.Contains(texts, t => t.Role == NetRole.Value && t.Text!.StartsWith("SERVER: "));
        var ys = texts.Select(t => t.Y).Distinct().OrderBy(y => y).ToArray();
        Assert.Equal(5, ys.Length);
        for (int i = 1; i < ys.Length; i++) Assert.Equal(NetworkMenuLayout.RaceChatPitch, ys[i] - ys[i - 1]);
        // the pitch holds the capitals and the descenders of the 13 px font (stage 2 found 15 px cramped)
        var font = fonts.Font(13);
        var h = font.Find('H')!.Value; var g = font.Find('g')!.Value;
        int cap = h.Height, descender = g.Top + g.Height - (h.Top + h.Height);
        Assert.True(NetworkMenuLayout.RaceChatPitch >= cap + descender, $"pitch {NetworkMenuLayout.RaceChatPitch} < cap {cap} + descender {descender}");
        // the ink rows of the layout hold for every glyph of the font
        int top = 99, bottom = -99;
        for (char c = '!'; c <= '~'; c++)
            if (font.Find(c) is { } q && q.Height > 0) { top = Math.Min(top, q.Top); bottom = Math.Max(bottom, q.Top + q.Height); }
        Assert.True(top >= NetworkMenuLayout.InkTop && bottom <= NetworkMenuLayout.InkBottom, $"ink {top}..{bottom}");
        // every text stands at least 4 px from every edge of the plate (by the metrics of the font)
        foreach (var t in texts)
        {
            var (x0, x1) = Extent(fonts, t);
            Assert.True(x0 - plate.X >= 4 && plate.X1 - x1 >= 4, $"'{t.Text}' {x0}..{x1} in {plate.X}..{plate.X1}");
            Assert.True(t.Y + top - plate.Y >= 4 && plate.Y1 - (t.Y + bottom) >= 4, $"'{t.Text}' rows");
        }
    }

    [GameDataFact]
    public void OriginalRaceChat_PixelsKeepFourClearRowsAndColumnsAroundTheText()
    {
        var skin = OriginalSkin()!;
        var menu = InGame(out _, NetGamePhase.Race, true, others: "Bob");
        const uint back = 0x506070;
        foreach (bool typing in new[] { false, true })
        {
            var chat = ChatWith(4);
            chat.Add(new ChatLine(NetProtocol.ServerChatName, "Game starts soon"), 0);
            chat.Add(new ChatLine("Masło", "Zażółć gęślą jaźń"), 0);
            if (typing) chat.Apply(new DesktopInput(default, [], false, Typed: ['t', 'h', 'i', 'g', 'y', 'p']), true, _ => true);
            var fb = RacePicture();
            NetworkMenuRenderer.DrawRaceOverlay(fb, menu, chat, 0, skin);
            // the plate: dimmed picture, a flat colour with no frame around it
            uint dim = fb.Get(NetworkMenuLayout.RacePlateX + 1, NetworkMenuLayout.RacePlateY + 1);
            Assert.NotEqual(back, dim);
            var box = Bounds(fb, back, 0, 400)!.Value;
            Assert.Equal((NetworkMenuLayout.RacePlateX, NetworkMenuLayout.RacePlateY), (box.X0, box.Y0));
            Assert.Equal(dim, fb.Get(box.X0, box.Y0 + 20));                                    // the edge is the dimmed fill: no frame
            Assert.Equal(dim, fb.Get(box.X1 - 1, box.Y1 - 1));
            var ink = Bounds(fb, back, box.Y0, box.Y1, dim)!.Value;
            Assert.True(ink.X0 - box.X0 >= 4 && box.X1 - ink.X1 >= 4 && ink.Y0 - box.Y0 >= 4 && box.Y1 - ink.Y1 >= 4, $"plate {box} ink {ink}");
            if (!typing) continue;
            // the line being typed: a full-width bar, the text 4 px or more from its edges
            var bar = Bounds(fb, back, 400, 480)!.Value;
            Assert.Equal((0, NetworkMenuLayout.RaceInputY0, 640, NetworkMenuLayout.RaceInputY1), bar);
            uint barDim = fb.Get(2, NetworkMenuLayout.RaceInputY0 + 1);
            var typed = Bounds(fb, back, bar.Y0, bar.Y1, barDim)!.Value;
            Assert.True(typed.X0 - bar.X0 >= 4 && bar.X1 - typed.X1 >= 4 && typed.Y0 - bar.Y0 >= 4 && bar.Y1 - typed.Y1 >= 4, $"bar {bar} ink {typed}");
        }
    }

    [GameDataFact]
    public void OriginalRaceBanner_IsADimBarAtTheTopWithTheTextInsideIt()
    {
        var fonts = new FrontEndFont();
        var lost = InGame(out var view, NetGamePhase.Race);
        view.Phase = SessionClientPhase.Reconnecting;
        lost.Update(2);
        var list = new NetDrawList();
        new NetworkMenuLayout().BuildRaceOverlay(list, lost, null, 0, 640, fonts);
        var bar = Assert.Single(list.Items.ToArray(), i => i.Op == NetOp.Panel);
        Assert.Equal((NetPanelKind.Banner, 0, 0, 640, NetworkMenuLayout.BannerHeight), (bar.Kind, bar.X, bar.Y, bar.X1, bar.Y1));
        var text = Assert.Single(list.Items.ToArray(), i => i.Op == NetOp.Text);
        Assert.Equal((NetworkMenuLayout.ReconnectHint, NetRole.Bad, NetAlign.Center, 320), (text.Text, text.Role, text.Align, text.X));
        // the pixels: the bar is the top 22 rows, the capitals stand inside it with room above and below
        var skin = OriginalSkin()!;
        const uint back = 0x506070;
        var fb = RacePicture();
        NetworkMenuRenderer.DrawRaceOverlay(fb, lost, null, 0, skin);
        var box = Bounds(fb, back, 0, 480)!.Value;
        Assert.Equal((0, 0, 640, NetworkMenuLayout.BannerHeight), box);
        uint dim = fb.Get(1, 1);
        var ink = Bounds(fb, back, 0, 40, dim)!.Value;
        Assert.True(ink.Y0 >= 2 && NetworkMenuLayout.BannerHeight - ink.Y1 >= 2, $"ink {ink}");
        Assert.True(ink.X0 >= 4 && 640 - ink.X1 >= 4);
        // the other banners are not red
        var cutoff = InGame(out var cv, NetGamePhase.Race);
        cv.Lobby = cv.Lobby! with { CutoffMillis = 9000 };
        cutoff.Update(1_000_000);
        list.Clear();
        new NetworkMenuLayout().BuildRaceOverlay(list, cutoff, null, 0, 640, fonts);
        Assert.Equal(NetRole.Warn, Assert.Single(list.Items.ToArray(), i => i.Op == NetOp.Text).Role);   // drawn as "value" by the original look
    }

    [GameDataFact]
    public void OriginalOverlays_AllocateNothingOnceWarm()
    {
        var skin = OriginalSkin()!;
        var race = InGame(out _, NetGamePhase.Race, true, others: "Bob");
        var chat = ChatWith(4);
        chat.Add(new ChatLine(NetProtocol.ServerChatName, "Game starts soon"), 0);
        chat.Apply(new DesktopInput(default, [], false, Typed: ['t', 'a']), true, _ => true);
        var lost = InGame(out var view, NetGamePhase.Race);
        view.Phase = SessionClientPhase.Reconnecting;
        lost.Update(2);
        var confirm = InGame(out _, NetGamePhase.Race);
        confirm.RequestExit();
        var fb = RacePicture();
        void Frame(int i)
        {
            NetworkMenuRenderer.DrawRaceOverlay(fb, race, chat, 0, skin);
            NetworkMenuRenderer.DrawRaceOverlay(fb, lost, null, 0, skin);
            NetworkMenuRenderer.DrawRaceOverlay(fb, confirm, null, 0, skin);
            NetworkMenuRenderer.DrawPause(fb, i & 1, skin);
        }
        for (int i = 0; i < 4; i++) Frame(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) Frame(i);
        Assert.True((GC.GetAllocatedBytesForCurrentThread() - before) / 8 < 64);
    }

    [Fact]
    public void ClassicRaceOverlay_BannerAndConfirmKeepTheirListWithoutTheOriginalFonts()
    {
        var lost = InGame(out var view, NetGamePhase.Race);
        view.Phase = SessionClientPhase.Reconnecting;
        lost.Update(2);
        var banner = RaceItems(lost);
        Assert.Contains(banner, i => i.Op == NetOp.Panel && i.Kind == NetPanelKind.Banner && i.Y == -2 && i.Y1 == 18);
        Assert.Equal(NetRole.Warn, RoleOf(banner, NetworkMenuLayout.ReconnectHint, 1));
        var confirm = InGame(out _, NetGamePhase.Race);
        confirm.RequestExit();
        var items = RaceItems(confirm);
        Assert.True(Has(items, "LEAVE THE GAME?"));
        Assert.True(Has(items, "> NO"));
    }

    // ---------------- round 2026-10-21 (change network-menu-look, D12) ----------------

    static NetworkMenuController OperatorLobby(bool waiting, string mode = "single")
    {
        // an operator with one connected player that is ready (no hint) or not (the hint under RACE); knockout adds a fourth board line
        var menu = InGame(out var view, NetGamePhase.Lobby, true, 0, mode == "single" ? "Friday night" : "Knockout cup", "Me", waiting ? new[] { "Bob", "Carol", "Dave", "Eve" } : new[] { "Bob" });
        if (mode == "knockout") view.Edit(l => l with { Mode = NetGameMode.Knockout, Skill = 0, Style = 0, Series = null });
        menu.Update(0);
        Assert.Equal(waiting, menu.NotAllReady);
        return WithNames(menu);
    }

    [GameDataTheory]
    [InlineData("single", false)]
    [InlineData("single", true)]
    [InlineData("knockout", false)]
    [InlineData("knockout", true)]
    public void OriginalLobby_TheOperatorsLeaveGameMovesDownForTheHintOnlyWhenTheHintIsShown(string mode, bool waiting)
    {
        var fonts = new FrontEndFont();
        var items = OrigItems(OperatorLobby(waiting, mode), fonts);
        var race = TextAt(items, "RACE");
        var leave = TextAt(items, "leave game");
        var hints = items.Where(i => i.Op == NetOp.Text && i.Text!.EndsWith(" not ready")).ToArray();
        if (waiting)
        {
            var hint = Assert.Single(hints);
            Assert.Equal(48, hint.Y - race.Y);
            Assert.Equal(30, leave.Y - hint.Y);                      // the hint has its own line, leave game under it
        }
        else
        {
            Assert.Empty(hints);
            Assert.Equal(54, leave.Y - race.Y);                      // close under the big RACE, no empty line
            Assert.True(leave.Y - race.Y < 60);
        }
        // the same distance to the last line above the actions for the knockout (four lines) and the single race (three)
        Assert.Equal(mode == "knockout" ? 214 : 204, BoardCar(items).Y);
        var panels = DataPanels(items);
        AssertOrigClean("lobby_leave_" + mode + waiting, items, fonts, PanelRect(panels[0]), PanelRect(panels[1]));
    }

    [GameDataFact]
    public void OriginalLobby_ThePlayersLeaveGameStaysOnTheEvenPitchUnderReady()
    {
        var items = OrigItems(WithNames(InGame(out _, NetGamePhase.Lobby, false, others: ["Bob"])), new FrontEndFont());
        Assert.Equal(36, TextAt(items, "leave game").Y - TextAt(items, "ready : no").Y);
    }

    [GameDataFact]
    public void OriginalLobby_TheCarLineOfTheBoardNamesTheModelAndKeepsTheCursorByColour()
    {
        var fonts = new FrontEndFont();
        var menu = OperatorLobby(false);
        var items = OrigItems(menu, fonts);
        var car = BoardCar(items);
        Assert.Equal("car : " + NetworkCarNames.Short(menu.Me!.CarType).ToLowerInvariant(), car.Text);
        Assert.Equal((NetFont.Normal, NetAlign.Right, NetworkMenuLayout.BoardX), (car.Size, car.Align, car.X));
        Assert.True(car.X1 > 0 && car.X1 <= 115);                    // within the logo-safe width of the board
        Assert.Equal(NetRole.Ink, car.Role);
        for (int i = 0; i < 6 && menu.LobbyRows[menu.Selected] != LobbyRow.Car; i++) menu.Key(MenuKey.Up);
        Assert.Equal(NetRole.InkSelected, BoardCar(OrigItems(menu, fonts)).Role);
        Assert.DoesNotContain(items, i => i.Op == NetOp.Text && i.Text == "car" && i.Align == NetAlign.Right);
    }

    [Fact]
    public void ClassicLobby_TheCarRowNamesTheModel()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob"]);
        string code = NetworkCarNames.Short(menu.Me!.CarType);
        Assert.True(Has(Items(menu), "CAR: " + code));
        Assert.DoesNotContain(Items(menu), i => IsText(i, 2) && i.Text == "CAR");
    }

    [GameDataFact]
    public void OriginalLobby_APlayerSeesWhoSetTheGameUpInsideTheOptionsPanel()
    {
        var fonts = new FrontEndFont();
        var menu = InGame(out var view, NetGamePhase.Lobby, false, 0, "Race", "Me", "Bob");
        view.Edit(l => l with { Members = [.. l.Members.Select(m => m.Operator ? m with { Name = "Miłosz" } : m)] });
        menu.Update(1);
        var chat = new NetworkChat();
        var items = OrigItems(WithNames(menu), fonts, chat);
        var line = TextAt(items, "set by Milosz");                      // through NetText.Safe: no "?" and no accent the font lacks
        Assert.Equal((NetFont.Small, NetRole.Disabled, NetAlign.Left), (line.Size, line.Role, line.Align));
        var panels = DataPanels(items);
        Assert.Equal(3, panels.Length);
        Assert.True(Inside(OrigRect(line, fonts), panels[1].X, panels[1].Y, panels[1].X1, panels[1].Y1), "inside the options panel");
        AssertOrigClean("lobby_player_setby", items, fonts, PanelRect(panels[0]), PanelRect(panels[1]), PanelRect(panels[2]));
        // the rows of the options stay clear of it
        var last = SettingsRow(items, "physics").Value;
        Assert.True(OrigRect(last, fonts).Y1 <= OrigRect(line, fonts).Y0);
        // an operator (the settings are his) and the countdown do not carry the line
        Assert.DoesNotContain(OrigItems(OperatorLobby(false), fonts, chat), i => i.Op == NetOp.Text && i.Text!.StartsWith("set by"));
        var counting = InGame(out _, NetGamePhase.Countdown, false, 3000, "Race", "Me", "Bob");
        Assert.DoesNotContain(OrigItems(counting, fonts, chat), i => i.Op == NetOp.Text && i.Text!.StartsWith("set by"));
    }

    [Fact]
    public void ClassicLobby_APlayerSeesWhoSetTheGameUp()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, false, others: ["Bob"]);
        Assert.True(Has(Items(menu), "SET BY OPERATOR", 1));
        Assert.False(Has(Items(InGame(out _, NetGamePhase.Lobby, true, others: ["Bob"])), "SET BY", 1));
    }

    [GameDataFact]
    public void OriginalChat_AnEmptyPanelCarriesAFaintHintHowToOpenIt()
    {
        var fonts = new FrontEndFont();
        foreach (var menu in new[] { OperatorLobby(false), InGame(out _, NetGamePhase.Countdown, true, 3000, "Race", "Me", "Bob"),
                     SummaryScreen(out _, SomeResults()) })
        {
            var empty = new NetworkChat();
            var items = OrigItems(menu, fonts, empty);
            var hint = TextAt(items, NetworkMenuLayout.ChatPlaceholder);
            Assert.Equal("press T to chat", hint.Text);
            Assert.Equal((NetFont.Small, NetRole.Disabled, NetAlign.Left), (hint.Size, hint.Role, hint.Align));
            var panels = DataPanels(items).Select(PanelRect).ToArray();
            Assert.Contains(panels, p => Inside(OrigRect(hint, fonts), p.X0, p.Y0, p.X1, p.Y1));
            AssertOrigClean("chat_empty_" + menu.Screen, items, fonts, panels);
            // gone as soon as there is a line, and while the line is typed
            Assert.DoesNotContain(OrigItems(menu, fonts, ChatWith(1)), i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ChatPlaceholder);
            var typing = new NetworkChat();
            typing.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
            Assert.DoesNotContain(OrigItems(menu, fonts, typing), i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ChatPlaceholder);
            // no chat at all (a screen without the panel): no hint either
            Assert.DoesNotContain(OrigItems(menu, fonts), i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ChatPlaceholder);
        }
    }

    [Fact]
    public void ClassicChat_AnEmptyChatCarriesTheHint_AndAServerLineIsDrawnAsAWarning()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob"]);
        Assert.True(Has(Items(menu, new NetworkChat()), "PRESS T TO CHAT", 1));
        Assert.False(Has(Items(menu, ChatWith(1)), "PRESS T TO CHAT", 1));
        var chat = new NetworkChat();
        chat.Add(new ChatLine(NetProtocol.ServerChatName, "Bob joined"), 0);
        Assert.Equal(NetRole.Warn, RoleOf(Items(menu, chat), "SERVER: BOB JOINED", 1));
    }

    [GameDataFact]
    public void OriginalSummary_ShowsTheTimeLeftUnderTheActionsAndItFollowsTheClock()
    {
        var fonts = new FrontEndFont();
        var menu = SummaryScreen(out _, SomeResults());
        var first = TextAt(OrigItems(menu, fonts, new NetworkChat()), "back to lobby in 30");
        Assert.Equal((NetFont.Small, NetRole.InkSoft, NetAlign.Right, NetworkMenuLayout.BoardX, NetworkMenuLayout.SummaryTimerY),
            (first.Size, first.Role, first.Align, first.X, first.Y));
        Assert.True(first.Y > TextAt(OrigItems(menu, fonts), "exit").Y);
        menu.Update(5_000_001);
        var items = OrigItems(menu, fonts, new NetworkChat());
        Assert.Equal(NetworkMenuLayout.SummaryTimerY, TextAt(items, "back to lobby in 25").Y);
        var panels = DataPanels(items);
        AssertOrigClean("summary_timer", items, fonts, panels.Select(PanelRect).ToArray());
        // the actions are still there and the timer is not a stop of the cursor
        Assert.Equal(NetRole.InkSelected, TextAt(items, "return to lobby").Role);
        menu.Key(MenuKey.Enter);                                    // back in the lobby: no timer
        Assert.DoesNotContain(OrigItems(menu, fonts), i => i.Op == NetOp.Text && i.Text!.StartsWith("back to lobby in"));
    }

    [Fact]
    public void ClassicSummary_ShowsTheTimeLeft()
    {
        var menu = SummaryScreen(out _, SomeResults());
        Assert.True(Has(Items(menu), "BACK TO LOBBY IN 30", 1));
        menu.Update(12_000_001);
        Assert.True(Has(Items(menu), "BACK TO LOBBY IN 18", 1));
    }

    [Fact]
    public void ReconnectBanner_ShowsTheSecondsLeftWhenTheSessionSaysThem_AndTheOldTextOtherwise()
    {
        var menu = InGame(out var view, NetGamePhase.Lobby, true, others: "Bob");
        view.Phase = SessionClientPhase.Reconnecting;
        menu.Update(2);
        Assert.True(Has(Items(menu), NetworkMenuLayout.ReconnectHint, 1));            // the figure is not known: as before
        view.ReconnectRemainingMicros = 21_400_000;
        Assert.True(Has(Items(menu), "CONNECTION LOST - RECONNECTING... 22 S LEFT   ESC GIVE UP", 1));
        view.ReconnectRemainingMicros = 20_000_000;
        Assert.True(Has(Items(menu), "RECONNECTING... 20 S LEFT", 1));
        // over the race picture too
        var race = InGame(out var rv, NetGamePhase.Race, true, others: "Bob");
        rv.Phase = SessionClientPhase.Reconnecting;
        rv.ReconnectRemainingMicros = 5_000_001;
        race.Update(2);
        Assert.Contains(RaceItems(race), i => i.Op == NetOp.Text && i.Text == "CONNECTION LOST - RECONNECTING... 6 S LEFT   ESC GIVE UP");
    }

    [GameDataFact]
    public void OriginalReconnectBanner_SaysTheSecondsLeftInTheFooterBar()
    {
        var fonts = new FrontEndFont();
        var menu = InGame(out var view, NetGamePhase.Lobby, true, others: "Bob");
        view.Phase = SessionClientPhase.Reconnecting;
        view.ReconnectRemainingMicros = 12_000_000;
        menu.Update(2);
        var items = OrigItems(menu, fonts);
        var text = TextAt(items, "CONNECTION LOST - RECONNECTING... 12 S LEFT   ESC GIVE UP");
        Assert.Equal((NetRole.Bad, NetworkMenuLayout.OriginalFooterY), (text.Role, text.Y));
        Assert.True(OrigRect(text, fonts).X0 >= 0 && OrigRect(text, fonts).X1 <= 640);
    }

    [Fact]
    public void NewTexts_AllocateNothingOnceWarm_InBothLooks()
    {
        var skin = OriginalSkin();
        var names = new[] { "x_lobby_chat_empty", "x_lobby_player_setby", "x_lobby_server_lines", "x_summary_timer", "x_reconnecting_seconds" };
        foreach (var scene in ExtraScenes().Where(s => names.Contains(s.Name)))
            foreach (var look in new NetSkin?[] { null, skin })
            {
                var fb = new FrameBuffer(640, 480);
                for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, scene.Menu, i, true, scene.Chat, skin: look);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 8; i++) NetworkMenuRenderer.Draw(fb, scene.Menu, 4 + i, true, scene.Chat, skin: look);
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 8;
                Assert.True(bytes < 64, $"{scene.Name} ({(look == null ? "classic" : "original")}): {bytes} B per frame");
            }
    }

    [Fact]
    public void TextCachesFilledOnAnotherThread_LeaveAWarmFrameAllocationFree()
    {
        var scene = ExtraScenes().First(s => s.Name == "x_summary_timer");
        var fb = new FrameBuffer(640, 480);
        for (int i = 0; i < 4; i++) NetworkMenuRenderer.Draw(fb, scene.Menu, i, true, scene.Chat);
        // another thread (a parallel test) types and counts past the size at which a cache drops itself
        var other = new Thread(() =>
        {
            for (int i = 0; i < 10_000; i++) { NetText.Time(i); NetText.Int(1000 + i); NetText.Join("x", NetText.Int(1000 + i)); NetText.Upper("a" + i); }
        });
        other.Start();
        other.Join();
        long before = GC.GetAllocatedBytesForCurrentThread();
        NetworkMenuRenderer.Draw(fb, scene.Menu, 4, true, scene.Chat);           // the very next frame: no text made again
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 64, $"{bytes} B");
    }

    // ---------------- chat font (task 26: special characters in the chat) ----------------

    const string ChatSpecials = ")?!:(@";

    static NetworkChat ChatSaying(string text, params string[] more)
    {
        var chat = new NetworkChat();
        chat.Add(new ChatLine("Bob", text), 0);
        foreach (string t in more) chat.Add(new ChatLine("Eve", t), 0);
        return chat;
    }

    static NetworkChat ChatTyping(string text)
    {
        var chat = new NetworkChat();
        chat.Apply(new DesktopInput(default, [], false, Typed: ['t']), true, _ => true);
        foreach (char c in text) chat.Apply(new DesktopInput(default, [], false, Typed: [c]), true, _ => true);
        Assert.True(chat.Typing);
        return chat;
    }

    static bool Differs(FrameBuffer a, FrameBuffer b) => !a.Rgba.AsSpan().SequenceEqual(b.Rgba);

    [Fact]
    public void ChatFont_EveryPrintableAsciiCharacterHasAGlyphWithPixels()
    {
        var font = ChatFont.Shared;
        if (font == null)
        {
            // a machine without any of the known system fonts: nothing to rasterize, the chat keeps the menu font
            Assert.DoesNotContain(ChatFont.Candidates(), File.Exists);
            return;
        }
        var missing = new List<char>();
        for (char c = ' '; c <= '~'; c++)
        {
            Assert.True(font.Advance(c) > 0, $"advance of '{c}'");
            if (c != ' ' && font.InkPixels(c) == 0) missing.Add(c);
        }
        Assert.Empty(missing);
        Assert.Equal(0, font.InkPixels(' '));
        Assert.Equal(0, font.Advance('ą'));                      // outside ASCII: not drawn, no width
        Assert.True(font.Measure("A?") > font.Measure("A"));
        Assert.True(font.CapHeight is >= 6 and <= 10, $"cap height {font.CapHeight}");
    }

    [Fact]
    public void ChatFont_LoadSkipsMissingAndBrokenFilesAndFindsNothingWithoutAFont()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nfs2se_chatfont_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string junk = Path.Combine(dir, "junk.ttf");
            File.WriteAllBytes(junk, new byte[200]);
            Assert.Null(ChatFont.Load([Path.Combine(dir, "none.ttf"), junk]));
            Assert.Null(ChatFont.Load([]));
            if (ChatFont.Shared is { } shared)
                Assert.Equal(shared.Path, ChatFont.Load([Path.Combine(dir, "none.ttf"), junk, shared.Path])!.Path);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChatFont_FitCutsALongTextToTheRoomWithDots()
    {
        var font = ChatFont.Shared;
        if (font == null) return;
        string text = new('W', 60);
        Assert.Equal("hi", font.Fit("hi", 200));
        string fit = font.Fit(text, 100);
        Assert.True(font.Measure(fit) <= 100 && fit.EndsWith("..."), fit);
        string tail = font.Fit("abc " + new string('W', 40) + "XYZ", 100, tail: true);
        Assert.True(font.Measure(tail) <= 100 && tail.StartsWith("...") && tail.EndsWith("XYZ"), tail);
        Assert.Equal("", font.Fit(text, 3));
    }

    [Fact]
    public void Chat_TextsAreMarkedAsChatAndOtherTextsAreNot()
    {
        var items = Items(InGame(out _, NetGamePhase.Lobby, true, others: "Bob"), ChatTyping("hi"));
        Assert.True(items.Single(i => i.Op == NetOp.Text && i.Text == NetworkMenuLayout.ChatHint).Chat);
        Assert.True(items.Single(i => i.Op == NetOp.Text && i.Text!.StartsWith("> hi")).Chat);
        Assert.False(items.Single(i => i.Op == NetOp.Text && i.Text == "  SETTINGS").Chat);
        items = Items(InGame(out _, NetGamePhase.Lobby, true, others: "Bob"), ChatSaying("hello " + ChatSpecials));
        Assert.True(items.Single(i => i.Op == NetOp.Text && i.Text == "BOB: HELLO " + ChatSpecials).Chat);
        Assert.All(items.Where(i => i.Op == NetOp.Text && !i.Text!.StartsWith("BOB:")), i => Assert.False(i.Chat, i.Text));
        var race = RaceItems(InGame(out _, NetGamePhase.Race), ChatSaying("race " + ChatSpecials), 0);
        Assert.Contains(race, i => i.Op == NetOp.Text && i.Chat && i.Text == "BOB: RACE " + ChatSpecials);
    }

    [Fact]
    public void ClassicChat_EverySpecialCharacterIsDrawn_AndTheFallbackFontStillWorks()
    {
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        foreach (bool font in new[] { true, false })
        {
            using var scope = font ? null : ChatFont.Override(null);
            // the chat font (or the bitmap fallback) has to show what these characters add over a blank
            var blank = Render(menu, ChatSaying("a   b"));
            Assert.True(Painted(blank) > 0);
            // the bitmap font of the fallback has no '@' (that is what the chat font is for)
            foreach (char c in font ? ChatSpecials : ChatSpecials.Replace("@", ""))
                Assert.True(Differs(blank, Render(menu, ChatSaying($"a {c} b"))), $"'{c}' font={font}");
        }
    }

    [Fact]
    public void ClassicChat_RaceOverlayAndInputLineDrawTheSpecialCharacters()
    {
        var menu = InGame(out _, NetGamePhase.Race);
        foreach (bool typing in new[] { false, true })
            foreach (char c in ChatSpecials)
            {
                var plain = new FrameBuffer(640, 480);
                NetworkMenuRenderer.DrawRaceOverlay(plain, menu, typing ? ChatTyping("a   b") : ChatSaying("a   b"), 0);
                var with = new FrameBuffer(640, 480);
                NetworkMenuRenderer.DrawRaceOverlay(with, menu, typing ? ChatTyping($"a {c} b") : ChatSaying($"a {c} b"), 0);
                Assert.True(Differs(plain, with), $"'{c}' typing={typing}");
            }
    }

    [Fact]
    public void OriginalChat_EverySpecialCharacterIsDrawnWithTheChatFont()
    {
        var skin = OriginalSkin();
        if (skin == null) return;                                       // game data not installed: the original look is not available
        var lobby = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        var race = InGame(out _, NetGamePhase.Race);
        foreach (char c in ChatSpecials)
        {
            var blank = new FrameBuffer(640, 480);
            NetworkMenuRenderer.Draw(blank, lobby, 0, true, ChatSaying("a   b"), skin: skin);
            var with = new FrameBuffer(640, 480);
            NetworkMenuRenderer.Draw(with, lobby, 0, true, ChatSaying($"a {c} b"), skin: skin);
            Assert.True(Differs(blank, with), $"lobby '{c}'");
            blank = new FrameBuffer(640, 480);
            NetworkMenuRenderer.DrawRaceOverlay(blank, race, ChatSaying("a   b"), 0, skin);
            with = new FrameBuffer(640, 480);
            NetworkMenuRenderer.DrawRaceOverlay(with, race, ChatSaying($"a {c} b"), 0, skin);
            Assert.True(Differs(blank, with), $"race '{c}'");
        }
    }

    [Fact]
    public void Chat_LongLinesStayInsideTheBufferInBothLooks()
    {
        string wide = new('W', 100);
        var race = InGame(out _, NetGamePhase.Race);
        foreach (var skin in new[] { null, OriginalSkin() }.Distinct())
        {
            var fb = new FrameBuffer(640, 480);
            NetworkMenuRenderer.DrawRaceOverlay(fb, race, ChatSaying(wide, wide), 0, skin);
            for (int y = 20; y < 70; y++)
                for (int x = 636; x < 640; x++) Assert.Equal(0u, fb.Get(x, y));
            Assert.True(Painted(fb) > 0);
        }
    }

    [Fact]
    public void Chat_OtherMenuTextKeepsItsFontWhateverTheChatFont()
    {
        // the chat font only changes chat items: the rest of the screen is the same with and without it
        var menu = InGame(out _, NetGamePhase.Lobby, true, others: "Bob");
        var with = Render(menu, new NetworkChat());
        FrameBuffer without;
        using (ChatFont.Override(null)) without = Render(menu, new NetworkChat());
        Assert.False(Differs(with, without));
    }

    [Fact]
    public void ChatWithSpecialCharacters_CanBeWrittenAsPng()
    {
        string? env = Environment.GetEnvironmentVariable("NFS_NETMENU_PNG_DIR");
        string dir = env is { Length: > 0 } ? env : Path.Combine(Path.GetTempPath(), "nfs2se_chat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var chat = ChatSaying("Special: " + ChatSpecials + " ok? (yes) #1 100% a+b=c [x] {y} <z> ~_^ \\ / | ; , . ' \"", "Hello! How are you?");
            var lobby = InGame(out _, NetGamePhase.Lobby, true, others: ["Bob", "Carol"]);
            var typing = ChatTyping("Typing (a)? @ !");
            var race = InGame(out _, NetGamePhase.Race);
            var skin = OriginalSkin();
            foreach (var (name, s) in new[] { ("chat_classic", (NetSkin?)null), ("chat_original", skin) })
            {
                if (s == null && name == "chat_original") continue;
                var fb = new FrameBuffer(640, 480);
                NetworkMenuRenderer.Draw(fb, lobby, 0, true, chat, skin: s);
                fb.SavePng(Path.Combine(dir, name + "_lobby.png"));
                fb = new FrameBuffer(640, 480);
                NetworkMenuRenderer.Draw(fb, lobby, 0, true, typing, skin: s);
                fb.SavePng(Path.Combine(dir, name + "_lobby_typing.png"));
                fb = new FrameBuffer(640, 480);
                fb.Clear(60, 90, 60);
                NetworkMenuRenderer.DrawRaceOverlay(fb, race, chat, 0, s);
                fb.SavePng(Path.Combine(dir, name + "_race.png"));
                fb = new FrameBuffer(640, 480);
                fb.Clear(60, 90, 60);
                NetworkMenuRenderer.DrawRaceOverlay(fb, race, typing, 0, s);
                fb.SavePng(Path.Combine(dir, name + "_race_typing.png"));
            }
            Assert.True(File.ReadAllBytes(Path.Combine(dir, "chat_classic_lobby.png")).Length > 200);
        }
        finally { if (env is not { Length: > 0 }) Directory.Delete(dir, true); }
    }

    [Fact]
    public void VersionRejections_FitTheMessageLine_InBothLooks()
    {
        // long values on purpose: the original look cuts the message bar at 380 px with "...", the classic one runs from x 60 to 580
        var rejects = new[]
        {
            new NetReject(NetRejectReason.ProtocolMismatch, ServerVersion: 65535),
            new NetReject(NetRejectReason.AppVersionMismatch, ServerAppVersion: new Version(10, 10, 10, 10)),
            new NetReject(NetRejectReason.PhysicsMismatch, ServerPhysics: 65535),
            new NetReject(NetRejectReason.DataMismatch),
        };
        var fonts = OriginalFonts();
        foreach (var r in rejects)
        {
            string text = NetText.Safe(NetworkMenuController.DescribeReject(r));
            Assert.InRange(HudOverlay.TextWidth(text, NetFont.ClassicScale(NetFont.Small)), 1, 520);
            if (fonts != null) Assert.InRange(fonts.Font(OriginalNetBackend.FontSize(NetFont.Small)).Measure(text), 1, 380);
        }
    }
}
