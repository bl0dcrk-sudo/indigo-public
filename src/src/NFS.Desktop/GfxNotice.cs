using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Short on-screen message after F8 / Shift+F8 ("GFX = ENHANCED X2", second line = the active Enhanced items). Drawn on the 2D layer (640×480, so it reads
/// the same in the menu and in a race, in Vanilla and in Enhanced) for <see cref="Seconds"/> with a short fade at the end. Time is passed in (seconds on any
/// monotonic clock), so the lifetime is testable without a window.
/// </summary>
internal sealed class GfxNotice
{
    public const double Seconds = 2.0;
    public const double FadeSeconds = 0.4;

    string? _title, _detail;
    uint _colour = 0xFFFFFF;
    double _start = double.NegativeInfinity;

    public string? Title => _title;
    public string? Detail => _detail;

    public void Show(string title, string? detail, double now, uint colour = 0xFFFFFF)
    {
        _title = title; _detail = detail; _colour = colour; _start = now;
    }

    public bool Active(double now) => _title != null && now >= _start && now < _start + Seconds;

    /// <summary>Opacity 0..255: full for the first <c>Seconds − FadeSeconds</c>, then a linear fade to 0.</summary>
    public int Alpha(double now)
    {
        if (!Active(now)) return 0;
        double left = _start + Seconds - now;
        return left >= FadeSeconds ? 255 : (int)Math.Round(255 * left / FadeSeconds);
    }

    /// <summary>The message of the F8 key (mode switch): "GFX = VANILLA" / "GFX = ENHANCED X2" (x = the scale the 3D scene is rendered at).</summary>
    public static (string Title, string? Detail, uint Colour) ForMode(GraphicsSettings g, int k) =>
        g.Mode == GraphicsMode.Vanilla
            ? ("GFX = VANILLA", g.QualityText(k), 0xFFD060u)
            : ($"GFX = ENHANCED X{k * g.EffectiveSsaa(k)}", g.QualityText(k), 0x80FF80u);

    /// <summary>The message of Shift+F8 (internal scale): "GFX SCALE = AUTO (X1)" / "GFX SCALE = 3 (X3)"; in Vanilla the scale is only remembered.</summary>
    public static (string Title, string? Detail, uint Colour) ForScale(GraphicsSettings g, int k)
    {
        string setting = g.Scale == 0 ? "AUTO" : g.Scale.ToString();
        if (g.Mode == GraphicsMode.Vanilla) return ($"GFX SCALE = {setting}", "VANILLA IGNORES THE SCALE (F8 = ENHANCED)", 0xFFD060u);
        int s = g.EffectiveSsaa(k);
        return ($"GFX SCALE = {setting} (X{k})", s > 1 ? $"RENDERED AT X{k * s} (SSAA {s}X)" : g.QualityText(k), 0x80FF80u);
    }

    /// <summary>The message of Ctrl+F8 (SSAA): "GFX SSAA = AUTO (X2)" / "GFX SSAA = OFF".</summary>
    public static (string Title, string? Detail, uint Colour) ForSsaa(GraphicsSettings g, int k)
    {
        if (g.Mode == GraphicsMode.Vanilla) return ("GFX SSAA = " + (g.Ssaa == 1 ? "OFF" : "AUTO"), "VANILLA HAS NO SSAA (F8 = ENHANCED)", 0xFFD060u);
        int s = g.EffectiveSsaa(k);
        return (g.Ssaa == 1 ? "GFX SSAA = OFF" : g.Ssaa == 0 ? $"GFX SSAA = AUTO (X{s})" : $"GFX SSAA = {s}X", g.QualityText(k), 0x80FF80u);
    }

    /// <summary>The message of Alt+F8 (display filter): "GFX DISPLAY = FSR" / "CRT" / "SHARP".</summary>
    public static (string Title, string? Detail, uint Colour) ForDisplay(GraphicsSettings g, int k)
    {
        string name = g.Display.Name().ToUpperInvariant();
        if (g.Mode == GraphicsMode.Vanilla) return ("GFX DISPLAY = " + name, "VANILLA IGNORES THE DISPLAY FILTER (F8 = ENHANCED)", 0xFFD060u);
        string note = g.Display switch
        {
            DisplayFilterKind.Fsr => "FSR-LIKE EASU + RCAS ON THE CPU (SLOW CPU: ALT+F8)",
            DisplayFilterKind.Crt => "CRT SCANLINES + MASK ON THE CPU (SLOW CPU: ALT+F8)",
            _ => "SHARP BILINEAR (GPU)",
        };
        return ("GFX DISPLAY = " + name, note, 0x80FF80u);
    }

    /// <summary>Draws the message (dark translucent plate, centred in the upper part of the 2D layer) when it is alive.</summary>
    public void Draw(FrameBuffer fb, double now)
    {
        int alpha = Alpha(now);
        if (alpha <= 0 || _title == null) return;
        const int titleScale = 3, detailScale = 2, pad = 10;
        int tw = HudOverlay.TextWidth(_title, titleScale), dw = _detail == null ? 0 : HudOverlay.TextWidth(_detail, detailScale);
        int w = Math.Max(tw, dw) + pad * 2, h = pad * 2 + 5 * titleScale + (_detail == null ? 0 : 8 + 5 * detailScale);
        int x0 = (fb.Width - w) / 2, y0 = 56;
        int plate = alpha * 190 / 255;
        for (int y = Math.Max(0, y0); y < Math.Min(fb.Height, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(fb.Width, x0 + w); x++) fb.Blend(x, y, 0x000000, plate);
        PopupBox.Frame(fb, x0, y0, w, h, 0x101820);
        HudOverlay.DrawText(fb, (fb.Width - tw) / 2, y0 + pad, _title, _colour, titleScale, alpha);
        if (_detail != null) HudOverlay.DrawText(fb, (fb.Width - dw) / 2, y0 + pad + 5 * titleScale + 8, _detail, 0xFFFFFF, detailScale, alpha);
    }
}
