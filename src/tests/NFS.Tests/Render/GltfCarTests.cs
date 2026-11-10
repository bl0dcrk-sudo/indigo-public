using System.Numerics;
using NFS.Data;
using NFS.Data.Gltf;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class GltfCarTests
{
    /// <summary>Builds a .glb of axis-aligned boxes (one mesh + node each, CCW outward faces as glTF requires). Per box: node name, centre,
    /// half size, parent (−1 = scene root). Material 0 = red, 1 = green (+Z face of the body), 2 = textured with a 2×2 PNG.</summary>
    sealed class GlbBuilder
    {
        public readonly List<(string Name, Vector3 Centre, Vector3 Half, int Parent)> Boxes = [];
        public string Extra = "";
        public string NodeExtra = "";

        public static GlbBuilder Car()
        {
            var b = new GlbBuilder();
            // glTF: car front +Z, car right = −X; wheels Ø 0.6 m, ground at y = 0
            b.Boxes.Add(("body", new Vector3(0, 0.8f, 0), new Vector3(0.9f, 0.5f, 2.1f), -1));
            b.Boxes.Add(("wheel_fl", new Vector3(0.8f, 0.3f, 1.3f), new Vector3(0.1f, 0.3f, 0.3f), -1));
            b.Boxes.Add(("wheel_fr", new Vector3(-0.8f, 0.3f, 1.3f), new Vector3(0.1f, 0.3f, 0.3f), -1));
            b.Boxes.Add(("wheel_rl", new Vector3(0.8f, 0.3f, -1.3f), new Vector3(0.1f, 0.3f, 0.3f), -1));
            b.Boxes.Add(("wheel_rr", new Vector3(-0.8f, 0.3f, -1.3f), new Vector3(0.1f, 0.3f, 0.3f), -1));
            return b;
        }

        // faces as CCW quads (outward normal by the right-hand rule); index bits: x = 1, y = 2, z = 4
        static readonly int[][] Faces = [[1, 3, 7, 5], [0, 4, 6, 2], [2, 6, 7, 3], [0, 1, 5, 4], [4, 5, 7, 6], [0, 2, 3, 1]];

        public byte[] Build()
        {
            var bin = new MemoryStream();
            var w = new BinaryWriter(bin);
            var views = new List<string>(); var accessors = new List<string>(); var meshes = new List<string>(); var nodes = new List<string>();
            void View(long start, string target = "") => views.Add($"{{\"buffer\":0,\"byteOffset\":{start},\"byteLength\":{bin.Length - start}{target}}}");
            for (int i = 0; i < Boxes.Count; i++)
            {
                var (name, c, h, _) = Boxes[i];
                // 4 vertices per face (separate UVs), positions relative to the node
                long p0 = bin.Length;
                var min = -h; var max = h;
                foreach (var f in Faces) foreach (int v in f)
                {
                    w.Write((v & 1) != 0 ? h.X : -h.X); w.Write((v & 2) != 0 ? h.Y : -h.Y); w.Write((v & 4) != 0 ? h.Z : -h.Z);
                }
                View(p0);
                accessors.Add($"{{\"bufferView\":{views.Count - 1},\"componentType\":5126,\"count\":24,\"type\":\"VEC3\",\"min\":[{F(min.X)},{F(min.Y)},{F(min.Z)}],\"max\":[{F(max.X)},{F(max.Y)},{F(max.Z)}]}}");
                long u0 = bin.Length;
                for (int f = 0; f < 6; f++) { w.Write(0f); w.Write(0f); w.Write(1f); w.Write(0f); w.Write(1f); w.Write(1f); w.Write(0f); w.Write(1f); }
                View(u0);
                accessors.Add($"{{\"bufferView\":{views.Count - 1},\"componentType\":5126,\"count\":24,\"type\":\"VEC2\"}}");
                int posAcc = accessors.Count - 2, uvAcc = accessors.Count - 1;
                // two primitives for the body (+Z face green), one otherwise
                var prims = new List<string>();
                foreach (var (faces, mat) in i == 0 ? new[] { (new[] { 0, 1, 2, 3, 5 }, 0), (new[] { 4 }, 1) } : new[] { (new[] { 0, 1, 2, 3, 4, 5 }, 2) })
                {
                    long i0 = bin.Length;
                    foreach (int f in faces) foreach (int k in new[] { 0, 1, 2, 0, 2, 3 }) w.Write((ushort)(f * 4 + k));
                    while (bin.Length % 4 != 0) w.Write((byte)0);
                    var idxLen = faces.Length * 6 * 2;
                    views.Add($"{{\"buffer\":0,\"byteOffset\":{i0},\"byteLength\":{idxLen}}}");
                    accessors.Add($"{{\"bufferView\":{views.Count - 1},\"componentType\":5123,\"count\":{faces.Length * 6},\"type\":\"SCALAR\"}}");
                    prims.Add($"{{\"attributes\":{{\"POSITION\":{posAcc},\"TEXCOORD_0\":{uvAcc}}},\"indices\":{accessors.Count - 1},\"material\":{mat}}}");
                }
                meshes.Add($"{{\"primitives\":[{string.Join(",", prims)}]}}");
            }
            // PNG 2x2 (white / grey) for material 2
            var png = PngWriter.EncodeRgba(2, 2, [255, 255, 255, 255, 128, 128, 128, 255, 128, 128, 128, 255, 255, 255, 255, 255]);
            long g0 = bin.Length; w.Write(png); View(g0);
            while (bin.Length % 4 != 0) w.Write((byte)0);
            int pngView = views.Count - 1;

            var roots = new List<int>();
            for (int i = 0; i < Boxes.Count; i++)
            {
                var (name, c, _, parent) = Boxes[i];
                var pc = parent >= 0 ? Boxes[parent].Centre : Vector3.Zero;
                var t = c - pc;
                var kids = Enumerable.Range(0, Boxes.Count).Where(k => Boxes[k].Parent == i).ToArray();
                nodes.Add($"{{\"name\":\"{name}\",\"mesh\":{i},\"translation\":[{F(t.X)},{F(t.Y)},{F(t.Z)}]" +
                    (kids.Length > 0 ? $",\"children\":[{string.Join(",", kids)}]" : "") + (i == 0 ? NodeExtra : "") + "}");
                if (parent < 0) roots.Add(i);
            }
            var json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[" + string.Join(",", roots) + "]}]," +
                "\"nodes\":[" + string.Join(",", nodes) + "],\"meshes\":[" + string.Join(",", meshes) + "]," +
                "\"materials\":[{\"name\":\"red\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,0,0,1]}},{\"name\":\"green\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0,1,0,1]}}," +
                "{\"name\":\"tyre\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]," +
                $"\"textures\":[{{\"source\":0}}],\"images\":[{{\"bufferView\":{pngView},\"mimeType\":\"image/png\"}}]," +
                "\"accessors\":[" + string.Join(",", accessors) + "],\"bufferViews\":[" + string.Join(",", views) + "]," +
                $"\"buffers\":[{{\"byteLength\":{bin.Length}}}]{Extra}}}";
            return GltfModel.Pack(json, bin.ToArray());
        }

        static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    static GltfCar Load(byte[] glb, float? ground = null) => GltfCar.FromGltf(GltfModel.Parse(glb), ground);

    [Fact]
    public void BoxCarWithFourWheelsLoads()
    {
        var car = Load(GlbBuilder.Car().Build(), ground: -0.55f);
        var mesh = car.Mesh;
        Assert.Equal(5, mesh.Lods[0].Count);
        Assert.Same(mesh.Lods[0], mesh.Lods[1]);   // no lod1 / lod2: LOD0 everywhere
        Assert.Same(mesh.Lods[0], mesh.Lods[2]);
        Assert.Equal(CarPartMotion.Lean, mesh.Lods[0][0].Motion);
        Assert.Equal(new[] { 0, 1, 2, 3 }, mesh.Lods[0].Skip(1).Select(p => p.Wheel));
        Assert.All(mesh.Lods[0].Skip(1), p => Assert.Equal(CarPartMotion.Wheel, p.Motion));
        Assert.Equal(12, mesh.Lods[0][0].Polys.Length);
        // wheel order FR, FL, RR, RL in car space (x right = glTF −X, z forward)
        var expect = new[] { new Vector2(0.8f, 1.3f), new Vector2(-0.8f, 1.3f), new Vector2(0.8f, -1.3f), new Vector2(-0.8f, -1.3f) };
        for (int i = 0; i < 4; i++) Assert.True(Vector2.Distance(expect[i], mesh.WheelAxes[i]) < 1e-5f, $"wheel {i}: {mesh.WheelAxes[i]}");
        // wheel part origin = wheel centre, on the road plane −0.55 + radius 0.3
        Assert.True(Vector3.Distance(new Vector3(0.8f, -0.25f, 1.3f), mesh.Lods[0][1].Origin) < 1e-5f, mesh.Lods[0][1].Origin.ToString());
        var model = new CarModel("box", car);
        var (min, max) = model.LocalBounds(0)!.Value;
        Assert.Equal(-0.55f, min.Y, 4);
        Assert.Equal(0.75f, max.Y, 4);
        Assert.Equal(0f, (min.X + max.X) / 2, 4);
        Assert.Equal(0f, (min.Z + max.Z) / 2, 4);
        // every face points outwards as in the .geo: cross(v1 − v0, v2 − v0) away from the part centre
        foreach (var part in mesh.Lods[0])
        {
            var c = part.Vertices.Aggregate(Vector3.Zero, (a, v) => a + v) / part.Vertices.Length;
            foreach (var p in part.Polys)
            {
                var v0 = part.Vertices[p.V0]; var v1 = part.Vertices[p.V1]; var v2 = part.Vertices[p.V2];
                Assert.True(Vector3.Dot(Vector3.Cross(v1 - v0, v2 - v0), (v0 + v1 + v2) / 3 - c) > 0, $"{part.Name} face inwards");
                Assert.Equal(p.V2, p.V3);
            }
        }
        // materials: factor → solid texture, image → PNG texels
        Assert.Equal(0xFF0000u, model.TextureFor("gltf:m0")!.Rgb[0]);
        Assert.Equal(0x00FF00u, model.TextureFor("gltf:m1")!.Rgb[0]);
        var tyre = model.TextureFor("gltf:m2")!;
        Assert.Equal((2, 2), (tyre.Width, tyre.Height));
        Assert.Equal(new uint[] { 0xFFFFFF, 0x808080, 0x808080, 0xFFFFFF }, tyre.Rgb);
    }

    [Fact]
    public void DefaultGroundPutsTheModelBoxCentreAtTheOrigin()
    {
        var model = new CarModel("box", Load(GlbBuilder.Car().Build()));
        var (min, max) = model.LocalBounds(0)!.Value;
        Assert.Equal(-0.65f, min.Y, 4);
        Assert.Equal(0.65f, max.Y, 4);
    }

    [Fact]
    public void WheelsUnderABodyNodeKeepTheirOwnRole()
    {
        var b = GlbBuilder.Car();
        for (int i = 1; i < 5; i++) b.Boxes[i] = b.Boxes[i] with { Parent = 0 };
        var mesh = Load(b.Build()).Mesh;
        Assert.Equal(12, mesh.Lods[0][0].Polys.Length);   // body without the wheels
        Assert.True(mesh.WheelAxes[0].X > 0.79f && mesh.WheelAxes[0].Y > 1.29f);
    }

    [Fact]
    public void Lod1AndLod2NodesAreUsed()
    {
        var b = GlbBuilder.Car();
        b.Boxes.Add(("lod1", new Vector3(0, 0.8f, 0), new Vector3(0.9f, 0.5f, 2.1f), -1));
        b.Boxes.Add(("LOD2", new Vector3(0, 0.8f, 0), new Vector3(0.9f, 0.5f, 2.1f), -1));
        var mesh = Load(b.Build()).Mesh;
        Assert.Equal("lod1", Assert.Single(mesh.Lods[1]).Name);
        Assert.Equal("lod2", Assert.Single(mesh.Lods[2]).Name);
        Assert.Equal(Vector3.Zero, mesh.Lods[1][0].Origin);
    }

    [Fact]
    public void MissingWheelIsRejected()
    {
        var b = GlbBuilder.Car();
        b.Boxes.RemoveAll(x => x.Name == "wheel_rl");
        var e = Assert.Throws<InvalidDataException>(() => Load(b.Build()));
        Assert.Contains("wheel_rl", e.Message);
    }

    [Fact]
    public void SkinningIsRejected()
    {
        var b = GlbBuilder.Car();
        b.Extra = ",\"skins\":[{\"joints\":[0]}]";
        Assert.Contains("skinning", Assert.Throws<InvalidDataException>(() => Load(b.Build())).Message);
        b.Extra = ",\"animations\":[{\"channels\":[],\"samplers\":[]}]";
        Assert.Contains("animations", Assert.Throws<InvalidDataException>(() => Load(b.Build())).Message);
        b.Extra = ",\"extensionsRequired\":[\"KHR_draco_mesh_compression\"]";
        Assert.Contains("KHR_draco_mesh_compression", Assert.Throws<InvalidDataException>(() => Load(b.Build())).Message);
    }

    [Fact]
    public void TruncatedFileIsRejected()
    {
        var glb = GlbBuilder.Car().Build();
        foreach (int len in new[] { 0, 10, 40, glb.Length / 2, glb.Length - 1 })
            Assert.Contains("truncated", Assert.Throws<InvalidDataException>(() => GltfModel.Parse(glb[..len])).Message);
        // an accessor that points past the BIN chunk
        var json = "{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":100,\"type\":\"VEC3\"}],\"bufferViews\":[{\"buffer\":0,\"byteLength\":12}],\"buffers\":[{\"byteLength\":12}]}";
        Assert.Contains("outside", Assert.Throws<InvalidDataException>(() => GltfModel.Parse(GltfModel.Pack(json, new byte[12]))).Message);
    }

    [Fact]
    public void SwappedWheelsAreRejected()
    {
        var b = GlbBuilder.Car();
        int fl = b.Boxes.FindIndex(x => x.Name == "wheel_fl"), fr = b.Boxes.FindIndex(x => x.Name == "wheel_fr");
        b.Boxes[fl] = b.Boxes[fl] with { Name = "wheel_fr" };
        b.Boxes[fr] = b.Boxes[fr] with { Name = "wheel_fl" };
        Assert.Contains("right", Assert.Throws<InvalidDataException>(() => Load(b.Build())).Message);
    }

    [Fact]
    public void TriangleBudgetIsEnforced()
    {
        var b = GlbBuilder.Car();
        for (int i = 0; i < 300; i++) b.Boxes.Add(($"trim{i}", new Vector3(0, 1.3f, 0), new Vector3(0.1f), 0));
        Assert.Contains("budget", Assert.Throws<InvalidDataException>(() => Load(b.Build())).Message);
    }

    [Fact]
    public void PngReaderDecodesWhatPngWriterWrites()
    {
        var rgba = new byte[5 * 3 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 37);
        var (w, h, back) = PngReader.Decode(PngWriter.EncodeRgba(5, 3, rgba));
        Assert.Equal((5, 3), (w, h));
        Assert.Equal(rgba, back);
        var idx = PngReader.Decode(PngWriter.EncodeIndexed(2, 1, [0, 1], [10, 20, 30, 40, 50, 60], transparentIndex: 1));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 0 }, idx.Rgba);
        Assert.Throws<InvalidDataException>(() => PngReader.Decode(PngWriter.EncodeRgba(5, 3, rgba), maxSide: 4));
    }

    /// <summary>The renderer culls back faces: seen from the front, the green +Z face must show; with a wrong winding the inside of the red rear face would.</summary>
    [GameFact]
    public void FrontViewShowsTheFrontFace()
    {
        var renderer = new TrackRenderer(0, GameData.Dir);
        var model = new CarModel("box", Load(GlbBuilder.Car().Build()));
        foreach (var mode in new[] { GraphicsMode.Vanilla, GraphicsMode.Enhanced })
        {
            int green = 0, red = 0;
            var fb = CarPreview.Render(renderer, model, CarPreview.Views.First(v => v.Name == "front").Direction, mode);
            var px = fb.Rgba;
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 1] > 2 * px[i] + 20 && px[i + 1] > 60) green++;
                if (px[i] > 2 * px[i + 1] + 20 && px[i] > 60) red++;
            }
            Assert.True(green > fb.Width * fb.Height / 20, $"{mode}: green {green}, red {red}");
        }
    }
}
