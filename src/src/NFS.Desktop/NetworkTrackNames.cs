using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>Track names of the network screens: the same texts the local race menu lists (the value texts of the track item,
/// config offset 0x10, from TEXT.xxx of the chosen language, <see cref="PortLanguage"/>). Without game data the screens keep the numbered fallback.</summary>
internal static class NetworkTrackNames
{
    /// <summary>The names in track order (upper case), or null when the game data cannot be read.</summary>
    public static IReadOnlyList<string>? Load(string? gameDir) => Load(gameDir, PortLanguage.Current);

    /// <summary>Same for an explicit language index 0..5.</summary>
    public static IReadOnlyList<string>? Load(string? gameDir, int language)
    {
        try
        {
            var tree = MenuTables.Load(TextFile.ForLanguage(language, gameDir));
            var item = tree.Screens.SelectMany(s => s.Items).FirstOrDefault(i => i.ConfigOffset == 0x10);
            if (item?.ValueTexts is not { Count: > 0 } texts) return null;
            return [.. texts.Select(t => t.Trim().ToUpperInvariant())];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException
            or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Name of <paramref name="track"/> (0-based); "TRACK n" when there is no list or no usable entry.</summary>
    public static string Name(IReadOnlyList<string>? names, int track) =>
        names != null && track >= 0 && track < names.Count && names[track].Length > 0 ? names[track] : NetText.Join("TRACK ", NetText.Int(track + 1));
}
