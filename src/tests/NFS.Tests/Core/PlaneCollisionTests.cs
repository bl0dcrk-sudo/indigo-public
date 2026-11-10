using NFS.Core;

namespace NFS.Tests.Core;

public class PlaneCollisionTests
{
    [Fact]
    public void LenSqScaled_Small()
    {
        Assert.Equal(9 * Fx.One, PlaneCollision.LenSqScaled(new Vec3(Fx.One, 2 * Fx.One, -2 * Fx.One)));
        Assert.Equal(0x30000000, PlaneCollision.LenSqScaled(new Vec3(0x400000, 0x400000, -0x400000)));
    }

    [Fact]
    public void ClampAngVel_1_8()
    {
        var c = new CarObj { AngVel = new(0x30000, -0x30000, 0x100) };
        PlaneCollision.ClampAngVel(c);
        Assert.Equal(new Vec3(0x1CCCC, -0x1CCCC, 0x100), c.AngVel);
    }

    private static CarObj Box(int y, int vy) => new()
    {
        Pos = new(0, y, 0), Vel = new(0, vy, 0x40000), Orient = Mat3.Identity,
        HalfWidth = 0xE000, HalfHeight = 0x8000, HalfLength = 0x20000,
        InvMass = Fx.Div(Fx.One, 10 * Fx.One), InvInertia = Fx.Div(Fx.One, 15 * Fx.One), SurfaceType = 5,
    };

    [Fact]
    public void NoContact_OnlyAngVelRoundTrip()
    {
        var c = Box(2 * Fx.One, -0x20000);
        var n = new Vec3(0, Fx.One, 0);
        PlaneCollision.CollPlane(c, ref n, Vec3.Zero);
        Assert.Equal(new Vec3(0, 2 * Fx.One, 0), c.Pos);
        Assert.Equal(new Vec3(0, -0x20000, 0x40000), c.Vel);
        Assert.Equal(0, c.Impact);

        var z = Vec3.Zero;                                   // zero normal → (0,1,0), written back
        PlaneCollision.CollPlane(c, ref z, Vec3.Zero);
        Assert.Equal(new Vec3(0, Fx.One, 0), z);
    }

    [Fact]
    public void Penetration_PushOutAndBounce()
    {
        var c = Box(0x4CCD, -0x20000);                      // 0.2 m into the ground, falling 2 m/s
        var n = new Vec3(0, Fx.One, 0);
        PlaneCollision.CollPlane(c, ref n, Vec3.Zero);
        Assert.Equal(0x4CCD + 0x1999, c.Pos.Y);              // half the penetration
        Assert.Equal(0x20000 * 4, c.Impact);                 // |vn|·4 (n.y ≥ 0.7)
        Assert.Equal(0x30005, c.ImpactType);                 // surface | 0x30000
        Assert.True(c.Vel.Y > -0x20000 && c.Vel.Y <= 0x20000, $"vy {c.Vel.Y:X}");
        Assert.True(c.Vel.Z < 0x40000, "friction slows the sliding corner");
        Assert.InRange(c.AngVel.X, -0x1CCCC, 0x1CCCC);
    }

    [Fact]
    public void Impulse_NoFrictionWithoutNormalImpulse()
    {
        var c = Box(0, 0x10000);                             // moving away from the plane: j ≤ 0
        var before = c.Vel;
        PlaneCollision.Impulse(c, new Vec3(0, -0x8000, 0), c.Vel, new Vec3(0, Fx.One, 0));
        Assert.Equal(before, c.Vel);
        Assert.Equal(0x10000 * 4, c.Impact);
    }
}
