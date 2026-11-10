using NFS.Render;

namespace NFS.Desktop;

/// <summary>Graphics setup of a run: the mode and the requested internal scale of the 3D scene (0 = auto). [ref:3389]
/// Priority: command line (<c>--gfx</c>, <c>--gfx-scale</c>) → <c>port.json</c> → Enhanced / auto. No MonoGame types (testable without a window).</summary>
internal sealed record GraphicsSettings(GraphicsMode Mode, int Scale)
{
    // ---- Enhanced image quality (stage 2); every item can be switched off separately ---- [ref:3390]
    /// <summary>Perspective-correct texture mapping.</summary>
    public bool Perspective { get; init; } = true;
    /// <summary>Texture filter: nearest / bilinear / mip maps (trilinear).</summary>
    public TextureFilter Filter { get; init; } = TextureFilter.Trilinear;
    /// <summary>Fog per pixel.</summary>
    public bool PixelFog { get; init; } = true;
    /// <summary>SSAA factor s (0 = auto, 1 = off, 2..4), see <see cref="Supersampling"/>.</summary>
    public int Ssaa { get; init; }

    // ---- Enhanced look (stage 3) ----
    /// <summary>Gouraud / soft light on the cars (<see cref="CarLighting"/>).</summary>
    public bool CarLight { get; init; } = true;
    /// <summary>Soft shadow under the cars (<see cref="CarShadow"/>).</summary>
    public bool CarShadow { get; init; } = true;
    /// <summary>Track polygon shading (<see cref="TrackShade"/>): darkened road / scenery corners from the TRK shade bits.</summary>
    public bool TrackShade { get; init; } = true;
    /// <summary>Extended view range (objects / blocks / cars twice as far, 1240 m z-sort); off by default (more polygons).</summary>
    public bool ExtendedRange { get; init; }
    /// <summary>Display filter applied on the CPU to the finished frame: sharp (the GPU bilinear path, default), fsr (EASU + RCAS), crt (scanlines + mask).</summary>
    public DisplayFilterKind Display { get; init; } = DisplayFilterKind.Sharp;

    // ---- Panorama ---- [ref:3391]
    /// <summary>Hor+ panorama: the 3D scene follows the aspect ratio of the window (wider than 4:3, the vertical angle stays); it changes the frame compared with the original, so it is off by default.
    /// Race and fly-through only (the menus stay 4:3); Enhanced only.</summary>
    public bool Wide { get; init; }

    // ---- 2D layer (task 08j) ----
    /// <summary>Menus, HUD and texts magnified by MMPX (rounded glyph edges, no blur) instead of k×k blocks; Enhanced with the sharp display only, on by default.</summary>
    public bool Smooth2D { get; init; } = true;

    public static GraphicsSettings Resolve(DesktopOptions opt, PortSettings? port) =>
        new(opt.Gfx ?? port?.Graphics ?? GraphicsModes.Default,
            opt.GfxScale ?? (port?.InternalScaleSetting is { } s ? Math.Clamp(s, 0, InternalScale.Max) : 0))
        {
            Perspective = opt.GfxPerspective ?? port?.GfxPerspective ?? true,
            Filter = opt.GfxFilter ?? ParseFilter(port?.GfxFilter) ?? TextureFilter.Trilinear,
            PixelFog = opt.GfxFog ?? port?.GfxPixelFog ?? true,
            Ssaa = Math.Clamp(opt.GfxSsaa ?? port?.GfxSsaa ?? 0, 0, InternalScale.Max),
            CarLight = opt.GfxLight ?? port?.GfxLight ?? true,
            CarShadow = opt.GfxShadow ?? port?.GfxShadow ?? true,
            TrackShade = opt.GfxTrackShade ?? port?.GfxTrackShade ?? true,
            ExtendedRange = opt.GfxRange ?? ParseRange(port?.GfxRange) ?? false,
            Display = opt.GfxDisplay ?? DisplayFilterKinds.Parse(port?.GfxDisplay) ?? DisplayFilterKind.Sharp,
            Wide = opt.GfxWide ?? port?.GfxWide ?? false,
            Smooth2D = opt.GfxSmooth2D ?? port?.GfxSmooth2D ?? true,
        };

    /// <summary>"normal" (also "original", "off") → false, "extended" (also "far", "on") → true; anything else → null.</summary>
    public static bool? ParseRange(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "normal" or "original" or "off" => false,
        "extended" or "far" or "on" => true,
        _ => null,
    };

    /// <summary>"nearest" / "bilinear" / "mip" (also "trilinear"); anything else → null.</summary>
    public static TextureFilter? ParseFilter(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "nearest" or "point" or "off" => TextureFilter.Nearest,
        "bilinear" or "linear" => TextureFilter.Bilinear,
        "mip" or "mipmap" or "trilinear" => TextureFilter.Trilinear,
        _ => null,
    };

    public static string FilterName(TextureFilter f) => f switch { TextureFilter.Nearest => "nearest", TextureFilter.Bilinear => "bilinear", _ => "mip" };

