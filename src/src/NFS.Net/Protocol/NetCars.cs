using NFS.Game.Cars;
using NFS.ModApi;

namespace NFS.Net.Protocol;

/// <summary>Conversion between car keys of the lobby messages and in-memory car numbers. The original cars have fixed numbers
/// (<see cref="VanillaCarSource.KeyOf"/>); mod cars exist only in a <see cref="CarCatalog"/> (same mods on both sides, see
/// <see cref="NetModSet"/>).</summary>
public static class NetCars
{
    static readonly CarKey[] VanillaKeys = [.. Enumerable.Range(0, CarCatalog.VanillaCount).Select(VanillaCarSource.KeyOf)];

    /// <summary>Key of an original car number, null for <see cref="NetProtocol.NoCarType"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not an original car number.</exception>
    public static CarKey? VanillaKey(int carType)
    {
        if (carType == NetProtocol.NoCarType) return null;
        if (carType is < 0 or >= CarCatalog.VanillaCount) throw new ArgumentOutOfRangeException(nameof(carType));
        return VanillaKeys[carType];
    }

    /// <summary>Original car number of a key, <see cref="NetProtocol.NoCarType"/> for null, a mod key or an unknown key.</summary>
    public static int VanillaNumber(CarKey? key)
    {
        if (key is not { } k) return NetProtocol.NoCarType;
        int i = -1;
        for (int n = 0; n < VanillaKeys.Length && i < 0; n++)
            if (string.Equals(VanillaKeys[n].Value, k.Value, StringComparison.Ordinal)) i = n;   // no boxing: the menu calls this every frame
        return i >= 0 ? i : NetProtocol.NoCarType;
    }

    /// <summary>Key of a car number in <paramref name="catalog"/> (null = original cars only).</summary>
    public static CarKey? KeyOf(int carType, CarCatalog? catalog)
    {
        if (carType == NetProtocol.NoCarType) return null;
        if (carType < CarCatalog.VanillaCount) return carType >= 0 ? VanillaKeys[carType] : null;
        return catalog is not null && catalog.TryGetByNumber(carType, out var def) ? def.Key : null;
    }

    /// <summary>Number of a key in <paramref name="catalog"/> (null = original cars only); <see cref="NetProtocol.NoCarType"/> when unknown.</summary>
    public static int NumberOf(CarKey? key, CarCatalog? catalog)
    {
        if (key is not { } k) return NetProtocol.NoCarType;
        if (catalog is not null) return catalog.TryGet(k, out _) ? catalog.NumberOf(k) : NetProtocol.NoCarType;
        return VanillaNumber(k);
    }

    /// <summary>True when a player may select the car in a network lobby: the showcase cars 0..11 and every mod car of the catalog
    /// (not the cheat cars 12..14 and not the other original cars).</summary>
    public static bool IsSelectable(CarDefinition car) => car.LegacyIndex is null || car.LegacyIndex <= NetProtocol.MaxCarType;

    /// <summary>True when a player may select the car number: 0..11, or a mod car of <paramref name="catalog"/>.</summary>
    public static bool IsSelectableNumber(int carType, CarCatalog? catalog)
        => NetProtocol.IsCarType(carType)
            || carType >= CarCatalog.VanillaCount && catalog is not null && catalog.TryGetByNumber(carType, out var def) && IsSelectable(def);

    /// <summary>Selectable car numbers in menu order: 0..11, then the mod cars.</summary>
    public static List<int> SelectableNumbers(CarCatalog? catalog)
    {
        var list = new List<int>();
        for (int i = 0; i <= NetProtocol.MaxCarType; i++) list.Add(i);
        if (catalog is not null) foreach (var c in catalog.ModCars) list.Add(catalog.NumberOf(c.Key));
        return list;
    }

    /// <summary>Palette size of a car number: original cars from the colour table, mod cars from car.json "colors" (at least 1).</summary>
    public static int PaletteSize(int carType, CarCatalog? catalog)
    {
        if (carType is >= 0 and <= NetProtocol.MaxCarType) return Math.Max(1, (int)NFS.Core.Constants.FrontEndConstants.ColourTable[carType * 2 + 1]);
        return catalog is not null && catalog.TryGetByNumber(carType, out var def) && def.LegacyIndex is null ? Math.Max(1, def.Colors.Count) : 1;
    }

    /// <summary>First palette entry of a car number (original: colour table; mod: 0, colours are indices into car.json "colors").</summary>
    public static int PaletteFirst(int carType)
        => carType is >= 0 and <= NetProtocol.MaxCarType ? NFS.Core.Constants.FrontEndConstants.ColourTable[carType * 2] : 0;
}
