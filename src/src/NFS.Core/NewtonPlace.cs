using NFS.Data.Track;

namespace NFS.Core;

/// <summary>placement of a car on a slice (grid, crash/manual reset). [ref:0508]</summary>
public static class NewtonPlace
{
    /// <summary>Resets the track position to the given slice. [ref:0509]</summary>
    public static void TrkPosInit(TrackPos t, int slice)
    {
        t.Xbid6Slice = null; t.RoadPoly = null; t.Xbid5Entry = null; t.VRoad = null;
        t.SliceChanged = 0; t.Unk6E = 0;
        t.State = 1;
        t.Column = -1;
        t.Slice = slice;
    }

    /// <summary>
    /// Places the car on a slice: road frame +0x118 from the slice (s8 ≪ 9, each row [ref:0510]
    /// normalized), <c>Pos = centre + Mᵀ·local</c> (unflipped frame, every product a rounded FX). [ref:0511]
    /// Then calls <paramref name="trackUpdate"/> and updates the road frame. [ref:0512]
    /// orientation and drive frame = road frame (right/forward negated when <paramref name="reverse"/>), +0x154 =
    /// slice y, the car lowered by its height above the road (+0x15C = 0), surface +0x180/+0x184 (rails: 1). [ref:0513]
    /// </summary>
    public static void PlaceOnSlice(CarObj c, TrackSlice[] slices, int slice, Vec3 local, bool reverse, Action<CarObj> trackUpdate)
    {
        TrkPosInit(c.Trk, slice);
        var sl = slices[slice];
        c.Progress = (ushort)slice;
        Vec3 V(sbyte[] v) => FxMath.Normalized(new Vec3(v[0] << 9, v[1] << 9, v[2] << 9));
        var m = new Mat3();
        m.Row0 = V(sl.Right); m.Row1 = V(sl.Up); m.Row2 = V(sl.Forward);
        c.RoadFrame = m;
        c.Pos = new Vec3(
            Fx.Mul(local.X, m.M00) + Fx.Mul(local.Y, m.M10) + Fx.Mul(local.Z, m.M20) + sl.X,
            Fx.Mul(local.X, m.M01) + Fx.Mul(local.Y, m.M11) + Fx.Mul(local.Z, m.M21) + sl.Y,
            sl.Z + Fx.Mul(local.X, m.M02) + Fx.Mul(local.Y, m.M12) + Fx.Mul(local.Z, m.M22));
        trackUpdate(c);
        TrackRoadQuery.UpdateRoadFrame(c, slices);
        var r = c.RoadFrame;
        var o = r;
        if (reverse) { o.Row0 = -r.Row0; o.Row2 = -r.Row2; }
        c.Orient = o;
        c.DriveFrame = o;
        c.PrevRoadHeight = sl.Y;
        int h = Newton.HeightAboveRoad(c, c.RoadFrame.Row1, c.RoadPoint);
        c.HeightAboveRoad = 0;
        c.Pos.Y -= h;
        if (c.Rail == 0)
        {
            int b = TrackLocator.SurfaceFlags(c.Trk);
            c.SurfaceByte = (byte)b;
        }
        else c.SurfaceByte = 1;
        c.SurfaceType = c.SurfaceByte & 0x3F;
    }
}
