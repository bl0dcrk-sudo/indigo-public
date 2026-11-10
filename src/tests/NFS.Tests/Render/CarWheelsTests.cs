using System.Numerics;
using NFS.Core;
using NFS.Core.Tables;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

/// <summary>Wheel / body animation of the car model, as in the original game [ref:3280] [ref:3496]
/// — presentation only, CarObj stays untouched. [ref:3281]</summary>
public class CarWheelsTests
{
    const double Rad = 2 * Math.PI / 0x10000;

    static readonly Dictionary<string, CarModel> Models = new();

    static CarInstance Car(string name, int type, CarAnim? anim, int lod = 0) =>
        new(Models.TryGetValue(name, out var m) ? m : Models[name] = new CarModel(name, GameData.Dir, carType: type), Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, lod, anim);

    /// <summary>Polygons per part (LOD order, polygons in file order).</summary>
    static List<(int Part, Vector3[] P, Texture? Tex)> ByPart(CarInstance car, Vector3? cam = null)
    {
        var polys = car.Polys(cam).ToList();
        var res = new List<(int, Vector3[], Texture?)>();
        int k = 0;
        foreach (int pi in CarModel.LodParts(car.Lod))
            foreach (var p in car.Model.Geo.Parts[pi].Polygons)
            {
                if (new[] { p.V0, p.V1, p.V2, p.V3 }.Any(i => i >= car.Model.Geo.Parts[pi].NumVerts)) continue;
                res.Add((pi, polys[k].P, polys[k].Tex)); k++;
            }
        return res;
    }

    /// <summary>Yaw (16-bit units) of the rim disc of a front wheel: direction of a rim edge that runs along the car z axis.</summary>
    static double RimYaw(CarInstance car, int part, Vector3? cam)
    {
        var rim = ByPart(car, cam).First(t => t.Part == part);
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                if (MathF.Abs(rim.P[i].Y - rim.P[j].Y) < 1e-4f && MathF.Abs(rim.P[i].Z - rim.P[j].Z) > 0.1f)
                {
                    var d = rim.P[i].Z > rim.P[j].Z ? rim.P[i] - rim.P[j] : rim.P[j] - rim.P[i];
                    return Math.Atan2(d.X, d.Z) / Rad;
                }
        throw new Exception("no rim edge");
    }

    [Theory]
    [InlineData(4, 0, 0, 0x2A)]            // no steering: constant (angle 0 anyway)
    [InlineData(4, 100, 0, 0x2A)]          // Indigo: full angle from every view
    [InlineData(4, 100, 0x4000, 0x2A)]
    [InlineData(0, 100, 0, 0)]             // straight from behind: no visible steering
    [InlineData(0, 100, 0x1000, 0x10)]     // |az|>>8
    [InlineData(0, 100, -0x1000, 0x10)]
    [InlineData(0, 100, 0x2A00, 0x2A)]
    [InlineData(0, 100, 0x31FF, 0x2A)]     // capped
    [InlineData(0, 100, 0x4000, 0)]        // side band 0x3200..0x4600: 0
    [InlineData(0, 100, -0x4000, 0)]
    [InlineData(0, 100, 0x5500, 0x2A)]     // 0x7F00 - az, capped
    [InlineData(0, 100, 0x7800, 0x07)]
    [InlineData(0, 100, -0x7800, 0x07)]
    [InlineData(0, 100, 0x7F00, 0)]        // from the front
    public void SteerScale_FollowsAsm459F70(int type, int steer, int az, int expected) =>
        Assert.Equal(expected, CarAnim.SteerScale(type, steer, az));

