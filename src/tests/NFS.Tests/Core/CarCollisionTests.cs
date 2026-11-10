using NFS.Core;
using NFS.Data.Track;

namespace NFS.Tests.Core;

public class CarCollisionTests
{
    private static readonly TrackSlice[] Wide =
        [new(0, 0, 0, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0x2000, 0x2000, 0, 0, 0, 0, 0, 0)];

    private static CarObj Car(int z, int vz) => new()
    {
        Pos = new(0, 0x8000, z), Vel = new(0, 0, vz), Orient = Mat3.Identity, DriveFrame = Mat3.Identity,
        HalfWidth = 0xE000, HalfHeight = 0x8000, HalfLength = 0x20000, Radius = 0x28000,
        Mass = 10 * Fx.One, InvMass = Fx.Div(Fx.One, 10 * Fx.One), InvInertia = Fx.Div(Fx.One, 15 * Fx.One),
    };

    [Fact]
    public void RearEnd_TransfersMomentum_AndSeparates()
    {
        var a = Car(0, 0x100000);              // 16 m/s
        var b = Car(0x38000, 0);               // 3.5 m ahead (boxes 4 m long overlap by 0.5 m)
        Assert.True(CarCollision.Collide(a, b, Wide, new int[29], true));
        Assert.True(a.Vel.Z < 0x100000 && b.Vel.Z > 0, $"a {a.Vel} b {b.Vel}");
        Assert.InRange(a.Vel.Z + b.Vel.Z, 0x100000 - 0x100, 0x100000 + 0x100);   // equal masses: momentum kept
        Assert.Equal((0x50001, 0x50001), (a.ImpactType, b.ImpactType));
        Assert.Same(b, a.ImpactOther);
        Assert.True(b.Pos.Z - a.Pos.Z > 0x38000, "pushed apart");
        Assert.Equal(1, a.AirTicks);
    }

    [Fact]
    public void FarApart_NoContact()
    {
        var a = Car(0, 0x100000);
        var b = Car(0x60000, 0);
        Assert.False(CarCollision.Collide(a, b, Wide, new int[29], true));
        Assert.Equal(0x100000, a.Vel.Z);
    }

    [Fact]
    public void Separating_NoImpulse()
    {
        var a = Car(0, -0x100000);             // overlapping but already moving apart
        var b = Car(0x38000, 0x100000);
        Assert.False(CarCollision.Collide(a, b, Wide, new int[29], true));
        Assert.Equal(-0x100000, a.Vel.Z);
        Assert.True(b.Pos.Z - a.Pos.Z > 0x38000, "positions are still separated");
    }
}
