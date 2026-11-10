using System.Numerics;
using System.Reflection;
using NFS.Core;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>
/// Effect particles (sfx, spawned in): pool, lifetimes, rate limits, the conditions [ref:3292]
/// which a car makes them (slip, braking, landing, crash, edge/car hits, surface) and drawing. Presentation only: the emitter never writes the car.
/// </summary>
public class ParticleTests
{
    const int Fx1 = 0x10000;

    // ---------------------------------------------------------------- pool

    [Theory]
    [InlineData(1, 16)]
    [InlineData(6, 16)]
    [InlineData(2, 32)]
    [InlineData(3, 32)]
    [InlineData(4, 8)]
    [InlineData(5, 8)]
    [InlineData(7, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    public void Lifetime_FollowsSfxSetLifetime(int type, int updates)
    {
        var ps = new ParticleSystem();
        ps.Spawn(default, type, default);
        Assert.Equal(updates, ps.Items[0].Life);
        int tick = 0;
        for (int u = 1; u < updates; u++) { tick += 4; ps.Update(tick); Assert.Equal(1, ps.Count); }
        tick += 4; ps.Update(tick);
        Assert.Equal(0, ps.Count);                                  // dies at the last update, 4 ticks each
    }

    [Fact]
    public void Update_RunsEveryFourthTickOnly()
    {
        var ps = new ParticleSystem();
        ps.Spawn(default, 1, default);
        int life = ps.Items[0].Life;
        for (int t = 1; t <= 3; t++) { ps.Update(t); Assert.Equal(life, ps.Items[0].Life); }
        ps.Update(4);
        Assert.Equal(life - 1, ps.Items[0].Life);
        ps.Update(5); ps.Update(7);
        Assert.Equal(life - 1, ps.Items[0].Life);
        ps.Update(8);
        Assert.Equal(life - 2, ps.Items[0].Life);
    }

    [Fact]
    public void Smoke_DriftsUp_AndSpraysFollowTheCarVelocity()
    {
        var ps = new ParticleSystem();
        ps.Spawn(new Vec3(0, 0, 0), 1, new Vec3(30 * Fx1, 0, 0));       // smoke ignores the car velocity: rises (0x7FF.. per update)
        ps.Spawn(new Vec3(0, 0, 0), 7, new Vec3(32 * Fx1, 64 * Fx1, -32 * Fx1)); // spray: velocity / 32 per update
        ps.Update(4);
        var smoke = ps.Items[0]; var spray = ps.Items[1];
        Assert.InRange(smoke.Pos.Y, 0x7FF, 0xBFE);
        Assert.InRange(smoke.Pos.X, -0xCCC, 0x3FF + 0xCCC);
        Assert.Equal((Fx1, 2 * Fx1, -Fx1), (spray.Pos.X, spray.Pos.Y, spray.Pos.Z));
    }

    [Fact]
    public void Pool_IsLimitedTo60_AndRecyclesTheEntryWithTheLeastLifeLeft()
    {
        var ps = new ParticleSystem();
        for (int i = 0; i < 100; i++) ps.Spawn(new Vec3(i, 0, 0), 2, default);
        Assert.Equal(60, ParticleSystem.Capacity);
        Assert.Equal(60, ps.Count);
        ps.Spawn(new Vec3(1000, 0, 0), 4, default);                   // full pool → recycled, not grown
        Assert.Equal(60, ps.Count);
        Assert.Equal(1, ps.Items.ToArray().Count(p => p.Type == 4));
        // the entry that is nearly dead is the one that is replaced
        var ps2 = new ParticleSystem();
        ps2.Spawn(new Vec3(1, 1, 1), 7, default);                       // life 3
        for (int i = 0; i < 59; i++) ps2.Spawn(default, 2, default);   // life 32
        ps2.Spawn(new Vec3(5, 5, 5), 3, default);
        Assert.Equal(3, ps2.Items[0].Type);
        Assert.Equal(5, ps2.Items[0].Pos.X);
    }

    [Fact]
    public void UnknownTypes_AreIgnored()
    {
        var ps = new ParticleSystem();
        Assert.Equal(-1, ps.Spawn(default, 0, default));
        Assert.Equal(-1, ps.Spawn(default, 10, default));
        Assert.Equal(-1, ps.Spawn(default, 11, default));
        Assert.Equal(0, ps.Count);
    }

    [Fact]
    public void Throttle_OneSpawnPer11TicksPerWheel_And3TicksForSprays()
    {
        var ps = new ParticleSystem();
        Assert.True(ps.SpawnThrottled(0x103, 2, default, 1, default, 100));
        Assert.False(ps.SpawnThrottled(0x103, 2, default, 1, default, 110));   // 10 ticks later: still too early
        Assert.True(ps.SpawnThrottled(0x103, 2, default, 1, default, 111));
        Assert.True(ps.SpawnThrottled(0x103, 3, default, 1, default, 111));    // another wheel
        Assert.False(ps.SpawnThrottled(0x113, 2, default, 1, default, 112));   // car id & 0xF is the same slot
        Assert.True(ps.SpawnThrottled(0x104, 0, default, 8, default, 200));
        Assert.False(ps.SpawnThrottled(0x104, 0, default, 8, default, 202));
        Assert.True(ps.SpawnThrottled(0x104, 0, default, 8, default, 203));
    }

    [Fact]
    public void CrashSmoke_AtMostOncePer9Ticks_TypeThreeAlways()
    {
        var ps = new ParticleSystem();
        ps.CrashSmoke(default, 100);
        int n = ps.Count;
        Assert.InRange(n, 1, 2);
        Assert.Contains(ps.Items.ToArray(), p => p.Type == 3);
        ps.CrashSmoke(default, 108);
        Assert.Equal(n, ps.Count);
        ps.CrashSmoke(default, 109);
        Assert.True(ps.Count > n);
        Assert.All(ps.Items.ToArray(), p => Assert.True(p.Vel.Y >= 0x7FF + 0xF5C));   // +0xF5C up
    }

    [Fact]
    public void OwnRandomStream_IsDeterministicPerSeed()
    {
        var a = new ParticleSystem(7); var b = new ParticleSystem(7); var c = new ParticleSystem(8);
        foreach (var ps in new[] { a, b, c }) for (int i = 0; i < 10; i++) ps.Spawn(default, 1 + i % 3, default);
        Assert.Equal(a.Snapshot(), b.Snapshot());
        Assert.NotEqual(a.Snapshot(), c.Snapshot());
    }

    // ---------------------------------------------------------------- emitter

    static CarObj Car(int surface = 1, int speed = 15 * Fx1)
    {
        var c = new CarObj
        {
            Id = 0x101, Active = 1, Rail = 0, DistToPlayer = 0, UpDotN = Fx1, SurfaceType = surface, SpeedApprox = speed,
            HalfWidth = 0x1CCCC, HalfLength = 0x48000, Pos = new Vec3(100 * Fx1, 5 * Fx1, 200 * Fx1), PrevRoadHeight = 5 * Fx1,
            Vel = new Vec3(0, 0, speed), HeightAboveRoad = 0,
        };
        c.Orient = Mat3.Identity;
        c.DriveFrame = Mat3.Identity;
        c.LocalVel = new Vec3(0, 0, speed);
        return c;
    }

    static CarObj Tweak(CarObj c, Action<CarObj> edit) { edit(c); return c; }

    static ParticleSystem Run(CarObj c, int tick = 100)
    {
        var ps = new ParticleSystem();
        bool was = ParticleEmitter.BeforePost(c, ps, tick);
        ParticleEmitter.AfterPost(c, ps, tick, was);
        return ps;
    }

    static int Count(ParticleSystem ps, int type) => ps.Items.ToArray().Count(p => p.Type == type);

    [Fact]
    public void NoSlipNoEffect()
    {
        Assert.Equal(0, Run(Car()).Count);
    }

    [Fact]
    public void RearWheelspinOnTarmac_MakesTyreSmokeBehindTheCar()
    {
        var c = Car(); c.Skid328 = 0x280000; c.Rpm = 5000;
        var ps = Run(c);
        Assert.Equal(2, Count(ps, 1));
        Assert.All(ps.Items.ToArray(), p => Assert.True(p.Pos.Z < c.Pos.Z));            // rear axle = behind the centre (forward +z)
        var xs = ps.Items.ToArray().Select(p => p.Pos.X).OrderBy(x => x).ToArray();
        Assert.True(xs[0] < c.Pos.X && xs[1] > c.Pos.X);                                  // one per side
    }

    [Fact]
    public void RearSlideWithoutWheelspinOrBraking_LeavesMarksButNoSmoke()
    {
        var c = Car(); c.Skid328 = 0x280000; c.Rpm = 2000;                               // rpm below 4000, brake not pressed
        Assert.Equal(0, Run(c).Count);
    }

    [Fact]
    public void HardBrakingOnTarmac_SmokesFromTheFrontAndRearWheels()
    {
        var c = Car(speed: 30 * Fx1); c.Skid324 = 0x180000; c.Skid328 = 0x280000; c.BrakeRamped = 0xF8;
        Assert.Equal(4, Count(Run(c), 1));
        c.BrakeRamped = 0xF0;                                                             // needs > 0xF5
        Assert.Equal(0, Run(c).Count);
    }

    [Theory]
    [InlineData(2, 7)]   // grass → gr
    [InlineData(3, 8)]   // gravel → ga
    [InlineData(5, 6)]   // dirt → dust
    [InlineData(9, 9)]   // snow → sn
    public void SlidingOffTarmac_SpraysTheSurfaceType(int surface, int type)
    {
        Assert.Equal(type, ParticleEmitter.SprayTypeOf(surface));
        var c = Car(surface, speed: 10 * Fx1); c.Skid324 = 0x180000; c.Skid328 = 0x280000;
        Assert.Equal(4, Count(Run(c), type));                                             // two wheels per axle
    }

    [Fact]
    public void SpraySpeedLimit_NoGrassSprayAbove20MetresPerSecond()
    {
        var c = Car(2, speed: 25 * Fx1); c.Skid324 = 0x180000; c.Skid328 = 0x280000;
        Assert.Equal(0, Run(c).Count);
    }

    [Fact]
    public void FarCarsRailsAndInactiveCars_MakeNothing()
    {
        CarObj Slide() { var c = Car(2, 10 * Fx1); c.Skid324 = 0x180000; c.Skid328 = 0x280000; return c; }
        Assert.True(Run(Slide()).Count > 0);
        Assert.Equal(0, Run(Tweak(Slide(), c => c.DistToPlayer = 0x5A0000)).Count);      // only < 90 m [ref:3293]
        Assert.Equal(0, Run(Tweak(Slide(), c => c.Rail = 1)).Count);
        Assert.Equal(0, Run(Tweak(Slide(), c => c.Active = 0)).Count);
    }

    [Fact]
    public void OnItsSide_ScrapesSparksOnSurface1Only()
    {
        CarObj Side(int surfaceByte) => Tweak(Car(1, 20 * Fx1), c => { c.UpDotN = 0x1000; c.HeightAboveRoad = 0; c.SurfaceByte = (byte)surfaceByte; });
        var ps = Run(Side(1));
        Assert.Equal(1, Count(ps, 5));
        Assert.Equal(0, Run(Side(2)).Count);
        Assert.Equal(0, Run(Tweak(Side(1), c => c.SpeedApprox = 2 * Fx1)).Count);        // too slow
    }

    [Fact]
    public void LandingSmoke_DustBehindTheCar_SparksOnSurfaces1And4()
    {
        var ps = Run(Tweak(Car(3, 30 * Fx1), c => c.LandingSmoke = 5));
        Assert.Equal(1, Count(ps, 6));
        Assert.Equal(200 * Fx1 - 0x48000, ps.Items[0].Pos.Z);                              // half a car length behind (forward = +z)
        Assert.Equal(1, Count(Run(Tweak(Car(1, 30 * Fx1), c => c.LandingSmoke = 5)), 5));
        Assert.Equal(0, Run(Tweak(Car(3, 10 * Fx1), c => c.LandingSmoke = 5)).Count);      // slower than 20 m/s
    }

    [Fact]
    public void Crash_MakesCrashSmoke_AlsoInTheStepOfTheImpact()
    {
        Assert.True(Count(Run(Tweak(Car(), c => c.Crashing = 1)), 3) >= 1);
        // the impact step: +0x58C is set by itself – the smoke of that step comes from AfterPost [ref:3294]
        var d = Car(); var ps = new ParticleSystem();
        bool was = ParticleEmitter.BeforePost(d, ps, 100);
        Assert.Equal(0, ps.Count);
        d.Crashing = 1;
        ParticleEmitter.AfterPost(d, ps, 100, was);
        Assert.True(Count(ps, 3) >= 1);
    }

    [Fact]
    public void EdgeHit_FastSparks_MediumSmoke_InTheAirDust_SlowNothing()
    {
        CarObj Hit(int speed, int height = 0, int impact = 0x200000) => Tweak(Car(1, speed), c =>
        {
            c.Impact = impact; c.ImpactType = 0x40004; c.LastHit = new Vec3(7 * Fx1, 0, 9 * Fx1); c.HeightAboveRoad = height;
        });
        var fast = Run(Hit(30 * Fx1));
        Assert.Equal(1, Count(fast, 4));
        Assert.Equal(new Vec3(7 * Fx1, 0, 9 * Fx1), fast.Items[0].Pos);
        Assert.Equal(30 * Fx1 >> 5, fast.Items[0].Vel.Z);                                 // velocity / 32
        Assert.Equal(1, Count(Run(Hit(20 * Fx1)), 1));                                    // 15..24 m/s: smoke
        Assert.Equal(0, Run(Hit(12 * Fx1, impact: 0x80000)).Count);
        Assert.Equal(1, Count(Run(Hit(30 * Fx1, 0x10000)), 6));
    }

    [Fact]
    public void CarToCarCrash_MakesSparksAndDust_UnlessTheBodyAlreadyShakes()
    {
        CarObj Hit() => Tweak(Car(), c =>
        {
            c.Impact = 0xB0000; c.ImpactType = 0x50001; c.ImpactOther = Car(); c.LastHit = new Vec3(Fx1, 0, Fx1);
        });
        var ps = Run(Hit());
        Assert.Equal(1, Count(ps, 4));
        Assert.Equal(1, Count(ps, 6));
        Assert.Equal(0, Run(Tweak(Hit(), c => c.ShakePhase0 = 31)).Count);
        Assert.Equal(0, Run(Tweak(Hit(), c => c.Impact = 0xA0000)).Count);
    }

    [Fact]
    public void Emitter_NeverWritesTheCar()
    {
        var c = Car(2, 10 * Fx1);
        c.Skid324 = 0x180000; c.Skid328 = 0x280000; c.LandingSmoke = 5; c.Impact = 0xB0000; c.ImpactType = 0x40004; c.Crashing = 1;
        var before = Fields(c);
        Assert.True(Run(c).Count > 0);
        Assert.Equal(before, Fields(c));
    }

    static string Fields(CarObj c) => string.Join('|', typeof(CarObj).GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Select(f => f.GetValue(c) is Array a ? string.Join(',', a.Cast<object?>()) : f.GetValue(c)?.ToString()));

    // ---------------------------------------------------------------- drawing

    static Camera ForwardCamera() { var c = new Camera(); c.LookAlong(Vector3.UnitZ, Vector3.UnitY); c.Position = new Vector3(0, 1.5f, 0); return c; }

    static Particle At(int type, float z, float x = 0, float y = 1.2f)
    {
        var p = new Particle { Type = (byte)type, Pos = new Vec3((int)(x * Fx1), (int)(y * Fx1), (int)(z * Fx1)), Slot = 1 };
        p.Life = Math.Max(1, ParticleSystem.LifetimeOf(type) / 2);
        if (type is 4 or 5) p.Vel = new Vec3(0, 0, 20 * Fx1);
        return p;
    }

    static int Painted(FrameBuffer fb)
    {
        int n = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4) if (fb.Rgba[i] != 200 || fb.Rgba[i + 1] != 200 || fb.Rgba[i + 2] != 200) n++;
        return n;
    }

    static FrameBuffer Grey() { var fb = new FrameBuffer(); fb.Clear(200, 200, 200); return fb; }

    [GameFact]
    public void EveryTypeDrawsSomething_NearbyOnly_NothingForTypeZeroOrBehindTheCamera()
    {
        var sprites = new ParticleSprites(4);
        var r = new ParticleRenderer();
        for (int type = 1; type <= 9; type++)
        {
            var fb = Grey();
            r.Draw(fb, ForwardCamera(), [At(type, 6f)], sprites);
            Assert.True(Painted(fb) > 30, $"type {type} paints {Painted(fb)} px");
            var far = Grey();
            r.Draw(far, ForwardCamera(), [At(type, 60f)], sprites);                      // > 50 m
            Assert.Equal(0, Painted(far));
        }
        var none = Grey();
        r.Draw(none, ForwardCamera(), [At(0, 6f), At(1, -6f)], sprites);
        Assert.Equal(0, Painted(none));
    }

    [GameFact]
    public void Sprites_ShrinkWithDistance_AndTheOrderInTheListDoesNotMatter()
    {
        var sprites = new ParticleSprites(4);
        var r = new ParticleRenderer();
        int Area(float z) { var fb = Grey(); r.Draw(fb, ForwardCamera(), [At(7, z)], sprites); return Painted(fb); }
        Assert.True(Area(6f) > Area(12f) * 2, "half the distance = about 4 times the area");
        var a = Grey(); var b = Grey();
        r.Draw(a, ForwardCamera(), [At(8, 5f), At(7, 5.5f)], sprites);
        r.Draw(b, ForwardCamera(), [At(7, 5.5f), At(8, 5f)], sprites);
        Assert.Equal(a.Sha256(), b.Sha256());                                             // far → near by depth, not by list order
    }

    [GameFact]
    public void Drawing_AllocatesNothing()
    {
        var sprites = new ParticleSprites(4);
        var r = new ParticleRenderer();
        var items = new Particle[ParticleSystem.Capacity];
        for (int i = 0; i < items.Length; i++) items[i] = At(1 + i % 9, 4f + i % 20, (i % 7 - 3) * 1.2f);
        var fb = new FrameBuffer(); var cam = ForwardCamera();
        for (int i = 0; i < 3; i++) r.Draw(fb, cam, items, sprites);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) r.Draw(fb, cam, items, sprites);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [GameFact]
    public void SparkShapes_AreDifferentOnTrack8()
    {
        var normal = new ParticleSprites(4); var ice = new ParticleSprites(8);
        Assert.NotEqual(normal.Sparks[0].Rgba, ice.Sparks[0].Rgba);
        Assert.Equal(8, normal.Smoke.Length);
        Assert.Equal(3, normal.Snow.Length);
    }

    // ---------------------------------------------------------------- z-sort against the cars (inserts into the car list) [ref:3295]

    const uint CarRed = 0xC01010;

    /// <summary>Stand-in for the scene: grey sky, a red 50×36 px block per car (its depth is all the renderer needs).</summary>
    sealed class FakeScene(Camera cam)
    {
        public int Renders;
        public void Render(FrameBuffer fb, IReadOnlyList<CarInstance> cars) { fb.Clear(200, 200, 200); Blocks(fb, cars); }
        /// <summary>The silhouette pass: cleared transparent, only the given cars.</summary>
        public void Silhouette(FrameBuffer fb, IReadOnlyList<CarInstance> cars) { fb.ClearTransparent(); Blocks(fb, cars); }
        void Blocks(FrameBuffer fb, IReadOnlyList<CarInstance> cars)
        {
            Renders++;
            foreach (var car in cars)
            {
                var (cx, cy) = cam.Project(cam.ToView(car.Position));
                for (int y = (int)cy - 18; y < (int)cy + 18; y++)
                    for (int x = (int)cx - 25; x < (int)cx + 25; x++) fb.Set(x, y, CarRed);
            }
        }
    }

    static CarInstance CarAt(float z) => new(null!, new Vector3(0, 0.8f, z), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    static int Count(FrameBuffer fb, uint rgb)
    {
        int n = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4) if ((uint)(fb.Rgba[i] << 16 | fb.Rgba[i + 1] << 8 | fb.Rgba[i + 2]) == rgb) n++;
        return n;
    }

    /// <summary>Scene with one car at z = 10 and a big type 7 sprite at <paramref name="particleZ"/>; returns (red car pixels, pixels that are
    /// neither grey nor car red, scene renders).</summary>
    static (int Car, int Sprite, int Renders, int CarBefore) Compose(float particleZ, float carZ = 10f, int type = 2)
    {
        var cam = ForwardCamera();
        var scene = new FakeScene(cam);
        var cars = new List<CarInstance> { CarAt(carZ) };
        var fb = new FrameBuffer();
        scene.Render(fb, cars);
        scene.Renders = 0;
        int before = Count(fb, CarRed);
        new ParticleRenderer().Draw(fb, cam, [At(type, particleZ, 0, 0.8f)], new ParticleSprites(4), cars, scene.Silhouette);
        int other = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4)
        {
            uint c = (uint)(fb.Rgba[i] << 16 | fb.Rgba[i + 1] << 8 | fb.Rgba[i + 2]);
            if (c != CarRed && c != 0xC8C8C8) other++;
        }
        return (Count(fb, CarRed), other, scene.Renders, before);
    }

