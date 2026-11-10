namespace NFS.Touch;

/// <summary>One active finger (contact down or moving) in overlay pixels; <see cref="Id"/> is stable while the finger stays down.</summary>
public readonly record struct TouchPoint(int Id, float X, float Y);

/// <summary>Which on-screen set is shown: the race controls, the menu navigation pad, or nothing.</summary>
public enum TouchMode { Hidden, Race, Menu }

/// <summary>Every on-screen button (race and menu sets).</summary>
[Flags]
public enum TouchButtons
{
    None = 0,
    Gas = 1 << 0, Brake = 1 << 1, Handbrake = 1 << 2, GearUp = 1 << 3, GearDown = 1 << 4,
    Camera = 1 << 5, LookBehind = 1 << 6, Hud = 1 << 7, Reset = 1 << 8, Horn = 1 << 9, Pause = 1 << 10,
    MenuUp = 1 << 11, MenuDown = 1 << 12, MenuLeft = 1 << 13, MenuRight = 1 << 14, MenuOk = 1 << 15, MenuBack = 1 << 16,
    Options = 1 << 17,
}

/// <summary>State of the touch controls in one frame: held buttons plus the analog steering (-1 left … +1 right).</summary>
public readonly record struct TouchControls(float Steer, TouchButtons Held)
{
    public static TouchControls None => default;
    public bool Has(TouchButtons b) => (Held & b) == b;
    /// <summary>Steering as the pad's analog axis (-127..127, as <c>DriveKeys.AnalogSteer</c>).</summary>
    public sbyte SteerByte => (sbyte)Math.Clamp((int)MathF.Round(Steer * 127f), -127, 127);
}

public readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public float CenterX => X + W / 2;
    public float CenterY => Y + H / 2;
    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;
}

public enum TouchShape { RoundRect, Circle }

/// <summary>A button: id, hit/draw rectangle, label; <see cref="Arrow"/> (menu pad: ^ v &lt; &gt;) is drawn instead of text.</summary>
public sealed record TouchControl(TouchButtons Id, RectF Rect, string Label, TouchShape Shape = TouchShape.RoundRect, char Arrow = ' ');

/// <summary>The left/right steering joystick: a horizontal track (<see cref="Track"/>), whose centre is zero and whose
/// ends are full lock; a finger that went down in <see cref="Hit"/> steers until it is lifted, even outside the zone.</summary>
public sealed record SteerZone(RectF Hit, RectF Track)
{
    public const float DeadZone = 0.06f;
    /// <summary>Gain over the plain linear mapping: full lock is reached at 1/1.5 of the track half-width (players found the
    /// whole half-width too long a throw).</summary>
    public const float Sensitivity = 1.5f;

    /// <summary>Steering for a finger at <paramref name="x"/>: linear (times <see cref="Sensitivity"/>) over the track half-width,
    /// dead zone in the middle, rescaled so that the end of the dead zone starts at 0; clamped to full lock.</summary>
    public float ValueAt(float x)
    {
        float v = (x - Track.CenterX) / (Track.W / 2);
        float a = MathF.Abs(v);
        if (a <= DeadZone) return 0;
        return MathF.Sign(v) * Math.Min(1f, Sensitivity * (a - DeadZone) / (1f - DeadZone));
    }
}

/// <summary>Layout of the controls for a screen of the given size. Everything is derived from one unit
/// <c>U = min(height, width / 1.9)</c>, so a narrow (4:3) screen shrinks the controls instead of overlapping them.</summary>
public sealed class TouchLayout
{
    public int Width { get; }
    public int Height { get; }
    public TouchMode Mode { get; }
    public float Unit { get; }
    public IReadOnlyList<TouchControl> Controls { get; }
    public SteerZone? Steer { get; }

    TouchLayout(int w, int h, TouchMode mode, float u, List<TouchControl> controls, SteerZone? steer)
    { Width = w; Height = h; Mode = mode; Unit = u; Controls = controls; Steer = steer; }

