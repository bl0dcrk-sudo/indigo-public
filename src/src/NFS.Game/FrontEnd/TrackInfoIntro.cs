namespace NFS.Game.FrontEnd;

/// <summary>Entry of the track description screen (before the key loop): if <c>config+0x12D [ref:1980]</c>
/// is nonzero, the "sldr" panel slides in from the left after a black screen (flag 0x1800: x = −0x138, −0x12C, … up to and including 0, y = 0),
/// then the "back" background brightens in 5 frames (level 0, 4, 8, 12, 16 of 16) with the panel [ref:1981]
/// "sldr" in its place on top. The loops have no wait of their own (one frame = one display).</summary>
public static class TrackInfoIntro
{
    public const int SlideStart = -0x138, SlideStep = 0xC, FadeLevelStep = 4, FadeLevelMax = 16;

    /// <summary>Slide-in frame: the "sldr" panel at <see cref="PanelX"/>, or (when <see cref="PanelX"/> = null) in its place, plus
    /// brightening of the "back" background (<see cref="FadeLevel"/> out of 16; null = background not drawn yet).</summary>
    public readonly record struct Frame(int? PanelX, int? FadeLevel);

    /// <summary>Slide-in frames; an empty list when <paramref name="detail"/> = 0 (loops skipped).</summary>
    public static IReadOnlyList<Frame> Frames(int detail)
    {
        var frames = new List<Frame>();
        if (detail == 0) return frames;
        for (int x = SlideStart; x < 1; x += SlideStep) frames.Add(new(x, null));
        for (int level = 0; level <= FadeLevelMax; level += FadeLevelStep) frames.Add(new(null, level));
        return frames;
    }
}
