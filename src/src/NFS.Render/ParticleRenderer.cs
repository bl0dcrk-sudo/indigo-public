using System.Numerics;
using NFS.Core;
using NFS.Data;

namespace NFS.Render;

/// <summary>Shapes of the car effects: <c>sm00-07</c> (tyre/crash smoke) and <c>di00-07</c> (dust) of GAMEDATA/RENDER/PC/SFXLUT.FSH, <c>gr/ga/sn00-02</c>
/// (grass, gravel, snow spray) and <c>sp00-07</c> (sparks; <c>ms00-07</c> on track 8) of SFX.FSH. [ref:2593]
/// Index 255 is transparent (all of them are 0x7B bitmaps).</summary>
public sealed class ParticleSprites
{
    public FshImage[] Smoke { get; }
    public FshImage[] Dust { get; }
    public FshImage[] Grass { get; }
    public FshImage[] Gravel { get; }
    public FshImage[] Snow { get; }
    public FshImage[] Sparks { get; }

    public ParticleSprites(int trackNumber, string? gameDir = null)
    {
        var sfx = Fsh.Load("GAMEDATA/RENDER/PC/SFX.FSH", gameDir);
        var lut = Fsh.Load("GAMEDATA/RENDER/PC/SFXLUT.FSH", gameDir);
        Smoke = Series(lut, "sm", 8);
        Dust = Series(lut, "di", 8);
        Gravel = Series(sfx, "ga", 3);
        Snow = Series(sfx, "sn", 3);
        Grass = Series(sfx, "gr", 3);
        Sparks = Series(sfx, trackNumber == 8 ? "ms" : "sp", 8);
    }

    /// <summary>Shape name format: "%s%02d". [ref:2594]</summary>
    static FshImage[] Series(Fsh fsh, string prefix, int count)
    {
        var pal = fsh.GlobalPalette();
        var list = new FshImage[count];
        for (int i = 0; i < count; i++)
        {
            var e = fsh.Find($"{prefix}{i:00}")!;
            var img = fsh.Decode(e, pal)!;
            var idx = fsh.Indices(e);
            for (int k = 0; k < idx.Length; k++) if (idx[k] == 255) img.Rgba[4 * k + 3] = 0;
            list[i] = img;
        }
        return list;
    }
}

/// <summary>
/// Draws the effect particles over the 3-D view: particles within 50 m of [ref:2595]
/// the camera, billboards = rotated squares of half-diagonal <c>size·K/z</c> (K = 10 px·m for the full 640×480 window) [ref:2596]
/// sparks = view-space quads along the velocity, far to near. [ref:2597]
/// <para>Deviations (hypotheses): the original sorts the sprites into the polygon list (bias 0x88000 = 1.5 m behind the cars); here they are
/// drawn over the finished scene; the cars hide the sprites that sort behind them (the overload with the car list), walls and scenery do not. The smoke/dust shapes (<c>sfxlut.fsh</c>, draw mode 5) are
/// blended with a fixed opacity instead of the light LUT of the 8-bit renderer.</para>
/// </summary>
public sealed class ParticleRenderer
{
    /// <summary>0x320000 = 50 m. [ref:2598]</summary>
    public const float RangeMetres = 50f;
    /// <summary>Opacity (0..255) of the smoke and dust shapes (mode 5, LUT-shaded in the original) [hypothesis].</summary>
    public const int SmokeAlpha = 170, DustAlpha = 190;

    readonly int[] _order = new int[ParticleSystem.Capacity];
    readonly float[] _depth = new float[ParticleSystem.Capacity];

    /// <summary>Draws <paramref name="items"/> as seen by <paramref name="cam"/> into <paramref name="fb"/> (pixels of the camera's frame).</summary>
    public void Draw(FrameBuffer fb, Camera cam, ReadOnlySpan<Particle> items, ParticleSprites sprites) => Draw(fb, cam, items, sprites, null);

