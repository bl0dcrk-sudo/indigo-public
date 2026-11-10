using NFS.Data.FrontEnd;

namespace NFS.Game.FrontEnd;

/// <summary>Names of players 1 and 2 (split screen): profile names at +0x1AC / +0x1B5 (<c>/de035</c>) and their copies [ref:1947]
/// used in the race at +0xE4 / +0xED (<c>/ddf6d</c>). Original-game routines: (default "Player1/2"), (empty → default) [ref:1948]
/// (copy for the race), (clearing in KO/final), (end of series). `TEXT 0x29D` = "Player". [ref:1949]</summary>
public static class PlayerNames
{
    /// <summary>Editor character limit (statchk gives 8; the field has 9 B including the zero terminator). [ref:1950]</summary>
    public const int MaxChars = 8;
    public const int PlayerTextId = 0x29D, PromptTextId = 0x2E3;

    /// <summary><c>txt(0x29D)</c> (≤ 8 characters) + "1" for player 1 / "2" for player 2 (<c>/</c>). [ref:1951]</summary>
    public static string DefaultName(string playerText, int player)
    {
        string head = playerText.Length < FeConfig.NameSize ? playerText : playerText[..MaxChars];
        return Truncate(head + (player == 0 ? "1" : "2"));
    }

    /// <summary>(after editing): an empty name or spaces only → "Player" + "1" (also for player 2). [ref:1952]</summary>
    public static string Fill(string name, string playerText) =>
        name.Length == 0 || name.All(ch => ch == ' ') ? Truncate(playerText + "1") : name;

    /// <summary>A name equal to "Player1" or "Player2" = "replace all" mode on the first key press in the editor. [ref:1953]</summary>
    public static bool IsDefault(string name, string playerText) =>
        name.Equals(playerText + "1", StringComparison.OrdinalIgnoreCase) || name.Equals(playerText + "2", StringComparison.OrdinalIgnoreCase);

    /// <summary>Characters accepted by the editor (returns 1) – the whole ASCII table except <c># $ % &lt; = &gt; @ ^ ~</c>, plus accented letters [ref:1954]
    /// Latin-1 from the original's list; 8 (Backspace) and 0x5300 (Delete) also pass the filter.</summary>
    public static bool Accepts(int key) =>
        key is 8 or 0x5300 or >= 0x20 and <= 0x22 or >= 0x26 and <= 0x3A or 0x3B or 0x3F or >= 0x41 and <= 0x5D or >= 0x5F and <= 0x7D
            or 0x91 or 0x92 or 0xA1 or 0xB0 or 0xBF or 0xC1 or 0xE1 or 0xC4 or 0xE4 or 0xC5 or 0xE5 or 199 or 0xE7 or 0xC9 or 0xE9
            or 0xCD or 0xED or 0xD1 or 0xF1 or 0xD3 or 0xF3 or 0xD6 or 0xF6 or 0xDA or 0xFA or 0xDC or 0xFC;

    /// <summary>Sets "Player1" and "Player2" in the profile. [ref:1955]</summary>
    public static void ResetProfile(FeConfig c, string playerText)
    {
        for (int p = 0; p < 2; p++) c.SetName(FeConfig.ProfileNameOffset(p), DefaultName(playerText, p));
    }

    /// <summary>An empty name in the profile (e.g. a config without names) gets the default, the same as with default settings. [ref:1956]</summary>
    public static void EnsureProfile(FeConfig c, string playerText)
    {
        for (int p = 0; p < 2; p++)
            if (c.ProfileName(p).Length == 0) c.SetName(FeConfig.ProfileNameOffset(p), DefaultName(playerText, p));
    }

    /// <summary>With one player only player 1's name is copied to the race; split copies both. [ref:1957]</summary>
    public static void CopyToRace(FeConfig c, bool split)
    {
        for (int p = 0; p < (split ? 2 : 1); p++)
            c.SetName(FeConfig.RaceNameOffset(p), c.ProfileName(p));
    }

