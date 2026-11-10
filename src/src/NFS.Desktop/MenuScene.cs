using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Constants;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>CPU-only front-end presentation, shared by Desktop and CLI. Draws like the original: QFS background shape at full brightness [ref:1125]
/// with the items as in the original [ref:1126]
/// (FFN font, colour and position from TEXT.ENG, selected = highlight colour, disabled = colour 6), option values
/// at x+0x13 (0xD for the 13/14/16 fonts), ':' / '...' markers, and the car/track preview panel. [ref:1127]
/// Without the FFN fonts the old bitmap text is used as a fallback. With <see cref="AnimateChrome"/> (set by the game
/// loop, driven by <see cref="Advance(double)"/>) the flickering bars and the title cross-fade are drawn, as is the [ref:1128]
/// sliding of the preview panel; the colour swatch and the tournament badge [ref:1129]
/// are always drawn.</summary>
public sealed partial class MenuScene
{
    const int PanelX = 0xD3, PanelY = 0x13C, PanelWidth = 0x118, PanelHeight = 0x80;
    /// <summary>the panel is drawn at x = -13, 51, 115, 179 (step 0x40, until 0xF3) before the final 0xD3. [ref:1130]</summary>
    public const int SlideStart = -0xD, SlideStepPixels = 0x40, SlideSteps = 4;
    /// <summary>Time of one slide frame. The original loop has no timing of its own (one frame per iteration, flip rate of the
    /// display) – 60 Hz assumed [hypothesis].</summary>
    public const double SlideStepSeconds = 1.0 / 60;
    /// <summary>Menu track -> bit of the series mask (case 2 tests bit 4, case 3 bit 2, case 4 bit 3). [ref:1131]</summary>
    static readonly int[] TrackMaskBit = [0, 1, 4, 2, 3, 5, 6, 7];
    /// <summary>Rows of the icon name table (<see cref="MenuArtConstants.IconNames"/>), 22 columns each. [ref:1132]</summary>
    const int IconRowStride = 22, IconRowCount = 6, IconLevel = 0x100;
    /// <summary>Rows 0/1 (+0x1C) and the rest (+0x19) offset to x from the shape header; level 0x100 [ref:1133]
    /// (with flag 0x2500). [ref:1134]</summary>
    const int IconDxFirst = 0x1C, IconDxRest = 0x19;
    /// <summary>Pulse of the highlighted icon – full 0x400, step 0x3C per frame after 0x28 ticks [ref:1135]
    /// at 128 Hz since the value changed; the bright variant when anim &lt;= 0x100 or &gt;= 0x300.</summary>
    public const int IconAnimFull = 0x400, IconAnimStep = 0x3C, IconChangeTicks = 0x28;
    public const int IconLitFrom = 0x100, IconLitTo = 0x300;
    /// <summary>"squash": quarter sine table (<see cref="MathConstants.SinQuarter"/>, 0x101 entries, [0x100] = 0x10000) [ref:1136]
    /// and full width 0x40 (64/64) – the icon width factor in 1/64.</summary>
    public const int IconSquashFull = 0x40;
    /// <summary>Animation (bars, title) works only when config+0x12D &gt;= 2. The field is set [ref:1137]
    /// according to CPU speed (0 for slow machines) and (2); the port does not detect the CPU [ref:1138]
    /// so it stays at the default 2 from MenuController.Defaults.</summary>
    public const int DetailOffset = 0x12D;
    readonly string _dir;
    ResultScreen? _results;
    VictoryScreen? _victoryScreen;
    readonly MenuSpeech _speech = new();
    /// <summary>Narration: clip name in ENGLISH.VIV; the host connects <see cref="NFS.Game.FrontEnd.MenuAudio.PlaySpeech"/>. [ref:1139]</summary>
    public event Action<string>? Speech { add => _speech.Requested += value; remove => _speech.Requested -= value; }
    double _victoryTicks;

