using System.Text.Json;
using System.Text.Json.Serialization;
using NFS.Data.FrontEnd;
using NFS.Game.Constants;

namespace NFS.Game.FrontEnd;

/// <summary>Front-end screen tree. The raw tree is [ref:1929]
/// <see cref="MenuConstants"/>; this class adds the texts from TEXT.ENG.
/// Screen: +0 char* art file, +4 char* background shape, +8 u16 type (handler index) [ref:1930]
/// +0xA u16 selected item (100 = mouse/none), +0xC inline item*[] terminated by 0.
/// Item (20 B): +0 u16 kind (2 selectable, 0 label, 1 special), +2 u16 text id (TEXT.*), +4 target
/// (screen*, 0 none, −1 back, −2 cancel, −3 race/action, −4 quit), +8 u8 flags, +9 u8 action code,
/// +0xC value pointer (or 1/2 = player), +0x10 u16* value text ids (0-terminated). [ref:1931]</summary>
public static class MenuTables
{
    /// <summary>The menu tree. With <paramref name="text"/> the texts of the items and of their value lists are filled in;
    /// without it they stay null.</summary>
    public static MenuTree Load(TextFile? text = null) => new(
        [.. MenuConstants.Screens.Select(s => s with { Items = [.. s.Items.Select(i => WithText(i, text))] })],
        MenuConstants.Handlers, MenuConstants.NonScreenTargets);

    private static MenuItem WithText(MenuItem item, TextFile? text)
    {
        if (text == null) return item;
        string? T(int id) => id < text.Entries.Count ? text[id] : null;
        return item with { Text = T(item.TextId), ValueTexts = [.. item.ValueTextIds.Select(id => T(id) ?? "")] };
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <param name="NonScreenTargets">item targets inside the region that are not screen structs (left unresolved)</param>
public sealed record MenuTree(IReadOnlyList<MenuScreen> Screens, IReadOnlyList<string> Handlers, IReadOnlyList<string> NonScreenTargets)
{
    Dictionary<string, MenuScreen>? _index;
    /// <summary>First screen with this id (an index instead of a LINQ scan: the menu looks its screen up several times per frame).</summary>
    public MenuScreen this[string id]
    {
        get
        {
            if (_index == null)
            {
                var index = new Dictionary<string, MenuScreen>(StringComparer.Ordinal);
                foreach (var s in Screens) index.TryAdd(s.Id, s);
                _index = index;
            }
            return _index.TryGetValue(id, out var screen) ? screen : throw new InvalidOperationException("Sequence contains no matching element");
        }
    }
    public string ToJson() => JsonSerializer.Serialize(this, MenuTables.Json);
}

/// <param name="Type">handler index [ref:1932]</param>
public sealed record MenuScreen(string Id, string? Art, string? Background, int Type, int Selected,
    IReadOnlyList<MenuItem> Items);

/// <param name="Kind">2 selectable, 0 label, 1 special</param>
/// <param name="Target">target screen id or back/cancel/race/quit</param>
/// <param name="Flags">1 disabled, 4 multi-value, 8 DONE, 0x10 CANCEL</param>
/// <param name="ConfigOffset">value byte offset in CONFIG.DAT [ref:1933]</param>
/// <param name="Shape">+0x10 when it holds 4 inline characters (kind 1 items: FSH shape name) instead of a list pointer</param>
/// <param name="ValueRaw">+0xC when not a pointer (1/2 = player) [ref:1934]</param>
public sealed record MenuItem(string Id, int Kind, int TextId, string? Text,
    string? Target, int Flags, int Action, int? ConfigOffset, int? ValueRaw, IReadOnlyList<int> ValueTextIds,
    IReadOnlyList<string>? ValueTexts, string? Shape);
