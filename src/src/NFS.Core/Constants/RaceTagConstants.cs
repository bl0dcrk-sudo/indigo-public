namespace NFS.Core.Constants;

/// <summary>
/// Race configuration tag tables (<c>NFS.Game.Race.RaceTags</c>) as constants. Copied from the original game.
/// Checked by tests.
/// </summary>
public static class RaceTagConstants
{
    /// <summary>
    /// Keywords of the PREFS.txt files with their tags: {char* name, int tag}, in original order. [ref:0872]
    /// </summary>
    public static IReadOnlyList<(string Name, int Tag)> Keywords { get; } = [
        ("IMPORT", 0x1), ("CONTROL", 0x1), ("RACETYPE", 0x2), ("NUMLAPS", 0x3),
        ("SKILL", 0x4), ("STYLE", 0x5), ("COMMMODE", 0x6), ("RAMP", 0x7),
        ("LOCALCAR", 0x8), ("CATCHUPLOGIC", 0x9), ("TRACK", 0xA), ("TIME", 0xB),
        ("RANDOM", 0xC), ("NUMCARS", 0xD), ("REPLAY", 0xE), ("NUMSMACKABLES", 0xF),
        ("NUMPLAYERRACECARS", 0x10), ("NUMOPPONENTRACECARS", 0x11), ("OPPONENTCARTYPE", 0x12), ("LANGUAGE", 0x13),
        ("SGGE", 0x14), ("WINDOWSIZE", 0x15), ("GRAPHICSDETAIL", 0x16), ("FEI_CARDETAIL", 0x17),
        ("VIEWDISTANCE", 0x18), ("HORIZONDISPLAY", 0x19), ("MUSICLEVEL", 0x1A), ("SFXLEVEL", 0x1B),
        ("INTERACTIVE", 0x1C), ("BESTLAP", 0x1D), ("BACKWARD", 0x1F), ("MIRRORED", 0x20),
        ("WILDARCADE", 0x21), ("LINEUP", 0x22), ("CAR", 0x7B), (".carType", 0x7B),
        (".carClass", 0x7C), (".Transmission", 0x7D), (".Colour", 0x7E), (".RampSteering", 0x7F),
        (".RampGas", 0x80), (".RampBrake", 0x81), (".FrontDownForce", 0x82), (".RearDownForce", 0x83),
        (".SteeringSensitivity", 0x84), (".BrakeBias", 0x85), (".GearRatio", 0x86), ("C1_LEFT", 0x23),
        ("C1_RIGHT", 0x24), ("C1_GAS", 0x25), ("C1_BRAKE", 0x26), ("C1_HANDBRAKE", 0x2B),
        ("C1_HORN", 0x2C), ("C1_SHIFTUP", 0x2D), ("C1_SHIFTDOWN", 0x2E), ("C1_LOOKBEHIND", 0x52),
        ("C1_CYCLECAMERA", 0x53), ("C1_RESERVED2", 0x28), ("C1_RESERVED1", 0x27), ("C2_LEFT", 0x2F),
        ("C2_RIGHT", 0x30), ("C2_GAS", 0x31), ("C2_BRAKE", 0x32), ("C2_HANDBRAKE", 0x37),
        ("C2_HORN", 0x38), ("C2_SHIFTUP", 0x39), ("C2_SHIFTDOWN", 0x3A), ("C2_LOOKBEHIND", 0x54),
        ("C2_CYCLECAMERA", 0x55), ("C2_RESERVED2", 0x34), ("C2_RESERVED1", 0x33), ("MAINEXIT", 0x3B),
        ("I_UP", 0x3C), ("I_DOWN", 0x3D), ("I_LEFT", 0x3E), ("I_RIGHT", 0x3F),
        ("I_SELECT", 0x40), ("I_PAUSE", 0x41), ("I_CAPTURE", 0x43), ("I_GAMMA", 0x44),
        ("I_DETAIL", 0x45), ("I_SCREENSIZE", 0x46), ("I_INTERLACE", 0x47), ("I_VIEWDISTANCE", 0x48),
        ("I_DEBUGHUD", 0x4A), ("I_HUD", 0x4B), ("I_REARVIEW", 0x4C), ("I_MAP1", 0x4D),
        ("I_MAP2", 0x4E), ("I_HORIZON", 0x49), ("I_HANGUP", 0x4F), ("I_SFX", 0x50),
        ("I_MUSIC", 0x51), ("INTERFACE_KEY27", 0x56), ("INTERFACE_KEY28", 0x57), ("INTERFACE_KEY29", 0x58),
        ("INTERFACE_KEY30", 0x59), ("INTERFACE_KEY31", 0x5A), ("kSetDebugMode", 0x5B), ("kUp", 0x5C),
        ("kDown", 0x5D), ("kLeft", 0x5E), ("kRight", 0x5F), ("kDecreaseYaw", 0x60),
        ("kIncreaseYaw", 0x61), ("kZoomIn", 0x62), ("kZoomOut", 0x63), ("kDecreasePitch", 0x64),
        ("kIncreasePitch", 0x65), ("kDecreaseRoll", 0x66), ("kIncreaseRoll", 0x67), ("kReset", 0x68),
        ("kSaveLocation", 0x69), ("kLoadLocation", 0x6A), ("kSpeed", 0x6B), ("kRotationSwitch", 0x6C),
        ("DEBUG_KEY18", 0x6D), ("DEBUG_KEY19", 0x6E), ("DEBUG_KEY20", 0x6F), ("DEBUG_KEY21", 0x70),
        ("DEBUG_KEY22", 0x71), ("DEBUG_KEY23", 0x72), ("DEBUG_KEY24", 0x73), ("DEBUG_KEY25", 0x74),
        ("DEBUG_KEY26", 0x75), ("DEBUG_KEY27", 0x76), ("DEBUG_KEY28", 0x77), ("DEBUG_KEY29", 0x78),
        ("DEBUG_KEY30", 0x79), ("DEBUG_KEY31", 0x7A),
    ];

