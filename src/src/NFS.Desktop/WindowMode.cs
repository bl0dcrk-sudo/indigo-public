using Microsoft.Xna.Framework.Input;

namespace NFS.Desktop;

/// <summary>Window ↔ full screen policy (no window/device: testable headless). Full screen is borderless at the
/// desktop resolution (<c>HardwareModeSwitch = false</c> – no display mode change, safe under Wine/Proton on the
/// Steam Deck); the 640×480 frame stays letterboxed by <see cref="Layout.Fit"/>. Windowed = 640×480 × <c>--scale</c>.</summary>
internal static class WindowMode
{
    public const int W = 640, H = 480;

    /// <summary>Start mode: autotest → window; else the command line, else the remembered <c>port.json</c> value,
    /// else full screen.</summary>
    public static bool StartsFullScreen(DesktopOptions o, PortSettings? saved) =>
        o.AutotestDir == null && (o.FullScreen ?? saved?.FullScreen ?? true);

    public static (bool FullScreen, int Width, int Height) Initial(DesktopOptions o, PortSettings? saved, int displayW, int displayH) =>
        o.Window is { } win ? (false, win.Width, win.Height) : Size(StartsFullScreen(o, saved), o.Scale, displayW, displayH);

    public static (bool FullScreen, int Width, int Height) Toggle(bool fullScreen, int scale, int displayW, int displayH) =>
        Size(!fullScreen, scale, displayW, displayH);

    static (bool, int, int) Size(bool full, int scale, int displayW, int displayH) =>
        full && displayW > 0 && displayH > 0 ? (true, displayW, displayH) : (false, W * scale, H * scale);

    /// <summary>Alt+Enter on the frame Enter goes down (either Alt key).</summary>
    public static bool IsToggle(KeyboardState now, KeyboardState prev) =>
        (now.IsKeyDown(Keys.LeftAlt) || now.IsKeyDown(Keys.RightAlt)) && now.IsKeyDown(Keys.Enter) && !prev.IsKeyDown(Keys.Enter);
}
