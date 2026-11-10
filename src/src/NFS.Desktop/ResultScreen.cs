using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;
using System.Diagnostics;

namespace NFS.Desktop;

/// <summary>Post-race screens (statdisp) on EPOST.QFS, TEXT and FFN fonts. [ref:1491]
/// The view chooses the menu screen address: race results (the shared port table + the panel of the selected [ref:1492]
/// driver and the total record 0x284), or lap times of one driver [ref:1493]
/// (lap record 0x2B1). The driver is chosen with LEFT/RIGHT (0x4B00/0x4D00) – the choice is kept in [ref:1494]
/// <see cref="MenuController.ResultDriver"/>.</summary>
public sealed class ResultScreen
{
    readonly Fsh _art;
    readonly TextFile _text;
    readonly string? _gameDir;
    readonly int _language;
    readonly Dictionary<string, FshImage?> _images = new();
    public FrontEndFont Font { get; }
    /// <summary>+0x40: speed unit (km/h or mph); scales the value [ref:1495]
    /// by the fixed-point factor 3.6/65536 or 2.23694/65536.</summary>
    public bool Metric { get; set; } = true;
    public ResultScreen(string? gameDir = null, int language = 0)
    {
        _gameDir = gameDir;
        _language = Math.Clamp(language, 0, 5);
        string[] languages = ["ENG", "GER", "FRE", "SPA", "ITA", "SWE"];
        // The file is E/G/F/S/I/WPOST.QFS, selected by language. [ref:1496]
        _art = Fsh.Load($"FEDATA/PC/ART/{"EGFSIW"[_language]}POST.QFS", gameDir);
        _text = TextFile.Load(languages[_language], gameDir);
        Font = new FrontEndFont(gameDir);
    }
    /// <summary>View line: text, position and palette colour index. Align in the TEXT encoding: [ref:1497]
    /// 0/3 left, 1/4 right, 2/5 centre, +3 = with shadow at +1,+1 (see <see cref="FrontEndFont.Draw"/>).</summary>
    public readonly record struct StatLine(string Text, int X, int Y, int Colour, int Align = 0);

    /// <summary>4 = right with shadow, 5 = centre with shadow – values for the results screens. [ref:1498]</summary>
    const int AlignRight = 4, AlignCentre = 5;

    /// <summary>Row spacing of the results screens (0x1A). [ref:1499]</summary>
    public const int Spacing = 0x1A;
    /// <summary>0x2B2 "lap" – anchor of the lap number (325,131).</summary>
    public const int LapHeaderText = 0x2B2;
    /// <summary>0x2A4 – lap time column (x 390).</summary>
    public const int LapColumnText = 0x2A4;
    /// <summary>0x2B1 „record lap time:” (280,440).</summary>
    public const int LapRecordText = 0x2B1;
    /// <summary>0x284 „record total time:” (280,440).</summary>
    public const int TotalRecordText = 0x284;
    /// <summary>0x280/0x281/0x282 – driver panel labels (310, rows 242/270/298).</summary>
    public const int PlaceLabelText = 0x280, TotalTimeText = 0x281, TopSpeedText = 0x282;
    /// <summary>0x279 – driver panel value column (x 375).</summary>
    public const int DriverValueText = 0x279;
    /// <summary>0x2A0 „n/a”, 0x2A1 „aborted”.</summary>
    public const int NotAvailableText = 0x2A0, AbortedText = 0x2A1;
    /// <summary>0x2A6/0x29D – driver name rows (y 100 on the lap times screen, 184 in the panel). [ref:1500]</summary>
    public const int DriverNameText = 0x2A6, PanelDriverText = 0x29D;

    public string Time(int ticks)
    {
        // x87 FISTP (nearest/even), unlike the HUD's truncation. [ref:1501]
        long t = (long)Math.Round(Math.Abs((double)ticks) * 1.5625, MidpointRounding.ToEven);
        char a = (char)RaceConstants.ResultTimeSeparatorA[_language];
        char b = (char)RaceConstants.ResultTimeSeparatorB[_language];
        return $"{t / 6000}{a}{t / 100 % 60:00}{b}{t % 100:00}";
    }

