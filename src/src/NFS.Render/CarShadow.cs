using System.Numerics;

namespace NFS.Render;

/// <summary>
/// Soft shadow under a car (Enhanced): a blob drawn on the road under the car (<see cref="CarInstance.Ground"/>; without it, [ref:3436]
/// on the plane of the tyre contact patch, normal = the car's up) and fading with the height above the road, as a
/// stack of <see cref="Rings"/> nested rounded rectangles each multiplying the pixels below by a constant factor, so the darkness falls off from the
/// centre to the edge. Painter order: it is a z-sort entry just behind the car (<see cref="Bias"/>), after the road polygons around the car and before
/// the car itself. Only the framebuffer RGB is touched (a multiply), the original has no shadows.
/// </summary>
public static class CarShadow
{
    /// <summary>Number of nested outlines (the darkness of the centre = <c>1 − strength</c>, split evenly).</summary>
    public const int Rings = 8;
    /// <summary>Points of an outline.</summary>
    public const int Points = 28;
    /// <summary>Darkness of the centre (0 = none, 1 = black).</summary>
    public const float DefaultStrength = 0.62f;
    /// <summary>z-sort bias: 0.5 m behind the car's own entry (bias 0xA0000), so the road polygons that the car itself is drawn over are drawn before the shadow too.</summary>
    public const int Bias = 0x98000;

    /// <summary>Height of the car's tyre plane above the road at which the shadow is gone (metres); in between the shadow fades out linearly.</summary>
    public const float FadeHeight = 4f;
    /// <summary>How much larger (relatively) the footprint gets at <see cref="FadeHeight"/>: a shadow of a higher object is wider and softer.</summary>
    public const float Spread = 0.35f;

    /// <summary>Where and how the shadow lies: the centre, the in-plane axes along which the half extents are measured and the fade (0..1) of its strength.</summary>
    public readonly record struct Shape(Vector3 Centre, Vector3 Right, Vector3 Forward, float HalfX, float HalfZ, float Fade);

    /// <summary>Shape of the shadow of <paramref name="car"/>. Without <see cref="CarInstance.Ground"/> it lies on the plane of the tyres in the car's own frame
    /// (menu, smackables). With it the blob is projected along the road normal onto the road plane and keeps the heading of the car only (axes = the car's
    /// forward projected on the road, so pitch and roll do not tilt it); the higher the tyre plane above the road, the weaker (and wider) it gets, and from
    /// <see cref="FadeHeight"/> up there is none. False when there is nothing to draw.</summary>
    public static bool TryShape(CarInstance car, out Shape shape)
    {
        shape = default;
        if (car.Model.LocalBounds(car.Lod) is not { } b) return false;
        var mid = (b.Min + b.Max) * 0.5f;
        float hx = (b.Max.X - b.Min.X) * 0.5f * 1.12f + 0.12f;
        float hz = (b.Max.Z - b.Min.Z) * 0.5f * 1.04f + 0.22f;
        // 0.02 m above the plane of the tyres: the road is not perfectly flat
        var tyres = car.ToWorld(new Vector3(mid.X, b.Min.Y + 0.02f, mid.Z));
        // a little towards the shadow side of the sun
        var sunShift = new Vector3(Sun.X, 0, Sun.Z) * -0.18f;
        if (car.Ground is not { } g)
        {
            shape = new Shape(tyres + sunShift, car.Right, car.Forward, hx, hz, 1f);
            return true;
        }
        var n = g.Normal;
        float h = Math.Max(0f, Vector3.Dot(tyres - g.Point, n) - 0.02f);   // height of the tyre plane above the road
        float t = h / FadeHeight;
        if (t >= 1f) return false;
        var fwd = car.Forward - n * Vector3.Dot(car.Forward, n);
        if (fwd.LengthSquared() < 0.04f) fwd = car.Up - n * Vector3.Dot(car.Up, n);   // nose (nearly) straight up/down: the footprint is symmetric, the roof points along the heading
        if (fwd.LengthSquared() < 1e-6f) return false;
        fwd = Vector3.Normalize(fwd);
        var right = Vector3.Normalize(Vector3.Cross(n, fwd));
        var centre = tyres - n * h + (sunShift - n * Vector3.Dot(sunShift, n));
        float grow = 1f + Spread * t;
        shape = new Shape(centre, right, fwd, hx * grow, hz * grow, 1f - t);
        return true;
    }

    /// <summary>Footprint centre (world) and the half extents along the shape's axes; see <see cref="TryShape"/>. False when there is no shadow.</summary>
    public static bool Footprint(CarInstance car, out Vector3 centre, out float halfX, out float halfZ)
    {
        bool ok = TryShape(car, out var s);
        centre = s.Centre; halfX = s.HalfX; halfZ = s.HalfZ;
        return ok;
    }

    static Vector3 Sun => CarLighting.Sun;

