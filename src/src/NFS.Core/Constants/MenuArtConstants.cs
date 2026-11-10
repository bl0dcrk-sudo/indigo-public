namespace NFS.Core.Constants;

/// <summary>
/// Art (QFS/FSH shape) names and language overlays of the front end as constants. Strings are copied from the original game
/// (NUL-terminated Latin-1 behind pointer tables, or plain strings where noted); verified by tests.
/// </summary>
public static class MenuArtConstants
{
    /// <summary>
    /// Showcase car QFS names (shape <c>FEDATA/PC/ART/&lt;name&gt;.QFS</c>), 12 entries of the original game [ref:0832]
    /// (mac, ferr, f355, gt90, indigo, mus, jag, gt1, lotus, nazca, ital, isdera).
    /// </summary>
    public static IReadOnlyList<string> ShowcaseArtNames { get; } = [
        "mac", "ferr", "f355", "gt90", "indigo", "mus",
        "jag", "gt1", "lotus", "nazca", "ital", "isdera",
    ];

    /// <summary>
    /// Car history QFS names (history screen), 12 entries of the original game. [ref:0833]
    /// </summary>
    public static IReadOnlyList<string> HistoryArtNames { get; } = [
        "hismac", "hisfer", "hisfer", "hisfrd", "hisfrd", "hisfrd",
        "hisjag", "hisltus", "hisltus", "hisita", "hisita", "hisisd",
    ];

    /// <summary>
    /// Slide show QFS names, 12 entries of the original game. Frame 0 uses the name [ref:0834]
    /// frame n &gt; 0 uses <c>sprintf("%s%d", name, n + 1)</c> (format string). [ref:0835]
    /// </summary>
    public static IReadOnlyList<string> SlideShowArtNames { get; } = [
        "sldmac", "sldfer", "sld355f", "sldfrd", "sldInd", "sldmus",
        "sldjag", "sldgt1", "sldv8", "sldnaz", "sldita", "sldisd",
    ];

    /// <summary>
    /// Language overlay shapes drawn on the car history background, 6 entries of the original game; index = min(language, 5) [ref:0836]
    /// (english, german, french, spanish, italian, and spanish again for language 5).
    /// </summary>
    public static IReadOnlyList<string> HistoryOverlays { get; } = [
        "engl", "germ", "fren", "span", "itly", "span",
    ];

    /// <summary>
    /// Track info language overlays, 48 entries of the original game; index = track * 6 + language [ref:0837]
    /// (track clamped to 0..7, language to 0..5).
    /// </summary>
    public static IReadOnlyList<string> TrackInfoOverlays { get; } = [
        "engl", "germ", "fren", "span", "itly", "swed",
        "engl", "germ", "fren", "engl", "engl", "germ",
        "engl", "germ", "fren", "span", "ital", "engl",
        "engl", "germ", "fren", "span", "itly", "swed",
        "engl", "germ", "engl", "span", "engl", "germ",
        "engl", "germ", "fren", "itly", "itly", "swed",
        "engl", "engl", "fren", "engl", "engl", "engl",
        "engl", "germ", "fren", "span", "itly", "swed",
    ];

    /// <summary>
    /// Track records language overlays, 6 entries of the original game; index = language. [ref:0838]
    /// </summary>
    public static IReadOnlyList<string> TrackRecordsOverlays { get; } = [
        "engl", "germ", "fren", "span", "ital", "swed",
    ];

    /// <summary>
    /// Credits language overlays (credits screen), 6 entries of the original game; index = language. [ref:0839]
    /// </summary>
    public static IReadOnlyList<string> CreditsOverlays { get; } = [
        "engl", "germ", "fren", "span", "itly", "swed",
    ];

    /// <summary>
    /// Shapes drawn on the credits row 0x5CC: "knwd" at (100, y + 0x32) and "chy2" at (300, y + 0x1E). These are plain strings
    /// of the original game, not pointers. [ref:0840]
    /// </summary>
    public static IReadOnlyList<string> CreditsShapeNames { get; } = [
        "knwd", "chy2",
    ];

    /// <summary>
    /// Track preview shapes (panel, track screen), 8 entries of the original game; index = min(track, 7). [ref:0841]
    /// </summary>
    public static IReadOnlyList<string> TrackShapeNames { get; } = [
        "trk6", "trk5", "trk8", "trk4", "trk1", "trk3",
        "trk2", "trk7",
    ];

