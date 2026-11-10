using NFS.ModApi;
namespace NFS.Game.Cars;

/// <summary>Single registry of all cars. Filled by sources in a fixed order, then frozen.</summary>
public sealed class CarCatalog
{
    /// <summary>Number of original cars; first in-memory number given to a mod car.</summary>
    public const int VanillaCount = 0x35;

    private readonly List<CarDefinition> _cars = [];
    private readonly List<int> _numbers = [];
    private readonly Dictionary<CarKey, int> _byKey = [];
    private int _nextModNumber = VanillaCount;

    public bool IsFrozen { get; private set; }

    /// <summary>Cars in registration order.</summary>
    public IReadOnlyList<CarDefinition> Cars => _cars;

    public int Count => _cars.Count;

    /// <summary>True when at least one car comes from a mod (a number from <see cref="VanillaCount"/> up).</summary>
    public bool HasModCars => _nextModNumber > VanillaCount;

    /// <summary>Mod cars in registration order (the order of their numbers).</summary>
    public IEnumerable<CarDefinition> ModCars => _cars.Where(c => c.LegacyIndex is null);

    /// <summary>Registers sources in the given order, collects rejections as diagnostics, then freezes.</summary>
    public static (CarCatalog Catalog, IReadOnlyList<string> Diagnostics) Build(IEnumerable<ICarSource> sources)
    {
        var catalog = new CarCatalog();
        var diagnostics = new List<string>();
        foreach (var source in sources)
            foreach (var car in source.GetCars())
                if (!catalog.TryRegister(car, source.IsVanilla, out var error))
                    diagnostics.Add($"{source.Name}: {error}");
        catalog.Freeze();
        return (catalog, diagnostics);
    }

    /// <summary>Registers a car; on rejection returns false with a message and leaves the catalog unchanged.</summary>
    /// <exception cref="InvalidOperationException">Catalog already frozen.</exception>
    public bool TryRegister(CarDefinition car, bool fromVanillaSource, out string? error)
    {
        if (IsFrozen) throw new InvalidOperationException("CarCatalog is frozen.");
        error = null;
        if (car.Key.IsVanilla != fromVanillaSource)
            error = fromVanillaSource
                ? $"vanilla car '{car.Key}' must use the '{CarKey.VanillaPrefix}' prefix"
                : $"prefix '{CarKey.VanillaPrefix}' is reserved for the vanilla source ('{car.Key}')";
        else if (fromVanillaSource && car.LegacyIndex is not (>= 0 and < VanillaCount))
            error = $"vanilla car '{car.Key}' needs LegacyIndex 0..0x34";
        else if (!fromVanillaSource && car.LegacyIndex is not null)
            error = $"mod car '{car.Key}' must not have a LegacyIndex";
        else if (_byKey.ContainsKey(car.Key))
            error = $"duplicate car key '{car.Key}'";
        else if (fromVanillaSource && _numbers.Contains(car.LegacyIndex!.Value))
            error = $"duplicate LegacyIndex {car.LegacyIndex} ('{car.Key}')";
        if (error is not null) return false;

        _byKey[car.Key] = _cars.Count;
        _cars.Add(car);
        _numbers.Add(fromVanillaSource ? car.LegacyIndex!.Value : _nextModNumber++);
        return true;
    }

    /// <summary>Registers a car or throws <see cref="InvalidOperationException"/>.</summary>
    public void Register(CarDefinition car, bool fromVanillaSource)
    {
        if (!TryRegister(car, fromVanillaSource, out var error)) throw new InvalidOperationException(error);
    }

    public void Freeze() => IsFrozen = true;

    public bool TryGet(CarKey key, out CarDefinition car)
    {
        if (_byKey.TryGetValue(key, out int i)) { car = _cars[i]; return true; }
        car = null!;
        return false;
    }

    public CarDefinition Get(CarKey key)
        => TryGet(key, out var car) ? car : throw new KeyNotFoundException($"Unknown car '{key}'.");

    /// <summary>In-memory number: LegacyIndex for vanilla cars, 0x35.. in registration order for mods. Never persisted.</summary>
    public int NumberOf(CarKey key)
        => _byKey.TryGetValue(key, out int i) ? _numbers[i] : throw new KeyNotFoundException($"Unknown car '{key}'.");

    /// <summary>Table-only catalog of the 0x35 original cars (no game files needed); the default for code without its own catalog.</summary>
    public static CarCatalog VanillaTables => _vanillaTables.Value;
    private static readonly Lazy<CarCatalog> _vanillaTables = new(() => Build([new VanillaCarSource()]).Catalog);

    /// <summary>Original car with the given <see cref="CarDefinition.LegacyIndex"/> (the value stored in the original file formats).</summary>
    public bool TryGetByLegacyIndex(int legacyIndex, out CarDefinition car)
    {
        foreach (var c in _cars)
            if (c.LegacyIndex == legacyIndex) { car = c; return true; }
        car = null!;
        return false;
    }

    public bool TryGetByNumber(int number, out CarDefinition car)
    {
        int i = _numbers.IndexOf(number);
        if (i >= 0) { car = _cars[i]; return true; }
        car = null!;
        return false;
    }
}
