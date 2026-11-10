using System.Numerics;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.Track;

namespace NFS.Render;

public readonly record struct MapCar(Vector3 Position, int Colour, bool Human);

/// <summary>hud_map_*... Coordinates are quantised before rotation [ref:2622]
/// as in the software renderer; only the source positions come from interpolated render poses.</summary>
public sealed class RaceMap
{
    public const int X = 0x214, Y = 0x13E, Width = 0x6C, Height = 0x87;
    readonly TrackSlice[] _slices;
    readonly (int X, int Z)[] _points;
    readonly FshImage[] _small, _big;
    readonly FshImage _frame;
    readonly FshImage? _cockpitFrame;
    readonly int[] _colours;
    readonly sbyte[] _offsetX, _offsetY;
    readonly FrameBuffer _buffer = new(Width, Height);
    public IReadOnlyList<(int X, int Z)> Points => _points;

    public RaceMap(TrackSlice[] slices, string? gameDir = null, int carType = -1)
    {
        _slices = slices;
        // index i*n/128, not equal-distance resampling. [ref:2623]
        _points = Enumerable.Range(0, 128).Select(i =>
            (slices[i * slices.Length / 128].X >> 19, slices[i * slices.Length / 128].Z >> 19)).ToArray();
        // Colour IDs are global paint IDs (e.g. replay: 24,56,81), not dot indices.
        _colours = RenderConstants.MapColours.ToArray();
        _offsetX = RenderConstants.MapOffsetX.ToArray().Select(b => (sbyte)b).ToArray();
        _offsetY = RenderConstants.MapOffsetY.ToArray().Select(b => (sbyte)b).ToArray();
        const string root = "GAMEDATA/DASHHUD/PC/";
        var dots = Fsh.Load(root + "MAPDOTS.FSH", gameDir);
        _small = Enumerable.Range(0, 16).Select(i => Decode(dots, $"ms{i:00}")).ToArray();
        _big = Enumerable.Range(0, 16).Select(i => Decode(dots, $"mb{i:00}")).ToArray();
        _frame = Decode(Fsh.Load(root + "HUD.FSH", gameDir), "hud4");
        // The cockpit view uses the dashboard's map shape. [ref:2624]
        if (carType is >= 0 and < 12)
            _cockpitFrame = Decode(Fsh.Load(root + $"DSH{carType:00}.QFS", gameDir), "mapp");
    }

    static FshImage Decode(Fsh fsh, string name)
    {
        var e = fsh.Find(name)!;
        var img = fsh.Decode(e, fsh.GlobalPalette())!;
        var indices = fsh.Indices(e);
        for (int i = 0; i < indices.Length; i++)
            if (indices[i] == 255) img.Rgba[4 * i + 3] = 0;
        return img;
    }

    // atan2(mean forward Z, mean forward X), including wrap and +0x288 guard. [ref:2625]
    public int Rotation(int slice, int noAssist = 0)
    {
        int x = 0, z = 0;
        for (int i = 0; i < 32; i++)
        {
            var s = _slices[(slice + i) % _slices.Length];
            x += s.Forward[0] << 9; z += s.Forward[2] << 9;
        }
        int angle = FxMath.Atan2(z >> 5, x >> 5);
        if (noAssist >= 0x94) { angle += 0x200; if (angle >= 0x200) angle -= 0x400; }
        return angle;
    }

    public static (int X, int Y) Project(Vector3 position, Vector3 player, int angle, bool backward, bool mirrored)
        => Transform(Quantise(position.X) - Quantise(player.X), Quantise(position.Z) - Quantise(player.Z), angle, backward, mirrored);

    static int Quantise(float metres) => (int)(metres * 65536f) >> 19;
    static (int X, int Y) Transform(int dx, int dz, int angle, bool backward, bool mirrored)
    {
        FxMath.SinCosTab(angle, out int sin, out int cos);
        int x = unchecked(sin * dx - cos * dz) >> 16;
        int y = unchecked(cos * dx + sin * dz) >> 16;
        return (53 + (backward != mirrored ? -x : x), 68 + (backward ? y : -y));
    }