    [GameFact]
    public void Indigo_FrontWheelsTurnWithSteer_RearAndBodyDoNot()
    {
        var still = Car("IDGO", 4, new CarAnim(4));
        var steered = Car("IDGO", 4, new CarAnim(4) { Steer = 100 });
        var cam = new Vector3(3, 1.5f, -6);
        var a = ByPart(still, cam); var b = ByPart(steered, cam);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            bool front = a[i].Part is >= 12 and <= 15;
            bool same = a[i].P.Zip(b[i].P).All(t => (t.First - t.Second).Length() < 1e-5f);
            Assert.Equal(!front, same);   // only the front wheel parts move
        }
        double expected = 0x2A * 100;     // Indigo: scale 0x2A x steer, 16-bit angle (about 23 degrees right)
        Assert.InRange(RimYaw(steered, 12, cam) - RimYaw(still, 12, cam), expected - 5, expected + 5);
        Assert.InRange(RimYaw(steered, 14, cam) - RimYaw(still, 14, cam), expected - 5, expected + 5);
        var left = Car("IDGO", 4, new CarAnim(4) { Steer = -100 });
        Assert.InRange(RimYaw(left, 12, cam) - RimYaw(still, 12, cam), -expected - 5, -expected + 5);
    }

    [GameFact]
    public void OtherCars_SteerDependsOnViewAzimuth_AndIsZeroFromStraightBehind()
    {
        var straight = Car("F355", 0, new CarAnim(0) { Steer = 100 });
        var still = Car("F355", 0, new CarAnim(0));
        var behind = new Vector3(0, 1.5f, -6);
        Assert.Equal(RimYaw(still, 12, behind), RimYaw(straight, 12, behind), 3);
        var oblique = new Vector3(5, 1.5f, -5);      // az about 0x2000 -> scale 0x20
        int az = straight.ViewAzimuth(oblique);
        Assert.InRange(az, -0x2100, -0x1F00);   // camera to the right of the car: negative azimuth
        double expected = CarAnim.SteerScale(0, 100, az) * 100;
        Assert.InRange(expected, 0x1F * 100, 0x21 * 100);
        Assert.InRange(RimYaw(straight, 12, oblique) - RimYaw(still, 12, oblique), expected - 5, expected + 5);
    }

    [GameFact]
    public void NeutralAnim_EqualsStaticModel()
    {
        var plain = Car("JAGR", 4, null);
        var neutral = Car("JAGR", 4, new CarAnim(4));
        var a = plain.Polys(new Vector3(2, 1, -5)).ToList(); var b = neutral.Polys(new Vector3(2, 1, -5)).ToList();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].P, b[i].P);
            Assert.Same(a[i].Tex, b[i].Tex);
        }
    }

    [GameFact]
    public void RimTexture_FollowsWheelFrame_RearAndFrontSeparately()
    {
        var f0 = ByPart(Car("F355", 0, new CarAnim(0)));
        var anim = new CarAnim(0) { FrontRimFrame = 3, RearRimFrame = 1 };
        var f3 = ByPart(Car("F355", 0, anim));
        Texture? Rim(List<(int Part, Vector3[] P, Texture? Tex)> l, int part) => l.First(t => t.Part == part).Tex;
        Assert.NotNull(Rim(f0, 12));
        Assert.NotSame(Rim(f0, 12), Rim(f3, 12));      // m000 -> m300
        Assert.NotSame(Rim(f0, 14), Rim(f3, 14));
        Assert.NotSame(Rim(f0, 16), Rim(f3, 16));      // m000 -> m100
        Assert.NotSame(Rim(f3, 12), Rim(f3, 16));      // front 3, rear 1: different frames
        Assert.Same(Rim(f0, 13), Rim(f3, 13));         // tyres do not animate
    }

    [Fact]
    public void RimFrames_GrowWithSpeed_AndSlipLocksToBlur()
    {
        CarObj At(double v) { var c = new CarObj(); c.LocalVel = new Vec3(0, 0, (int)(v * Fx.One)); return c; }
        Assert.Equal((0, 0), CarAnim.RimFrames(At(0), 5));
        Assert.Equal((0, 0), CarAnim.RimFrames(At(0.1), 5));
        Assert.Equal((2, 2), CarAnim.RimFrames(At(1), 4));          // < 2.5 m/s: 1/2 flicker every 4 ticks
        Assert.Equal((1, 1), CarAnim.RimFrames(At(1), 0));
        Assert.Equal((2, 2), CarAnim.RimFrames(At(5), 2));          // < 7: every 2 ticks
        Assert.Equal((1, 1), CarAnim.RimFrames(At(5), 0));
        Assert.Equal((2, 2), CarAnim.RimFrames(At(10), 1));         // < 20: every tick
        Assert.Equal((3, 3), CarAnim.RimFrames(At(30), 0));         // fast: always 3
        Assert.Equal((3, 3), CarAnim.RimFrames(At(-30), 1));        // reverse too (|v|)
        var slip = At(10); slip.SlipState = 3;                       // +0x2F4: rear
        Assert.Equal((1, 3), CarAnim.RimFrames(slip, 0));
        var front = At(10); front.SlipFront = 1;                     // +0x2F8: front
        Assert.Equal((3, 2), CarAnim.RimFrames(front, 1));
        var locked = At(30); locked.SlidingAxles = 1;                // braking lock: front wheel does not blur
        Assert.Equal((0, 3), CarAnim.RimFrames(locked, 0));
    }

    [GameFact]
    public void Tick_ReadsCarAndLeavesItUntouched()
    {
        var t = GameTables.Default;
        var c = new CarObj { Active = 1, Steer = 64, BrakeRamped = 1 };
        c.LocalVel = new Vec3(0, 0, 30 * Fx.One);
        c.SpeedApprox = 30 * Fx.One;
        c.LocalForce = new Vec3(3 * Fx.One, 0, -2 * Fx.One);
        var anim = CarAnim.For(0, t);
        int steer = c.Steer, roll = c.BodyRoll, pitch = c.BodyPitch, fz = c.SmoothFz, fx = c.SmoothFx, amp = c.ShakeAmplitude, gain = c.RollGain;
        for (int i = 0; i < 8; i++) anim.Tick(c, t);
        Assert.Equal(64, anim.Steer);
        Assert.True(anim.BrakeLight);
        Assert.Equal(3, anim.FrontRimFrame);
        Assert.NotEqual(0, anim.Roll | anim.Pitch);                   // cornering force leans the body
        Assert.InRange(anim.Roll, -CarAnim.RollLimit, CarAnim.RollLimit);
        Assert.InRange(anim.Pitch, -CarAnim.PitchLimit, CarAnim.PitchLimit);
        Assert.All(anim.WheelY, y => Assert.InRange(y, -0x1000, 0x1000));   // < 6 cm
        Assert.Equal((steer, roll, pitch, fz, fx, amp, gain), (c.Steer, c.BodyRoll, c.BodyPitch, c.SmoothFz, c.SmoothFx, c.ShakeAmplitude, c.RollGain));
    }

    [GameFact]
    public void BodyLean_MovesBodyParts_NotWheels_AndLeavesLod1Alone()
    {
        var still = ByPart(Car("F355", 0, new CarAnim(0)));
        var lean = ByPart(Car("F355", 0, new CarAnim(0) { Roll = 0x1000, Pitch = 0x800 }));
        for (int i = 0; i < still.Count; i++)
        {
            bool body = still[i].Part < 12;
            bool same = still[i].P.Zip(lean[i].P).All(t => (t.First - t.Second).Length() < 1e-5f);
            Assert.Equal(!body, same);
        }
        var top = still.Where(t => t.Part == 1).SelectMany(t => t.P).OrderByDescending(p => p.Y).First();
        var moved = lean.Where(t => t.Part == 1).SelectMany(t => t.P).OrderByDescending(p => p.Y).First();
        Assert.True((top - moved).Length() > 0.1f);
        var l1 = Car("F355", 0, new CarAnim(0) { Roll = 0x1000, Steer = 100, FrontRimFrame = 3 }, lod: 1);
        var l1plain = Car("F355", 0, null, lod: 1);
        Assert.Equal(l1plain.Polys().Select(p => p.P).ToList(), l1.Polys(new Vector3(3, 1, -4)).Select(p => p.P).ToList());
    }

    [GameFact]
    public void BrakeLight_SwitchesLightPolygonFrame()
    {
        Texture? Light(CarAnim a) => ByPart(Car("F355", 0, a)).First(t => t.Part == 11).Tex;
        Assert.NotSame(Light(new CarAnim(0)), Light(new CarAnim(0) { BrakeLight = true }));
    }

    [GameFact]
    public void WheelWobble_MovesWheelVerticallyOnly()
    {
        var still = ByPart(Car("F355", 0, new CarAnim(0)));
        var an = new CarAnim(0);
        an.WheelY[2] = 0x1000;                                        // 6 cm up, rear right (parts 16, 17)
        var wob = ByPart(Car("F355", 0, an));
        for (int i = 0; i < still.Count; i++)
            for (int k = 0; k < 4; k++)
            {
                var d = wob[i].P[k] - still[i].P[k];
                if (still[i].Part is 16 or 17) Assert.True(MathF.Abs(d.Y - 0.0625f) < 1e-4f && MathF.Abs(d.X) + MathF.Abs(d.Z) < 1e-5f);
                else Assert.True(d.Length() < 1e-5f);
            }
    }

    [GameFact]
    public void ExeBodyTables_AreReadFromTheExe()
    {
        var t = GameTables.Default;
        Assert.Equal(30, t.ModelBody.Length);
        Assert.All(Enumerable.Range(0, 15), m => Assert.InRange(t.ModelBody[m * 2], 0x1A, 0x41));          // lean gain (int, scales a 16.16 force into a 16-bit angle)
        Assert.All(Enumerable.Range(0, 15), m => Assert.InRange(t.ModelBody[m * 2 + 1], 0x1999, 0x10000));  // shake cap 0.1..1.0
        Assert.Equal([0, 0, -3, -6, -8, -9, -8, -6, -3, 0, 7, 12, 16, 18, 19, 18, 16, 12, 7, 0], t.BodyShakeWave[..20]);
        Assert.Equal(0x40, t.SurfShake.Length);
    }
}
