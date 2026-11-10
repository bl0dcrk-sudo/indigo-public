using System.Numerics;
using NFS.Data;
using NFS.Data.Car;

namespace NFS.Render;

/// <summary>
/// Car model for the software renderer: <c>&lt;NAME&gt;.geo</c> from CARDATA.VIV + textures <c>CARMODEL/PC/&lt;NAME&gt;.QFS</c>
/// Parts: LOD0 = parts 0–19 (body, moving part, lights, wheels), LOD1 = 20–23, LOD2 = 24–25. [ref:2466]
/// Drawn from <see cref="Mesh"/> (<see cref="CarMesh.FromGeo"/>: polygon vertex i gets texture corner (0,1),(1,1),(1,0),(0,0) of the whole FSH shape, V = FSH rows).
/// </summary>
public sealed class CarModel
{
    public string Name { get; }
    /// <summary>The original .geo; a glTF mod model (<see cref="GltfCar"/>) has none.</summary>
    public CarGeo Geo => _geo ?? throw new InvalidOperationException($"car model {Name} comes from glTF and has no .geo");
    readonly CarGeo? _geo;
    /// <summary>Render model (openspec modding-api-cars D6): the renderer, animation and LOD read only this.</summary>
    public CarMesh Mesh { get; }
    readonly int? _carType;
    internal bool RootParts => _carType >= 17;
    readonly Dictionary<string, Texture?> _tex = new();

    /// <summary>Paint colour for the green key palette entries (r &lt; 0x56, g &gt; 0x7F, b &lt; 0x56).</summary>
    /// <param name="carType">Car type of the race config record (index); the geo holds the per-type [ref:2467]
    /// wheel relocation, so the renderer must know it. Null = no relocation. [ref:2468]</param>
    /// <param name="wheelShift">Front wheel shift (CarTraits.WheelShift); null = the vanilla rule for <paramref name="carType"/>.</param>
    /// <param name="rearWheelShift">Rear wheel shift (CarTraits.RearWheelShift); null = the vanilla rule for <paramref name="carType"/>.</param>
    public CarModel(string name, string? gameDir = null, uint paintRgb = 0xC01010, int? carType = null, int? paintIndex = null,
        bool? wheelShift = null, bool? rearWheelShift = null)
    {
        Name = name;
        _carType = carType;
        var viv = Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", gameDir);
        _geo = CarGeo.Parse(viv.Get(name.ToLowerInvariant() + ".geo"));
        if (carType is { } ct)
            _geo.ApplyWheelShift(wheelShift ?? CarGeo.FrontWheelShift(ct), rearWheelShift ?? CarGeo.RearWheelShift(ct));
        Mesh = CarMesh.FromGeo(_geo, RootParts);
        string qfs = $"GAMEDATA/CARMODEL/PC/{name.ToUpperInvariant()}.QFS";
        if (!GameFiles.Exists(qfs, gameDir)) return;
        var fsh = Fsh.Load(qfs, gameDir);
        var pal = fsh.GlobalPalette();
        if (pal != null)
        {
            if (paintIndex is >= 0 and <= MaxPaint) CarPaint.RecolourPalette(pal, CarPaint.Hsv(paintIndex.Value));
            else Recolor(pal, paintRgb);
        }
        foreach (var e in fsh.Entries)
            if (!_tex.ContainsKey(e.Name)) _tex[e.Name] = e.Main.IsBitmap ? Texture.FromFsh(fsh, e, pal) : null;
    }

    /// <summary>Mod car model from glTF (openspec modding-api-cars D6): mesh and textures of <paramref name="car"/>, no car type (LOD = distance LOD).</summary>
    public CarModel(string name, GltfCar car)
    {
        Name = name;
        Mesh = car.Mesh;
        foreach (var (k, t) in car.Textures) _tex[k] = t;
    }

    /// <summary>Highest colour index of the HSV table (clears 0x59 entries). [ref:2469]</summary>
    public const int MaxPaint = CarPaint.TableEntries - 1;

    /// <summary>Palette variants of a traffic car (types 0x11 &lt; t &lt; 0x28, not 0x24) are [ref:2470]
    /// the configured colour c, ((c + t)·2) % 0x49 + 0xF and ((t/2 + c/2)·2 + 1) % 0x49 + 0xF; other cars have the one colour.
    /// The car shows variant +0x634 (CarObj.TrafficSkin). [ref:2471]</summary>
    public static int[] PaintVariants(int carType, int colour) => PaintVariants(carType, colour, VanillaPaintVariants(carType));

