using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Draws a <see cref="NetOp.Panel"/> (change network-menu-look, design D5). Original look: the area is darkened like the dialog
/// window of the game (<see cref="MenuScene.Dim"/>, 70%, towards 0x080808) and framed with a 1 px line (<see cref="Border"/>,
/// <see cref="BorderActive"/> for <see cref="NetPanelKind.DataActive"/>); banners and the chat input have no frame, the chat plate
/// over the race picture is lighter (55%) and has none either. Classic look: the chequered <see cref="PopupBox"/> panel, the chat
/// plate in its dark fill.
/// </summary>
internal static class NetPanel
{
    public const uint Border = 0x5A4014, BorderActive = 0xB8862A, RowBarColour = 0x7A5210;
    public const byte DimAlpha = 0xB4, PlateAlpha = 0x8C;
    /// <summary>Opacity of the selected row bar in percent.</summary>
    public const int RowBarPercent = 45;

    const uint PanelA = 0x101820, PanelB = 0x141E28, PlateA = 0x000000, PlateB = 0x080808;

    public static void Draw(FrameBuffer fb, int x0, int y0, int x1, int y1, NetPanelKind kind, bool original)
    {
        if (!original)
        {
            if (kind == NetPanelKind.ChatPlate) PopupBox.Panel(fb, x0, y0, x1, y1, PlateA, PlateB);
            else PopupBox.Panel(fb, x0, y0, x1, y1, PanelA, PanelB);
            return;
        }
        MenuScene.Dim(fb, x0, y0, x1, y1, kind == NetPanelKind.ChatPlate ? PlateAlpha : DimAlpha);
        if (kind is NetPanelKind.Data or NetPanelKind.DataActive or NetPanelKind.Bar)
            Frame(fb, x0, y0, x1, y1, kind == NetPanelKind.DataActive ? BorderActive : Border);
    }

    /// <summary>The selection bar of a row: <see cref="RowBarColour"/> mixed in at <see cref="RowBarPercent"/>%.</summary>
    public static void RowBar(FrameBuffer fb, int x0, int y0, int x1, int y1)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(fb.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(fb.Width, x1); x++)
            {
                uint c = fb.Get(x, y);
                fb.Set(x, y, Mix(c >> 16 & 0xFF, RowBarColour >> 16 & 0xFF) << 16 | Mix(c >> 8 & 0xFF, RowBarColour >> 8 & 0xFF) << 8
                    | Mix(c & 0xFF, RowBarColour & 0xFF));
            }
    }

    static uint Mix(uint back, uint front) => (back * (100 - RowBarPercent) + front * RowBarPercent) / 100;

    static void Frame(FrameBuffer fb, int x0, int y0, int x1, int y1, uint rgb)
    {
        for (int x = x0; x < x1; x++) { Put(fb, x, y0, rgb); Put(fb, x, y1 - 1, rgb); }
        for (int y = y0; y < y1; y++) { Put(fb, x0, y, rgb); Put(fb, x1 - 1, y, rgb); }
    }

    static void Put(FrameBuffer fb, int x, int y, uint rgb)
    {
        if ((uint)x < fb.Width && (uint)y < fb.Height) fb.Set(x, y, rgb);
    }
}