    /// <summary>(<c>1</c>, i.e. split): a call with <paramref name="final"/> (KO final) clears both race names [ref:1958]
    /// and without it only when the first human is no longer active (<c>*== 0</c> in the series mask). [ref:1959]</summary>
    public static void ClearRace(FeConfig c, bool split, bool final, bool humanZeroActive)
    {
        if (!split || !final && humanZeroActive) return;
        Array.Clear(c.Raw, FeConfig.RaceNameOffset(0), 2 * FeConfig.NameSize);
    }

    /// <summary>In split (end or abort of the series): race names are cleared, the profile returns to "Player1/2", then copied to [ref:1960]
    /// the race names and the config is saved (port: <c>PersistRequested</c>). [ref:1961]</summary>
    public static void EndSeries(FeConfig c, string playerText)
    {
        ClearRace(c, split: true, final: true, humanZeroActive: false);
        ResetProfile(c, playerText);
        CopyToRace(c, split: true);
    }

    private static string Truncate(string s) => s.Length > MaxChars ? s[..MaxChars] : s;
}

/// <summary>Player name editor (called from statchk; text field with a "_" cursor blinking every [ref:1962]
/// 15 clock units). Keys: Enter accepts (0xD), Esc keeps the old name (0x1B), Backspace (8) deletes a character; when the name
/// at the start is "Player1/2", the first character or Backspace replaces/deletes the whole name. The result passes through Fill. [ref:1963]</summary>
public sealed class NameEditor
{
    private readonly string _original, _playerText;
    private readonly List<char> _text;
    private bool _replace;   // "replace all" mode; the first key overwrites the whole text [ref:1964] [ref:3417]
    private int _blink;      // blink counter [ref:3418]

    /// <param name="name">Starting name (from the profile).</param>
    /// <param name="playerText">TEXT 0x29D "Player".</param>
    public NameEditor(string name, string playerText)
    {
        _original = name; _playerText = playerText;
        _text = [.. name.Length > PlayerNames.MaxChars ? name[..PlayerNames.MaxChars] : name];
        _replace = PlayerNames.IsDefault(name, playerText);
        CursorVisible = false;
    }

    /// <summary>Text in the field (without the cursor).</summary>
    public string Text => new([.. _text]);
    /// <summary>The "_" cursor is appended to the drawn text when the flag is enabled. [ref:1965] [ref:3419]</summary>
    public bool CursorVisible { get; private set; }
    public bool Finished { get; private set; }
    /// <summary>true = Enter (name accepted), false = Esc (old name).</summary>
    public bool Accepted { get; private set; }
    /// <summary>Result after finishing: the typed name (Enter) or the old one (Esc), in both cases after Fill. [ref:1966]</summary>
    public string Result => PlayerNames.Fill(Finished && Accepted ? Text : _original, _playerText);

    /// <summary>One key (ASCII code: 0xD Enter, 0x1B Esc, 8 Backspace, characters ≥ 0x20). [ref:1967]</summary>
    public void Key(int key)
    {
        if (Finished || key == 0) return;
        if (key == 0xD) { Accepted = true; Finished = true; return; }
        if (key == 0x1B) { Finished = true; return; }
        if (PlayerNames.Accepts(key))
        {
            if (key == 8 && _text.Count > 0)
            {
                if (!_replace) _text.RemoveAt(_text.Count - 1);
                else { _text.Clear(); _replace = false; }
            }
            else if (_text.Count < PlayerNames.MaxChars && key is > 0x1F and < 0xFF)
            {
                if (_replace) { _text.Clear(); _replace = false; }
                _text.Add((char)key);
            }
            else if (_replace)
            {
                _text.Clear(); _replace = false;
                if (key is > 0x1F and < 0xFF) _text.Add((char)key);
            }
        }
    }

    /// <summary>Blink clock: after exceeding <c>14</c> units the cursor toggles. [ref:3420]</summary>
    public void Advance(int ticks)
    {
        _blink += ticks;
        if (_blink > 0xE) { CursorVisible = !CursorVisible; _blink = 0; }
    }
}
