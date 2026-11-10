using System.Numerics;
using NFS.Data.Gltf;

namespace NFS.Render;

/// <summary>Paint of a glTF car: materials named in <paramref name="Materials"/> (case-insensitive; empty = names containing "paint") take colour
/// (<paramref name="R"/>,<paramref name="G"/>,<paramref name="B"/>, sRGB 0..255). A textured material: the saturated texels (the paint, found as
/// the texels with HSV saturation &gt; 0.35) are recoloured keeping their relative brightness (target x value / mean paint value), grey, white and
/// black parts (trim, glass, lamps) stay; a texture with no such texels is multiplied by the colour. An untextured material is replaced by the colour.</summary>
public sealed record GltfPaint(byte R, byte G, byte B, IReadOnlyList<string> Materials)
{
    public bool Applies(string materialName) => Materials.Count == 0
        ? materialName.Contains("paint", StringComparison.OrdinalIgnoreCase)
        : Materials.Any(m => string.Equals(m, materialName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Mod car model from glTF (openspec modding-api-cars D6): <see cref="CarMesh"/> + its textures (named <c>gltf:m&lt;material&gt;</c>).</summary>
public sealed record GltfCar(CarMesh Mesh, IReadOnlyDictionary<string, Texture> Textures)
{
    /// <summary>Triangle budget of D5/D6 per LOD (LOD0, lod1, lod2).</summary>
    public static readonly int[] TriangleBudget = [3000, 600, 100];

    static readonly string[] WheelNodes = ["wheel_fr", "wheel_fl", "wheel_rr", "wheel_rl"];   // CarMesh wheel order 0 FR, 1 FL, 2 RR, 3 RL
    static readonly string[] RoleNodes = ["body", .. WheelNodes, "lod1", "lod2"];

    public static GltfCar Load(string path, float? groundY = null, GltfPaint? paint = null) => FromGltf(GltfModel.Load(path), groundY, paint);

    /// <summary>Roles by node name (case-insensitive): <c>body</c>, <c>wheel_fl/fr/rl/rr</c> (the node origin is the wheel centre), optional <c>lod1</c>,
    /// <c>lod2</c> (missing: LOD0 is drawn at that distance too). A role takes the meshes of its node and of the descendants that are not role nodes.
    /// glTF space (right-handed, +Y up, car front +Z, so the car's right is −X) → car space (x right, y up, z forward): x = −x, the triangle order is
    /// reversed (v0, v2, v1) so that, as in the .geo, cross(v1 − v0, v2 − v0) points out of a front face. The body bbox centre goes to x = z = 0 and
    /// the lowest wheel point to <paramref name="groundY"/> (car-local road plane; null = half the model height below the centre, i.e. the model's
    /// box centre at CarObj.Pos). Throws <see cref="InvalidDataException"/> for a missing role, misplaced wheels or a blown triangle budget.</summary>
    public static GltfCar FromGltf(GltfModel model, float? groundY = null, GltfPaint? paint = null)
    {
        var world = model.WorldMatrices();
        var roleOf = new Dictionary<int, string>();
        var nodeOf = new Dictionary<string, int>();
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            string name = model.Nodes[i].Name.ToLowerInvariant();
            if (System.Array.IndexOf(RoleNodes, name) < 0 || world[i] == null) continue;
            if (!nodeOf.TryAdd(name, i)) throw new InvalidDataException($"glTF car: more than one '{name}' node");
            roleOf[i] = name;
        }
        foreach (var need in RoleNodes.Take(5))
            if (!nodeOf.ContainsKey(need)) throw new InvalidDataException($"glTF car: no '{need}' node (needs body, wheel_fl, wheel_fr, wheel_rl, wheel_rr)");

        // raw triangles per role in car space (x flipped), before centring
        var raw = new Dictionary<string, List<(Vector3 P, Vector2 Uv)[]>>();
        var rawMat = new Dictionary<string, List<int>>();
        foreach (var (role, node) in nodeOf)
        {
            var tris = raw[role] = [];
            var mats = rawMat[role] = [];
            var stack = new Stack<int>([node]);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                var nd = model.Nodes[n];
                if (nd.Mesh >= 0)
                    foreach (var prim in model.Meshes[nd.Mesh])
                    {
                        var m = world[n]!.Value;
                        for (int t = 0; t < prim.Indices.Length; t += 3)
                        {
                            var tri = new (Vector3, Vector2)[3];
                            for (int k = 0; k < 3; k++)
                            {
                                int vi = prim.Indices[t + (k == 0 ? 0 : 3 - k)];   // v0, v2, v1
                                var p = Vector3.Transform(prim.Positions[vi], m);
                                tri[k] = (new Vector3(-p.X, p.Y, p.Z), prim.Uv?[vi] ?? Vector2.Zero);
                            }
                            tris.Add(tri);
                            mats.Add(prim.Material);
                        }
                    }
                foreach (int c in nd.Children)
                    if (!roleOf.ContainsKey(c)) stack.Push(c);
            }
        }
        if (raw["body"].Count == 0) throw new InvalidDataException("glTF car: 'body' has no triangles");

        var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
        foreach (var tri in raw["body"]) foreach (var (p, _) in tri) { bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); }
        float wheelLow = float.MaxValue, top = bmax.Y;
        foreach (var w in WheelNodes)
        {
            if (raw[w].Count == 0) throw new InvalidDataException($"glTF car: '{w}' has no triangles");
            foreach (var tri in raw[w]) foreach (var (p, _) in tri) { wheelLow = Math.Min(wheelLow, p.Y); top = Math.Max(top, p.Y); }
        }
        float ground = groundY ?? -(top - wheelLow) / 2;
        var shift = new Vector3(-(bmin.X + bmax.X) / 2, ground - wheelLow, -(bmin.Z + bmax.Z) / 2);

        var wheelCentre = new Vector3[4];
        for (int w = 0; w < 4; w++)
        {
            var t = world[nodeOf[WheelNodes[w]]]!.Value.Translation;
            wheelCentre[w] = new Vector3(-t.X, t.Y, t.Z) + shift;
        }
        if (!(wheelCentre[0].X > wheelCentre[1].X && wheelCentre[2].X > wheelCentre[3].X))
            throw new InvalidDataException("glTF car: right wheels (wheel_fr, wheel_rr) must be on the car's right (glTF −X)");
        if (!(wheelCentre[0].Z > wheelCentre[2].Z && wheelCentre[1].Z > wheelCentre[3].Z))
            throw new InvalidDataException("glTF car: front wheels must be ahead (glTF +Z) of the rear ones");

        var textures = new Dictionary<string, Texture>();
        string TextureName(int material)
        {
            string name = material < 0 ? "gltf:default" : $"gltf:m{material}";
            if (!textures.ContainsKey(name)) textures[name] = MakeTexture(model, material, paint);
            return name;
        }

        CarMeshPart Part(string role, Vector3 origin, CarPartMotion motion, int wheel)
        {
            var tris = raw[role];
            var verts = new Vector3[tris.Count * 3];
            var polys = new CarMeshPoly[tris.Count];
            for (int t = 0; t < tris.Count; t++)
            {
                for (int k = 0; k < 3; k++) verts[t * 3 + k] = tris[t][k].P + shift - origin;
                int mat = rawMat[role][t];
                var gm = mat >= 0 ? model.Materials[mat] : null;
                polys[t] = new CarMeshPoly(t * 3, t * 3 + 1, t * 3 + 2, t * 3 + 2, TextureName(mat), tris[t][0].Uv, tris[t][1].Uv, tris[t][2].Uv, tris[t][2].Uv,
                    Transparent: gm != null && gm.AlphaMode != "OPAQUE", TwoSided: gm?.DoubleSided ?? false, Mirrored: false, AnimFrames: 0);
            }
            return new CarMeshPart(role, origin, verts, polys, motion, wheel);
        }

        var lod0 = new List<CarMeshPart> { Part("body", Vector3.Zero, CarPartMotion.Lean, -1) };
        for (int w = 0; w < 4; w++) lod0.Add(Part(WheelNodes[w], wheelCentre[w], CarPartMotion.Wheel, w));
        var lods = new IReadOnlyList<CarMeshPart>[3];
        lods[0] = lod0;
        lods[1] = nodeOf.ContainsKey("lod1") ? [Part("lod1", Vector3.Zero, CarPartMotion.None, -1)] : lod0;
        lods[2] = nodeOf.ContainsKey("lod2") ? [Part("lod2", Vector3.Zero, CarPartMotion.None, -1)] : lods[1];
        for (int l = 0; l < 3; l++)
        {
            if (l > 0 && ReferenceEquals(lods[l], lods[l - 1])) continue;
            int count = lods[l].Sum(p => p.Polys.Length);
            if (count > TriangleBudget[l]) throw new InvalidDataException($"glTF car: LOD{l} has {count} triangles, budget {TriangleBudget[l]}");
        }
        var axes = wheelCentre.Select(c => new Vector2(c.X, c.Z)).ToArray();
        return new GltfCar(new CarMesh(lods, axes, 0), textures);
    }

