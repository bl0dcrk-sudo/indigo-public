using System.Globalization;
using System.Numerics;
using System.Text;

namespace NFS.Render;

/// <summary>
/// Port-only multiplayer overlay: a driver's name above their car. A network client knows the names of the humans from the
/// welcome roster; every other car of the race is an AI opponent and carries the <see cref="BotTag"/> tag instead.
/// Pure layout + drawing, so the screen placement is testable without the desktop host.
/// </summary>
public static class DriverLabels
{
    /// <summary>Tag of an AI-controlled car (no network player name for its CarId).</summary>
    public const string BotTag = "[bot]";

    /// <summary>Anchor height above the car origin (CarObj.Pos, the box centre), in the car's own up axis: about 0.45 m over the roof of a typical car.</summary>
    public const float HeightMetres = 1.025f;

    /// <summary>Labels farther than this are not drawn (the same car range as the rear-view mirror).</summary>
    public const float MaxDistanceMetres = 150f;

    public const int TextScale = 2;
    public const uint PlayerColor = 0xFFFFFF, BotColor = 0xB0B0B0;

    /// <summary>Glyph opacity (0..255). Semi-transparent by default, so the labels do not hide the road/cars behind them.</summary>
    public const int TextAlpha = 140;

    /// <summary>One labelled car: the render instance pose (world metres) and its text.</summary>
    public readonly record struct Car(Vector3 Position, Vector3 Up, string Text, bool Bot);

    /// <summary>Projected label: top-left pixel of the text and its distance from the camera.</summary>
    public readonly record struct Placed(string Text, bool Bot, int X, int Y, float Distance);

    /// <summary>Name shown for <paramref name="carId"/>: its network player name, else <see cref="BotTag"/>.</summary>
    public static string NameOf(IReadOnlyDictionary<int, string>? playerNames, int carId) =>
        IsPlayer(playerNames, carId) ? playerNames![carId].Trim() : BotTag;

    /// <summary>Whether the car belongs to a network player (as opposed to an AI opponent).</summary>
    public static bool IsPlayer(IReadOnlyDictionary<int, string>? playerNames, int carId) =>
        playerNames != null && playerNames.TryGetValue(carId, out var name) && !string.IsNullOrWhiteSpace(name);

    /// <summary>ASCII form for the built-in 3×5 font: accents are dropped (Ż → Z), and the letters that do not decompose
    /// (Ł, Ø, Đ) are mapped by hand. Without it the accented glyphs would draw as blank cells.</summary>
    public static string AsciiLabel(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch
            {
                'ł' => 'l', 'Ł' => 'L', 'ø' => 'o', 'Ø' => 'O', 'đ' => 'd', 'Đ' => 'D', _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Labels of the cars that fall inside the view, far to near: nearer labels are drawn last, on top.
    /// A car is skipped when it is behind the camera plane, farther than <see cref="MaxDistanceMetres"/> or when its
    /// text would not fit on screen at all.</summary>
    public static IReadOnlyList<Placed> Layout(Camera camera, IEnumerable<Car> cars)
    {
        var placed = new List<Placed>();
        foreach (var car in cars)
        {
            var world = car.Position + car.Up * HeightMetres;
            float distance = Vector3.Distance(camera.Position, world);
            if (distance > MaxDistanceMetres) continue;
            var view = camera.ToView(world);
            if (view.Z < (float)Camera.NearZ) continue;
            var (px, py) = camera.Project(view);
            string text = AsciiLabel(car.Text);
            int width = HudOverlay.TextWidth(text, TextScale), height = 5 * TextScale;
            int x = (int)Math.Round(px) - width / 2, y = (int)Math.Round(py) - height;
            if (x < 0 || y < 0 || x + width > camera.Width || y + height > camera.Height) continue;
            placed.Add(new Placed(text, car.Bot, x, y, distance));
        }
        return placed.OrderByDescending(p => p.Distance).ToList();
    }

    /// <summary>Draws every on-screen label with the HUD's 3×5 font (players white, AI cars grey), blended at
    /// <see cref="TextAlpha"/> so the scene stays visible through the letters.</summary>
    public static void Draw(FrameBuffer fb, Camera camera, IEnumerable<Car> cars)
    {
        foreach (var label in Layout(camera, cars))
            HudOverlay.DrawText(fb, label.X, label.Y, label.Text, label.Bot ? BotColor : PlayerColor, TextScale, TextAlpha);
    }
}