    /// <summary>World outline of the ring of relative size <paramref name="scale"/> (1 = the footprint): a superellipse (|x|^2.6 + |z|^2.6 = 1), convex.</summary>
    public static void Outline(in Shape shape, float scale, Span<Vector3> world)
    {
        for (int i = 0; i < world.Length; i++)
        {
            double a = 2 * Math.PI * i / world.Length, c = Math.Cos(a), s = Math.Sin(a);
            float x = (float)(Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / 2.6)), z = (float)(Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / 2.6));
            world[i] = shape.Centre + shape.Right * (x * shape.HalfX * scale) + shape.Forward * (z * shape.HalfZ * scale);
        }
    }

    /// <summary>Draws the shadow of <paramref name="car"/>; <paramref name="fog"/> (0..16) fades it with the distance like the fog hides the car.
    /// Returns 1 when something was drawn.</summary>
    public static int Draw(FrameBuffer fb, Camera cam, CarInstance car, int fog, float strength = DefaultStrength)
    {
        if (!TryShape(car, out var shape)) return 0;
        strength *= Math.Clamp(1f - fog / 16f, 0f, 1f) * shape.Fade;
        if (strength <= 0.01f) return 0;
        // each ring multiplies by f, all rings together give 1 − strength in the centre
        double f = Math.Pow(1 - strength, 1.0 / Rings);
        int mul = (int)Math.Round(f * 256);
        Span<Vector3> world = stackalloc Vector3[Points];
        Span<ScreenVertex> clipped = stackalloc ScreenVertex[Points * 2];
        int drawn = 0;
        for (int r = 0; r < Rings; r++)
        {
            Outline(shape, 1f - 0.82f * r / Rings, world);
            int n = ClipProject(cam, world, clipped);
            if (n < 3) continue;
            Multiply(fb, clipped[..n], mul);
            drawn = 1;
        }
        return drawn;
    }

    /// <summary>World ring → view space → near clip (z ≥ 0.1) → screen.</summary>
    static int ClipProject(Camera cam, ReadOnlySpan<Vector3> world, Span<ScreenVertex> output)
    {
        const float nz = 0.1f;
        int n = 0, count = world.Length;
        Vector3 prev = cam.ToView(world[count - 1]);
        for (int i = 0; i < count; i++)
        {
            var cur = cam.ToView(world[i]);
            bool pin = prev.Z >= nz, cin = cur.Z >= nz;
            if (pin != cin)
            {
                float t = (nz - prev.Z) / (cur.Z - prev.Z);
                var (x, y) = cam.Project(Vector3.Lerp(prev, cur, t));
                output[n++] = new ScreenVertex(x, y, 0, 0);
            }
            if (cin)
            {
                var (x, y) = cam.Project(cur);
                output[n++] = new ScreenVertex(x, y, 0, 0);
            }
            prev = cur;
        }
        return n;
    }

    /// <summary>Multiplies the RGB of the pixels covered by the convex polygon (top-left rule of <see cref="Rasterizer.Fill"/>) by <paramref name="mul"/>/256.</summary>
    public static void Multiply(FrameBuffer fb, ReadOnlySpan<ScreenVertex> p, int mul, int yMin = 0, int yMax = int.MaxValue)
    {
        if (FillRecorder.Active is { } rec && rec.Target == fb) { rec.AddMultiply(p, mul); return; }
        int n = p.Length;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++) { minY = Math.Min(minY, p[i].Y); maxY = Math.Max(maxY, p[i].Y); }
        int y0 = Math.Max(Math.Max(0, yMin), (int)Math.Ceiling(minY - 0.5));
        int y1 = Math.Min(Math.Min(fb.Height, yMax) - 1, (int)Math.Ceiling(maxY - 0.5) - 1);
        var px = fb.Rgba;
        for (int y = y0; y <= y1; y++)
        {
            double sy = y + 0.5, xl = double.MaxValue, xr = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                var a = p[i]; var b = p[i + 1 == n ? 0 : i + 1];
                if (a.Y == b.Y) continue;
                if ((sy < a.Y || sy >= b.Y) && (sy < b.Y || sy >= a.Y)) continue;
                double x = a.X + (b.X - a.X) * ((sy - a.Y) / (b.Y - a.Y));
                if (x < xl) xl = x;
                if (x > xr) xr = x;
            }
            if (xr < xl) continue;
            int x0 = Math.Max(0, (int)Math.Ceiling(xl - 0.5)), x1 = Math.Min(fb.Width - 1, (int)Math.Ceiling(xr - 0.5) - 1);
            for (int x = x0, o = (y * fb.Width + x0) * 4; x <= x1; x++, o += 4)
            {
                px[o] = (byte)(px[o] * mul >> 8); px[o + 1] = (byte)(px[o + 1] * mul >> 8); px[o + 2] = (byte)(px[o + 2] * mul >> 8);
            }
        }
    }
}
