using System.Numerics;

namespace NFS.Render;

/// <summary>Headless car preview (CLI render-car, openspec modding-api-cars D7): one car on a neutral background from four fixed angles,
/// drawn by <see cref="TrackRenderer.RenderCarPreview"/> (the race car path). Vanilla = 640×480 with the original rasteriser; Enhanced = 1280×960
/// (k = 2) with perspective-correct UV, trilinear filter and car light.</summary>
public static class CarPreview
{
    /// <summary>Neutral grey background.</summary>
    public const uint Background = 0x707880;

    /// <summary>View name → direction from the car centre to the camera, in car space (x right, y up, z forward).</summary>
    public static IReadOnlyList<(string Name, Vector3 Direction)> Views { get; } =
    [
        ("front", new(0f, 0.25f, 1f)),
        ("rear", new(0f, 0.25f, -1f)),
        ("side", new(1f, 0.15f, 0f)),
        ("threequarter", new(0.8f, 0.45f, 0.9f)),
    ];

    public static RenderOptions Options(GraphicsMode mode) => mode == GraphicsMode.Vanilla
        ? new RenderOptions()
        : new RenderOptions { PerspectiveCorrect = true, Filter = TextureFilter.Trilinear, CarLight = true };

    /// <summary>Renders <paramref name="model"/> (LOD 0, at rest) seen along <paramref name="direction"/>; the camera distance frames the LOD 0 bounds.</summary>
    public static FrameBuffer Render(TrackRenderer renderer, CarModel model, Vector3 direction, GraphicsMode mode)
    {
        int k = mode == GraphicsMode.Vanilla ? 1 : 2;
        var fb = new FrameBuffer(640 * k, 480 * k);
        var car = new CarInstance(model, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var (min, max) = model.LocalBounds(0) ?? (new Vector3(-1, 0, -2), new Vector3(1, 1.3f, 2));
        var centre = (min + max) / 2;
        float radius = (max - min).Length() / 2;
        var cam = new Camera(fb.Width, fb.Height, hHalfDeg: 30, vHalfDeg: 22.5);
        var dir = Vector3.Normalize(direction);
        cam.Position = centre + dir * (radius / MathF.Tan(22.5f * MathF.PI / 180) * 0.88f);
        cam.LookAlong(-dir, Vector3.UnitY);
        renderer.RenderCarPreview(fb, cam, Options(mode), car, Background);
        return fb;
    }
}