    /// <summary>"on"/"off" (also 1/0, true/false); anything else → null.</summary>
    public static bool? ParseSwitch(string text) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "yes" => true,
        "off" or "0" or "false" or "no" => false,
        _ => null,
    };

    /// <summary>Fills the renderer options from the mode and the quality items (Vanilla = all off = the original rasteriser).</summary>
    public void ApplyTo(RenderOptions render)
    {
        bool on = Mode == GraphicsMode.Enhanced;
        render.PerspectiveCorrect = on && Perspective;
        render.Filter = on ? Filter : TextureFilter.Nearest;
        render.PixelFog = on && PixelFog;
        render.CarLight = on && CarLight;
        render.CarShadow = on && CarShadow;
        render.TrackShade = on && TrackShade;
        render.ExtendedRange = on && ExtendedRange;
        render.ParallelFill = on;   // only for frames of 960+ rows (TrackRenderer.ParallelMinHeight); same pixels as the single-threaded fill
    }

    /// <summary>SSAA factor actually used at the displayed scale <paramref name="k"/>.</summary>
    public int EffectiveSsaa(int k) => Supersampling.Resolve(Mode, Ssaa, k);

    /// <summary>Width of the 3D frame at scale 1 for a window of <paramref name="windowW"/>×<paramref name="windowH"/> pixels: 640 unless the panorama is on
    /// (Enhanced only) and the window is wider than 4:3 (<see cref="Panorama.ViewWidth"/>).</summary>
    public int ViewWidth(int windowW, int windowH) =>
        Mode == GraphicsMode.Enhanced && Wide && windowW > 0 && windowH > 0 ? Panorama.ViewWidth((double)windowW / windowH) : 640;

    /// <summary>One-line description of the active Enhanced items (the F8 notice, the log).</summary>
    public string QualityText(int k)
    {
        if (Mode == GraphicsMode.Vanilla) return "ORIGINAL RENDERER";
        var parts = new List<string>();
        if (Perspective) parts.Add("PERSPECTIVE");
        if (Filter != TextureFilter.Nearest) parts.Add(Filter == TextureFilter.Trilinear ? "MIPMAPS" : "BILINEAR");
        if (PixelFog) parts.Add("FOG");
        int s = EffectiveSsaa(k);
        if (s > 1) parts.Add($"SSAA {s}X");
        if (CarLight) parts.Add("CAR LIGHT");
        if (CarShadow) parts.Add("SHADOW");
        if (TrackShade) parts.Add("TRACK SHADE");
        if (ExtendedRange) parts.Add("RANGE X2");
        if (Display != DisplayFilterKind.Sharp) parts.Add("DISPLAY " + Display.Name().ToUpperInvariant());
        if (Wide) parts.Add("WIDE VIEW");
        if (Smooth2D) parts.Add("2D SMOOTH");
        return parts.Count == 0 ? "NO QUALITY ITEMS" : string.Join(" + ", parts);
    }

    /// <summary>"auto" or "0" → 0; "1".."4" → k; anything else → null.</summary>
    public static int? ParseScale(string text)
    {
        if (string.Equals(text.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) return 0;
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int k)
            && k is >= 0 and <= InternalScale.Max ? k : null;
    }

    /// <summary>F8: Enhanced ⇄ Vanilla.</summary>
    public GraphicsSettings Toggled() => this with { Mode = Mode.Other() };

    /// <summary>Alt+F8: display filter sharp → fsr → crt → sharp.</summary>
    public GraphicsSettings NextDisplay() => this with { Display = Display.Next() };

    /// <summary>Shift+F8: scale auto → 1 → 2 → 3 → 4 → auto.</summary>
    public GraphicsSettings NextScale() => this with { Scale = Scale >= InternalScale.Max ? 0 : Scale + 1 };

    /// <summary>Ctrl+F8: SSAA automatic (0) ⇄ off (1); an explicit 2..4 goes back to automatic.</summary>
    public GraphicsSettings ToggledSsaa() => this with { Ssaa = Ssaa == 1 ? 0 : 1 };

    /// <summary>Internal scale k actually used for an image area of <paramref name="areaW"/>×<paramref name="areaH"/> pixels (the letterboxed picture).</summary>
    public int EffectiveScale(int areaW, int areaH) => InternalScale.Resolve(Mode, Scale, areaW, areaH);

    /// <summary>Stores the mode and scale in <c>port.json</c> (written by the caller).</summary>
    public void WriteTo(PortSettings port)
    {
        port.GraphicsModeText = Mode.Name();
        port.InternalScaleSetting = Scale;
        port.GfxPerspective = Perspective;
        port.GfxFilter = FilterName(Filter);
        port.GfxPixelFog = PixelFog;
        port.GfxSsaa = Ssaa;
        port.GfxLight = CarLight;
        port.GfxShadow = CarShadow;
        port.GfxTrackShade = TrackShade;
        port.GfxRange = ExtendedRange ? "extended" : "normal";
        port.GfxDisplay = Display.Name();
        port.GfxWide = Wide;
        port.GfxSmooth2D = Smooth2D;
    }
}
