using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>Full names of the car models of the network menu (<c>McLaren F1</c>, not the short <c>MCF1</c>), taken from the game's
/// TEXT file like the original statistics screens (<see cref="TrackRecords.CarName"/>).</summary>
internal static class NetworkCarNames
{
    /// <summary>The names of the selectable models 0..<see cref="NetProtocol.MaxCarType"/> (upper case), or null when the game data
    /// cannot be read.</summary>
    public static IReadOnlyList<string>? Load(string? gameDir) => Load(gameDir, PortLanguage.Current);

    /// <summary>Same for an explicit language index 0..5.</summary>
    public static IReadOnlyList<string>? Load(string? gameDir, int language)
    {
        try
        {
            var text = TextFile.ForLanguage(language, gameDir);
            return [.. Enumerable.Range(0, NetProtocol.MaxCarType + 1).Select(car => TrackRecords.CarName(text, car).Trim().ToUpperInvariant())];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DirectoryNotFoundException or FileNotFoundException
            or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Name of <paramref name="car"/>: the full name when the list has one, otherwise the short name of the port
    /// (<see cref="RaceConstants.CarNames"/>), otherwise "CAR n".</summary>
    public static string Name(IReadOnlyList<string>? names, int car)
    {
        if (names != null && car >= 0 && car < names.Count && names[car].Length > 0) return names[car];
        return Short(car);
    }

    /// <summary>Short code of the model (<c>MCF1</c>) as the tables of the network menu show it; "CAR n" when the port has none.</summary>
    public static string Short(int car) =>
        car >= 0 && car < RaceConstants.CarNames.Count ? NetText.Upper(RaceConstants.CarNames[car]) : NetText.Join("CAR ", NetText.Int(car));
}
