using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>
/// Chat of the network menu and race (task 15c): the received lines (a short history with arrival times) and the line being
/// typed. Pure state, no window: <see cref="Apply"/> turns one frame of <see cref="DesktopInput"/> into text editing, and while
/// the player is typing it tells the host to keep every key away from the menu and from the car.
/// <para>Key T opens the line (lobby, countdown, race, summary), Enter sends it, Esc cancels, Backspace deletes. The opening
/// key press is consumed (the "t" is not typed into the line).</para>
/// </summary>
internal sealed class NetworkChat
{
    /// <summary>Lines shown at once.</summary>
    public const int Visible = 5;
    public const int History = 20;
    /// <summary>In the race a line fades from the picture after this long (unless the player is typing).</summary>
    public const long RaceFadeMicros = 12_000_000;
    readonly List<(ChatLine Line, long At)> _lines = [];

    public bool Typing { get; private set; }
    /// <summary>The line being typed (printable ASCII, at most <see cref="NetProtocol.MaxChatLength"/> characters).</summary>
    public string Text { get; private set; } = "";
    public int GameId { get; private set; }

    public void Add(ChatLine line, long nowMicros)
    {
        _lines.Add((line, nowMicros));
        if (_lines.Count > History) _lines.RemoveRange(0, _lines.Count - History);
    }

    /// <summary>Forgets lines and the typed text (another game, or the session ended).</summary>
    public void Clear()
    {
        _lines.Clear();
        Close();
    }

    /// <summary>The chat belongs to one game: another game id starts with an empty history.</summary>
    public void Follow(int gameId)
    {
        if (gameId == GameId) return;
        GameId = gameId;
        Clear();
    }

    public void Close()
    {
        Typing = false;
        Text = "";
    }

    /// <summary>The newest lines, oldest first (at most <see cref="Visible"/>); <paramref name="maxAgeMicros"/> hides older ones
    /// (0 = no age limit). While typing nothing is hidden.</summary>
    public IReadOnlyList<ChatLine> Recent(long nowMicros, long maxAgeMicros = 0) =>
        _lines.Where(l => Typing || maxAgeMicros <= 0 || nowMicros - l.At <= maxAgeMicros)
            .TakeLast(Visible).Select(l => l.Line).ToArray();

    /// <summary><see cref="Recent"/> without allocating: copies the newest lines, oldest first, to the start of
    /// <paramref name="buffer"/> (at most its length and <see cref="Visible"/>) and returns how many.</summary>
    public int CopyRecent(ChatLine[] buffer, long nowMicros, long maxAgeMicros = 0)
    {
        int max = Math.Min(buffer.Length, Visible), count = 0;
        for (int i = _lines.Count - 1; i >= 0 && count < max; i--)
        {
            var (line, at) = _lines[i];
            if (Typing || maxAgeMicros <= 0 || nowMicros - at <= maxAgeMicros) buffer[count++] = line;
        }
        Array.Reverse(buffer, 0, count);
        return count;
    }

    /// <summary>Handles one frame. Returns true when the chat took the frame: the host must not give these keys to the menu,
    /// the pause or the car (typing must never steer). <paramref name="canOpen"/> says whether T may open the line now;
    /// <paramref name="send"/> delivers a finished line (the text is already cleaned) and returns whether it was sent.</summary>
    public bool Apply(DesktopInput input, bool canOpen, Func<string, bool> send)
    {
        if (!Typing)
        {
            if (!canOpen || input.Typed is not { } typed || !typed.Any(c => c is 't' or 'T')) return false;
            Typing = true;
            Text = "";
            return true;
        }
        foreach (char c in input.Typed ?? [])
        {
            if (c == (char)8) { if (Text.Length > 0) Text = Text[..^1]; }
            else if (c is >= ' ' and < (char)0x7F && Text.Length < NetProtocol.MaxChatLength) Text += c;
        }
        foreach (var key in input.Menu)
        {
            if (key == MenuKey.Escape) { Close(); return true; }
            if (key == MenuKey.Enter)
            {
                string line = NetProtocol.SanitizeChat(Text);
                if (line.Length > 0) send(line);
                Close();
                return true;
            }
        }
        return true;
    }
}
