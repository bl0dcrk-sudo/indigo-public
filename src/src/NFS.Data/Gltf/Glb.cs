using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace NFS.Data.Gltf;

/// <summary>Triangle list of one glTF primitive: positions in the space of its node, optional TEXCOORD_0, three indices per triangle.</summary>
public sealed record GltfPrimitive(Vector3[] Positions, Vector2[]? Uv, int[] Indices, int Material);

/// <summary>glTF material subset: base colour factor (linear RGBA as stored), base colour image (index into <see cref="GltfModel.Images"/>, −1 = none),
/// alpha mode and double-sidedness.</summary>
public sealed record GltfMaterial(string Name, Vector4 BaseColor, int Image, string AlphaMode, bool DoubleSided);

/// <summary>Decoded PNG image (RGBA rows top to bottom).</summary>
public sealed record GltfImage(int Width, int Height, byte[] Rgba);

/// <summary>Node: local transform as a System.Numerics row-vector matrix (v' = v × Local), mesh index (−1 = none), children.</summary>
public sealed record GltfNode(string Name, Matrix4x4 Local, int Mesh, int[] Children);

/// <summary>Reader of the binary glTF 2.0 subset of openspec modding-api-cars D6: one JSON chunk + one BIN chunk, triangle primitives (mode 4)
/// with POSITION (float VEC3), TEXCOORD_0 (float or normalised u8/u16 VEC2) and indices (u8/u16/u32), node TRS or matrix, materials with
/// baseColorFactor / baseColorTexture (PNG in a bufferView). Skins, animations, morph targets, required extensions, external or data URIs and
/// sparse accessors are rejected. Every error throws <see cref="InvalidDataException"/> with a message for the mod diagnostics.</summary>
public sealed class GltfModel
{
    public const int MaxFileBytes = 16 << 20;

    public IReadOnlyList<GltfNode> Nodes { get; }
    public IReadOnlyList<GltfPrimitive[]> Meshes { get; }
    public IReadOnlyList<GltfMaterial> Materials { get; }
    public IReadOnlyList<GltfImage> Images { get; }
    /// <summary>Root nodes of the default scene (all parentless nodes when the file has no scene).</summary>
    public IReadOnlyList<int> Roots { get; }

    GltfModel(GltfNode[] nodes, GltfPrimitive[][] meshes, GltfMaterial[] materials, GltfImage[] images, int[] roots)
    {
        Nodes = nodes; Meshes = meshes; Materials = materials; Images = images; Roots = roots;
    }

    /// <summary>World matrices (row-vector, v × M) of every node reachable from <see cref="Roots"/>; unreachable nodes get null.</summary>
    public Matrix4x4?[] WorldMatrices()
    {
        var world = new Matrix4x4?[Nodes.Count];
        var stack = new Stack<(int Node, Matrix4x4 Parent)>();
        foreach (int r in Roots) stack.Push((r, Matrix4x4.Identity));
        while (stack.Count > 0)
        {
            var (n, parent) = stack.Pop();
            if (world[n] != null) throw new InvalidDataException($"glTF: node {n} has more than one parent");
            var m = Nodes[n].Local * parent;
            world[n] = m;
            foreach (int c in Nodes[n].Children) stack.Push((c, m));
        }
        return world;
    }