    // 0x466C C9..E8: x=y=5, width=97, height=127. No edge clamping of markers.
    public static bool Inside(int x, int y) => x >= 5 && x < 102 && y >= 5 && y < 132;
    void Pixel(int x, int y, uint colour) { if (Inside(x, y)) _buffer.Set(x, y, colour); }
    void Line((int X, int Y) a, (int X, int Y) b, uint colour)
    {
        // clips endpoints with integer division before rasterising. [ref:2626]
        if (!Inside(a.X, a.Y) || !Inside(b.X, b.Y))
        {
            if ((a.X >= 102 && b.X >= 102) || (a.X < 5 && b.X < 5) ||
                (a.Y >= 132 && b.Y >= 132) || (a.Y < 5 && b.Y < 5)) return;
            if (b.X < a.X) (a, b) = (b, a);
            if (a.X < 5 && a.X != b.X) { a.Y += (b.Y - a.Y) * (5 - a.X) / (b.X - a.X); a.X = 5; }
            if (b.X >= 102 && a.X != b.X) { b.Y = (b.Y - a.Y) * (102 - a.X) / (b.X - a.X) + a.Y; b.X = 101; }
            if (b.Y < a.Y) (a, b) = (b, a);
            if (a.Y < 5 && a.Y != b.Y) { a.X += (b.X - a.X) * (5 - a.Y) / (b.Y - a.Y); a.Y = 5; }
            if (b.Y >= 132 && a.Y != b.Y) { b.X = (132 - a.Y) * (b.X - a.X) / (b.Y - a.Y) + a.X; b.Y = 131; }
            if (!Inside(a.X, a.Y) || !Inside(b.X, b.Y)) return;
        }
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        if (dy < dx)
        {
            if (b.X < a.X) (a, b) = (b, a);
            int error = dx / 2, step = b.Y < a.Y ? -1 : 1;
            while (a.X <= b.X)
            {
                Pixel(a.X++, a.Y, colour);
                error -= dy;
                if (error < 0) { a.Y += step; error += dx; }
            }
        }
        else
        {
            if (b.Y < a.Y) (a, b) = (b, a);
            int error = dy / 2, step = b.X < a.X ? -1 : 1;
            while (a.Y <= b.Y)
            {
                Pixel(a.X, a.Y++, colour);
                error -= dx;
                if (error < 0) { a.X += step; error += dy; }
            }
        }
    }

    /// <param name="originX">Left edge of the map on the screen (<see cref="X"/> = slot 5 of the full-screen HUD).</param>
    /// <param name="originY">Top edge (<see cref="Y"/>); split screen: slot 5 of the player's HUD table = (0, 0x45) / (0, 0x135).</param>
    public void Draw(FrameBuffer target, IReadOnlyList<MapCar> cars, int playerIndex, int slice,
        bool backward = false, bool mirrored = false, int noAssist = 0, bool cockpit = false, int originX = X, int originY = Y)
    {
        _buffer.Clear(0, 0, 0);
        OriginalHud.Blit(_buffer, cockpit ? _cockpitFrame ?? _frame : _frame, 0, 0);
        var player = cars[playerIndex];
        int angle = Rotation(slice, noAssist);
        (int X, int Y) Point(int i) => Transform(_points[i].X - Quantise(player.Position.X),
            _points[i].Z - Quantise(player.Position.Z), angle, backward, mirrored);
        // four parallel lines with offsets read from E4. [ref:2627]
        for (int i = 0; i < 128; i++)
        {
            var a = Point(i); var b = Point((i + 1) % 128);
            for (int j = 0; j < 4; j++)
                Line((a.X + _offsetX[j], a.Y + _offsetY[j]), (b.X + _offsetX[j], b.Y + _offsetY[j]), 0xE0E0E0);
        }
        // Start/finish bar, slice0 Right at +0x12/+0x14; truncation toward zero in the original.
        var first = _slices[0];
        (int X, int Y) End(int sign)
        {
            FxMath.SinCosTab(angle, out int s, out int c);
            int dx = _points[0].X - Quantise(player.Position.X) + sign * ((first.Right[0] << 9) >> 13);
            int dz = _points[0].Z - Quantise(player.Position.Z) + sign * ((first.Right[2] << 9) >> 13);
            int px = unchecked(s * dx - c * dz) / 65536, py = unchecked(c * dx + s * dz) / 65536;
            return (53 + (backward != mirrored ? -px : px), 68 + (backward ? py : -py));
        }
        Line(End(1), End(-1), 0x40E040);
        void Dot(int i)
        {
            var car = cars[i];
            var p = Project(car.Position, player.Position, angle, backward, mirrored);
            var img = (i == playerIndex || car.Human ? _big : _small)[_colours[car.Colour]];
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    int o = (y * img.Width + x) * 4;
                    if (img.Rgba[o + 3] != 0) Pixel(p.X - img.CenterX + x, p.Y - img.CenterY + y,
                        (uint)(img.Rgba[o] << 16 | img.Rgba[o + 1] << 8 | img.Rgba[o + 2]));
                }
        }
        for (int i = 0; i < cars.Count; i++) if (i != playerIndex) Dot(i);
        Dot(playerIndex); // player last, always at (53,68). [ref:2628]
        for (int y = 0; y < Height && originY + y < target.Height; y++)
            for (int x = 0; x < Width && originX + x < target.Width; x++) target.Set(originX + x, originY + y, _buffer.Get(x, y));
    }
}
