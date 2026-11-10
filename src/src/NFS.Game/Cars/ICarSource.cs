using NFS.ModApi;
namespace NFS.Game.Cars;

/// <summary>Supplies cars to the <see cref="CarCatalog"/> at startup.</summary>
public interface ICarSource
{
    string Name { get; }
    /// <summary>True only for the vanilla source: the sole one allowed to use <c>nfs2se/</c> keys and legacy indices.</summary>
    bool IsVanilla { get; }
    IEnumerable<CarDefinition> GetCars();
}