    /// <summary>The largest LapMaxSpeed value, printed as "%5.2f" after scaling [ref:1502]
    /// (16.16 m/s → km/h or mph). The port adds the unit as the HUD does (HudOverlay.SpeedText).</summary>
    public string TopSpeed(int speedFx) =>
        $"{Math.Abs((double)speedFx) / 65536.0 * (Metric ? 3.6 : 2.23694):0.00} {(Metric ? "KMH" : "MPH")}";

    /// <summary>Index of the fastest lap (0 when there are no times). [ref:1503]</summary>
    public static int BestLapIndex(IReadOnlyList<int> lapTicks)
    {
        int best = 0;
        for (int i = 1; i < lapTicks.Count; i++)
            if (lapTicks[i] > 0 && (lapTicks[best] <= 0 || lapTicks[i] < lapTicks[best])) best = i;
        return best;
    }

    /// <summary>Place text: 0x285..0x290 "1st".."12th" ((0x284 + place)). [ref:1504]</summary>
    public string PlaceText(int place) => place is >= 1 and <= 12 ? _text[0x284 + place] : _text[NotAvailableText];

    /// <summary>(1P panel): driver and car name above the column, number and time of each lap [ref:1505]
    /// (fastest in colour 2, others 4), "aborted" when the driver did not finish [ref:1506]
    /// (state +0x280 ≠ 2). Numbering in column 0x2B2 (325, right-aligned), times in 0x2A4 (390, centre), rows from
    /// y(0x2B2) + 0x1A; driver name at y(0x2A6) = 100, car at y(0x2B2) = 131. [ref:1507]</summary>
    public IReadOnlyList<StatLine> LapTimesLines(RacerResult racer, int laps)
    {
        var header = _text.Entries[LapHeaderText];
        int timeX = _text.Entries[LapColumnText].X;
        var lines = new List<StatLine>
        {
            new(racer.Driver, timeX, _text.Entries[DriverNameText].Y, 5, AlignCentre),
            new(racer.Model, timeX, header.Y, 2, AlignCentre),
            new(_text[LapHeaderText], header.X, header.Y, header.Colour, header.Align)
        };
        int best = BestLapIndex(racer.LapTicks);
        int count = racer.RaceState == 2 ? Math.Clamp(laps, 0, 8) : racer.LapTicks.Count;
        int y = header.Y;
        for (int i = 0; i < count && i < racer.LapTicks.Count; i++)
        {
            y = header.Y + Spacing * (i + 1);
            lines.Add(new((i + 1).ToString(), header.X, y, 2, AlignRight));                   // number [ref:1508]
            if (racer.LapTicks[i] > 0) lines.Add(new(Time(racer.LapTicks[i]), timeX, y, i == best ? 2 : 4, AlignCentre));
        }
        if (racer.RaceState != 2) lines.Add(new(_text[AbortedText], timeX, y + Spacing, 4, AlignCentre));
        return lines;
    }

    /// <summary>(driver panel, column 0x279 = 375): surname (y 184, colour 5), car (y(0x280) − 0x18 = 218 [ref:1509]
    /// white), place (0x284 + place from 0x285..0x290), total time (0x281) and "top speed" (0x282). Labels
    /// at their TEXT anchors (310, 242/270/298, right-aligned), values in colour 4 at the centre of column 375.
    /// In the original the panel sits inside the results screen frame – the port keeps the shared table there, so the panel is below it.</summary>
    public IReadOnlyList<StatLine> DriverPanelLines(RacerResult racer, int y)
    {
        var label = _text.Entries[PlaceLabelText];
        int valueX = _text.Entries[DriverValueText].X;
        string total = racer.RaceState == 1 ? _text[AbortedText] : Time(racer.RaceTicks) + (racer.Estimated ? "*" : "");
        string speed = racer.RaceState == 1 ? _text[NotAvailableText] : TopSpeed(racer.TopSpeedFx);
        return
        [
            new(racer.Driver, valueX, y - (label.Y - _text.Entries[PanelDriverText].Y), 5, AlignCentre),
            new(racer.Model, valueX, y - 0x18, 2, AlignCentre),
            new(_text[PlaceLabelText], label.X, y, label.Colour, label.Align),
            new(PlaceText(racer.Place), valueX, y, 4, AlignCentre),
            new(_text[TotalTimeText], label.X, y + Spacing, label.Colour, label.Align),
            new(total, valueX, y + Spacing, 4, AlignCentre),
            new(_text[TopSpeedText], label.X, y + Spacing * 2, label.Colour, label.Align),
            new(speed, valueX, y + Spacing * 2, 4, AlignCentre)
        ];
    }

