using System.Numerics;
using NFS.Render;

namespace NFS.Tests.Render;

/// <summary>The Duster mod model (<c>mods/indigo.duster/1.0.0/cars/duster/duster.glb</c> in the repo root, openspec modding-api-cars 1.6):
/// loads through <see cref="GltfCar"/>, fits the triangle budget and has the real car's size (Dacia Duster II: 4.34 × 1.80 × 1.69 m,
/// wheelbase 2.67 m; width without mirrors, height without the antenna), within 5%.</summary>
public class DusterModTests
{
    static string? ModelPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "mods", "indigo.duster", "1.0.0", "cars", "duster", "duster.glb");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static GltfCar Load()
    {
        var path = ModelPath();
        Assert.True(path != null, "mods/indigo.duster/1.0.0/cars/duster/duster.glb not found above " + AppContext.BaseDirectory);
        return GltfCar.Load(path!);
    }

    static IEnumerable<Vector3> Points(CarMesh mesh, int lod) =>
        mesh.Lods[lod].SelectMany(part => part.Polys.SelectMany(p => new[] { p.V0, p.V1, p.V2, p.V3 }).Select(v => part.Origin + part.Vertices[v]));

    static void Near(float expected, float actual, string what) =>
        Assert.True(MathF.Abs(actual - expected) <= 0.05f * expected, $"{what}: {actual:F3} m, expected {expected} m ± 5%");

    [Fact]
    public void DusterFitsTheTriangleBudget()
    {
        var mesh = Load().Mesh;
        for (int l = 0; l < 3; l++)
            Assert.InRange(mesh.Lods[l].Sum(p => p.Polys.Length), 1, GltfCar.TriangleBudget[l]);
        Assert.NotSame(mesh.Lods[0], mesh.Lods[1]);
        Assert.NotSame(mesh.Lods[0], mesh.Lods[2]);
        Assert.Equal(4, mesh.Lods[0].Count(p => p.Motion == CarPartMotion.Wheel));
    }

    [Fact]
    public void DusterHasTheRealCarSize()
    {
        var mesh = Load().Mesh;
        var pts = Points(mesh, 0).ToArray();
        float ground = pts.Min(v => v.Y);
        float length = pts.Max(v => v.Z) - pts.Min(v => v.Z);
        // mirrors sit above 0.9 m, the antenna is the only thing near the centre line above the roof rails
        var sides = pts.Where(v => v.Y - ground < 0.9f).ToArray();
        float width = sides.Max(v => v.X) - sides.Min(v => v.X);
        float height = pts.Where(v => MathF.Abs(v.X) > 0.1f).Max(v => v.Y) - ground;
        Near(4.34f, length, "length");
        Near(1.80f, width, "width");
        Near(1.69f, height, "height");

        // WheelAxes: 0 FR, 1 FL, 2 RR, 3 RL (XZ of the wheel centres); right = +x, front = +z
        var w = mesh.WheelAxes;
        Near(2.67f, (w[0].Y + w[1].Y) / 2 - (w[2].Y + w[3].Y) / 2, "wheelbase");
        Assert.True(w[0].X > 0 && w[2].X > 0 && w[1].X < 0 && w[3].X < 0, "right wheels on +x");
        // the wheels stand on the ground: the lowest wheel point is the lowest point of the car
        float wheelLow = mesh.Lods[0].Where(p => p.Motion == CarPartMotion.Wheel)
            .SelectMany(part => part.Vertices.Select(v => part.Origin.Y + v.Y)).Min();
        Assert.Equal(ground, wheelLow, 0.01f);
    }

    /// <summary>Scale of the game (openspec modding-api-cars 4.8): the original models are drawn 1 world unit = 1 m (CarMesh.FromGeo: 1/256 m,
    /// CarObj half sizes 1/256 m &lt;&lt; 8 in 16.16), measured by <c>render-car</c>: McLaren F1 LOD0 4.32 m long (real 4.29 m), F355 4.50 (4.25),
    /// LGT1 4.50 (~4.5), Isdera 4.61 (4.7); mean game/real length 1.01. So mod models need no scale factor and the Duster keeps the real ratio.</summary>
    [Fact]
    public void DusterLengthRelativeToMcLarenMatchesReality()
    {
        const float mcLarenGameLength = 4.32f, mcLarenRealLength = 4.29f, dusterRealLength = 4.34f;
        var pts = Points(Load().Mesh, 0).ToArray();
        float length = pts.Max(v => v.Z) - pts.Min(v => v.Z);
        Near(dusterRealLength / mcLarenRealLength, length / mcLarenGameLength, "Duster/McLaren length ratio");
    }
}