    /// <summary>Vanilla number of palette variants of car type <paramref name="carType"/> (3 for 0x11..0x27 except 0x24, else 1); single source for CarTraits.TrafficPaintVariants.</summary>
    public static int VanillaPaintVariants(int carType) => carType is > 0x10 and < 0x28 && carType != 0x24 ? 3 : 1;

    /// <summary>The palette variants of a car with <paramref name="variants"/> variants (CarTraits.TrafficPaintVariants): 1 = the configured colour only.</summary>
    public static int[] PaintVariants(int carType, int colour, int variants) =>
        variants > 1
            ? [colour, ((colour + carType) * 2) % 0x49 + 0xF, ((carType / 2 + colour / 2) * 2 + 1) % 0x49 + 0xF]
            : [colour];

    /// <summary>Simplified: key entry i = g − 0x80 → paint·i/127 (no HSV table [ref:2472]
    /// no highlight branch). [ref:2473]</summary>
    static void Recolor(byte[] pal, uint paint)
    {
        for (int k = 0; k < 256; k++)
        {
            int r = pal[k * 4], g = pal[k * 4 + 1], b = pal[k * 4 + 2];
            if (r >= 0x56 || g <= 0x7F || b >= 0x56) continue;
            int i = g - 0x80;
            pal[k * 4] = (byte)((paint >> 16 & 255) * i / 127);
            pal[k * 4 + 1] = (byte)((paint >> 8 & 255) * i / 127);
            pal[k * 4 + 2] = (byte)((paint & 255) * i / 127);
        }
    }

    public Texture? TextureFor(string name) => _tex.GetValueOrDefault(name);

    readonly Dictionary<int, (Vector3 Min, Vector3 Max)?> _bounds = new();

    /// <summary>Car-local bounding box (metres, x right, y up, z forward) of the vertices of the parts of <paramref name="lod"/> in the rest pose
    /// (wheels included; the origin is CarObj.Pos, the box centre, so Min.Y is about −HalfHeight, see <see cref="CarMesh.FromGeo"/>); null when the LOD has no vertices. Cached.</summary>
    public (Vector3 Min, Vector3 Max)? LocalBounds(int lod)
    {
        if (_bounds.TryGetValue(lod, out var cached)) return cached;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var part in Mesh.Lods[MeshLod(lod)])
            foreach (var p in part.Polys)
                for (int k = 0; k < 4; k++)
                {
                    var v = part.Origin + part.Vertices[p.Corner(k)];
                    min = Vector3.Min(min, v); max = Vector3.Max(max, v); any = true;
                }
        return _bounds[lod] = any ? (min, max) : null;
    }

    // Smackable objects reuse the [ref:2474]
    // unanimated node (parts 20–23) at every LOD; the traffic models (types 0x11..0x27) have no body parts 0–19 at all
    // (their .geo only holds parts 20–25) – the LOD0 node +0x568 is not built and car+0x568 stays 0x15b's node (0x15a = 0x15b).
    public IEnumerable<int> PartsForLod(int lod) => LodParts(_carType >= 40 || (_carType >= 17 && lod == 0) ? 1 : lod);

    /// <summary>The <see cref="CarMesh.Lods"/> entry drawn at <paramref name="lod"/> (the parts of <see cref="PartsForLod"/>).</summary>
    internal int MeshLod(int lod) => _carType >= 40 || (_carType >= 17 && lod == 0) ? 1 : Math.Clamp(lod, 0, 2);

    readonly Dictionary<(string, int), string> _frameNames = new();

    /// <summary>Name of frame <paramref name="frame"/> (&gt; 0) of the animated texture <paramref name="name"/> (name[1] + frame), the base name when the model has no such texture. Cached.</summary>
    internal string FrameTexture(string name, int frame)
    {
        if (_frameNames.TryGetValue((name, frame), out var cached)) return cached;
        string alt = string.Concat(name.AsSpan(0, 1), ((char)(name[1] + frame)).ToString(), name.AsSpan(2));
        return _frameNames[(name, frame)] = TextureFor(alt) != null ? alt : name;
    }

    public static IEnumerable<int> LodParts(int lod) => lod switch
    {
        0 => Enumerable.Range(0, 20),
        1 => Enumerable.Range(20, 4),
        _ => Enumerable.Range(24, 2),
    };
}

/// <summary>Road plane under a car (world metres): a point on it and its unit normal. Lets the renderer put the shadow on the road, not under the car's belly.</summary>
public readonly record struct CarGround(Vector3 Point, Vector3 Normal);

