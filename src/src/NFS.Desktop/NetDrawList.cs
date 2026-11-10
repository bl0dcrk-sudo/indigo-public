namespace NFS.Desktop;

/// <summary>What a <see cref="NetDrawItem"/> draws.</summary>
internal enum NetOp : byte { Backdrop, Text, Panel, RowBar, Swatch, Dialog, Preview }

internal enum NetAlign : byte { Left, Right, Center }

/// <summary>
/// Meaning of a text, not its colour: the backend maps a role to a colour (change network-menu-look, design D4). The classic
/// backend (<see cref="ClassicNetBackend"/>) keeps the colours of the first network menu:
/// <list type="bullet">
/// <item><see cref="Ink"/> white, <see cref="InkSelected"/> yellow (left column of the original look; unused by the classic layout)</item>
/// <item><see cref="Label"/> white: plain text, labels, hints, chat text, other players</item>
/// <item><see cref="Selected"/> yellow: the selected row, the local player</item>
/// <item><see cref="Value"/> yellow: titles and values; <see cref="ValueSelected"/> yellow: the countdown digit</item>
/// <item><see cref="Header"/> cyan: column headers, the status line, nicks in the chat, the reserved game</item>
/// <item><see cref="Disabled"/> grey: dimmed, not available, secondary information; <see cref="Muted"/> grey (unused by classic)</item>
/// <item><see cref="Good"/> green: ready, link ok, lobby, info message; <see cref="Bad"/> red: lost link, race, error</item>
/// <item><see cref="Warn"/> orange: banners, countdown status, a message from the server; the original look draws it as
/// <see cref="Value"/> or <see cref="Bad"/> (added because classic tells these apart from <see cref="Value"/>)</item>
/// <item><see cref="ButtonIdle"/> light grey: the button of a form that is not selected (original look only; selected = <see cref="Selected"/>);
/// <see cref="InkSoft"/> board brown: the lines under the name of the game on the yellow board; <see cref="InkDisabled"/>: a dimmed entry of the
/// yellow board (the vanilla dim brown; <see cref="Disabled"/> is the grey for dark panels); <see cref="InkWarn"/> dark red: a warning on the yellow board,
/// where the gold of <see cref="Warn"/> does not read. Classic does not use these four.</item>
/// </list>
/// </summary>
internal enum NetRole : byte
{
    Ink, InkSelected, Label, Selected, Value, ValueSelected, Header, Disabled, Good, Bad, Muted, Warn, ButtonIdle, InkSoft, InkDisabled, InkWarn,
}

/// <summary>Kind of a <see cref="NetOp.Panel"/>. Classic: every kind is the framed chequer panel, <see cref="ChatPlate"/> in the
/// dark fill of the plate over the race picture.</summary>
internal enum NetPanelKind : byte { Data, DataActive, Bar, Banner, ChatPlate, ChatInput }

/// <summary>One record of a <see cref="NetDrawList"/>. Which fields are used depends on <see cref="Op"/>:
/// Text: <c>X, Y, Size, Align, Role, Text</c> (<c>X</c> = left, right or centre of the text; <c>X1</c> = widest it may be in the original look, 0 = no limit); Panel / RowBar: rectangle
/// <c>X, Y, X1, Y1</c> (X1 / Y1 exclusive) and <c>Kind</c>; Swatch: <c>X, Y, X1 = width, Y1 = height, Rgb</c>; Dialog: <c>Text = head,
/// Text2 = body</c>; Preview: picture of a car of the menu, <c>X, Y</c> = top left, <c>X1</c> = car model (original look only); Backdrop: nothing.</summary>
internal struct NetDrawItem
{
    public NetOp Op;
    public NetAlign Align;
    public NetRole Role;
    public NetPanelKind Kind;
    /// <summary>Font size of a text: 13, 16, 18 or 36.</summary>
    public int Size;
    public int X, Y, X1, Y1;
    public uint Rgb;
    /// <summary>Text: when it is too wide the original look keeps its end (<c>...XYZ_</c>), not its beginning; for a field being edited.</summary>
    public bool Tail;
    /// <summary>Text: a line of the chat (lines, the input line, its hint): the backends draw it with <see cref="NFS.Render.ChatFont"/>, the system
    /// font that has every printable ASCII character, instead of the menu font (when that font is missing: the menu font as before).</summary>
    public bool Chat;
    public string? Text, Text2;
}