    /// <summary>0x284/0x2B1: "record total time:"/"record lap time:" (own TEXT anchor, right-aligned) + the record
    /// from FEDATA/PC/STATS (anchor x + 10, left-aligned with shadow – align 3, white). The original draws the line only
    /// when the time in the file is not 0.</summary>
    public IReadOnlyList<StatLine> RecordLines(int labelTextId, string? record)
    {
        if (record is null) return [];
        var anchor = _text.Entries[labelTextId];
        return [new(_text[labelTextId], anchor.X, anchor.Y, anchor.Colour, anchor.Align),
                new(record, anchor.X + 10, anchor.Y, 2, 3)];
    }

    /// <summary>Track record from the .STF of the current race. [ref:1510]</summary>
    string? RecordText(MenuController? menu, bool lapRecord)
    {
        // The track's .STF index is the track index in the menu; the menu holds the user's copy and the records saved after the race. [ref:1511]
        if (menu?.Session is not { } session || menu.RaceRecords is not { } file) return null;
        int index = lapRecord ? 0 : session.Setup.NumLaps == 2 ? 1 : session.Setup.NumLaps == 4 ? 0xB : 0x15;
        if (index >= file.Records.Count || file.Records[index].Time == 0) return null;
        return TrackRecords.Text(_text, file.Records[index], Time);
    }

    // ---- Animated sponsor logo (mode 0: shape "omg7" from EPOST.QFS) ---- [ref:1512]
    // The screens call them in each loop. This is NOT the car: in mode 0 there is only [ref:1513]
    // the Omega logo (215x112 @13,27); the car brand logo (car7/cpg7) is drawn by the victory screen, and the showcase [ref:1514]
    // does the same "squash" in MenuScene.

    /// <summary>Frame timing: the loop advances the frame number when the tick sum of the clock [ref:1515] [ref:3409]
    /// (128 Hz) divides by 10 with no remainder (division by 10, remainder 0). [ref:1516]</summary>
    public const int LogoFrameTicks = 10, LogoTicksPerSecond = 128;
    /// <summary>Last animation frame (0x23 – alpha range: frame × 256 / 0x23). [ref:1517]</summary>
    public const int LogoLastFrame = 0x23;
    /// <summary>Each call adds 0x1E to the phase – the same constant as the showcase logo. [ref:1518]</summary>
    public const int LogoPhaseStep = MenuScene.ShowcaseLogoStep;
    /// <summary>Logo shape in EPOST/GPOST/….QFS (literal constant). [ref:1519]</summary>
    public const string LogoShape = "omg7";

    /// <summary>Frame number after <paramref name="ticks"/> ticks at 128 Hz since entering the screen (0 at the first drawing).
    /// [hypothesis: the original loop hits every multiple of 10 – with uneven clock steps it could skip some]. Without animation
    /// (config +0x12D = 0) the last frame is shown at once. [ref:1520]</summary>
    public static int LogoFrame(long ticks, bool animated) =>
        animated ? (int)Math.Min(Math.Max(ticks, 0) / LogoFrameTicks, LogoLastFrame) : LogoLastFrame;

    /// <summary>Phase when drawing frame <paramref name="frame"/>: 0x1E per drawing; the first has phase 0x1E. [ref:1521]</summary>
    public static int LogoPhase(int frame) => LogoPhaseStep * (frame + 1);

    /// <summary>Logo width factor (0x40 = full): quarter sine as in the config icon / showcase logo,
    /// after 35 drawings (phase ≥ 0x400) full width.</summary>
    public static int LogoFactor(int frame, bool animated) =>
        animated ? MenuScene.SquashFactor(LogoPhase(frame)) : MenuScene.IconSquashFull;

    /// <summary>Alpha level of the full-width logo: frame × 256 / 0x23; 256 from frame 0x23. [ref:1522]</summary>
    public static int LogoLevel(int frame) => Math.Min(frame, LogoLastFrame) * 256 / LogoLastFrame;

