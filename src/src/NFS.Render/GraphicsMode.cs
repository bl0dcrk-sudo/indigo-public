namespace NFS.Render;

/// <summary>Graphics presentation mode. <see cref="Vanilla"/> = image identical to the earlier port [ref:3439]
/// (3D scene and 2D layer in one 640×480 buffer, point display, no geometry changes). <see cref="Enhanced"/> (default) =
/// 3D scene at internal scale k (640k×480k), 2D layer still 640×480 scaled entirely ×k, sharp bilinear display filter.
/// The simulation (CarObj, RaceRunner) does not depend on the mode.</summary>
public enum GraphicsMode { Enhanced, Vanilla }

public static class GraphicsModes
{
    public const GraphicsMode Default = GraphicsMode.Enhanced;

    /// <summary>"enhanced" / "vanilla" (case-insensitive); any other value → null.</summary>
    public static GraphicsMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "enhanced" => GraphicsMode.Enhanced,
        "vanilla" => GraphicsMode.Vanilla,
        _ => null,
    };

    public static string Name(this GraphicsMode mode) => mode == GraphicsMode.Vanilla ? "vanilla" : "enhanced";

    public static GraphicsMode Other(this GraphicsMode mode) => mode == GraphicsMode.Vanilla ? GraphicsMode.Enhanced : GraphicsMode.Vanilla;
}

/// <summary>Supersampling (SSAA) of the Enhanced 3D scene: rendered at k·s, box-filtered down to the displayed scale k. The displayed scale k follows the
/// window (<see cref="InternalScale.Auto"/>), s makes sure the renderer always draws at least <see cref="AutoRenderScale"/>× (1280×960) even in a small
/// window, where k = 1 would otherwise show no difference from Vanilla.</summary>
public static class Supersampling
{
    /// <summary>Automatic target of the render scale k·s (CPU budget: 2× = 4× the pixels; 3×/4× only on request).</summary>
    public const int AutoRenderScale = 2;

    /// <summary>s for a setting (0 = auto: just enough to reach <see cref="AutoRenderScale"/>, 1 = off, 2..4 explicit) at displayed scale <paramref name="k"/>;
    /// k·s never exceeds <see cref="InternalScale.Max"/>; Vanilla always 1.</summary>
    public static int Resolve(GraphicsMode mode, int setting, int k)
    {
        if (mode == GraphicsMode.Vanilla) return 1;
        int cap = Math.Max(1, InternalScale.Max / Math.Max(1, k));
        return setting <= 0 ? Math.Min(cap, Math.Max(1, AutoRenderScale / Math.Max(1, k))) : Math.Clamp(setting, 1, cap);
    }
}

/// <summary>Internal scale of the 3D scene in Enhanced mode: 1..<see cref="Max"/> (0 = auto by window size).</summary>
public static class InternalScale
{
    public const int Max = 4;
    /// <summary>Base upper limit of the automatic choice (render time measurement, 2026-10-20 "graphics modes"). [ref:3440]</summary>
    public const int AutoCap = 2;
    /// <summary>From this many logical cores, auto may choose k = 3. render-bench (strip fill): k=3 ≈ 20–23 ms with 16–20 threads
    /// (does not fit in 16.6 ms), ≥ 26 ms with ≤ 4; k=2 fits from 4 threads. The 32 threshold is cautious (unmeasured – on 20 cores auto stays at k=2).
    /// k = 4 is never chosen automatically.</summary>
    public const int Auto3MinProcessors = 32;

    /// <summary>Upper limit of the automatic scale for a machine with <paramref name="processorCount"/> logical cores: <see cref="AutoCap"/> (2),
    /// and from <see cref="Auto3MinProcessors"/> cores, 3. Below 4 cores we do not drop to 1: auto SSAA renders k·s = 2× anyway, so k = 1 would save nothing.</summary>
    public static int AutoCapFor(int processorCount) => processorCount >= Auto3MinProcessors ? 3 : AutoCap;

    public static int Clamp(int k) => Math.Clamp(k, 1, Max);

    /// <summary>Largest k (≤ <paramref name="cap"/>) for which 640k×480k fits in the image area
    /// (<paramref name="areaW"/>×<paramref name="areaH"/> after letterboxing); at least 1.</summary>
    public static int Auto(int areaW, int areaH, int cap)
    {
        int k = 1;
        for (int c = 2; c <= Math.Min(cap, Max); c++)
            if (640 * c <= areaW && 480 * c <= areaH) k = c;
        return k;
    }

    /// <summary><see cref="Auto(int,int,int)"/> with a cap depending on this machine's core count (<see cref="AutoCapFor"/>).</summary>
    public static int Auto(int areaW, int areaH) => Auto(areaW, areaH, AutoCapFor(Environment.ProcessorCount));

    /// <summary>Scale used in the game: <paramref name="setting"/> 1..4 directly, 0 (auto) by image area; Vanilla always 1.</summary>
    public static int Resolve(GraphicsMode mode, int setting, int areaW, int areaH) =>
        mode == GraphicsMode.Vanilla ? 1 : setting <= 0 ? Auto(areaW, areaH) : Clamp(setting);
}

/// <summary>Hor+ panorama of the Enhanced 3D scene (optional, off by default): the scene buffer gets wider than 4:3 while the [ref:3441]
/// vertical angle and the pixel scale stay those of the original frame, so the horizontal angle grows with the aspect ratio. The 640×480 2D layer (HUD,
/// dashboard, mirror) is centred in the wide frame.</summary>
public static class Panorama
{
    /// <summary>Widest supported aspect ratio (21:9); a wider window gets bars at the sides.</summary>
    public const double MaxAspect = 7.0 / 3.0;

    /// <summary>Frame width at scale 1 (height 480) for the display aspect ratio <paramref name="aspect"/> (width / height): 640 for 4:3 or narrower,
    /// otherwise 480·aspect rounded to a multiple of 8 (so the centred 640-pixel layer sits on an even pixel).</summary>
    public static int ViewWidth(double aspect)
    {
        if (!(aspect > 4.0 / 3)) return 640;
        int w = (int)Math.Round(480 * Math.Min(aspect, MaxAspect) / 8) * 8;
        return Math.Max(640, w);
    }

    /// <summary>Horizontal half angle (degrees) of a frame of <paramref name="viewWidth"/> (scale 1): 45° for 640.</summary>
    public static double HalfAngleDegrees(int viewWidth) => Math.Atan(viewWidth / 2.0 / 320.0) * 180 / Math.PI;
}