    public static GltfModel Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException($"glTF: file not found: {path}");
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"glTF: {info.Length} B over the {MaxFileBytes} B limit");
        return Parse(File.ReadAllBytes(path));
    }

    public static GltfModel Parse(byte[] glb, int maxImageSide = 1024)
    {
        if (glb.Length > MaxFileBytes) throw new InvalidDataException($"glTF: {glb.Length} B over the {MaxFileBytes} B limit");
        if (glb.Length < 20) throw new InvalidDataException("glTF: truncated header");
        var span = glb.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x46546C67) throw new InvalidDataException("glTF: not a binary glTF (magic)");
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) != 2) throw new InvalidDataException("glTF: version 2 expected");
        long total = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        if (total > glb.Length) throw new InvalidDataException($"glTF: truncated file ({glb.Length} of {total} B)");
        int pos = 12;
        ReadOnlyMemory<byte> json = default, bin = default;
        bool haveJson = false, haveBin = false;
        while (pos + 8 <= total)
        {
            long len = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (pos + 8 + len > total) throw new InvalidDataException("glTF: truncated chunk");
            var data = glb.AsMemory(pos + 8, (int)len);
            if (type == 0x4E4F534A && !haveJson) { json = data; haveJson = true; }
            else if (type == 0x004E4942 && !haveBin) { bin = data; haveBin = true; }
            pos += 8 + (int)len;
        }
        if (!haveJson) throw new InvalidDataException("glTF: no JSON chunk");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException e) { throw new InvalidDataException("glTF: bad JSON: " + e.Message); }
        using (doc)
            return new Reader(doc.RootElement, bin, haveBin, maxImageSide).Read();
    }

    sealed class Reader(JsonElement root, ReadOnlyMemory<byte> bin, bool haveBin, int maxImageSide)
    {
        JsonElement[] _views = [], _accessors = [];

        static JsonElement[] Array(JsonElement o, string name) =>
            o.TryGetProperty(name, out var a) ? a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToArray()
                : throw new InvalidDataException($"glTF: '{name}' is not an array") : [];

        static int Int(JsonElement o, string name, int def = -1) =>
            o.TryGetProperty(name, out var v) ? v.TryGetInt32(out int i) ? i : throw new InvalidDataException($"glTF: '{name}' is not an integer") : def;

        static string Str(JsonElement o, string name, string def = "") =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

        static float[] Floats(JsonElement o, string name, int n)
        {
            if (!o.TryGetProperty(name, out var v)) return [];
            var r = v.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            if (r.Length != n) throw new InvalidDataException($"glTF: '{name}' must have {n} numbers");
            return r;
        }

        static int Index(int i, int count, string what) =>
            i >= 0 && i < count ? i : throw new InvalidDataException($"glTF: {what} index {i} out of range 0..{count - 1}");

        public GltfModel Read()
        {
            var ver = root.TryGetProperty("asset", out var asset) ? Str(asset, "version") : "";
            if (!ver.StartsWith("2.", StringComparison.Ordinal)) throw new InvalidDataException($"glTF: asset.version '{ver}' is not 2.x");
            foreach (var req in Array(root, "extensionsRequired"))
                throw new InvalidDataException($"glTF: required extension '{req.GetString()}' is not supported");
            if (Array(root, "skins").Length > 0) throw new InvalidDataException("glTF: skinning is not supported");
            if (Array(root, "animations").Length > 0) throw new InvalidDataException("glTF: animations are not supported");
            var buffers = Array(root, "buffers");
            if (buffers.Length > 1) throw new InvalidDataException("glTF: only one buffer (the BIN chunk) is supported");
            if (buffers.Length == 1)
            {
                if (buffers[0].TryGetProperty("uri", out _)) throw new InvalidDataException("glTF: external / data URI buffers are not supported");
                if (!haveBin) throw new InvalidDataException("glTF: buffer 0 without a BIN chunk");
                if (Int(buffers[0], "byteLength", 0) > bin.Length) throw new InvalidDataException("glTF: BIN chunk shorter than buffer 0");
            }
            _views = Array(root, "bufferViews");
            _accessors = Array(root, "accessors");

            var images = Array(root, "images").Select(ReadImage).ToArray();
            var textures = Array(root, "textures").Select(t => Index(Int(t, "source"), images.Length, "texture source")).ToArray();
            var materials = Array(root, "materials").Select(m =>
            {
                var color = Vector4.One; int image = -1;
                if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    var f = Floats(pbr, "baseColorFactor", 4);
                    if (f.Length == 4) color = new Vector4(f[0], f[1], f[2], f[3]);
                    if (pbr.TryGetProperty("baseColorTexture", out var bt))
                    {
                        if (Int(bt, "texCoord", 0) != 0) throw new InvalidDataException("glTF: baseColorTexture must use TEXCOORD_0");
                        image = textures[Index(Int(bt, "index"), textures.Length, "texture")];
                    }
                }
                bool doubleSided = m.TryGetProperty("doubleSided", out var ds) && ds.ValueKind == JsonValueKind.True;
                return new GltfMaterial(Str(m, "name"), color, image, Str(m, "alphaMode", "OPAQUE"), doubleSided);
            }).ToArray();

            var meshes = Array(root, "meshes").Select(m => Array(m, "primitives").Select(p => ReadPrimitive(p, materials.Length)).ToArray()).ToArray();

            var nodeEls = Array(root, "nodes");
            var nodes = nodeEls.Select((n, i) =>
            {
                if (n.TryGetProperty("skin", out _)) throw new InvalidDataException("glTF: skinning is not supported");
                if (n.TryGetProperty("weights", out _)) throw new InvalidDataException("glTF: morph targets are not supported");
                int mesh = Int(n, "mesh");
                if (mesh >= 0) Index(mesh, meshes.Length, "mesh");
                var children = Array(n, "children").Select(c => Index(c.GetInt32(), nodeEls.Length, "child node")).ToArray();
                return new GltfNode(Str(n, "name", $"node{i}"), LocalMatrix(n), mesh, children);
            }).ToArray();

            int[] roots;
            var scenes = Array(root, "scenes");
            if (scenes.Length > 0)
            {
                int s = Index(Int(root, "scene", 0), scenes.Length, "scene");
                roots = Array(scenes[s], "nodes").Select(e => Index(e.GetInt32(), nodes.Length, "scene node")).ToArray();
            }
            else
            {
                var isChild = new bool[nodes.Length];
                foreach (var n in nodes) foreach (int c in n.Children) isChild[c] = true;
                roots = Enumerable.Range(0, nodes.Length).Where(i => !isChild[i]).ToArray();
            }
            return new GltfModel(nodes, meshes, materials, images, roots);
        }

        static Matrix4x4 LocalMatrix(JsonElement n)
        {
            var m = Floats(n, "matrix", 16);
            if (m.Length == 16)   // column-major array = row-vector matrix in row order
                return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            var t = Floats(n, "translation", 3); var r = Floats(n, "rotation", 4); var s = Floats(n, "scale", 3);
            var scale = s.Length == 3 ? Matrix4x4.CreateScale(s[0], s[1], s[2]) : Matrix4x4.Identity;
            var rot = r.Length == 4 ? Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3]))) : Matrix4x4.Identity;
            var tr = t.Length == 3 ? Matrix4x4.CreateTranslation(t[0], t[1], t[2]) : Matrix4x4.Identity;
            return scale * rot * tr;
        }

        GltfPrimitive ReadPrimitive(JsonElement p, int materialCount)
        {
            int mode = Int(p, "mode", 4);
            if (mode != 4) throw new InvalidDataException($"glTF: primitive mode {mode} (only triangles, 4, are supported)");
            if (p.TryGetProperty("targets", out _)) throw new InvalidDataException("glTF: morph targets are not supported");
            if (!p.TryGetProperty("attributes", out var attrs)) throw new InvalidDataException("glTF: primitive without attributes");
            int posAcc = Int(attrs, "POSITION");
            if (posAcc < 0) throw new InvalidDataException("glTF: primitive without POSITION");
            var raw = ReadAccessor(posAcc, "VEC3", out _);
            var positions = new Vector3[raw.Length / 3];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = new Vector3((float)raw[i * 3], (float)raw[i * 3 + 1], (float)raw[i * 3 + 2]);
                if (!float.IsFinite(positions[i].X + positions[i].Y + positions[i].Z)) throw new InvalidDataException("glTF: non-finite POSITION");
            }
            Vector2[]? uv = null;
            int uvAcc = Int(attrs, "TEXCOORD_0");
            if (uvAcc >= 0)
            {
                var u = ReadAccessor(uvAcc, "VEC2", out _);
                if (u.Length / 2 != positions.Length) throw new InvalidDataException("glTF: TEXCOORD_0 count differs from POSITION");
                uv = new Vector2[positions.Length];
                for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2((float)u[i * 2], (float)u[i * 2 + 1]);
            }
            int[] indices;
            int idxAcc = Int(p, "indices");
            if (idxAcc >= 0)
            {
                var f = ReadAccessor(idxAcc, "SCALAR", out int comp);
                if (comp == 5126) throw new InvalidDataException("glTF: float indices");
                indices = new int[f.Length];
                for (int i = 0; i < f.Length; i++) indices[i] = Index((int)f[i], positions.Length, "vertex");
            }
            else indices = Enumerable.Range(0, positions.Length).ToArray();
            if (indices.Length % 3 != 0) throw new InvalidDataException("glTF: triangle index count not a multiple of 3");
            int material = Int(p, "material");
            if (material >= 0) Index(material, materialCount, "material");
            return new GltfPrimitive(positions, uv, indices, material);
        }

        /// <summary>Accessor values as doubles (exact for u32 indices), normalised integers mapped to 0..1.</summary>
        double[] ReadAccessor(int index, string type, out int componentType)
        {
            var a = _accessors[Index(index, _accessors.Length, "accessor")];
            if (a.TryGetProperty("sparse", out _)) throw new InvalidDataException("glTF: sparse accessors are not supported");
            if (Str(a, "type") != type) throw new InvalidDataException($"glTF: accessor {index} type '{Str(a, "type")}', expected {type}");
            componentType = Int(a, "componentType");
            int size = componentType switch { 5121 => 1, 5123 => 2, 5125 => 4, 5126 => 4,
                _ => throw new InvalidDataException($"glTF: accessor {index} component type {componentType} not supported") };
            bool normalized = a.TryGetProperty("normalized", out var nv) && nv.ValueKind == JsonValueKind.True;
            int comps = type switch { "SCALAR" => 1, "VEC2" => 2, _ => 3 };
            int count = Int(a, "count", 0);
            if (count < 0) throw new InvalidDataException($"glTF: accessor {index} negative count");
            var result = new double[(long)count * comps];
            int viewIndex = Int(a, "bufferView");
            if (viewIndex < 0) return result;   // all zeros (glTF 3.6.2.3)
            var view = _views[Index(viewIndex, _views.Length, "bufferView")];
            if (Int(view, "buffer", 0) != 0) throw new InvalidDataException("glTF: bufferView of an unknown buffer");
            long viewOff = Int(view, "byteOffset", 0), viewLen = Int(view, "byteLength", 0);
            int stride = Int(view, "byteStride", 0);
            if (stride == 0) stride = size * comps;
            long off = viewOff + Int(a, "byteOffset", 0);
            if (count > 0 && (viewOff < 0 || viewOff + viewLen > bin.Length || off < viewOff
                || off + (long)(count - 1) * stride + size * comps > viewOff + viewLen))
                throw new InvalidDataException($"glTF: accessor {index} outside its bufferView / the BIN chunk");
            var b = bin.Span;
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    var s = b[(int)(off + (long)i * stride + c * size)..];
                    result[i * comps + c] = componentType switch
                    {
                        5121 => normalized ? s[0] / 255.0 : s[0],
                        5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535.0 : BinaryPrimitives.ReadUInt16LittleEndian(s),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                        _ => BinaryPrimitives.ReadSingleLittleEndian(s),
                    };
                }
            return result;
        }

        GltfImage ReadImage(JsonElement img, int i)
        {
            if (img.TryGetProperty("uri", out _)) throw new InvalidDataException($"glTF: image {i} uses a URI (only PNG in a bufferView)");
            string mime = Str(img, "mimeType");
            if (mime != "image/png") throw new InvalidDataException($"glTF: image {i} '{mime}' is not image/png");
            var view = _views[Index(Int(img, "bufferView"), _views.Length, "bufferView")];
            long off = Int(view, "byteOffset", 0), len = Int(view, "byteLength", 0);
            if (off < 0 || len < 0 || off + len > bin.Length) throw new InvalidDataException($"glTF: image {i} outside the BIN chunk");
            var (w, h, rgba) = PngReader.Decode(bin.Span.Slice((int)off, (int)len), maxImageSide);
            return new GltfImage(w, h, rgba);
        }
    }

    /// <summary>Builds a .glb from a JSON document and a BIN payload (tests, tools): chunks padded to 4 B (JSON with spaces, BIN with zeros).</summary>
    public static byte[] Pack(string json, ReadOnlySpan<byte> bin)
    {
        var j = Encoding.UTF8.GetBytes(json);
        int jl = (j.Length + 3) & ~3, bl = (bin.Length + 3) & ~3;
        var o = new byte[12 + 8 + jl + (bin.Length > 0 ? 8 + bl : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(o, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), (uint)o.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(12), (uint)jl);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16), 0x4E4F534A);
        j.CopyTo(o, 20);
        o.AsSpan(20 + j.Length, jl - j.Length).Fill((byte)' ');
        if (bin.Length > 0)
        {
            int at = 20 + jl;
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(at), (uint)bl);
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(at + 4), 0x004E4942);
            bin.CopyTo(o.AsSpan(at + 8));
        }
        return o;
    }
}