    void Draw(FrameBuffer fb, Camera cam, ReadOnlySpan<Particle> items, ParticleSprites sprites, byte[]? mask)
    {
        int n = 0;
        for (int i = 0; i < items.Length && n < _order.Length; i++)
        {
            var p = items[i];
            if (p.Type == 0) continue;
            var w = World(p.Pos);
            // max + min/4 < 50 m [ref:2599]
            float dx = Math.Abs(w.X - cam.Position.X), dz = Math.Abs(w.Z - cam.Position.Z);
            if ((dx > dz ? dx + dz * 0.25f : dz + dx * 0.25f) >= RangeMetres) continue;
            float z = cam.ToView(w).Z;
            if (z < (float)Camera.NearZ) continue;
            // far → near (insertion sort; stable for equal depth)
            int j = n++;
            while (j > 0 && _depth[j - 1] < z) { _order[j] = _order[j - 1]; _depth[j] = _depth[j - 1]; j--; }
            _order[j] = i; _depth[j] = z;
        }
        for (int k = 0; k < n; k++) DrawOne(fb, cam, items[_order[k]], sprites, mask);
    }

    // ---- z-sort against the cars (inserts the sprites into the same polygon list as the cars) ---- [ref:2600]

    /// <summary>Radius, in metres, of the circle that bounds a car on screen (the model is about 4.5 m long): only used to decide whether a
    /// sprite and a car can overlap at all.</summary>
    const float CarRadiusMetres = 3.2f;

    readonly Particle[] _hidden = new Particle[ParticleSystem.Capacity], _free = new Particle[ParticleSystem.Capacity];
    readonly List<CarInstance> _occluders = new();
    FrameBuffer? _scratch;

    /// <summary>order: the sprite goes in with bias 0x88000 (it sorts as 1.5 m behind its true depth), the car with bias 0xA0000 [ref:2601]
    /// at the depth of its centre; the list is drawn far → near and within a bucket the entry inserted last (the car) first, so the car lands on
    /// top of the sprite exactly when its bucket is nearer than the sprite's.</summary>
    public static bool CarDrawnAfter(float particleZ, float carZ)
    {
        int kp = ZSort<int>.BucketFor(particleZ, ZSort<int>.ParticleBias), kc = ZSort<int>.BucketFor(carZ, ZSort<int>.CarBias);
        return kp >= 0 && kc >= 0 && kc < kp && carZ >= -2f && carZ <= 200f;
    }