/// <summary>
/// The layout of one network menu frame as a list of records (change network-menu-look, design D2): <see cref="NetworkMenuLayout"/>
/// fills it, a backend draws it. The buffer is reused (<see cref="Clear"/> keeps the capacity), so a warm frame allocates nothing.
/// </summary>
internal sealed class NetDrawList
{
    NetDrawItem[] _items = new NetDrawItem[128];

    public int Count { get; private set; }

    public ReadOnlySpan<NetDrawItem> Items => new(_items, 0, Count);

    public void Clear()
    {
        // the strings are dropped with the next frame's records; clearing them would only cost time
        Count = 0;
    }

    ref NetDrawItem Next()
    {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        ref var item = ref _items[Count++];
        item = default;
        return ref item;
    }

    public void Backdrop() => Next().Op = NetOp.Backdrop;

    /// <summary>A text; <paramref name="maxWidth"/> (pixels, 0 = none) is the width the original look cuts it to with "..." (classic
    /// ignores it: its layout cuts by characters); <paramref name="tail"/> keeps the end of a text that is too wide;
    /// <paramref name="chat"/> marks chat text (<see cref="NetDrawItem.Chat"/>; <paramref name="maxWidth"/> is then in pixels in both looks).</summary>
    public void Text(int x, int y, int size, NetAlign align, NetRole role, string text, int maxWidth = 0, bool tail = false, bool chat = false)
    {
        ref var item = ref Next();
        item.Op = NetOp.Text;
        item.X = x; item.Y = y; item.Size = size; item.Align = align; item.Role = role; item.Text = text; item.X1 = maxWidth; item.Tail = tail;
        item.Chat = chat;
    }

    public void Panel(int x0, int y0, int x1, int y1, NetPanelKind kind)
    {
        ref var item = ref Next();
        item.Op = NetOp.Panel;
        item.X = x0; item.Y = y0; item.X1 = x1; item.Y1 = y1; item.Kind = kind;
    }

    public void RowBar(int x0, int y0, int x1, int y1)
    {
        ref var item = ref Next();
        item.Op = NetOp.RowBar;
        item.X = x0; item.Y = y0; item.X1 = x1; item.Y1 = y1;
    }

    public void Swatch(int x, int y, int w, int h, uint rgb)
    {
        ref var item = ref Next();
        item.Op = NetOp.Swatch;
        item.X = x; item.Y = y; item.X1 = w; item.Y1 = h; item.Rgb = rgb;
    }

    /// <summary>The picture panel of the car menu (280 x 128, <see cref="MenuScene.CarPreviewWidth"/>) of model <paramref name="car"/> at the
    /// given top left; the classic look has none.</summary>
    public void Preview(int x, int y, int car)
    {
        ref var item = ref Next();
        item.Op = NetOp.Preview;
        item.X = x; item.Y = y; item.X1 = car;
    }

    public void Dialog(string head, string body)
    {
        ref var item = ref Next();
        item.Op = NetOp.Dialog;
        item.Text = head; item.Text2 = body;
    }
}

/// <summary>Font sizes of the layout and what the classic backend makes of them (<see cref="NFS.Render.HudOverlay"/> scale).</summary>
internal static class NetFont
{
    public const int Small = 13, Medium = 16, Normal = 18, Digit = 36;

    /// <summary>Scale of the bitmap font of <see cref="NFS.Render.HudOverlay"/> for a size: 13 = 1, 16 and 18 = 2, 36 = 12.</summary>
    public static int ClassicScale(int size) => size switch { <= Small => 1, <= Normal => 2, _ => 12 };
}
