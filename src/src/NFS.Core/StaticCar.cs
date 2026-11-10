namespace NFS.Core;

/// <summary>
/// Parked cars (class 0x40, records of SMnn.DAT) have their own task set. [ref:0594]
/// Their task runs at prio 0x1E [ref:0595]
/// (the 0x28 and 0x32 priorities, see <see cref="TaskPrio"/>). [ref:0596]
/// </summary>
public static class StaticCar
{
    /// <summary>Off rails, only the local velocity +0x2B0..+0x2B8 is computed: M·v. [ref:0597]</summary>
    public static void Drive(CarObj c)
    {
        if (c.Rail != 0) return;
        c.LocalVel = new Vec3(Vec3.Dot(c.Vel, c.Orient.Row0), Vec3.Dot(c.Vel, c.Orient.Row1), Vec3.Dot(c.Vel, c.Orient.Row2));
    }

    /// <summary>
    /// Off rails, v and ω are damped (on the ground ×0xE6/256, in the air with +0x15C &lt; 0.5 [ref:0598]
    /// ×0xF8/256; every product truncates towards zero); an impact (+0x160) above simtune[0] launches the car
    /// and arms the return counter +0x578 = 0x1C0. The counter runs down once per tick [ref:0599]
    /// at 0 it stops the car and puts it on the road plane. A car at rest off the road [ref:0600]
    /// (surface 0/0xE) or crashed (+0x17C) re-arms the counter.
    /// </summary>
    public static void Post(CarObj c, ReadOnlySpan<int> simTune, int arcadeMode, int raceTick, Tables.GameTables tables)
    {
        if (c.Rail != 0) return;
        if (c.AirTicks == 0)
        {
            c.Vel = Damp(c.Vel, 0xE6);
            c.AngVel = Damp(c.AngVel, 0xE6);
        }
        else if (c.HeightAboveRoad < 0x8000)
        {
            c.Vel = Damp(c.Vel, 0xF8);
            c.AngVel = Damp(c.AngVel, 0xF8);
        }
        bool counting = false;
        if (simTune[CarPost.StCrashImpact] < c.Impact)
        {
            CarPost.Launch(c, arcadeMode, raceTick, tables);
            c.AutoResetCounter = 0x1C0;
            counting = true;
        }
        else if (c.AutoResetCounter > 0) counting = true;
        if (counting && --c.AutoResetCounter < 1)
        {
            Settle(c);
            c.AutoResetCounter = 0;
        }
        else if (c.AutoResetCounter > 0) return;
        if (c.SurfaceType != 0 && c.SurfaceType != 0xE && c.CrashSlice == 0) return;
        c.AutoResetCounter = 0x1C0;
    }

    /// <summary>v = ω = 0, y = road plane height at (x, z) minus the height of the lowest box corner. [ref:0601]</summary>
    public static void Settle(CarObj c)
    {
        c.Vel = default; c.AngVel = default;
        c.Pos.Y = Newton.PlaneHeightAt(c.Pos, c.RoadFrame.Row1, c.RoadPoint);
        int h = Newton.HeightAboveRoad(c, c.RoadFrame.Row1, c.RoadPoint);
        c.HeightAboveRoad = 0;
        c.Pos.Y -= h;
    }

    private static Vec3 Damp(Vec3 v, int k) => new(v.X * k / 256, v.Y * k / 256, v.Z * k / 256);
}
