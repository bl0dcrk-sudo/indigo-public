namespace NFS.Core.Constants;

/// <summary>
/// AI tables of the original simulation (the original game), as constants. Values copied from the original game
/// (verified by tests). Fixed-point values are 16.16 (0x10000 = 1.0).
/// </summary>
public static class AiConstants
{
    /// <summary>
    /// In the original game: int[15][2] {sim, arcade}, 16.16 multiplier of the AI acceleration curve [ref:0747]
    /// One car model per line. [ref:0748]
    /// </summary>
    public static ReadOnlySpan<int> AiAccelScale => [
        0x10F5C, 0x10B85,
        0x10F5C, 0x10B85,
        0x10F5C, 0x1051E,
        0x111EB, 0x10B85,
        0x10F5C, 0x10B85,
        0x10F5C, 0x1051E,
        0xFAE1, 0xFAE1,
        0x111EB, 0x10B85,
        0x10A3D, 0x10000,
        0x10000, 0x1051E,
        0x1147A, 0x1051E,
        0xFD70, 0xF5C2,
        0xFD70, 0xF5C2,
        0xFD70, 0xF5C2,
        0x10F5C, 0x10B85,
    ];

    /// <summary>
    /// In the original game: 13 letters (a..m) × 2 skill levels, 16.16 speed factor [ref:0749]
    /// (indexed by letter and skill level). One letter per line. [ref:0750]
    /// </summary>
    public static ReadOnlySpan<int> AiSkillSpeed => [
        0x10000, 0x10000,
        0xF851, 0xFAE1,
        0xF0A3, 0xF5C2,
        0xE8F5, 0xF0A3,
        0xE147, 0xEB85,
        0xD999, 0xE666,
        0xD1EB, 0xE666,
        0xCA3D, 0xE147,
        0xC28F, 0xE147,
        0xBAE1, 0xE147,
        0xB5C2, 0xE147,
        0xB5C2, 0xE147,
        0x10000, 0x10000,
    ];

    /// <summary>
    /// In the original game: 13 personality letters × 2 skill levels, 16.16. One letter per line. [ref:0751]
    /// </summary>
    public static ReadOnlySpan<int> AiSkillAcc => [
        0x10000, 0x10000,
        0xFAE1, 0xFD70,
        0xF5C2, 0xFAE1,
        0xF0A3, 0xF851,
        0xEB85, 0xF5C2,
        0xE666, 0xF333,
        0xE147, 0xF0A3,
        0xDC28, 0xEE14,
        0xD70A, 0xEB85,
        0xD1EB, 0xE8F5,
        0xCCCC, 0xE666,
        0xC7AE, 0xE3D7,
        0x10000, 0x10000,
    ];

    /// <summary>
    /// In the original game: 15 tracks × 2 skill levels; an extra factor is applied [ref:0752]
    ///  while progress &gt; 100. One track per line. [ref:0753]
    /// </summary>
    public static ReadOnlySpan<int> AiTrackSkill => [
        0xEB85, 0xFAE1,
        0x10000, 0x10000,
        0xD999, 0x10000,
        0xCCCC, 0x10000,
        0xDEB8, 0x10000,
        0xCCCC, 0x10000,
        0xCCCC, 0x10000,
        0xCCCC, 0x10000,
        0xD999, 0x10000,
        0x10000, 0x10000,
        0xF851, 0xFAE1,
        0xF0A3, 0xF5C2,
        0xE8F5, 0xF0A3,
        0xE147, 0xEB85,
        0xD999, 0xE666,
    ];

    /// <summary>
    /// In the original game: 4 rows × 12 personality letters ('a' = 0). [ref:0754]
    /// A car's letter follows from its grid slot: <c>slots[gameType][numRacers − 1 − gridSlot]</c>. [ref:0755]
    /// PRSONAL.DAT overwrites this table with an identical copy. [ref:0756]
    /// </summary>
    public static ReadOnlySpan<byte> PersonalitySlots => [
        (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', (byte)'g', (byte)'h', (byte)'i', (byte)'j', (byte)'k', (byte)'l',
        (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', (byte)'g', (byte)'h', (byte)'i', (byte)'j', (byte)'k', (byte)'l',
        (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m', (byte)'m',
        (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', (byte)'g', (byte)'h', (byte)'i', (byte)'j', (byte)'k', (byte)'l',
    ];
}