/// <summary>Car placed in the world: position (metres) and basis (car x right, y up, z forward). <paramref name="Anim"/>
/// (optional) animates body lean, front wheel steering, rim blur frames, wheel wobble and the brake light (R3DCar.c).</summary>
public sealed record CarInstance(CarModel Model, Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward, int Lod = 0, CarAnim? Anim = null)
{
    // Mutable so that the race scene can update its instances in place every frame instead of allocating new ones.
    public CarModel Model { get; set; } = Model;
    public Vector3 Position { get; set; } = Position;
    public Vector3 Right { get; set; } = Right;
    public Vector3 Up { get; set; } = Up;
    public Vector3 Forward { get; set; } = Forward;
    /// <summary>Road plane under the car (null = unknown: the shadow then lies on the tyre plane in the car's frame).</summary>
    public CarGround? Ground { get; set; }

    /// <summary>Replaces model and pose in place (no allocation).</summary>
    public void SetPose(CarModel model, Vector3 position, Vector3 right, Vector3 up, Vector3 forward)
    {
        Model = model; Position = position; Right = right; Up = up; Forward = forward;
    }

    /// <summary>Car-local metres → world.</summary>
    public Vector3 ToWorld(Vector3 local) => Position + Right * local.X + Up * local.Y + Forward * local.Z;

    /// <summary>Angle of the car relative to the camera, 16-bit [ref:2475]
    /// <c>(d·right, d·forward)</c> with d = car − camera; 0 when seen from straight behind. [ref:2476]</summary>
    public int ViewAzimuth(Vector3 cameraPosition)
    {
        var d = Position - cameraPosition;
        return (int)Math.Round(Math.Atan2(Vector3.Dot(d, Right), Vector3.Dot(d, Forward)) * 0x8000 / Math.PI);
    }

    const double AngleToRad = 2 * Math.PI / 0x10000;

    /// <summary>All polygons of the LOD as world quads with texture coordinates (texels) and flags. With
    /// <see cref="Anim"/> the body parts lean (rotZ·rotX about the ground point below the car centre), the front wheel parts
    /// (0xC–0xF) turn about the vertical axis of the tyre by steer × scale(view azimuth of <paramref name="cameraPosition"/>;
    /// no camera = azimuth 0) and all wheels wobble; animated polygons use frame textures (name[1] + frame).</summary>
    public IEnumerable<(Vector3[] P, (double U, double V)[] Uv, Texture? Tex, bool Transparent, bool TwoSided, bool Mirrored)> Polys(Vector3? cameraPosition = null)
    {
        var buf = new CarPolyBuffer();
        AppendPolys(buf, cameraPosition);
        for (int i = 0; i < buf.Count; i++)
        {
            var m = buf.Meta[i];
            yield return ([buf.P[i * 4], buf.P[i * 4 + 1], buf.P[i * 4 + 2], buf.P[i * 4 + 3]],
                [buf.Texel(i, 0), buf.Texel(i, 1), buf.Texel(i, 2), buf.Texel(i, 3)], m.Tex, m.Transparent, m.TwoSided, m.Mirrored);
        }
    }

