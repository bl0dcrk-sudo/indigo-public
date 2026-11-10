namespace NFS.Core.Constants;

/// <summary>
/// Race-time tables of the original race code (the original game), as constants. Values copied from the original game
/// (verified by tests).
/// </summary>
public static class RaceConstants
{
    /// <summary>
    /// Surface table (of the original game): 64 ints per surface type; [ref:0867]
    /// non-zero = the surface keeps tyre marks (use <c>SkidMarkTask.SurfaceTable</c>).
    /// </summary>
    public static ReadOnlySpan<int> SkidMarkSurfaces => [
        0x0, 0x1, 0x0, 0x0, 0x1, 0x0, 0x0, 0x1,
        0x0, 0x0, 0x1, 0x1, 0x1, 0x0, 0x0, 0x0,
        0x1, 0x0, 0x1, 0x1, 0x0, 0x0, 0x0, 0x0,
        0x0, 0x0, 0x1, 0x1, 0x1, 0x1, 0x0, 0x1,
        0x1, 0x0, 0x0, 0x1, 0x0, 0x0, 0x0, 0x0,
        0x0, 0x0, 0x0, 0x1, 0x1, 0x1, 0x0, 0x0,
        0x0, 0x0, 0x10000, 0x18000, 0x9999, 0x13333, 0x0, 0x8000,
        0x0, 0x4CCC, 0x8000, 0x10000, 0xCCCC, 0x13333, 0x0, 0x13333,
    ];

    /// <summary>
    /// AI driver names by personality (of the original game), 11 entries. [ref:0868]
    /// </summary>
    public static IReadOnlyList<string> AiDriverNames { get; } = [
        "Slick", "Jammin", "Shusaka", "Natasha",
        "BigGuy", "Zipper", "Cheyenne", "Crash",
        "Beefy", "Sassy", "Klutch",
    ];

    /// <summary>
    /// Car model names by car type (of the original game): char[8] per car type, 53 entries (0..0x34; cheat cars [ref:0869]
    /// reach 0x33). Read with max 8 characters, NUL-terminated.
    /// </summary>
    public static IReadOnlyList<string> CarNames { get; } = [
        "MCF1", "FF50", "F355", "GT90", "IDGO", "MACH",
        "JAGR", "LGT1", "ESPR", "NAZC", "CALA", "ISDE",
        "CHEV", "DAYT", "FUTR", "CITR", "OPP", "TRAF",
        "CITR", "COMC", "SBUS", "SEMI", "VANA", "QUAT",
        "VOLW", "YJ", "MERC", "VWFB", "MIAT", "BUG",
        "LAND", "ARMY", "BMW5", "SNOW", "mono", "limo",
        "MIAT", "SBUS", "SBUS", "SBUS", "CRAT", "CART",
        "OUTH", "TREX", "WAGO", "SOU1", "SOU2", "SOU3",
        "LOG", "BOX", "BEER", "RUIN", "SM47",
    ];

    /// <summary>
    /// Time separator after the minutes of the result screen (of the original game), per language. [ref:0870]
    /// </summary>
    public static ReadOnlySpan<byte> ResultTimeSeparatorA => [
        0x3A, 0x3A, 0x27, 0x27, 0x27, 0x2E,
    ];

    /// <summary>
    /// Time separator after the seconds of the result screen (of the original game), per language. [ref:0871]
    /// </summary>
    public static ReadOnlySpan<byte> ResultTimeSeparatorB => [
        0x2E, 0x2E, 0x22, 0x22, 0x22, 0x2C,
    ];
}