    /// <summary>Material texture: base colour image × baseColorFactor (factor converted linear → sRGB), or a 1×1 texture of the factor. Alpha &lt; 0.5
    /// marks a texel transparent for MASK / BLEND materials (the renderer has 1-bit alpha).</summary>
    static Texture MakeTexture(GltfModel model, int material, GltfPaint? paint)
    {
        var m = material >= 0 ? model.Materials[material] : new GltfMaterial("", Vector4.One, -1, "OPAQUE", false);
        static float Srgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        var f = new Vector4(Srgb(Math.Clamp(m.BaseColor.X, 0, 1)), Srgb(Math.Clamp(m.BaseColor.Y, 0, 1)), Srgb(Math.Clamp(m.BaseColor.Z, 0, 1)), m.BaseColor.W);
        bool alpha = m.AlphaMode != "OPAQUE";
        bool painted = paint != null && material >= 0 && paint.Applies(m.Name);
        if (painted && m.Image < 0) f = new Vector4(paint!.R / 255f, paint.G / 255f, paint.B / 255f, f.W);
        if (m.Image < 0)
            return new Texture(1, 1, [Pack(f.X * 255, f.Y * 255, f.Z * 255)], [alpha && f.W < 0.5f]);
        var img = model.Images[m.Image];
        var rgb = new uint[img.Width * img.Height];
        var tr = new bool[rgb.Length];
        float[]? paintMask = null;      // per texel: paint brightness ratio (null = multiply)
        float tr0 = 1, tg0 = 1, tb0 = 1;
        if (painted)
        {
            tr0 = paint!.R / 255f; tg0 = paint.G / 255f; tb0 = paint.B / 255f;
            paintMask = new float[rgb.Length];
            double sum = 0; int n = 0;
            for (int i = 0; i < rgb.Length; i++)
            {
                int r = img.Rgba[i * 4], g = img.Rgba[i * 4 + 1], b = img.Rgba[i * 4 + 2];
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx > 40 && (mx - mn) / (float)mx > 0.35f) { paintMask[i] = mx; sum += mx; n++; }
            }
            if (n < rgb.Length / 20) paintMask = null;                 // no paint texels: multiply
            else
            {
                float mean = (float)(sum / n);
                for (int i = 0; i < rgb.Length; i++) if (paintMask[i] > 0) paintMask[i] = paintMask[i] / mean;
            }
        }
        for (int i = 0; i < rgb.Length; i++)
        {
            if (painted && paintMask != null)
            {
                if (paintMask[i] > 0)
                {
                    rgb[i] = Pack(tr0 * paintMask[i] * 255, tg0 * paintMask[i] * 255, tb0 * paintMask[i] * 255);
                    tr[i] = alpha && img.Rgba[i * 4 + 3] * f.W < 127.5f;
                    continue;
                }
            }
            else if (painted) { rgb[i] = Pack(img.Rgba[i * 4] * tr0, img.Rgba[i * 4 + 1] * tg0, img.Rgba[i * 4 + 2] * tb0); tr[i] = alpha && img.Rgba[i * 4 + 3] * f.W < 127.5f; continue; }
            rgb[i] = Pack(img.Rgba[i * 4] * f.X, img.Rgba[i * 4 + 1] * f.Y, img.Rgba[i * 4 + 2] * f.Z);
            tr[i] = alpha && img.Rgba[i * 4 + 3] * f.W < 127.5f;
        }
        return new Texture(img.Width, img.Height, rgb, tr);
    }

    static uint Pack(float r, float g, float b) =>
        (uint)Math.Clamp((int)MathF.Round(r), 0, 255) << 16 | (uint)Math.Clamp((int)MathF.Round(g), 0, 255) << 8 | (uint)Math.Clamp((int)MathF.Round(b), 0, 255);
}
