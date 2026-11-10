using NFS.Core;
using NFS.Desktop;
using NFS.Render;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>
/// Effect particles in a race scene (sfx: tyre smoke, sparks, dust, spray): they appear while the car slides, die after their lifetime, never exceed the
/// pool and are drawn over the 3-D view. Vanilla note: GraphicsModeTests compares with golden hashes of the OLD PORT (not of the original game); the
/// particles only show up in a frame while the car skids, so the goldens (launch + 40 frames of full throttle) stay valid.
/// PNG: out/gfx_check/particles. One driven scene for the whole class.
/// </summary>
public class ParticleSceneTests(ITestOutputHelper output)
{
    sealed class Run
    {
        public RaceDrive Drive = null!;
        public int MaxCount, MaxSmoke, FirstSmokeStep = -1, CountAfterStop = -1, CountAfterFlood = -1;
        public int[] GalleryTypes = [];
        public string? EmptyOff, EmptyOn;
        public FrameBuffer? SlideOff, SlideOn, GalleryOff, GalleryOn;
    }

    static string OutDir()
    {
        if (Environment.GetEnvironmentVariable("NFS_PARTICLES_PNG_DIR") is { Length: > 0 } custom) { Directory.CreateDirectory(custom); return custom; }
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, Path.Combine("src", "NFS2SE.sln")))) dir = Path.GetDirectoryName(dir);
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, "out", "gfx_check", "particles");
        Directory.CreateDirectory(path);
        return path;
    }

    static Vec3 Ahead(CarObj c, float forward, float right, float up = 0.4f) => new(
        c.Pos.X + (int)(c.Orient.M20 * forward) + (int)(c.Orient.M00 * right),
        c.Pos.Y + (int)(up * 65536),
        c.Pos.Z + (int)(c.Orient.M22 * forward) + (int)(c.Orient.M02 * right));

    static readonly Lazy<Run> Scene = new(() =>
    {
        var r = new Run { Drive = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true) };
        var d = r.Drive;
        var system = d.Host.View.Particles!.Particles;

        // a fresh race has no particles: the option changes nothing
        var a = new FrameBuffer(); var b = new FrameBuffer();
        d.ShowParticles = false; d.Render(a); d.ShowParticles = true; d.Render(b);
        (r.EmptyOff, r.EmptyOn) = (a.Sha256(), b.Sha256());

        void Observe(int step)
        {
            var items = d.View.Particles ?? [];
            r.MaxCount = Math.Max(r.MaxCount, items.Count);
            int smoke = 0;
            foreach (var p in items) if (p.Type == 1) smoke++;
            if (smoke > 0 && r.FirstSmokeStep < 0) r.FirstSmokeStep = step;
            if (smoke > r.MaxSmoke)
            {
                r.MaxSmoke = smoke;
                d.SnapToHost();
                d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
                r.SlideOff ??= new FrameBuffer(); r.SlideOn ??= new FrameBuffer();
                d.ShowParticles = false; d.Render(r.SlideOff);
                d.ShowParticles = true; d.Render(r.SlideOn);
            }
        }
        for (int i = 0; i < 32 * 3; i++) d.Step(Autopilot.Input(d.Runner, 255));
        // lock the brakes and weave: the rear wheels spin up above 4000 rpm and smoke
        for (int i = 0; i < 110; i++)
        {
            d.Step(new PlayerInput(i < 40 ? (sbyte)0 : (sbyte)((i / 6) % 2 == 0 ? 100 : -100), 0, 255, 0));
            Observe(i);
        }
        // standing still: every particle must die within its lifetime (longest: 32 updates of 4 ticks)
        for (int i = 0; i < 32 * 3; i++) d.Step(new PlayerInput(0, 0, 255, 0));
        r.CountAfterStop = d.View.Particles?.Count ?? 0;

        // gallery: one of every type ahead of the car
        d.SnapToHost();
        d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        var car = d.Car;
        float z = 7f;
        int tick = d.Runner.Tick;
        void Place(int type, int slot)
        {
            float lat = (slot % 3 - 1) * 3.2f;
            var fwd = new Vec3(car.Orient.M20 * 8, 0, car.Orient.M22 * 8);   // sparks fly along the car
            Assert.True(system.Spawn(Ahead(car, z + (slot / 3) * 6f, lat), type, fwd) >= 0);
        }
        foreach (int t in new[] { 1, 2, 3, 6 }) Place(t, t - 1);      // smoke and dust: half way through their life (8 updates)
        for (int u = 1; u <= 8; u++) system.Update(tick + 4 * u);
        tick += 36;
        foreach (int t in new[] { 4, 5, 7, 8, 9 }) Place(t, t - 1);   // short-lived: one update old
        system.Update(tick + 4);
        d.SnapToHost();
        d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        r.GalleryTypes = d.View.Particles!.Select(p => (int)p.Type).Distinct().Order().ToArray();
        r.GalleryOff = new FrameBuffer(); r.GalleryOn = new FrameBuffer();
        d.ShowParticles = false; d.Render(r.GalleryOff);
        d.ShowParticles = true; d.Render(r.GalleryOn);

        // flood the pool
        for (int i = 0; i < 200; i++) system.Spawn(Ahead(car, 3 + i % 10, 0), 2, default);
        d.SnapToHost();
        r.CountAfterFlood = d.View.Particles!.Count;
        return r;
    });

    static int Diff(FrameBuffer a, FrameBuffer b)
    {
        int n = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            if (a.Rgba[i] != b.Rgba[i] || a.Rgba[i + 1] != b.Rgba[i + 1] || a.Rgba[i + 2] != b.Rgba[i + 2]) n++;
        return n;
    }

    [GameDataFact]
    public void FreshRace_HasNoParticles_TheOptionChangesNothing()
    {
        var r = Scene.Value;
        Assert.Equal(r.EmptyOff, r.EmptyOn);
    }

    [GameDataFact]
    public void LockedBrakesAndWeaving_MakeTyreSmoke_DrawnOverTheRoad()
    {
        var r = Scene.Value;
        output.WriteLine($"smoke from step {r.FirstSmokeStep}, max {r.MaxSmoke} puffs, pool max {r.MaxCount}");
        Assert.True(r.MaxSmoke >= 2, "rear wheels smoke");
        Assert.NotNull(r.SlideOn);
        int changed = Diff(r.SlideOff!, r.SlideOn!);
        Assert.True(changed > 300, $"smoke covers pixels, changed {changed}");
        r.SlideOff!.SavePng(Path.Combine(OutDir(), "slide_off.png"));
        r.SlideOn!.SavePng(Path.Combine(OutDir(), "slide_on.png"));
    }

    [GameDataFact]
    public void Particles_DieAfterTheirLifetime_WhenTheCarStopsSliding()
    {
        var r = Scene.Value;
        Assert.True(r.MaxCount > 0);
        Assert.Equal(0, r.CountAfterStop);
    }

    [GameDataFact]
    public void EveryTypeIsDrawnInTheScene_Gallery()
    {
        var r = Scene.Value;
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], r.GalleryTypes);
        int changed = Diff(r.GalleryOff!, r.GalleryOn!);
        Assert.True(changed > 2000, $"the gallery covers pixels, changed {changed}");
        r.GalleryOff!.SavePng(Path.Combine(OutDir(), "gallery_off.png"));
        r.GalleryOn!.SavePng(Path.Combine(OutDir(), "gallery_on.png"));
    }

    [GameDataFact]
    public void PoolNeverExceeds60_EvenWhenFlooded()
    {
        var r = Scene.Value;
        Assert.True(r.MaxCount <= ParticleSystem.Capacity);
        Assert.Equal(ParticleSystem.Capacity, r.CountAfterFlood);
    }

    [GameDataFact]
    public void ShapeSheet_EveryTypeAtSixStagesOfItsLife()
    {
        var sprites = new ParticleSprites(4);
        var renderer = new ParticleRenderer();
        var fb = new FrameBuffer(640, 480);
        fb.Clear(120, 120, 120);
        var cam = new Camera();
        cam.LookAlong(System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitY);
        cam.Position = new System.Numerics.Vector3(0, 0, 0);
        var items = new Particle[60];
        int n = 0;
        for (int type = 1; type <= 9; type++)
            for (int col = 0; col < 6; col++)
            {
                int life = ParticleSystem.LifetimeOf(type);
                var p = new Particle
                {
                    Type = (byte)type, Slot = 1, Life = Math.Max(1, life - col * life / 6), Angle = 0x80 * col,
                    Pos = new Vec3((int)((col - 2.5f) * 1.5f * 65536), (int)((4 - type) * 1.0f * 65536), 12 * 65536),
                    Vel = new Vec3(8 * 65536, 0, 0),
                };
                items[n++] = p;
            }
        renderer.Draw(fb, cam, items.AsSpan(0, 54), sprites);
        int painted = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4) if (fb.Rgba[i] != 120) painted++;
        Assert.True(painted > 3000, $"{painted} px");
        fb.SavePng(Path.Combine(OutDir(), "shape_sheet.png"));
    }
}
