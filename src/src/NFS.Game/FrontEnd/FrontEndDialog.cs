namespace NFS.Game.FrontEnd;

public enum DialogKind { Message, Confirm, List, Text }

/// <summary>Modal front-end dialog: a message (closed by any key or when the time elapses [ref:1663]
/// <see cref="MessageTicks"/> = 128 Hz clock units), a yes/no question [ref:1664] [ref:3410]
/// a scrollable list and a text field (file name). The state is data only [ref:1665]
/// the controller passes keys, the scene draws, and after <see cref="Done"/> the controller calls the continuation of the calling screen.</summary>
public sealed class FrontEndDialog
{
    /// <summary>Display time of the message (0x15E). [ref:1666]</summary>
    public const int MessageTicks = 0x15E;
    /// <summary>Number of visible list rows. [ref:1667]</summary>
    public const int VisibleRows = 8;
    /// <summary>List panel slide-in: the panel "bsld" with its rows moves in from the right by 8 px per frame (from the panel width [ref:1668]
    /// down while the offset is non-negative), then the tab "tsld" with the text "cancel" moves by 4 px per frame from 0x28 to 0.</summary>
    public const int PanelIntroStep = 8, TabIntroStart = 0x28, TabIntroStep = 4;

    /// <summary>One frame of the list slide-in: panel offset to the right and tab offset (null = tab not yet drawn).</summary>
    public readonly record struct ListIntroFrame(int PanelOffset, int? TabOffset);

    /// <summary>Frames of the list panel slide-in (loops before the key loop): first the panel offsets [ref:1669]
    /// <c>width, width − 8, … ≥ 0</c> (the original does not go down to zero when the width is not a multiple of 8), then the panel in
    /// place and the tab 0x28, 0x24, … 0. Each frame ends with a display, without its own wait.</summary>
    public static IReadOnlyList<ListIntroFrame> ListIntroFrames(int panelWidth)
    {
        var frames = new List<ListIntroFrame>();
        for (int offset = panelWidth; offset >= 0; offset -= PanelIntroStep) frames.Add(new(offset, null));
        for (int offset = TabIntroStart; offset >= 0; offset -= TabIntroStep) frames.Add(new(0, offset));
        return frames;
    }

    /// <summary>Exit after Enter/Esc: the panel with rows, the tab "tsld" and the arrows move together to the right [ref:1670]
    /// by 12 px per frame; the loop ends when the offset exceeds 0xD2.</summary>
    public const int ExitStep = 0xC, ExitLimit = 0xD2;

    /// <summary>Offsets of successive frames of the list exit (0, 12, … 204 = 18 frames): each frame is the background from the buffer, panel
    /// 0x2C00 at (dx, 0), rows at x = 0x1E8 + dx (colour 0, without "cancel" and without selection), tab, arrows "uppd" / "dwnd"
    /// (when there is something to scroll) at dx + 2, and the display. The original does not read keys during this time.</summary>
    public static IReadOnlyList<int> ListExitFrames()
    {
        var frames = new List<int>();
        for (int offset = 0; offset <= ExitLimit; offset += ExitStep) frames.Add(offset);
        return frames;
    }

    /// <summary>Vertical step of the "cancel" text: <c>FILD(font height) · 0.6</c> (double) truncated to an integer [ref:1671]
    /// (sets the FPU to truncate). Computed exactly on the double fraction, without error for a product close to an integer. [ref:1672]</summary>
    public static int VerticalLetterStep(int fontHeight) => (int)((long)fontHeight * 5404319552844595L >> 53);

    /// <summary>(mode 2): characters forbidden in a file name (space * / ? &lt; &gt; " : \ |.). [ref:1673]</summary>
    public const string FileNameForbidden = " */?<>\":\\|.";

    private readonly string _yesKeys;
    private readonly List<char> _entry = [];
    private bool _replace;     // the first character replaces the default name [ref:1674]
    private int _blink, _age, _rememberedRow;
    private readonly int _maxChars;

    private FrontEndDialog(DialogKind kind, int textId, string yesKeys, IReadOnlyList<string>? items, string initial, int maxChars)
    {
        Kind = kind; TextId = textId; _yesKeys = yesKeys; Items = items ?? [];
        _maxChars = maxChars;
        if (kind == DialogKind.List) Selected = Items.Count == 0 ? -1 : 0;
        if (kind == DialogKind.Text) { _entry.AddRange(initial.Length > maxChars ? initial[..maxChars] : initial); _replace = true; }
    }