    [Theory]
    [InlineData(10f, 10f, false)]   // same depth: the sprite sorts 1.5 m behind (bias 0x88000), the car is drawn over it
    [InlineData(5f, 10f, true)]     // clearly nearer than the car
    [InlineData(8f, 10f, true)]     // 2 m in front of the centre: just in front of the car in the list
    [InlineData(9f, 10f, false)]    // 1 m in front of the centre: still sorted behind the car
    [InlineData(12f, 10f, false)]   // behind
    [InlineData(30f, 10f, false)]
    [InlineData(1f, -5f, true)]     // a car behind the camera is not drawn at all
    public void CarDrawnAfter_FollowsTheZSortBuckets(float particleZ, float carZ, bool particleOnTop)
    {
        if (carZ < -2) Assert.False(ParticleRenderer.CarDrawnAfter(particleZ, carZ));
        else Assert.Equal(!particleOnTop, ParticleRenderer.CarDrawnAfter(particleZ, carZ));
    }

    [GameFact]
    public void SpriteBehindACar_DoesNotCoverTheCar_ButShowsAroundIt()
    {
        var (car, sprite, renders, before) = Compose(16f);
        Assert.Equal(before, car);                    // every car pixel survives
        Assert.True(sprite > 50, $"the sprite is still visible around the car ({sprite} px)");
        Assert.Equal(1, renders);                     // the scene without the car was needed once
    }

