namespace NFS.ModApi;

/// <summary>Textual car identity: <c>author.mod/car</c> for mods, <c>nfs2se/NAME</c> for original cars.</summary>
public readonly record struct CarKey
{
    public const string VanillaPrefix = "nfs2se/";

    public string Value { get; }

    private CarKey(string value) => Value = value;

    /// <summary>True for <c>nfs2se/...</c> keys (reserved for the vanilla source).</summary>
    public bool IsVanilla => Value.StartsWith(VanillaPrefix, StringComparison.Ordinal);

    public static CarKey Vanilla(string name) => Parse(VanillaPrefix + name);

    /// <summary>Parses <c>left/right</c>; both parts non-empty, only letters, digits, '.', '_' and '-'.</summary>
    public static CarKey Parse(string text)
        => TryParse(text, out var key) ? key : throw new FormatException($"Invalid car key '{text}'.");

    public static bool TryParse(string? text, out CarKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(text)) return false;
        int slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1 || text.IndexOf('/', slash + 1) >= 0) return false;
        foreach (char c in text)
            if (c != '/' && !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-') return false;
        key = new CarKey(text);
        return true;
    }

    public override string ToString() => Value;
}
