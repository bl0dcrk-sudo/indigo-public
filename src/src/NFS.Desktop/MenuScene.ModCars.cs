using NFS.Core.Constants;
using NFS.Game.Cars;
using NFS.Game.FrontEnd;
using NFS.ModApi;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Mod cars in the car menus (openspec modding-api-cars 4.4): the PLAYER ONE / TWO and OPPONENTS panels and the showcase screen show the car
/// by its <c>car.json</c> (name, class, description, colour) and a 3D preview of its .glb instead of the pictures of the original cars.</summary>
public sealed partial class MenuScene
{
    TrackRenderer? _modPreviewRenderer;
    readonly Dictionary<(string Key, int Colour), FrameBuffer?> _modPreviews = [];

    /// <summary>Background of the mod car preview panels (the neutral grey of <see cref="CarPreview"/> is replaced by it).</summary>
    const uint ModPanelBackground = 0x14181C;

    /// <summary>640x480 render of the mod car (three-quarter view, original rasteriser), cached per car and colour; null when the model cannot be drawn
    /// (the menu must go on without a preview).</summary>
    FrameBuffer? ModPreview(CarDefinition car, int colour)
    {
        if (_modPreviews.TryGetValue((car.Key.Value, colour), out var cached)) return cached;
        FrameBuffer? fb = null;
        try
        {
            _modPreviewRenderer ??= new TrackRenderer(0, _dir);
            var model = new CarModel(car.DisplayName, ModCarModel.Load(car, colour));
            fb = CarPreview.Render(_modPreviewRenderer, model, CarPreview.Views[3].Direction, GraphicsMode.Vanilla);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { /* no preview: the panel stays dark */ }
        return _modPreviews[(car.Key.Value, colour)] = fb;
    }

    readonly Dictionary<(string Key, int Colour, int W, int H), FrameBuffer> _modPanels = [];

    /// <summary>Draws the preview of a mod car into the rectangle (the scaled picture is cached, so a menu frame only copies pixels).</summary>
    void DrawModPreview(FrameBuffer fb, CarDefinition car, int colour, int x, int y, int w, int h)
    {
        if (!_modPanels.TryGetValue((car.Key.Value, colour, w, h), out var panel))
            _modPanels[(car.Key.Value, colour, w, h)] = panel = ComposeModPreview(car, colour, w, h);
        for (int ty = 0; ty < h; ty++)
        {
            int py = y + ty;
            if ((uint)py >= (uint)fb.Height) continue;
            int from = Math.Max(0, -x), to = Math.Min(w, fb.Width - x);
            if (to > from) Buffer.BlockCopy(panel.Rgba, (ty * w + from) * 4, fb.Rgba, (py * fb.Width + x + from) * 4, (to - from) * 4);
        }
    }

    /// <summary>The preview of a mod car as a <paramref name="w"/> x <paramref name="h"/> picture: the car (pixels that differ from the neutral
    /// background) is cropped with a margin to the aspect of the rectangle and shrunk with 3x3 sampling; the rest is <see cref="ModPanelBackground"/>.</summary>
    FrameBuffer ComposeModPreview(CarDefinition car, int colour, int w, int h)
    {
        var fb = new FrameBuffer(w, h);
        FillRect(fb, 0, 0, w, h, ModPanelBackground);
        if (ModPreview(car, colour) is not { } src) return fb;
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int sy = 0; sy < src.Height; sy++)
            for (int sx = 0; sx < src.Width; sx++)
                if (src.Get(sx, sy) != CarPreview.Background)
                { if (sx < minX) minX = sx; if (sx > maxX) maxX = sx; if (sy < minY) minY = sy; if (sy > maxY) maxY = sy; }
        if (maxX < 0) return fb;
        double centreX = (minX + maxX + 1) / 2.0, centreY = (minY + maxY + 1) / 2.0;
        double scale = Math.Max((maxX - minX + 1) * 1.12 / w, (maxY - minY + 1) * 1.12 / h);   // source pixels per panel pixel
        for (int ty = 0; ty < h; ty++)
            for (int tx = 0; tx < w; tx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 3; i++)
                    {
                        int sx = (int)Math.Floor(centreX + (tx + (i + 0.5) / 3 - w / 2.0) * scale), sy = (int)Math.Floor(centreY + (ty + (j + 0.5) / 3 - h / 2.0) * scale);
                        if ((uint)sx >= (uint)src.Width || (uint)sy >= (uint)src.Height) continue;
                        uint p = src.Get(sx, sy);
                        if (p == CarPreview.Background) continue;
                        r += (int)(p >> 16 & 255); g += (int)(p >> 8 & 255); b += (int)(p & 255); n++;
                    }
                if (n >= 5) fb.Set(tx, ty, (uint)((r / n) << 16 | (g / n) << 8 | b / n));
            }
        return fb;
    }

    /// <summary>"class A".."class G": the original text for A..C, for D..G the text of class C with the letter replaced (same font).</summary>
    string ClassLabel(CarRaceClass c)
        => CarRaceClasses.IsLow(c) ? CarRaceClasses.Label(_text![FrontEndConstants.RatingTexts[SiPhysicsConverter.TemplateNumber(CarRaceClass.C)]], c)
            : _text![FrontEndConstants.RatingTexts[SiPhysicsConverter.TemplateNumber(c)]];

    void DrawClassLabel(FrameBuffer fb, CarRaceClass c) => _font!.Draw(fb, ClassLabel(c), 0xAA, _text!.Entries[0x55].Y, 18, 1, 0);

    /// <summary>Texts of the PLAYER / OPPONENT panel of a mod car: the class label of the original (left of the panel) and the description from car.json.</summary>
    void DrawModPanelTexts(FrameBuffer fb, CarDefinition car)
    {
        DrawClassLabel(fb, car.Traits.RaceClass);   // "class A/B/C" like the original cars of the class, "class D..G" composed from it
        DrawWrapped(fb, MenuController.ModDescription(car), 16, 352, 190, 13, 14, 6, colour: 2);
    }

    /// <summary>Wraps <paramref name="text"/> to <paramref name="width"/> px in the original font <paramref name="size"/> and draws at most
    /// <paramref name="maxLines"/> lines (the last one ends with "..." when the text is longer).</summary>
    void DrawWrapped(FrameBuffer fb, string text, int x, int y, int width, int size, int lineHeight, int maxLines, int colour)
    {
        if (text.Length == 0) return;
        var font = _font!.Font(size);
        var lines = WrapLines(text, width, font).ToList();
        for (int i = 0; i < lines.Count && i < maxLines; i++)
        {
            string line = lines[i];
            if (i == maxLines - 1 && lines.Count > maxLines)
            {
                while (line.Length > 0 && font.Measure(line + "...") > width) line = line[..^1];
                line = line.TrimEnd() + "...";
            }
            _font.Draw(fb, line, x, y + i * lineHeight, size, 3, colour);
        }
    }

    /// <summary>Colour of the swatch of a mod car (car.json "colors").</summary>
    uint ModSwatchColour(CarDefinition car, int player)
        => car.Colors.Count == 0 ? 0u : car.Colors[Controller.ModColour(player, car)] is var c ? (uint)(c.R << 16 | c.G << 8 | c.B) : 0u;

    /// <summary>Showcase screen of a mod car: dark background, 3D preview where the original has its photos, then name, class, maker, year,
    /// description and the colours from car.json.</summary>
    void DrawModShowcase(FrameBuffer fb, CarDefinition car)
    {
        BackgroundSource = null;
        for (int y = 0; y < fb.Height; y++)
        {
            int k = y * 255 / (fb.Height - 1);
            uint rgb = (uint)((0x2A + k * 0x40 / 255) << 16 | (0x06 + k * 0x08 / 255) << 8 | 0x08 + k * 0x08 / 255);
            for (int x = 0; x < fb.Width; x++) fb.Set(x, y, rgb);
        }
        DrawModPreview(fb, car, Controller.ModColour(0, car), ModShowcaseX, ModShowcaseY, ModShowcaseWidth, ModShowcaseHeight);
    }

    public const int ModShowcaseX = 243, ModShowcaseY = 20, ModShowcaseWidth = 361, ModShowcaseHeight = 150;

    /// <summary>Texts of the showcase screen of a mod car (drawn after the menu items).</summary>
    void DrawModShowcaseTexts(FrameBuffer fb, CarDefinition car)
    {
        const int x = 243, valueX = 602;
        int y = 184;
        _font!.Draw(fb, car.DisplayName, x, y, 18, 3, 4);
        _font.Draw(fb, ClassLabel(car.Traits.RaceClass), valueX, y, 18, 4, 2);   // the class label of the original cars of the class
        y += 30;
        string maker = (car.Manufacturer ?? "") + (car.Year is { } year ? (car.Manufacturer is null ? "" : ", ") + year : "");
        if (maker.Length > 0) { _font.Draw(fb, maker, x, y, 13, 3, 2); y += 22; }
        DrawWrapped(fb, MenuController.ModDescription(car), x, y, ModShowcaseWidth, 13, 17, 5, colour: 2);
        if (car.Colors.Count > 0)
        {
            int cy = 400;
            _font.Draw(fb, "colours", x, cy, 13, 3, 4);
            const int swatch = 20, gap = 8;
            for (int i = 0; i < car.Colors.Count; i++)
            {
                int sx = x + 70 + i * (swatch + gap);
                FillRect(fb, sx - 1, cy - 1, swatch + 2, swatch + 2, i == Controller.ModColour(0, car) ? 0xFFFFFFu : 0u);
                var c = car.Colors[i];
                FillRect(fb, sx, cy, swatch, swatch, (uint)(c.R << 16 | c.G << 8 | c.B));
            }
        }
    }
}