    /// <summary>Victory screen (see <see cref="VictoryScreen"/>); if the files are missing it ends at once, so as not to block the results. [ref:1140]</summary>
    bool DrawVictory(FrameBuffer fb, RacerResult winner)
    {
        try
        {
            _victoryScreen ??= new VictoryScreen(_dir, Controller.Config.Language);
            _victoryScreen.Draw(fb, winner, Controller.Session?.TrackNumber ?? Controller.Config.Track, (long)_victoryTicks,
                Controller.Config[DetailOffset] != 0, Controller.Config[0x1D]);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException)
        {
            Controller.EndVictory();
            return false;
        }
    }
    FrontEndFont? _font;
    TextFile? _text;
    bool _fontTried;
    readonly Dictionary<string, Fsh?> _art = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string Art, string Shape), FshImage?> _images = new(new ShapeKeyComparer());
    public MenuController Controller { get; }
    /// <summary>Animation state (bars, title colour). [ref:1141]</summary>
    public MenuAnimation Animation { get; } = new();
    /// <summary>Draw the animated chrome and slide the preview panel; false = the static first frame of the original.</summary>
    public bool AnimateChrome { get; set; }
    string? _panelKey, _panelScreen;
    /// <summary>Intro clocks (frames of <see cref="SlideStepSeconds"/>) for the list panel and the track info screen [ref:1142]
    /// counted from the moment the window / screen appeared in the scene.</summary>
    FrontEndDialog? _introDialog;
    double _dialogElapsed, _trackInfoElapsed, _exitElapsed;
    bool _exitStarted;
    bool _wasTrackInfo;
    string? _showcaseKey;
    /// <summary>"Squash" phase of the showcase logo: 0..0x400, step <see cref="ShowcaseLogoStep"/> per screen frame. [ref:1143]</summary>
    int _logoPhase;
    double _slideElapsed = double.PositiveInfinity, _showcaseElapsed, _creditsElapsed;
    /// <summary>128 Hz clock for the config icon animation. [ref:1144] [ref:3396]</summary>
    double _iconTicks;
    /// <summary>Icon animation state (original BSS): anim 0..0x400, +2 last selection [ref:1145]
    /// of the main menu, ticks of the last value change, snapshot of 6 values. [ref:1146]</summary>
    int _iconAnim, _iconSel, _iconChangeTicks;
    readonly ushort[] _iconValues = new ushort[IconRowCount], _iconScratch = new ushort[IconRowCount];
    /// <summary>0 on entry, 1 when this frame advanced the icon animation. [ref:1147]</summary>
    bool _iconStepped;
    /// <summary>Gate flag: the config icons advanced a step in the last drawn frame. [ref:1148]</summary>
    public bool IconStepPending => _iconStepped;
    public string? BackgroundSource { get; private set; }
    /// <summary>True when the last <see cref="Render"/> used the original FFN fonts.</summary>
    public bool UsesOriginalFonts { get; private set; }

    public MenuScene(MenuController controller, string? gameDir = null)
    {
        Controller = controller;
        Controller.RecordSpeech += _speech.Say;
        _dir = GameFiles.FindGameDir(gameDir);
        controller.InputBlocked = InputLocked;
        controller.HoldClosedDialog = d => AnimateChrome && d.Kind == DialogKind.List && Shape("main", "bsld") != null;
    }

    /// <summary>Menu clock: advances the bar / title animation and the panel slide by <paramref name="seconds"/>.</summary>
    public void Advance(double seconds) => Advance(seconds, false, false);

    /// <summary>Menu clock as <see cref="Advance(double)"/>; on the credits screen one more scroll frame [ref:1149]
    /// passes per <see cref="SlideStepSeconds"/>, where <paramref name="creditsSlow"/> = 'H' held (0x48, slower), and
    /// <paramref name="creditsFast"/> = 'P' (0x50, faster). <paramref name="keyPressed"/> =
    /// a key press or other event in this frame pauses the bar animation for <see cref="MenuAnimation.PauseTicks"/> ticks.</summary>
    public void Advance(double seconds, bool creditsSlow, bool creditsFast, bool keyPressed = false)
    {
        // is the 128 Hz hardware counter – the icon animation does not depend on config+0x12D. [ref:3397]
        _iconTicks += seconds * TimerHz;
        // with config+0x12D < 2 it leaves right after – without the bar step [ref:1150]
        // and without drawing (see DrawChrome); the animation counters stand still.
        // Gate: the frame after an icon step does not count (see DrawConfigIcons). [ref:1151]
        if (Controller.Config[DetailOffset] >= 2) Animation.Advance(seconds, keyPressed && !_iconStepped, _iconStepped);
        if (_slideElapsed < double.PositiveInfinity) _slideElapsed += seconds;
        SyncIntros();
        if (Controller.VictoryActive)
        {
            // the loop ends by itself after 0x500 ticks of the 128 Hz clock. [ref:1152]
            _victoryTicks += seconds * TimerHz;
            if (_victoryTicks > VictoryRules.DurationTicks) Controller.EndVictory();
        }
        else _victoryTicks = 0;
        if (_introDialog != null) _dialogElapsed += seconds;
        if (_exitStarted && Controller.Dialog is { Done: true })
        {
            // list exit on Enter/Esc; after the last frame the window disappears and the calling screen gets its continuation. [ref:1153]
            _exitElapsed += seconds;
            if ((int)(_exitElapsed / SlideStepSeconds) >= FrontEndDialog.ListExitFrames().Count) { _exitStarted = false; Controller.CompleteDialogClose(); }
        }
        if (_wasTrackInfo) _trackInfoElapsed += seconds;
        if (_showcaseKey != null) _showcaseElapsed += seconds;
        Controller.UpdateRecords();
        _speech.Advance(seconds, new MenuSpeechState(
            Victory: Controller.VictoryWinner != null,
            Showcase: Controller.InShowcase && Controller.ShowcaseModCar == null, ShowcaseCar: Controller.ShowcaseCar,   // no speech clip for a mod car
            History: Controller.InHistory, HistoryCar: Controller.HistoryCar,
            TrackInfo: Controller.InTrackInfo, TrackIntro: Controller.InTrackInfo && TrackInfoIntroFrame() != null,
            Track: Controller.Config.Track, Language: Controller.Config.Language, Units: Controller.Config.Units,
            ResultKey: ResultSpeechShown ? Controller.Results : null, ResultClip: ResultSpeechClip));
        if (!Controller.InCredits) { _creditsElapsed = 0; return; }
        _creditsElapsed += seconds;
        while (_creditsElapsed >= SlideStepSeconds)
        {
            _creditsElapsed -= SlideStepSeconds;
            Controller.CreditsFrame(creditsSlow, creditsFast);
        }
    }

    /// <summary>Result screens with spoken commentary: KO (round and final) and the tournament final. [ref:1154]</summary>
    bool ResultSpeechShown => Controller.Results != null &&
        (Controller.KnockoutSummary || Controller.TournamentSummary && Controller.Tournament!.Finished);

    string? ResultSpeechClip()
    {
        var rows = Controller.Results!.Racers;
        bool split = Controller.Config.GameType == 1;
        if (Controller.TournamentSummary) return ResultSpeech.TournamentFinal(rows, Controller.Tournament!.Standings, split);
        return ResultSpeech.Knockout(rows, final: Controller.Knockout!.Stage == 7, split, Controller.Knockout.Humans,
            Controller.Session?.TrackNumber ?? -1);
    }

    /// <summary>Starts the intro clocks from zero when a new modal window opens or the track info screen is entered (called from Advance and Render,
    /// so frame zero is also drawn before the first Advance).</summary>
    void SyncIntros()
    {
        if (!ReferenceEquals(Controller.Dialog, _introDialog)) { _introDialog = Controller.Dialog; _dialogElapsed = 0; _exitStarted = false; }
        if (!_exitStarted && Controller.Dialog is { Done: true, Kind: DialogKind.List } && AnimateChrome) { _exitStarted = true; _exitElapsed = 0; }
        bool trackInfo = Controller.InTrackInfo;
        if (trackInfo != _wasTrackInfo) { _wasTrackInfo = trackInfo; _trackInfoElapsed = 0; }
    }

    /// <summary>The original does not read keys in the intro / exit loops of the list panel or the track info intro (the loops [ref:1155]
    /// are synchronous, without input); the controller then ignores input. Only with <see cref="AnimateChrome"/>. [ref:1156]</summary>
    bool InputLocked()
    {
        if (!AnimateChrome) return false;
        SyncIntros();
        if (Controller.Dialog is { Kind: DialogKind.List } dialog)
        {
            if (dialog.Done) return _exitStarted;
            return ListIntroFrame() != null;
        }
        return Controller.InTrackInfo && TrackInfoIntroFrame() != null;
    }

    Fsh? Art(string name)
    {
        if (_art.TryGetValue(name, out var fsh)) return fsh;
        // localized QFS, plain QFS, plain FSH. [ref:1157]
        foreach (string rel in new[] { $"FEDATA/PC/ART/E{name}.QFS", $"FEDATA/PC/ART/{name}.QFS", $"FEDATA/PC/ART/{name}.FSH" })
            if (GameFiles.Exists(rel, _dir))
            {
                try { fsh = Fsh.Load(rel, _dir); break; }
                catch (Exception e) when (e is IOException or InvalidDataException) { }
            }
        return _art[name] = fsh;
    }

    FshImage? Shape(string art, string shape)
    {
        var key = (art, shape);
        if (_images.TryGetValue(key, out var image)) return image;
        var fsh = Art(art);
        var entry = fsh?.Find(shape);
        try { image = entry == null ? null : fsh!.Decode(entry, fsh.GlobalPalette()); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException) { image = null; }
        return _images[key] = image;
    }

    static string ArtName(MenuScreen s) => s.Art ?? "main";

    FshImage? Background()
    {
        var s = Controller.CurrentScreen;
        return Shape(ArtName(s), s.Background ?? "blk6");
    }

    bool LoadFont()
    {
        if (_fontTried) return _font != null;
        _fontTried = true;
        try
        {
            _text = Controller.Text;   // TEXT.<language> [ref:1158]
            var font = new FrontEndFont(_dir);
            foreach (int size in new[] { 13, 16, 18, 36 }) _ = font.Font(size);
            _font = font;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException)
        {
            _font = null;
        }
        return _font != null;
    }

    /// <summary>TEXT.ENG of the original (read by the screens) [ref:1159]
    /// available when the FFN fonts have loaded.</summary>
    TextFile Text() => _text!;

    /// <summary>ResultScreen, used only for the time format (z.STF); null when there is no data. [ref:1160]</summary>
    ResultScreen? Result()
    {
        if (_results != null) return _results;
        try { return _results = new ResultScreen(_dir, Controller.Config.Language); }
        catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException) { return null; }
    }

    public void Render(FrameBuffer fb)
    {
        fb.Clear(0, 0, 0);
        SyncIntros();
        if (Controller.VictoryWinner is { } winner && DrawVictory(fb, winner)) return;
        if (Controller.InTrackInfo && TrackInfoIntroFrame() is { } intro) { DrawTrackInfoIntro(fb, intro); return; }
        if (Controller.InShowcase) DrawShowcase(fb);
        else if (Controller.InHistory) DrawHistory(fb);
        else if (Controller.InSlideShow) DrawSlideShow(fb);
        else if (Controller.InTrackInfo) DrawTrackInfo(fb);
        else if (Controller.InTrackRecords) DrawTrackRecords(fb);
        else if (Controller.InPersonalStats) DrawPersonalStats(fb);
        else if (Controller.InCredits) DrawCredits(fb);
        else
        {
            _showcaseKey = null;
            var bg = Background();
            BackgroundSource = bg == null ? null : ArtName(Controller.CurrentScreen) + "/" + bg.Name;
            if (bg != null) OriginalHud.Blit(fb, bg, bg.PosX, bg.PosY);
        }
        DrawChrome(fb);
        if (Controller.Status == MenuStatus.Results && Controller.Results is { } results)
        {
            try { (_results ??= new ResultScreen(_dir, Controller.Config.Language)).Draw(fb, results, Controller); DrawNameEditor(fb); DrawDialog(fb); return; }
            catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException) { }
        }
        UsesOriginalFonts = LoadFont();
        if (UsesOriginalFonts)
        {
            DrawPreview(fb);
            if (Controller.CurrentScreen.Id == MenuController.AudioScreen) DrawVolumeSliders(fb);
            // (credits) does not call it – the only item of that screen is not drawn. [ref:1161]
            if (!Controller.InCredits) DrawOriginalItems(fb);
            if (Controller.InShowcase && Controller.ShowcaseModCar is { } showcaseMod) DrawModShowcaseTexts(fb, showcaseMod);
            // the config icon bar is drawn after this (screens of type 1 and 2). [ref:1162]
            DrawConfigIcons(fb);
            if (Controller.InHistory) DrawHistoryTexts(fb);
            if (Controller.InTrackInfo) DrawTrackInfoTexts(fb);
            if (Controller.InTrackRecords) DrawTrackRecordTexts(fb);
            if (Controller.InPersonalStats) DrawPersonalStatsTexts(fb);
            if (Controller.InCredits) DrawCreditsTexts(fb);
            if (Controller.CurrentScreen.Id == MenuController.SplitControlsScreen) DrawSplitControlsHint(fb);
            DrawCapturePrompt(fb);
            DrawNameEditor(fb);
            DrawDialog(fb);
            if (!string.IsNullOrEmpty(Controller.LastMessage)) DrawMessage(fb, Controller.LastMessage);
            _font!.Draw(fb, "UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER OK   ESC BACK", 320, 465, 13, 5, 1);
            return;
        }
        HudOverlay.DrawText(fb, 24, 20, "NEED FOR SPEED II SE", 0xFFFF00);
        DrawFallbackItems(fb, 74, 26);
        if (!string.IsNullOrEmpty(Controller.LastMessage))
            Wrapped(fb, 24, 402, Controller.LastMessage, 0xFFAA44);
        HudOverlay.DrawText(fb, 24, 456, "UP/DOWN SELECT  LEFT/RIGHT CHANGE  ENTER OK  ESC BACK", scale: 2);
    }

    /// <summary>Car showcase screen: "back" background (640x480) and the photo collage of the page [ref:1163]
    /// ("gnrl" / "perf" / "mech", 361x143) from the QFS of the selected car (`FEDATA/PC/ART/&lt;name&gt;.QFS`), and on top the "car7" logo
    /// with its entry animation. The item texts are drawn by <see cref="DrawOriginalItems"/> with the IDs [ref:1164]
    /// substituted in. The Film.dct of the "video" item is not played. [ref:1165]</summary>
    void DrawShowcase(FrameBuffer fb)
    {
        if (Controller.ShowcaseModCar is { } modCar) { DrawModShowcase(fb, modCar); return; }
        string art = Controller.ShowcaseArt;
        BackgroundSource = null;
        if (Shape(art, "back") is { } back)
        {
            OriginalHud.Blit(fb, back, back.PosX, back.PosY);
            BackgroundSource = art + "/back";
        }
        string page = ShowcasePageShape(Controller.ShowcasePage);
        if (Shape(art, page) is { } photos) OriginalHud.Blit(fb, photos, photos.PosX, photos.PosY);
        int level = ShowcaseLevel(art);
        if (Shape(art, "car7") is { } logo) DrawShowcaseLogo(fb, logo, level);
    }

    /// <summary>Brand logo "car7". The "squash" phase is still running (&lt; 0x400, and with [ref:1166]
    /// config+0x12D = 0 the original picks the full width) → drawn with a scaled blit [ref:1167]
    /// which takes no level argument, so the logo is then opaque and does not enter by level. After the end of the
    /// phase the logo is drawn normally as a shape with the entry level. Geometry as for the icons [ref:1168]
    /// <c>w = factor * width / 64</c> (16 bit), shape centre and height unchanged. [ref:1169]</summary>
    void DrawShowcaseLogo(FrameBuffer fb, FshImage logo, int level)
    {
        int x = logo.PosX, y = logo.PosY + ShowcaseLogoDy, factor = ShowcaseLogoFactor();
        if (factor == IconSquashFull) { BlitLevel(fb, logo, x, y, level); return; }
        int w = (short)(factor * logo.Width / 64), half = w / 2;
        if (half < 1) half = -half;
        BlitScaled(fb, logo, x + logo.Width / 2 - half, y, w, logo.Height, IconLevel);
    }

    /// <summary>Directory of the slideshow frames in ART; builds "%s%s.qfs" with the directory [ref:1170]
    /// and "%ssld%s.qfs" – in the game `FEDATA/PC/ART/SLIDES/`. [ref:1171]</summary>
    const string SlideDir = "SLIDES/";

    /// <summary>Car history screen: "back" background from `FEDATA/PC/ART/&lt;HIS&gt;.QFS` drawn [ref:1172]
    /// at (0,0) (with flag 0x400 and position 0,0), with the language overlay on top [ref:1173]
    /// then the items (TEXT 781/782 – <see cref="DrawOriginalItems"/>), the photo captions and the time axis
    /// (<see cref="DrawHistoryTexts"/>).</summary>
    void DrawHistory(FrameBuffer fb)
    {
        string art = Controller.HistoryArt;
        BackgroundSource = null;
        if (Shape(art, "back") is { } back)
        {
            OriginalHud.Blit(fb, back, 0, 0);
            BackgroundSource = art + "/back";
        }
        if (Shape(art, Controller.HistoryOverlay) is { } overlay) OriginalHud.Blit(fb, overlay, 0, 0);
    }

    /// <summary>Slideshow screen: one frame = one file [ref:1174]
    /// `FEDATA/PC/ART/SLIDES/&lt;name&gt;.QFS`, from which only the "back" background is drawn at (0,0).
    /// Frames do not change by themselves – only on a key press (there is no timer). [ref:1175]</summary>
    void DrawSlideShow(FrameBuffer fb)
    {
        string art = SlideDir + Controller.SlideArt;
        BackgroundSource = null;
        if (Shape(art, "back") is { } back)
        {
            OriginalHud.Blit(fb, back, 0, 0);
            BackgroundSource = art + "/back";
        }
    }

    /// <summary>Track info screen: "back" background and language overlay [track * 6 [ref:1176]
    /// ] from `FEDATA/PC/ART/TRACK/&lt;name&gt;INFO.QFS`, both at (0,0) (with flag 0x400) [ref:1177]
    /// with the "sldr" panel on top (flag 0x1000 = position from the shape header). The panel entry (flag 0x1800) and the brightening [ref:1178]
    /// of the background are drawn by <see cref="DrawTrackInfoIntro"/> (<see cref="TrackInfoIntro"/>).</summary>
    void DrawTrackInfo(FrameBuffer fb)
    {
        string art = TrackArt(Controller.TrackInfoArt);
        BackgroundSource = null;
        if (Shape(art, "back") is { } back)
        {
            OriginalHud.Blit(fb, back, 0, 0);
            BackgroundSource = art + "/back";
        }
        if (Shape(art, Controller.TrackInfoOverlay) is { } overlay) OriginalHud.Blit(fb, overlay, 0, 0);
        // draws "sldr" with flag 0x1000 – takes the position from the shape header (+0xC/+0xE). [ref:1179]
        if (Shape(art, "sldr") is { } slider) OriginalHud.Blit(fb, slider, slider.PosX, slider.PosY);
    }

    /// <summary>Current frame of the track info screen entry; null without <see cref="AnimateChrome"/> or after it ends.</summary>
    TrackInfoIntro.Frame? TrackInfoIntroFrame()
    {
        if (!AnimateChrome) return null;
        var frames = TrackInfoIntro.Frames(Controller.Config[DetailOffset]);
        int index = (int)(_trackInfoElapsed / SlideStepSeconds);
        return index < frames.Count ? frames[index] : null;
    }

    /// <summary>Loops before input: black screen, "sldr" sliding in from the left (with flag 0x1800 – x [ref:1180]
    /// absolute, y = 0), then the "back" background at level 0..16 / 16 and "sldr" in its place. No texts or positions. [ref:1181]</summary>
    void DrawTrackInfoIntro(FrameBuffer fb, TrackInfoIntro.Frame frame)
    {
        string art = TrackArt(Controller.TrackInfoArt);
        if (frame.FadeLevel is { } level && Shape(art, "back") is { } back)
            BlitLevel(fb, back, 0, 0, level * 256 / TrackInfoIntro.FadeLevelMax);
        if (Shape(art, "sldr") is { } slider)
            OriginalHud.Blit(fb, slider, frame.PanelX ?? slider.PosX, frame.PanelX is null ? slider.PosY : 0);
    }

    /// <summary>Track info draws two lines right-aligned at x = 0x268 (616): difficulty level [ref:1182]
    /// (TXT[track]) at y = 0x3F and length with unit at y = 0x5A – font 0x12, alignment 4, colour 2. [ref:1183]</summary>
    void DrawTrackInfoTexts(FrameBuffer fb)
    {
        _font!.Draw(fb, Controller.TrackInfoDifficulty, 0x268, 0x3F, 18, 4, 2);
        _font.Draw(fb, Controller.TrackInfoLength, 0x268, 0x5A, 18, 4, 2);
    }

    /// <summary>Track records screen: "back" background and language overlay [ref:1184]
    /// from `FEDATA/PC/ART/TRACK/&lt;name&gt;REC.QFS`, both at (0,0). [ref:1185]</summary>
    void DrawTrackRecords(FrameBuffer fb)
    {
        string art = TrackArt(Controller.TrackRecordsArt);
        BackgroundSource = null;
        if (Shape(art, "back") is { } back)
        {
            OriginalHud.Blit(fb, back, 0, 0);
            BackgroundSource = art + "/back";
        }
        if (Shape(art, Controller.TrackRecordsOverlay) is { } overlay) OriginalHud.Blit(fb, overlay, 0, 0);
    }

    /// <summary>Builds the file name in the directory `FEDATA/PC/ART/TRACK/`. [ref:1186]</summary>
    static string TrackArt(string name) => "TRACK/" + name;

    void DrawTrackRecordTexts(FrameBuffer fb)
    {
        foreach (var line in TrackRecordLines())
            _font!.Draw(fb, line.Text, line.X, line.Y, 18, line.Align, line.Colour);
    }

    /// <summary>Rows of the track records screen: the laps header ("%s %d", and for language 5 [ref:1188]
    /// "%d %s", in column 0xAB − 0x14, right-aligned, colour 2), the lap record label 0xB0 at y 0x1AC with the record line
    /// (colour 2) and ten place rows 0xB1..0xBA from the RowOffset(laps) entries + row, if
    /// their time ≠ 0 (colour 4).</summary>
    public IReadOnlyList<ResultScreen.StatLine> TrackRecordLines()
    {
        var lines = new List<ResultScreen.StatLine>();
        var text = Text();
        int laps = Controller.RecordsLaps;
        var header = text.Entries[MenuController.RecordsLapsText];
        lines.Add(new(Controller.Config.Language == 5 ? $"{header.Text} {laps}" : $"{laps} {header.Text}",
            header.X - 0x14, header.Y, 2, 4));
        var label = text.Entries[MenuController.RecordsLabelText];
        int labelX = text.Entries[MenuController.RecordsColumnText].X;
        lines.Add(new(label.Text, labelX, MenuController.RecordsRecordRowY, label.Colour, label.Align));
        var stats = Controller.TrackStats;
        if (stats is { Records: [var lapRecord, ..] } && lapRecord.Time != 0)
            AddRecordLines(lines, text, lapRecord, MenuController.RecordsRecordRowY, 2);
        int first = TrackRecords.RowOffset(laps);
        for (int row = 0; row < MenuController.RecordsRowTexts; row++)
        {
            var rowLabel = text.Entries[MenuController.RecordsFirstRowText + row];
            lines.Add(new(rowLabel.Text, labelX, rowLabel.Y, rowLabel.Colour, rowLabel.Align));
            int index = first + row;
            if (stats != null && index < stats.Records.Count && stats.Records[index].Time != 0)
                AddRecordLines(lines, text, stats.Records[index], rowLabel.Y, 4);
        }
        return lines;
    }

    /// <summary>Columns of a record row (align 3): name at x(0xA5), car at [ref:1189]
    /// x(0xA6) − 0x19, time at x(0xA7) and difficulty (2 arc / 1 wild / 0 sim) at x(0xA8). [ref:1190]</summary>
    void AddRecordLines(List<ResultScreen.StatLine> lines, TextFile text, StatRecord rec, int y, int colour)
    {
        var result = Result();
        lines.Add(new(TrackRecords.RecName(rec), text.Entries[MenuController.RecordsNameText].X, y, colour, 3));
        lines.Add(new(TrackRecords.CarName(text, rec.Car),
            text.Entries[MenuController.RecordsCarText].X + MenuController.RecordsCarOffset, y, colour, 3));
        if (result != null)
            lines.Add(new(result.Time(rec.Time), text.Entries[MenuController.RecordsTimeText].X, y, colour, 3));
        lines.Add(new(text[TrackRecords.SkillText(rec.Skill)], text.Entries[MenuController.RecordsSkillText].X,
            y, colour, 3));
    }

    /// <summary>Personal stats screen: "back" background and overlay [ref:1191]
    /// from `FEDATA/PC/ART/TRACK/&lt;name&gt;REC.QFS` (the same file as the records screen, but the track is chosen on this
    /// screen), both at (0,0) (with flag 0x400), and on top the history chart [ref:1192]
    /// – grid, axes and data line. The texts and menu items are drawn by <see cref="DrawPersonalStatsTexts"/>.</summary>
    void DrawPersonalStats(FrameBuffer fb)
    {
        string art = TrackArt(Controller.PersonalStatsArt);
        BackgroundSource = null;
        if (Shape(art, "back") is { } back)
        {
            OriginalHud.Blit(fb, back, 0, 0);
            BackgroundSource = art + "/back";
        }
        if (Shape(art, Controller.PersonalStatsOverlay) is { } overlay) OriginalHud.Blit(fb, overlay, 0, 0);
        DrawPersonalStatsGraph(fb, art);
    }

    /// <summary>Chart area: Y axis strip at (0xF1, 0x8F) 5 × 0x126 and X axis strip at (0xF3, 0x1B0) [ref:1193]
    /// 0x180 × 5; ten horizontal lines every 1/10 of the height and nine vertical lines every 1/10 of the width
    /// (colour 0xFFAAAAAA), and the data line and axes 0xFFFFFF50. Entries run from the oldest (left) to the newest
    /// (right), like the labels 0xCB/0xCA. Simplification: the original computes the point positions via [ref:1194]
    /// (projectors, not reproduced) – the port spreads the entries evenly along the time axis and scales [ref:1195]
    /// values linearly (smallest at the bottom at 0x1B0, largest at the top at 0x8F).</summary>
    public const int PlotLeft = 0xF3, PlotRight = 0x273, PlotTop = 0x8F, PlotBottom = 0x1B0;
    public const int GridLinesY = 10, GridLinesX = 9;
    public const uint GridColour = 0xFFAAAAAA, AxisColour = 0xFFFFFF50;

    void DrawPersonalStatsGraph(FrameBuffer fb, string art)
    {
        for (int i = 1; i <= GridLinesY; i++)
            Horizontal(fb, PlotLeft, PlotRight, PlotTop + (PlotBottom - PlotTop) * i / GridLinesY, GridColour);
        for (int i = 1; i <= GridLinesX; i++)
            Vertical(fb, PlotLeft + (PlotRight - PlotLeft) * i / (GridLinesX + 1), PlotTop, PlotBottom, GridColour);
        Fill(fb, 0xF1, PlotTop, 5, PlotBottom - PlotTop + 5, AxisColour);          // Y axis strip (0xF1, 0x8F, 5, 0x126)
        Fill(fb, PlotLeft, PlotBottom, PlotRight - PlotLeft, 5, AxisColour);       // X axis strip (0xF3, 0x1B0, 0x180, 5)
        var values = Controller.StatValues;
        if (values.Count == 0) return;                                            // no history: grid only
        int min = values.Min(), max = values.Max(), span = max - min;
        int X(int i) => values.Count == 1 ? (PlotLeft + PlotRight) / 2
            : PlotLeft + (PlotRight - PlotLeft) * i / (values.Count - 1);
        int Y(int value) => span == 0 ? (PlotTop + PlotBottom) / 2
            : PlotBottom - (PlotBottom - PlotTop) * (value - min) / span;
        for (int i = 1; i < values.Count; i++)
            Line(fb, X(i - 1), Y(values[i - 1]), X(i), Y(values[i]), AxisColour);
        for (int i = 0; i < values.Count; i++)
            if (Shape(art, "dot ") is { } dot) OriginalHud.Blit(fb, dot, X(i), Y(values[i]));
    }

    static void Horizontal(FrameBuffer fb, int x0, int x1, int y, uint colour)
    {
        if ((uint)y >= fb.Height) return;
        for (int x = x0; x <= x1; x++) fb.Set(x, y, colour);
    }

    static void Vertical(FrameBuffer fb, int x, int y0, int y1, uint colour)
    {
        if ((uint)x >= fb.Width) return;
        for (int y = y0; y <= y1; y++) fb.Set(x, y, colour);
    }

    static void Fill(FrameBuffer fb, int x, int y, int w, int h, uint colour)
    {
        for (int py = y; py < y + h; py++) Horizontal(fb, x, x + w - 1, py, colour);
    }

    /// <summary>Line segment – Bresenham rasterisation. [ref:1196]</summary>
    static void Line(FrameBuffer fb, int x0, int y0, int x1, int y1, uint colour)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            if ((uint)x0 < fb.Width && (uint)y0 < fb.Height) fb.Set(x0, y0, colour);
            if (x0 == x1 && y0 == y1) return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Chart type name (0xC1+type) at (0xE0, 0xA7) with font 0x10, shadow and colour 4 [ref:1197]
    /// "… history" title (0xC5+type) at (0xF3, y(0xC9) + 0x19) with font 0x12, time axis labels 0xCB "oldest"
    /// (0xF3, 0x1B9) and 0xCA "newest" (0x273, 0x1B9), and – when there are entries – the chart's extreme values at
    /// x 0xEC: minimum at the bottom (y 0x1B0) and maximum at the top (y 0x86), font 0x12, right-aligned, colour 4.
    /// The header and car name (font 0x18) go at x(0x2B4) + 0x1E. Extreme values are numbers [ref:1198]
    /// times (types 1 and 2) and speed (as in the results panel); the original builds them [ref:1199]
    /// with formats not reproduced here – the port shows the bare values only. [ref:1200]</summary>
    void DrawPersonalStatsTexts(FrameBuffer fb)
    {
        var font = _font!;
        var text = Text();
        int headerX = text.Entries[MenuController.PersonalStatsLapsText].X + MenuController.PersonalStatsHeaderDx;
        int secondY = text.Entries[MenuController.PersonalStatsHistoryText].Y + MenuController.PersonalStatsCarDy;
        font.Draw(fb, Controller.PersonalStatsHeader, headerX, text.Entries[MenuController.PersonalStatsLapsText].Y, 24, 1, 2);
        font.Draw(fb, Controller.PersonalStatsCarName, headerX, secondY, 24, 1, 2);
        font.Draw(fb, Controller.PersonalStatsViewName, ViewNameX, ViewNameY, 16, 3, 4);
        foreach (var line in PersonalStatLines())
            font.Draw(fb, line.Text, line.X, line.Y, 18, line.Align, line.Colour);
    }

    /// <summary>Chart type name and extreme values (0 when there are no entries). [ref:1201]</summary>
    public const int ViewNameX = 0xE0, ViewNameY = 0xA7, ExtremeX = 0xEC, MaxY = 0x86, MinY = 0x1B0;
    /// <summary>Time axis labels – "oldest" and "newest" at y 0x1B9 (x 0xF3 / 0x273). [ref:1202]</summary>
    public const int AxisLabelY = 0x1B9;

    /// <summary>Chart text lines: the "… history" title, time axis labels and extreme values. [ref:1203]
    /// The header, car name and chart type name use other fonts and are drawn in
    /// <see cref="DrawPersonalStatsTexts"/>.</summary>
    public IReadOnlyList<ResultScreen.StatLine> PersonalStatLines()
    {
        var text = Text();
        var lines = new List<ResultScreen.StatLine>
        {
            new(text[MenuController.PersonalStatsViewTitleText + Controller.StatView], 0xF3,
                text.Entries[MenuController.PersonalStatsHistoryText].Y + MenuController.PersonalStatsCarDy, 4, 3),
            new(text[MenuController.PersonalStatsOldestText], 0xF3, AxisLabelY, 4, 3),
            new(text[MenuController.PersonalStatsNewestText], 0x273, AxisLabelY, 4, 4),
        };
        var values = Controller.StatValues;
        if (values.Count == 0) return lines;                       // with no entries the original ends before the values
        lines.Add(new(ExtremeText(values.Min()), ExtremeX, MinY, 4, 1));
        lines.Add(new(ExtremeText(values.Max()), ExtremeX, MaxY, 4, 1));
        return lines;
    }

    /// <summary>Extreme value of the axis: time (types 1 and 2), speed as in the results panel [ref:1204]
    /// the rest as a number.</summary>
    string ExtremeText(int value) => Controller.StatView switch
    {
        1 or 2 => Result()?.Time(value) ?? value.ToString(),
        3 => Result()?.TopSpeed(value) ?? value.ToString(),
        _ => value.ToString(),
    };

    /// <summary>Credits screen: "Bac1" background and language overlay [ref:1205]
    /// from `FEDATA/PC/ART/CREDITS.QFS`, both at (0,0) with flag 0x400. On the final card the original draws instead of
    /// "Bac1" the shape "Bac%d" = "Bac7", which is not in CREDITS.QFS (so only the overlay). The list drawing
    /// is clipped by the original to the rectangle (0, 100)-(width, 0x1C2) – the port draws without clipping, because the visibility strip
    /// <see cref="MenuController.CreditsClipTop"/>..<see cref="MenuController.CreditsClipBottom"/>
    /// anyway fits on the screen. The text rows are drawn by <see cref="DrawCreditsTexts"/>.</summary>
    void DrawCredits(FrameBuffer fb)
    {
        BackgroundSource = null;
        if (Controller.CreditsFinale < 0 && Shape(MenuController.CreditsArt, "Bac1") is { } back)
        {
            OriginalHud.Blit(fb, back, 0, 0);
            BackgroundSource = MenuController.CreditsArt + "/Bac1";
        }
        if (Shape(MenuController.CreditsArt, Controller.CreditsOverlay) is { } overlay)
            OriginalHud.Blit(fb, overlay, 0, 0);
    }

    /// <summary>Credits list or the final card. The "knwd" and "chy2" graphics go with the text row 0x5CC [ref:1206]
    /// and the final card draws the last three rows over the overlay (the shape "Bac%d" comes out as "Bac7",
    /// which is not in CREDITS.QFS – the original then draws nothing).</summary>
    void DrawCreditsTexts(FrameBuffer fb)
    {
        if (Controller.CreditsFinale >= 0)
        {
            foreach (var line in Controller.CreditsFinaleLines)
                _font!.Draw(fb, line.Text, line.X, line.Y, 18, line.Align, line.Colour);
            return;
        }
        foreach (var (shape, x, y) in Controller.CreditsShapes)
            if (Shape(MenuController.CreditsArt, shape) is { } image) OriginalHud.Blit(fb, image, x, y);
        foreach (var line in MenuController.CreditsLines(_text!, Controller.CreditsY, fb.Width))
            _font!.Draw(fb, line.Text, line.X, line.Y, 18, line.Align, line.Colour);
    }

    /// <summary>Draws the three history photo captions at fixed positions (0xEC,0xC8), (0x29,0x15E) [ref:1207]
    /// (0xEC,0x15E) – font 18, alignment 3 (with shadow), colour 2 (texts from tables 48/60). [ref:1208]</summary>
    public static readonly (int X, int Y)[] HistoryCaptionPositions = [(236, 200), (41, 350), (236, 350)];

    /// <summary>History time axis: starting x and y from the record of the first row; each [ref:1209]
    /// drawn line increases y by 0x1A (26), including the wrapped tail – which goes to x+0x31. [ref:1210]</summary>
    void DrawHistoryTexts(FrameBuffer fb)
    {
        for (int i = 0; i < MenuController.HistoryCaptionCount; i++)
        {
            var (x, y) = HistoryCaptionPositions[i];
            _font!.Draw(fb, _text!.Entries[Controller.HistoryCaptionId(i)].Text, x, y, 18, 3, 2);
        }
        var ids = Controller.HistoryTimelineIds;
        if (ids.Count == 0) return;
        int lineY = _text!.Entries[ids[0]].Y;
        foreach (int id in ids)
        {
            var entry = _text.Entries[id];
            var (line, wrapped) = WrapTimeline(entry.Text, entry.X, fb.Width - 5, _font!.Font(entry.FontSize));
            if (line.Length > 0) { _font.Draw(fb, line, entry.X, lineY, entry.FontSize, entry.Align, entry.Colour); lineY += 0x1A; }
            if (wrapped.Length > 0)
            { _font.Draw(fb, wrapped, entry.X + 0x31, lineY, entry.FontSize, entry.Align, entry.Colour); lineY += 0x1A; }
        }
    }

    /// <summary>Wrapping of a time axis line: while the measured width from x exceeds the screen width [ref:1211]
    /// minus 5, it moves the tail FROM the last space INCLUDING it into the buffer and cuts the line before it [ref:1212]
    /// and builds `sprintf(buf, "%s %s", tail, buf)` (the first argument [ref:1213]
    /// of the format is the tail) – the tail goes BEFORE the existing content, so the word order stays,
    /// and at the junction of the older parts a double space appears.
    /// A line without a space cannot be split (the original would then loop forever).</summary>
    public static (string Line, string Wrapped) WrapTimeline(string text, int x, int limit, Ffn font)
    {
        string wrapped = "";
        while (text.Length > 0 && font.Measure(text) + x > limit)
        {
            int space = text.LastIndexOf(' ');
            if (space < 0) break;
            string tail = text[space..];
            wrapped = wrapped.Length == 0 ? tail : $"{tail} {wrapped}";
            text = text[..space];
        }
        return (text, wrapped);
    }

    /// <summary>Page 1 = GENERAL (gnrl), 2 = PERFORMANCE (perf), 3 = MECHANICAL (mech). [ref:1214]</summary>
    public static string ShowcasePageShape(int page) => page switch { 1 => "gnrl", 2 => "perf", _ => "mech" };

    /// <summary>Draws the "car7" logo 3 pixels higher than the shape header gives [ref:1215]
    /// (y = (pos &gt;&gt; 20) - 3, x = pos &amp; 0xFFFFF).</summary>
    public const int ShowcaseLogoDy = -3;
    /// <summary>Entry animation frames (up to 0x23) and 128 Hz timer steps per frame [ref:1216] [ref:3398]
    /// (counts the ticks; the frame advances when the accumulated ticks exceed a multiple of 10). [ref:1217] [ref:3399]</summary>
    public const int ShowcaseFrames = 0x23, ShowcaseTicksPerFrame = 10;
    /// <summary>The "squash" phase of the logo grows by this much on each screen draw until it reaches [ref:1218]
    /// <see cref="IconAnimFull"/> (0x400) – only then does the logo take the full width.</summary>
    public const int ShowcaseLogoStep = 0x1E;
    /// <summary>Level 0x100 (full opacity) – the last animation frame gives this.</summary>
    public const int ShowcaseFullLevel = 0x100;
    const double TimerHz = 128;

    /// <summary>Logo draw level (level = frame * 256 / 0x23; full 0x100 after 0x23 frames). [ref:1219]
    /// Without <see cref="AnimateChrome"/> it is full at once – like the static first frame of the original with the preview panel.
    /// A car change also resets the "squash" phase: it resets when the marker has changed [ref:1220]
    /// and in the port the restart marker is the car object (it resets its own tick counter on a car change). [ref:1221]</summary>
    int ShowcaseLevel(string art)
    {
        if (art != _showcaseKey) { _showcaseKey = art; _showcaseElapsed = 0; _logoPhase = 0; }
        if (!AnimateChrome) return ShowcaseFullLevel;
        int frame = Math.Min((int)(_showcaseElapsed * TimerHz / ShowcaseTicksPerFrame), ShowcaseFrames);
        return frame * 0x100 / ShowcaseFrames;
    }

    /// <summary>Logo width factor in the "squash" phase (<see cref="SquashFactor"/>, the same arithmetic as
    /// the icon – it has bit-for-bit the same instructions). Raises the phase by [ref:1222]
    /// <see cref="ShowcaseLogoStep"/> on each screen draw: the gate "0x28 ticks at 128 Hz" passes [ref:1223]
    /// at once, because the marker is the screen's tick counter, reset on entry and on a car change [ref:1224]
    /// and not the global counter that it is compared with. [ref:1225] [ref:3400]</summary>
    int ShowcaseLogoFactor()
    {
        if (AnimateChrome && _logoPhase < IconAnimFull) _logoPhase += ShowcaseLogoStep;
        if (Controller.Config[DetailOffset] == 0) return IconSquashFull;
        return SquashFactor(_logoPhase);
    }

    /// <summary>On screens with the MAIN art: "side", the four flickering bars [ref:1226]
    /// (table, translucency = bar level, hotspot-relative offset), the title colour (table index Counter / 0x50) [ref:1227]
    /// with the next colour cross-faded in at level (Counter % 0x50) * 255 / 0x50, and "logo". Without [ref:1228]
    /// <see cref="AnimateChrome"/> only side, the first title colour and logo are drawn (no bars).</summary>
    void DrawChrome(FrameBuffer fb)
    {
        // with config+0x12D < 2 it exits right away – then there are no [ref:1229]
        // bars, title, or "side"/"logo".
        if (Controller.Config[DetailOffset] < 2) return;
        var s = Controller.CurrentScreen;
        if (s.Art != "main") return;
        void Draw(string name) { if (Shape("main", name) is { } image) OriginalHud.Blit(fb, image, image.PosX, image.PosY); }
        Draw("side");
        if (!AnimateChrome) { Draw("gren"); Draw("logo"); return; }
        var a = Animation;
        for (int i = 0; i < 4; i++)
        {
            if (a.Level[i] == 0) continue;
            string name = MenuArtConstants.BarShapeNames[i * 2 + a.Variant[i]];
            if (Shape("main", name) is { } bar)
                BlitLevel(fb, bar, a.OffsetX[i] - bar.CenterX, a.OffsetY[i] - bar.CenterY, a.Level[i]);
        }
        string TitleName(int index) => MenuArtConstants.TitleShapeNames[index % MenuAnimation.TitleCount];
        Draw(TitleName(a.TitleIndex));
        if (Shape("main", TitleName(a.TitleIndex + 1)) is { } next) BlitLevel(fb, next, next.PosX, next.PosY, a.TitleFade);
        Draw("logo");
    }

    /// <summary>With a level below 0xFF: palette (colour and alpha) scaled by level / 256 and blended onto the [ref:1230]
    /// frame as premultiplied alpha (out = src + dst * (255 - alpha) / 256); transparent pixels stay untouched. [ref:1231]</summary>
    public static void BlitLevel(FrameBuffer fb, FshImage image, int x, int y, int level, int clipLeft = 0)
    {
        if (level <= 0) return;
        if (level >= 0xFF)
        {
            for (int sy = 0; sy < image.Height; sy++)
                for (int sx = 0; sx < image.Width; sx++)
                {
                    int px = x + sx, py = y + sy, o = (sy * image.Width + sx) * 4;
                    if (px < clipLeft || (uint)px >= fb.Width || (uint)py >= fb.Height || image.Rgba[o + 3] == 0) continue;
                    fb.Set(px, py, (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]));
                }
            return;
        }
        int alpha = 255 * level >> 8;
        for (int sy = 0; sy < image.Height; sy++)
            for (int sx = 0; sx < image.Width; sx++)
            {
                int px = x + sx, py = y + sy, o = (sy * image.Width + sx) * 4;
                if (px < clipLeft || (uint)px >= fb.Width || (uint)py >= fb.Height || image.Rgba[o + 3] == 0) continue;
                uint dst = fb.Get(px, py);
                int r = (image.Rgba[o] * level >> 8) + ((int)(dst >> 16 & 255) * (255 - alpha) >> 8);
                int g = (image.Rgba[o + 1] * level >> 8) + ((int)(dst >> 8 & 255) * (255 - alpha) >> 8);
                int b = (image.Rgba[o + 2] * level >> 8) + ((int)(dst & 255) * (255 - alpha) >> 8);
                fb.Set(px, py, (uint)(Math.Min(r, 255) << 16 | Math.Min(g, 255) << 8 | Math.Min(b, 255)));
            }
    }

    /// <summary>Shape scaled to <paramref name="w"/> x <paramref name="h"/> at (x, y) [ref:1232]
    /// nearest-neighbour sampling. Source step in 16.16 = <c>(source &lt;&lt; 16) / target</c> (division, truncated to zero),
    /// the first sample is half a step, and with a negative target (mirror) the step does not change sign, only [ref:1233]
    /// the start shifts by the whole source – the samples then run from the right edge of the row. Palette level [ref:1234]
    /// clipping to the frame as in <see cref="BlitLevel"/> (the original has its own clipping window). [ref:1235]</summary>
    public static void BlitScaled(FrameBuffer fb, FshImage image, int x, int y, int w, int h, int level)
    {
        if (level <= 0 || w == 0 || h == 0) return;
        int stepX = (image.Width << 16) / w, stepY = (image.Height << 16) / h;
        int startX = stepX / 2 + (w < 0 ? image.Width << 16 : 0), startY = stepY / 2 + (h < 0 ? image.Height << 16 : 0);
        int dw = Math.Abs(w), dh = Math.Abs(h), alpha = 255 * level >> 8;
        for (int dy = 0; dy < dh; dy++)
        {
            int srcY = (startY + dy * stepY) >> 16;
            if ((uint)srcY >= (uint)image.Height) continue;
            for (int dx = 0; dx < dw; dx++)
            {
                int srcX = (startX + dx * stepX) >> 16;
                if ((uint)srcX >= (uint)image.Width) continue;
                int px = x + dx, py = y + dy, o = (srcY * image.Width + srcX) * 4;
                if ((uint)px >= fb.Width || (uint)py >= fb.Height || image.Rgba[o + 3] == 0) continue;
                if (level >= 0xFF)
                {
                    fb.Set(px, py, (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]));
                    continue;
                }
                uint dst = fb.Get(px, py);
                int r = (image.Rgba[o] * level >> 8) + ((int)(dst >> 16 & 255) * (255 - alpha) >> 8);
                int g = (image.Rgba[o + 1] * level >> 8) + ((int)(dst >> 8 & 255) * (255 - alpha) >> 8);
                int b = (image.Rgba[o + 2] * level >> 8) + ((int)(dst & 255) * (255 - alpha) >> 8);
                fb.Set(px, py, (uint)(Math.Min(r, 255) << 16 | Math.Min(g, 255) << 8 | Math.Min(b, 255)));
            }
        }
    }

    static void FillRect(FrameBuffer fb, int x, int y, int w, int h, uint rgb)
    {
        for (int yy = Math.Max(y, 0); yy < Math.Min(y + h, fb.Height); yy++)
            for (int xx = Math.Max(x, 0); xx < Math.Min(x + w, fb.Width); xx++) fb.Set(xx, yy, rgb);
    }

    /// <summary>action 0xF: colour of the selected car (config +0x21 / +0x3C, index into the HSV table) [ref:1236]
    /// → HSV to RGB → recolour of the key entry (0, 0xD2, 0); 0x54 x 0x15 rectangle at (x+1, y+5) [ref:1237]
    /// over a black shadow at (x+2, y+6).</summary>
    public static uint SwatchColour(int paint)
    {
        paint = Math.Min(paint, CarModel.MaxPaint);
        if ((uint)paint < (uint)SwatchCache.Length && SwatchCache[paint] != uint.MaxValue) return SwatchCache[paint];
        var hsv = CarPaint.Hsv(paint);
        var palette = new byte[1024];
        palette[1] = 0xD2;
        Cockpit.Recolour(palette, hsv);
        uint rgb = (uint)(palette[0] << 16 | palette[1] << 8 | palette[2]);
        if ((uint)paint < (uint)SwatchCache.Length) SwatchCache[paint] = rgb;   // pure function of the paint: the menu asks every frame
        return rgb;
    }
    static readonly uint[] SwatchCache = Enumerable.Repeat(uint.MaxValue, CarModel.MaxPaint + 1).ToArray();

    void DrawSwatch(FrameBuffer fb, MenuItem item, int x, int y)
    {
        var cfg = Controller.Config;
        int car = item.ValueRaw == 1 ? cfg.P1Car : cfg.P2Car;
        if (Controller.ModCarOf(car) is { } modCar)
        {
            FillRect(fb, x + 2, y + 6, 0x54, 0x15, 0);
            FillRect(fb, x + 1, y + 5, 0x54, 0x15, ModSwatchColour(modCar, item.ValueRaw == 1 ? 0 : 1));
            return;
        }
        int paint = cfg[(item.ValueRaw == 1 ? 0x21 : 0x3C) + Math.Min(car, 14)];
        FillRect(fb, x + 2, y + 6, 0x54, 0x15, 0);
        FillRect(fb, x + 1, y + 5, 0x54, 0x15, SwatchColour(paint));
    }

    /// <summary>Handler 14, (screen): three volume rows (config +0x5C menu music, +0x5D racing music, +0x5E [ref:1238]
    /// sound effects). Per row at dy = 6 / 0x21 / 0x3C and dx = -0x17 (shapes with the hotspot = -position, so drawn at position + d):
    /// track "audi", bar "abar" shifted to dx = volume * 0x8C / 0x7F - 0xA3 and clipped to x &gt;= 0x188, arrows "laud" (always full)
    /// and "raud". The row of the selected item is drawn at full level, the others at level 0x8C. The pressed-arrow shapes
    /// laul / raul belong to the mouse and are not drawn.</summary>
    void DrawVolumeSliders(FrameBuffer fb)
    {
        int selected = Controller.SelectedIndex;
        int[] rowY = [6, 0x21, 0x3C];
        void Put(string name, int row, int dx, int level, int clipLeft = 0)
        {
            if (Shape("main", name) is not { } image) return;
            BlitLevel(fb, image, dx + image.PosX, rowY[row] + image.PosY, level, clipLeft);
        }
        int Level(int row) => row == selected ? 0x100 : 0x8C;
        for (int row = 0; row < 3; row++) Put("audi", row, -0x17, Level(row));
        for (int row = 0; row < 3; row++)
            Put("abar", row, VolumeBarOffset(Controller.Config[0x5C + row]), Level(row), SliderClipLeft);
        for (int row = 0; row < 3; row++) { Put("laud", row, -0x17, 0x100); Put("raud", row, -0x17, Level(row)); }
    }

    /// <summary>Bar shift: volume (0..0x7F) * 0x8C / 0x7F, minus 0xA3. [ref:1239]</summary>
    public static int VolumeBarOffset(int volume) => Math.Min(volume, 0x7F) * 0x8C / 0x7F - 0xA3;
    public const int SliderClipLeft = 0x188;

    /// <summary>Draws the text items of the current screen with their values, markers and controls. [ref:1240]</summary>
    void DrawOriginalItems(FrameBuffer fb)
    {
        var screen = Controller.CurrentScreen;
        var items = screen.Items;
        int disabledColour = FrontEndConstants.DisabledColour;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool selected = i == Controller.SelectedIndex, disabled = (item.Flags & 1) != 0;
            if (item.Kind == 1) { DrawShapeItem(fb, screen, item, selected); continue; }
            if (i is >= 6 and <= 20 && Controller.InShowcase && Controller.ShowcaseModCar != null) continue;   // the stats rows of the original cars
            if (item.Kind is not (0 or 2) || item.TextId >= _text!.Entries.Count) continue;
            var entry = _text.Entries[item.TextId];
            int colour = disabled ? disabledColour : selected ? entry.HighlightColour : entry.Colour;
            int size = entry.FontSize, valueOffset = size < 18 ? 0xD : 0x13, markerOffset = size < 18 ? 8 : 0xC;
            _font!.Draw(fb, entry.Text, entry.X, entry.Y, size, entry.Align, colour);
            if ((item.Flags & 4) != 0) _font.Draw(fb, ":", entry.X + markerOffset, entry.Y, size, entry.Align, colour);
            if ((item.Flags & 2) != 0) _font.Draw(fb, "...", entry.X + valueOffset, entry.Y, size, entry.Align, colour);
            if (item.Kind != 2) continue;
            if (item.Action == 15) { DrawSwatch(fb, item, entry.X + valueOffset, entry.Y); continue; }
            if (item.Action == 11) { DrawKeyBinding(fb, item, entry, selected, entry.X + valueOffset); continue; }
            if (item.Action == 0xC) { DrawSplitKeyBinding(fb, item, entry, selected, entry.X + valueOffset); continue; }
            var (value, valueEntry) = ValueOf(item);
            if (value == null) continue;
            int vColour = disabled ? disabledColour : selected ? valueEntry.HighlightColour : valueEntry.Colour;
            _font.Draw(fb, value, entry.X + valueOffset, entry.Y, size, valueEntry.Align < 3 ? 0 : 3, vColour);
        }
    }

    /// <summary>Strip of configuration icons in the right column of the main menu and type 2 screens [ref:1241]
    /// Called after (for the main screen, for setup). Rows 0..5 are game type, race type [ref:1242]
    /// track, player 1 car, player 2 car (two-player game only) and opponent car; row 5 drops out when
    /// there are no opponents and the race is a normal one. The row lit by the main menu selection (position 0 →
    /// row 0, 1..4 → +1, none beyond) pulses: 0x28 ticks at 128 Hz after a value change gives a step of 0x3C per frame
    /// from 0 to 0x400, and the name variant is "…5" (bright) when anim &lt;= 0x100 or &gt;= 0x300, and "…d" (dark) between.
    /// The highlighted icon also pulses in width ("squash"): the factor comes from <see cref="SquashFactor"/>, and at
    /// anim &lt; 0x400 the icon is drawn scaled (<see cref="BlitScaled"/>), from full width [ref:1243]
    /// through zero, to a mirror image and back. The gate flag [ref:1244]
    /// (set in the icon animation step, cleared on entry to this function): in the main menu loop [ref:1245]
    /// and setup, a frame iteration with the icon step skips it and forces a redraw. Drawing [ref:1246]
    /// and icon stepping run in the port every frame, as in the original loop; only the bar and title clock changes:
    /// <see cref="Advance(double, bool, bool, bool)"/> holds it while <see cref="IconStepPending"/>.</summary>
    void DrawConfigIcons(FrameBuffer fb)
    {
        var screen = Controller.CurrentScreen;
        _iconStepped = false;                        // = 0 on entry [ref:1247]
        if (screen.Type is not (1 or 2)) return;     // (audio, type 14) draws no strip [ref:1248]
        var cfg = Controller.Config;
        int ticks = (int)_iconTicks;
        int selection = Controller.MainSelection;
        int lit = selection switch { 0 => 0, >= 1 and <= 4 => selection + 1, _ => -1 };
        var values = _iconScratch;
        for (int row = 0; row < IconRowCount; row++) values[row] = (ushort)IconValue(cfg, row);
        string art = ArtName(screen);
        for (int row = 0; row < IconRowCount; row++)
        {
            if (row == 5 && cfg.OpponentCount == 0 && cfg.RaceType == 0) break;
            if (row == 4 && cfg.GameType != 1) continue;
            bool highlighted = row == lit || (row == 1 && lit == 0);
            if (highlighted)
            {
                if (_iconSel != selection) { _iconSel = selection; _iconAnim = IconAnimFull; }
                if (!values.AsSpan().SequenceEqual(_iconValues)) { _iconChangeTicks = ticks; _iconAnim = 0; }
                if (row != 1 && ticks - _iconChangeTicks > IconChangeTicks && _iconAnim < IconAnimFull)
                {
                    _iconAnim += IconAnimStep;
                    _iconStepped = true;             // = 1 [ref:1249]
                }
            }
            bool bright = highlighted && (_iconAnim <= IconLitFrom || _iconAnim >= IconLitTo);
            string name = IconName(row, values[row], bright);
            if (name.Length == 0 || Shape(art, name) is not { } image) continue;
            int x = image.PosX + (row < 2 ? IconDxFirst : IconDxRest);
            int y = image.PosY + FrontEndConstants.IconYOffsets[row];
            int squash = highlighted ? SquashFactor(_iconAnim) : IconSquashFull;
            if (squash == IconSquashFull) { BlitLevel(fb, image, x, y, IconLevel); continue; }
            // width = factor * icon width / 64 (truncated toward zero, 16 bit), drawn from [ref:1250]
            // the icon centre (x + width/2 - |width/2|); height unchanged. [ref:1251]
            int w = (short)(squash * image.Width / 64), half = w / 2;
            if (half < 1) half = -half;
            BlitScaled(fb, image, x + image.Width / 2 - half, y, w, image.Height, IconLevel);
        }
        values.CopyTo(_iconValues, 0);
    }

    /// <summary>Value shown by an icon row: game type, race type, track (0 in knockout) [ref:1252]
    /// player 1 car, player 2 car and opponent – in knockout 0xF, in a tournament the class from table[track] + 0x10 [ref:1253]
    /// (cars A/B/C), and in a normal race the opponent car + 4 when it names a class (&gt; 0xB). The original does not clamp
    /// the track index: the name table row has 22 columns, so track 8 hits NULL and nothing is drawn.</summary>
    int IconValue(FeConfig cfg, int row) => row switch
    {
        0 => cfg.GameType == FeConfig.NetworkGameType ? 4 : cfg.GameType,   // icon table keeps the original order (4 = network)
        1 => cfg.RaceType,
        2 => cfg.RaceType == 2 ? 0 : cfg.Track,
        3 => cfg.P1Car >= NFS.Game.Cars.CarCatalog.VanillaCount ? -1 : cfg.P1Car,   // a mod car has no icon (-1 = none)
        4 => cfg.P2Car >= NFS.Game.Cars.CarCatalog.VanillaCount ? -1 : cfg.P2Car,
        5 => cfg.RaceType switch
        {
            2 => 0xF,
            1 => FrontEndConstants.TournamentOpponentClasses[cfg.Track] + 0x10,
            _ => cfg.OpponentCar >= NFS.Game.Cars.CarCatalog.VanillaCount || NFS.Game.Cars.CarRaceClasses.FromOpponentValue(cfg.OpponentCar) != null ? -1   // a mod car and a class D..G have no icon
                : cfg.OpponentCar > 0xB ? cfg.OpponentCar + 4 : cfg.OpponentCar,
        },
        _ => 0,
    };

    /// <summary>(squash of the highlighted icon): width factor from the animation phase 0..0x400. [ref:1254]
    /// Phase &gt;= 0x400 = 0x40 (64/64, full width – the original takes its normal path then). Otherwise the byte [ref:1255]
    /// <c>c = (phase + 0x100) &gt;&gt; 8</c> (1..4) selects the quadrant: bit 0 = mirrored addressing
    /// (<c>idx = 0x100 - (phase &amp; 0xFF)</c>; with the entry [0x100] = 0x10000, phase 0 gives full width),
    /// bit 1 = negation of the table value; result = <c>sin[idx] / 1024</c> truncated toward zero. [ref:1256]</summary>
    public static int SquashFactor(int anim)
    {
        if (anim >= IconAnimFull) return IconSquashFull;
        int c = ((anim + 0x100) >> 8) & 0xFF;
        int index = (c & 1) != 0 ? 0x100 - (anim & 0xFF) : anim & 0xFF;
        int v = MathConstants.SinQuarter[index];
        return (c & 2) != 0 ? -v / 1024 : v / 1024;
    }

    /// <summary>Case-insensitive (art, shape) key without building a joined string per lookup.</summary>
    sealed class ShapeKeyComparer : IEqualityComparer<(string Art, string Shape)>
    {
        public bool Equals((string Art, string Shape) a, (string Art, string Shape) b) =>
            string.Equals(a.Art, b.Art, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Shape, b.Shape, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Art, string Shape) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Art), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Shape));
    }

    readonly Dictionary<int, string> _iconNames = new();

    /// <summary>Memoised <see cref="BuildIconName"/> (the name only depends on its arguments and the language).</summary>
    string IconName(int row, int value, bool bright)
    {
        int key = ((row * 0x10000 + value) << 1 | (bright ? 1 : 0)) * 256 + Controller.Config.Language;
        if (!_iconNames.TryGetValue(key, out var name)) _iconNames[key] = name = BuildIconName(row, value, bright);
        return name;
    }

    /// <summary>Shape name of an icon: name table row = min(row, 3), column = value; [ref:1257]
    /// it makes "%s5"/"%sd"; for language 2 six French substitutions apply (pairs from the table), and names [ref:1258]
    /// 2-character names (A5/Ad/B5/Bd/C5/Cd) get two padding spaces, because MAIN.QFS stores them as 4 characters. [ref:1259]</summary>
    string BuildIconName(int row, int value, bool bright)
    {
        if (value is < 0 || value >= IconRowStride) return "";
        string baseName = MenuArtConstants.IconNames[Math.Min(row, 3) * IconRowStride + value];
        if (baseName.Length == 0) return "";
        string name = baseName + (bright ? MenuArtConstants.IconLightFormat : MenuArtConstants.IconDarkFormat)[2..];
        if (Controller.Config.Language == 2)
            foreach (var (from, to) in MenuArtConstants.IconFrenchPairs)
                if (name == from) { name = to; break; }
        return name.Length == 2 ? name + MenuArtConstants.IconPadding : name;
    }

    /// <summary>Action 0xB: TEXT 0x11D "keyboard" in the value column, and the key name at a gap of 0x42 [ref:1260]
    /// (0x50 for font 18); no binding = "________" (TEXT 0x133). Colour as for the item (selected = highlight colour).</summary>
    void DrawKeyBinding(FrameBuffer fb, MenuItem item, TextEntry entry, bool selected, int x)
    {
        int colour = selected ? entry.HighlightColour : entry.Colour, size = entry.FontSize;
        if (Controller.JoyText(item) is { } joy) { DrawJoystickBinding(fb, joy, x, entry.Y, size, colour); return; }
        var (device, name) = Controller.KeyText(item);
        if (device == null) { _font!.Draw(fb, name, x, entry.Y, size, 3, colour); return; }
        _font!.Draw(fb, device, x, entry.Y, size, 3, colour);
        _font.Draw(fb, name, x + (size == 18 ? 0x50 : 0x42), entry.Y, size, 3, colour);
    }

    /// <summary>Joystick binding (device 1/2): "joystick" in the value column and its number after it (by the text width [ref:1261]
    /// and a gap of 6, 10 for font 18), then at a gap of 0x42 (0x50) "axis" / "button" and the axis number or button letter, the same way.</summary>
    void DrawJoystickBinding(FrameBuffer fb, JoyLabel joy, int x, int y, int size, int colour)
    {
        int gap = size == 18 ? 10 : 6, second = x + (size == 18 ? 0x50 : 0x42);
        var font = _font!.Font(size);
        _font.Draw(fb, joy.Joystick, x, y, size, 3, colour);
        _font.Draw(fb, joy.JoystickNumber, x + font.Measure(joy.Joystick) + gap, y, size, 3, colour);
        _font.Draw(fb, joy.Kind, second, y, size, 3, colour);
        _font.Draw(fb, joy.KindValue, second + font.Measure(joy.Kind) + gap, y, size, 3, colour);
    }

    /// <summary>Action 0xC: two rows (player 1 in the value column, player 2 at x + 0x8A); on the selected item the column [ref:1262]
    /// of the active player (config +0x08) is highlighted; the other one has the normal colour.</summary>
    void DrawSplitKeyBinding(FrameBuffer fb, MenuItem item, TextEntry entry, bool selected, int x)
    {
        int size = entry.FontSize;
        for (int player = 0; player < 2; player++)
        {
            int colour = selected && player == Controller.SplitColumn ? entry.HighlightColour : entry.Colour;
            int px = x + player * 0x8A;
            if (Controller.JoyText(item, player) is { } joy) { DrawJoystickBinding(fb, joy, px, entry.Y, size, colour); continue; }
            var (device, name) = Controller.KeyText(item, player);
            if (device == null) { _font!.Draw(fb, name, px, entry.Y, size, 3, colour); continue; }
            _font!.Draw(fb, device, px, entry.Y, size, 3, colour);
            _font.Draw(fb, name, px + (size == 18 ? 0x50 : 0x42), entry.Y, size, 3, colour);
        }
    }

    /// <summary>After the items, draws TEXT 0x134 "press ENTER to re-map" in state 1 (highlight colour) [ref:1263]
    /// at x = 0x16B (300 for language 2) and y = 0x4D (<c>(0x134, x, 0x4D, 1)</c>). [ref:1264]</summary>
    void DrawSplitControlsHint(FrameBuffer fb)
    {
        const int id = 0x134;
        if (id >= _text!.Entries.Count) return;
        var entry = _text.Entries[id];
        _font!.Draw(fb, _text[id], Controller.Config.Language == 2 ? 300 : 0x16B, 0x4D, entry.FontSize, entry.Align, entry.HighlightColour);
    }

    /// <summary>Player name editor (called with two strings – the heading "Player n: enter name" and the name): frame from [ref:1265]
    /// <see cref="DrawDialogFrame"/>, and the cursor "_" goes to the right end of the name centred at x = 0x140. [ref:1266]</summary>
    void DrawNameEditor(FrameBuffer fb)
    {
        if (Controller.NameEditing is not { } editor) return;
        LoadFont();
        if (_font == null) return;
        int y = DrawDialogFrame(fb, Controller.NamePrompt, editor.Text);
        if (editor.CursorVisible) _font!.Draw(fb, "_", fb.Width / 2 + _font.Font(18).Measure(editor.Text) / 2, y, 18, 0, 2);
    }

    /// <summary>While capturing, TEXT 0x132 "enter new control" centred at x = 0x19A. [ref:1267]</summary>
    void DrawCapturePrompt(FrameBuffer fb)
    {
        if (!Controller.Capturing || 0x132 >= _text!.Entries.Count) return;
        var entry = _text.Entries[0x132];
        string text = _text[0x132];
        _font!.Draw(fb, text, 0x19A - _font.Font(entry.FontSize).Measure(text) / 2, entry.Y, entry.FontSize, 3, entry.Colour);
    }

    /// <summary>Location of MOUSE.FSH in ART (the cursor – the same file also holds the "dbar" and "dlog" window shapes). [ref:1268]</summary>
    const string DialogArt = "MOUSE";

    /// <summary>Front-end modal window (<see cref="FrontEndDialog"/>): frame for a message / question / name field, and for a list [ref:1269]
    /// a side panel (sliding in when <see cref="AnimateChrome"/> is set). [ref:1270]</summary>
    void DrawDialog(FrameBuffer fb)
    {
        if (Controller.Dialog is not { } dialog) return;
        LoadFont();
        if (_font == null) return;
        if (dialog.Kind == DialogKind.List) { DrawListDialog(fb, dialog); return; }
        string head = dialog.Kind switch { DialogKind.Message => "", _ => _text![dialog.TextId] };
        string body = dialog.Kind switch { DialogKind.Message => _text![dialog.TextId], DialogKind.Text => dialog.Entry, _ => "" };
        int y = DrawDialogFrame(fb, head, body);
        if (dialog.Kind == DialogKind.Confirm)
        {
            // "yes" at x = 0x118, "no" at x = 0x168, colour 2 when selected, otherwise 1; "no" selected by default. [ref:1271]
            _font.Draw(fb, _text![0x2F6], 0x118, y, 18, 2, dialog.Yes ? 2 : 1);
            _font.Draw(fb, _text[0x2F7], 0x168, y, 18, 2, dialog.Yes ? 1 : 2);
        }
        else if (dialog.Kind == DialogKind.Text && dialog.CursorVisible)
            // cursor "_" at the right end of the centred text (x = 0x140 + width / 2). [ref:1272]
            _font.Draw(fb, "_", fb.Width / 2 + _font.Font(18).Measure(dialog.Entry) / 2, y, 18, 0, 2);
    }

    /// <summary>For two strings: a semi-transparent dark rectangle (<c>0xFF080808</c>, alpha 0xB4) of the width [ref:1273]
    /// of the longest string (heading + 0x3C) + 0x1E and height 0x3C, centred on the screen; the "dbar" bar (clipped from x + 0x14)
    /// on the right, the "dlog" logo on the left, the heading on the bar (y − 0x19, colour 0), the body at y + 0xF (colour 2). Returns the y of the body.</summary>
    int DrawDialogFrame(FrameBuffer fb, string head, string body)
    {
        var font = _font!.Font(18);
        int widest = Math.Max(font.Measure(head) + 0x3C, font.Measure(body));   // heading always + 0x3C (room for the logo) [ref:1274]
        int width = widest + 0x1E, height = 0x3C, x0 = (fb.Width - width) / 2, y0 = (fb.Height - height) / 2;
        Dim(fb, x0, y0, x0 + width, y0 + height, 0xB4);
        if (Shape(DialogArt, "dbar") is { } bar) BlitLevel(fb, bar, x0 + width - 0x13B, y0 - 0x1F, 0xFF, x0 + 0x14);
        // draws the shape at (x − centre X, y − centre Y); the centre of "dlog" is (−112, −103). [ref:1275]
        if (Shape(DialogArt, "dlog") is { } logo) OriginalHud.Blit(fb, logo, x0 - 0xA4 - logo.CenterX, y0 - 0x8E - logo.CenterY);
        if (head.Length > 0) _font.Draw(fb, head, x0 + 0x3C + (widest - 0x1E) / 2, y0 - 0x19, 18, 2, 0);
        if (body.Length > 0) _font.Draw(fb, body, x0 + width / 2, y0 + 0xF, 18, 2, 2);
        return y0 + 0xF;
    }

    /// <summary>The darkening of the dialog box: every pixel of the rectangle (clipped to the buffer) goes towards [ref:1276]
    /// <c>0x080808</c> by <paramref name="alpha"/> / 255 (the original uses 0xB4).</summary>
    public static void Dim(FrameBuffer fb, int x0, int y0, int x1, int y1, byte alpha)
    {
        static uint Mix(uint v, uint alpha) => (v * (255 - alpha) + 0x08u * alpha) / 255;
        for (int y = Math.Max(0, y0); y < Math.Min(fb.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(fb.Width, x1); x++)
            {
                uint c = fb.Get(x, y);
                fb.Set(x, y, Mix(c >> 16 & 0xFF, alpha) << 16 | Mix(c >> 8 & 0xFF, alpha) << 8 | Mix(c & 0xFF, alpha));
            }
    }

    /// <summary>The game's fonts (SWISS13 / 16 / 18 / 36 of the front end); null when they cannot be loaded.</summary>
    public FrontEndFont? Fonts
    {
        get { LoadFont(); return _font; }
    }

    /// <summary>The background of the network menu screens, in the order of the original "game setup" screen: MAIN.QFS
    /// <c>blk4</c>, <c>side</c>, <c>gren</c> and <c>logo</c> (no animated bars, no icons of the right column, whatever the detail
    /// level). False when the fonts or the picture are missing: nothing is guaranteed drawn then (the caller falls back).</summary>
    public bool DrawNetworkBackdrop(FrameBuffer fb)
    {
        if (!LoadFont() || Shape("main", "blk4") is not { } back) return false;
        fb.Clear(0, 0, 0);
        OriginalHud.Blit(fb, back, 0, 0);
        if (Shape("main", "side") is { } side) OriginalHud.Blit(fb, side, 0, 0);
        if (Shape("main", "gren") is { } gren) OriginalHud.Blit(fb, gren, 34, 22);
        if (Shape("main", "logo") is { } logo) OriginalHud.Blit(fb, logo, 3, 14);
        return true;
    }

    /// <summary>The dialog window of the game (<see cref="DrawDialogFrame"/>) with a head on the bar and a body; false without fonts.</summary>
    public bool DrawNetworkDialog(FrameBuffer fb, string head, string body)
    {
        if (!LoadFont()) return false;
        DrawDialogFrame(fb, head, body);
        return true;
    }

    /// <summary>Current frame of the list panel slide-in; null without <see cref="AnimateChrome"/>, without the panel shape, or after it has finished.</summary>
    FrontEndDialog.ListIntroFrame? ListIntroFrame()
    {
        if (!AnimateChrome || Shape("main", "bsld") is not { } panel) return null;
        var frames = FrontEndDialog.ListIntroFrames(panel.Width);
        int index = (int)(_dialogElapsed / SlideStepSeconds);
        return index < frames.Count ? frames[index] : null;
    }

    /// <summary>Slide-out on Enter/Esc: panel, rows (colour 0), the "tsld" tab and arrows move right [ref:1277]
    /// by <see cref="FrontEndDialog.ExitStep"/> per frame; no "cancel" text and no selection.</summary>
    void DrawListExit(FrameBuffer fb, FrontEndDialog dialog, FshImage panel)
    {
        var frames = FrontEndDialog.ListExitFrames();
        int dx = frames[Math.Min((int)(_exitElapsed / SlideStepSeconds), frames.Count - 1)];
        OriginalHud.Blit(fb, panel, panel.PosX + dx, panel.PosY);
        for (int i = 0; i < FrontEndDialog.VisibleRows && dialog.Scroll + i < dialog.Items.Count; i++)
            _font!.Draw(fb, dialog.Items[dialog.Scroll + i], 0x1E8 + dx, 0x62 + i * 0x16, 18, 0, 0);
        if (Shape("main", "tsld") is { } tab) OriginalHud.Blit(fb, tab, tab.PosX + dx, tab.PosY);
        if (dialog.CanScrollUp && Shape("main", "uppd") is { } up) OriginalHud.Blit(fb, up, up.PosX + 2 + dx, up.PosY);
        if (dialog.CanScrollDown && Shape("main", "dwnd") is { } down) OriginalHud.Blit(fb, down, down.PosX + 2 + dx, down.PosY);
    }

    /// <summary>The "bsld" panel at its place (438, 83), up to 8 rows from x = 0x1E8 [ref:1278]
    /// y = 0x62 + 0x16·i (colour 2 when selected), the "tsld" tab (596, 80) with the text "cancel" (TEXT 0x74) laid out
    /// vertically (one character per row, x = 0x26C, step <see cref="FrontEndDialog.VerticalLetterStep"/>) and arrows [ref:1279]
    /// "uppd" / "dwnd" when scrolling. With <see cref="AnimateChrome"/> the panel with the rows slides in first, then the tab
    /// (<see cref="FrontEndDialog.ListIntroFrames"/>); the rows have colour 0 and no selection in the meantime.</summary>
    void DrawListDialog(FrameBuffer fb, FrontEndDialog dialog)
    {
        var panel = Shape("main", "bsld");
        if (dialog.Done && _exitStarted && panel != null) { DrawListExit(fb, dialog, panel); return; }
        var frame = ListIntroFrame();
        bool intro = frame != null;
        int panelDx = frame?.PanelOffset ?? 0;
        if (panel != null) OriginalHud.Blit(fb, panel, panel.PosX + panelDx, panel.PosY);
        for (int i = 0; i < FrontEndDialog.VisibleRows && dialog.Scroll + i < dialog.Items.Count; i++)
        {
            int index = dialog.Scroll + i;
            _font!.Draw(fb, dialog.Items[index], 0x1E8 + panelDx, 0x62 + i * 0x16, 18, 0, !intro && index == dialog.Selected ? 2 : 0);
        }
        if (frame is { TabOffset: null }) return;
        int tabDx = frame?.TabOffset ?? 0;
        if (Shape("main", "tsld") is { } tab) OriginalHud.Blit(fb, tab, tab.PosX + tabDx, tab.PosY);
        int lang = Math.Min((int)Controller.Config.Language, 5);
        int top = FrontEndConstants.ListCancelTopY[lang];
        string cancel = _text![0x74];
        // step = trunc(font height 0x12 · 0.6) = 23 for SWISS18. [ref:1280]
        int step = FrontEndDialog.VerticalLetterStep(_font!.Font(18).TextHeight);
        for (int k = 0; k < cancel.Length; k++)
            _font!.Draw(fb, cancel[k].ToString(), 0x26C + tabDx, top + k * step, 18, 0, !intro && dialog.Selected == -1 ? 2 : 0);
        if (!intro && dialog.CanScrollUp && Shape("main", "uppd") is { } up) OriginalHud.Blit(fb, up, up.PosX + 2, up.PosY);
        if (dialog.CanScrollDown && Shape("main", "dwnd") is { } down) OriginalHud.Blit(fb, down, down.PosX + 2 + tabDx, down.PosY);
    }

    /// <summary>Kind 1 item: shape name at +4 (normal) / +8 (selected), drawn at its own FSH position (0x2C00).</summary>
    void DrawShapeItem(FrameBuffer fb, MenuScreen screen, MenuItem item, bool selected)
    {
        var shape = MenuConstants.ShapeItems[item.Id];
        if ((shape.Flags & 1) != 0) return;
        string name = selected ? shape.Selected : shape.Normal;
        if (Shape(ArtName(screen), name) is { } image) OriginalHud.Blit(fb, image, image.PosX, image.PosY);
    }

    (string? Text, TextEntry Entry) ValueOf(MenuItem item)
    {
        var defaultEntry = new TextEntry(18, 3, 4, 3, 0, 0, 0, "");
        if (item.ConfigOffset is { } off && item.ValueTextIds.Count > 0)
        {
            if (off is 0x1F or 0x3A or 0x55 && Controller.ModCarName(Controller.Config[off]) is { } modName)
                return (modName, _text!.Entries[item.ValueTextIds[0]]);   // a mod car: its name in the style of the first value
            if (off == 0x55 && Controller.LowClassName(Controller.Config[off]) is { } className)
                return (className, _text!.Entries[item.ValueTextIds[Math.Min(14, item.ValueTextIds.Count - 1)]]);   // class D..G: the style of "class C"
            int index = Math.Min(Controller.Config[off], item.ValueTextIds.Count - 1);
            int id = item.ValueTextIds[index];
            return id < _text!.Entries.Count ? (_text[id], _text.Entries[id]) : (null, defaultEntry);
        }
        if (item.Action == 15) return (null, defaultEntry); // colour swatch not drawn [ref:1281]
        string full = Controller.ItemText(item);
        int colon = full.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 && item.Text != null && full.StartsWith(item.Text, StringComparison.Ordinal)
            ? (full[(colon + 2)..], defaultEntry) : (null, defaultEntry);
    }

    /// <summary>Blanks the panel rectangle, then draws the panel shape in it. [ref:1282]</summary>
    bool DrawPreviewPanel(FrameBuffer fb, string art, string shape, int x, int y)
    {
        FillRect(fb, x, y, PanelWidth, PanelHeight, 0);
        if (Shape(art, shape) is not { } image) return false;
        OriginalHud.Blit(fb, image, x, y);
        return true;
    }

    /// <summary>Size of the car picture panel of the menu (0x118 x 0x80) and where the menu puts it.</summary>
    public const int CarPreviewWidth = PanelWidth, CarPreviewHeight = PanelHeight, CarPreviewX = PanelX, CarPreviewY = PanelY;

    /// <summary>The picture of <paramref name="car"/> (menu model 0..14) of the PLAYER ONE screen at <paramref name="x"/>,
    /// <paramref name="y"/>; false (nothing drawn) without the fonts or the picture.</summary>
    public bool DrawNetworkCarPreview(FrameBuffer fb, int car, int x, int y) =>
        LoadFont() && DrawPreviewPanel(fb, "main", MenuArtConstants.PlayerCarShapeNames[Math.Clamp(car, 0, 14)], x, y);

    /// <summary>Draws the preview panel (shape at 0xD3,0x13C) and the labels / rating text. [ref:1283]</summary>
    void DrawPreview(FrameBuffer fb)
    {
        var s = Controller.CurrentScreen;
        var cfg = Controller.Config;
        string art = ArtName(s);
        int panelX = SlidingPanelX(s.Id, cfg);
        // first blanks the panel rectangle (0x118 x 0x80) at its current x. [ref:1284]
        void Panel(string shape) => DrawPreviewPanel(fb, art, shape, panelX, PanelY);
        switch (s.Id)
        {
            case "location":
            {
                int track = Math.Min((int)cfg.Track, 7);
                Panel(MenuArtConstants.TrackShapeNames[track]);
                // tournament only (race type 1 with a series), bit <track> of the completed-tracks mask. [ref:1285]
                if (cfg.RaceType == 1 && Controller.Tournament is { } series && (series.CompletedMask >> TrackMaskBit[track] & 1) != 0)
                {
                    if (Shape(art, "trnm") is { } badge) OriginalHud.Blit(fb, badge, panelX + 0xB5, PanelY + 0x2D);
                    string word = MenuArtConstants.TournamentBadgeWords[Math.Min((int)cfg.Language, 5)];
                    if (Shape(art, word) is { } label) OriginalHud.Blit(fb, label, panelX + 0xB5, PanelY + 0xA);
                }
                break;
            }
            case "player-one" or "player-two":
            {
                int carNumber = s.Id == "player-one" ? cfg.P1Car : cfg.P2Car;
                if (Controller.ModCarOf(carNumber) is { } modCar)
                {
                    // a mod car: 3D preview in the panel (the graph view of the original cars has no data for it)
                    Panel("");
                    DrawModPreview(fb, modCar, Controller.ModColour(s.Id == "player-one" ? 0 : 1, modCar), panelX, PanelY, PanelWidth, PanelHeight);
                    DrawModPanelTexts(fb, modCar);
                    break;
                }
                int car = Math.Min(carNumber, 14);
                if (cfg[0x31] == 0 || car > 11)
                {
                    Panel(MenuArtConstants.PlayerCarShapeNames[car]);
                    if (cfg[0x31] == 0 && car < 12) DrawRating(fb, car);
                    break;
                }
                Panel("grph");
                int px = panelX;
                // per-car bar values (FrontEndConstants.GraphBar*); bar x = PanelX + 0x1A - (12 - value) * 9.
                (string Shape, int Value, int Dy)[] bars =
                [
                    ("grpg", FrontEndConstants.GraphBarGreen[car], 0xB), ("grpb", FrontEndConstants.GraphBarBlue[car], 0x29),
                    ("grpr", FrontEndConstants.GraphBarRed[car], 0x47), ("grpy", FrontEndConstants.GraphBarYellow[car], 0x65),
                ];
                foreach (var (shape, value, dy) in bars)
                {
                    if (Shape(art, shape) is { } image) OriginalHud.Blit(fb, image, px + 0x1A - (12 - value) * 9, PanelY + dy);
                }
                for (int id = 0x55; id <= 0x58; id++)
                {
                    var e = _text!.Entries[id];
                    _font!.Draw(fb, e.Text, e.X, e.Y, 18, e.Align, e.HighlightColour);
                }
                break;
            }
            case "opponents" when cfg.OpponentCount != 0:
            {
                if (Controller.ModCarOf(cfg.OpponentCar) is { } modOpponent)
                {
                    Panel("");
                    DrawModPreview(fb, modOpponent, Controller.ModColour(0, modOpponent), panelX, PanelY, PanelWidth, PanelHeight);
                    DrawModPanelTexts(fb, modOpponent);
                    break;
                }
                if (NFS.Game.Cars.CarRaceClasses.FromOpponentValue(cfg.OpponentCar) is { } lowClass)
                {
                    // class D..G: the original has no collage or car list for it – preview of the first mod car of the class and the car names
                    var lowCars = Controller.ModCars.Where(m => m.Traits.RaceClass == lowClass).ToList();
                    Panel("");
                    if (lowCars.Count > 0) DrawModPreview(fb, lowCars[0], 0, panelX, PanelY, PanelWidth, PanelHeight);
                    for (int k = 0; k < 4 && k < lowCars.Count; k++)
                        _font!.Draw(fb, lowCars[k].DisplayName, 0xAA, _text!.Entries[0x55 + k].Y, 18, 1, 0);
                    break;
                }
                int car = Math.Min((int)cfg.OpponentCar, 14);
                Panel(MenuArtConstants.OpponentCarShapeNames[car]);
                if (car < 12) DrawRating(fb, car);
                else
                    for (int k = 0; k < 4; k++)
                    {
                        int id = FrontEndConstants.ClassTexts[(car - 12) * 4 + k];
                        _font!.Draw(fb, _text![id], 0xAA, _text.Entries[0x55 + k].Y, 18, 1, 0);
                    }
                break;
            }
        }
    }

    /// <summary>x of the preview panel: 0xD3, or while sliding (after a change of the shown track / car / graph on the [ref:1286]
    /// same screen) -13 + 0x40 per step. A new screen draws it at once. [ref:1287]</summary>
    int SlidingPanelX(string screen, FeConfig cfg)
    {
        string key = screen switch
        {
            "location" => $"{screen}:{cfg.Track}",
            "player-one" => $"{screen}:{cfg.P1Car}:{cfg[0x31]}:{Controller.ModColourOfNumber(0, cfg.P1Car)}",
            "player-two" => $"{screen}:{cfg.P2Car}:{cfg[0x31]}:{Controller.ModColourOfNumber(1, cfg.P2Car)}",
            "opponents" => $"{screen}:{cfg.OpponentCar}:{cfg.OpponentCount != 0}",
            _ => screen
        };
        if (key != _panelKey)
        {
            _slideElapsed = _panelScreen == screen && _panelKey != null ? 0 : double.PositiveInfinity;
            _panelKey = key; _panelScreen = screen;
        }
        if (!AnimateChrome || _slideElapsed == double.PositiveInfinity) return PanelX;
        int step = (int)(_slideElapsed / SlideStepSeconds);
        if (step >= SlideSteps) { _slideElapsed = double.PositiveInfinity; return PanelX; }
        return SlideStart + step * SlideStepPixels;
    }

    void DrawRating(FrameBuffer fb, int car)
    {
        int id = FrontEndConstants.RatingTexts[car];
        _font!.Draw(fb, _text![id], 0xAA, _text.Entries[0x55].Y, 18, 1, 0);
    }

    void DrawMessage(FrameBuffer fb, string message)
    {
        var font = _font!.Font(13);
        int y = 24;
        foreach (string line in WrapLines(message, 330, font))
        {
            _font.Draw(fb, line, 24, y, 13, 3, 4);
            y += 16;
        }
    }

    static IEnumerable<string> WrapLines(string text, int width, Ffn font)
    {
        string line = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (font.Measure(candidate) > width && line.Length > 0) { yield return line; line = word; }
            else line = candidate;
        }
        if (line.Length > 0) yield return line;
    }

    void DrawFallbackItems(FrameBuffer fb, int y, int spacing)
    {
        var items = Controller.CurrentScreen.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Kind != 2) continue;
            bool selected = i == Controller.SelectedIndex;
            string line = (selected ? "= " : "  ") + Controller.ItemText(items[i]);
            uint color = (items[i].Flags & 1) != 0 ? 0x707070u : selected ? 0xFFFF00u : 0xFFFFFFu;
            HudOverlay.DrawText(fb, 24, y, line.Length > 73 ? line[..73] : line, color, scale: 2);
            y += spacing;
        }
    }

    static void Wrapped(FrameBuffer fb, int x, int y, string text, uint color)
    {
        for (int start = 0; start < text.Length && y < 448; start += 73, y += 13)
            HudOverlay.DrawText(fb, x, y, text.Substring(start, Math.Min(73, text.Length - start)), color, scale: 2);
    }
}
