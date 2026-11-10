namespace NFS.Render;

/// <summary>
/// Frame of the panels and pop-ups that the port draws itself (a filled rectangle over the picture, not art from the original game
/// files): "LEAVE THE GAME?", network paused, the network menu panels, chat plates, the graphics message. Without it a dark panel
/// melts into the dark background around it. The frame is three pixels thick and lies INSIDE the given rectangle: the two outermost pixels
/// are clearly darker than the fill, the one inside them a lighter bevel, so the edge reads on a dark and on a bright background alike.
/// </summary>
public static class PopupBox
{
    /// <summary>Share (percent) of the fill brightness kept by the outer border pixel.</summary>
    public const int OuterPercent = 25;
    /// <summary>Light added to every channel of the fill for the inner bevel pixel.</summary>
    public const int InnerLift = 0x48;

    /// <summary>Outer border colour for a fill colour: clearly darker (a quarter of the brightness).</summary>
    public static uint BorderColour(uint fill) => Scale(fill, OuterPercent);

    /// <summary>Inner (bevel) colour for a fill colour: lighter, so the dark border stands out against a dark fill.</summary>
    public static uint BevelColour(uint fill)
    {
        static uint Up(uint c) => (uint)Math.Min(255, (int)c + InnerLift);
        return Up(fill >> 16 & 0xFF) << 16 | Up(fill >> 8 & 0xFF) << 8 | Up(fill & 0xFF);
    }

    static uint Scale(uint rgb, int percent)
    {
        uint R = (rgb >> 16 & 0xFF) * (uint)percent / 100, G = (rgb >> 8 & 0xFF) * (uint)percent / 100, B = (rgb & 0xFF) * (uint)percent / 100;
        return R << 16 | G << 8 | B;
    }

    /// <summary>Draws the three-pixel frame inside [<paramref name="x"/>, <paramref name="x"/>+<paramref name="w"/>) ×
    /// [<paramref name="y"/>, <paramref name="y"/>+<paramref name="h"/>) (clipped to the buffer) for a panel filled with
    /// <paramref name="fill"/> (a chequer: pass its darker or average colour).</summary>
    public static void Frame(FrameBuffer fb, int x, int y, int w, int h, uint fill)
    {
        if (w < 8 || h < 8) return;
        uint dark = BorderColour(fill);
        Ring(fb, x, y, w, h, dark);
        Ring(fb, x + 1, y + 1, w - 2, h - 2, dark);
        Ring(fb, x + 2, y + 2, w - 4, h - 4, BevelColour(fill));
    }

    /// <summary>Fills [<paramref name="x0"/>,<paramref name="x1"/>) × [<paramref name="y0"/>,<paramref name="y1"/>) with a one-pixel chequer of
    /// <paramref name="a"/> / <paramref name="b"/> and frames it.</summary>
    public static void Panel(FrameBuffer fb, int x0, int y0, int x1, int y1, uint a, uint b)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(fb.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(fb.Width, x1); x++)
                fb.Set(x, y, ((x + y) & 1) == 0 ? a : b);
        Frame(fb, x0, y0, x1 - x0, y1 - y0, Math.Min(a, b));
    }

    static void Ring(FrameBuffer fb, int x, int y, int w, int h, uint rgb)
    {
        for (int i = 0; i < w; i++) { Put(fb, x + i, y, rgb); Put(fb, x + i, y + h - 1, rgb); }
        for (int j = 1; j < h - 1; j++) { Put(fb, x, y + j, rgb); Put(fb, x + w - 1, y + j, rgb); }
    }

    static void Put(FrameBuffer fb, int x, int y, uint rgb)
    {
        if ((uint)x < (uint)fb.Width && (uint)y < (uint)fb.Height) fb.Set(x, y, rgb);
    }
}
