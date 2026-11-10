using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class CarPreviewTests
{
    // Reference pixels of CLI render-car (openspec modding-api-cars 1.2): the CarMesh rewrite (1.3) must keep them.
    static readonly Dictionary<string, string> Reference = new()
    {
        ["MCF1_front_vanilla"] = "85d6f8782abdf4d03b7be0bce768f1d87191d75f697a359215698061253825d5",
        ["MCF1_rear_vanilla"] = "62a9aa1ca09fcccbfef1f4b09d74a99d7fb3e1d319830972d673dad0b1e004a7",
        ["MCF1_side_vanilla"] = "5f1c5b37193834349a1e2459317824cd07f1ced0860bfaedb7f6642bf6e8b2e3",
        ["MCF1_threequarter_vanilla"] = "836019fcc66342a0eefdf0b2a26c6fa54f6354548402a9e3e9d82b1c0dd97dcc",
        ["MCF1_front_enhanced"] = "d2cd37191364d9c2997f5f0600b597213462357df0270f83b9b6cb191d4b8d0e",
        ["MCF1_rear_enhanced"] = "58793244f3ece3ea194df067dbc4ff4bc7657f34cee8a4b03b58f6708a00aa2b",
        ["MCF1_side_enhanced"] = "9bbc2749356e361470cd0754d2163ec4344548f57ad10e4c302bbe68380165e2",
        ["MCF1_threequarter_enhanced"] = "c04efd3dcb5819747a3dd3b855a429a42ba71528d24e65f661bb3192627f701f",
        ["CHEV_front_vanilla"] = "3d0d3244f0c23a2c2cd22816f28386e68d5b9af95ce5ff7fc3ce70156e950d92",
        ["CHEV_rear_vanilla"] = "2e2ec88153ca0dfb7c97646066eab67406f3f4de1663c124cab115a458aaf273",
        ["CHEV_side_vanilla"] = "254594ac54e5e71538dc07b833be10a23c8f2720f625fe42764d89c70e941dd8",
        ["CHEV_threequarter_vanilla"] = "faa6fb4a96aa1f14dcfb59ffdc15055681e475a49011ea6fe41f390e5050d8fa",
        ["CHEV_front_enhanced"] = "d897e20897518f3abb9aaa29cc152bca3db58d8da681cf8bbfebffddd1f07748",
        ["CHEV_rear_enhanced"] = "3e7ebba557f34ca81c28a119f383af018f85f0343c8346a007415d281cc7eb41",
        ["CHEV_side_enhanced"] = "0c110621d85b1505526ecfe3b8cc24940ee03e6cf2954361c7c92b202ecfcefb",
        ["CHEV_threequarter_enhanced"] = "6f38cc64e2a96f0b118f59a76279e81425940cb9e91459fa328db37f76d8bec6",
    };

    [GameFact]
    public void FourViewsPerModeShowTheCarAndMatchTheReference()
    {
        var renderer = new TrackRenderer(0, GameData.Dir);
        foreach (var (name, type) in new[] { ("MCF1", 0), ("CHEV", 12) })
        {
            var model = new CarModel(name, GameData.Dir, carType: type);
            foreach (var mode in new[] { GraphicsMode.Vanilla, GraphicsMode.Enhanced })
            {
                var hashes = new HashSet<string>();
                Assert.Equal(4, CarPreview.Views.Count);
                foreach (var (view, direction) in CarPreview.Views)
                {
                    var fb = CarPreview.Render(renderer, model, direction, mode);
                    Assert.Equal(mode == GraphicsMode.Vanilla ? 640 : 1280, fb.Width);
                    int car = 0;
                    for (int i = 0; i < fb.Rgba.Length; i += 4)
                        if ((uint)(fb.Rgba[i] << 16 | fb.Rgba[i + 1] << 8 | fb.Rgba[i + 2]) != CarPreview.Background) car++;
                    Assert.InRange(car, fb.Width * fb.Height / 20, fb.Width * fb.Height / 2);   // car fills 5..50% of the frame
                    string hash = fb.Sha256();
                    Assert.True(hashes.Add(hash), $"{name} {view} {mode} same as another view");
                    Assert.Equal(Reference[$"{name}_{view}_{GraphicsModes.Name(mode)}"], hash);
                }
            }
        }
    }
}
