using NFS.Core;
using NFS.Core.Constants;

namespace NFS.Render;

/// <summary>
/// When a car makes effect particles (presentation of the spawn calls inside). It only READS the car; [ref:2577]
/// the particles live in <see cref="ParticleSystem"/>. Call <see cref="BeforePost"/> just before and <see cref="AfterPost"/> just
/// after <c>CarPost.Update</c> of a car that the simulation runs (the "particles (sfx)" section): [ref:2578]
/// <list type="bullet">
/// <item>(&lt; 100 m): the landing counter +0x90 &gt; 0 and speed &gt; 20 m/s → type 6 dust (type 5 sparks on surfaces 1 and 4) behind the car. [ref:2579]</item>
/// <item>Crash smoke while +0x58C is set; the edge hit (0x40004) → type 1 smoke below 24 m/s, otherwise [ref:2580]
///   the surface table (4 = sparks) or type 6 in the air; a car–car hit above 10 m/s → sparks (type 4) and dust (6); [ref:2581]
///   a plane hit (0x3xxxx) → crash smoke.</item>
/// <item>(&lt; 90 m): hard braking or wheel spin → type 1 smoke [ref:2582]
///   at the wheels; sliding on a surface without marks → its spray type (7 grass, 8 gravel, 9 snow, 6 dust) [ref:2583]
///   below the spray speed limit (16.16 m/s); on its side → sparks. [ref:2584]</item>
/// </list>
/// </summary>
public static class ParticleEmitter
{
    /// <summary>spray type per surface (surfaces 0..15; 0 = none). [ref:2585]</summary>
    static ReadOnlySpan<byte> SprayType => [0, 0, 7, 8, 0, 6, 0, 0, 0, 9, 0, 0, 0, 6, 0, 9];
    /// <summary>the spray only appears below this speed (16.16 m/s). [ref:2586]</summary>
    static ReadOnlySpan<int> SpraySpeed => [0, 0, 0x140000, 0x140000, 0, 0xC80000, 1, 0, 0, 0x140000, 0, 0, 0, 0xC80000, 0, 0x140000];
    /// <summary>effect of a fast edge hit per surface of ImpactType &amp; 0xFF (types 10/11 do not exist in the original). [ref:2587]</summary>
    static ReadOnlySpan<byte> EdgeHitType => [0, 4, 4, 6, 4, 8, 0, 6, 4, 4, 4, 10, 4, 11, 10, 10];

    public static int SprayTypeOf(int surface) => (uint)surface < 16 ? SprayType[surface] : 0;
    public static int SpraySpeedOf(int surface) => (uint)surface < 16 ? SpraySpeed[surface] : 0;
    public static int EdgeHitTypeOf(int surface) => (uint)surface < 16 ? EdgeHitType[surface] : 0;

    /// <summary>The simulation skips for cars on rails and inactive cars. [ref:2588]</summary>
    static bool Runs(CarObj c) => c.Rail == 0 && c.Active != 0;

    /// <summary>State seen before <c>CarPost.Update</c> (Impact, +0x90 and +0x58C are consumed/changed by it). Returns whether the car was
    /// crashing (+0x58C) – pass it to <see cref="AfterPost"/>.</summary>
    public static bool BeforePost(CarObj c, ParticleSystem ps, int tick)
    {
        bool wasCrashing = c.Crashing != 0;
        if (!Runs(c)) return wasCrashing;
        int id = c.Id;
        // cars < 100 m from a player [ref:2589]
        if (c.DistToPlayer < 0x640000 && c.LandingSmoke != 0 && c.SpeedApprox > 0x140000)
        {
            // pos − fwd·HalfLength (DriveFrame forward +0x1A0)
            int k = c.HalfLength / 256;
            var f = c.DriveFrame.Row2;
            var pos = new Vec3(c.Pos.X - unchecked(f.X * k) / 256, c.Pos.Y - unchecked(f.Y * k) / 256, c.Pos.Z - unchecked(f.Z * k) / 256);
            ps.SpawnThrottled(id, 0, pos, c.SurfaceType is 1 or 4 ? 5 : 6, c.Vel, tick);
        }
        if (c.Crashing != 0) ps.CrashSmoke(c.Pos, tick);
        if (c.Impact != 0) ImpactEffects(c, ps, tick);
        return wasCrashing;
    }

    /// <summary>After <c>CarPost.Update</c>: the crash smoke of the impact step itself (+0x58C just became set) and the skid effects.</summary>
    public static void AfterPost(CarObj c, ParticleSystem ps, int tick, bool wasCrashing)
    {
        if (!Runs(c)) return;
        if (!wasCrashing && c.Crashing != 0) ps.CrashSmoke(c.Pos, tick);   // (+0x9C) in the crash branch [ref:2591]
        if (c.DistToPlayer < 0x5A0000) SkidEffects(c, ps, tick);
    }

