using NFS.Render;

namespace NFS.Touch;

/// <summary>Software raster of the on-screen controls: semi-transparent anti-aliased shapes into a
/// <b>premultiplied</b> RGBA buffer (what <c>SpriteBatch</c> blends with its default state). Transparent where nothing is drawn.
/// Deterministic and independent of any window, so tests can write it to PNG.</summary>
public sealed class TouchOverlay
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Premultiplied RGBA, row-major, 4 bytes per pixel.</summary>
    public byte[] Pixels { get; }

    const uint Idle = 0xFFFFFF, Pressed = 0xFFD020, Accent = 0xFF5030, Dark = 0x101820;

    /// <summary>Clip rectangle of the primitives (x0, y0 inclusive; x1, y1 exclusive); the whole buffer outside <see cref="RedrawSteer"/>.</summary>
    int _cx0, _cy0, _cx1, _cy1;

    public TouchOverlay(int width, int height)
    {
        Width = width; Height = height; Pixels = new byte[width * height * 4];
        ResetClip();
    }

    void ResetClip() => (_cx0, _cy0, _cx1, _cy1) = (0, 0, Width, Height);

    public void Clear() => Array.Clear(Pixels);

    /// <summary>Draws the whole control set for <paramref name="layout"/> (buffer size = layout size).</summary>
    public void Draw(TouchLayout layout, TouchControls state)
    {
        Clear();
        DrawControls(layout, state);
    }

    /// <summary>Only the steering knob moved (same layout and held buttons as the last <see cref="Draw"/>): redraws just the
    /// area of the old and the new knob – the same pixels a full <see cref="Draw"/> with <paramref name="state"/> gives – and
    /// returns that area (x, y, w, h) for a partial texture upload. Empty when there is no steering bar.</summary>
    public (int X, int Y, int W, int H) RedrawSteer(TouchLayout layout, TouchControls state, float previousSteer)
    {
        if (layout.Steer is not { } zone) return default;
        var a = KnobBounds(zone, previousSteer, layout.Unit);
        var b = KnobBounds(zone, state.Steer, layout.Unit);
        return RedrawArea(layout, state, Union(a, b));
    }

    /// <summary>The state changed from <paramref name="previous"/> to <paramref name="state"/> (same layout as the last full
    /// <see cref="Draw"/>): redraws only what changed – each button whose held state flipped and the knob if it moved – giving
    /// the same pixels as a full <see cref="Draw"/>. Returns the redrawn areas (x, y, w, h) for partial texture uploads,
    /// written to <paramref name="areas"/> (at most one per control plus one for the knob); the count is returned, or -1 (nothing
    /// drawn) when <paramref name="areas"/> is too small – the caller then does a full <see cref="Draw"/>.</summary>
    public int Redraw(TouchLayout layout, TouchControls state, TouchControls previous, Span<(int X, int Y, int W, int H)> areas)
    {
        var flipped = state.Held ^ previous.Held;
        int need = state.Steer != previous.Steer && layout.Steer != null ? 1 : 0;
        foreach (var c in layout.Controls) if ((flipped & c.Id) != 0) need++;
        if (need > areas.Length) return -1;
        int n = 0;
        if (state.Steer != previous.Steer && layout.Steer is { } zone)
        {
            var area = RedrawArea(layout, state, Union(KnobBounds(zone, previous.Steer, layout.Unit), KnobBounds(zone, state.Steer, layout.Unit)));
            if (area.W > 0) areas[n++] = area;
        }
        if (flipped != TouchButtons.None)
            foreach (var c in layout.Controls)
                if ((flipped & c.Id) != 0)
                {
                    var area = RedrawArea(layout, state, c.Rect);
                    if (area.W > 0) areas[n++] = area;
                }
        return n;
    }

    static RectF Union(RectF a, RectF b)
    {
        float x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        return new RectF(x0, y0, Math.Max(a.Right, b.Right) - x0, Math.Max(a.Bottom, b.Bottom) - y0);
    }

    /// <summary>Clears <paramref name="r"/> (plus a 3 px margin for anti-aliasing and strokes) and redraws every primitive
    /// reaching into it, clipped; returns the pixel area.</summary>
    (int X, int Y, int W, int H) RedrawArea(TouchLayout layout, TouchControls state, RectF r)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(r.X) - 3), y0 = Math.Max(0, (int)MathF.Floor(r.Y) - 3);
        int x1 = Math.Min(Width, (int)MathF.Ceiling(r.Right) + 3), y1 = Math.Min(Height, (int)MathF.Ceiling(r.Bottom) + 3);
        if (x1 <= x0 || y1 <= y0) return default;
        for (int y = y0; y < y1; y++) Array.Clear(Pixels, (y * Width + x0) * 4, (x1 - x0) * 4);
        (_cx0, _cy0, _cx1, _cy1) = (x0, y0, x1, y1);
        try { DrawControls(layout, state); }
        finally { ResetClip(); }
        return (x0, y0, x1 - x0, y1 - y0);
    }

    static RectF KnobBounds(SteerZone z, float steer, float u)
    {
        var t = z.Track;
        float knobR = KnobRadius(u), kx = KnobX(t, steer, knobR);
        return new RectF(kx - knobR, t.CenterY - knobR, 2 * knobR, 2 * knobR);
    }

    static float KnobRadius(float u) => 0.095f * u;
    static float KnobX(RectF t, float steer, float knobR) => t.CenterX + Math.Clamp(steer, -1f, 1f) * (t.W / 2 - knobR * 0.6f);

    /// <summary>Does <paramref name="r"/> (plus the 1 px anti-aliasing fringe) reach into the clip rectangle?</summary>
    bool InClip(RectF r) => r.Right + 2 > _cx0 && r.X - 2 < _cx1 && r.Bottom + 2 > _cy0 && r.Y - 2 < _cy1;

    void DrawControls(TouchLayout layout, TouchControls state)
    {
        if (layout.Steer is { } zone && InClip(zone.Track)) DrawSteer(zone, state.Steer, layout.Unit);
        foreach (var c in layout.Controls)
        {
            if (!InClip(c.Rect)) continue;
            bool down = state.Has(c.Id);
            uint colour = down ? Pressed : c.Id == TouchButtons.Brake || c.Id == TouchButtons.Handbrake ? Accent : Idle;
            float fill = down ? 0.55f : 0.20f;
            var r = c.Rect;
            float rad = c.Shape == TouchShape.Circle ? Math.Min(r.W, r.H) / 2 : 0.18f * Math.Min(r.W, r.H);
            FillRoundRect(r, rad, Dark, 0.35f);
            FillRoundRect(r, rad, colour, fill);
            StrokeRoundRect(r, rad, Math.Max(2f, 0.012f * layout.Unit), colour, down ? 0.95f : 0.65f);
            if (c.Arrow != ' ') DrawArrow(r, c.Arrow, colour, down ? 1f : 0.85f);
            else if (c.Label.Length > 0)
            {
                int scale = Math.Max(1, (int)MathF.Floor(Math.Min(r.H * 0.40f / 5f, r.W * 0.86f / Math.Max(1, c.Label.Length * 4 - 1))));
                DrawLabel(r.CenterX, r.CenterY, c.Label, scale, down ? Pressed : Idle, down ? 1f : 0.9f);
            }
        }
    }

    void DrawSteer(SteerZone z, float steer, float u)
    {
        var t = z.Track;
        float rad = t.H / 2;
        FillRoundRect(t, rad, Dark, 0.35f);
        FillRoundRect(t, rad, Idle, 0.12f);
        StrokeRoundRect(t, rad, Math.Max(2f, 0.012f * u), Idle, 0.55f);
        // centre mark and end ticks
        FillRoundRect(new RectF(t.CenterX - 0.004f * u, t.Y + 0.2f * t.H, 0.008f * u, 0.6f * t.H), 0, Idle, 0.5f);
        DrawArrow(new RectF(t.X + 0.04f * u, t.CenterY - 0.05f * u, 0.1f * u, 0.1f * u), '<', Idle, 0.6f);
        DrawArrow(new RectF(t.Right - 0.14f * u, t.CenterY - 0.05f * u, 0.1f * u, 0.1f * u), '>', Idle, 0.6f);
        float knobR = KnobRadius(u);
        float kx = KnobX(t, steer, knobR);
        bool active = steer != 0;
        FillCircle(kx, t.CenterY, knobR, Dark, 0.4f);
        FillCircle(kx, t.CenterY, knobR, active ? Pressed : Idle, active ? 0.65f : 0.35f);
        Ring(kx, t.CenterY, knobR, Math.Max(2f, 0.012f * u), active ? Pressed : Idle, 0.9f);
    }

    // ---- primitives (signed-distance coverage; premultiplied source-over) ----

    void Plot(int x, int y, uint rgb, float alpha)
    {
        if (alpha <= 0f || x < _cx0 || x >= _cx1 || y < _cy0 || y >= _cy1) return;
        alpha = Math.Min(1f, alpha);
        int o = (y * Width + x) * 4;
        float inv = 1f - alpha;
        Pixels[o] = (byte)MathF.Round((rgb >> 16 & 0xFF) * alpha + Pixels[o] * inv);
        Pixels[o + 1] = (byte)MathF.Round((rgb >> 8 & 0xFF) * alpha + Pixels[o + 1] * inv);
        Pixels[o + 2] = (byte)MathF.Round((rgb & 0xFF) * alpha + Pixels[o + 2] * inv);
        Pixels[o + 3] = (byte)MathF.Round(255f * alpha + Pixels[o + 3] * inv);
    }

    delegate float Sdf(float x, float y);

    void Fill(RectF bounds, Sdf sdf, uint rgb, float alpha)
    {
        int x0 = Math.Max(_cx0, (int)MathF.Floor(bounds.X) - 1), x1 = Math.Min(_cx1 - 1, (int)MathF.Ceiling(bounds.Right) + 1);
        int y0 = Math.Max(_cy0, (int)MathF.Floor(bounds.Y) - 1), y1 = Math.Min(_cy1 - 1, (int)MathF.Ceiling(bounds.Bottom) + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float cov = Math.Clamp(0.5f - sdf(x + 0.5f, y + 0.5f), 0f, 1f);
                if (cov > 0) Plot(x, y, rgb, cov * alpha);
            }
    }

    static Sdf RoundRectSdf(RectF r, float rad)
    {
        rad = Math.Min(rad, Math.Min(r.W, r.H) / 2);
        float hx = r.W / 2 - rad, hy = r.H / 2 - rad, cx = r.CenterX, cy = r.CenterY;
        return (x, y) =>
        {
            float dx = MathF.Max(MathF.Abs(x - cx) - hx, 0), dy = MathF.Max(MathF.Abs(y - cy) - hy, 0);
            float outside = MathF.Sqrt(dx * dx + dy * dy);
            float inside = MathF.Min(MathF.Max(MathF.Abs(x - cx) - hx, MathF.Abs(y - cy) - hy), 0);
            return outside + inside - rad;
        };
    }

    public void FillRoundRect(RectF r, float rad, uint rgb, float alpha) => Fill(r, RoundRectSdf(r, rad), rgb, alpha);

    public void StrokeRoundRect(RectF r, float rad, float thickness, uint rgb, float alpha)
    {
        var d = RoundRectSdf(r, rad);
        Fill(r, (x, y) => MathF.Abs(d(x, y) + thickness / 2) - thickness / 2, rgb, alpha);
    }

    public void FillCircle(float cx, float cy, float rad, uint rgb, float alpha) =>
        Fill(new RectF(cx - rad, cy - rad, 2 * rad, 2 * rad), (x, y) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - rad, rgb, alpha);

    public void Ring(float cx, float cy, float rad, float thickness, uint rgb, float alpha) =>
        Fill(new RectF(cx - rad, cy - rad, 2 * rad, 2 * rad),
            (x, y) => MathF.Abs(MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - rad + thickness / 2) - thickness / 2, rgb, alpha);

    /// <summary>Filled triangle arrow ('^' 'v' '&lt;' '&gt;') inscribed in 60% of <paramref name="r"/>, 4×4 supersampled.</summary>
    public void DrawArrow(RectF r, char dir, uint rgb, float alpha)
    {
        float s = 0.6f * Math.Min(r.W, r.H), cx = r.CenterX, cy = r.CenterY, h = s / 2;
        (float X, float Y)[] p = dir switch
        {
            '^' => [(cx, cy - h), (cx - h, cy + h * 0.8f), (cx + h, cy + h * 0.8f)],
            'v' => [(cx, cy + h), (cx - h, cy - h * 0.8f), (cx + h, cy - h * 0.8f)],
            '<' => [(cx - h, cy), (cx + h * 0.8f, cy - h), (cx + h * 0.8f, cy + h)],
            _ => [(cx + h, cy), (cx - h * 0.8f, cy - h), (cx - h * 0.8f, cy + h)],
        };
        const int N = 4;
        for (int y = (int)MathF.Floor(cy - h) - 1; y <= (int)MathF.Ceiling(cy + h) + 1; y++)
            for (int x = (int)MathF.Floor(cx - h) - 1; x <= (int)MathF.Ceiling(cx + h) + 1; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < N; sy++)
                    for (int sx = 0; sx < N; sx++)
                        if (InTriangle(x + (sx + 0.5f) / N, y + (sy + 0.5f) / N, p)) hit++;
                if (hit > 0) Plot(x, y, rgb, alpha * hit / (N * N));
            }
    }

    static bool InTriangle(float x, float y, (float X, float Y)[] p)
    {
        float d1 = Cross(x, y, p[0], p[1]), d2 = Cross(x, y, p[1], p[2]), d3 = Cross(x, y, p[2], p[0]);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    static float Cross(float x, float y, (float X, float Y) a, (float X, float Y) b) => (x - b.X) * (a.Y - b.Y) - (a.X - b.X) * (y - b.Y);

    /// <summary>Centred label in the HUD's 3×5 font: glyph coverage comes from a white-on-black scratch buffer.</summary>
    public void DrawLabel(float cx, float cy, string text, int scale, uint rgb, float alpha)
    {
        int tw = HudOverlay.TextWidth(text, scale), th = 5 * scale;
        var fb = new FrameBuffer(tw + 2, th + 2);
        fb.Clear(0, 0, 0);
        HudOverlay.DrawText(fb, 0, 0, text, 0xFFFFFF, scale);
        int ox = (int)MathF.Round(cx - tw / 2f), oy = (int)MathF.Round(cy - th / 2f);
        for (int y = 0; y < fb.Height; y++)
            for (int x = 0; x < fb.Width; x++)
            {
                float v = fb.Rgba[(y * fb.Width + x) * 4] / 255f;
                if (v > 0) Plot(ox + x, oy + y, rgb, alpha * v);
            }
    }

    /// <summary>The overlay composited over an opaque background (RGBA, e.g. a frame) – used for the review PNG.</summary>
    public byte[] CompositeOver(ReadOnlySpan<byte> backgroundRgba)
    {
        var o = new byte[Pixels.Length];
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            int a = Pixels[i + 3], inv = 255 - a;
            o[i] = (byte)Math.Min(255, Pixels[i] + (backgroundRgba[i] * inv + 127) / 255);
            o[i + 1] = (byte)Math.Min(255, Pixels[i + 1] + (backgroundRgba[i + 1] * inv + 127) / 255);
            o[i + 2] = (byte)Math.Min(255, Pixels[i + 2] + (backgroundRgba[i + 2] * inv + 127) / 255);
            o[i + 3] = 255;
        }
        return o;
    }
}
