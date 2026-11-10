using NFS.Core.Constants;
using NFS.Data;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Race loading screen. Two images: [ref:1099]
/// (0) the player's car slide "%ssld%s.qfs" (FEDATA/PC/ART/SLIDES/SLD&lt;car&gt;[2..8].QFS, shape "back" 640×480), when car &lt; 0xC + unlock level
/// and it is not a replay; (2) track image "%s%sload.qfs" (GAMEDATA/RENDER/PC/&lt;track&gt;LOAD.QFS: "back" + caption [ref:1100]
/// of the language "eng1/ger1/fre1/spa1/ita1/swe1" at the position from the shape header). On the track image (1..10) it draws a vertical [ref:1101]
/// bar: shape "back" from BAR.FSH (14×5), 8 times every 5 px vertically per step, x = 0x20, y = 0x1B4 − 40·(step−1) − 5·k, k = 0..7.
/// The original has no time limit – the screen lasts as long as the loading (hypothesis: 1–2 s on 1997 hardware); the port loads in one frame,
/// so it holds each image for a fixed time (<see cref="SlideSeconds"/>, <see cref="TrackSeconds"/>) – a choice of the port, not from the original.</summary>
public sealed class LoadingScreen
{
    /// <summary>Track names from the table, index (0..8, otherwise 0). Track 1 has an empty name (LOAD.QFS is not a track image). [ref:1102]</summary>
    public static readonly string[] TrackPrefixes = ["pio", "", "out", "pac", "las", "nor", "med", "mys", "mon"];
    /// <summary>Car names from the table (index = car model). [ref:1103]</summary>
    public static readonly string[] CarSlides = ["mac", "fer", "355f", "frd", "ind", "mus", "jag", "gt1", "v8", "naz", "ita", "isd", "bra", "blk", "fzr"];
    /// <summary>Language captions on the track image (table); index = game language (ENG, GER, FRE, SPA, ITA, SWE). [ref:1104]</summary>
    public static readonly string[] LanguageShapes = ["eng1", "ger1", "fre1", "spa1", "ita1", "swe1"];

    /// <summary>How long the car slide and the track image are held in the port (seconds).</summary>
    public const double SlideSeconds = 0.6, TrackSeconds = 0.5;
    /// <summary>Bar steps 1..10; x and top edge of the first bar step (0x20, 0x1B4). [ref:1105]</summary>
    public const int ProgressSteps = 10, BarX = 0x20, BarBottom = 0x1B4, BarStepHeight = 40, BarSliceHeight = 5, BarSlices = 8;

    readonly string? _gameDir;
    readonly int _language;
    readonly Dictionary<string, Fsh?> _files = new(StringComparer.OrdinalIgnoreCase);
    byte _sequence;

    public LoadingScreen(string? gameDir = null, int language = 0, byte sequenceIndex = 0)
    {
        _gameDir = gameDir;
        _language = Math.Clamp(language, 0, 5);
        _sequence = sequenceIndex;
    }

    /// <summary>Byte counter after the last variant draw (to carry over to the next loading). [ref:1106]</summary>
    public byte Sequence => _sequence;

    /// <summary>Track image file: index outside 0..8 → 0 (as in the original). [ref:1107]</summary>
    public static string TrackFile(int trackId) =>
        $"GAMEDATA/RENDER/PC/{TrackPrefixes[trackId is < 0 or > 8 ? 0 : trackId].ToUpperInvariant()}LOAD.QFS";

    /// <summary>Whether the car gets a slide: model &lt; 0xC + unlock level, and a known name; otherwise the track screen. [ref:1108]</summary>
    public static bool HasSlide(int car, int unlockLevel) => car >= 0 && car < CarSlides.Length && car < 0xC + unlockLevel;

    /// <summary>Number of slide variants: 8, and 7 for cars 3 and 5; models ≥ 0xC have only variant 1 (no random draw). [ref:1109]</summary>
    public static int SlideVariants(int car) => car >= 0xC ? 1 : car is 3 or 5 ? 7 : 8;

    /// <summary>Variant number 1..N from the table via the byte counter. [ref:1110]</summary>
    public int NextVariant(int car) => car >= 0xC ? 1 : FrontEndConstants.SequenceTable[_sequence++] % SlideVariants(car) + 1;

    /// <summary>Slide file: variant 1 = "%s" (SLDFER), then "%s%d" (SLDFER2…).</summary>
    public static string SlideFile(int car, int variant) =>
        $"FEDATA/PC/ART/SLIDES/SLD{(variant == 1 ? CarSlides[car] : CarSlides[car] + variant).ToUpperInvariant()}.QFS";

    Fsh? Open(string path)
    {
        if (_files.TryGetValue(path, out var cached)) return cached;
        try { return _files[path] = Fsh.Load(path, _gameDir); }
        catch (Exception e) when (e is IOException or InvalidDataException) { return _files[path] = null; }
    }

    static FshImage? Shape(Fsh fsh, string name) => fsh.Find(name) is { } e ? fsh.Decode(e, fsh.GlobalPalette()) : null;

    static void Blit(FrameBuffer fb, FshImage? image)
    {
        if (image != null) OriginalHud.Blit(fb, image, image.PosX, image.PosY);
    }

    /// <summary>Car slide (shape "back"). Returns false (buffer untouched) when the car has no slide or the file is missing.</summary>
    public bool DrawSlide(FrameBuffer fb, int car, int unlockLevel, int variant)
    {
        if (!HasSlide(car, unlockLevel) || variant < 1 || variant > SlideVariants(car)) return false;
        if (Open(SlideFile(car, variant)) is not { } fsh || Shape(fsh, "back") is not { } back) return false;
        fb.Clear(0, 0, 0);
        Blit(fb, back);
        return true;
    }

    /// <summary>Track image: "back" + language caption; <paramref name="step"/> 0..10 = progress bar. False when the file is missing. [ref:1111]</summary>
    public bool DrawTrack(FrameBuffer fb, int trackId, int step)
    {
        if (Open(TrackFile(trackId)) is not { } fsh || Shape(fsh, "back") is not { } back) return false;
        fb.Clear(0, 0, 0);
        Blit(fb, back);
        Blit(fb, Shape(fsh, LanguageShapes[_language]));
        DrawBar(fb, step);
        return true;
    }

    /// <summary>Bar: steps 1..<paramref name="step"/> build up from the bottom of the screen (drawings accumulate, the screen is not cleared).</summary>
    public void DrawBar(FrameBuffer fb, int step)
    {
        step = Math.Clamp(step, 0, ProgressSteps);
        if (step == 0 || Open("GAMEDATA/RENDER/PC/BAR.FSH") is not { } bar || Shape(bar, "back") is not { } segment) return;
        for (int s = 1; s <= step; s++)
            for (int k = 0; k < BarSlices; k++)
                OriginalHud.Blit(fb, segment, BarX, BarBottom - BarStepHeight * (s - 1) - BarSliceHeight * k);
    }
}