    static void ImpactEffects(CarObj c, ParticleSystem ps, int tick)
    {
        uint type = (uint)c.ImpactType & 0xEFFF0000;
        int id = c.Id, speed = c.SpeedApprox;
        if (type == 0x40000)
        {
            if (speed <= 0x180000)
            {
                if (speed > 0xF0000 || c.Impact > 0xF0000) ps.SpawnThrottled(id, 0, c.LastHit, 1, c.Vel, tick);
            }
            else if (c.HeightAboveRoad < 0x9999) ps.SpawnThrottled(id, 0, c.LastHit, EdgeHitTypeOf(c.ImpactType & 0xFF), c.Vel, tick);
            else ps.SpawnThrottled(id, 0, c.LastHit, 6, c.Vel, tick);
        }
        if (c.Impact <= 0xA0000 || Math.Max(c.ShakePhase0, c.ShakePhase1) >= 0x1F) return;
        if (type == 0x30000) ps.CrashSmoke(c.LastHit, tick);
        else if (type == 0x50000)
        {
            bool metal = (c.Flags & CarFlags.Static) == 0 && (c.ImpactOther is not { } o || (o.Flags & CarFlags.Static) == 0);
            ps.SpawnThrottled(id, 0, c.LastHit, metal ? 4 : 6, c.Vel, tick);
            ps.SpawnThrottled(id, 1, c.LastHit, 6, c.Vel, tick);
        }
    }

    static Vec3 Mul8(Vec3 v, int k) => new(unchecked(v.X * k) / 256, unchecked(v.Y * k) / 256, unchecked(v.Z * k) / 256);

    /// <summary>The same gating as <c>SkidMarks.CarSkidMarks</c>. [ref:2592]</summary>
    static void SkidEffects(CarObj c, ParticleSystem ps, int tick)
    {
        int id = c.Id;
        if (c.UpDotN < 0x3333)
        {
            // on its side / roof: sparks scraping along (type 5) instead of tyre effects
            if (c.HeightAboveRoad >= 0x3333 || c.SpeedApprox <= 0x20000) return;
            if (c.SpeedApprox > 0x40000 && (c.SurfaceByte & 0x3F) == 1) ps.SpawnThrottled(id, 2, c.Pos, 5, c.Vel, tick);
            return;
        }
        int surf = c.SurfaceType;
        int front, frontEmit, rear;
        if (c.DistToPlayer < 0x3C0000 && c.HeightAboveRoad < 0x6666 && c.CrashSlice == 0)
        {
            front = c.Skid324 - 0x4CCC;
            frontEmit = front - Math.Abs(c.LocalVel.Z / 8);
            rear = c.Skid328 / 4 - 0x4CCC;
        }
        else front = frontEmit = rear = 0;
        if ((surf == 0xD || surf == 5) && c.SpeedApprox > 0x2C9999) rear = 0x10000;
        if (front <= 0 && rear <= 0) return;
        if (rear < 0) rear = 0;
        frontEmit = Math.Max(frontEmit, 0);

        var side = Mul8(c.Orient.Row0, c.HalfWidth * 3 / 4 / 256);
        var fwdR = Mul8(c.Orient.Row2, c.HalfLength * 3 / 4 / 256);
        var fwdF = Mul8(c.Orient.Row2, c.HalfLength * 2 / 4 / 256);
        var pos = new Vec3(c.Pos.X, c.PrevRoadHeight, c.Pos.Z);
        bool marks = (uint)surf < 16 && RaceConstants.SkidMarkSurfaces[surf] != 0;
        bool braking = c.BrakeRamped > 0xF5 && c.SpeedApprox > 0xA0000;
        int spray = SprayTypeOf(surf);

        if (front > 0 && frontEmit != 0)
        {
            var l = Add(pos, side, fwdF); var r = Sub(pos, side, fwdF);
            if (marks)
            {
                if (braking) { ps.SpawnThrottled(id, 0, l, 1, c.Vel, tick); ps.SpawnThrottled(id, 1, r, 1, c.Vel, tick); }
            }
            else if (c.SpeedApprox < SpraySpeedOf(surf))
            {
                ps.SpawnThrottled(id, 0, l, spray, c.Vel, tick); ps.SpawnThrottled(id, 1, r, spray, c.Vel, tick);
            }
        }
        if (rear > 0)
        {
            var l = new Vec3(pos.X + side.X - fwdR.X, pos.Y + side.Y - fwdR.Y, pos.Z + side.Z - fwdR.Z);
            var r = new Vec3(pos.X - side.X - fwdR.X, pos.Y - side.Y - fwdR.Y, pos.Z - side.Z - fwdR.Z);
            if (!marks)
            {
                if (c.SpeedApprox < SpraySpeedOf(surf)) { ps.SpawnThrottled(id, 2, l, spray, c.Vel, tick); ps.SpawnThrottled(id, 3, r, spray, c.Vel, tick); }
            }
            else if ((Math.Abs(c.LocalVel.Z) < 0x140000 && c.Rpm > 4000) || braking)
            {
                ps.SpawnThrottled(id, 2, l, 1, c.Vel, tick); ps.SpawnThrottled(id, 3, r, 1, c.Vel, tick);
            }
        }
    }

    static Vec3 Add(Vec3 p, Vec3 a, Vec3 b) => new(p.X + a.X + b.X, p.Y + a.Y + b.Y, p.Z + a.Z + b.Z);
    static Vec3 Sub(Vec3 p, Vec3 a, Vec3 b) => new(p.X - a.X + b.X, p.Y - a.Y + b.Y, p.Z - a.Z + b.Z);
}
