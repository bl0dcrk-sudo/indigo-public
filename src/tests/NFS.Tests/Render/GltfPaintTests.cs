using NFS.Data.Gltf;
using NFS.Game.Cars;
using NFS.Game.Mods;
using NFS.ModApi;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>Paint of glTF cars (<see cref="GltfPaint"/>, openspec modding-api-cars 4.2): the recolour rule is private to
/// <see cref="GltfCar"/> (no InternalsVisibleTo for NFS.Tests), so it is checked on the repo Duster (mods/indigo.duster: UV_body is the paint,
/// UV_glass_clear is not) through the public <see cref="GltfCar.Load(string, float?, GltfPaint?)"/>, and the colour index clamping of ModCarModel.</summary>
public class GltfPaintTests
{
    static readonly GltfPaint OrangePaint = new(0xC8, 0x69, 0x1E, ["UV_body"]);
    static readonly GltfPaint BluePaint = new(0x2F, 0x4A, 0x6B, ["UV_body"]);

    static string? RepoDuster(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "mods", "indigo.duster", "1.0.0", "cars", "duster", file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static string GlbPath()
    {
        var p = RepoDuster("duster.glb");
        Assert.True(p != null, "mods/indigo.duster/1.0.0/cars/duster/duster.glb not found above " + AppContext.BaseDirectory);
        return p!;
    }

    static int MaterialIndex(GltfModel model, string name)
    {
        int i = model.Materials.ToList().FindIndex(m => m.Name == name);
        Assert.True(i >= 0, $"material '{name}' missing in duster.glb");
        return i;
    }

    static string TextureKey(int material) => $"gltf:m{material}";

    static uint Px(GltfImage img, int i) => (uint)(img.Rgba[i * 4] << 16 | img.Rgba[i * 4 + 1] << 8 | img.Rgba[i * 4 + 2]);

    /// <summary>The paint rule of GltfCar: HSV saturation above 0.35 and max channel above 40.</summary>
    static bool Saturated(uint px)
    {
        int r = (int)(px >> 16 & 255), g = (int)(px >> 8 & 255), b = (int)(px & 255);
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        return mx > 40 && (mx - mn) / (float)mx > 0.35f;
    }

    static CarDefinition LoadRepoDusterDefinition()
    {
        var carJson = RepoDuster("car.json");
        Assert.True(carJson != null, "mods/indigo.duster/1.0.0/cars/duster/car.json not found");
        string modDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(carJson!)!, "..", ".."));
        var diag = new ModDiagnostics();
        var mod = new LoadedMod(new ModManifest("indigo.duster", "1.0.0", "^1.0", ModKind.Data, ["cars"]), modDir);
        return Assert.Single(new ModCarSource([mod], diag).GetCars());
    }

    [Fact]
    public void Applies_EmptyMaterials_MatchesNamesContainingPaint()
    {
        var p = new GltfPaint(1, 2, 3, []);
        Assert.True(p.Applies("UV_Paint_body"));
        Assert.True(p.Applies("car PAINT"));
        Assert.False(p.Applies("UV_body"));
    }

    [Fact]
    public void Applies_ListedMaterials_ExactNameCaseInsensitive()
    {
        var p = new GltfPaint(1, 2, 3, ["UV_body"]);
        Assert.True(p.Applies("UV_body"));
        Assert.True(p.Applies("uv_BODY"));
        Assert.False(p.Applies("UV_body_trim"));
        Assert.False(p.Applies("UV_glass_clear"));
    }

    [ModFact]
    public void Duster_PaintedBody_DiffersFromUnpaintedBaseline()
    {
        string path = GlbPath();
        var model = GltfModel.Load(path);
        int body = MaterialIndex(model, "UV_body");
        Assert.True(model.Materials[body].Image >= 0, "UV_body is expected to be a textured material");

        var baseline = GltfCar.Load(path).Textures[TextureKey(body)].Rgb;
        var orange = GltfCar.Load(path, paint: OrangePaint).Textures[TextureKey(body)].Rgb;
        var blue = GltfCar.Load(path, paint: BluePaint).Textures[TextureKey(body)].Rgb;
        Assert.False(baseline.SequenceEqual(orange), "orange paint did not change the body");
        Assert.False(orange.SequenceEqual(blue), "two colours gave the same body");
    }

    [ModFact]
    public void Duster_NonPaintMaterial_IdenticalForEveryColour()
    {
        string path = GlbPath();
        int glass = MaterialIndex(GltfModel.Load(path), "UV_glass_clear");
        var plain = GltfCar.Load(path).Textures[TextureKey(glass)].Rgb;
        Assert.True(plain.SequenceEqual(GltfCar.Load(path, paint: OrangePaint).Textures[TextureKey(glass)].Rgb));
        Assert.True(plain.SequenceEqual(GltfCar.Load(path, paint: BluePaint).Textures[TextureKey(glass)].Rgb));
    }

    [ModFact]
    public void Duster_GreyTexels_StayAsTheyAreForEveryColour()
    {
        string path = GlbPath();
        var model = GltfModel.Load(path);
        int body = MaterialIndex(model, "UV_body");
        var src = model.Images[model.Materials[body].Image];
        var plain = GltfCar.Load(path).Textures[TextureKey(body)].Rgb;
        var orange = GltfCar.Load(path, paint: OrangePaint).Textures[TextureKey(body)].Rgb;
        int checkedTexels = 0;
        for (int i = 0; i < plain.Length; i++)
        {
            if (Saturated(Px(src, i))) continue;
            checkedTexels++;
            Assert.Equal(plain[i], orange[i]);
        }
        Assert.True(checkedTexels > 0, "the body texture has no grey texels to check");
    }

    [ModFact]
    public void Duster_PaintedTexels_TakeTheTargetHue()
    {
        string path = GlbPath();
        var model = GltfModel.Load(path);
        int body = MaterialIndex(model, "UV_body");
        var src = model.Images[model.Materials[body].Image];
        var orange = GltfCar.Load(path, paint: OrangePaint).Textures[TextureKey(body)].Rgb;
        var blue = GltfCar.Load(path, paint: BluePaint).Textures[TextureKey(body)].Rgb;
        int n = 0;
        long orangeRb = 0, blueRb = 0;
        for (int i = 0; i < orange.Length; i++)
        {
            if (!Saturated(Px(src, i))) continue;
            n++;
            orangeRb += (long)(orange[i] >> 16 & 255) - (orange[i] & 255);
            blueRb += (long)(blue[i] >> 16 & 255) - (blue[i] & 255);
        }
        Assert.True(n > 0, "no paint texels in the body texture");
        Assert.True(orangeRb > 0, "orange paint is not reddish");
        Assert.True(blueRb < 0, "blue paint is not bluish");
    }

    [ModFact]
    public void Duster_PaintedTexels_KeepRelativeBrightness()
    {
        string path = GlbPath();
        var model = GltfModel.Load(path);
        int body = MaterialIndex(model, "UV_body");
        var src = model.Images[model.Materials[body].Image];
        var orange = GltfCar.Load(path, paint: OrangePaint).Textures[TextureKey(body)].Rgb;
        // the target's R (0xC8) is its maximum channel: out R = 200 * sourceMax / meanPaintMax, so out R / source max is constant
        var ratios = new List<double>();
        for (int i = 0; i < orange.Length; i++)
        {
            int mx = Math.Max(src.Rgba[i * 4], Math.Max(src.Rgba[i * 4 + 1], src.Rgba[i * 4 + 2]));
            if (mx < 100 || !Saturated(Px(src, i))) continue;
            ratios.Add((orange[i] >> 16 & 255) / (double)mx);
        }
        Assert.True(ratios.Count > 10, "too few bright paint texels to check");
        double min = ratios.Min(), max = ratios.Max();
        Assert.True(max / min < 1.03, $"brightness ratio not constant: {min:F4}..{max:F4}");
    }

    [ModFact]
    public void ModCarModel_ColourIndexIsClampedToTheColourList()
    {
        var car = LoadRepoDusterDefinition();
        var last = car.Colors[^1];
        var first = car.Colors[0];

        var high = ModCarModel.Paint(car, 99)!;
        Assert.Equal((last.R, last.G, last.B), (high.R, high.G, high.B));
        var low = ModCarModel.Paint(car, -3)!;
        Assert.Equal((first.R, first.G, first.B), (low.R, low.G, low.B));
        Assert.Equal(["UV_body"], high.Materials);

        string path = GlbPath();
        int body = MaterialIndex(GltfModel.Load(path), "UV_body");
        var clampedHigh = ModCarModel.Load(car, 99).Textures[TextureKey(body)].Rgb;
        Assert.True(clampedHigh.SequenceEqual(ModCarModel.Load(car, car.Colors.Count - 1).Textures[TextureKey(body)].Rgb));
        var clampedLow = ModCarModel.Load(car, -3).Textures[TextureKey(body)].Rgb;
        Assert.True(clampedLow.SequenceEqual(ModCarModel.Load(car, 0).Textures[TextureKey(body)].Rgb));
        Assert.False(clampedLow.SequenceEqual(clampedHigh));
    }
}