    /// <summary>Appends the world quads of <see cref="Polys"/> to <paramref name="buf"/> without allocating (the renderer reuses one buffer per frame).</summary>
    internal void AppendPolys(CarPolyBuffer buf, Vector3? cameraPosition = null)
    {
        double steerAngle = 0;
        if (Anim is { } a && Lod == 0)
        {
            int az = cameraPosition is { } cp ? ViewAzimuth(cp) : 0;
            steerAngle = CarAnim.SteerScale(a.FullAngleSteer, a.Steer, az) * a.Steer * AngleToRad;
        }
        var mesh = Model.Mesh;
        float pivotZ = mesh.PivotZ;   // body node pos.z = pos of the last body part added (0–8, 10, 11), y = 0 [ref:2477]
        var parts = mesh.Lods[Model.MeshLod(Lod)];
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts[pi];
            // node position of the part (CarMesh.FromGeo: shift the .geo pos down by parts[1].Y; [ref:2478]
            // LOD1/2 and LOD0 of types >= 0x11 are root nodes at CarObj.Pos, without any part pos) [ref:2479]
            var pos = part.Origin;
            int mode = 0;   // 0 = none, 1 = wheel (steer + wobble), 2 = body lean
            float cx = 0, cz = 0, dy = 0, sw = 0, cw = 1, sr = 0, cr = 1, sp = 0, cp2 = 1;
            if (Anim is { } an && Lod == 0)
            {
                if (part.Motion == CarPartMotion.Wheel)
                {
                    int wheel = part.Wheel;                                     // 0 FR, 1 FL, 2 RR, 3 RL
                    cx = mesh.WheelAxes[wheel].X; cz = mesh.WheelAxes[wheel].Y; dy = an.WheelY[wheel] / 65536f;
                    double ang = wheel < 2 ? steerAngle : 0;
                    sw = (float)Math.Sin(ang); cw = (float)Math.Cos(ang);
                    mode = 1;
                }
                else if (part.Motion == CarPartMotion.Lean && (an.Roll != 0 || an.Pitch != 0))
                {
                    sr = (float)Math.Sin(an.Roll * AngleToRad); cr = (float)Math.Cos(an.Roll * AngleToRad);
                    sp = (float)Math.Sin(an.Pitch * AngleToRad); cp2 = (float)Math.Cos(an.Pitch * AngleToRad);
                    mode = 2;
                }
            }
            var verts = part.Vertices;
            foreach (var p in part.Polys)
            {
                var tex = Model.TextureFor(FrameName(p, part.Frames));
                ref var meta = ref buf.Next(out int slot);
                meta.Tex = tex; meta.W = tex?.Width ?? 1; meta.H = tex?.Height ?? 1;
                meta.Transparent = p.Transparent; meta.TwoSided = p.TwoSided; meta.Mirrored = p.Mirrored;
                for (int k = 0; k < 4; k++)
                {
                    var v = pos + verts[p.Corner(k)];
                    if (mode == 1)
                    {
                        float x = v.X - cx, z = v.Z - cz;                       // [c,0,−s; 0,1,0; s,0,c], row vectors [ref:2480]
                        v = new Vector3(cx + x * cw + z * sw, v.Y + dy, cz - x * sw + z * cw);
                    }
                    else if (mode == 2)
                    {
                        float x = v.X, y = v.Y, z = v.Z - pivotZ;
                        float x1 = x * cr - y * sr, y1 = x * sr + y * cr;       // [c,s,0; −s,c,0; 0,0,1] [ref:2481]
                        float y2 = y1 * cp2 - z * sp, z2 = y1 * sp + z * cp2;   // [1,0,0; 0,c,s; 0,−s,c] [ref:2482]
                        v = new Vector3(x1, y2, z2 + pivotZ);
                    }
                    buf.P[slot * 4 + k] = ToWorld(v);
                    buf.Uv[slot * 4 + k] = p.Uv(k);
                }
            }
        }
    }

    /// <summary>Texture of an animated polygon: makes one ref per frame with [ref:2483]
    /// name[1] + k; the frame comes from (rims: wheel frames, part 11 lights: brake light). [ref:2484]</summary>
    string FrameName(in CarMeshPoly p, CarFrameSource source)
    {
        if (Anim is not { } a || Lod != 0 || p.AnimFrames == 0) return p.Texture;
        int frame = source switch
        {
            CarFrameSource.FrontRim => a.FrontRimFrame,
            CarFrameSource.RearRim => a.RearRimFrame,
            CarFrameSource.BrakeLight => a.BrakeLight ? 1 : 0,
            _ => 0,
        };
        frame = Math.Min(frame, p.AnimFrames - 1);
        if (frame <= 0 || p.Texture.Length < 2) return p.Texture;
        return Model.FrameTexture(p.Texture, frame);
    }
}

/// <summary>Flat, reusable list of car quads (4 world corners each) with the per-polygon texture data; filled by <see cref="CarInstance.AppendPolys"/>.</summary>
internal sealed class CarPolyBuffer
{
    public struct Info { public Texture? Tex; public double W, H; public bool Transparent, TwoSided, Mirrored; }
    public Vector3[] P = new Vector3[512 * 4];
    /// <summary>Texture coordinates of the corners as fractions of the texture (<see cref="CarMeshPoly"/>); texels = fraction × W / H.</summary>
    public Vector2[] Uv = new Vector2[512 * 4];
    public Info[] Meta = new Info[512];
    public int Count;

    public void Clear() => Count = 0;

    /// <summary>Texture coordinates (texels) of corner <paramref name="corner"/> of quad <paramref name="index"/>.</summary>
    public (double U, double V) Texel(int index, int corner)
    {
        var uv = Uv[index * 4 + corner];
        return (uv.X * Meta[index].W, uv.Y * Meta[index].H);
    }

    /// <summary>Reserves the next quad: its index (its corners are <c>P[4·index .. 4·index+3]</c>) and a reference to its cleared info slot.</summary>
    public ref Info Next(out int index)
    {
        if (Count == Meta.Length) { Array.Resize(ref Meta, Count * 2); Array.Resize(ref P, Count * 8); Array.Resize(ref Uv, Count * 8); }
        index = Count;
        ref var m = ref Meta[Count++];
        m = default;
        return ref m;
    }
}
