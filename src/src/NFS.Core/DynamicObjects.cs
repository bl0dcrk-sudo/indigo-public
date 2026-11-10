using NFS.Data.Track;

namespace NFS.Core;

/// <summary>
/// Track objects of XBID 11 that cars can hit (signs, barriers...): the object part of the track data, [ref:0396]
/// and collision. The knocked-over state ( [ref:0397]
/// byte 0xFF = intact, reset at race start) is kept per object id here; the CAN animation [ref:0398]
/// started by the first hit is visual only and not modelled – the state leaves 0xFF whether [ref:0399]
/// or not a free animation slot exists.
/// </summary>
public sealed class DynamicObjects(TrackGeometry geometry)
{
    private readonly HashSet<ushort> _hit = [];
    private readonly Dictionary<ushort, KnockedObject> _knocked = [];

    /// <summary>Clock of the sim step in progress; set by the race loop before [ref:0400]
    /// <see cref="Collide"/>. Only recorded for the renderer, never read by the collision logic.</summary>
    public int Tick { get; set; }

    /// <summary>Visual record of a knocked-over object (per object id): when and how it was hit. The renderer reads it
    /// (type 4: CAN anim, then lying flat); it does not feed back into the simulation. [ref:0401]</summary>
    public IReadOnlyDictionary<ushort, KnockedObject> Knocked => _knocked;

    /// <summary>Number of objects knocked over so far.</summary>
    public int HitCount => _hit.Count;

    /// <summary>max(|dx|,|dz|) + min/4. [ref:0402]</summary>
    public static int DistXz(Vec3 p, Vec3 q)
    {
        int dx = Math.Abs(p.X - q.X), dz = Math.Abs(p.Z - q.Z);
        return dx > dz ? dx + (dz >> 2) : dz + (dx >> 2);
    }

    /// <summary>The object disc (point + radius r) overlaps the car's XZ rectangle [ref:0403]
    /// (|d·right| ≤ half width + r, |d·fwd| ≤ half length + r, d = point − pos with y = 0).</summary>
    public static bool BoxTest(CarObj c, Vec3 pt, int r)
    {
        var d = new Vec3(pt.X - c.Pos.X, 0, pt.Z - c.Pos.Z);
        if (Math.Abs(Vec3.Dot(d, c.Orient.Row0)) > c.HalfWidth + r) return false;
        return Math.Abs(Vec3.Dot(d, c.Orient.Row2)) <= c.HalfLength + r;
    }

    /// <summary>−1 = the car knocks the object over now (first hit of a type-2 object at [ref:0404]
    /// speed ≥ 11 m/s for human cars, any speed for computer cars), 1 = still intact (solid), 0 = already knocked over.</summary>
    public int Hit(CarObj c, in TrackPoint p)
    {
        int speed = (Math.Abs(c.Vel.X) + Math.Abs(c.Vel.Z)) >> 16;
        int min = (c.Flags & CarFlags.NotLocal) == 0 ? 11 : 0;
        bool intact = !_hit.Contains(p.ObjectId);
        if (p.Type != 2 || !intact || speed < min) return intact ? 1 : 0;
        _hit.Add(p.ObjectId);
        // tail: CAN 2 (speed < 25 m/s) or 3, hit angle = high byte of atan2(vx>>8, vz>>8) [ref:0405]
        _knocked[p.ObjectId] = new KnockedObject(Tick, speed < 0x19 ? 2 : 3, (sbyte)(FxMath.Atan2_16(c.Vel.X >> 8, c.Vel.Z >> 8) >> 8));
        return -1;
    }

    /// <summary>The XBID 11 loop (only for cars with surface bit 0x40 and an active collision state). [ref:0406]</summary>
    public void Collide(CarObj c)
    {
        int blk = c.Trk.Block;
        if (blk < 0 || blk >= geometry.NumBlocks) return;
        var pts = geometry.DynPoints[blk];
        for (int i = 0; i < pts.Length; i++)
        {
            var p = pts[i];
            int s = (short)p.Unknown_0x0C, r = s * 0x100;
            var pt = new Vec3(p.X, p.Y, p.Z);
            if (DistXz(pt, c.Pos) >= c.Radius + r || c.HeightAboveRoad >= s * 0x200) continue;
            if (!BoxTest(c, pt, r)) continue;
            int h = Hit(c, p);
            if (h < 0)
            {
                c.ImpactType = 0x60005; c.ImpactOther = null; c.Impact = 0xF0000;
                c.LastHit = c.Pos;
            }
            else if (h > 0)
            {
                var n = new Vec3(c.Pos.X - p.X, 0, c.Pos.Z - p.Z);
                FxMath.NormalizeBig(ref n);
                int k = r / 256;
                var q = new Vec3(p.X + n.X * k / 256, p.Y + n.Y * k / 256, p.Z + n.Z * k / 256);   // 32-bit product, truncating
                PlaneCollision.CollPlane(c, ref n, q);
                if (c.Impact != 0)
                {
                    c.ImpactType = 0x60005; c.Impact /= 2;
                    c.LastHit = c.Pos;
                }
            }
        }
    }
}

/// <summary>A knocked-over XBID 11 object: tick of the first hit, CAN file index (2 slow / 3 fast, TRxx0N.CAN) [ref:0407]
/// the hit direction byte (from the car velocity &gt;&gt; 8). [ref:0408]</summary>
public readonly record struct KnockedObject(int HitTick, int CanIndex, sbyte Angle);
