namespace NFS.Core.Constants;

/// <summary>
/// Text tables of the front end and the key/cheat names as constants. Strings are copied from the original game
/// (NUL-terminated Latin-1 behind pointer tables); verified by tests.
/// </summary>
public static class TextConstants
{
    /// <summary>
    /// Track names used in the <c>STF</c> file names, 8 pointers in [ref:0892]
    /// the original game (the file name is upper-cased).
    /// </summary>
    public static IReadOnlyList<string> TrackStatNames { get; } = [
        "oval", "oz", "last", "nort",
        "pac", "med", "myst", "mono",
    ];

    /// <summary>
    /// Track info art names (suffix "info"), 8 entries of the original game. [ref:0893]
    /// </summary>
    public static IReadOnlyList<string> TrackInfoArt { get; } = [
        "oval", "oz", "lrt", "nort",
        "pac", "med", "myst", "mono",
    ];

    /// <summary>
    /// Track records art names (suffix "rec"), 8 entries of the original game. [ref:0894]
    /// </summary>
    public static IReadOnlyList<string> TrackRecordsArt { get; } = [
        "oval", "oz", "last", "nort",
        "pac", "med", "myst", "mono",
    ];

    /// <summary>Name of the special driver (car 0x12 and 0x24): inline string of the original game. [ref:0895]</summary>
    public const string SpecialDriverName = "Jacques";

    /// <summary>
    /// Personal statistics file names, 8 entries of the original game; the file is [ref:0896]
    /// "prh01".."prh08" with the extension and the backward prefix added by the caller.
    /// </summary>
    public static IReadOnlyList<string> PersonalStatNames { get; } = [
        "prh01", "prh02", "prh03", "prh04",
        "prh05", "prh06", "prh07", "prh08",
    ];

    /// <summary>
    /// Personal statistics screen overlays per language (index), 6 entries of the original game. [ref:0897]
    /// </summary>
    public static IReadOnlyList<string> PersonalStatsOverlays { get; } = [
        "esta", "gsta", "fsta", "ssta",
        "ista", "wsta",
    ];

    /// <summary>
    /// Cheat codes, 17 entries of the original game. The strings are stored encrypted: each byte is the character + 9 [ref:0898]
    /// (the port decodes them in <c>CheatCodes</c>).
    /// </summary>
    public static IReadOnlyList<string> CheatCodes { get; } = [
        "Oc[;999", "]XVK\u005C]XWN", "KXVKN[", "QXUUb`XXM",
        "YRXWNN[", "\u005CURY", "\u005CUX]", "VJM",
        "[JRW", "LQJ\u005CN", "TLSXWN\u005C", "[^\u005CQQX^[",
        "[NaQX^[", "VJMUJWM", "\u005CLQXXUcXWN", "_RY",
        "[XJM[JPN",
    ];

    /// <summary>
    /// Key names <c>SC_*</c> with their scancodes (of the original game): 82 entries of <c>{char* name, int scan}</c>. [ref:0899]
    /// </summary>
    public static IReadOnlyList<(string Name, int Scan)> ScancodeNames { get; } = [
        ("SC_ESC", 0x1), ("SC_1", 0x2), ("SC_2", 0x3), ("SC_3", 0x4),
        ("SC_4", 0x5), ("SC_5", 0x6), ("SC_6", 0x7), ("SC_7", 0x8),
        ("SC_8", 0x9), ("SC_9", 0xA), ("SC_0", 0xB), ("SC_MINUS", 0xC),
        ("SC_EQUAL", 0xD), ("SC_BS", 0xE), ("SC_TAB", 0xF), ("SC_Q", 0x10),
        ("SC_W", 0x11), ("SC_E", 0x12), ("SC_R", 0x13), ("SC_T", 0x14),
        ("SC_Y", 0x15), ("SC_U", 0x16), ("SC_I", 0x17), ("SC_O", 0x18),
        ("SC_P", 0x19), ("SC_LBRACKET", 0x1A), ("SC_RBRACKET", 0x1B), ("SC_ENTER", 0x1C),
        ("SC_CTRL", 0x1D), ("SC_A", 0x1E), ("SC_S", 0x1F), ("SC_D", 0x20),
        ("SC_F", 0x21), ("SC_G", 0x22), ("SC_H", 0x23), ("SC_J", 0x24),
        ("SC_K", 0x25), ("SC_L", 0x26), ("SC_COLON", 0x27), ("SC_QUOTE", 0x28),
        ("SC_BQUOTE", 0x29), ("SC_LSHIFT", 0x2A), ("SC_Z", 0x2C), ("SC_X", 0x2D),
        ("SC_C", 0x2E), ("SC_V", 0x2F), ("SC_B", 0x30), ("SC_N", 0x31),
        ("SC_M", 0x32), ("SC_COMMA", 0x33), ("SC_PERIOD", 0x34), ("SC_QUESTION", 0x35),
        ("SC_RSHIFT", 0x36), ("SC_PRTSRC", 0x37), ("SC_ALT", 0x38), ("SC_SPACE", 0x39),
        ("SC_CAPLOCK", 0x3A), ("SC_F1", 0x3B), ("SC_F2", 0x3C), ("SC_F3", 0x3D),
        ("SC_F4", 0x3E), ("SC_F5", 0x3F), ("SC_F6", 0x40), ("SC_F7", 0x41),
        ("SC_F8", 0x42), ("SC_F9", 0x43), ("SC_F10", 0x44), ("SC_NUMLOCK", 0x45),
        ("SC_SCROLLLOCK", 0x46), ("SC_HOME", 0x47), ("SC_UP", 0x48), ("SC_PGUP", 0x49),
        ("SC_NPMINUS", 0x4A), ("SC_CENTER", 0x4C), ("SC_PLUS", 0x4E), ("SC_END", 0x4F),
        ("SC_DOWN", 0x50), ("SC_PGDN", 0x51), ("SC_INS", 0x52), ("SC_DEL", 0x53),
        ("SC_LEFT", 0x4B), ("SC_RIGHT", 0x4D),
    ];

    /// <summary>
    /// Track lengths in kilometres per menu track 0..7 (track 0 = 5.1 …), 8 pointers in [ref:0900]
    /// of the original game. The unit text is TEXT 0x9D.
    /// </summary>
    public static IReadOnlyList<string> TrackLengthsKm { get; } = [
        "5.1", "7.5", "7.5", "7.5", "8.1", "6.5", "7.6", "7.3",
    ];

    /// <summary>
    /// Track lengths in miles per menu track 0..7 (used when Config[0x5A] == 0), 8 entries of the original game. [ref:0901]
    /// The unit text is TEXT 0x9E.
    /// </summary>
    public static IReadOnlyList<string> TrackLengthsMiles { get; } = [
        "3.2", "4.7", "4.7", "4.7", "5.0", "4.0", "4.7", "4.5",
    ];
}
