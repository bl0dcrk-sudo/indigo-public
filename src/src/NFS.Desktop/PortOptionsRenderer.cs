using NFS.Render;

namespace NFS.Desktop;

/// <summary>Draws <see cref="PortOptionsScreen"/> on the 2D layer (640×480, the font and plate style of <see cref="GfxNotice"/>): a translucent dark
/// panel over whatever is below (menu or the paused race), the rows as "LABEL … VALUE", the selected one highlighted, rows ignored by Vanilla dimmed.</summary>
internal static class PortOptionsRenderer
{
    public const int PanelW = 420, RowH = 21;

    public static void Draw(FrameBuffer fb, PortOptionsScreen s, int k)
    {
        const int titleScale = 3, rowScale = 2, helpScale = 1, pad = 14;
        int rows = PortOptionsScreen.Rows.Length;
        int h = pad * 2 + 5 * titleScale + 14 + rows * RowH + 8 + 4 * 12;
        int w = PanelW, x0 = (fb.Width - w) / 2, y0 = Math.Max(4, (fb.Height - h) / 2);
        for (int y = y0; y < Math.Min(fb.Height, y0 + h); y++)
            for (int x = x0; x < Math.Min(fb.Width, x0 + w); x++) fb.Blend(x, y, 0x000000, 205);
        PopupBox.Frame(fb, x0, y0, w, h, 0x101820);   // dark border + bevel, then the yellow line inside
        Frame(fb, x0 + 3, y0 + 3, w - 6, h - 6);
        const string title = "PORT OPTIONS";
        HudOverlay.DrawText(fb, (fb.Width - HudOverlay.TextWidth(title, titleScale)) / 2, y0 + pad, title, 0xFFD060, titleScale);
        int y1 = y0 + pad + 5 * titleScale + 14;
        for (int i = 0; i < rows; i++)
        {
            var o = PortOptionsScreen.Rows[i];
            bool sel = i == s.Selected, active = s.IsActive(o);
            int ry = y1 + i * RowH;
            if (sel)
                for (int y = ry - 4; y < ry + RowH - 6; y++)
                    for (int x = x0 + 6; x < x0 + w - 6; x++) fb.Blend(x, y, 0xFFD060, 55);
            uint label = sel ? 0xFFFF40u : active ? 0xFFFFFFu : 0x808080u;
            uint value = sel ? 0xFFFF40u : active ? 0x80FF80u : 0x707070u;
            string text = (sel ? "> " : "  ") + PortOptionsScreen.Label(o);
            int lx = o == PortOption.Back ? x0 + (w - HudOverlay.TextWidth(text, rowScale)) / 2 : x0 + pad;
            HudOverlay.DrawText(fb, lx, ry, text, label, rowScale);
            string v = s.Value(o);
            if (v.Length > 0)
            {
                string shown = sel && active ? "< " + v + " >" : v;
                HudOverlay.DrawText(fb, x0 + w - pad - HudOverlay.TextWidth(shown, rowScale), ry, shown, value, rowScale);
            }
        }
        int hy = y1 + rows * RowH + 8;
        bool vanilla = s.Settings.Mode == GraphicsMode.Vanilla;
        Centered(fb, x0, w, hy, vanilla ? "VANILLA IGNORES THE DIMMED ROWS" : s.Settings.QualityText(k), vanilla ? 0xFFD060u : 0xC0C0C0u, helpScale);
        Centered(fb, x0, w, hy + 12, "UP/DOWN SELECT   LEFT/RIGHT/ENTER CHANGE", 0xFFFFFF, helpScale);
        Centered(fb, x0, w, hy + 24, "ESC / F7 / BACK CLOSE", 0xFFFFFF, helpScale);
        Centered(fb, x0, w, hy + 36, "LANGUAGE APPLIES AFTER RESTART", 0xC0C0C0, helpScale);
    }

    static void Centered(FrameBuffer fb, int x0, int w, int y, string text, uint colour, int scale)
    {
        int tw = HudOverlay.TextWidth(text, scale);
        HudOverlay.DrawText(fb, x0 + Math.Max(2, (w - tw) / 2), y, text, colour, scale);
    }

    static void Frame(FrameBuffer fb, int x0, int y0, int w, int h)
    {
        for (int x = x0; x < x0 + w; x++) { fb.Blend(x, y0, 0xFFD060, 200); fb.Blend(x, y0 + h - 1, 0xFFD060, 200); }
        for (int y = y0; y < y0 + h; y++) { fb.Blend(x0, y, 0xFFD060, 200); fb.Blend(x0 + w - 1, y, 0xFFD060, 200); }
    }
}
