using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Draws the text items marked <see cref="NetDrawItem.Chat"/> with <see cref="ChatFont"/> for both backends. Without a system font
/// (<see cref="ChatFont.Shared"/> null) <see cref="Draw"/> returns false and the backend draws the item with its own font as before.
/// </summary>
internal static class NetChatText
{
    /// <summary>Margin kept free at the edge of the buffer by a long text (the same as the original look uses).</summary>
    const int Margin = 4;

    /// <summary>Width in pixels of a chat text, or -1 when there is no chat font.</summary>
    public static int Measure(string text) => ChatFont.Shared is { } f ? f.Measure(text) : -1;

    /// <summary>Draws a chat item whose top of capitals is <paramref name="capTop"/> pixels below its Y; false when it cannot (no font).</summary>
    public static bool Draw(FrameBuffer fb, in NetDrawItem item, uint rgb, int capTop)
    {
        if (ChatFont.Shared is not { } font) return false;
        string text = item.Text ?? "";
        if (text.Length == 0) return true;
        int room = item.Align switch
        {
            NetAlign.Left => fb.Width - Margin - item.X,
            NetAlign.Right => item.X - Margin,
            _ => 2 * Math.Min(item.X, fb.Width - item.X) - 2 * Margin,
        };
        if (item.X1 > 0) room = Math.Min(room, item.X1);
        text = font.Fit(text, room, item.Tail);
        if (text.Length == 0) return true;
        int x = item.Align switch { NetAlign.Right => item.X - font.Measure(text), NetAlign.Center => item.X - font.Measure(text) / 2, _ => item.X };
        font.Draw(fb, text, x, item.Y + capTop, rgb);
        return true;
    }
}