    public static TouchLayout Create(int width, int height, TouchMode mode)
    {
        float u = Math.Max(1, Math.Min(height, width / 1.9f));
        var c = new List<TouchControl>();
        SteerZone? steer = null;
        float m = 0.04f * u;
        float W = width, H = height;
        switch (mode)
        {
            case TouchMode.Race:
            {
                // Left: steering track (pill) and the generous hit zone around it.
                float pillW = 0.70f * u, pillH = 0.26f * u;
                var track = new RectF(m, H - m - pillH - 0.04f * u, pillW, pillH);
                steer = new SteerZone(new RectF(0, H - 0.62f * u, track.Right + 0.12f * u, 0.62f * u), track);
                // Right: gas (tall), brake/reverse and handbrake, gears above the gas.
                float gasW = 0.30f * u, gasH = 0.46f * u;
                var gas = new RectF(W - m - gasW, H - m - gasH, gasW, gasH);
                float brW = 0.26f * u, brH = 0.30f * u, gap = 0.04f * u;
                var brake = new RectF(gas.X - gap - brW, H - m - brH, brW, brH);
                var hand = new RectF(brake.X, brake.Y - gap - 0.18f * u, brW, 0.18f * u);
                float gs = 0.145f * u;
                var gearUp = new RectF(gas.X, gas.Y - gap - gs, gs, gs);
                var gearDown = new RectF(gas.Right - gs, gas.Y - gap - gs, gs, gs);
                c.Add(new(TouchButtons.Gas, gas, "GAS"));
                c.Add(new(TouchButtons.Brake, brake, "BRAKE"));
                c.Add(new(TouchButtons.Handbrake, hand, "HAND"));
                c.Add(new(TouchButtons.GearUp, gearUp, "+"));
                c.Add(new(TouchButtons.GearDown, gearDown, "-"));
                // Top bar: camera, look behind, HUD, reset, horn; the pause/menu button in the corner.
                float bw = 0.22f * u, bh = 0.12f * u, g = 0.02f * u;
                var top = new (TouchButtons Id, string Label)[]
                {
                    (TouchButtons.Camera, "CAM"), (TouchButtons.LookBehind, "LOOK"), (TouchButtons.Hud, "HUD"),
                    (TouchButtons.Reset, "RESET"), (TouchButtons.Horn, "HORN"),
                };
                float x0 = (W - (top.Length * bw + (top.Length - 1) * g)) / 2;
                for (int i = 0; i < top.Length; i++)
                    c.Add(new(top[i].Id, new RectF(x0 + i * (bw + g), m, bw, bh), top[i].Label));
                c.Add(new(TouchButtons.Pause, new RectF(W - m - 0.2f * u, m, 0.2f * u, bh), "MENU"));
                break;
            }
            case TouchMode.Menu:
            {
                // Left: cross; right: OK (big) and BACK.
                float b = 0.20f * u, g = 0.02f * u;
                float cx = m + 0.04f * u + b * 1.5f + g, cy = H - m - 0.04f * u - b * 1.5f - g;
                c.Add(new(TouchButtons.MenuUp, new RectF(cx - b / 2, cy - b * 1.5f - g, b, b), "", Arrow: '^'));
                c.Add(new(TouchButtons.MenuDown, new RectF(cx - b / 2, cy + b / 2 + g, b, b), "", Arrow: 'v'));
                c.Add(new(TouchButtons.MenuLeft, new RectF(cx - b * 1.5f - g, cy - b / 2, b, b), "", Arrow: '<'));
                c.Add(new(TouchButtons.MenuRight, new RectF(cx + b / 2 + g, cy - b / 2, b, b), "", Arrow: '>'));
                float ok = 0.32f * u;
                c.Add(new(TouchButtons.MenuOk, new RectF(W - m - ok, H - m - 0.04f * u - ok, ok, ok), "OK", TouchShape.Circle));
                float bk = 0.20f * u;
                c.Add(new(TouchButtons.MenuBack, new RectF(W - m - ok - 0.05f * u - bk, H - m - 0.04f * u - bk, bk, bk), "BACK", TouchShape.Circle));
                // Top right corner (where MENU sits in a race): the port options screen (graphics), far from the cross, OK and BACK.
                c.Add(new(TouchButtons.Options, new RectF(W - m - 0.2f * u, m, 0.2f * u, 0.12f * u), "GFX"));
                break;
            }
        }
        return new TouchLayout(width, height, mode, u, c, steer);
    }

    public TouchControl? Find(TouchButtons id) => Controls.FirstOrDefault(x => x.Id == id);
}

/// <summary>Multitouch → <see cref="TouchControls"/>. Stateful only for the steering finger: the one that went down in the
/// steering zone keeps steering after it leaves the zone, until it is lifted. Every other finger presses the button under it
/// (several buttons may be held at once; one finger holds at most one button, re-evaluated every frame, so sliding from
/// GAS to BRAKE switches pedals).</summary>
public sealed class TouchTracker
{
    int _steerId = -1;

    public void Reset() => _steerId = -1;

    public TouchControls Update(TouchLayout layout, IReadOnlyList<TouchPoint> points)
    {
        if (layout.Mode == TouchMode.Hidden) { _steerId = -1; return TouchControls.None; }
        if (_steerId >= 0 && !points.Any(p => p.Id == _steerId)) _steerId = -1;
        float steer = 0;
        var held = TouchButtons.None;
        // Steering finger first, so it never also presses a button.
        if (layout.Steer is { } zone)
        {
            if (_steerId < 0)
                foreach (var p in points)
                    if (zone.Hit.Contains(p.X, p.Y) && !HitsButton(layout, p)) { _steerId = p.Id; break; }
            foreach (var p in points)
                if (p.Id == _steerId) steer = zone.ValueAt(p.X);
        }
        foreach (var p in points)
        {
            if (p.Id == _steerId) continue;
            foreach (var ctl in layout.Controls)
                if (ctl.Rect.Contains(p.X, p.Y)) { held |= ctl.Id; break; }
        }
        return new TouchControls(steer, held);
    }

    static bool HitsButton(TouchLayout layout, TouchPoint p) => layout.Controls.Any(c => c.Rect.Contains(p.X, p.Y));
}
