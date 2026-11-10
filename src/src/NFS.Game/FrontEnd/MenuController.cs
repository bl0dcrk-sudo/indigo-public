using NFS.ModApi;
using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Constants;
using NFS.Game.Cars;
using NFS.Game.Mods;
using NFS.Game.Race;
using NFS.Game.Flow;

namespace NFS.Game.FrontEnd;

public enum MenuKey { Up, Down, Left, Right, Enter, Escape }
public enum MenuStatus { Menu, Race, Results, Exit }

/// <summary>UI sound of the front end: slot in FESFX.BNK [ref:1719]
/// (see <see cref="MenuAudio.Slot"/>). 0 = change/OK, 1 = move, 2 = Enter with a target, 3 = Esc, 6 = race start roar.</summary>
public enum MenuSound { Change, Move, Select, Back, StartRace }

/// <summary>Credits list row: text and position with colour corrections, colour/align from the TEXT record; [ref:1720]
/// <see cref="Colon"/> = gold row (colour 4), which in the original additionally gets a ":" glyph (x + 10).</summary>
public readonly record struct CreditsLine(string Text, int X, int Y, int Colour, int Align, bool Colon);

/// <summary>Keyboard front-end. [ref:1721]
/// Screen/item text and option lists come from the constants (tables of the original game) and TEXT.ENG.</summary>
public sealed class MenuController
{
    private readonly MenuTree _tree;
    private readonly string? _gameDir;
    private readonly Stack<(string Screen, int Selection)> _stack = new();
    private readonly Dictionary<string, int> _selection = new();
    private byte[] _backup0, _backup1;
    private readonly byte[] _colours;
    private readonly TextFile _text;
    /// <summary>The menu text of the active language (TEXT.xxx).</summary>
    public TextFile Text => _text;
    private string _screen = "main";
    private bool _trafficDisabled;
    private bool _catchUpDisabled;
    private byte _personalityIndex;
    private int _nextTournamentTrack;
    private readonly int[] _defaultKeyScans;
    private readonly int[][] _defaultSplitScans;
    private int _showcaseCar;
    private int _showcasePage = 1;
    private int _historyCar, _slideCar, _slideFrame;
    private KeyAction? _capture;
    private int _captureBackup;
    /// <summary>Joystick capture state for the current <see cref="_capture"/>; null outside capturing. [ref:1722]</summary>
    private JoyCapture? _joyCapture;
    private FrontEndDialog? _dialog;
    private Action<FrontEndDialog>? _dialogDone;
    private NameEditor? _nameEditor;
    private int _namePlayer;

    public FeConfig Config { get; }
    /// <summary>Scancode of the HUD toggle key (C1_TOGGLEHUD from KEYBOARD.TXT; outside the controls screen).</summary>
    public int ToggleHudScan { get; }
    /// <summary>Key capture for an action is in progress ("enter new control"; the action entry is then empty). [ref:1723]</summary>
    public bool Capturing => _capture != null;
    /// <summary>Front-end modal window (message, yes/no question, list, file name – <see cref="FrontEndDialog"/>); null = none.</summary>
    public FrontEndDialog? Dialog => _dialog;
    /// <summary>Action 0x13 (controls reset) waits for an answer in the TEXT 0x131 dialog (default "no"). [ref:1724]</summary>
    public bool ConfirmingReset => _dialog is { Kind: DialogKind.Confirm, TextId: ResetControlsText };
    /// <summary>"clear records" waits for an answer in the TEXT 0x349 dialog (default "no"). [ref:1725]</summary>
    public bool ConfirmingClearRecords => _dialog is { Kind: DialogKind.Confirm, TextId: ClearRecordsText };
    private const int ResetControlsText = 0x131, ClearRecordsText = 0x349;
    /// <summary>Player name editor before a split-screen start (the port opens it after RACE). [ref:1726]
    /// <see cref="BeginNameEditing"/>); null = no editing in progress.</summary>
    public NameEditor? NameEditing => _nameEditor;
    /// <summary>Player (0/1) whose name <see cref="NameEditing"/> edits.</summary>
    public int NameEditPlayer => _namePlayer;
    /// <summary>Editor header: format "%s %d: %s" from TEXT 0x29D ("Player"), the player number and TEXT 0x2E3 ("enter name"). [ref:1727]</summary>
    public string NamePrompt => $"{_text[PlayerNames.PlayerTextId]} {_namePlayer + 1}: {_text[PlayerNames.PromptTextId]}";
    public GameStateMachine Flow { get; } = new();
    public MenuStatus Status { get; private set; } = MenuStatus.Menu;
    /// <summary>Sound options screen (fe_screen_sound_options); the selected speech (1) and SFX (2) sliders [ref:1728]
    /// play a looped engine roar (slot 7, <see cref="MenuAudio.SetRoar"/>).</summary>
    public const string AudioScreen = "audio";
    /// <summary>Whether the current screen is the sound options with the speech/SFX slider selected (roar loop). [ref:1729]</summary>
    public bool RoarLoopActive => _screen == AudioScreen && SelectedIndex is 1 or 2;
    /// <summary>UI sound to play, for the host; e.g. subscribed to by <see cref="MenuAudio"/>. [ref:1730]</summary>
    public event Action<MenuSound>? Sound;
    public string? LastMessage { get; private set; }
    public RaceSession? Session { get; private set; }
    public RaceResults? Results { get; private set; }
    public ReplayInputSource? PlaybackSource { get; private set; }
    public bool ResultsReplay { get; private set; }
    private RaceSession? _replayReturnSession;
    private RaceResults? _replayReturnResults;
    private string? _replayReturnScreen;
    private int _replayReturnSelection, _replayReturnDriver;
    public KnockoutSeries? Knockout { get; private set; }
    public TournamentSeries? Tournament { get; private set; }
    /// <summary>Split-screen knockout with only one human left (COMMMODE 0, one view): his index (0 / 1) – the host reads that player's keys
    /// (puts them into the player 1 tags); null otherwise. [ref:1731]</summary>
    public int? SoleSplitHuman => Config.GameType == 1 && Knockout is { Humans: 2 } ko && ko.HumanActive(0) != ko.HumanActive(1) ? (ko.HumanActive(0) ? 0 : 1) : null;
    public bool TournamentSummary => Tournament != null && Status == MenuStatus.Results && _screen is "tournament-standings" or "tournament-final";
    public int NextTournamentTrack => _nextTournamentTrack;
    public string TournamentHeading => Tournament?.Finished == true ? "TOURNAMENT FINAL" : $"TOURNAMENT - {Tournament?.CompletedTracks}/7 TRACKS";
    public IReadOnlyList<string> TournamentActions => [Tournament?.Finished == true ? "FINISH" : $"NEXT RACE - {TournamentTrackName(_nextTournamentTrack)}", "RESTART", "MENU", "SAVE"];
    private string TournamentTrackName(int track) => _tree.Screens.SelectMany(s => s.Items)
        .First(i => i.ConfigOffset == 0x10).ValueTexts![track].ToUpperInvariant();
    private const string LoadSaveScreen = "load-save";
    private const string ControlsScreen = "controls-setup";
    /// <summary>Two-player controls screen (type 19): two columns of keys from the table at +0x94 (action 0xC); [ref:1732]
    /// it replaces the controls screen when the game type is split screen. [ref:1733]</summary>
    public const string SplitControlsScreen = "split-controls";
    private int _capturePlayer = -1;
    /// <summary>Column edited on the screen (0 = player 1, 1 = player 2), value of config +0x08, toggled with LEFT/RIGHT [ref:1734]
    /// on items 0..9.</summary>
    public int SplitColumn => Config[0x08] != 0 ? 1 : 0;
    /// <summary>Car showcase screen ("showcase" from the PLAYER ONE/TWO screen, type 7): [ref:1735]
    /// (screen handler). [ref:1736]</summary>
    public const string ShowcaseScreen = "showcase";
    /// <summary>Upper limit (12) – number of cars in the database; the "next car" range (0xB = 11 → 0). [ref:1737]</summary>
    public const int ShowcaseCarCount = 12;
    /// <summary>Car registry; original indices in FeConfig / records are converted through <see cref="LegacyIndex"/>.</summary>
    public CarCatalog Cars
    {
        get => _cars;
        set { _cars = value; _modCars = [.. value.ModCars]; SanitizeCarSelection(); }
    }
    private CarCatalog _cars = CarCatalog.VanillaTables;
    private IReadOnlyList<CarDefinition> _modCars = [];
    /// <summary>Mod cars in the catalog order = the order after the original cars in every car list of the menu (openspec modding-api-cars D1/4.4).</summary>
    public IReadOnlyList<CarDefinition> ModCars => _modCars;
    /// <summary>The mod car with in-memory number <paramref name="number"/> (0x35..), null for an original car or an unknown number.</summary>
    public CarDefinition? ModCarOf(int number)
        => number >= CarCatalog.VanillaCount && Cars.TryGetByNumber(number, out var car) && car.LegacyIndex is null ? car : null;
    /// <summary>Classes D..G that have at least one mod car (the others stay hidden in the opponent list); empty without mods.</summary>
    public IReadOnlyList<CarRaceClass> VisibleLowClasses => _modCars.Count == 0 ? [] : CarRaceClasses.Present(Cars);

    /// <summary>Opponent value (config +0x55) that stands for a class or "no opponents" (12..15) and not for one car.</summary>
    public static bool IsOpponentClass(int value) => value is >= 12 and < CarCatalog.VanillaCount;

    /// <summary>Colour index (into car.json "colors") chosen for a mod car by player 0 / 1; 0 when none was chosen.
    /// The original config has no room for it, so the menu keeps it per car key; the settings file persists it.</summary>
    public int ModColour(int player, CarDefinition car)
        => (player == 0 ? P1ModColours : P2ModColours).TryGetValue(car.Key.Value, out int i) ? Math.Clamp(i, 0, Math.Max(car.Colors.Count - 1, 0)) : 0;
    /// <summary>Per car key colour choices of player 1 / 2 for mod cars.</summary>
    public Dictionary<string, int> P1ModColours { get; } = [];
    public Dictionary<string, int> P2ModColours { get; } = [];
    /// <summary>Colour index of the mod car with number <paramref name="number"/> for player 0 / 1.</summary>
    public int ModColourOfNumber(int player, int number) => ModCarOf(number) is { } car ? ModColour(player, car) : 0;

    /// <summary>Language of the texts taken from car.json: the six game languages have no Polish, so English (falls back to "pl").</summary>
    public static string ModTextLanguage => "en";
    /// <summary>Description of a mod car from car.json in <see cref="ModTextLanguage"/> (empty = none).</summary>
    public static string ModDescription(CarDefinition car)
        => car.Descriptions.GetValueOrDefault(ModTextLanguage) ?? car.Descriptions.GetValueOrDefault("pl") ?? "";

    /// <summary>The mod cars may have changed (catalog set, settings applied): a selected mod number that is not in the catalog (the mod was
    /// removed) falls back to the first original car / the class opponent of a fresh config.</summary>
    public void SanitizeCarSelection()
    {
        if (Config.P1Car >= CarCatalog.VanillaCount && ModCarOf(Config.P1Car) is null) Config.P1Car = 0;
        if (Config.P2Car >= CarCatalog.VanillaCount && ModCarOf(Config.P2Car) is null) Config.P2Car = 0;
        if (Config.OpponentCar >= CarCatalog.VanillaCount && ModCarOf(Config.OpponentCar) is null) Config.OpponentCar = 12;
        if (Config.OpponentCar is > 15 and < CarCatalog.VanillaCount && !(CarRaceClasses.FromOpponentValue(Config.OpponentCar) is { } low && VisibleLowClasses.Contains(low))) Config.OpponentCar = 12;   // a class D..G without a car
        if (_showcaseCar >= CarCatalog.VanillaCount && ModCarOf(_showcaseCar) is null) _showcaseCar = 0;
    }

    /// <summary>The cars of a car list of the menu in cycling order: the first <paramref name="vanillaCount"/> original cars, then the mod cars.
    /// <paramref name="classesAfter"/> are the values (opponent class / none) that follow the cars in the opponent list.</summary>
    private List<int> CarCycle(int vanillaCount, IEnumerable<int>? classesAfter = null)
    {
        var list = new List<int>(Enumerable.Range(0, vanillaCount));
        foreach (var car in _modCars) list.Add(Cars.NumberOf(car.Key));
        if (classesAfter != null) list.AddRange(classesAfter);
        return list;
    }

    /// <summary>Opponent values after the cars: the classes 12..14, the visible low classes (D..G), then "none" (15).</summary>
    private IEnumerable<int> OpponentClassValues(int valueCount)
    {
        for (int v = 12; v < valueCount; v++)
        {
            if (v == 15) foreach (var c in VisibleLowClasses) yield return CarRaceClasses.OpponentValue(c);
            yield return v;
        }
    }

    /// <summary>The next/previous value of a car item (player 1 / 2 car, opponent car) when mod cars exist; null = plain cycling of the item's values.</summary>
    private int? NextCarValue(int off, int current, int direction, int valueCount)
    {
        if (_modCars.Count == 0) return null;
        List<int>? cycle = off switch
        {
            0x1F or 0x3A => CarCycle(valueCount),
            0x55 => CarCycle(Math.Min(12, valueCount), OpponentClassValues(valueCount)),
            _ => null,
        };
        if (cycle == null) return null;
        int at = cycle.IndexOf(current);
        if (at < 0) return cycle[direction > 0 ? 0 : cycle.Count - 1];
        return cycle[(at + direction + cycle.Count) % cycle.Count];
    }

    /// <summary>"class D".."class G" of an opponent value that stands for a low class (text of "class C" with the letter replaced); null for any other value.</summary>
    public string? LowClassName(int opponentValue)
        => CarRaceClasses.FromOpponentValue(opponentValue) is { } c ? CarRaceClasses.Label(_text[123], c) : null;

