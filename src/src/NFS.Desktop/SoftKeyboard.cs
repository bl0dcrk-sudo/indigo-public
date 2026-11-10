using System.Collections.Concurrent;
using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>On-screen keyboard hook (Android): the host injects <see cref="Platform"/> (show = true / hide = false); Desktop leaves
/// it null and nothing happens. <see cref="Update"/> runs once per frame with "is a text field being edited now" and calls the
/// hook only on a change, so entering an edit shows the keyboard and Enter / Esc / Back / leaving the screen hides it.
/// The soft keyboard's key events are too short for the polled keyboard state (down and up within one frame), so the platform
/// host queues them with <see cref="Type"/> and <see cref="Merge"/> adds them to the frame's input like physical keys:
/// characters (8 = Backspace) to <see cref="DesktopInput.Typed"/>, '\r' (Enter / Done) to <see cref="DesktopInput.Menu"/>.</summary>
public sealed class SoftKeyboard
{
    /// <summary>Set by the platform host (Android activity); may be called from the game thread.</summary>
    public static Action<bool>? Platform { get; set; }

    static readonly ConcurrentQueue<char> _queue = new();

    /// <summary>A key from the on-screen keyboard (any thread): a character, 8 = Backspace, '\r' = Enter.</summary>
    public static void Type(char c) => _queue.Enqueue(c);

    /// <summary>The frame's input with the queued soft keyboard keys (the queue is emptied); unchanged when nothing was typed.</summary>
    public static DesktopInput Merge(DesktopInput input)
    {
        if (_queue.IsEmpty) return input;
        var typed = new List<char>(input.Typed ?? []);
        var menu = new List<MenuKey>(input.Menu);
        while (_queue.TryDequeue(out char c))
        {
            if (c == '\r') menu.Add(MenuKey.Enter);
            else typed.Add(c);
        }
        return input with { Typed = typed, Menu = menu };
    }

    readonly Action<bool>? _hook;
    bool _shown;

    /// <param name="hook">Null = <see cref="Platform"/> read at call time (tests pass their own).</param>
    public SoftKeyboard(Action<bool>? hook = null) => _hook = hook;

    public bool Shown => _shown;

    public void Update(bool editing)
    {
        if (editing == _shown) return;
        _shown = editing;
        (_hook ?? Platform)?.Invoke(editing);
    }

    /// <summary>A text field is open: player / record name editor, a text dialog (file name), a network menu field or the chat line.</summary>
    internal static bool Editing(MenuController? menu, NetworkMenuHost? net) =>
        net != null ? net.Menu.Editable || net.Chat.Typing
            : menu?.NameEditing != null || menu?.Dialog?.Kind == DialogKind.Text;
}
