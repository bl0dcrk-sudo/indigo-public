using System.Numerics;

namespace NFS.Render;

/// <summary>
/// Soft light of the cars (Enhanced): a fixed sun plus a sky/ground ambient give every polygon corner an intensity that [ref:3435]
/// multiplies the texture colour (Gouraud). The corner normal is the area-weighted average of the face normals of all polygons sharing that corner
/// (welded by position, across parts) whose normal is within <see cref="SmoothAngleDegrees"/> of this polygon's normal – so curved panels shade smoothly
/// while real creases (roof edge, window frame, wheel arch) stay sharp. Faces are oriented towards the viewer, which is the only visible side
/// (one-sided polygons are culled otherwise, two-sided ones show both). The original has no lighting at all: Enhanced only.
/// </summary>
public sealed class CarLighting
{
    /// <summary>Unit vector from the car towards the sun (world space, y up).</summary>
    public static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.35f, 0.85f, 0.4f));
    /// <summary>Ambient light that does not depend on the direction.</summary>
    public const float Ambient = 0.68f;
    /// <summary>Extra ambient from the sky for up-facing normals (a faint blue-ish sky vs dark ground).</summary>
    public const float Hemisphere = 0.18f;
    /// <summary>Strength of the direct sun term.</summary>
    public const float Diffuse = 0.42f;

    /// <summary>Normals further apart than this angle are not averaged (a crease).</summary>
    public float SmoothAngleDegrees = 40f;

    /// <summary>Intensity (1 = the texel colour unchanged) of a surface with unit normal <paramref name="n"/>.</summary>
    public static float Intensity(Vector3 n)
    {
        float lambert = MathF.Max(0f, Vector3.Dot(n, Sun));
        return Ambient + Hemisphere * (n.Y * 0.5f + 0.5f) + Diffuse * lambert;
    }

    Vector3[] _area = new Vector3[0], _normal = new Vector3[0];
    int[] _vertex = new int[0], _next = new int[0], _head = new int[0];
    readonly Dictionary<long, int> _weld = new();

    /// <summary>Intensity of the 4 corners of every polygon (<paramref name="intensity"/>[4·i + c]; a triangle repeats the corner 2 in 3). A polygon of zero area
    /// gets 1. <paramref name="quads"/>[i] = 4 world positions (a triangle has [2] == [3]); <paramref name="viewer"/> = the camera position.</summary>
    public void Compute(IReadOnlyList<Vector3[]> quads, Vector3 viewer, float[] intensity)
    {
        var flat = new Vector3[quads.Count * 4];
        for (int i = 0; i < quads.Count; i++) for (int k = 0; k < 4; k++) flat[i * 4 + k] = quads[i][k];
        Compute(flat, quads.Count, viewer, intensity);
    }

    /// <summary><see cref="Compute(IReadOnlyList{Vector3[]}, Vector3, float[])"/> for <paramref name="n"/> quads stored flat (<paramref name="corners"/>[4·i + c]); allocation free once warm.</summary>
    public void Compute(ReadOnlySpan<Vector3> corners, int n, Vector3 viewer, float[] intensity)
    {
        if (_area.Length < n) { _area = new Vector3[n]; _normal = new Vector3[n]; _next = new int[n * 4]; _vertex = new int[n * 4]; }
        _weld.Clear();
        int vertices = 0;
        for (int i = 0; i < n; i++)
        {
            var q = corners.Slice(i * 4, 4);
            var a = Vector3.Cross(q[2] - q[0], q[3] - q[1]);
            float len = a.Length();
            Vector3 c = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            if (len > 1e-9f)
            {
                if (Vector3.Dot(a, viewer - c) < 0) a = -a;
                _area[i] = a; _normal[i] = a / len;
            }
            else { _area[i] = Vector3.Zero; _normal[i] = Vector3.Zero; }
            for (int k = 0; k < 4; k++)
            {
                long key = Key(q[k]);
                if (!_weld.TryGetValue(key, out int id)) _weld[key] = id = vertices++;
                _vertex[i * 4 + k] = id;
            }
        }
        if (_head.Length < vertices) _head = new int[vertices];
        Array.Fill(_head, -1, 0, vertices);
        for (int i = 0; i < n; i++)
        {
            if (_area[i] == Vector3.Zero) continue;
            for (int k = 0; k < 4; k++)
            {
                if (k == 3 && _vertex[i * 4 + 3] == _vertex[i * 4 + 2]) continue;   // triangle: the corner is already linked
                int e = i * 4 + k, v = _vertex[e];
                _next[e] = _head[v]; _head[v] = e;
            }
        }
        float cosLimit = MathF.Cos(SmoothAngleDegrees * MathF.PI / 180f);
        for (int i = 0; i < n; i++)
        {
            if (_area[i] == Vector3.Zero) { for (int k = 0; k < 4; k++) intensity[i * 4 + k] = 1f; continue; }
            for (int k = 0; k < 4; k++)
            {
                var sum = Vector3.Zero;
                for (int e = _head[_vertex[i * 4 + k]]; e >= 0; e = _next[e])
                {
                    int f = e >> 2;
                    if (Vector3.Dot(_normal[f], _normal[i]) >= cosLimit) sum += _area[f];
                }
                float l = sum.Length();
                intensity[i * 4 + k] = Intensity(l > 1e-12f ? sum / l : _normal[i]);
            }
        }
    }

    /// <summary>Position quantised to 1/32 m (3 × 21 bit) – corners closer than that are one vertex.</summary>
    static long Key(Vector3 p)
    {
        const int mask = 0x1FFFFF;
        long x = (long)MathF.Round(p.X * 32) & mask, y = (long)MathF.Round(p.Y * 32) & mask, z = (long)MathF.Round(p.Z * 32) & mask;
        return x << 42 | y << 21 | z;
    }
}
