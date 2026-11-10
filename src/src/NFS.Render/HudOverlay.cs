namespace NFS.Render;

/// <summary>HUD values for one frame (independent of NFS.Game). Speed in m/s 16.16, gear 0 = R, 1 = N, 2.. = 1st..</summary>
public readonly record struct HudState(int SpeedFx, int Gear, int Lap, int NumLaps, int Place, int NumCars, int RaceTicks64, bool Metric);

/// <summary>
/// Minimal race HUD drawn directly onto a <see cref="FrameBuffer"/> with a built-in 3×5 bitmap font (stand-in for the
/// original dashhud / FSH font shapes, not yet decoded): speed (km/h or mph), gear, lap x/y, position x/n, race time.
/// </summary>
public static class HudOverlay
{
    public const int Scale = 3;
    public const uint TextColor = 0xFFFFFF, ShadowColor = 0x000000;

    // 3×5 glyphs, rows top→bottom, bit 2 = left column.
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = [7, 5, 5, 5, 7], ['1'] = [2, 6, 2, 2, 7], ['2'] = [7, 1, 7, 4, 7], ['3'] = [7, 1, 7, 1, 7],
        ['4'] = [5, 5, 7, 1, 1], ['5'] = [7, 4, 7, 1, 7], ['6'] = [7, 4, 7, 5, 7], ['7'] = [7, 1, 1, 1, 1],
        ['8'] = [7, 5, 7, 5, 7], ['9'] = [7, 5, 7, 1, 7], ['/'] = [1, 1, 2, 4, 4], [':'] = [0, 2, 0, 2, 0],
        ['.'] = [0, 0, 0, 0, 2], [' '] = [0, 0, 0, 0, 0], ['-'] = [0, 0, 7, 0, 0], ['_'] = [0, 0, 0, 0, 7],
        ['['] = [7, 4, 4, 4, 7], [']'] = [7, 1, 1, 1, 7],
        ['A'] = [2, 5, 7, 5, 5], ['E'] = [7, 4, 6, 4, 7], ['G'] = [7, 4, 5, 5, 7], ['H'] = [5, 5, 7, 5, 5],
        ['K'] = [5, 5, 6, 5, 5], ['L'] = [4, 4, 4, 4, 7], ['M'] = [5, 7, 7, 5, 5], ['N'] = [6, 5, 5, 5, 5],
        ['O'] = [7, 5, 5, 5, 7], ['P'] = [7, 5, 7, 4, 4], ['R'] = [6, 5, 6, 5, 5], ['S'] = [7, 4, 7, 1, 7],
        ['T'] = [7, 2, 2, 2, 2], ['I'] = [7, 2, 2, 2, 7],
        ['B'] = [6, 5, 6, 5, 6], ['C'] = [7, 4, 4, 4, 7], ['D'] = [6, 5, 5, 5, 6],
        ['F'] = [7, 4, 6, 4, 4], ['J'] = [1, 1, 1, 5, 7], ['Q'] = [7, 5, 5, 7, 1],
        ['U'] = [5, 5, 5, 5, 7], ['V'] = [5, 5, 5, 5, 2], ['W'] = [5, 5, 7, 7, 5],
        ['X'] = [5, 5, 2, 5, 5], ['Y'] = [5, 5, 2, 2, 2], ['Z'] = [7, 1, 2, 4, 7], ['='] = [0, 7, 0, 7, 0],
        ['>'] = [4, 2, 1, 2, 4], ['<'] = [1, 2, 4, 2, 1], ['('] = [2, 4, 4, 4, 2], [')'] = [2, 1, 1, 1, 2], [','] = [0, 0, 0, 2, 4],
        ['!'] = [2, 2, 2, 0, 2], ['?'] = [7, 1, 3, 0, 2], ['+'] = [0, 2, 7, 2, 0], ['*'] = [5, 2, 7, 2, 5], ['"'] = [5, 5, 0, 0, 0],
        ['\''] = [2, 2, 0, 0, 0], ['%'] = [5, 1, 2, 4, 5], ['#'] = [5, 7, 5, 7, 5],
    };

    /// <summary>Width in pixels of <paramref name="text"/> at the given scale (4 columns per glyph incl. spacing).</summary>
    public static int TextWidth(string text, int scale = Scale) => text.Length * 4 * scale - scale;

    /// <summary>Draws text with a 1-pixel drop shadow; unknown characters are blank. <paramref name="alpha"/> blends the
    /// glyphs and their shadow with the scene underneath (255 = opaque, 0 = invisible).</summary>
    public static void DrawText(FrameBuffer fb, int x, int y, string text, uint color = TextColor, int scale = Scale, int alpha = 255)
    {
        DrawRaw(fb, x + 1, y + 1, text, ShadowColor, scale, alpha);
        DrawRaw(fb, x, y, text, color, scale, alpha);
    }

    private static void DrawRaw(FrameBuffer fb, int x, int y, string text, uint color, int scale, int alpha)
    {
        foreach (char ch in text.ToUpperInvariant())
        {
            if (Glyphs.TryGetValue(ch, out var g))
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if ((g[r] >> (2 - c) & 1) != 0)
                            for (int dy = 0; dy < scale; dy++)
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    int px = x + c * scale + dx, py = y + r * scale + dy;
                                    if ((uint)px >= (uint)fb.Width || (uint)py >= (uint)fb.Height) continue;
                                    if (alpha >= 255) fb.Set(px, py, color);
                                    else if (alpha > 0) fb.Blend(px, py, color, alpha);
                                }
            x += 4 * scale;
        }
    }

    public static string SpeedText(HudState s)
    {
        long v = Math.Abs((long)s.SpeedFx);
        // m/s → km/h ×3.6, → mph ×2.23694 (rounded)
        long shown = s.Metric ? (v * 36 + 5 * 65536) / (10 * 65536) : (v * 223694 + 50000L * 65536) / (100000L * 65536);
        return $"{shown} {(s.Metric ? "KMH" : "MPH")}";
    }

    public static string GearText(int gear) => gear switch { 0 => "R", 1 => "N", _ => (gear - 1).ToString() };

    /// <summary>Race time m:ss.hh from 64 Hz ticks.</summary>
    public static string TimeText(int ticks64)
    {
        int t = Math.Max(0, ticks64), cs = t * 100 / 64;
        return $"{cs / 6000}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    /// <summary>Layout: top-left lap, top-right position, top-centre time, bottom-right speed + gear.</summary>
    public static void Draw(FrameBuffer fb, HudState s)
    {
        int m = 8;
        DrawText(fb, m, m, $"LAP {Math.Min(s.Lap, s.NumLaps)}/{s.NumLaps}");
        string pos = $"POS {s.Place}/{s.NumCars}";
        DrawText(fb, fb.Width - m - TextWidth(pos), m, pos);
        string time = TimeText(s.RaceTicks64);
        DrawText(fb, (fb.Width - TextWidth(time)) / 2, m, time);
        string spd = SpeedText(s);
        int y = fb.Height - m - 5 * Scale;
        DrawText(fb, fb.Width - m - TextWidth(spd), y - 7 * Scale, spd);
        string gear = "GEAR " + GearText(s.Gear);
        DrawText(fb, fb.Width - m - TextWidth(gear), y, gear);
    }
}