    /// <summary>The logo is drawn only when the first-place car is a regular model (&lt; 0xC). [ref:1523]</summary>
    static bool LogoShown(RaceResults results) =>
        results.Racers.FirstOrDefault(r => r.Place == 1) is not { } winner || winner.CarType < 0xC;

    readonly Stopwatch _logoClock = Stopwatch.StartNew();
    object? _logoKey;
    long? _fixedLogoTicks;
    /// <summary>Sets the logo animation clock (128 Hz ticks since entering the screen) to a fixed value – PNG snapshots and tests.
    /// <c>null</c> = real time (restarts when the screen or the results change).</summary>
    public void SetLogoTicks(long? ticks) => _fixedLogoTicks = ticks;

    long LogoTicks(object key)
    {
        if (_fixedLogoTicks is { } fixedTicks) return fixedTicks;
        if (!Equals(_logoKey, key)) { _logoKey = key; _logoClock.Restart(); }   // a new screen starts the phase from zero [ref:1524]
        return _logoClock.ElapsedMilliseconds * LogoTicksPerSecond / 1000;
    }

    /// <summary>Draws the logo of frame <paramref name="frame"/>: during the phase (&lt; 0x400) a scaled blit
    /// from the centre of the shape (without level), then a plain blit with alpha frame × 256 / 0x23. [ref:1525]</summary>
    public void DrawLogo(FrameBuffer fb, int frame, bool animated) => DrawLogo(fb, frame, animated, Image(LogoShape), 0, 0);

    /// <summary>Shared drawing of modes 0 and 2: <paramref name="dx"/>/<paramref name="dy"/> is the offset from the shape position
    /// (mode 0: 0,0; mode 2 rec7: −10, +0x19). [ref:1526]</summary>
    void DrawLogo(FrameBuffer fb, int frame, bool animated, FshImage? logo, int dx, int dy)
    {
        if (logo is null) return;
        int x = logo.PosX + dx, y = logo.PosY + dy;
        int factor = LogoFactor(frame, animated);
        if (factor == MenuScene.IconSquashFull) { MenuScene.BlitLevel(fb, logo, x, y, LogoLevel(frame)); return; }
        int w = (short)(factor * logo.Width / 64), half = w / 2;
        if (half < 1) half = -half;
        MenuScene.BlitScaled(fb, logo, x + logo.Width / 2 - half, y, w, logo.Height, MenuScene.ShowcaseFullLevel);
    }

    // ---- Mode 2 (KO/tournament screens) ---- [ref:1527]
    // The same condition (winner < 0xC), the same animation (a frame every 10 ticks, phase +0x1E, alpha frame×256/0x23), but
    // the "rec7" shape from the track records QFS (mode 2: [ref:1528]
    // i.e. FEDATA/PC/ART/TRACK/<track>REC.QFS) at the shape position (−10, +0x19). All four screens call it,
    // but only for a winner < 0xC (`if (sVar < 0xc)` right before the call). [ref:1529]

    /// <summary>Mode 2 logo shape (literal constant). [ref:1530]</summary>
    public const string SeriesLogoShape = "rec7";
    /// <summary>Offset of rec7 relative to the shape position (x −10, y +0x19). [ref:1531]</summary>
    public const int SeriesLogoDx = -10, SeriesLogoDy = 0x19;

    readonly Dictionary<int, FshImage?> _seriesLogos = new();

    /// <summary>rec7 shape from the track records QFS of track <paramref name="track"/> (menu index 0..7); null when the file or shape is missing
    /// (original: == 0 → return without drawing). [ref:1532]</summary>
    public FshImage? SeriesLogo(int track)
    {
        track = Math.Clamp(track, 0, 7);
        if (_seriesLogos.TryGetValue(track, out var cached)) return cached;
        FshImage? image = null;
        try
        {
            var fsh = Fsh.Load($"FEDATA/PC/ART/TRACK/{TrackRecords.RecordsArt(track).ToUpperInvariant()}.QFS", _gameDir);
            if (fsh.Find(SeriesLogoShape) is { } entry) image = fsh.Decode(entry, fsh.GlobalPalette());
        }
        catch (IOException) { }
        return _seriesLogos[track] = image;
    }

