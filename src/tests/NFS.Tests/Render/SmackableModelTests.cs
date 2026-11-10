using System.Numerics;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class SmackableModelTests
{
    [GameFact]
    public void SmackablesUseTheirUnanimatedGeometryAtEveryLod()
    {
        foreach (var (name, type) in new[] { ("CART", 41), ("BEER", 50), ("RUIN", 51) })
        {
            var model = new CarModel(name, GameData.Dir, carType: type);
            int expected = Enumerable.Range(20, 4).Sum(i => model.Geo.Parts[i].Polygons.Length);
            Assert.True(expected > 0);
            for (int lod = 0; lod <= 2; lod++)
            {
                Assert.Equal(new[] { 20, 21, 22, 23 }, model.PartsForLod(lod));
                var instance = new CarInstance(model, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, lod);
                Assert.Equal(expected, instance.Polys().Count());
                Assert.All(instance.Polys(), p => Assert.NotNull(p.Tex));
            }
        }
    }
}
