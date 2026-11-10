using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Draws a <see cref="NetDrawList"/> the way the first network menu did (change network-menu-look, design D2): the dark chequer
/// with <see cref="PopupBox"/> frames and the <see cref="HudOverlay"/> bitmap font. Sizes map to the font scale
/// (<see cref="NetFont.ClassicScale"/>), roles to the colours of <see cref="Colour"/>; <see cref="NetOp.RowBar"/> has no classic
/// look (the selection is the colour and the <c>&gt;</c> of the text) and is skipped.
/// </summary>
internal static class ClassicNetBackend
{
    const uint PanelA = 0x101820, PanelB = 0x141E28, BackdropA = 0x0A0F14, BackdropB = 0x0C1218;

    /// <summary>Colour of a role (see <see cref="NetRole"/> for what each stands for).</summary>
    public static uint Colour(NetRole role) => role switch
    {
        NetRole.Ink or NetRole.Label => 0xFFFFFF,
        NetRole.InkSelected or NetRole.Selected or NetRole.Value or NetRole.ValueSelected => 0xFFFF00,
        NetRole.Header => 0x66CCFF,
        NetRole.Disabled or NetRole.Muted => 0x707070,
        NetRole.Good => 0x66FF66,
        NetRole.Bad => 0xFF6666,
        NetRole.Warn => 0xFFAA33,
        _ => 0xFFFFFF,
    };

    public static void Draw(FrameBuffer fb, NetDrawList list)
    {
        foreach (ref readonly var item in list.Items)
        {
            switch (item.Op)
            {
                case NetOp.Backdrop: Fill(fb, 0, 0, fb.Width, fb.Height, BackdropA, BackdropB); break;
                case NetOp.Text: DrawText(fb, in item); break;
                case NetOp.Panel:
                    NetPanel.Draw(fb, item.X, item.Y, item.X1, item.Y1, item.Kind, original: false);
                    break;
                case NetOp.Swatch:
                    // the swatch with a grey frame
                    Fill(fb, item.X - 1, item.Y - 1, item.X + item.X1 + 1, item.Y + item.Y1 + 1, Colour(NetRole.Disabled), Colour(NetRole.Disabled));
                    Fill(fb, item.X, item.Y, item.X + item.X1, item.Y + item.Y1, item.Rgb, item.Rgb);
                    break;
                case NetOp.Dialog: DrawDialog(fb, in item); break;
            }
        }
    }

    /// <summary>The framed box in the middle with the head and the body centred in it.</summary>
    static void DrawDialog(FrameBuffer fb, in NetDrawItem item)
    {
        PopupBox.Panel(fb, 130, 190, 510, 290, PanelA, PanelB);
        Text(fb, fb.Width / 2, 205, NetFont.Normal, NetAlign.Center, NetRole.Value, item.Text ?? "");
        Text(fb, fb.Width / 2, 245, NetFont.Normal, NetAlign.Center, NetRole.Label, item.Text2 ?? "");
    }

    /// <summary>Rows between the Y of a classic text line and the top of the capitals of the chat font (its capitals are about 8 px tall:
    /// they start where the 5 px capitals of the bitmap font do and, with descenders, end before the next line 10 px below).</summary>
    const int ChatCapTop = 0;

    static void DrawText(FrameBuffer fb, in NetDrawItem item)
    {
        if (item.Chat && NetChatText.Draw(fb, in item, Colour(item.Role), ChatCapTop)) return;
        Text(fb, item.X, item.Y, item.Size, item.Align, item.Role, item.Text ?? "");
    }

    static void Text(FrameBuffer fb, int x, int y, int size, NetAlign align, NetRole role, string text)
    {
        if (text.Length == 0) return;
        int scale = NetFont.ClassicScale(size), width = HudOverlay.TextWidth(text, scale);
        if (align == NetAlign.Center) x = (2 * x - width) / 2;
        else if (align == NetAlign.Right) x -= width;
        // the font only has capitals: upper-casing here (cached) spares its own copy of every lower-case text
        HudOverlay.DrawText(fb, x, y, NetText.Upper(text), Colour(role), scale);
    }

    static void Fill(FrameBuffer fb, int x0, int y0, int x1, int y1, uint a, uint b)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(fb.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(fb.Width, x1); x++)
                fb.Set(x, y, ((x + y) & 1) == 0 ? a : b);
    }
}