    /// <summary>Draws the mode 2 logo (rec7) of frame <paramref name="frame"/> of track <paramref name="track"/>.</summary>
    public void DrawSeriesLogo(FrameBuffer fb, int track, int frame, bool animated) =>
        DrawLogo(fb, frame, animated, SeriesLogo(track), SeriesLogoDx, SeriesLogoDy);

    FshImage? Image(string name)
    {
        if (!_images.TryGetValue(name, out var image))
            _images[name] = image = _art.Find(name) is { } e ? _art.Decode(e, _art.GlobalPalette()) : null;
        return image;
    }
    void Shape(FrameBuffer fb, string name)
    {
        if (Image(name) is { } image) OriginalHud.Blit(fb, image, image.PosX, image.PosY);
    }
    public void Background(FrameBuffer fb) { fb.Clear(0, 0, 0); Shape(fb, "back"); }
    void Lines(FrameBuffer fb, IReadOnlyList<StatLine> lines)
    {
        foreach (var line in lines) Font.Draw(fb, line.Text, line.X, line.Y, align: line.Align, colour: line.Colour);
    }

    public void Draw(FrameBuffer fb, RaceResults results, MenuController? menu = null)
    {
        Background(fb);
        bool series = menu?.TournamentSummary == true || menu?.KnockoutSummary == true;
        // Localized post-race heading graphics: e1..e6, placement carried by FSH.
        int heading = menu?.TournamentSummary == true ? (menu.Tournament!.Finished ? 4 : 3)
            : menu?.KnockoutSummary == true ? (menu.Knockout!.CanContinue ? 5 : 6) : 1;
        Shape(fb, $"{"egfsiw"[_language]}{heading}  ");
        if (menu != null) Metric = menu.Session?.Setup.Get(0x40) != 0;
        // Mode 0 (screens) = omg7, mode 2 (KO/tournament etc.) = rec7 from the track records QFS. [ref:1533]
        // Adaptation: the original draws the logo after the texts (its upper-left part is empty there); the port's table reaches
        // this area, so the logo goes under the texts to keep the table readable.
        if (LogoShown(results))
        {
            bool animated = menu == null || menu.Config[MenuScene.DetailOffset] != 0;   // config +0x12D [ref:1534]
            int frame = LogoFrame(LogoTicks((results, menu?.CurrentScreen.Id)), animated);
            if (!series) DrawLogo(fb, frame, animated);
            else DrawSeriesLogo(fb, menu!.Config.Track, frame, animated);
        }
        bool lapView = menu != null && !series && menu.CurrentScreen.Id == MenuController.LapTimesScreen;
        RacerResult? selected = menu == null || results.Racers.Count == 0
            ? null : results.Racers[Math.Clamp(menu.ResultDriver, 0, results.Racers.Count - 1)];
        int start = _text.Entries[0x2C5].Y, spacing = Spacing;
        int placeX = Image($"{"egfsiw"[_language]}meg")!.PosX;
        // Reuse TEXT anchors, widening the original series table to include car/time/best in one view.
        int nameX = _text.Entries[0x27B].X, modelX = _text.Entries[0x2C5].X;
        int timeX = _text.Entries[0x2C7].X, bestX = _text.Entries[0x2C4].X;
        int rows = 0, panelY = 0;
        if (lapView) Lines(fb, LapTimesLines(selected!, menu!.Session?.Setup.NumLaps ?? selected!.LapTicks.Count));
        else
        {
            Font.Draw(fb, "#", placeX, start - spacing);
            Font.Draw(fb, _text[0x2B6], nameX, start - spacing, align: 1);
            Font.Draw(fb, "Car", modelX, start - spacing, align: 1);
            Font.Draw(fb, _text[series && menu?.TournamentSummary == true ? 0x2C3 : 0x281], timeX, start - spacing, align: 1);
            Font.Draw(fb, "best lap", bestX, start - spacing, align: 1);
            var table = menu?.TournamentSummary == true
                ? menu.Tournament!.Standings.Select(id => results.Racers.Single(r => r.CarIndex == id))
                : results.Racers;
            foreach (var row in table)
            {
                int y = start + rows++ * spacing;
                // row colour depends only on the player flag (+0x1F4 & 4) [ref:1535]
                // 2 for AI, 5 for the player; selecting a driver's row does not highlight it.
                int colour = row.Human ? 5 : 2;
                Font.Draw(fb, (series ? rows : row.Place).ToString(), placeX, y, colour: colour);
                Font.Draw(fb, string.IsNullOrEmpty(row.Driver) ? _text[0x29D] : row.Driver, nameX, y, align: 1, colour: colour);
                Font.Draw(fb, row.Model, modelX, y, align: 1, colour: colour);
                string time = menu?.TournamentSummary == true ? menu.Tournament!.Points[row.CarIndex].ToString()
                    : row.RaceState == 1 ? _text[0x2A1] : Time(row.RaceTicks) + (row.Estimated ? "*" : "");
                Font.Draw(fb, time, timeX, y, align: 1, colour: colour);
                Font.Draw(fb, row.BestLapTicks is { } b ? Time(b) : _text[0x2A0], bestX, y, align: 1, colour: colour);
            }
        }
        // 0x27B = first row of the action column (y 253 on both results screens; the next ones every 0x1A, like
        // anchors 0x27C/0x27D/0x27F and 0x2AB/0x2AC/0x2AE/0x2AF). The port's table may be longer, so the actions
        // start only below it.
        int actionY = Math.Max(start + rows * spacing, _text.Entries[0x27B].Y);
        // the selected driver's panel (surname/car + labels 0x280..0x282, values in column 0x279) [ref:1536]
        // in its own TEXT rows (242/270/298; the action column 0x27B sits at x 167, so they do not overlap);
        // when the table reaches these rows (5+ cars), the panel moves below it together with the surname and car.
        panelY = Math.Max(_text.Entries[PlaceLabelText].Y,
            start + rows * spacing + (_text.Entries[PlaceLabelText].Y - _text.Entries[PanelDriverText].Y));
        if (selected != null && !series && !lapView && panelY + Spacing * 2 <= _text.Entries[TotalRecordText].Y)
            Lines(fb, DriverPanelLines(selected, panelY));
        else panelY = 0;   // no panel – messages go right below the action column
        if (menu != null)
        {
            IReadOnlyList<string> actions = menu.TournamentSummary ? menu.TournamentActions : menu.KnockoutSummary
                ? menu.KnockoutActions : menu.CurrentScreen.Items.Where(it => it.Kind == 2).Select(menu.ItemText).ToArray();
            for (int n = 0; n < actions.Count; n++)
            {
                bool wide = Font.Font(18).Measure(actions[n]) > nameX - placeX;
                Font.Draw(fb, actions[n], wide ? placeX : nameX, actionY + n * spacing,
                    align: wide ? 0 : 1, colour: n == menu.SelectedIndex ? 4 : 2);
            }
            if (series)
                Font.Draw(fb, menu.TournamentSummary ? menu.TournamentHeading : menu.KnockoutHeading,
                    bestX, actionY, align: 1, colour: 4);
            if (menu.KnockoutSummary && menu.Knockout?.EliminatedIdentity is { } id)
                Font.Draw(fb, $"{_text[0x2E0]}: {id + 1}", bestX, actionY + spacing, align: 1, colour: 4);
            if (menu.KnockoutSummary && menu.Knockout?.Won == true)
                Font.Draw(fb, "MONOLITHIC STUDIOS UNLOCKED", bestX, actionY + spacing * 2, align: 1, colour: 4);
            if (!string.IsNullOrEmpty(menu.LastMessage))
            {
                string line = "";
                int y = Math.Max(actionY + actions.Count * spacing, panelY == 0 ? 0 : panelY + Spacing * 3);
                foreach (string word in menu.LastMessage.Split(' '))
                {
                    if (Font.Font(18).Measure(line + word) > bestX - modelX && line.Length > 0)
                    { Font.Draw(fb, line, modelX, y); y += spacing; line = ""; }
                    line += word + " ";
                }
                Font.Draw(fb, line, modelX, y);
            }
            if (!series) Lines(fb, RecordLines(lapView ? LapRecordText : TotalRecordText, RecordText(menu, lapView)));
        }
        else Font.Draw(fb, "ESC - EXIT", placeX, _text.Entries[TotalRecordText].Y);
        if (results.Racers.Any(r => r.Estimated && r.RaceState != 1) && menu?.TournamentSummary != true)
            Font.Draw(fb, "* estimated", bestX, _text.Entries[TotalRecordText].Y, align: 1);
    }
}
