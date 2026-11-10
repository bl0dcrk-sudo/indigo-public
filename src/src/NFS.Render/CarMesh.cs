using System.Numerics;
using NFS.Data.Car;

namespace NFS.Render;

/// <summary>How a part moves with <see cref="CarAnim"/> (R3DCar.c): body parts lean about the body pivot, wheel parts steer (front) and wobble.</summary>
public enum CarPartMotion { None, Lean, Wheel }

/// <summary>Which animation frame a polygon's texture follows (name[1] + frame). [ref:2459]</summary>
public enum CarFrameSource { None, FrontRim, RearRim, BrakeLight }

/// <summary>One car polygon: corner indices into <see cref="CarMeshPart.Vertices"/> (triangle: V2 == V3), texture name and explicit
/// texture coordinates per corner as fractions of the texture (multiplied by its width / height when drawn).</summary>
public readonly record struct CarMeshPoly(int V0, int V1, int V2, int V3, string Texture, Vector2 Uv0, Vector2 Uv1, Vector2 Uv2, Vector2 Uv3,
    bool Transparent, bool TwoSided, bool Mirrored, int AnimFrames)
{
    public int Corner(int k) => k switch { 0 => V0, 1 => V1, 2 => V2, _ => V3 };
    public Vector2 Uv(int k) => k switch { 0 => Uv0, 1 => Uv1, 2 => Uv2, _ => Uv3 };
}

/// <summary>A part of a <see cref="CarMesh"/> LOD: vertices in metres relative to <see cref="Origin"/> (car-local node position in the rest pose).</summary>
public sealed record CarMeshPart(string Name, Vector3 Origin, Vector3[] Vertices, CarMeshPoly[] Polys, CarPartMotion Motion = CarPartMotion.None,
    int Wheel = -1, CarFrameSource Frames = CarFrameSource.None);

/// <summary>Render model shared by the original cars (<see cref="FromGeo"/>) and mod cars (glTF, openspec modding-api-cars D6). Car space: metres,
/// x right, y up, z forward, origin = CarObj.Pos (the collision box centre). <see cref="WheelAxes"/> = XZ of the wheel centres 0 FR, 1 FL, 2 RR, 3 RL
/// (steering axis), <see cref="PivotZ"/> = z of the body lean pivot.</summary>
public sealed class CarMesh
{
    public IReadOnlyList<CarMeshPart>[] Lods { get; }
    public Vector2[] WheelAxes { get; }
    public float PivotZ { get; }

    public CarMesh(IReadOnlyList<CarMeshPart>[] lods, Vector2[] wheelAxes, float pivotZ)
    {
        if (lods.Length != 3) throw new ArgumentException("3 LODs expected", nameof(lods));
        if (wheelAxes.Length != 4) throw new ArgumentException("4 wheels expected", nameof(wheelAxes));
        Lods = lods; WheelAxes = wheelAxes; PivotZ = pivotZ;
    }