    [GameFact]
    public void SpriteInFrontOfACar_CoversIt_WithoutRenderingTheSceneAgain()
    {
        var (car, sprite, renders, before) = Compose(6f);
        Assert.True(car < before, "the sprite lies over the car");
        Assert.True(sprite > 200);
        Assert.Equal(0, renders);
    }

    [GameFact]
    public void SpriteJustInFrontOfTheCarCentre_StillSortsBehindIt()
    {
        var (car, _, renders, before) = Compose(9f);    // 1 m in front of the centre: sorts behind (bias 0x88000 = +1.5 m)
        Assert.Equal(before, car);
        Assert.Equal(1, renders);
    }

    [GameFact]
    public void SpriteBehindACar_ThatCannotOverlapOnScreen_NeedsNoSecondRender()
    {
        var cam = ForwardCamera();
        var scene = new FakeScene(cam);
        var cars = new List<CarInstance> { new(null!, new Vector3(-30, 0.8f, 10), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ) };
        var fb = new FrameBuffer(); scene.Render(fb, cars); scene.Renders = 0;
        new ParticleRenderer().Draw(fb, cam, [At(7, 16f, 0, 0.8f)], new ParticleSprites(4), cars, scene.Silhouette);
        Assert.Equal(0, scene.Renders);
    }

    [GameFact]
    public void NoCars_DrawsLikeThePlainOverload()
    {
        var cam = ForwardCamera();
        var sprites = new ParticleSprites(4);
        var a = Grey(); var b = Grey();
        Particle[] items = [At(1, 8f), At(4, 9f, 1f)];
        var r = new ParticleRenderer();
        r.Draw(a, cam, items, sprites);
        r.Draw(b, cam, items, sprites, [], (_, _) => Assert.Fail("no car, no second render"));
        Assert.Equal(a.Sha256(), b.Sha256());
    }
}
