namespace NFS.Core.Constants;

/// <summary>
/// Numeric tables of the front end (race setup built from the menu) as constants. [ref:0782]
/// Values copied from the original game (verified by tests).
/// </summary>
public static class FrontEndConstants
{
    /// <summary>
    /// Menu track index → race track number, <c>int[8]</c> in the original game. The original [ref:0783]
    /// reads only the low word; the port treats index 8 as out of range.
    /// </summary>
    public static ReadOnlySpan<int> TrackMap => [
        0x0, 0x2, 0x4, 0x5, 0x3, 0x6, 0x7, 0x8,
    ];

    /// <summary>Downforce per setup level, tags 0x82 (front) and 0x83 (rear): <c>int[3]</c> in the original game. [ref:0784]</summary>
    public static ReadOnlySpan<int> Downforce => [
        0x10000, 0x18000, 0x20000,
    ];

    /// <summary>Tyre grip per setup level, tag 0x84: <c>int[3]</c> in the original game. [ref:0785]</summary>
    public static ReadOnlySpan<int> Tyre => [
        0xCCCC, 0x10000, 0x13333,
    ];

    /// <summary>Brake force per setup level, tag 0x85: <c>int[3]</c> in the original game. [ref:0786]</summary>
    public static ReadOnlySpan<int> Brake => [
        0x3333, 0x0, -0x3333,
    ];

    /// <summary>Gear ratio per setup level, tag 0x86: <c>int[3]</c> in the original game. [ref:0787]</summary>
    public static ReadOnlySpan<int> Gear => [
        0xD999, 0x10000, 0x12666,
    ];

    /// <summary>
    /// Opponent colour table per car, 16 pairs <c>{first colour, colour count}</c> in the original game. [ref:0788]
    /// </summary>
    public static ReadOnlySpan<byte> ColourTable => [
        0x3C, 0x6, 0x1E, 0x5, 0x52, 0x5, 0x42, 0x6, 0x1E, 0x0, 0x38, 0x4, 0x18, 0x6, 0x33, 0x5,
        0x2A, 0x9, 0x23, 0x7, 0x4D, 0x5, 0x48, 0x5, 0x1E, 0x0, 0x18, 0x0, 0x57, 0x0, 0x0, 0x0,
    ];

    /// <summary>Knockout opponent models, class A (player model in class A), <c>int16[11]</c> in the original game. [ref:0789]</summary>
    public static ReadOnlySpan<short> KnockoutOpponentsA => [
        0x8, 0x5, 0xA, 0x2, 0x5, 0x8, 0x2, 0xA, 0x0, 0x0, 0x1308,
    ];

    /// <summary>Knockout opponent models, class B, <c>int16[11]</c> in the original game. [ref:0790]</summary>
    public static ReadOnlySpan<short> KnockoutOpponentsB => [
        0x4, 0x9, 0x6, 0xB, 0x4, 0x9, 0xB, 0x6, 0x5, 0xA, 0x2,
    ];

    /// <summary>Knockout opponent models, class C (neither A nor B), <c>int16[11]</c> in the original game. [ref:0791]</summary>
    public static ReadOnlySpan<short> KnockoutOpponentsC => [
        0x7, 0x3, 0x0, 0x7, 0x3, 0x1, 0x1, 0x0, 0x9, 0x6, 0xB,
    ];

    /// <summary>Tournament opponent models, class A, <c>int16[11]</c> in the original game. [ref:0792]</summary>
    public static ReadOnlySpan<short> TournamentOpponentsA => [
        0x5, 0xA, 0x2, 0x5, 0x8, 0x2, 0xA, 0xC, 0x8, 0xE, 0xD,
    ];

    /// <summary>Tournament opponent models, class B, <c>int16[11]</c> in the original game. [ref:0793]</summary>
    public static ReadOnlySpan<short> TournamentOpponentsB => [
        0x9, 0x6, 0xB, 0x4, 0x9, 0xB, 0x6, 0xC, 0x5, 0x8, 0x3,
    ];

    /// <summary>Tournament opponent models, class C, <c>int16[11]</c> in the original game. [ref:0794]</summary>
    public static ReadOnlySpan<short> TournamentOpponentsC => [
        0x3, 0x0, 0x7, 0x3, 0x1, 0x1, 0x0, 0xC, 0x8, 0xE, 0xD,
    ];