    static readonly int[] BodyNodeParts = [11, 10, 8, 7, 6, 5, 4, 3, 2, 1, 0];
    static readonly Vector2[] CornerUv = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];

    /// <summary>The original <c>.geo</c>: LOD0 = parts 0–19, LOD1 = 20–23, LOD2 = 24–25 (one mesh part per .geo part, same order), every corner gets
    /// the texture corner (0,1), (1,1), (1,0), (0,0) of the whole FSH shape. <paramref name="rootParts"/> (traffic types ≥ 0x11): LOD0 parts are root
    /// nodes without a node position. Node positions: see <see cref="NodeOrigin"/>.</summary>
    public static CarMesh FromGeo(CarGeo geo, bool rootParts)
    {
        var parts = geo.Parts;
        Vector2 bodyXz = Vector2.Zero;
        float pivotZ = 0;   // body node pos.z = pos of the last body part added (0–8, 10, 11), y = 0 [ref:2460]
        foreach (int bi in BodyNodeParts)
            if (parts[bi].Polygons.Length > 0) { bodyXz = new Vector2(parts[bi].X, parts[bi].Z) / 65536f; pivotZ = parts[bi].Z / 65536f; break; }
        var wheels = new Vector2[4];
        for (int w = 0; w < 4; w++) wheels[w] = new Vector2(parts[13 + w * 2].X / 65536f, parts[13 + w * 2].Z / 65536f);

        CarMeshPart Part(int pi, int lod)
        {
            var part = parts[pi];
            var verts = new Vector3[part.NumVerts];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = new Vector3(part.Vertices[i * 3], part.Vertices[i * 3 + 1], part.Vertices[i * 3 + 2]) / 256f;
            var polys = new List<CarMeshPoly>(part.Polygons.Length);
            foreach (var p in part.Polygons)
            {
                if (p.V0 >= part.NumVerts || p.V1 >= part.NumVerts || p.V2 >= part.NumVerts || p.V3 >= part.NumVerts) continue;
                polys.Add(new CarMeshPoly(p.V0, p.V1, p.V2, p.V3, p.Texture, CornerUv[0], CornerUv[1], CornerUv[2], CornerUv[3],
                    p.IsTransparent, p.IsTwoSided, p.IsMirrored, p.AnimFrames));
            }
            bool node = lod == 0 && !rootParts;
            var motion = lod != 0 ? CarPartMotion.None : pi is >= 12 and <= 19 ? CarPartMotion.Wheel : pi < 12 ? CarPartMotion.Lean : CarPartMotion.None;
            var frames = pi switch { 12 or 14 => CarFrameSource.FrontRim, 16 or 18 => CarFrameSource.RearRim, 11 => CarFrameSource.BrakeLight, _ => CarFrameSource.None };
            return new CarMeshPart($"geo{pi}", node ? NodeOrigin(geo, pi, bodyXz) : Vector3.Zero, verts, polys.ToArray(), motion,
                pi is >= 12 and <= 19 ? (pi - 12) / 2 : -1, lod == 0 ? frames : CarFrameSource.None);
        }

        var lods = new IReadOnlyList<CarMeshPart>[3];
        lods[0] = Enumerable.Range(0, 20).Select(pi => Part(pi, 0)).ToArray();
        lods[1] = Enumerable.Range(20, 4).Select(pi => Part(pi, 1)).ToArray();
        lods[2] = Enumerable.Range(24, 2).Select(pi => Part(pi, 2)).ToArray();
        return new CarMesh(lods, wheels, pivotZ);
    }

    /// <summary>Car-local position (metres, relative to CarObj.Pos = the centre of the collision box) of the node that holds LOD0 part <paramref name="part"/>
    /// in the rest pose; the vertices of a part are raw, relative to this node. [ref:2461]
    /// The node of a part copies the pos of the last part added to it, then: [ref:2462]
    /// body parts (0–8, 10, 11) get Y = 0, so the raw body, whose lowest point is about −HalfHeight, sits [ref:2463]
    /// on the road plane of the sim; part 9 and every wheel node get [ref:2464]
    /// Y − parts[1].Y; a wheel node (rim + tyre) holds the pos of the tyre part. LOD1/2 and the traffic types (root nodes) have no offset: their
    /// world position is CarObj.Pos itself. The .geo pos is relative to the tyre plane; the original shifts it by parts[1].Y (≈ the half box height). [ref:2465]</summary>
    static Vector3 NodeOrigin(CarGeo geo, int part, Vector2 bodyXz)
    {
        var parts = geo.Parts;
        float shiftY = parts[1].Y / 65536f;
        if (part is >= 12 and <= 19)
        {
            var tyre = parts[12 + (part - 12) / 2 * 2 + 1];
            return new Vector3(tyre.X / 65536f, tyre.Y / 65536f - shiftY, tyre.Z / 65536f);
        }
        if (part == 9) return new Vector3(parts[9].X / 65536f, parts[9].Y / 65536f - shiftY, parts[9].Z / 65536f);
        return new Vector3(bodyXz.X, 0, bodyXz.Y);
    }
}