    /// <summary>Name of the car shown in a car item (mod car: <c>DisplayName</c> of car.json); null for an original car.</summary>
    public string? ModCarName(int number) => ModCarOf(number)?.DisplayName;
    /// <summary>TEXT 0x157 (space) – the original inserts it on page 2 in place of the last two values.</summary>
    public const int ShowcaseBlankText = 343;
    /// <summary>Showcase screen pages in the original's order: 1 = GENERAL (gnrl), 2 = PERFORMANCE (perf), 3 = MECHANICAL (mech).</summary>
    public const int ShowcasePages = 3;
    /// <summary>Car shown on the showcase screen (number = row of the tables). [ref:1738]</summary>
    public int ShowcaseCar => _showcaseCar;
    /// <summary>The mod car on the showcase screen (a number from 0x35 up in <see cref="ShowcaseCar"/>), null for an original car. Such a car has no
    /// pictures or tables of the original: the host draws its name, class, description and a 3D preview instead.</summary>
    public CarDefinition? ShowcaseModCar => ModCarOf(_showcaseCar);
    /// <summary>Current page of the showcase screen (1..<see cref="ShowcasePages"/>).</summary>
    public int ShowcasePage => _showcasePage;
    /// <summary>Whether the current screen is the car showcase (<see cref="ShowcaseScreen"/>).</summary>
    public bool InShowcase => _screen == ShowcaseScreen;
    /// <summary>QFS name of the car shown on the showcase screen (table). [ref:1739]</summary>
    public string ShowcaseArt => ShowcaseModCar != null ? "" : MenuArtConstants.ShowcaseArtNames[_showcaseCar];
    /// <summary>Car history screen ("history" item of the showcase screen, type 8). [ref:1740]</summary>
    public const string HistoryScreen = "history";
    /// <summary>Slide show screen ("slide show" item, type 12). [ref:1741]</summary>
    public const string SlideShowScreen = "slide-show";
    /// <summary>Number of photo captions. [ref:1742]</summary>
    public const int HistoryCaptionCount = 3;
    /// <summary>Car shown on the history screen (table index). [ref:1743]</summary>
    public int HistoryCar => _historyCar;
    /// <summary>Car shown on the slide show screen (table index). [ref:1744]</summary>
    public int SlideCar => _slideCar;
    /// <summary>Slide show frame (0 = file without a digit, n &gt; 0 = "&lt;name&gt;&lt;n+1&gt;").</summary>
    public int SlideFrame => _slideFrame;
    /// <summary>Whether the current screen is the car history (<see cref="HistoryScreen"/>).</summary>
    public bool InHistory => _screen == HistoryScreen;
    /// <summary>Whether the current screen is the slide show (<see cref="SlideShowScreen"/>).</summary>
    public bool InSlideShow => _screen == SlideShowScreen;
    /// <summary>QFS name of the car history (table). [ref:1745]</summary>
    public string HistoryArt => MenuArtConstants.HistoryArtNames[_historyCar];
    /// <summary>Slide show frame name (table): frame 0 = car name [ref:1746]
    /// frame n &gt; 0 = sprintf("%s%d", name, n+1) (format). [ref:1747]</summary>
    public string SlideArt => _slideFrame == 0
        ? MenuArtConstants.SlideShowArtNames[_slideCar]
        : $"{MenuArtConstants.SlideShowArtNames[_slideCar]}{_slideFrame + 1}";
    /// <summary>Language overlay drawn on the history background. [ref:1748]</summary>
    public string HistoryOverlay => MenuArtConstants.HistoryOverlays[Math.Min((int)Config.Language, 5)];
    /// <summary>Number of slide show frames of a car: 7 for cars 3 and 5 – only SLDFRD has 7 files [ref:1749]
    /// 8 for the others (car 4 shares a file with car 3, but the original does not shorten it).</summary>
    public static int SlideShowFrames(int car) => car is 3 or 5 ? 7 : 8;
    /// <summary>ID of photo caption <paramref name="caption"/> (0..2) of the history (tables). [ref:1750]</summary>
    public int HistoryCaptionId(int caption) => FrontEndConstants.HistoryCaptionTexts[caption * ShowcaseCarCount + _historyCar];
    /// <summary>IDs of the car's timeline rows (pair "first ID, number of rows"). [ref:1751]</summary>
    public IReadOnlyList<int> HistoryTimelineIds
    {
        get
        {
            int first = FrontEndConstants.HistoryTimeline[2 * _historyCar], rows = FrontEndConstants.HistoryTimeline[2 * _historyCar + 1];
            return Enumerable.Range(first, rows).ToArray();
        }
    }

    // --- track info and track records ----- [ref:1752]
    /// <summary>Track info screen ("track info" item of the track screen, type 11). [ref:1753]</summary>
    public const string TrackInfoScreen = "track-info";
    /// <summary>Track records screen ("track records", type 10). [ref:1754]</summary>
    public const string TrackRecordsScreen = "track-records";
    /// <summary>Personal statistics screen ("personal statistics", type 21). [ref:1755]</summary>
    public const string PersonalStatsScreen = "personal-stats";
    /// <summary>Items that select the track: "next track" – 7 tracks, or 8 when the eighth is unlocked (Config[0x1C] bit 0). [ref:1756]</summary>
    public int TrackCount => (Config[0x1C] & 1) != 0 ? 8 : 7;
    /// <summary>Whether the current screen is the track info (<see cref="TrackInfoScreen"/>).</summary>
    public bool InTrackInfo => _screen == TrackInfoScreen;
    /// <summary>Whether the current screen is the track records (<see cref="TrackRecordsScreen"/>).</summary>
    public bool InTrackRecords => _screen == TrackRecordsScreen;
    /// <summary>Length unit – TEXT 0x9E "miles", 0x9D "km"; the separator is a space. [ref:1757]</summary>
    private const int MilesText = 0x9E, KmText = 0x9D;
    /// <summary>QFS name of the track info (table). [ref:1758]</summary>
    public string TrackInfoArt => TrackRecords.InfoArt(Config.Track);
    /// <summary>Language overlay of the track info ([track * 6 +]). [ref:1759]</summary>
    public string TrackInfoOverlay => MenuArtConstants.TrackInfoOverlays[
        Math.Clamp((int)Config.Track, 0, 7) * 6 + Math.Clamp((int)Config.Language, 0, 5)];
    /// <summary>Track difficulty level (TXT[track], drawn at x 0x268, y 0x3F). [ref:1760]</summary>
    public string TrackInfoDifficulty => _text[FrontEndConstants.TrackInfoDifficultyTexts[Math.Clamp((int)Config.Track, 0, 7)]];
    /// <summary>Track length with unit (table km / miles by Config[0x5A] == 0 [ref:1761]
    /// joined with a space; when == 5 the decimal point becomes a comma 0x2C). [ref:1762]</summary>
    public string TrackInfoLength
    {
        get
        {
            bool miles = Config.Units == 0;
            string value = (miles ? TextConstants.TrackLengthsMiles : TextConstants.TrackLengthsKm)[Math.Clamp((int)Config.Track, 0, 7)];
            string text = $"{value} {_text[miles ? MilesText : KmText]}";
            // when == 5 the second buffer character (decimal point) becomes a comma. [ref:1763]
            return Config.Language == 5 && text.Length > 1 ? text[..1] + "," + text[2..] : text;
        }
    }

    /// <summary>IDs of the laps header text (0xAB), the lap record label (0xB0) and the place rows (0xB1..0xBA). [ref:1764]</summary>
    public const int RecordsLapsText = 0xAB, RecordsLabelText = 0xB0, RecordsFirstRowText = 0xB1, RecordsRowTexts = 10;
    /// <summary>Records screen columns – label (0xA4), name (0xA5), car (0xA6), time (0xA7), difficulty (0xA8). [ref:1765]</summary>
    public const int RecordsColumnText = 0xA4, RecordsNameText = 0xA5, RecordsCarText = 0xA6,
        RecordsTimeText = 0xA7, RecordsSkillText = 0xA8;
    /// <summary>The lap record label and row are at y = 0x1AC. [ref:1766]</summary>
    public const int RecordsRecordRowY = 0x1AC;
    /// <summary>The car name is 0x19 to the left of column 0xA6. [ref:1767]</summary>
    public const int RecordsCarOffset = -0x19;
    /// <summary>QFS name of the records screen (table). [ref:1768]</summary>
    public string TrackRecordsArt => TrackRecords.RecordsArt(Config.Track);
    /// <summary>Language overlay of the records screen. [ref:1769]</summary>
    public string TrackRecordsOverlay => MenuArtConstants.TrackRecordsOverlays[Math.Clamp((int)Config.Language, 0, 5)];
    /// <summary>Number of laps shown on the records screen (item 1: 2 / 4 / 8) – selects the .STF rows.</summary>
    public int RecordsLaps { get; private set; } = 4;
    private StatsFile? _stats;
    private bool _statsLoaded;
    /// <summary>Track records – loaded once on entry or on a track change. [ref:1770]</summary>
    public StatsFile? TrackStats
    {
        get
        {
            if (!_statsLoaded) { _stats = TrackRecords.Load(Config.Track, _gameDir, StatsDirectory); _statsLoaded = true; }
            return _stats;
        }
    }
    /// <summary>The "clear records" item is greyed out when all 31 entries have time 0. [ref:1771]</summary>
    public bool HasTrackRecords => TrackStats?.Records.Any(r => r.Time != 0) == true;

    // --- saving records after a race (statchk, see TrackRecordUpdate) ---------------------- [ref:1772]
    private bool _recordsTotalPending, _recordsLapPending;   // [ref:1773]
    private Queue<RacerResult>? _recordsQueue;
    private Action<string>? _nameDone;
    /// <summary>Track record narrator clip (c03…c11); the host connects it. [ref:1774]</summary>
    public event Action<string>? RecordSpeech;

    /// <summary>Results screen frame: the first frame of the screen checks the top 10 total times, the first frame [ref:1775]
    /// of the lap times screen – the lap record. Waits until the victory screen, dialog and editor are gone. [ref:1776]</summary>
    public void UpdateRecords()
    {
        if (Status != MenuStatus.Results || VictoryActive || _nameEditor != null || _dialog != null) return;
        if (_screen == RaceResultsScreen && _recordsTotalPending)
        {
            _recordsTotalPending = false;
            _recordsQueue = new Queue<RacerResult>(TrackRecordUpdate.TotalCandidates(Config, Results!));
            NextTotalRecord();
        }
        else if (_screen == LapTimesScreen && _recordsLapPending && _recordsQueue is not { Count: > 0 })
        {
            _recordsLapPending = false;
            LapRecord();
        }
    }

    private StatsFile? _raceStats;
    private int _raceStatsTrack = -1;
    /// <summary>Menu track index for the race .STF: from the session when there is one (also a race outside the menu). [ref:1777]</summary>
    public int RaceRecordsTrack => Session is { } s ? VictoryRules.MenuTrack(s.TrackNumber) : Config.Track;
    /// <summary>Track records of the last race (on the results screens), with the user copy [ref:1778]
    /// and the records saved after the race.</summary>
    public StatsFile? RaceRecords
    {
        get
        {
            int track = RaceRecordsTrack;
            if (_raceStatsTrack != track) { _raceStats = TrackRecords.Load(track, _gameDir, StatsDirectory); _raceStatsTrack = track; }
            return _raceStats;
        }
    }

    StatsFile RecordsFile() => RaceRecords is { Records.Count: >= TrackRecords.ClearRecordCount } f ? f : StatsFile.Parse(TrackRecords.ClearedBytes());
    bool RecordsSplit => Config.GameType == 1;
    int RaceLaps => Session?.Setup.NumLaps ?? Config.Laps;
    int RecordsPlayerCars => Results!.Racers.Count(r => r.Human);

    // --- Modded mode (D8): races with a mod car never touch the .STF files; records go to records_mods.json ---------
    /// <summary>`%APPDATA%\NFS2SE-port\records_mods.json` (<see cref="ModRecords"/>); null = modded records stay in memory only.</summary>
    public string? ModRecordsPath { get; set; }
    GameDataMode _recordsMode;
    ModRecords? _modRecords;
    ModRecords ModRecordStore => _modRecords ??= ModRecordsPath is { } path ? ModRecords.Load(path) : ModRecords.Empty();
    public ModRecords ModdedRecords => ModRecordStore;
    string? ModCarKey(int number) => Cars.TryGetByNumber(number, out var def) ? def.Key.Value : null;

