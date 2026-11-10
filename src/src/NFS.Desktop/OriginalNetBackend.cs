using NFS.Data.FrontEnd;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Draws a <see cref="NetDrawList"/> with the resources of the game (change network-menu-look, design D1-D5): the background and
/// the window of <see cref="MenuScene"/>, the FFN fonts (<see cref="FrontEndFont"/>; the sizes of <see cref="NetFont"/> are the
/// sizes of the game fonts), the colours of <see cref="Colour"/> and the panels of <see cref="NetPanel"/>. A text that does not fit
/// between its position and the edge of the buffer is cut with "...".
/// </summary>
internal static class OriginalNetBackend
{
    /// <summary>Margin kept free at the edge of the buffer by a long text.</summary>
    const int Margin = 4;

    /// <summary>Colour of a role (D4): the colour table of the front end for the roles that have an index; disabled, good and bad
    /// are the port's own (readable on the dark panels).</summary>
    public static uint Colour(NetRole role) => role switch
    {
        NetRole.Ink => 0x000008,
        NetRole.InkSelected or NetRole.Selected => 0xFFFFFF,
        NetRole.Label => 0x908080,
        NetRole.Value or NetRole.Warn => 0xD59C30,
        NetRole.ValueSelected => 0xF4CC28,
        NetRole.Header => 0x08F0F0,
        NetRole.Disabled => 0x857A7A,
        NetRole.ButtonIdle => 0xC8C0C0,
        NetRole.InkSoft => 0x815412,
        NetRole.InkDisabled => 0x484040,
        NetRole.InkWarn => 0x800000,
        NetRole.Good => 0x6FD27A,
        NetRole.Bad => 0xF2573F,
        NetRole.Muted => 0x815412,
        _ => 0x908080,
    };

    // Cut texts per (text, size, room): a long text is cut once, not on every frame.
    [ThreadStatic] static Dictionary<(string Text, int Size, int Room), string>? _cut;
    // Same for the texts that keep their end (a field being edited).
    [ThreadStatic] static Dictionary<(string Text, int Size, int Room), string>? _cutTail;

    /// <summary>Colour of the frame round a swatch.</summary>
    const uint SwatchFrame = 0x6A6060;

    /// <summary>The game font for a size of the layout (the nearest of 13, 16, 18, 36).</summary>
    public static int FontSize(int size) => size switch { <= NetFont.Small => 13, <= NetFont.Medium => 16, <= NetFont.Normal => 18, _ => 36 };

    public static void Draw(FrameBuffer fb, NetDrawList list, MenuScene scene)
    {
        if (scene.Fonts is not { } fonts) return;
        foreach (ref readonly var item in list.Items)
        {
            switch (item.Op)
            {
                case NetOp.Backdrop: scene.DrawNetworkBackdrop(fb); break;
                case NetOp.Text: DrawText(fb, fonts, in item); break;
                case NetOp.Panel: NetPanel.Draw(fb, item.X, item.Y, item.X1, item.Y1, item.Kind, original: true); break;
                case NetOp.RowBar: NetPanel.RowBar(fb, item.X, item.Y, item.X1, item.Y1); break;
                case NetOp.Swatch:
                    Fill(fb, item.X - 1, item.Y - 1, item.X + item.X1 + 1, item.Y + item.Y1 + 1, SwatchFrame);
                    Fill(fb, item.X, item.Y, item.X + item.X1, item.Y + item.Y1, item.Rgb);
                    break;
                case NetOp.Dialog: scene.DrawNetworkDialog(fb, item.Text ?? "", item.Text2 ?? ""); break;
                case NetOp.Preview: scene.DrawNetworkCarPreview(fb, item.X1, item.X, item.Y); break;
            }
        }
    }

    /// <summary>Rows between the Y of a text of the 13 px game font and the top of its capitals (ink rows start at
    /// <see cref="NetworkMenuLayout.InkTop"/>); the chat font puts its capitals there.</summary>
    const int ChatCapTop = NetworkMenuLayout.InkTop;

    static void DrawText(FrameBuffer fb, FrontEndFont fonts, in NetDrawItem item)
    {
        if (item.Chat && NetChatText.Draw(fb, in item, Colour(item.Role), ChatCapTop)) return;
        string text = item.Text ?? "";
        if (text.Length == 0) return;
        int size = FontSize(item.Size);
        // room between the anchor and the nearest edge of the buffer
        int room = item.Align switch
        {
            NetAlign.Left => fb.Width - Margin - item.X,
            NetAlign.Right => item.X - Margin,
            _ => 2 * Math.Min(item.X, fb.Width - item.X) - 2 * Margin,
        };
        if (item.X1 > 0) room = Math.Min(room, item.X1);
        text = Fit(fonts.Font(size), text, size, room, item.Tail);
        if (text.Length == 0) return;
        // InkSelected is the highlighted text of the yellow board: white with the shadow of the original (align + 3)
        int align = item.Align switch { NetAlign.Right => 1, NetAlign.Center => 2, _ => 0 } + (item.Role == NetRole.InkSelected ? 3 : 0);
        fonts.Draw(fb, text, item.X, item.Y, size, align, Colour(item.Role));
    }

    /// <summary>The text itself when it fits into <paramref name="room"/> pixels, else its longest beginning plus "..." that does
    /// (just "..." or nothing when even that does not fit); with <paramref name="tail"/> the end of the text behind a leading "..."
    /// (<c>...XYZ_</c>: the caret of a field being edited stays visible).</summary>
    internal static string Fit(Ffn font, string text, int size, int room, bool tail = false)
    {
        if (font.Measure(text) <= room) return text;
        if (tail) return FitTail(font, text, size, room);
        var cache = _cut ??= new();
        if (cache.TryGetValue((text, size, room), out var cut)) return cut;
        int n = text.Length;
        while (n > 0 && font.Measure(string.Concat(text.AsSpan(0, n).TrimEnd(), "...")) > room) n--;
        cut = n > 0 ? string.Concat(text.AsSpan(0, n).TrimEnd(), "...") : font.Measure("...") <= room ? "..." : "";
        if (cache.Count > 512) cache.Clear();
        return cache[(text, size, room)] = cut;
    }

    static string FitTail(Ffn font, string text, int size, int room)
    {
        var cache = _cutTail ??= new();
        if (cache.TryGetValue((text, size, room), out var cut)) return cut;
        int start = 1;
        while (start < text.Length && font.Measure(string.Concat("...", text.AsSpan(start))) > room) start++;
        cut = start < text.Length ? string.Concat("...", text.AsSpan(start)) : font.Measure("...") <= room ? "..." : "";
        if (cache.Count > 512) cache.Clear();
        return cache[(text, size, room)] = cut;
    }

    static void Fill(FrameBuffer fb, int x0, int y0, int x1, int y1, uint rgb)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(fb.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(fb.Width, x1); x++) fb.Set(x, y, rgb);
    }
}

/// <summary>What <see cref="NetworkMenuRenderer"/> needs for the original look: the menu scene that owns the fonts and the art, and
/// the look chosen in <c>port.json</c>. When the fonts cannot be loaded (or the scene is missing) <see cref="UsesOriginal"/> is false
/// and classic is drawn (design D1).</summary>
internal sealed class NetSkin(MenuScene? scene, NetMenuLook look)
{
    public MenuScene? Scene { get; } = scene;
    public NetMenuLook Look { get; } = look;

    /// <summary>True when the original look is chosen and the fonts of the game are loaded.</summary>
    public bool UsesOriginal => Look == NetMenuLook.Original && Scene?.Fonts != null;
}