    static bool Has(List<CarInstance> list, CarInstance car)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], car)) return true;
        return false;
    }

    /// <summary>True when <paramref name="car"/> is drawn over <paramref name="p"/> (<see cref="CarDrawnAfter"/>) and their screen footprints can overlap.</summary>
    public static bool IsHiddenBy(Camera cam, in Particle p, CarInstance car)
    {
        var pv = cam.ToView(World(p.Pos));
        var cv = cam.ToView(car.Position);
        if (pv.Z < (float)Camera.NearZ || !CarDrawnAfter(pv.Z, cv.Z)) return false;
        if (cv.Z < 1f) return true;   // the car is on top of the camera: it can cover anything
        float fx = (float)cam.Fx;
        float pr = p.Type is 4 or 5 ? 4f * fx / Math.Max(pv.Z, 1f) : SizeAndFrame(p).Size * (fx / 32f) / Math.Max(pv.Z, 1f);
        var (px, py) = cam.Project(pv);
        var (cx, cy) = cam.Project(cv);
        float cr = CarRadiusMetres * fx / cv.Z, dx = (float)(px - cx), dy = (float)(py - cy);
        return dx * dx + dy * dy < (pr + cr) * (pr + cr);
    }

    /// <summary>Draws the sprites like <see cref="Draw(FrameBuffer, Camera, ReadOnlySpan{Particle}, ParticleSprites)"/>, but a sprite that the original
    /// sorts behind a car (see <see cref="CarDrawnAfter"/>) does not cover that car. <paramref name="fb"/> holds the finished scene with all of
    /// <paramref name="cars"/>; when some sprite is hidden by a car, <paramref name="renderCars"/> draws just those cars (no track, sky or lines) into a
    /// cleared scratch buffer (their silhouette = pixels with alpha != 0), and the hidden sprites are drawn only outside it.
    /// [deviation] Walls and scenery stay in front of the sprites (they are not available separately), and a hidden sprite is kept out of the pixels
    /// of every occluding car, also those of a farther car it would cover.</summary>
    public void Draw(FrameBuffer fb, Camera cam, ReadOnlySpan<Particle> items, ParticleSprites sprites,
        IReadOnlyList<CarInstance> cars, Action<FrameBuffer, IReadOnlyList<CarInstance>> renderCars)
    {
        _occluders.Clear();
        int nh = 0, nf = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Type == 0) continue;
            bool hidden = false;
            for (int c = 0; c < cars.Count; c++)
            {
                if (!IsHiddenBy(cam, items[i], cars[c])) continue;
                hidden = true;
                if (!Has(_occluders, cars[c])) _occluders.Add(cars[c]);
            }
            if (hidden) { if (nh < _hidden.Length) _hidden[nh++] = items[i]; }
            else if (nf < _free.Length) _free[nf++] = items[i];
        }
        if (nh > 0)
        {
            if (_scratch == null || _scratch.Width != fb.Width || _scratch.Height != fb.Height) _scratch = new FrameBuffer(fb.Width, fb.Height);
            renderCars(_scratch, _occluders);   // silhouette of the occluding cars: alpha != 0
            Draw(fb, cam, _hidden.AsSpan(0, nh), sprites, _scratch.Rgba);
        }
        Draw(fb, cam, _free.AsSpan(0, nf), sprites);
    }

    static Vector3 World(in Vec3 v) => new(v.X / 65536f, v.Y / 65536f, v.Z / 65536f);

    /// <summary>size, frame and shape series per type. [ref:2602]</summary>
    public static (int Size, int Frame) SizeAndFrame(in Particle p)
    {
        int life = p.Life;
        switch (p.Type)
        {
            case 1:
                return ((p.Slot & 3) != 0 ? (16 - life) * 4 + 16 : (16 - life) * 2 + 32, (16 - life >> 1) & 7);
            case 2:
            case 3:
                return ((32 - life) * 2 + 16, (32 - life >> 2) & 7);
            case 4:
            case 5:
                return (0, (8 - life) & 7);
            case 6:
                return ((16 - life) * 2 + 32, (16 - life >> 1) & 7);
            case 7:
            case 8:
                return ((3 - life) * 2 + 16, ((3 - life) >> 1) % 3);
            default:
                return (20 - life, ((4 - life) >> 1) % 3);
        }
    }

    static void DrawOne(FrameBuffer fb, Camera cam, in Particle p, ParticleSprites sprites, byte[]? mask)
    {
        var (size, frame) = SizeAndFrame(p);
        var world = World(p.Pos);
        var view = cam.ToView(world);
        if (p.Type is 4 or 5)
        {
            DrawStreak(fb, cam, p, view, sprites.Sparks[frame], mask);
            return;
        }
        FshImage img; int alpha = 255;
        switch (p.Type)
        {
            case 1: case 2: case 3: img = sprites.Smoke[frame]; alpha = SmokeAlpha; break;
            case 6: img = sprites.Dust[frame]; alpha = DustAlpha; break;
            case 7: img = sprites.Grass[frame]; break;
            case 8: img = sprites.Gravel[frame]; break;
            default: img = sprites.Snow[frame]; break;
        }
        // z is at least 1 m; half-diagonal = size · 10 / z at 640 px (Fx/320 for other widths) [ref:2603]
        float r = size * (float)(cam.Fx / 32.0) / Math.Max(view.Z, 1f);
        var (cx, cy) = cam.Project(view);
        FxMath.SinCosTab(p.Angle & 0x3FF, out int si, out int co);
        float s = si / 65536f * r, c = co / 65536f * r;
        // corners (c4,c8) (bc,c0) (b4,b8) (ac,b0): (+cos, +sin) (+sin, −cos) (−cos, −sin) (−sin, +cos)
        Quad(fb, mask, img, alpha,
            (float)cx + c, (float)cy + s, (float)cx + s, (float)cy - c,
            (float)cx - c, (float)cy - s, (float)cx - s, (float)cy + c);
    }

    /// <summary>in view space; the tail lies 4 m (type 4) or 2 m (type 5) against the velocity, the quad is 0.6 m [ref:2604]
    /// high (4) / 1 m wide (5) and at least 0.5 m long.</summary>
    static void DrawStreak(FrameBuffer fb, Camera cam, in Particle p, Vector3 head, FshImage img, byte[]? mask)
    {
        var vel = World(p.Vel);
        float len = vel.Length();
        var dir = len > 1e-6f ? vel / len : Vector3.Zero;
        var tail = cam.ToView(World(p.Pos) - dir * (p.Type == 4 ? 4f : 2f));
        Vector3 a, b, c, d;
        if (p.Type == 4)
        {
            if (Math.Abs(tail.X - head.X) < 0.5f) tail.X = head.X + (tail.X < head.X ? -0.5f : 0.5f);
            a = head + new Vector3(0, 0.3f, 0); b = tail + new Vector3(0, 0.3f, 0);
            c = tail - new Vector3(0, 0.3f, 0); d = head - new Vector3(0, 0.3f, 0);
        }
        else
        {
            if (Math.Abs(tail.Y - head.Y) < 0.5f) tail.Y = head.Y + (tail.Y < head.Y ? -0.5f : 0.5f);
            a = head + new Vector3(0.5f, 0, 0); b = tail + new Vector3(0.5f, 0, 0);
            c = tail - new Vector3(0.5f, 0, 0); d = head - new Vector3(0.5f, 0, 0);
        }
        float nz = (float)Camera.NearZ;
        if (a.Z < nz || b.Z < nz || c.Z < nz || d.Z < nz) return;
        var (ax, ay) = cam.Project(a); var (bx, by) = cam.Project(b);
        var (cx, cy) = cam.Project(c); var (dx, dy) = cam.Project(d);
        Quad(fb, mask, img, 255, (float)ax, (float)ay, (float)bx, (float)by, (float)cx, (float)cy, (float)dx, (float)dy);
    }

    /// <summary>Textured quad (corners = texture TL, TR, BR, BL) as two affine triangles; colour key = alpha 0 of the image.</summary>
    static void Quad(FrameBuffer fb, byte[]? mask, FshImage img, int alpha, float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        float w = img.Width, h = img.Height;
        Tri(fb, mask, img, alpha, x0, y0, 0, 0, x1, y1, w, 0, x2, y2, w, h, false);
        Tri(fb, mask, img, alpha, x0, y0, 0, 0, x2, y2, w, h, x3, y3, 0, h, true);
    }

    static void Tri(FrameBuffer fb, byte[]? mask, FshImage img, int alpha, float ax, float ay, float au, float av, float bx, float by, float bu, float bv,
        float cx, float cy, float cu, float cv, bool excludeFirstEdge)
    {
        float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (Math.Abs(area) < 1e-4f) return;
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))));
        int maxX = Math.Min(fb.Width - 1, (int)MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(ay, Math.Min(by, cy))));
        int maxY = Math.Min(fb.Height - 1, (int)MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))));
        float inv = 1f / area;
        int tw = img.Width, th = img.Height;
        var rgba = img.Rgba;
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float wb = ((px - ax) * (cy - ay) - (cx - ax) * (py - ay)) * inv;   // weight of B
                float wc = ((bx - ax) * (py - ay) - (px - ax) * (by - ay)) * inv;   // weight of C
                float wa = 1 - wb - wc;
                // the second triangle leaves out the shared diagonal (A–B edge here = opposite of C): drawn once
                if (wa < 0 || wb < 0 || wc < 0 || excludeFirstEdge && wc <= 0) continue;
                int u = Math.Clamp((int)(wa * au + wb * bu + wc * cu), 0, tw - 1);
                int v = Math.Clamp((int)(wa * av + wb * bv + wc * cv), 0, th - 1);
                int o = (v * tw + u) * 4;
                if (rgba[o + 3] == 0 || mask != null && mask[(y * fb.Width + x) * 4 + 3] != 0) continue;
                uint rgb = (uint)(rgba[o] << 16 | rgba[o + 1] << 8 | rgba[o + 2]);
                if (alpha >= 255) fb.Set(x, y, rgb); else fb.Blend(x, y, rgb, alpha);
            }
        }
    }
}
