using NFS.Data;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Title screen at game start: FEDATA/PC/ART/TITLE.QFS, shape "back" [ref:1538]
/// 640×480 and the language caption ("engl/germ/fren/span/itly/swed", position from the shape header). Original: first the movie titleav.dct (this installation has no movie files), then it waits for the keys to be released, shows the title until any key is pressed (keyboard, pad button = Enter 0xD; the second loop also ends when the hook returns ≠ 0 – hypothesis: end of movie/sound), fades out the palette [ref:1539]
/// (loop until the end of the fade) and waits for the keys to be released. On the first launch the original also launches a further item [ref:1540]
/// (outside the scope of the port). The title is not time-limited.</summary>
public sealed class TitleScreen
{
    /// <summary>Order as the game's languages (ENG, GER, FRE, SPA, ITA, SWE) – switch table. [ref:1541]</summary>
    public static readonly string[] LanguageShapes = ["engl", "germ", "fren", "span", "itly", "swed"];
    public const string File = "FEDATA/PC/ART/TITLE.QFS";

    readonly Fsh _art;
    readonly int _language;

    /// <exception cref="IOException">TITLE.QFS is missing (the caller skips the screen).</exception>
    public TitleScreen(string? gameDir = null, int language = 0)
    {
        _language = Math.Clamp(language, 0, 5);
        _art = Fsh.Load(File, gameDir);
    }

    public void Draw(FrameBuffer fb)
    {
        fb.Clear(0, 0, 0);
        foreach (var name in new[] { "back", LanguageShapes[_language] })
            if (_art.Find(name) is { } e && _art.Decode(e, _art.GlobalPalette()) is { } image)
                OriginalHud.Blit(fb, image, image.PosX, image.PosY);
    }
}