    /// <summary>
    /// Traffic model pools per menu track 1..7 (pool locations [ref:0795]
    /// in the original game). Each pool is the original bytes as they are: byte 0 = count, then <c>count</c> model [ref:0796]
    /// numbers, so the array length is <c>1 + count</c>.
    /// </summary>
    public static IReadOnlyList<byte[]> TrafficPools { get; } = [
        [0x8, 0x13, 0x15, 0x16, 0x19, 0x1B, 0x1D, 0x1E, 0x1F],
        [0x5, 0x13, 0x16, 0x19, 0x1E, 0x1F],
        [0x8, 0x12, 0x17, 0x18, 0x1A, 0x1B, 0x1D, 0x1F, 0x20],
        [0xB, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1C, 0x20],
        [0x6, 0x12, 0x19, 0x1A, 0x1C, 0x1D, 0x20],
        [0x5, 0x13, 0x16, 0x19, 0x1E, 0x21],
        [0x4, 0x22, 0x23, 0x22, 0x23],
    ];

    /// <summary>Traffic colours: 16 bytes in the original game. [ref:0797]</summary>
    public static ReadOnlySpan<byte> TrafficColours => [
        0x0, 0x2, 0x5, 0x6, 0x9, 0xA, 0xB, 0xD, 0xF, 0x10, 0x11, 0x12, 0x13, 0x15, 0x16, 0x17,
    ];

    /// <summary>
    /// Shared sequence table in the original game, read through the one-byte counter [ref:0798]
    /// (<c>(byte)</c>; the counter wraps at 256, so 256 bytes are indexable, up to where <c>AudioConstants.Curve</c> starts). [ref:0799]
    /// Used by the traffic model/colour pickers, the tournament personality [ref:0800]
    /// and the AI personality rotation.
    /// </summary>
    public static ReadOnlySpan<byte> SequenceTable => [
        0x5A, 0x92, 0xE6, 0x42, 0x36, 0xCD, 0xBB, 0x73, 0xA4, 0x96, 0x57, 0xDE, 0x8F, 0x5F, 0x7D, 0x3D,
        0x24, 0x61, 0xB9, 0xD7, 0xEF, 0xD1, 0xA5, 0xA1, 0x7, 0x75, 0xA, 0x60, 0x54, 0x68, 0xB, 0x99,
        0x97, 0x29, 0x8A, 0xFE, 0x89, 0xEB, 0xBE, 0x2D, 0xF2, 0xEE, 0xAC, 0xD8, 0xF7, 0xDC, 0x18, 0xF4,
        0xE7, 0x9F, 0x6F, 0xBC, 0x1F, 0x6A, 0xEA, 0x7A, 0xFB, 0x62, 0x71, 0x15, 0x40, 0x7F, 0x44, 0xBD,
        0x81, 0xC6, 0xFD, 0xCC, 0x4A, 0xE9, 0x23, 0x19, 0xED, 0xFF, 0x8D, 0xC1, 0xAA, 0xE, 0xD5, 0x8C,
        0x21, 0xE0, 0xDF, 0xD6, 0xE3, 0x64, 0xC8, 0x52, 0xCE, 0xB7, 0xA0, 0x43, 0x13, 0xBA, 0xAB, 0x65,
        0x31, 0x26, 0xBF, 0xD2, 0xD9, 0xC5, 0x77, 0xDB, 0x1D, 0x1C, 0x46, 0x3, 0x7B, 0x82, 0x94, 0xB6,
        0x45, 0x4F, 0xB8, 0xD3, 0x87, 0x12, 0xA6, 0x32, 0x80, 0xA7, 0x74, 0x59, 0x33, 0x3C, 0xAE, 0xD0,
        0x78, 0x34, 0x6B, 0xE8, 0x95, 0x48, 0x91, 0x5B, 0x93, 0x27, 0x9, 0xC0, 0x8E, 0xC2, 0x53, 0x56,
        0x17, 0xD, 0x6D, 0x2A, 0x83, 0xA8, 0x9D, 0x3F, 0xA3, 0xF6, 0x9B, 0x4, 0x51, 0xE2, 0xE5, 0x41,
        0x14, 0xD4, 0x47, 0x30, 0x2F, 0x8, 0x69, 0x10, 0xB2, 0x16, 0xCA, 0xF, 0xDD, 0x1A, 0xE4, 0x22,
        0x20, 0x98, 0xF5, 0xDA, 0x50, 0x2, 0xFA, 0xF9, 0x4C, 0x28, 0x7E, 0xB0, 0x55, 0x67, 0xC9, 0x4D,
        0xB3, 0xC7, 0x6, 0xE1, 0x90, 0x5E, 0x0, 0xFC, 0xF3, 0x70, 0x35, 0x1E, 0xC, 0x11, 0x25, 0x6E,
        0xB4, 0xF8, 0xEC, 0x86, 0x66, 0x2E, 0x3E, 0x49, 0x2B, 0x58, 0x5D, 0x63, 0x9C, 0xA2, 0xB1, 0x2C,
        0x8B, 0x4E, 0x76, 0x3A, 0x5C, 0x37, 0x9A, 0xF0, 0x5, 0x9E, 0xAD, 0x84, 0x79, 0xB5, 0xC4, 0xCB,
        0x3B, 0xCF, 0x39, 0x1, 0xA9, 0xAF, 0xC3, 0x88, 0x38, 0x72, 0x1B, 0x85, 0x4B, 0xF1, 0x6C, 0x7C,
    ];

