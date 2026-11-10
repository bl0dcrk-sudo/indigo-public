using NFS.Game.Mods;
using NFS.ModApi;

namespace NFS.Game.Cars;

/// <summary>Cars of the loaded data mods: <c>cars/*/car.json</c> of every mod with capability "cars" (at most
/// <see cref="ModPaths.MaxCarsPerMod"/> per mod, directories in ordinal order). A broken car becomes a <see cref="ModDiagnostics"/> error
/// and is skipped; the rest of the mod continues. Mod cars get in-memory numbers from 0x35 in this order (see <see cref="CarCatalog"/>).</summary>
public sealed class ModCarSource(IReadOnlyList<LoadedMod> mods, ModDiagnostics diagnostics) : ICarSource
{
    public const string CarsDir = "cars";
    public const string CarFile = "car.json";

    public string Name => "mods";
    public bool IsVanilla => false;

    public IEnumerable<CarDefinition> GetCars()
    {
        foreach (var mod in mods)
        {
            string id = mod.Manifest.Id;
            if (!mod.Manifest.Capabilities.Contains("cars")) continue;
            string carsDir = Path.Combine(mod.Dir, CarsDir);
            string[] dirs;
            try
            {
                if (!Directory.Exists(carsDir)) continue;
                dirs = Directory.GetDirectories(carsDir);
                Array.Sort(dirs, StringComparer.Ordinal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Error(id, $"cannot list '{CarsDir}': {e.Message}");
                continue;
            }
            int count = 0;
            foreach (var dir in dirs)
            {
                string name = Path.GetFileName(dir);
                CarDefinition? car = null;
                try
                {
                    if (!File.Exists(Path.Combine(dir, CarFile))) continue;
                    if (++count > ModPaths.MaxCarsPerMod)
                    {
                        diagnostics.Error(id, $"more than {ModPaths.MaxCarsPerMod} cars, '{name}' and the rest skipped");
                        break;
                    }
                    string rel = $"{CarsDir}/{name}/{CarFile}";
                    if (!ModPaths.TryResolveFile(mod.Dir, rel, out var full, out var pe)) { diagnostics.Error(id, $"{rel}: {pe}"); continue; }
                    if (!CarJsonParser.TryParse(File.ReadAllText(full), id, mod.Dir, name, diagnostics, out car, out var error))
                    {
                        diagnostics.Error(id, $"{rel}: {error}");
                        continue;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    diagnostics.Error(id, $"{CarsDir}/{name}: {e.Message}");
                    continue;
                }
                yield return car!;
            }
        }
    }
}
