using NFS.ModApi;
namespace NFS.Game.Cars;

/// <summary>
/// The boundary between in-memory car numbers (vanilla = LegacyIndex, mods 0x35+) and the car index stored in the original
/// file formats (Replay, PersonalStats, stat records, FeConfig/UserSettings, PrefsText). Everything that reads or writes
/// an original format converts through here; mod cars have no LegacyIndex and cannot be written to those formats.
/// </summary>
public static class LegacyIndex
{
    /// <summary>In-memory number to the original index; null for mod cars and unknown numbers (skip, do not write).</summary>
    public static int? FromNumber(this CarCatalog catalog, int number)
        => catalog.TryGetByNumber(number, out var car) ? car.LegacyIndex : null;

    /// <summary>Original index read from a file to the in-memory number; null when no such original car exists.</summary>
    public static int? ToNumber(this CarCatalog catalog, int legacyIndex)
        => catalog.TryGetByLegacyIndex(legacyIndex, out var car) ? catalog.NumberOf(car.Key) : null;
}
