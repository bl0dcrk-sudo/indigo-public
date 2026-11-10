using NFS.Render;

namespace NFS.Desktop;

/// <summary>Platform side of "All files access" (Android 11+ MANAGE_EXTERNAL_STORAGE / older READ_EXTERNAL_STORAGE).</summary>
public interface IStorageAccess
{
    bool HasAccess { get; }
    /// <summary>Opens the system permission screen (or dialog); the user returns to the app afterwards.</summary>
    void Request();
}

/// <summary>"Using data dir" screen and folder picker, drawn into a 640×480 <see cref="FrameBuffer"/> and driven by taps in
/// those coordinates (the host maps touch / mouse positions). No window, no platform types: first run with no valid saved
/// folder goes straight to the browser; later runs show "USING DATA DIR: path" with [OK] / [CHOOSE ANOTHER].
/// The choice is stored in <see cref="PortSettings.GameDir"/> (port.json).</summary>
public sealed class DataDirScreen
{
    public enum Mode { NeedAccess, Confirm, Browse }
    public enum Outcome { None, Accepted, Quit }

    public const int W = 640, H = 480, RowH = 40, ListTop = 84, Rows = 8, ListLeft = 4, ListRight = 572;
    public static readonly (int X, int Y, int W, int H) OkButton = (60, 340, 240, 90), AnotherButton = (340, 340, 240, 90);
    public static readonly (int X, int Y, int W, int H) SelectButton = (4, 416, 400, 58), CancelButton = (412, 416, 224, 58);
    public static readonly (int X, int Y, int W, int H) ScrollUp = (580, ListTop, 56, 150), ScrollDown = (580, ListTop + RowH * Rows - 150, 56, 150);
    public static readonly (int X, int Y, int W, int H) GrantButton = (120, 300, 400, 90);

    readonly PortSettings _settings;
    readonly string _configPath;
    readonly IReadOnlyList<string> _roots;
    readonly IStorageAccess? _access;
    DirBrowser? _browser;
    int _scroll;

    public Mode Current { get; private set; }
    public Outcome Result { get; private set; }
    /// <summary>The game directory (holds GAMEDATA / FEDATA) once <see cref="Result"/> is Accepted.</summary>
    public string? ChosenDir { get; private set; }
    /// <summary>Saved (or default) directory shown on the confirm screen; null when none is valid.</summary>
    public string? KnownDir { get; private set; }
    public DirBrowser? Browser => _browser;
    public int Scroll => _scroll;

    /// <param name="settings">Port settings holding the saved <c>gameDir</c>.</param>
    /// <param name="configPath">Where <paramref name="settings"/> is saved on accept.</param>
    /// <param name="roots">Browser start points (<see cref="DataDirResolver.DiscoverRoots"/>).</param>
    /// <param name="defaultDir">Used when nothing valid is saved and it is a game dir (the app files dir after an adb push).</param>
    /// <param name="access">Storage permission; null = always granted (desktop).</param>
    public DataDirScreen(PortSettings settings, string configPath, IReadOnlyList<string> roots, string? defaultDir = null, IStorageAccess? access = null)
    {
        _settings = settings; _configPath = configPath; _roots = roots; _access = access;
        KnownDir = DataDirResolver.Resolve(settings.GameDir) ?? DataDirResolver.Resolve(defaultDir);
        Begin();
    }

    void Begin()
    {
        if (_access is { HasAccess: false } && KnownDir == null) { Current = Mode.NeedAccess; return; }
        if (KnownDir != null) Current = Mode.Confirm; else StartBrowse();
    }

    void StartBrowse()
    {
        _browser = new DirBrowser(_roots);
        _scroll = 0;
        Current = Mode.Browse;
    }

    /// <summary>Per frame: leaves the permission screen as soon as the system grants access.</summary>
    public void Update()
    {
        if (Current == Mode.NeedAccess && _access is { HasAccess: true }) { StartBrowse(); }
    }

    int Count => _browser == null ? 0 : _browser.Entries.Count + (_browser.Current != null ? 1 : 0);

    static bool In((int X, int Y, int W, int H) r, int x, int y) => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    /// <summary>Android Back: browser one level up (at the start points: cancel), confirm / permission screens quit.</summary>
    public void Back()
    {
        if (Result != Outcome.None) return;
        if (Current == Mode.Browse)
        {
            if (_browser!.Up()) { _scroll = 0; return; }
            Cancel();
        }
        else Result = Outcome.Quit;
    }

