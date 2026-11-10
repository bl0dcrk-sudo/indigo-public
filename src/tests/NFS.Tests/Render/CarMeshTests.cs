using System.Numerics;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class CarMeshTests
{
    [GameFact]
    public void GeoBecomesThreeLodsWithCornerUvsAndRoles()
    {
        var model = new CarModel("MCF1", GameData.Dir, carType: 0);
        var mesh = model.Mesh;
        Assert.Equal(new[] { 20, 4, 2 }, mesh.Lods.Select(l => l.Count));
        int polys = 0;
        for (int pi = 0; pi < 20; pi++)
        {
            var part = mesh.Lods[0][pi];
            Assert.Equal(pi is >= 12 and <= 19 ? CarPartMotion.Wheel : CarPartMotion.Lean, part.Motion);
            Assert.Equal(pi is >= 12 and <= 19 ? (pi - 12) / 2 : -1, part.Wheel);
            foreach (var p in part.Polys)
            {
                Assert.Equal(new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) }, Enumerable.Range(0, 4).Select(p.Uv));
                Assert.All(Enumerable.Range(0, 4), k => Assert.InRange(p.Corner(k), 0, part.Vertices.Length - 1));
                polys++;
            }
        }
        Assert.Equal(model.Geo.Parts.Take(20).Sum(p => p.Polygons.Length), polys);
        Assert.All(mesh.Lods[1].Concat(mesh.Lods[2]), p => Assert.Equal(Vector3.Zero, p.Origin));
        // front wheels sit ahead of the rear ones, right wheels at +x
        Assert.True(mesh.WheelAxes[0].Y > mesh.WheelAxes[2].Y && mesh.WheelAxes[0].X > mesh.WheelAxes[1].X);
    }
}
