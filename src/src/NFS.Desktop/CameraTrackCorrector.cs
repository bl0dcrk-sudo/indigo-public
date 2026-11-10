using System.Numerics;
using NFS.Core;
using NFS.Data.Track;

namespace NFS.Desktop;

/// <summary>
/// Correction of the view position against the track, 1:1 with the integer code of the original: runs [ref:1054]
/// for every view whose +0x30DC flag is set (cockpit, hood, both chase views, finish and reset orbit) [ref:1055]
/// and when the selected mode is neither 0 nor 1. Presentation only: the retained camera state [ref:1056]
/// (the smoothed offset) is not changed, the correction applies to the position handed to the renderer.
/// </summary>
public sealed class CameraTrackCorrector(TrackLocator locator)
{
    const int Half = 0x8000, WallZone = 0x28000, Shift = 0x38000, Rise = 0x30000, Margin = 0x60001;
    readonly TrackPos _tp = new() { Slice = -1 };

    /// <summary>Slice of the camera position from the last <see cref="Correct"/> (cam+0x60 = cam+0x54 TrackPos slice); −1 before the first.</summary>
    public int LocatorSlice => _tp.Slice;

    /// <summary>with +0x30DC clear (TV): update only the retained locator, without moving the station. [ref:1057]</summary>
    public void Locate(Vector3 position) => locator.Update(ToFixed(position), _tp, true);

    public static Vec3 ToFixed(Vector3 v) => new(F(v.X), F(v.Y), F(v.Z));
    public static Vector3 ToWorld(Vec3 v) => new(v.X / 65536f, v.Y / 65536f, v.Z / 65536f);
    static int F(float x) => (int)MathF.Round(x * 65536f);

    /// <summary><paramref name="pos"/> is corrected in place. <paramref name="clamp"/> = mode not in {0, 1}. [ref:1058]
    /// Returns true when it moved the position (it then sets the look-at flag for the finish camera). [ref:1059]</summary>
    public bool Correct(ref Vector3 pos, Vector3 carPos, int carSlice, bool clamp)
    {
        var p = ToFixed(pos);
        var car = ToFixed(carPos);
        bool moved = ChasePos(ref p, carSlice);
        locator.Update(p, _tp, true);                    // (pos, cam+0x54, 1) [ref:1060]
        if (clamp) ClampToTrack(ref p, car);
        pos = ToWorld(p);
        return moved;
    }

    static Vec3 Unit(sbyte[] v) => new(v[0] << 9, v[1] << 9, v[2] << 9);

    static Vec3 Cross(Vec3 a, Vec3 b) => new(Fx.Mul(a.Y, b.Z) - Fx.Mul(a.Z, b.Y), Fx.Mul(a.Z, b.X) - Fx.Mul(a.X, b.Z),
        Fx.Mul(a.X, b.Y) - Fx.Mul(a.Y, b.X));

    /// <summary>Within 6 m of the road edges of the slice, a camera in the 2.5 m zone next to a wall [ref:1061]
    /// (slice nibble +0x1E, left = high, right = low) is pulled in by a smooth curve and lifted, in the frame of the slice.</summary>
    bool ChasePos(ref Vec3 pos, int carSlice)
    {
        var slices = locator.Geometry.Slices;
        _tp.Slice = carSlice;                            // cam+= car+0x14 [ref:1062]
        locator.UpdateSlice(pos, _tp);                   // [ref:1063]
        var sl = slices[_tp.Slice];
        int right = sl.RightEdge * 0x100, left = sl.LeftEdge * 0x100;
        var r = Unit(sl.Right);
        var d = new Vec3(pos.X - sl.X, pos.Y - sl.Y, pos.Z - sl.Z);
        int lat = Fx.Mul(d.X, r.X) + Fx.Mul(d.Y, r.Y) + Fx.Mul(d.Z, r.Z);
        if (!(lat - right < Margin && -left - lat < Margin)) return false;
        int rightWall = sl.Surroundings & 0xF, leftWall = sl.Surroundings >> 4;
        bool leftCase;
        if (rightWall == 0 || lat + WallZone <= right)
        {
            if (leftWall == 0 || !(WallZone - lat > left)) return false;
            leftCase = true;
        }
        else leftCase = false;
        // Orthonormal frame of the slice: forward normalised, up = fwd × right normalised, right = up × fwd.
        var f = FxMath.Normalized(Unit(sl.Forward));
        var u = FxMath.Normalized(Cross(f, r));
        r = Cross(u, f);
        var m = new Mat3(r.X, r.Y, r.Z, u.X, u.Y, u.Z, f.X, f.Y, f.Z);
        var local = new Vec3(Fx.Mul(d.X, r.X) + Fx.Mul(d.Y, r.Y) + Fx.Mul(d.Z, r.Z),
            Fx.Mul(d.X, u.X) + Fx.Mul(d.Y, u.Y) + Fx.Mul(d.Z, u.Z),
            Fx.Mul(d.X, f.X) + Fx.Mul(d.Y, f.Y) + Fx.Mul(d.Z, f.Z));
        int edge = leftCase ? left : right, lx = local.X;
        bool narrow = false;
        if (edge < Shift)
        {
            edge += Shift;
            lx += leftCase ? -Shift : Shift;
            narrow = true;
        }
        int dirX = -lx;
        int gap = (leftCase ? dirX : lx) + Shift - edge;
        if (gap <= 0x10000) return false;
        int q = Fx.Div(0x10000, gap);
        int den = leftCase ? dirX : lx;
        if (den == 0) return false;                      // the original divides by zero here
        int s = Fx.Div(den - ((edge - 0x18000) - q), den);
        int nx = lx + Fx.Mul(s, dirX);
        int ny = local.Y + (int)(((long)s * Rise) >> 16);
        if (narrow) nx += leftCase ? Shift : -Shift;
        var o = Mat3.MulRow(new Vec3(nx, ny, local.Z), m);
        pos = new Vec3(o.X + sl.X, o.Y + sl.Y, o.Z + sl.Z);
        return true;
    }

    /// <summary>y ≥ plane height under the camera + 1 m; on an off-road polygon (surface flags &amp; 0x3F = 0) [ref:1064]
    /// the camera is bisected (10 steps, until closer than 0x290) towards the car + 3 m.</summary>
    void ClampToTrack(ref Vec3 pos, Vec3 car)
    {
        var slices = locator.Geometry.Slices;
        var sl = slices[_tp.Slice];
        Vec3 n; Vec3 point;
        if (_tp.RoadPoly == null || _tp.VRoad is not short[] vr)
        {
            n = Unit(sl.Up);
            point = _tp.RoadPoly == null ? new Vec3(sl.X, sl.Y, sl.Z) : _tp.Corners[0];
        }
        else
        {
            n = new Vec3(vr[0] * 2, vr[1] * 2, vr[2] * 2);
            point = _tp.Corners[0];
        }
        int h = Newton.PlaneHeightAt(pos, n, point);
        if (pos.Y < h + 0x10000) pos.Y = h + 0x10000;
        if ((TrackLocator.SurfaceFlags(_tp) & 0x3F) != 0) return;
        var a = new Vec3(car.X, car.Y + 3 * 0x10000, car.Z);
        var b = pos;
        for (int i = 0; ; i++)
        {
            if (i > 9) break;
            if (RaceOptionEffects.DistApprox3(a, b) < 0x290) break;
            var mid = new Vec3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
            locator.Update(mid, _tp, true);
            if ((TrackLocator.SurfaceFlags(_tp) & 0x3F) == 0) b = mid; else a = mid;
        }
        pos = b;
    }
}