    void SaveModRecords()
    {
        if (ModRecordsPath is not { } path) return;
        try { ModRecordStore.Save(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastMessage = _text[TrackRecordUpdate.WriteErrorTextId]; }
    }

    void NextTotalModRecord()
    {
        while (_recordsQueue is { Count: > 0 } queue)
        {
            var car = queue.Dequeue();
            if (ModCarKey(car.CarType) is not { } key) continue;
            int track = RaceRecordsTrack, laps = RaceLaps;
            if (!ModRecordStore.TotalQualifies(track, laps, key, car.RaceTicks)) continue;
            ModRecord Record(string name) => new(track, laps, ModRecords.Total, key, name, car.RaceTicks, TrackRecordUpdate.Skill(Config));
            int rank = ModRecordStore.RankOf(track, laps, key, car.RaceTicks);
            if (TrackRecordUpdate.TotalClip(rank, car.CarIndex, RecordsSplit, RecordsPlayerCars) is { } clip) RecordSpeech?.Invoke(clip);
            StartRecordName(car.CarIndex, name =>
            {
                ModRecordStore.InsertTotal(Record(name));
                SaveModRecords();
                NextTotalRecord();
            });
            return;
        }
    }

    void ModLapRecord()
    {
        var none = StatsFile.Parse(TrackRecords.ClearedBytes());   // the original file is not consulted in modded mode
        if (TrackRecordUpdate.LapCandidate(Config, none, Results!, RecordsSplit, RecordsPlayerCars) is not { } car) return;
        if (ModCarKey(car.CarType) is not { } key) return;
        int track = RaceRecordsTrack, laps = RaceLaps, best = car.LapTicks[TrackRecordUpdate.BestLapIndex(car)];
        if (!ModRecordStore.LapQualifies(track, key, best)) return;
        if (TrackRecordUpdate.LapClip(car.CarIndex, RecordsSplit) is { } clip) RecordSpeech?.Invoke(clip);
        void Save(string name)
        {
            ModRecordStore.SetLap(new ModRecord(track, 0, ModRecords.Lap, key, name, best, TrackRecordUpdate.Skill(Config)));
            SaveModRecords();
        }
        // a total time in the first 9 rows gives the name without asking [ref:1779]
        if (ModRecordStore.Records.Any(r => r.Track == track && r.Laps == laps && r.Kind == ModRecords.Total && r.Car == key && r.Ticks == car.RaceTicks
                && ModRecordStore.RankOf(track, laps, key, r.Ticks) <= TrackRecordUpdate.Rows - 1))
            Save(Config.RaceName(car.CarIndex));
        else StartRecordName(car.CarIndex, Save);
    }

    void NextTotalRecord()
    {
        if (_recordsMode == GameDataMode.Modded) { NextTotalModRecord(); return; }
        while (_recordsQueue is { Count: > 0 } queue)
        {
            var car = queue.Dequeue();
            if (!TrackRecordUpdate.TotalQualifies(RecordsFile(), RaceLaps, car.RaceTicks)) continue;
            if (Cars.FromNumber(car.CarType) is not { } legacy) continue; // mod cars have no slot in the original records file
            StatRecord Record(string name) => new(name, (short)legacy, car.RaceTicks, TrackRecordUpdate.Skill(Config), 0);
            int rank = TrackRecordUpdate.InsertTotal(RecordsFile(), RaceLaps, Record("")).Rank;
            if (TrackRecordUpdate.TotalClip(rank, car.CarIndex, RecordsSplit, RecordsPlayerCars) is { } clip) RecordSpeech?.Invoke(clip);
            StartRecordName(car.CarIndex, name =>
            {
                SaveRecords(TrackRecordUpdate.InsertTotal(RecordsFile(), RaceLaps, Record(name)).File);
                NextTotalRecord();
            });
            return;
        }
    }

    void LapRecord()
    {
        if (_recordsMode == GameDataMode.Modded) { ModLapRecord(); return; }
        if (TrackRecordUpdate.LapCandidate(Config, RecordsFile(), Results!, RecordsSplit, RecordsPlayerCars) is not { } car) return;
        if (Cars.FromNumber(car.CarType) is not { } legacy) return; // mod cars have no slot in the original records file
        if (TrackRecordUpdate.LapClip(car.CarIndex, RecordsSplit) is { } clip) RecordSpeech?.Invoke(clip);
        void Save(string name) => SaveRecords(TrackRecordUpdate.SetLap(RecordsFile(),
            new StatRecord(name, (short)legacy, car.LapTicks[TrackRecordUpdate.BestLapIndex(car)], TrackRecordUpdate.Skill(Config), 0)));
        if (TrackRecordUpdate.LapNameFromTotal(RecordsFile(), RaceLaps, car)) Save(Config.RaceName(car.CarIndex));
        else StartRecordName(car.CarIndex, Save);
    }

    /// <summary>Name dialog with the name of player 0's profile (single machine) or the race name of the car (split); the result goes [ref:1780]
    /// to the race name and the profile of car 0/1.</summary>
    void StartRecordName(int car, Action<string> done)
    {
        _namePlayer = car;
        _nameEditor = new NameEditor(RecordsSplit ? Config.RaceName(car) : Config.ProfileName(0), _text[PlayerNames.PlayerTextId]);
        _nameDone = name =>
        {
            if (car is 0 or 1)
            {
                Config.SetName(FeConfig.RaceNameOffset(car), name);
                Config.SetName(FeConfig.ProfileNameOffset(car), name);
                PersistRequested?.Invoke();
            }
            done(name);
        };
    }

    /// <summary>Saves .STF to the user directory (error → TEXT 0x2EF). Without a directory the record stays in memory only. [ref:1781]</summary>
    void SaveRecords(StatsFile file)
    {
        _raceStats = file; _statsLoaded = false;
        if (StatsDirectory == null) return;
        try { UserStats.Write(StatsDirectory, TrackRecords.FilePath(RaceRecordsTrack), file.ToBytes()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastMessage = _text[TrackRecordUpdate.WriteErrorTextId]; }
    }

    // --- personal statistics (screen) ------------------------------------ [ref:1782]
    /// <summary>Whether the current screen is the personal statistics (<see cref="PersonalStatsScreen"/>).</summary>
    public bool InPersonalStats => _screen == PersonalStatsScreen;
    /// <summary>Track, laps and car are SCREEN variables  – they start [ref:1783] [ref:3413]
    /// from the config (+0x10, +0x18, +0x1F) and do not return to it on exit; the chart type starts at 0.</summary>
    public int StatTrack { get; private set; }
    public int StatLaps { get; private set; } = 4;
    public int StatCar { get; private set; }
    public int StatView { get; private set; }
    /// <summary>QFS name of the screen (table): "&lt;track&gt;rec" – the same file [ref:1784]
    /// as the records screen, but reloaded on a track change on this screen. [ref:1785] [ref:3414]</summary>
    public string PersonalStatsArt => TrackRecords.RecordsArt(StatTrack);
    /// <summary>Language overlay (esta/gsta/fsta/ssta/ista/wsta). [ref:1786]</summary>
    public string PersonalStatsOverlay => TextConstants.PersonalStatsOverlays[Math.Clamp((int)Config.Language, 0, 5)];
    /// <summary>Laps header – TEXT 0x2B4 ("laps"), "%s %d" (language 5 → "%d %s") plus [ref:1787]
    /// " (%s)" from TEXT 0xD ("backwards") when config +0x1A ≠ 0. [ref:1788]</summary>
    public const int PersonalStatsLapsText = 0x2B4, PersonalStatsBackwardText = 0xD;
    /// <summary>Anchor of the second line (TEXT 0xC9 "history", y + 0x19) and the x offset of both lines (+0x1E). [ref:1789]</summary>
    public const int PersonalStatsHistoryText = 0xC9, PersonalStatsHeaderDx = 0x1E, PersonalStatsCarDy = 0x19;
    /// <summary>Chart type name (0xC1..0xC4) and its "... history" variant (0xC5..0xC8). [ref:1790]</summary>
    public const int PersonalStatsViewText = 0xC1, PersonalStatsViewTitleText = 0xC5;
    /// <summary>Time axis labels – 0xCA "newest" (x 0x273, to the right), 0xCB "oldest" (x 0xF3). [ref:1791]</summary>
    public const int PersonalStatsNewestText = 0xCA, PersonalStatsOldestText = 0xCB;
    /// <summary>Header and car name x = x(0x2B4) + 0x1E, font 0x18, to the right, colour 2; [ref:1792]
    /// y of the car name = y(0xC9) + 0x19.</summary>
    public string PersonalStatsHeader
    {
        get
        {
            string word = _text[PersonalStatsLapsText];
            string head = Config.Language == 5 ? $"{StatLaps} {word}" : $"{word} {StatLaps}";
            return Config.Backward == 0 ? head : $"{head} ({_text[PersonalStatsBackwardText]})";
        }
    }
    /// <summary>Car name from the selection fields (via <see cref="TrackRecords.CarName"/>). [ref:1793]</summary>
    public string PersonalStatsCarName => TrackRecords.CarName(_text, StatCar);
    /// <summary>Chart type name drawn at (0xE0, 0xA7), font 0x10, colour 4. [ref:1794]</summary>
    public string PersonalStatsViewName => _text[PersonalStatsViewText + StatView];
    private PersonalStatsFile? _stat;
    private bool _statLoaded;
    /// <summary>Personal statistics file of the track and mode – loaded once on entry or on a track change. [ref:1795]</summary>
    public PersonalStatsFile? PersonalStatFile
    {
        get
        {
            if (!_statLoaded)
            {
                _stat = PersonalStats.Load(StatTrack, Config.Backward != 0, _gameDir, StatsDirectory);
                _statLoaded = true;
            }
            return _stat;
        }
    }
    /// <summary>Chart entries: the car and lap-category block, trimmed to the entries with a lap time. [ref:1796]</summary>
    public IReadOnlyList<PersonalStatRecord> StatEntries =>
        PersonalStats.Entries(PersonalStats.Block(PersonalStatFile, StatCar, StatLaps));
    /// <summary>Values drawn for the selected chart type. [ref:1797]</summary>
    public IReadOnlyList<int> StatValues => PersonalStats.Values(StatEntries, StatView);

    // --- credits  ------------------------------------------------------------------------- [ref:1798]
    /// <summary>Credits screen ("credits" item of the options screen, type 18). [ref:1799]</summary>
    public const string CreditsScreen = "credits";
    /// <summary>Scrolled rows TEXT 0x35A..0x5D8 inclusive. [ref:1800]</summary>
    public const int CreditsFirstText = 0x35A, CreditsLastText = 0x5D8;
    /// <summary>The finale card is the last three rows (0x5D6..0x5D8). [ref:1801]</summary>
    public const int CreditsFinaleText = 0x5D6, CreditsFinaleRows = 3;
    /// <summary>First y of the list (0x1C2 = 450), row step (0x1A) and frame step (−3). The list ends [ref:1802]
    /// when row 0x5D6 reaches y &lt; 0x6F: the original then sets y to the final value (−0x40CC), which ends the loop.</summary>
    public const int CreditsStartY = 0x1C2, CreditsRowStep = 0x1A, CreditsStep = 3, CreditsEndLineY = 0x6E;
    /// <summary>The "P" key (0x50) scrolls faster (−0xA), "H" (0x48) slower (+7, not above 0x1C2). [ref:1803]</summary>
    public const int CreditsFastStep = 0xA, CreditsSlowStep = 7;
    /// <summary>Visibility band of a list row – y in (0x4A, 0x1DC), hard edges; [ref:1804]
    /// clipping rectangle <see cref="CreditsClipTop"/>..<see cref="CreditsClipBottom"/>.</summary>
    public const int CreditsClipTop = 0x4A, CreditsClipBottom = 0x1DC;
    /// <summary>Clipping rectangle for drawing the list (0, 100)-(width, 0x1C2). [ref:1805]</summary>
    public const int CreditsClipRectTop = 100, CreditsClipRectBottom = 0x1C2;
    /// <summary>The finale card moves x = x + x + 2 until it reaches 600; row x = 0x140 + offset. [ref:1806]</summary>
    public const int CreditsFinaleLimit = 600, CreditsFinaleX = 0x140;
    /// <summary>QFS name of the credits screen (shapes "Bac1" and language overlays).</summary>
    public const string CreditsArt = "credits";
    /// <summary>Y of credits row 0x35A; decreases by <see cref="CreditsStep"/> per frame. [ref:1807]</summary>
    public int CreditsY { get; private set; } = CreditsStartY;
    /// <summary>Offset of the finale card; −1 = the list is still scrolling. [ref:1808]</summary>
    public int CreditsFinale { get; private set; } = -1;
    /// <summary>y of row 0x5D6 from the frame in which it reached the band – anchor of the finale card. [ref:1809]</summary>
    public int CreditsFinaleLineY { get; private set; }
    /// <summary>Whether the current screen is the credits (<see cref="CreditsScreen"/>).</summary>
    public bool InCredits => _screen == CreditsScreen;
    /// <summary>Credits language overlay. [ref:1810]</summary>
    public string CreditsOverlay => MenuArtConstants.CreditsOverlays[Math.Clamp((int)Config.Language, 0, 5)];

    /// <summary>Visible credits list rows for the offset <paramref name="y"/> of row 0x35A. [ref:1811]
    /// The gold row (colour 4) moves 0xD down and gets the ":" glyph, and the next white row (colour 2) goes back [ref:1812]
    /// by 0x1A; colours ≥ 4 move the row 0x14 to the left (colour 5 only that much). Rows are drawn in the band
    /// of visibility (<see cref="CreditsClipTop"/>, <see cref="CreditsClipBottom"/> – both with hard edges).</summary>
    /// <remarks>Note on the ":" glyph: it is drawn at (the list offset in this frame) + 0xA [ref:1813]
    /// not in the row column (x takes from the same variable as the scrolled y being set [ref:1814]
    /// and clamped). We keep it that way – faithful to the original. [ref:1815]</remarks>
    public static IReadOnlyList<CreditsLine> CreditsLines(TextFile text, int y, int width)
    {
        var lines = new List<CreditsLine>();
        bool gold = false;
        int scrollY = y;
        for (int id = CreditsFirstText; id <= CreditsLastText; id++)
        {
            var entry = text.Entries[id];
            int x = width / 2;
            if (entry.Colour < 4)
            {
                if (entry.Colour == 2) { if (gold) y -= CreditsRowStep; gold = false; }
            }
            else
            {
                x -= 0x14;
                if (entry.Colour == 4) { gold = true; y += 0xD; }
            }
            if (y > CreditsClipTop && y < CreditsClipBottom)
            {
                if (entry.Colour == 4) lines.Add(new(":", scrollY + 10, y, entry.Colour, entry.Align, true));
                lines.Add(new(entry.Text, x, y, entry.Colour, entry.Align, false));
            }
            y += CreditsRowStep;
        }
        return lines;
    }

    /// <summary>y of row <paramref name="id"/> at offset <paramref name="y"/> – row step [ref:1816]
    /// and colour corrections from <see cref="CreditsLines"/> (used to detect the end of the list: row 0x5D6 reaches
    /// the band). Row 0x5D6 is colour 2 after a white row, so its own correction is 0 and the result is
    /// equal to the y at which the original draws this row (the running y value at the time of the check). [ref:1817]</summary>
    public static int CreditsLineY(TextFile text, int y, int id)
    {
        bool gold = false;
        for (int i = CreditsFirstText; i < id; i++)
        {
            int colour = text.Entries[i].Colour;
            if (colour < 4) { if (colour == 2) { if (gold) y -= CreditsRowStep; gold = false; } }
            else if (colour == 4) { gold = true; y += 0xD; }
            y += CreditsRowStep;   // the row step is added after each row [ref:1818]
        }
        return y;
    }

    /// <summary>Graphics of credits row 0x5CC: "knwd" at (100, y + 0x32) and "chy2" at (300, y + 0x1E). [ref:1819]
    /// The original always draws them, regardless of whether the row is in the visibility band.</summary>
    public IReadOnlyList<(string Shape, int X, int Y)> CreditsShapes
    {
        get
        {
            int y = CreditsLineY(_text, CreditsY, 0x5CC);
            return [(MenuArtConstants.CreditsShapeNames[0], 100, y + 0x32), (MenuArtConstants.CreditsShapeNames[1], 300, y + 0x1E)];
        }
    }

    /// <summary>Finale card: the last three rows at x = 0x140 + offset, y = anchor + 0x1A * row. [ref:1820]</summary>
    public IReadOnlyList<CreditsLine> CreditsFinaleLines
    {
        get
        {
            var lines = new List<CreditsLine>();
            for (int i = 0; i < CreditsFinaleRows; i++)
            {
                var entry = _text.Entries[CreditsFinaleText + i];
                lines.Add(new(entry.Text, CreditsFinaleX + CreditsFinale, CreditsFinaleLineY + CreditsRowStep * i,
                    entry.Colour, entry.Align, false));
            }
            return lines;
        }
    }

    /// <summary>One frame of the credits list. <paramref name="fast"/> = "P" held, <paramref name="slow"/> = "H". [ref:1821]
    /// Once the list has scrolled into place, the finale card flies in, and after it the screen returns to the options (original: the handler ends, and the loop
    /// of the finale card ends when the offset reaches 0x258).</summary>
    public void CreditsFrame(bool slow, bool fast)
    {
        if (!InCredits) return;
        if (CreditsFinale >= 0)
        {
            CreditsFinale += CreditsFinale + 2;
            if (CreditsFinale >= CreditsFinaleLimit) Back(true);
            return;
        }
        int y = CreditsY;
        if (fast) y -= CreditsFastStep;
        if (slow && (y += CreditsSlowStep) > CreditsStartY) y = CreditsStartY;
        int anchor = CreditsLineY(_text, y, CreditsFinaleText);
        CreditsY = y - CreditsStep;
        if (anchor <= CreditsEndLineY) { CreditsFinaleLineY = anchor; CreditsFinale = 0; }
    }

    /// <summary>Entering the credits screen: the list from y = 0x1C2, without the finale card. The original still waits [ref:1822]
    /// before starting for keyboard/mouse events to clear – the port does not reproduce this: Enter on this screen is
    /// immediately scrolling, so any key closes it at once (like any other key). [ref:1823]</summary>
    private void EnterCredits() { CreditsY = CreditsStartY; CreditsFinale = -1; CreditsFinaleLineY = 0; }

    /// <summary>Directory of `.tou` saves (`%APPDATA%\NFS2SE-port\saves`); null = tournament save/load unavailable.</summary>
    public string? SavesDirectory { get; set; }
    /// <summary>Directory of statistics copies (`%APPDATA%\NFS2SE-port\stats`, <see cref="UserStats"/>); null = no
    /// saving, so "clear records" is unavailable (the port does not touch the game directory).</summary>
    public string? StatsDirectory { get; set; }
    /// <summary>Change of values that must go to the settings file immediately (granted unlock).</summary>
    public event Action? PersistRequested;

    /// <summary>Opens a modal dialog; <paramref name="done"/> is called after it closes (it may open the next one).</summary>
    private void OpenDialog(FrontEndDialog dialog, Action<FrontEndDialog> done) { _dialog = dialog; _dialogDone = done; }

    /// <summary>Message from TEXT <paramref name="textId"/>. [ref:1824]</summary>
    private void ShowMessage(int textId) => OpenDialog(FrontEndDialog.Message(textId), _ => { });

    /// <summary>The "yes" letters of the current language: the upper-case and lower-case bytes. [ref:1825]</summary>
    private string YesKeys
    {
        get
        {
            int lang = Math.Min((int)Config.Language, 5);
            return new string([FrontEndConstants.YesUpper[lang], FrontEndConstants.YesLower[lang]]);
        }
    }

    private void OpenConfirm(int textId, Action onYes) =>
        OpenDialog(FrontEndDialog.Confirm(textId, YesKeys), d => { if (d.Result == 1) onYes(); });

    /// <summary>The scene with animation (<c>AnimateChrome</c>) sets: true = ignore keys (the original does not read input in the entry loops
    /// of the list panel's exit and of the track info entry). Without a scene with animation it is null – logic unchanged. [ref:1826]</summary>
    public Func<bool>? InputBlocked { get; set; }

    /// <summary>The scene with animation sets: true = the closed list should leave (<see cref="FrontEndDialog.ListExitFrames"/>) – the dialog
    /// stays in <see cref="Dialog"/> as <c>Done</c> until the scene calls <see cref="CompleteDialogClose"/>.</summary>
    public Func<FrontEndDialog, bool>? HoldClosedDialog { get; set; }

    private bool Blocked => InputBlocked?.Invoke() ?? false;

    /// <summary>Finishes the list exit: removes the dialog and calls the continuation of the calling screen.</summary>
    public void CompleteDialogClose()
    {
        if (_dialog is not { Done: true } finished) return;
        var done = _dialogDone;
        _dialog = null; _dialogDone = null;
        done?.Invoke(finished);
    }

    private void CloseFinishedDialog()
    {
        if (_dialog is not { Done: true } finished) return;
        if (HoldClosedDialog?.Invoke(finished) == true) return;
        var done = _dialogDone;
        _dialog = null; _dialogDone = null;
        done?.Invoke(finished);
    }

    /// <summary>Keyboard character for the modal dialog (letters "yes"/"n", file name entry; 8 = Backspace); outside a dialog it does nothing.</summary>
    public void DialogChar(int ascii) { if (Blocked) return; _dialog?.Char(ascii); CloseFinishedDialog(); }

    /// <summary>Modal window clock in 128 Hz units (field cursor, message time limit).</summary>
    public void AdvanceDialog(int ticks) { _dialog?.Advance(ticks); CloseFinishedDialog(); }
    private bool _victory;
    /// <summary>Victory screen before the results table (see <see cref="VictoryRules"/>): lasts until a key or [ref:1827]
    /// <see cref="VictoryRules.DurationTicks"/> ticks (the host runs the clock – <see cref="EndVictory"/>). Only a normal race:
    /// the KO/tournament screens replace statdisp and do not call it [hypothesis]. [ref:1828]</summary>
    public bool VictoryActive => _victory && Status == MenuStatus.Results;
    /// <summary>Winner shown on the victory screen (null outside it).</summary>
    public RacerResult? VictoryWinner => VictoryActive && Results is { } r ? VictoryRules.Winner(r) : null;
    /// <summary>Ends the victory screen (a key or the timeout) – then the normal results screen follows. [ref:1829]</summary>
    public void EndVictory() => _victory = false;
    public bool KnockoutSummary => Status == MenuStatus.Results && _screen is "knockout-standings" or "knockout-final";
    /// <summary>Race results screen (the port's table; screen handler 3). [ref:1830]</summary>
    public const string RaceResultsScreen = "race-results";
    /// <summary>Lap times screen (screen handler 4) – "continue" from the results screen. [ref:1831]</summary>
    public const string LapTimesScreen = "lap-times";
    /// <summary>The place selected with LEFT/RIGHT, as an index into <see cref="Results"/>. [ref:1832]
    /// Initially the local player's place. [ref:1833]</summary>
    public int ResultDriver { get; private set; }
    public string KnockoutHeading => Knockout is { } ko
        ? ko.Aborted ? "KNOCKOUT ENDED - DNF" : ko.Won ? "KNOCKOUT WINNER"
        : !ko.CanContinue ? "KNOCKOUT - ELIMINATED" : $"KNOCKOUT - ROUND {ko.Stage + 1}/8" : "";
    public IReadOnlyList<string> KnockoutActions => [Knockout?.CanContinue == true ? "NEXT RACE" : "FINISH", "RESTART", "MENU"];
    public bool ReplayRequested { get; private set; }
    /// <summary>Codes typed into the menu and their lasting effects; see <see cref="TypeChar"/>. [ref:1834]</summary>
    public CheatCodes Cheats { get; }
    public int SelectedIndex { get; private set; }
    /// <summary>The item selected in the main menu while we are on it; elsewhere, the last one set there
    /// (read as the +0xA field of the main screen; sub-screens do not change [ref:1835]
    /// this field, because the keys go to them).</summary>
    public int MainSelection => _screen == "main" ? SelectedIndex : _selection.GetValueOrDefault("main");
    /// <summary>The screen with the current selection and item enablement. The result is remembered as long as nothing it depends on
    /// (source, selection, item enablement, dynamic value list, page / showcase car) has changed –
    /// drawing the menu reads this property several times per frame, and rebuilding the screen from scratch allocated several kB.</summary>
    public MenuScreen CurrentScreen
    {
        get
        {
            var source = _tree[_screen];
            int n = source.Items.Count;
            if (_screenSig.Length != 2 * n + 3) _screenSig = new uint[2 * n + 3];
            var sig = _screenSig;
            for (int i = 0; i < n; i++)
            {
                var item = source.Items[i];
                sig[i] = Enabled(item) ? 1u : 0u;
                sig[n + i] = DynamicList(item) is { } list ? 1u + (uint)DynamicListNames.IndexOf(list) : 0u;
            }
            sig[2 * n] = (uint)SelectedIndex;
            sig[2 * n + 1] = _screen == ShowcaseScreen ? (uint)_showcasePage : 0;
            sig[2 * n + 2] = _screen == ShowcaseScreen ? (uint)_showcaseCar : 0;
            if (_screenCache != null && ReferenceEquals(_screenCacheSource, source) && sig.AsSpan().SequenceEqual(_screenCacheSig))
                return _screenCache;
            var screen = source with
            {
                Selected = SelectedIndex,
                Items = ScreenItems().Select(i => i with { Flags = Enabled(i) ? i.Flags & ~1 : i.Flags | 1 }).ToArray()
            };
            _screenCacheSig = (uint[])sig.Clone();
            _screenCacheSource = source;
            return _screenCache = screen;
        }
    }
    private MenuScreen? _screenCache, _screenCacheSource;
    private uint[] _screenSig = [], _screenCacheSig = [];

    private IReadOnlyList<MenuItem> ScreenItems()
    {
        var items = _tree[_screen].Items;
        if (_screen != ShowcaseScreen) return items.Select(DynamicItem).ToArray();
        // called on entry and on every car / page change. [ref:1836]
        var result = items.ToArray();
        if (result.Length >= 21 && ShowcaseModCar == null) ShowcaseTexts(result);
        return result;
    }

    /// <summary>Replaces the texts of the eight labels (items 6..13) and seven values (items 14..20) with [ref:1837]
    /// the texts of the current page and car. Page 2 has only 5 values – the last two are a space (TEXT 0x157).</summary>
    private void ShowcaseTexts(MenuItem[] items)
    {
        int[] labels = _showcasePage switch
        {
            1 => [381, 382, 383, 384, 385, 386, 387, 388],
            2 => [565, 566, 567, 568, 569, 570, 571, ShowcaseBlankText],
            _ => [473, 474, 475, 476, 477, 478, 479, 480]
        };
        ReadOnlySpan<int> pageTexts = _showcasePage switch
        {
            1 => FrontEndConstants.ShowcasePage1Texts, 2 => FrontEndConstants.ShowcasePage2Texts, _ => FrontEndConstants.ShowcasePage3Texts
        };
        int first = pageTexts[_showcaseCar];
        for (int i = 0; i < 8; i++) items[6 + i] = WithText(items[6 + i], labels[i]);
        for (int i = 0; i < 7; i++)
            items[14 + i] = WithText(items[14 + i], _showcasePage == 2 && i >= 5 ? ShowcaseBlankText : first + i);
    }

    private MenuItem WithText(MenuItem item, int textId) => item with { TextId = textId, Text = _text[textId] };

    private MenuController(MenuTree tree, FeConfig config, string? gameDir, TextFile text, int? pinnedLanguage = null)
    {
        _tree = tree; Config = config; _gameDir = gameDir;
        _pinnedLanguage = pinnedLanguage;
        Cheats = new CheatCodes();
        _text = text;
        _colours = FrontEndConstants.ColourTable.ToArray();
        (_defaultKeyScans, int hud) = KeyBindings.LoadDefaults(gameDir);
        ToggleHudScan = hud;
        KeyBindings.EnsureDefaults(Config, _defaultKeyScans);
        _defaultSplitScans = KeyBindings.LoadSplitDefaults(gameDir);   // 2PLAYER.TXT [ref:1838]
        KeyBindings.EnsureSplitDefaults(Config, _defaultSplitScans);
        PlayerNames.EnsureProfile(Config, _text[PlayerNames.PlayerTextId]);
        Normalize();
        _backup0 = _backup1 = Config.ToBytes();
        SelectedIndex = FirstEnabled(0);
        Flow.Booted();
    }

    /// <summary>Action 0x13 ("reset controls") on the screen: the split table goes back to the defaults. [ref:1839]</summary>
    public void ResetSplitControls() => KeyBindings.ApplySplitDefaults(Config, _defaultSplitScans);

    /// <summary>The language chosen by the host (port.json), not by the saved profile: the original re-derives the language from [ref:1840]
    /// install.win at every start, so a language stored in the profile never wins. [ref:1841]</summary>
    private readonly int? _pinnedLanguage;

    /// <summary>Puts the host-chosen language back into CONFIG +0x5B after a profile (settings.json) was applied over the config; no-op when none was chosen.</summary>
    public void PinLanguage() { if (_pinnedLanguage is { } l) Config.Language = (byte)l; }

    /// <param name="language">Host-chosen language 0..5 (null = the saved config's value, else English): selects TEXT.xxx
    ///  and CONFIG +0x5B, which the art/layout switches read. [ref:1842]</param>
    public static MenuController Load(string? gameDir = null, bool useSavedConfig = false, int? language = null)
    {
        byte[]? saved = useSavedConfig && GameFiles.Exists(FeConfig.FilePath, gameDir)
            ? GameFiles.ReadRaw(FeConfig.FilePath, gameDir) : null;
        var config = saved?.Length == FeConfig.Size ? FeConfig.Parse(saved) : Defaults();
        // always resets game/race type on startup. [ref:1843]
        config.GameType = config.RaceType = 0;   // also drops legacy modem/serial values (FeConfig.SanitizeGameType)
        int? pinned = language is { } l ? Math.Clamp(l, 0, 5) : null;
        if (pinned is { } p) config.Language = (byte)p;
        // a fresh config gets units = 1 (km/h) for every language but English. [ref:1844]
        if (saved == null && config.Language != 0) config.Raw[0x5A] = 1;
        var text = TextFile.ForLanguage(config.Language, gameDir);
        return new MenuController(MenuTables.Load(text), config, gameDir, text, pinned);
    }

    // Platform detection, names and key bindings are host concerns. [ref:1845]
    private static FeConfig Defaults()
    {
        var c = new FeConfig(new byte[FeConfig.Size]);
        foreach (int o in new[] { 0x0B, 0x0E, 0x0F, 0x20, 0x3B, 0x32, 0x35, 0x37, 0x36, 0x38, 0x39,
            0x4C, 0x50, 0x51, 0x52, 0x53, 0x54, 0x58, 0x59, 0x19E, 0x19F }) c[o] = 1;
        c.OpponentCar = 12; c.OpponentCount = 2;
        c[0x5C] = c[0x5D] = c[0x5E] = 128; c[0x12D] = 2; c[0x1A2] = 8;
        var colours = FrontEndConstants.ColourTable.ToArray();
        for (int i = 0; i < 15; i++) c[0x21 + i] = c[0x3C + i] = colours[2 * i];
        return c;
    }

    private bool Enabled(MenuItem item)
    {
        // Runtime flags are also needed (original table bytes alone are insufficient). [ref:1846]
        // bit 0 of item 0's flags = no tournament series, bit 0 of item 3's flags = no `replay.rpy`. [ref:1847]
        if (_screen == LoadSaveScreen && item.TextId == 373) return Config.RaceType == 1 && Tournament is { Finished: false };
        if (_screen == LoadSaveScreen && item.TextId == 376) return CurrentReplayExists;
        if (_screen == "main" && item.TextId == 3) return Config.GameType == 1;
        if (_screen == "main" && item.TextId == 1) return Config.RaceType is 0 or 1;
        if (item.ConfigOffset == 0x0E) return Config.GameType != 0;
        if (item.ConfigOffset == 0x10) return Config.RaceType is 0 or 1;
        if (item.ConfigOffset == 0x19) return Config.RaceType == 0;
        if (item.ConfigOffset == 0x58) return Config.Track != 0 && Config.OpponentCount != 2;
        if (item.ConfigOffset == 0x0D) return Config.OpponentCar != 15;
        // Showcase of a mod car: it has no history, slide show or video (those are pictures and films of the original cars).
        if (_screen == ShowcaseScreen && ShowcaseModCar != null && IsOriginalCarOnlyShowcaseItem(item.Id)) return false;
        if (item.ConfigOffset is 0x30 or 0x4B) return Config[0x1E] != 0 && Config.RaceType == 0;
        if (item.Target is "car-settings-p1" or "car-settings-p2") return Config.Style == 0 && Config.RaceType == 0;
        if (item.Action == 15)
        {
            int car = item.ValueRaw == 1 ? Config.P1Car : Config.P2Car;
            if (ModCarOf(car) is { } modCar) return modCar.Colors.Count > 1;   // one colour (or none) = nothing to choose
            return _colours[car * 2 + 1] != 0;
        }
        // Controls screen: DONE (flag 8) is greyed out until some action has no key (bit 0). [ref:1848]
        if (_screen == ControlsScreen && (item.Flags & 8) != 0 && !KeyBindings.AllAssigned(Config)) return false;
        // Screen: DONE is greyed out while any of the 20 entries is empty (table). [ref:1849]
        if (_screen == SplitControlsScreen && (item.Flags & 8) != 0 && !KeyBindings.AllAssignedSplit(Config)) return false;
        // "clear records" (0xAC) is greyed out without any entry, and "personal statistics" (0xAD) [ref:1850]
        // when the player's statistics are off (+0x0F: bit 0 of item 3's flags). [ref:1851]
        if (_screen == TrackRecordsScreen && item.TextId == 0xAC) return HasTrackRecords;
        if (_screen == TrackRecordsScreen && item.TextId == 0xAD) return Config.PersonalStats != 0;
        return (item.Flags & 1) == 0;
    }

    private static readonly List<string> DynamicListNames = [.. MenuConstants.DynamicValueTextIds.Keys];

    private string? DynamicList(MenuItem item) => item.ConfigOffset switch
    {
        0x1F or 0x3A => Config.RaceType != 0 ? "laps-12" : Config[0x1D] switch
        { 1 => "laps-13", 2 => "laps-14", 3 => "laps-15", _ => "laps-12" },
        0x10 => (Config[0x1C] & 1) != 0 && Config.RaceType != 1 ? "tracks-8" : "tracks-7",
        _ => null
    };

    private MenuItem DynamicItem(MenuItem item)
    {
        string? list = DynamicList(item);
        if (list == null) return item;
        var ids = MenuConstants.DynamicValueTextIds[list];
        return item with { ValueTextIds = ids, ValueTexts = ids.Select(id => _text[id]).ToArray() };
    }

    private int FirstEnabled(int start, int direction = 1)
    {
        var items = _tree[_screen].Items;
        for (int n = 0; n < items.Count; n++)
        {
            int i = (start + direction * n % items.Count + items.Count) % items.Count;
            if (items[i].Kind == 2 && Enabled(items[i])) return i;
        }
        return 0;
    }

    // laps lookup, single-player catch-up, opponent and traffic dependencies. [ref:1852]
    private void Normalize()
    {
        Config.GameType = FeConfig.SanitizeGameType(Config.GameType);
        Config.Laps = (byte)FrontEndConstants.LapCounts[Math.Min(Config.LapsMenuIndex, (byte)2)];
        if (Config.GameType == 0) { Config.CatchUp = 0; _catchUpDisabled = true; }
        else if (_catchUpDisabled) { Config.CatchUp = 1; _catchUpDisabled = false; }
        if (Config.GameType == 1 && Config.OpponentCount == 1) Config.OpponentCount = 2;
        if (Config.Track == 7 && (Config.RaceType == 1 || Config.RaceType != 2 && (Config[0x1C] & 1) == 0)) Config.Track = 0;
        if (Config.OpponentCar == 15) Config.OpponentCount = 0;
        else if (IsOpponentClass(Config.OpponentCar)) Config.OpponentCount = 2;
        if (Config.Track == 0 || Config.OpponentCount == 2)
        {
            if (!_trafficDisabled) Config[0x59] = Config[0x58];
            Config[0x58] = 0;
            _trafficDisabled = true;
        }
        else if (_trafficDisabled) { Config[0x58] = Config[0x59]; _trafficDisabled = false; }
    }

    /// <summary>Items 1..4 of the showcase screen (more stats, history, slide show, video): pages, pictures and films of the original cars.
    /// A separate method, so that <see cref="Enabled"/> (called for every item every frame) captures nothing and allocates nothing.</summary>
    private bool IsOriginalCarOnlyShowcaseItem(string address)
    {
        var items = _tree[_screen].Items;
        for (int i = 1; i <= 4 && i < items.Count; i++) if (items[i].Id == address) return true;
        return false;
    }

    public string ItemText(MenuItem item)
    {
        var text = item.Text ?? item.Id;
        if (item.ConfigOffset is { } off && item.ValueTexts is { Count: > 0 } values)
            return $"{text}: {(off is 0x1F or 0x3A or 0x55 ? ModCarName(Config[off]) : null) ?? (off == 0x55 ? LowClassName(Config[off]) : null) ?? values[Math.Min(Config[off], values.Count - 1)]}";
        if (item.Action == 15)
        {
            int car = item.ValueRaw == 1 ? Config.P1Car : Config.P2Car;
            if (ModCarOf(car) is { } modCar)
                return $"{text}: {(modCar.Colors.Count == 0 ? "" : modCar.Colors[ModColour(item.ValueRaw == 1 ? 0 : 1, modCar)].Name(ModTextLanguage))}";
            return $"{text}: {Config[(item.ValueRaw == 1 ? 0x21 : 0x3C) + car]}";
        }
        return text;
    }

    /// <summary>Character typed into the menu: the codes FZR2000, TOMBSTONE, BOMBER, HOLLYWOOD, SLOT [ref:1853]
    /// PIONEER, SLIP, RAIN, CHASE, RUSHHOUR, REXHOUR, MADLAND, SCHOOLZONE, VIP, ROADRAGE (MAD and KCJONES do nothing)
    /// and GOnn (18..51: the player's car model and traffic). Only while the menu is active (not in a race) and single race is selected.
    /// Unlocks (+0x1C/+0x1D/+0x1E/+0x1F, track) are persistent – <see cref="PersistRequested"/>.</summary>
    public void TypeChar(char ch)
    {
        if (Status is MenuStatus.Race or MenuStatus.Exit || VictoryActive || Capturing || _dialog != null || _nameEditor != null || Blocked) return;
        var r = Cheats.Type(ch, Config, Normalize);
        if (r.Dialog)
        {
            var entries = _text.Entries;
            LastMessage = 0x34A < entries.Count ? _text[0x34A] : null; // TEXT 0x34A when the code opens a dialog [ref:1854]
        }
        if (r.ConfigChanged)
        {
            if (Config.Track == 7 && (Config[0x1C] & 1) != 0) SelectedIndex = FirstEnabled(SelectedIndex);
            PersistRequested?.Invoke();
        }
    }

    /// <summary>Character for the name editor (ASCII, as in the original; 8 = Backspace); does nothing outside editing. [ref:1855]</summary>
    public void NameKey(int ascii) { if (Blocked) return; _nameEditor?.Key(ascii); }

    /// <summary>Cursor blink clock of the name editor (clock units of the original, 128 Hz, see <see cref="NameEditor.Advance"/>).</summary>
    public void AdvanceNameEditor(int ticks) => _nameEditor?.Advance(ticks);

    /// <summary>Split screen: before a race starts from the main screen it asks for the names of players 1 and 2 (a window; in the original it is called [ref:1856]
    /// after the track record; here it is at the point where the name is asked for before a network game – a function). Returns true when editing started. [ref:1857]</summary>
    private bool BeginNameEditing()
    {
        if (Config.GameType != 1 || Status != MenuStatus.Menu) return false;
        StartNameEditor(0);
        return true;
    }

    private void StartNameEditor(int player)
    {
        _namePlayer = player;
        _nameEditor = new NameEditor(Config.ProfileName(player), _text[PlayerNames.PlayerTextId]);
    }

    /// <summary>Enter / Esc in the editor: Enter saves the name and goes to the next player; after the second, the race starts; [ref:1858]
    /// Esc aborts the start (names already confirmed stay).</summary>
    private void HandleNameEditorKey(MenuKey key)
    {
        if (key is not (MenuKey.Enter or MenuKey.Escape)) return;
        var editor = _nameEditor!;
        editor.Key(key == MenuKey.Enter ? 0xD : 0x1B);
        if (_nameDone is { } done)
        {
            // with a record: Esc keeps the old name, but the record is still written to the file. [ref:1859]
            _nameEditor = null; _nameDone = null;
            done(editor.Result);
            return;
        }
        if (key == MenuKey.Escape) { _nameEditor = null; return; }
        Config.SetName(FeConfig.ProfileNameOffset(_namePlayer), editor.Result);
        PersistRequested?.Invoke();
        if (_namePlayer == 0) { StartNameEditor(1); return; }
        _nameEditor = null;
        StartRace();
    }

    public void HandleKey(MenuKey key)
    {
        if (Blocked) return;
        LastMessage = null;
        if (Status is MenuStatus.Race or MenuStatus.Exit) return;
        if (VictoryActive) { EndVictory(); return; }   // any key ends the screen [ref:1860]
        if (_nameEditor != null) { HandleNameEditorKey(key); return; }
        if (_capture != null) { if (key == MenuKey.Escape) CancelCapture(); return; }   // Esc ends the capture (0x1B) [ref:1861]
        if (_dialog != null) { _dialog.Key(key); CloseFinishedDialog(); return; }
        if (TournamentSummary)
        {
            if (key == MenuKey.Escape) { ReturnToMenu(); return; }
            if (key == MenuKey.Up) SelectedIndex = (SelectedIndex + 3) % 4;
            if (key == MenuKey.Down) SelectedIndex = (SelectedIndex + 1) % 4;
            if (SelectedIndex == 0 && !Tournament!.Finished && key is MenuKey.Left or MenuKey.Right)
            {
                var tracks = Tournament.AvailableTracks.ToList();
                _nextTournamentTrack = tracks[(tracks.IndexOf(_nextTournamentTrack) + (key == MenuKey.Left ? tracks.Count - 1 : 1)) % tracks.Count];
            }
            if (key == MenuKey.Enter && SelectedIndex == 3) StartSaveTournament();
            else if (key == MenuKey.Enter)
            {
                if (SelectedIndex == 1) StartRace();
                else if (SelectedIndex == 2 || Tournament!.Finished) ReturnToMenu();
                else
                {
                    Tournament.NextRace(_nextTournamentTrack); Config.Track = (ushort)Tournament.Track;
                    Session = null; StartRace();
                }
            }
            return;
        }
        if (KnockoutSummary)
        {
            if (key == MenuKey.Escape) { ReturnToMenu(); return; }
            if (key == MenuKey.Up) SelectedIndex = (SelectedIndex + 2) % 3;
            if (key == MenuKey.Down) SelectedIndex = (SelectedIndex + 1) % 3;
            if (key == MenuKey.Enter)
            {
                if (SelectedIndex == 1) StartRace();
                else if (SelectedIndex == 2 || Knockout?.CanContinue != true) ReturnToMenu();
                else NextKnockoutRace();
            }
            return;
        }
        if (key == MenuKey.Escape) { Back(true); Sound?.Invoke(MenuSound.Back); return; }
        if (_screen is RaceResultsScreen or LapTimesScreen && (key is MenuKey.Left or MenuKey.Right))
        { SelectResultDriver(key == MenuKey.Left ? -1 : 1); return; }
        if (_screen == ShowcaseScreen && ShowcaseKey(key)) return;
        if (_screen == HistoryScreen && HistoryKey(key)) return;
        if (_screen == SlideShowScreen && SlideShowKey(key)) return;
        if (_screen == TrackInfoScreen && TrackInfoKey(key)) return;
        if (_screen == PersonalStatsScreen && PersonalStatsKey(key)) return;
        if (_screen == TrackRecordsScreen)
        {
            // clears the selection of "clear records" when there is no entry in the .STF. [ref:1862]
            if (SelectedIndex == 2 && !HasTrackRecords) SelectedIndex = 0;
            if (TrackRecordsKey(key)) return;
        }
        // any key ends the credits scroll (Enter/Escape directly, others pass through) [ref:1863]
        // – the screen returns to the options without the end card.
        if (_screen == CreditsScreen) { Back(true); return; }
        if (_screen == LoadSaveScreen && SelectedIndex <= 5 && key == MenuKey.Enter && Enabled(_tree[_screen].Items[SelectedIndex]))
        { LoadSaveAction(SelectedIndex); return; }
        // LEFT/RIGHT on items 0..9 change the column (player), not the value [ref:1864]
        if (_screen == SplitControlsScreen && SelectedIndex < 10 && key is MenuKey.Left or MenuKey.Right)
        { Config[0x08] = (byte)(SplitColumn == 0 ? 1 : 0); return; }
        if (key is MenuKey.Up or MenuKey.Down)
        {
            int direction = key == MenuKey.Up ? -1 : 1;
            int count = _tree[_screen].Items.Count;
            int previous = SelectedIndex;
            SelectedIndex = FirstEnabled((SelectedIndex + direction + count) % count, direction);
            if (SelectedIndex != previous) Sound?.Invoke(MenuSound.Move);
            return;
        }
        var item = DynamicItem(_tree[_screen].Items[SelectedIndex]);
        if (!Enabled(item)) return;
        if (key == MenuKey.Enter && item.Action == 11 && item.ConfigOffset is { } keyOffset && KeyBindings.ActionAt(keyOffset) is { } keyAction)
        {
            // Action 0xB: Enter starts key capture; the entry is empty until a key is chosen. [ref:1865]
            _capture = keyAction; _joyCapture = new(); _capturePlayer = -1; _captureBackup = KeyBindings.Get(Config, keyAction); KeyBindings.Set(Config, keyAction, 0);
            return;
        }
        if (key == MenuKey.Enter && item.Action == 0xC && item.ConfigOffset is { } splitOffset && KeyBindings.SplitActionAt(splitOffset) is { } split)
        {
            // Action 0xC: the entry for the selected player's column (value pointer + 4·column) is empty until a choice is made. [ref:1866]
            _capture = split.Action; _joyCapture = new(); _capturePlayer = SplitColumn;
            _captureBackup = KeyBindings.GetSplit(Config, _capturePlayer, split.Action);
            KeyBindings.SetSplit(Config, _capturePlayer, split.Action, 0);
            return;
        }
        if (key == MenuKey.Enter && item.Action == 0x13)   // "reset controls?" (0x131) [ref:1867]
        {
            OpenConfirm(ResetControlsText, () =>
            {
                if (_screen == SplitControlsScreen) ResetSplitControls();
                else KeyBindings.ApplyDefaults(Config, _defaultKeyScans);
            });
            return;
        }
        if (key is MenuKey.Left or MenuKey.Right or MenuKey.Enter)
        {
            if ((item.Flags & 0x18) != 0 && key != MenuKey.Enter)
            {
                SelectedIndex = FirstEnabled((SelectedIndex + ((item.Flags & 8) != 0 ? 1 : -1) + CurrentScreen.Items.Count) % CurrentScreen.Items.Count);
                Sound?.Invoke(MenuSound.Move);
                return;
            }
            ChangeValue(item, key == MenuKey.Left ? -1 : 1);
            if ((item.ConfigOffset != null && item.ValueTextIds.Count > 0) || item.Action == 15) Sound?.Invoke(MenuSound.Change);
            if (key != MenuKey.Enter) return;
            if (item.Action == 10) ResetCarSetup();
            if (item.Target is "back" or "cancel") { Sound?.Invoke(MenuSound.Select); Back((item.Flags & 16) != 0 || item.Target == "cancel"); return; }
            if (item.Target == "quit") { Sound?.Invoke(MenuSound.Select); ReturnToMenu(); return; }
            if (item.Target == "race")
            {
                if (Status == MenuStatus.Results && SelectedIndex == 2)
                { StartResultsReplay(); return; }
                if (BeginNameEditing()) return;
                StartRace(); return;
            }
            if (item.Target is { } target && _tree.Screens.Any(s => s.Id == target))
            {
                // single races leave the post-race chain here. [ref:1868]
                if (_screen == "lap-times" && target == "tournament-standings")
                {
                    if (Knockout != null)
                    { _stack.Clear(); _screen = Knockout.Stage == 7 ? "knockout-final" : "knockout-standings"; SelectedIndex = 0; return; }
                    if (Tournament != null)
                    {
                        _stack.Clear(); _screen = Tournament.Finished ? "tournament-final" : "tournament-standings"; SelectedIndex = 0;
                        if (!Tournament.Finished) _nextTournamentTrack = Tournament.AvailableTracks[0];
                        return;
                    }
                    if (Config.RaceType == 0) { ReturnToMenu(); return; }
                }
                EnterScreen(target); Sound?.Invoke(MenuSound.Select); return;
            }
            if (item.Action != 0 && item.ConfigOffset == null && item.Action is not (10 or 15))
                LastMessage = "This screen action is not implemented.";
        }
    }

    /// <summary>On the showcase screen LEFT/RIGHT do not change the item's value; they select the car [ref:1869]
    /// (item 0 "next car") or the page (item 1 "more stats"); the other keys are passed on. [ref:1870]
    /// The selected item is the number of the selectable item; those sit at the start of the screen, so it equals the item index.</summary>
    private bool ShowcaseKey(MenuKey key)
    {
        if (key is not (MenuKey.Left or MenuKey.Right)) return false;
        int delta = key == MenuKey.Left ? -1 : 1;
        if (SelectedIndex == 0)
        {
            if (_modCars.Count == 0) { _showcaseCar = (_showcaseCar + delta + ShowcaseCarCount) % ShowcaseCarCount; return true; }
            var cycle = CarCycle(ShowcaseCarCount);   // original cars, then the mod cars
            int at = cycle.IndexOf(_showcaseCar);
            _showcaseCar = cycle[(Math.Max(at, 0) + delta + cycle.Count) % cycle.Count];
            return true;
        }
        if (SelectedIndex == 1 && ShowcaseModCar != null) return true;   // one page only
        if (SelectedIndex == 1) { _showcasePage = (_showcasePage - 1 + delta + ShowcasePages) % ShowcasePages + 1; return true; }
        return false;
    }

    /// <summary>On the history screen LEFT/RIGHT (or ENTER) select the car, provided item 0 is selected [ref:1871]
    /// ("next car"). Cars sharing one history file jump: left 8→7, 5→3, 2→1, 10→9, right
    /// 7→8, 3→5, 1→2, 9→10; then a normal step with wrap-around (0 ↔ 11).</summary>
    private bool HistoryKey(MenuKey key)
    {
        if (key is not (MenuKey.Left or MenuKey.Right or MenuKey.Enter) || SelectedIndex != 0) return false;
        if (key != MenuKey.Left)
        {
            if (_historyCar == 7) _historyCar = 8;
            else if (_historyCar == 3) _historyCar = 5;
            else if (_historyCar == 1) _historyCar = 2;
            else if (_historyCar == 9) _historyCar = 10;
            _historyCar++;
            if (_historyCar == ShowcaseCarCount) _historyCar = 0;
            return true;
        }
        if (_historyCar == 8) _historyCar = 7;
        else if (_historyCar == 5) _historyCar = 3;
        else if (_historyCar == 2) _historyCar = 1;
        else if (_historyCar == 10) _historyCar = 9;
        _historyCar--;
        if (_historyCar < 0) _historyCar = ShowcaseCarCount - 1;
        return true;
    }

    /// <summary>Item 1 ("next car") – LEFT previous car, RIGHT/ENTER next (wrapping 0 ↔ 11); [ref:1872]
    /// item 0 ("next slide") – RIGHT/ENTER next frame, LEFT previous. The frame wraps to 0 (8 → 0)
    /// and does not exceed the car's last frame (cars 3 and 5 have 7 frames: 7 → 0). A car change restores the frame
    /// to 0 when the new car has fewer. On a car change ENTER also works (as RIGHT).</summary>
    private bool SlideShowKey(MenuKey key)
    {
        if (key is not (MenuKey.Left or MenuKey.Right or MenuKey.Enter)) return false;
        if (SelectedIndex == 1)
        {
            if (key == MenuKey.Left) _slideCar = (_slideCar - 1 + ShowcaseCarCount) % ShowcaseCarCount;
            else _slideCar = (_slideCar + 1) % ShowcaseCarCount;
            if (_slideFrame > SlideShowFrames(_slideCar) - 1) _slideFrame = 0;
            return true;
        }
        if (SelectedIndex != 0) return false;
        if (key != MenuKey.Left)
        {
            _slideFrame++;
            if (_slideFrame == 8 || _slideFrame > SlideShowFrames(_slideCar) - 1) _slideFrame = 0;
        }
        else if (--_slideFrame < 0) _slideFrame = SlideShowFrames(_slideCar) - 1;
        return true;
    }

    /// <summary>The action whose key is now being captured (for drawing "enter new control").</summary>
    public KeyAction? CapturingAction => _capture;

    /// <summary>Item 0 ("next track") – LEFT previous track (from 0 to the last), RIGHT/ENTER next [ref:1873]
    /// (modulo the number of tracks). The chosen track goes into the config when leaving the screen. [ref:1874]</summary>
    private bool TrackInfoKey(MenuKey key)
    {
        if (key is not (MenuKey.Left or MenuKey.Right or MenuKey.Enter) || SelectedIndex != 0) return false;
        ChangeTrack(key == MenuKey.Left ? -1 : 1);
        return true;
    }

    /// <summary>Item 0 "next track" (as <see cref="TrackInfoKey"/>), item 1 "laps" [ref:1875]
    /// (LEFT = laps / 2, below 2 → 8; RIGHT/ENTER = laps * 2, above 8 → 2).</summary>
    private bool TrackRecordsKey(MenuKey key)
    {
        if (key is not (MenuKey.Left or MenuKey.Right or MenuKey.Enter)) return false;
        if (SelectedIndex == 0) { ChangeTrack(key == MenuKey.Left ? -1 : 1); return true; }
        if (SelectedIndex == 1)
        {
            if (key == MenuKey.Left) RecordsLaps = RecordsLaps / 2 < 2 ? 8 : RecordsLaps / 2;
            else RecordsLaps = RecordsLaps * 2 > 8 ? 2 : RecordsLaps * 2;
            return true;
        }
        if (SelectedIndex != 2) return false;
        // (track, 1) writes the cleared .STF – but only after confirmation [ref:1876]
        // (TEXT 0x349); the copy goes to the user's directory, never to the game directory. [ref:1877]
        if (key == MenuKey.Enter) OpenConfirm(ClearRecordsText, ClearTrackRecords);
        return true;
    }

    /// <summary>Confirmed "clear records": zeroes the track's `.STF` in the user's copy [ref:1878]
    /// (<see cref="UserStats"/>) and reloads it, so the screen shows no entries and the item is greyed out.</summary>
    private void ClearTrackRecords()
    {
        if (StatsDirectory == null) { LastMessage = "Clearing track records is unavailable."; return; }
        try
        {
            TrackRecords.Clear(Config.Track, StatsDirectory);
            _statsLoaded = false;
            SelectedIndex = 0;   // does not stay on the greyed-out item after clearing [ref:1879]
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { LastMessage = $"Clearing track records failed: {e.Message}"; }
    }

    /// <summary>LEFT: track − 1 (from 0 to the last), RIGHT/ENTER: (track + 1) % number of tracks. [ref:1880]
    /// A new track is a new .STF file, so the loaded records must be set aside.</summary>
    private void ChangeTrack(int delta)
    {
        int count = TrackCount;
        Config.Track = (ushort)(delta < 0 ? (Config.Track == 0 ? count - 1 : Config.Track - 1)
            : (Config.Track + 1) % count);
        _statsLoaded = false;
    }

    /// <summary>(each item only on LEFT/RIGHT/ENTER keys, as on other screens): [ref:1881]
    /// 0 "track" – the screen's track, wrapping on the track count (bit 0), 1 "laps" – LEFT / 2 [ref:1882]
    /// (below 2 → 8), RIGHT/ENTER * 2 (above 8 → 2), 2 "car" – LEFT − 1 (below 0 → 11), RIGHT/ENTER + 1
    /// (12 → 0), 3 "graph type" – 0..3 with wrap. The other keys (and the "done" item) are passed on;
    /// backing out returns to the track records screen. [ref:1883]</summary>
    private bool PersonalStatsKey(MenuKey key)
    {
        if (key is not (MenuKey.Left or MenuKey.Right or MenuKey.Enter)) return false;
        bool left = key == MenuKey.Left;
        switch (SelectedIndex)
        {
            case 0:
                StatTrack = left ? (StatTrack == 0 ? TrackCount - 1 : StatTrack - 1) : (StatTrack + 1) % TrackCount;
                _statLoaded = false;                          // new track = new file and new picture [ref:3415]
                return true;
            case 1:
                StatLaps = left ? (StatLaps / 2 < 2 ? 8 : StatLaps / 2) : (StatLaps * 2 > 8 ? 2 : StatLaps * 2);
                return true;
            case 2:
                StatCar = left ? (StatCar == 0 ? PersonalStats.CarCount - 1 : StatCar - 1)
                    : (StatCar + 1) % PersonalStats.CarCount;
                return true;
            case 3:
                StatView = left ? (StatView + PersonalStats.ViewCount - 1) % PersonalStats.ViewCount
                    : (StatView + 1) % PersonalStats.ViewCount;
                return true;
        }
        return false;
    }

    /// <summary>Entering the personal statistics screen: track/laps/car from the config (+0x10, +0x18 [ref:1884]
    /// +0x1F), chart type 0. Leaving writes nothing to the config (the screen state is local).</summary>
    private void EnterPersonalStats()
    {
        StatTrack = Math.Min((int)Config.Track, TrackCount - 1);
        StatLaps = Config.Laps is 2 or 8 ? Config.Laps : 4;
        StatCar = Math.Clamp((int)Config.P1Car, 0, PersonalStats.CarCount - 1);
        StatView = 0;
        _statLoaded = false;
    }

    /// <summary>Key pressed during capture (PC set 1 scancode + upper-case ASCII, 0 = no character).
    /// F1..F10 and Enter are ignored as in the original (capture continues); the same key displaces another action.</summary>
    public bool CaptureKey(int scan, int ascii)
    {
        if (_capture is not { } action || !KeyBindings.Assignable(scan)) return false;
        if (_capturePlayer >= 0) KeyBindings.AssignSplit(Config, _capturePlayer, action, scan, ascii);
        else KeyBindings.Assign(Config, action, scan, ascii);
        _capture = null; _joyCapture = null;
        return true;
    }

    /// <summary>One frame of joystick/pad capture: a button, or an axis moved &gt; 0x40 from rest [ref:1885]
    /// (after all buttons are released) becomes the action's binding – device word 1 (axis) / 2 (button), see
    /// <see cref="JoyBindings"/>. Returns true when assigned (capture ends). Keys are handled by <see cref="CaptureKey"/>.</summary>
    public bool CaptureJoystick(IReadOnlyList<JoyState> joysticks)
    {
        if (_capture is not { } action || _joyCapture is not { } joy) return false;
        int word = joy.Poll(joysticks, Config.Raw);
        if (word == 0) return false;
        if (_capturePlayer >= 0) KeyBindings.AssignSplitWord(Config, _capturePlayer, action, word);
        else KeyBindings.AssignWord(Config, action, word);
        _capture = null; _joyCapture = null;
        return true;
    }

    /// <summary>Esc during capture restores the previous key.</summary>
    public void CancelCapture()
    {
        if (_capture is not { } action) return;
        if (_capturePlayer >= 0) KeyBindings.SetSplit(Config, _capturePlayer, action, _captureBackup);
        else KeyBindings.Set(Config, action, _captureBackup);
        _capture = null; _joyCapture = null;
    }

    /// <summary>Text of an action's key on the controls screen: (device label "keyboard", name) as produced by `KeyBindings`. [ref:1886]</summary>
    public (string? Device, string Name) KeyText(MenuItem item) => KeyText(item, 0);

    /// <summary>Joystick column for an item's binding (the player's column <paramref name="player"/>): "joystick N axis M" [ref:1887]
    /// "joystick N button X"; null when a key or nothing is assigned (then <see cref="KeyText(MenuItem, int)"/>).</summary>
    public JoyLabel? JoyText(MenuItem item, int player = 0) =>
        item.ConfigOffset is { } o ? JoyBindings.Display(Config.I32(o + 4 * player), _text) : null;

    /// <summary>As <see cref="KeyText(MenuItem)"/> for the screen's <paramref name="player"/> column (value + 4·player). [ref:1888]</summary>
    public (string? Device, string Name) KeyText(MenuItem item, int player) =>
        KeyBindings.Display(item.ConfigOffset is { } o ? Config.I32(o + 4 * player) : 0, _text);

    /// <summary>Text "name" (TEXT 0x2F8): default content of the file-name field (starting buffer). [ref:1889]</summary>
    private const int DefaultFileNameText = 0x2F8;
    private const int ReplayCurrentText = 0x2E5;
    private const string ReplayExtension = ".rpy";
    private const string CurrentReplayFile = "REPLAY.RPY";

    /// <summary>Whether the current replay exists (`replay.rpy`; flag bit 0 disables "save replay"). [ref:1890]</summary>
    private bool CurrentReplayExists => ResultsReplayRecording != null || GameFiles.Exists(CurrentReplayFile, _gameDir);
    public bool HasCurrentReplay => CurrentReplayExists;

    /// <summary>Handler 9, Enter on item 0..5: save/load/delete tournament, save/view/delete replay. [ref:1891]</summary>
    private void LoadSaveAction(int index)
    {
        switch (index)
        {
            case 0: StartSaveTournament(); break;
            case 1: StartLoadTournament(); break;
            case 2: StartDeleteTournament(); break;
            case 3: StartSaveReplay(); break;
            case 4: StartViewReplay(); break;
            case 5: StartDeleteReplay(); break;
        }
    }

    /// <summary>(mode 2) with the header <paramref name="promptId"/>: a name of up to 8 characters with no special file characters; [ref:1892]
    /// empty or Esc = cancel. An existing file asks for overwrite confirmation (TEXT 0x2F5).</summary>
    private void PromptFileName(int promptId, string extension, Action<string> write)
    {
        OpenDialog(FrontEndDialog.TextEntry(promptId, _text[DefaultFileNameText]), d =>
        {
            if (d.Result == 0) return;
            string name = d.Entry;
            if (File.Exists(SaveFiles.PathFor(SavesDirectory!, name, extension))) OpenConfirm(0x2F5, () => write(name));
            else write(name);
        });
    }

    /// <summary>Item 0 (TEXT 0x2E1) (TEXT 0x2ED "saved" / 0x2EF "failed"); the original saves [ref:1893]
    /// only manually (no autosave after the race).</summary>
    private void StartSaveTournament()
    {
        if (SavesDirectory == null) { LastMessage = "Saving is unavailable."; return; }
        if (Tournament is not { Finished: false } t) { LastMessage = "No tournament in progress to save."; return; }
        // The .tou file is the original format: it has no slot for a mod car (and a mod number would mean another car after a change of mods).
        if (ModCarOf(Config.P1Car) != null || Config.GameType == 1 && ModCarOf(Config.P2Car) != null)
        { LastMessage = "A tournament with a mod car cannot be saved."; return; }
        PromptFileName(0x2E1, TournamentSaves.Extension, name =>
        {
            try { TournamentSaves.Save(SavesDirectory, name, t.ToTou(Config)); ShowMessage(0x2ED); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowMessage(0x2EF); }
        });
    }

    /// <summary>Item 1: while a series is running, the question "quit the current tournament?" (TEXT 0x2E9), then the list of `*.tou`.</summary>
    private void StartLoadTournament()
    {
        if (SavesDirectory == null) { LastMessage = "Loading is unavailable."; return; }
        void Pick() => OpenDialog(FrontEndDialog.List(TournamentSaves.List(SavesDirectory)),
            d => { if (d.Result >= 0) LoadTournament(d.Items[d.Result]); });
        if (Config.RaceType == 1 && Tournament is { Finished: false }) OpenConfirm(0x2E9, Pick);
        else Pick();
    }

    /// <summary>Item 2: list of `*.tou`, "delete saved game from disk?" (TEXT 0x2EB), deletion and message 0x2EC.</summary>
    private void StartDeleteTournament()
    {
        if (SavesDirectory == null) { LastMessage = "Deleting is unavailable."; return; }
        OpenDialog(FrontEndDialog.List(TournamentSaves.List(SavesDirectory)), d =>
        {
            if (d.Result < 0) return;
            string name = d.Items[d.Result];
            OpenConfirm(0x2EB, () =>
            {
                try { TournamentSaves.Delete(SavesDirectory, name); ShowMessage(0x2EC); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowMessage(0x2EF); }
            });
        });
    }

    /// <summary>Name prompt (TEXT 0x2E2), then a copy of the current replay `replay.rpy` (message 0x2EE / 0x2EF). [ref:1894]</summary>
    private void StartSaveReplay()
    {
        if (SavesDirectory == null) { LastMessage = "Saving is unavailable."; return; }
        PromptFileName(0x2E2, ReplayExtension, name =>
        {
            try
            {
                Directory.CreateDirectory(SavesDirectory);
                if (ResultsReplayRecording is { } recorded)
                {
                    Directory.CreateDirectory(SavesDirectory);
                    File.WriteAllBytes(SaveFiles.PathFor(SavesDirectory, name, ReplayExtension), recorded.RawData);
                }
                else File.Copy(GameFiles.FullPath(CurrentReplayFile, _gameDir), SaveFiles.PathFor(SavesDirectory, name, ReplayExtension), overwrite: true);
                ShowMessage(0x2EE);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowMessage(0x2EF); }
        });
    }

    /// <summary>Item 4: list of replays (starting with "current replay", TEXT 0x2E5, when `replay.rpy` exists).</summary>
    private void StartViewReplay()
    {
        var entries = new List<(string Label, Func<byte[]> Read)>();
        if (ResultsReplayRecording is { } latest)
            entries.Add((_text[ReplayCurrentText], () => latest.RawData.ToArray()));
        else if (GameFiles.Exists(CurrentReplayFile, _gameDir))
            entries.Add((_text[ReplayCurrentText], () => GameFiles.ReadRaw(CurrentReplayFile, _gameDir)));
        if (SavesDirectory != null)
            entries.AddRange(SaveFiles.List(SavesDirectory, ReplayExtension).Select(name =>
            {
                string path = SaveFiles.PathFor(SavesDirectory, name, ReplayExtension);
                return (name, (Func<byte[]>)(() => File.ReadAllBytes(path)));
            }));
        OpenDialog(FrontEndDialog.List(entries.Select(e => e.Label).ToArray()), d =>
        {
            if (d.Result < 0) return;
            try
            {
                BeginReplayPlayback(Replay.Parse(entries[d.Result].Read()), fromResults: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
            { LastMessage = "Replay could not be loaded: " + e.Message; }
        });
    }

    private void StartResultsReplay()
    {
        if (PlaybackSource != null) return;
        if (ResultsReplayRecording == null) { LastMessage = "No replay was recorded for this race."; return; }
        try { BeginReplayPlayback(ResultsReplayRecording, fromResults: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { LastMessage = "Replay could not be loaded: " + e.Message; }
    }

    private Replay? ResultsReplayRecording { get; set; }

    /// <summary>Mods checked against a replay mods header (D8); null = the mods of <see cref="ModSession.Current"/>.</summary>
    public IReadOnlyList<LoadedMod>? LoadedMods { get; set; }

    public void BeginReplayPlayback(Replay replay, bool fromResults = false)
    {
        ModsReplayHeader.Validate(replay, Cars, LoadedMods ?? ModSession.Current.Mods);   // ReplayModsMismatchException (InvalidDataException) = rejected
        var tags = RaceTags.FromConstants();
        var setup = RaceArgsParser.Parse(tags, RaceArgsParser.StreamFromBlock(tags, replay.Config.Raw));
        // Keep the recorded header intact. Building current FE args here advances tournament personality
        // selection and consumes cheat effects, so replay viewing must not call BuildSetup().
        var session = NewLoader().Load(setup);
        if (fromResults)
        {
            _replayReturnSession = Session; _replayReturnResults = Results; _replayReturnScreen = _screen;
            _replayReturnSelection = SelectedIndex; _replayReturnDriver = ResultDriver;
        }
        PlaybackSource = new ReplayInputSource(replay);
        ResultsReplay = fromResults;
        Session = session; Status = MenuStatus.Race; ReplayRequested = !fromResults;
        if (!fromResults && Flow.State == GameState.FrontEnd)
        { Flow.FrontEndDone(1, replayFromMenu: true); Flow.ArgsBuilt(); Flow.RaceLoaded(); }
    }

    /// <summary>Ends replay playback at EOF or user exit and restores the pre-playback screen/state.</summary>
    public void EndReplayPlayback()
    {
        if (PlaybackSource == null) return;
        PlaybackSource = null; ResultsReplay = false; ReplayRequested = false;
        if (_replayReturnResults != null)
        {
            Session = _replayReturnSession; Results = _replayReturnResults;
            _screen = _replayReturnScreen ?? RaceResultsScreen; SelectedIndex = _replayReturnSelection; ResultDriver = _replayReturnDriver;
            _replayReturnSession = null; _replayReturnResults = null; _replayReturnScreen = null;
            Status = MenuStatus.Results;
            return;
        }
        Session = null; Results = null; Status = MenuStatus.Menu;
        if (Flow.State == GameState.Race) { Flow.RaceEnded(); Flow.Unloaded(); }
        Normalize();
    }

    /// <summary>Installs a freshly loaded replay race after restart without changing the saved return-to-results/menu state.</summary>
    public void RestartReplayPlayback(RaceSession freshSession)
    {
        if (PlaybackSource == null) throw new InvalidOperationException("no replay is active");
        Session = freshSession;
        Status = MenuStatus.Race;
    }

    /// <summary>Item 5: list of `*.rpy`, "delete replay file from disk?" (TEXT 0x2F1), deletion and message 0x2F2.</summary>
    private void StartDeleteReplay()
    {
        if (SavesDirectory == null) { LastMessage = "Deleting is unavailable."; return; }
        OpenDialog(FrontEndDialog.List(SaveFiles.List(SavesDirectory, ReplayExtension)), d =>
        {
            if (d.Result < 0) return;
            string name = d.Items[d.Result];
            OpenConfirm(0x2F1, () =>
            {
                try { SaveFiles.Delete(SavesDirectory, name, ReplayExtension); ShowMessage(0x2F2); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowMessage(0x2EF); }
            });
        });
    }

    /// <summary>Loading a tournament: only the selected config fields (game type, style, track, units, car/gearbox/P1 colour) [ref:1895]
    /// race type = 1; the rest of the config stays. A bad file does not change state (the original: message 0x2F0 "load failed"); after
    /// success, message 0x17C "tournament loaded" and leaving the screen (code −5).</summary>
    private void LoadTournament(string name)
    {
        TouFile file; TournamentSeries series;
        try
        {
            file = TournamentSaves.Load(SavesDirectory!, name);
            series = TournamentSeries.Restore(file, file.Config[0x10], file.Config[9] == 1 ? 2 : 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { ShowMessage(0x2F0); return; }
        var c = file.Config;
        Config.GameType = FeConfig.SanitizeGameType(c[9]); Config.RaceType = 1; Config.Style = c[0xB];
        Config[0x5A] = c[0x5A]; Config.P1Car = c[0x1F]; Config.P1Transmission = c[0x20];
        if (c[0x1F] < 16) Config[0x21 + c[0x1F]] = c[0x21 + c[0x1F]];
        SanitizeCarSelection();
        Config.Track = (ushort)series.Track;
        Knockout = null; Tournament = series;
        _stack.Clear(); _screen = "main"; SelectedIndex = FirstEnabled(0);
        Normalize();
        ShowMessage(0x17C);
    }

    private void ChangeValue(MenuItem item, int direction)
    {
        if (item.ConfigOffset is { } off && item.ValueTextIds.Count > 0)
        {
            Config[off] = NextCarValue(off, Config[off], direction, item.ValueTextIds.Count) is { } carValue
                ? (byte)carValue : (byte)((Config[off] + direction + item.ValueTextIds.Count) % item.ValueTextIds.Count);
            if (off == 0x56 && Config.GameType == 1 && Config.OpponentCount == 1)
                Config.OpponentCount = direction < 0 ? (byte)0 : (byte)2; // split game type with one opponent: 0 or 2 (0x410D6x) [ref:1896]
            if (off == 0x58) Config[0x59] = Config[0x58];
            if (off == 0x55 && !IsOpponentClass(Config.OpponentCar) && Config.OpponentCount == 0) Config.OpponentCount = 2;
            if (off == 0x56 && Config.OpponentCount == 0) Config.OpponentCar = 15;
            if (off == 0x56 && Config.OpponentCount == 1 && IsOpponentClass(Config.OpponentCar)) Config.OpponentCar = 0;
            if (off == 0x56 && Config.OpponentCount == 2 && Config.OpponentCar == 15) Config.OpponentCar = 0;
            Normalize();
        }
        else if (item.Action == 15)
        {
            int car = item.ValueRaw == 1 ? Config.P1Car : Config.P2Car;
            if (ModCarOf(car) is { } modCar)
            {
                var colours = item.ValueRaw == 1 ? P1ModColours : P2ModColours;
                int n = modCar.Colors.Count;
                if (n > 0) colours[modCar.Key.Value] = (ModColour(item.ValueRaw == 1 ? 0 : 1, modCar) + direction + n) % n;
                return;
            }
            int offset = (item.ValueRaw == 1 ? 0x21 : 0x3C) + car;
            int first = _colours[car * 2], count = _colours[car * 2 + 1];
            if (count > 0) Config[offset] = (byte)(first + (Config[offset] - first + direction + count) % count);
        }
    }

    private void ResetCarSetup()
    {
        int[] offsets = _screen == "car-settings-p1" ? [0x32, 0x33, 0x34, 0x35, 0x37] : [0x4C, 0x4E, 0x4F, 0x50, 0x51];
        for (int i = 0; i < offsets.Length; i++) Config[offsets[i]] = (byte)(i is 1 or 2 ? 0 : 1);
    }

    private void EnterScreen(string target)
    {
        if (target == ControlsScreen && Config.GameType == 1) target = SplitControlsScreen;   // split-screen game type uses the split controls screen [ref:1897]
        if (_stack.Count >= 10) throw new InvalidOperationException("Original front-end screen stack limit reached.");
        _selection[_screen] = SelectedIndex;
        _stack.Push((_screen, SelectedIndex));
        if (_stack.Count == 1) _backup0 = Config.ToBytes(); else _backup1 = Config.ToBytes();
        if (_screen == ShowcaseScreen) LeaveShowcase();
        if (_screen == HistoryScreen) LeaveHistory();
        if (_screen == SlideShowScreen) LeaveSlideShow();
        if (_screen == TrackRecordsScreen) LeaveTrackRecords();
        if (target == ShowcaseScreen) EnterShowcase();
        if (target == HistoryScreen) EnterHistory();
        if (target == SlideShowScreen) EnterSlideShow();
        if (target == TrackRecordsScreen) EnterTrackRecords();
        if (target == PersonalStatsScreen) EnterPersonalStats();
        if (target == CreditsScreen) EnterCredits();
        _screen = target; SelectedIndex = FirstEnabled(0);
    }

    /// <summary>Entering the showcase screen: car from the config, page 1, entry animation from zero [ref:1898]
    /// The value is read only when it equals 3, but that value is never stored [ref:1899]
    /// (only 2 and 4 are stored) – in practice always the first player's car (+0x1F). [ref:1900]</summary>
    private void EnterShowcase()
    {
        _showcaseCar = ModCarOf(Config.P1Car) != null ? Config.P1Car : Math.Min((int)Config.P1Car, ShowcaseCarCount - 1);
        _showcasePage = 1;
    }

    /// <summary>Leaving the showcase screen writes the viewed car back to the config [ref:1901]
    /// (+0x1F) on every exit from the screen. [ref:1902]</summary>
    private void LeaveShowcase() => Config.P1Car = (byte)_showcaseCar;

    /// <summary>Entering the history screen: car from the config (the branch is dead, because [ref:1903]
    /// it never takes the value 3). [ref:1904]</summary>
    private void EnterHistory() => _historyCar = Config.P1Car >= CarCatalog.VanillaCount ? 0 : Math.Min((int)Config.P1Car, ShowcaseCarCount - 1);

    /// <summary>Writes the viewed car to the config when leaving the screen. [ref:1905]</summary>
    private void LeaveHistory() => Config.P1Car = (byte)_historyCar;

    /// <summary>Entering the slide show screen: car from the config, frame 0. [ref:1906]</summary>
    private void EnterSlideShow()
    {
        _slideCar = Config.P1Car >= CarCatalog.VanillaCount ? 0 : Math.Min((int)Config.P1Car, ShowcaseCarCount - 1);
        _slideFrame = 0;
    }

    /// <summary>Writes the viewed car to the config when leaving the screen. [ref:1907]</summary>
    private void LeaveSlideShow() => Config.P1Car = (byte)_slideCar;

    /// <summary>Entering the track records screen: lap count from the config (+0x18), records of the new track. [ref:1908]</summary>
    private void EnterTrackRecords()
    {
        RecordsLaps = Config.Laps is 2 or 8 ? Config.Laps : 4;
        _statsLoaded = false;
    }

    /// <summary>On leaving, writes the lap count to the config: +0x18 = laps [ref:1909]
    /// +0x19 = list index (4 → 0, 8 → 1, 2 → 2) from which <see cref="FeConfig.Laps"/> is restored. [ref:1910]</summary>
    private void LeaveTrackRecords()
    {
        Config.Laps = (byte)RecordsLaps;
        Config.LapsMenuIndex = (byte)(RecordsLaps switch { 4 => 0, 8 => 1, _ => 2 });
    }

    private void Back(bool cancel)
    {
        if (_stack.Count == 0)
        {
            if (Status == MenuStatus.Results) ReturnToMenu();
            else { Status = MenuStatus.Exit; Flow.FrontEndDone(0); }
            return;
        }
        // The car chosen on the showcase screen goes into the config on leaving, but before the config copy is restored [ref:1911]
        // on ESC: "done" changes the player's car, ESC undoes the browsing. The same for history and the slide show.
        if (_screen == ShowcaseScreen) LeaveShowcase();
        if (_screen == HistoryScreen) LeaveHistory();
        if (_screen == SlideShowScreen) LeaveSlideShow();
        if (_screen == TrackRecordsScreen) LeaveTrackRecords();
        if (cancel)
        {
            (_stack.Count == 1 ? _backup0 : _backup1).CopyTo(Config.Raw, 0);
            _trafficDisabled = Config.Track == 0 || Config.OpponentCount == 2;
            _catchUpDisabled = Config.GameType == 0;
        }
        var previous = _stack.Pop(); _screen = previous.Screen; SelectedIndex = previous.Selection;
        // Return to the showcase screen: the original runs its handler anew, so the car comes from the config
        // (just saved by history / slide show), and the page and animation start from the beginning.
        if (_screen == ShowcaseScreen) EnterShowcase();
        Normalize();
    }

    /// <summary>The loader of a race of this menu: the menu's own catalog when it has mod cars (the host sets the same catalog as the default one;
    /// the table-only default of a headless menu has no physics and must not be used for loading).</summary>
    private RaceLoader NewLoader() => Cars.HasModCars ? new RaceLoader(_gameDir, Cars) : new RaceLoader(_gameDir);

    /// <summary>The byte-sequence index (shared with the tournament personality [ref:1912]
    /// pick and the traffic model/colour pick) lives in the menu and is advanced by the traffic picks. [ref:1913]</summary>
    public RaceSetup BuildSetup()
    {
        PlayerNames.CopyToRace(Config, Config.GameType == 1);   // player 1's names (and 2's) into the race fields +0xE4 / +0xED [ref:1914]
        var builder = new FeRaceArgsBuilder(Cars) { SequenceIndex = _personalityIndex, ModColourOf = ModColourOfNumber };
        builder.Cheats = Cheats.Effects;
        var stream = builder.Build(Config, Knockout, Tournament);
        _personalityIndex = builder.SequenceIndex;
        // (end) in a mode other than single race / split, cheat effects are cleared after the arguments are built [ref:1915]
        if (Config.GameType > 1 || Config.RaceType is not (0 or 3)) Cheats.Effects.Clear();
        return RaceArgsParser.Parse(RaceTags.FromConstants(), stream);
    }

    /// <summary>Frozen single-race setup for a two-seat network session; leaves the interactive menu configuration untouched.</summary>
    public RaceSetup BuildNetworkSetup(string hostName)
    {
        var config = new FeConfig(Config.ToBytes()) { GameType = 1, RaceType = 0 };
        config.SetName(FeConfig.ProfileNameOffset(0), hostName);
        config.SetName(FeConfig.ProfileNameOffset(1), "");
        if (ModCarOf(config.P1Car) != null) config.P1Car = 0;   // the network lobby (0..14) has no mod cars yet
        if (ModCarOf(config.P2Car) != null) config.P2Car = 0;
        if (config.OpponentCar is > 15 and < CarCatalog.VanillaCount) config.OpponentCar = 12;   // no low classes (D..G) in the network lobby
        var builder = new FeRaceArgsBuilder(Cars) { SequenceIndex = _personalityIndex, ModColourOf = ModColourOfNumber };
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), builder.Build(config));
        var name = System.Text.Encoding.ASCII.GetBytes(hostName.Length > 12 ? hostName[..12] : hostName);
        Array.Clear(setup.Block, 0x78 + 0x34, 12);
        name.CopyTo(setup.Block, 0x78 + 0x34);
        return setup;
    }

    public void StartRace()
    {
        bool restart = Status == MenuStatus.Results && Session != null;
        // Split screen (two humans on one screen): single race, tournament and knockout (active-human mask) [ref:1916]
        // the network game types are not ported. [ref:3416]
        if (!restart && (Config.GameType > 1 || Config.RaceType is not (0 or 1 or 2)))
        { LastMessage = "Playable: single player and split screen (single race, tournament or knockout), in simulation, arcade or wild. Network games are incomplete."; return; }
        int humans = Config.GameType == 1 ? 2 : 1;
        // a game type change abandons the series in progress. [ref:1917]
        if (!restart && Tournament != null && Tournament.Humans != humans) Tournament = null;
        if (!restart && Config.RaceType != 1 && Tournament != null) Tournament = null; // a mode change clears the series [ref:1918]
        if (!restart && Config.RaceType == 2 && Knockout == null)
        { Knockout = new KnockoutSeries(humans); Config.Track = 0; }
        Normalize();
        if (!restart && Config.RaceType == 1 && Tournament == null)
        {
            // shared FE byte sequence, wrapping index; host starts its sequence at zero. [ref:1919]
            Config[4] = (byte)(FrontEndConstants.SequenceTable[_personalityIndex] % 11);
            Config[5] = Config[6] = Config[7] = 0;
            _personalityIndex = unchecked((byte)(_personalityIndex + 1));
            Tournament = new TournamentSeries(Config.Track, humans);
        }
        if (!restart && Tournament is { ResultsApplied: false, Finished: false } loaded)
        {
            // Loaded save: track from the menu, unless it is already completed (mask from the file).
            if (loaded.AvailableTracks.Contains(Config.Track)) loaded.SelectTrack(Config.Track);
            else Config.Track = (ushort)loaded.Track;
        }
        if (restart) { Knockout?.RestartStage(); Tournament?.RestartStage(); }
        var setup = restart ? Session!.Setup : BuildSetup();
        Session = NewLoader().Load(setup);
        if (Flow.State == GameState.PostRace) Flow.PostRaceDone(1);
        else Flow.FrontEndDone(1);
        Flow.ArgsBuilt(); Flow.RaceLoaded();
        _selection[_screen] = SelectedIndex;
        Sound?.Invoke(MenuSound.StartRace);
        _victory = false; Status = MenuStatus.Race; Results = null; ReplayRequested = false; ResultsReplayRecording = null;
        _recordsTotalPending = _recordsLapPending = false; _recordsQueue = null;
    }

    public void CompleteRace(RaceResults results, Replay? replay = null)
    {
        if (Status != MenuStatus.Race) throw new InvalidOperationException("No race is running.");
        if (Flow.State == GameState.Race) Flow.RaceEnded();
        Flow.Unloaded();
        Cheats.Effects.Clear(); // every entry into the front end (also after a race) clears the cheat effects [ref:1920]
        results = WithHumanNames(results);
        Knockout?.Complete(results);
        Tournament?.Complete(results);
        Results = results; Status = MenuStatus.Results; _stack.Clear();
        ResultsReplayRecording = replay;
        _victory = Knockout == null && Tournament == null && VictoryRules.Winner(results) != null;
        _screen = RaceResultsScreen; SelectedIndex = FirstEnabled(0);
        ResultDriver = Math.Max(0, LocalRacerIndex(results));   // start at the local player's place [ref:1921]
        // sets the record flags; the KO/tournament screens clear them. The PIONEER code is already zeroed by [ref:1922]
        // (Cheats.Effects.Clear above), so in the original this gate never works – here too. [ref:1923]
        _recordsTotalPending = _recordsLapPending = Knockout == null && Tournament == null
            && TrackRecordUpdate.Allowed(Config, Cheats.Effects.Pioneer);
        _recordsQueue = null; _nameDone = null; _raceStatsTrack = -1;
        _recordsMode = GameDataModes.Of(results.Racers.Select(r => r.CarType));
    }

    /// <summary>0x4B00/0x4D00: LEFT/RIGHT select the driver's place. Scrolling skips [ref:1924]
    /// the local player's place (as in the original loop), and with a single car there is nothing to scroll. [ref:1925]
    /// The selection is only view state – it does not touch the simulation.</summary>
    public void SelectResultDriver(int delta)
    {
        if (Results is not { } results || results.Racers.Count < 2 || delta == 0) return;
        int local = LocalRacerIndex(results), next = Math.Clamp(ResultDriver, 0, results.Racers.Count - 1);
        for (int step = 0; step < results.Racers.Count; step++)
        {
            next = (next + delta % results.Racers.Count + results.Racers.Count) % results.Racers.Count;
            if (next != local) break;
        }
        ResultDriver = next;
    }

    /// <summary>Index of the local player (Flags +0x1F4 bit 4) in dump order; -1 when absent.</summary>
    public static int LocalRacerIndex(RaceResults results)
    {
        for (int i = 0; i < results.Racers.Count; i++)
            if (results.Racers[i].Human) return i;
        return -1;
    }

    /// <summary>Attach a race started by a host command line rather than the menu.</summary>
    public void AdoptRace(RaceSession session)
    {
        Session = session; Status = MenuStatus.Race; Results = null;
        Flow.FrontEndDone(1); Flow.ArgsBuilt(); Flow.RaceLoaded();
    }

    private void NextKnockoutRace()
    {
        Knockout!.NextRace(); Config.Track = (ushort)Knockout.Stage;
        // StartRace normally means restart when called from results; leave that path explicitly.
        Session = null;
        StartRace();
    }

    /// <summary>The end or interruption of a split-screen series clears the race names and restores "Player1/2" in the profile. [ref:1926]</summary>
    private void EndSplitSeries()
    {
        if (Config.GameType != 1 || Tournament is not { Humans: 2 } && Knockout is not { Humans: 2 }) return;
        PlayerNames.EndSeries(Config, _text[PlayerNames.PlayerTextId]);
        PersistRequested?.Invoke();
    }

    /// <summary>A human (class 1) gets the name from race field +0xE4 + 9·number (players 1 and 2 = cars 0 and 1); [ref:1927]
    /// the others keep the name from the race configuration.</summary>
    private RaceResults WithHumanNames(RaceResults results) => results with
    {
        Racers = [.. results.Racers.Select(r => r.Human && r.CarIndex < 2 && Config.RaceName(r.CarIndex).Length > 0
            ? r with { Driver = Config.RaceName(r.CarIndex) } : r)]
    };

    public void ReturnToMenu()
    {
        EndSplitSeries();
        if (Tournament is { } tournament)
        {
            if (TournamentSummary && tournament.CloseFinal(Config)) PersistRequested?.Invoke();
            Tournament = null; Config.RaceType = 0;
        }
        if (Knockout is { } ko)
        {
            if (KnockoutSummary && ko.CloseFinal(Config)) PersistRequested?.Invoke();
            Config.RaceType = 0;
            if (!ko.Won) Config.Track = 0;
            Knockout = null;
        }
        if (Flow.State == GameState.Unload) Flow.Unloaded();
        if (Flow.State == GameState.Race) { Flow.RaceEnded(); Flow.Unloaded(); }
        if (Flow.State == GameState.PostRace) Flow.PostRaceDone(0);
        _stack.Clear(); _screen = "main"; Status = MenuStatus.Menu;
        SelectedIndex = FirstEnabled(_selection.GetValueOrDefault(_screen));
        Session = null; Results = null; ReplayRequested = false; PlaybackSource = null; Normalize();
    }

    /// <summary>Host reload after restart; Flow has already returned Pause → Race. [ref:1928]</summary>
    public void RestartPausedRace()
    {
        if (Status != MenuStatus.Race || Flow.State != GameState.Race || Session == null)
            throw new InvalidOperationException("No paused race to restart.");
        Session = NewLoader().Load(Session.Setup);
    }
}