    /// <summary>
    /// Symbolic constants <c>GameSetup_k*</c>, <c>SC_*</c>, <c>PSX_*</c>, <c>numCars</c>: {char* name, int value}, in original order. [ref:0873]
    /// </summary>
    public static IReadOnlyList<(string Name, int Value)> Symbols { get; } = [
        ("GameSetup_kSingleRace", 0x0), ("GameSetup_kTournament", 0x1), ("GameSetup_kKnockOut", 0x2), ("GameSetup_kBeginner", 0x0),
        ("GameSetup_kExpert", 0x1), ("GameSetup_kSimStyle", 0x0), ("GameSetup_kArcadeStyle", 0x2), ("GameSetup_kNoComm", 0x0),
        ("GameSetup_kSplitScreen", 0x1), ("GameSetup_kModem", 0x2), ("GameSetup_kLink", 0x3), ("GameSetup_kNetWork", 0x4),
        ("GameSetup_kNoForce", 0x0), ("GameSetup_kCyberImpact", 0x1), ("GameSetup_kIForce", 0x2), ("numCars", 0x10),
        ("GameSetup_kMacLaren_F1", 0x0), ("GameSetup_kFerrari_F50", 0x1), ("GameSetup_kFerrari_F355", 0x2), ("GameSetup_kFord_GT90", 0x3),
        ("GameSetup_kFord_INDIGO", 0x4), ("GameSetup_kFord_Mustang", 0x5), ("GameSetup_kJaguar_XJ220", 0x6), ("GameSetup_kLotus_GT1", 0x7),
        ("GameSetup_kLotus_V8", 0x8), ("GameSetup_kItal_Nazca", 0x9), ("GameSetup_kItalD_CALA", 0xA), ("GameSetup_kIsdera_112i", 0xB),
        ("GameSetup_kBonus1", 0xC), ("GameSetup_kBonus2", 0xD), ("GameSetup_kBonus3", 0xE), ("GameSetup_kHuman", 0x1),
        ("GameSetup_kAI", 0x2), ("GameSetup_kTraffic", 0x4), ("GameSetup_kCop", 0x8), ("GameSetup_kRed", 0x0),
        ("GameSetup_kGreen", 0x1), ("GameSetup_kBlue", 0x2), ("GameSetup_kManualTrans", 0x0), ("GameSetup_kAutoTrans", 0x1),
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
        ("SC_LEFT", 0x4B), ("SC_RIGHT", 0x4D), ("PSX_UP", 0x10), ("PSX_RIGHT", 0x20),
        ("PSX_DOWN", 0x40), ("PSX_LEFT", 0x80), ("PSX_TRIANGLE", 0x1000), ("PSX_CIRCLE", 0x2000),
        ("PSX_CROSS", 0x4000), ("PSX_SQUARE", 0x8000), ("PSX_START", 0x8), ("PSX_SELECT", 0x1),
        ("PSX_LEFTSHIFT1", 0x400), ("PSX_LEFTSHIFT2", 0x100), ("PSX_RIGHTSHIFT1", 0x800), ("PSX_RIGHTSHIFT2", 0x200),
    ];

    /// <summary>Input device names by index: Null, Analog, Button, Key, Intface. [ref:0874]</summary>
    public static IReadOnlyList<string> Devices { get; } = [
        "Null", "Analog", "Button", "Key",
        "Intface",
    ];

    /// <summary>
    /// Parser targets: {int tag, int offset}, where offset = pointer − base of the race [ref:0875]
    /// config block (replay header). Duplicate tags: the last entry wins (as in the parser).
    /// </summary>
    public static IReadOnlyList<(int Tag, int Offset)> ArgTable { get; } = [
        (0x2, 0x0), (0x3, 0x4), (0x4, 0x8), (0x5, 0xC),
        (0x6, 0x10), (0x8, 0x14), (0x9, 0x18), (0x14, 0x20),
        (0xA, 0x24), (0xB, 0x28), (0xC, 0x2C), (0xD, 0x68),
        (0x10, 0x6C), (0x11, 0x70), (0x12, 0x74), (0xF, 0x4F8),
        (0x22, 0x478), (0x13, 0x3C), (0x1E, 0x40), (0x1D, 0x44),
        (0x15, 0x48), (0x16, 0x4C), (0x17, 0x50), (0x18, 0x54),
        (0x19, 0x58), (0x1A, 0x5C), (0x1B, 0x60), (0x1C, 0x64),
        (0x7B, 0x78), (0x7C, 0x7C), (0x7D, 0x80), (0x7F, 0x88),
        (0x80, 0x8C), (0x81, 0x90), (0x7E, 0x84), (0x82, 0x94),
        (0x83, 0x98), (0x84, 0x9C), (0x85, 0xA0), (0x86, 0xA4),
        (0x87, 0xA8), (0x1F, 0x30), (0x20, 0x34), (0x21, 0x38),
    ];
}