    void Cancel()
    {
        if (KnownDir != null) Current = Mode.Confirm; else Result = Outcome.Quit;
    }

    void Accept(string dir)
    {
        _settings.GameDir = dir;
        _settings.Save(_configPath);
        ChosenDir = dir;
        Result = Outcome.Accepted;
    }

    public void Tap(int x, int y)
    {
        if (Result != Outcome.None) return;
        switch (Current)
        {
            case Mode.NeedAccess:
                if (In(GrantButton, x, y)) _access?.Request();
                break;
            case Mode.Confirm:
                if (In(OkButton, x, y)) Accept(KnownDir!);
                else if (In(AnotherButton, x, y)) StartBrowse();
                break;
            case Mode.Browse:
                TapBrowse(x, y);
                break;
        }
    }

    void TapBrowse(int x, int y)
    {
        var b = _browser!;
        if (In(SelectButton, x, y)) { if (b.ResolvedHere is { } dir) Accept(dir); return; }
        if (In(CancelButton, x, y)) { Cancel(); return; }
        if (In(ScrollUp, x, y)) { _scroll = Math.Max(0, _scroll - Rows + 1); return; }
        if (In(ScrollDown, x, y)) { _scroll = Math.Min(Math.Max(0, Count - Rows), _scroll + Rows - 1); return; }
        if (x >= ListLeft && x < ListRight && y >= ListTop && y < ListTop + RowH * Rows)
        {
            int row = _scroll + (y - ListTop) / RowH;
            if (row >= Count) return;
            if (b.Current != null)
            {
                if (row == 0) { b.Up(); _scroll = 0; return; }
                row--;
            }
            b.Enter(row);
            _scroll = 0;
        }
    }

    // ---- drawing ----

    const uint Bg = 0x101820, Panel = 0x1C2A38, Edge = 0x6080A0, White = 0xFFFFFF, Yellow = 0xFFE040, Green = 0x40E060, Red = 0xFF6050, Grey = 0x708090;