    /// <summary>Colour index of disabled menu items: <c>int16</c> in the original game. [ref:0801]</summary>
    public const int DisabledColour = 6;

    /// <summary>
    /// Showcase value texts, page 1 (GENERAL): first TEXT id per car, <c>int[12]</c> in the original game; seven values follow from this id. [ref:0802]
    /// Indexed by the showcase car 0..11 (<c>MenuController.ShowcaseCarCount</c>). [ref:0803]
    /// </summary>
    public static ReadOnlySpan<int> ShowcasePage1Texts => [
        389, 396, 459, 403, 445, 452, 410, 417, 424, 466, 431, 438,
    ];

    /// <summary>
    /// Showcase value texts, page 2 (PERFORMANCE): first TEXT id per car, <c>int[12]</c> in the original game. Page 2 has [ref:0804]
    /// only five values; the last two are the blank text (TEXT 0x157).
    /// </summary>
    public static ReadOnlySpan<int> ShowcasePage2Texts => [
        572, 577, 622, 582, 612, 617, 587, 592, 597, 627, 602, 607,
    ];

    /// <summary>
    /// Showcase value texts, page 3 (MECHANICAL): first TEXT id per car, <c>int[12]</c> in the original game. [ref:0805]
    /// </summary>
    public static ReadOnlySpan<int> ShowcasePage3Texts => [
        481, 488, 551, 495, 537, 544, 502, 509, 516, 558, 523, 530,
    ];

    /// <summary>
    /// Car history photo captions (history screen): three tables of TEXT ids, <c>int16[3][12]</c> in the original game. [ref:0806]
    /// Caption <c>c</c> of car <c>a</c> is at <c>[c * 12 + a]</c>; stored unsigned (the port always read them as ushort). [ref:0807]
    /// </summary>
    public static ReadOnlySpan<ushort> HistoryCaptionTexts => [
        783, 798, 798, 786, 786, 786, 789, 792, 792, 795, 795, 801,
        784, 799, 799, 787, 787, 787, 790, 793, 793, 796, 796, 802,
        785, 800, 800, 788, 788, 788, 791, 794, 794, 797, 797, 803,
    ];

    /// <summary>
    /// Car history timeline rows: 12 pairs <c>{first TEXT id, row count}</c> as <c>int32[24]</c>; the pair of car <c>a</c> is at [ref:0808]
    /// <c>[2 * a]</c> and <c>[2 * a + 1]</c>. The timeline lines are the ids <c>first .. first + count - 1</c>.
    /// </summary>
    public static ReadOnlySpan<int> HistoryTimeline => [
        810, 6, 821, 6, 821, 6,
        804, 6, 804, 6, 804, 6,
        833, 4, 816, 5, 816, 5,
        827, 6, 827, 6, 837, 4,
    ];

    /// <summary>
    /// Track difficulty TEXT ids (novice 0x9F / intermediate 0xA0 / advanced 0xA1), <c>int16[8]</c> in the original game [ref:0809]
    /// one per track 0..7 (drawn at x 0x268, y 0x3F). [ref:0810]
    /// </summary>
    public static ReadOnlySpan<ushort> TrackInfoDifficultyTexts => [
        159, 159, 160, 160, 160, 161, 161, 160,
    ];

