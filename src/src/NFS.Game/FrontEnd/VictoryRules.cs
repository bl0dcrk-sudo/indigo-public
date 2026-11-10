using NFS.Game.Race;

namespace NFS.Game.FrontEnd;

/// <summary>Victory screen of statdisp (called only before the results table) [ref:2025]
/// determines when it appears, which shapes it draws and when it ends. Data from the original executable [ref:2026]
/// The host draws the screen (<c>VictoryScreen</c> in NFS.Desktop). [ref:2027]</summary>
public static class VictoryRules
{
    /// <summary>128 Hz clock after which the screen ends by itself (<c>ticks above 0x500 and flag = 0</c>; [ref:2028] [ref:3422]
    /// the flag is reset after tick 0x7F, so that always happens earlier). [ref:3423]</summary>
    public const int DurationTicks = 0x500, TicksPerSecond = 128;
    /// <summary>After tick 0x7F the original plays the narrator speech (c00; c01/c02 in network mode) – the port has no [ref:2029] [ref:3424]
    /// narrator; the constant only documents the moment.</summary>
    public const int SpeechTick = 0x80;
    /// <summary>The animation frame grows every 10 ticks (% 10 == 0), up to 0x23; alpha = frame × 256 / 0x23. [ref:2030] [ref:3425]</summary>
    public const int FrameTicks = 10, LastFrame = 0x23;
    /// <summary>Each subsequent call adds 0x1E to the phase (the same constant as the showcase logo). [ref:2031]</summary>
    public const int PhaseStep = 0x1E;
    /// <summary>Car model &lt; 0xC (<c>&lt; 0xc</c>) has the brand shape; bonus cars (≥ 0xC) do not. [ref:3426]</summary>
    public const int BrandModels = 0xC;
    /// <summary>Track title: TEXT 0x3D + the track's menu index, drawn at (0x7A, 0xBE) in font 0x12, alignment 5, colour 2. [ref:2032]</summary>
    public const int TrackTextBase = 0x3D, TrackTextX = 0x7A, TrackTextY = 0xBE, TrackTextFont = 0x12, TrackTextAlign = 5, TrackTextColour = 2;

    /// <summary>(table, 12 entries): brand QFS for model 0..11 (<c>FEDATA/PC/ART/&lt;name&gt;.QFS [ref:2033]</c>
    /// format "%s%s.qfs"). Models 1 and 2 (F50, F355) share the same "ferr" file. [ref:2034]</summary>
    public static IReadOnlyList<string> BrandArt { get; } =
        ["mac", "ferr", "ferr", "gt90", "indigo", "mus", "jag", "gt1", "lotus", "nazca", "ital", "isdera"];

    /// <summary>Table (15 entries): car photo shape in VICTORY.QFS for model 0..14 (flag 0x400). [ref:2035]</summary>
    public static IReadOnlyList<string> CarPhotoShapes { get; } =
        ["mcf1", "ff50", "f355", "fgt9", "find", "fmus", "jagr", "lgt1", "lesp", "nazc", "cala", "isde", "bbfs", "dayt", "fzr2"];

    /// <summary>Table (6 entries, index = language E/G/F/S/I/W): congratulations text in VICTORY.QFS (flag 0x2800). [ref:2036]</summary>
    public static IReadOnlyList<string> CongratsShapes { get; } = ["econ", "gcon", "fcon", "scon", "icon", "wcon"];

    /// <summary>(table, 12 entries): x offset of the brand shape; non-zero only for models 7 and 8 (−10). [ref:2037]</summary>
    public static int BrandDx(int model) => model is 7 or 8 ? -10 : 0;
    /// <summary>Y offset of the brand shape relative to its position in the QFS (−3). [ref:2038]</summary>
    public const int BrandDy = -3;

    /// <summary>mode 1 (..): model 0, 3, 4, 5 → "cpg7", the others (1, 2, 6..11) → "car7". [ref:2039]</summary>
    public static string BrandShape(int model) => model is 0 or 3 or 4 or 5 ? "cpg7" : "car7";

    /// <summary>(table): 0..8 → track index in the menu. [ref:2040]</summary>
    public static int MenuTrack(int trackId) => trackId is < 0 or > 8 ? 0 : new[] { 0, 0, 1, 4, 2, 3, 5, 6, 7 }[trackId];

    /// <summary>The winner for whom the original shows the screen: place 1, a human (flag +0x1F4 bit 4), race state 2 [ref:2041]
    /// (finished) and at least 2 cars (<c>cars &gt; 1</c>). Otherwise <c>null</c> – the screen does not appear. The network condition [ref:3427]
    /// (winner = local player, mode 1: index &lt; 2) is omitted – the port does not send network results.</summary>
    public static RacerResult? Winner(RaceResults results)
    {
        if (results.Racers.Count < 2) return null;
        RacerResult? winner = null;
        foreach (var r in results.Racers)
            if (r.Place == 1 && r.Human && r.RaceState == 2) winner = r;
        return winner;
    }

    /// <summary>Animation frame after <paramref name="ticks"/> ticks; without animation (config +0x12D = 0, == 0) the last frame at once. [ref:2042]</summary>
    public static int Frame(long ticks, bool animated) =>
        animated ? (int)Math.Min(Math.Max(ticks, 0) / FrameTicks, LastFrame) : LastFrame;
    /// <summary>"Squash" phase when drawing a frame: 0x1E per draw, the first one has 0x1E.</summary>
    public static int Phase(int frame) => PhaseStep * (frame + 1);
    /// <summary>Full-width alpha level: frame × 256 / 0x23.</summary>
    public static int Level(int frame) => Math.Min(frame, LastFrame) * 256 / LastFrame;
}