    static void Fill(FrameBuffer fb, int x, int y, int w, int h, uint rgb)
    {
        for (int yy = Math.Max(0, y); yy < Math.Min(fb.Height, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(fb.Width, x + w); xx++) fb.Set(xx, yy, rgb);
    }

    static void Box(FrameBuffer fb, (int X, int Y, int W, int H) r, uint fill, uint edge)
    {
        Fill(fb, r.X, r.Y, r.W, r.H, edge);
        Fill(fb, r.X + 3, r.Y + 3, r.W - 6, r.H - 6, fill);
    }

    static void Centered(FrameBuffer fb, (int X, int Y, int W, int H) r, string text, uint colour, int scale)
    {
        int tw = HudOverlay.TextWidth(text, scale);
        HudOverlay.DrawText(fb, r.X + (r.W - tw) / 2, r.Y + (r.H - 5 * scale) / 2, text, colour, scale);
    }

    static void Triangle(FrameBuffer fb, (int X, int Y, int W, int H) r, bool up, uint rgb)
    {
        int h = 36, cx = r.X + r.W / 2, top = r.Y + (r.H - h) / 2;
        for (int i = 0; i < h; i++)
        {
            int half = up ? i * 20 / h : (h - 1 - i) * 20 / h;
            Fill(fb, cx - half, top + i, half * 2 + 1, 1, rgb);
        }
    }

    static string Fit(string text, int maxChars, bool keepEnd)
    {
        if (text.Length <= maxChars) return text;
        return keepEnd ? "..." + text[^(maxChars - 3)..] : text[..(maxChars - 3)] + "...";
    }

    /// <summary>Wraps a path to lines of at most <paramref name="width"/> characters, breaking after '/'.</summary>
    public static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var part in text.Replace('\\', '/').Split('/'))
        {
            var piece = (cur.Length == 0 ? "" : "/") + part;
            if (cur.Length + piece.Length > width && cur.Length > 0) { lines.Add(cur + "/"); cur = part; }
            else cur += piece;
            while (cur.Length > width) { lines.Add(cur[..width]); cur = cur[width..]; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    public void Render(FrameBuffer fb)
    {
        fb.Clear(0x10, 0x18, 0x20);
        Fill(fb, 0, 0, W, 40, Panel);
        HudOverlay.DrawText(fb, 12, 12, "NFS2SE GAME DATA", Yellow, 3);
        switch (Current)
        {
            case Mode.NeedAccess:
                HudOverlay.DrawText(fb, 24, 90, "ALL FILES ACCESS IS NEEDED", White, 3);
                HudOverlay.DrawText(fb, 24, 140, "TO READ THE GAME FOLDER (GAMEDATA,", Grey, 2);
                HudOverlay.DrawText(fb, 24, 164, "FEDATA) FROM THE PHONE OR SD CARD.", Grey, 2);
                HudOverlay.DrawText(fb, 24, 200, "TAP THE BUTTON, ALLOW ACCESS FOR THIS", Grey, 2);
                HudOverlay.DrawText(fb, 24, 224, "APP, THEN COME BACK (BACK KEY).", Grey, 2);
                Box(fb, GrantButton, 0x204060, Yellow);
                Centered(fb, GrantButton, "GRANT ACCESS", Yellow, 3);
                break;
            case Mode.Confirm:
                HudOverlay.DrawText(fb, 24, 80, "USING DATA DIR:", White, 3);
                int y = 130;
                foreach (var line in Wrap(KnownDir ?? "", 52).Take(8)) { HudOverlay.DrawText(fb, 24, y, line, Yellow, 2); y += 26; }
                Box(fb, OkButton, 0x205030, Green);
                Centered(fb, OkButton, "OK", Green, 4);
                Box(fb, AnotherButton, 0x204060, Edge);
                Centered(fb, AnotherButton, "CHOOSE ANOTHER", White, 2);
                break;
            case Mode.Browse:
                RenderBrowse(fb);
                break;
        }
    }

    void RenderBrowse(FrameBuffer fb)
    {
        var b = _browser!;
        Fill(fb, 0, 40, W, 40, 0x0C121A);
        HudOverlay.DrawText(fb, 8, 53, b.Current == null ? "CHOOSE A FOLDER (STORAGE)" : Fit(b.Current.Replace((char)92, (char)47), 76, true), White, 2);
        int total = Count;
        var names = b.Names.ToList();
        for (int i = 0; i < Rows; i++)
        {
            int idx = _scroll + i;
            if (idx >= total) break;
            int ry = ListTop + i * RowH;
            Fill(fb, ListLeft, ry, ListRight - ListLeft, RowH - 2, (idx & 1) == 0 ? Panel : 0x16222Eu);
            bool up = b.Current != null && idx == 0;
            string label = up ? ".. (UP)" : "/ " + Fit(names[idx - (b.Current != null ? 1 : 0)].Replace((char)92, (char)47), 40, b.Current == null);
            HudOverlay.DrawText(fb, ListLeft + 8, ry + 12, label, up ? Yellow : White, 2);
        }
        if (b.Current != null && total == 1) HudOverlay.DrawText(fb, 16, ListTop + 52, b.Error != null ? "CANNOT READ THIS FOLDER" : "(NO SUBFOLDERS)", Grey, 2);
        if (b.Current == null && total == 0) HudOverlay.DrawText(fb, 16, ListTop + 12, "NO STORAGE FOUND", Red, 2);
        Box(fb, ScrollUp, 0x16222E, _scroll > 0 ? Edge : 0x203040);
        Triangle(fb, ScrollUp, true, _scroll > 0 ? White : Grey);
        bool more = _scroll + Rows < total;
        Box(fb, ScrollDown, 0x16222E, more ? Edge : 0x203040);
        Triangle(fb, ScrollDown, false, more ? White : Grey);
        bool valid = b.IsValidHere;
        Box(fb, SelectButton, valid ? 0x205030u : 0x182028u, valid ? Green : 0x405060u);
        Centered(fb, SelectButton, valid ? "SELECT: GAME DATA FOUND" : b.Current == null ? "OPEN A FOLDER" : "NO GAME DATA HERE", valid ? Green : Red, 2);
        Box(fb, CancelButton, 0x204060, Edge);
        Centered(fb, CancelButton, KnownDir != null ? "CANCEL" : "QUIT", White, 3);
    }
}