    /// <summary>
    /// Lap counts per menu laps index 0..2: 4, 8, 2 (<c>int[3]</c> in the original game; sets [ref:0811]
    /// <c>Config.Laps = LapCounts[min(LapsMenuIndex, 2)]</c>).
    /// </summary>
    public static ReadOnlySpan<int> LapCounts => [
        4, 8, 2,
    ];

    /// <summary>
    /// Opponent class label TEXT ids (opponent screen), <c>int[12]</c> in the original game. Cars 12..14 (classes [ref:0812]
    /// A / B / C) use four ids each: <c>[(car - 12) * 4 + k]</c> for the four label rows.
    /// </summary>
    public static ReadOnlySpan<int> ClassTexts => [
        93, 94, 96, 100, 97, 99, 102, 104, 95, 98, 101, 103,
    ];

    /// <summary>
    /// Rating TEXT ids per car (player and opponent screens, rating label), <c>int[12]</c> in the original game; [ref:0813]
    /// used for cars 0..11 only.
    /// </summary>
    public static ReadOnlySpan<int> RatingTexts => [
        121, 121, 123, 121, 122, 123, 122, 121, 123, 122, 123, 122,
    ];

    /// <summary>
    /// Performance graph bar values per car (shape "grpg", panel, car screens), <c>int[12]</c> in the original game. [ref:0814]
    /// Bar x = panel x + 0x1A - (12 - value) * 9.
    /// </summary>
    public static ReadOnlySpan<int> GraphBarGreen => [
        10, 12, 1, 11, 7, 5, 9, 6, 3, 4, 2, 8,
    ];

    /// <summary>
    /// Performance graph bar values per car (shape "grpb"), <c>int[12]</c> in the original game; see <see cref="GraphBarGreen"/>. [ref:0815]
    /// </summary>
    public static ReadOnlySpan<int> GraphBarBlue => [
        12, 8, 4, 11, 5, 3, 10, 7, 1, 6, 2, 9,
    ];

    /// <summary>
    /// Performance graph bar values per car (shape "grpr"), <c>int[12]</c> in the original game; see <see cref="GraphBarGreen"/>. [ref:0816]
    /// </summary>
    public static ReadOnlySpan<int> GraphBarRed => [
        12, 8, 5, 2, 11, 4, 3, 10, 7, 6, 9, 1,
    ];

    /// <summary>
    /// Performance graph bar values per car (shape "grpy"), <c>int[12]</c> in the original game; see <see cref="GraphBarGreen"/>. [ref:0817]
    /// </summary>
    public static ReadOnlySpan<int> GraphBarYellow => [
        9, 8, 4, 6, 12, 5, 1, 11, 3, 10, 7, 2,
    ];

    /// <summary>
    /// Y of the configuration icon rows 0..5 relative to the icon shape, <c>int32[6]</c> in the original game. [ref:0818]
    /// </summary>
    public static ReadOnlySpan<int> IconYOffsets => [
        -9, -9, 69, 176, 269, 363,
    ];

    /// <summary>
    /// Tournament opponent class per menu track 0..8 (icon row 5 in a tournament: class + 0x10), <c>int16[9]</c> in the original game. [ref:0819]
    /// The table ends where <see cref="TournamentOpponentsC"/> starts. [ref:0820]
    /// </summary>
    public static ReadOnlySpan<short> TournamentOpponentClasses => [
        0, 0, 1, 2, 0, 1, 2, 0, 0,
    ];

    /// <summary>
    /// Top Y of the vertical "cancel" tab text of the list dialog, one per language 0..5: [ref:0821]
    /// <c>uint16</c> at offset 2 * (language + 2) in the original game (the first two entries of that table are not used). [ref:0822]
    /// </summary>
    public static ReadOnlySpan<int> ListCancelTopY => [
        118, 118, 108, 118, 108, 118,
    ];

    /// <summary>
    /// Capital letter for "yes" in the confirm dialog, per language 0..5: the byte at offset + language in the original game. [ref:0823]
    /// </summary>
    public static ReadOnlySpan<char> YesUpper => [
        'Y', 'J', 'O', 'S', 'S', 'J',
    ];

    /// <summary>
    /// Small letter for "yes" in the confirm dialog, per language 0..5: the byte at offset + language in the original game; see <see cref="YesUpper"/>. [ref:0824]
    /// </summary>
    public static ReadOnlySpan<char> YesLower => [
        'y', 'j', 'o', 's', 's', 'j',
    ];
}