    public DialogKind Kind { get; }
    /// <summary>Message/Confirm: question TEXT; Text: header TEXT (e.g. 0x2E1 "save tournament as").</summary>
    public int TextId { get; }
    /// <summary>List: entries (in file-list order).</summary>
    public IReadOnlyList<string> Items { get; }
    /// <summary>Confirm: selected "yes"; default "no".</summary>
    public bool Yes { get; private set; }
    /// <summary>List: selected entry, −1 = the "cancel" button. [ref:3411]</summary>
    public int Selected { get; private set; } = -1;
    /// <summary>List: first visible entry. [ref:3412]</summary>
    public int Scroll { get; private set; }
    public bool Done { get; private set; }
    /// <summary>Text: field text without the cursor.</summary>
    public string Entry => new([.. _entry]);
    /// <summary>Text: the cursor "_" is appended to the drawn text. [ref:1675]</summary>
    public bool CursorVisible { get; private set; }
    public bool CanScrollUp => Scroll > 0;
    public bool CanScrollDown => Scroll + VisibleRows < Items.Count;

    /// <summary>Result after <see cref="Done"/>: Confirm 1 = yes; List entry index or −1 (cancel/Esc); Text 1 = name accepted
    /// (Enter and non-empty), 0 = Esc or empty; Message 0.</summary>
    public int Result { get; private set; }

    public static FrontEndDialog Message(int textId) => new(DialogKind.Message, textId, "", null, "", 0);

    /// <param name="yesKeys">Letters of "yes" in the current language (table, upper and lower case). [ref:1676]</param>
    public static FrontEndDialog Confirm(int textId, string yesKeys) => new(DialogKind.Confirm, textId, yesKeys, null, "", 0);

    public static FrontEndDialog List(IReadOnlyList<string> items) => new(DialogKind.List, 0, "", items, "", 0);

    /// <param name="initial">Default field text (TEXT 0x2F8 "name") – the first character replaces it entirely.</param>
    public static FrontEndDialog TextEntry(int promptId, string initial, int maxChars = 8) =>
        new(DialogKind.Text, promptId, "", null, initial, maxChars);

    /// <summary>Navigation key (direction codes 0x4800/0x4B00/0x4D00/0x5000, 0xD Enter, 0x1B Esc).</summary>
    public void Key(MenuKey key)
    {
        if (Done) return;
        switch (Kind)
        {
            case DialogKind.Message: Finish(0); break;
            case DialogKind.Confirm:
                if (key == MenuKey.Enter) Finish(Yes ? 1 : 0);
                else if (key == MenuKey.Escape) { Yes = false; Finish(0); }
                else if (key is MenuKey.Left or MenuKey.Right) Yes = !Yes;
                break;
            case DialogKind.List: ListKey(key); break;
            case DialogKind.Text:
                if (key == MenuKey.Enter) Finish(_entry.Count == 0 ? 0 : 1);
                else if (key == MenuKey.Escape) Finish(0);
                break;
        }
    }

    /// <summary>Keyboard character (ASCII, 8 = Backspace): letters "yes"/"n" in a question, input into a text field.</summary>
    public void Char(int ascii)
    {
        if (Done || ascii == 0) return;
        if (Kind == DialogKind.Message) { Finish(0); return; }
        if (Kind == DialogKind.Confirm)
        {
            if (ascii is 'N' or 'n') { Yes = false; Finish(0); }
            else if (_yesKeys.Contains((char)ascii)) { Yes = true; Finish(1); }
            return;
        }
        if (Kind != DialogKind.Text) return;
        if (ascii == 8 && _entry.Count > 0)
        {
            if (!_replace) _entry.RemoveAt(_entry.Count - 1);
            else { _entry.Clear(); _replace = false; }
        }
        else if (ascii is > 0x1F and < 0xFF && !FileNameForbidden.Contains((char)ascii))
        {
            if (_replace) { _entry.Clear(); _replace = false; }
            if (_entry.Count < _maxChars) _entry.Add((char)ascii);
        }
    }

    /// <summary>128 Hz clock: blinking field cursor (toggles after &gt; 0xE units) and the message time limit.</summary>
    public void Advance(int ticks)
    {
        if (Done) return;
        if (Kind == DialogKind.Text)
        {
            _blink += ticks;
            if (_blink > 0xE) { CursorVisible = !CursorVisible; _blink = 0; }
        }
        else if (Kind == DialogKind.Message)
        {
            _age += ticks;
            if (_age >= MessageTicks) Finish(0);
        }
    }

    private void ListKey(MenuKey key)
    {
        switch (key)
        {
            case MenuKey.Enter: Finish(Selected >= 0 && Selected < Items.Count ? Selected : -1); break;
            case MenuKey.Escape: Finish(-1); break;
            case MenuKey.Up:
                if (Selected > 0) { Selected--; if (Selected < Scroll) Scroll--; }
                break;
            case MenuKey.Down:
                if (Selected >= 0 && Selected + 1 < Items.Count) { Selected++; if (Selected - Scroll >= VisibleRows) Scroll++; }
                break;
            case MenuKey.Left or MenuKey.Right when Items.Count > 0:
                // (0x4B00/0x4D00/Tab): switches between the list and the "cancel" button, remembering the row. [ref:1677]
                if (Selected >= 0) { _rememberedRow = Selected; Selected = -1; }
                else Selected = _rememberedRow;
                break;
        }
    }

    private void Finish(int result) { Result = result; Done = true; }
}
