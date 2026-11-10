using NFS.ModApi;
using NFS.Render;

namespace NFS.Game.Cars;

/// <summary>Loads the render model of a mod car with the chosen paint (openspec modding-api-cars 4.2; the menu colour choice of 4.4 passes its index here).</summary>
public static class ModCarModel
{
    /// <summary>The paint for colour <paramref name="colorIndex"/> of <paramref name="car"/> (out of range is clamped); null when the car has no colours.</summary>
    public static GltfPaint? Paint(CarDefinition car, int colorIndex)
    {
        if (car.Colors.Count == 0) return null;
        var c = car.Colors[Math.Clamp(colorIndex, 0, car.Colors.Count - 1)];
        return new GltfPaint(c.R, c.G, c.B, car.PaintMaterials);
    }

    /// <summary>The glTF car of a mod car definition painted with colour <paramref name="colorIndex"/> (default 0 = first colour).
    /// Throws <see cref="InvalidDataException"/> for a bad model, <see cref="InvalidOperationException"/> for an original car.</summary>
    public static GltfCar Load(CarDefinition car, int colorIndex = 0, float? groundY = null) =>
        GltfCar.Load(car.ModelPath ?? throw new InvalidOperationException($"'{car.Key}' is not a mod car"), groundY, Paint(car, colorIndex));
}
