using System.Numerics;
using NFS.Data.Track;

namespace NFS.Render;

/// <summary>
/// View camera: position + orthonormal basis (right, up, forward) in world metres, and the software projection of
/// fx = (w/2)·ctg(hHalf), fy = −(h/2)·ctg(vHalf), cx = w/2, cy = h/2 [ref:2440]
/// sx = x·fx/z + cx, sy = y·fy/z + cy. Main view: hHalf = 45°, vHalf = ½·45°·13/16·2 = 36°.
/// </summary>
public sealed class Camera
{
    public const double NearZ = 1.0 / 256; // p.z>>8 < 1 → clipped [ref:2441]

    public Vector3 Position { get; set; }
    public Vector3 Right { get; private set; } = Vector3.UnitX;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public Vector3 Forward { get; private set; } = Vector3.UnitZ;

    public int Width { get; }
    public int Height { get; }
    public double Fx { get; }
    public double Fy { get; }
    public double Cx => Width / 2.0 + _centreXOffset;
    public double Cy => Height / 2.0 + _centreYOffset;
    readonly double _centreYOffset;
    readonly double _centreXOffset;

    /// <summary>Width of the 4:3 frame at this vertical scale (= <see cref="Width"/> except for a panorama view, see <see cref="PanoramaBase"/>).
    /// Resolution-dependent thresholds (quad subdivision) follow this, not the wider buffer.</summary>
    public int LayoutWidth { get; }
    /// <summary>Slope |x|/z of the left / right view edge for cheap cone culling (1 = the original 90° cone; larger for a panorama).</summary>
    public double CullSlope { get; }

    /// <summary>Hor+ panorama: a frame wider than 4:3 keeps the focal length (vertical angle, pixel scale) of the [ref:3434]
    /// 4:3 frame of the same height, so the horizontal angle grows with the aspect ratio. Returns the width of that 4:3 frame, or null
    /// for a normal (4:3 or narrower) frame.</summary>
    public static double? PanoramaBase(int width, int height) => width * 3 > height * 4 ? height * 4.0 / 3 : null;

    /// <param name="baseWidth">Width the horizontal focal length is derived from (Hor+, see <see cref="PanoramaBase"/>); null = <paramref name="width"/>.</param>
    public Camera(int width = 640, int height = 480, double hHalfDeg = 45, double vHalfDeg = 36, double centreYOffset = 0,
        double centreXOffset = 0, double? focalX = null, double? focalY = null, double? baseWidth = null)
    {
        Width = width; Height = height;
        _centreYOffset = centreYOffset;
        _centreXOffset = centreXOffset;
        Fx = focalX ?? (baseWidth ?? width) / 2.0 / Math.Tan(hHalfDeg * Math.PI / 180);
        LayoutWidth = baseWidth.HasValue ? (int)Math.Round(baseWidth.Value) : width;
        CullSlope = baseWidth.HasValue ? Math.Max(1.0, (width / 2.0 + Math.Abs(centreXOffset)) / Fx) : 1.0;
        Fy = focalY ?? -(height / 2.0) / Math.Tan(vHalfDeg * Math.PI / 180);
    }

    /// <summary>Orients the camera along <paramref name="forward"/> with <paramref name="up"/> as the up hint.</summary>
    public void LookAlong(Vector3 forward, Vector3 up)
    {
        Forward = Vector3.Normalize(forward);
        Right = Vector3.Normalize(Vector3.Cross(up, Forward));
        Up = Vector3.Cross(Forward, Right);
    }

    /// <summary>World → camera space (x right, y up, z forward).</summary>
    public Vector3 ToView(Vector3 world)
    {
        var d = world - Position;
        return new(Vector3.Dot(d, Right), Vector3.Dot(d, Up), Vector3.Dot(d, Forward));
    }

    /// <summary>Camera space → screen pixels (no clipping; caller ensures z ≥ NearZ).</summary>
    public (double X, double Y) Project(Vector3 v) => (v.X * Fx / v.Z + Cx, v.Y * Fy / v.Z + Cy);

    /// <summary>Slice (COL XBID 15) centre in metres.</summary>
    public static Vector3 SliceCentre(TrackSlice s) => new(s.X / 65536f, s.Y / 65536f, s.Z / 65536f);

    public static Vector3 Vec(sbyte[] v) => new(v[0] / 126f, v[1] / 126f, v[2] / 126f);

    /// <summary>Camera standing on a slice, <paramref name="height"/> m above its centre, looking down the track.</summary>
    public static Camera FromSlice(TrackSlice s, float height = 1.5f, float back = 0f, int width = 640, int height2 = 480)
    {
        var c = new Camera(width, height2, baseWidth: PanoramaBase(width, height2));
        var up = Vec(s.Up);
        var fw = Vec(s.Forward);
        c.LookAlong(fw, up);
        c.Position = SliceCentre(s) + c.Up * height - c.Forward * back;
        return c;
    }
}