    /// <summary>
    /// Player car preview shapes (screens), 16 entries of the original game; index = min(car, 14). Index 15 is [ref:0842]
    /// NULL and is never read, so 15 entries are kept.
    /// </summary>
    public static IReadOnlyList<string> PlayerCarShapeNames { get; } = [
        "car1", "car9", "carc", "car7", "car8", "cara",
        "car4", "car6", "car3", "carb", "car5", "car2",
        "carf", "care", "card",
    ];

    /// <summary>
    /// Opponent car preview shapes (screen), 16 entries of the original game; index = min(car, 14). Cars 12..14 are the [ref:0843]
    /// classes A / B / C (their texts are <see cref="FrontEndConstants.ClassTexts"/>). Index 15 is NULL and is not kept.
    /// </summary>
    public static IReadOnlyList<string> OpponentCarShapeNames { get; } = [
        "car1", "car9", "carc", "car7", "car8", "cara",
        "car4", "car6", "car3", "carb", "car5", "car2",
        "clsa", "clsb", "clsc",
    ];

    /// <summary>
    /// Flickering bar shapes, 12 entries of the original game; index = 2 * bar + variant [ref:0844]
    /// (bar 0..3; the variant is 0..1 for bars 0..2 and 0..5 for bar 3, see <c>MenuAnimation.Variant</c>).
    /// </summary>
    public static IReadOnlyList<string> BarShapeNames { get; } = [
        "nedb", "neds", "spdb", "spds", "forb", "fors",
        "bspb", "bsps", "bndb", "bnds", "bfrb", "bfrs",
    ];

    /// <summary>
    /// Title colour shapes, 4 entries of the original game; index = counter % <c>MenuAnimation.TitleCount</c>. [ref:0845]
    /// </summary>
    public static IReadOnlyList<string> TitleShapeNames { get; } = [
        "gren", "redd", "yell", "blue",
    ];

    /// <summary>
    /// Words of the tournament badge on the track panel, 6 entries of the original game; index = min(language, 5). [ref:0846]
    /// </summary>
    public static IReadOnlyList<string> TournamentBadgeWords { get; } = [
        "come", "comg", "comf", "coms", "comi", "comw",
    ];

    /// <summary>
    /// Base shape names of the configuration icon row: <c>4 × 22</c> pointers of the original game, row-major. [ref:0847]
    /// Row 0 = game type (sin, two, mod, lnk, net, ova), row 1 = race type (rac, trn, nok, slt), row 2 = track (cir … mon, 8 names),
    /// row 3 = car (mac … tko, then A / B / C). A row above 3 uses row 3. A NULL pointer is kept as an empty string.
    /// </summary>
    public static IReadOnlyList<string> IconNames { get; } = [
        "sin", "two", "mod", "lnk", "net", "ova", "ova", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "rac", "trn", "nok", "slt", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "cir", "out", "Lrs", "nor", "pac", "med", "him", "mon", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
        "mac", "F50", "355", "fgt", "ind", "mus", "jag", "lgt", "lv8", "nas", "Cal", "isd", "bom", "tom", "fzr", "tko", "A", "B", "C", "", "", "",
    ];

    /// <summary>Icon name format, dark variant: "%sd" of the original game (the last character is the suffix). [ref:0848]</summary>
    public const string IconDarkFormat = "%sd";

    /// <summary>Icon name format, light variant: "%s5" of the original game (the last character is the suffix). [ref:0849]</summary>
    public const string IconLightFormat = "%s5";

    /// <summary>Padding of the two-character icon names (A5, Ad, B5, …) of the original game; MAIN.QFS keeps them as four characters. [ref:0850]</summary>
    public const string IconPadding = "  ";

    /// <summary>
    /// French replacements of icon names (language 2 only): six pairs of 12 bytes of the original game, <c>{name, replacement} [ref:0851]</c>
    /// with both as NUL-terminated strings at offsets 0 and 4 of each pair.
    /// </summary>
    public static IReadOnlyList<(string Name, string Replacement)> IconFrenchPairs { get; } = [
        ("A5", "fre1"),
        ("Ad", "fred"),
        ("B5", "fre2"),
        ("Bd", "free"),
        ("C5", "fre3"),
        ("Cd", "fref"),
    ];
}
