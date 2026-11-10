using NFS.Data;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Original pause screen graphics (GAMEDATA/PAUSE/Pause??.QFS) as drawn by the original [ref:1321]
/// in the single-player layout. [ref:1322]
/// Shapes: back (320x240 mirrored into four quadrants), head, powr, slid, ps%d%d (item, 1 = selected),
/// ps12 = greyed restart, qtb1/qtbx confirmation caption, yes1/yes2, noo1/noo2 (1 = highlighted), logo.</summary>
public sealed class PauseArt
{
    static readonly string[] Suffix = ["EN", "GE", "FR", "SP", "IT", "SW"]; // language 0..5 = ENG GER FRE SPA ITA SWE
    readonly Fsh _fsh;
    /// <summary>Master palette entries 0..25 = group 0 = the palette of DASHHUD/PC/HUD.FSH [ref:1323]
    /// (registered starting at entry 0); the pause shapes index it directly, 0xFF = transparent. [ref:1324]
    /// Their own attached 0x2D palettes (logo, ps5x) are not used by the original.</summary>
    readonly byte[] _palette;
    readonly Dictionary<string, FshImage?> _images = new();

    PauseArt(Fsh fsh, byte[] palette) { _fsh = fsh; _palette = palette; }

    public static PauseArt? TryLoad(string? gameDir, int language)
    {
        try
        {
            var palette = Fsh.Load("GAMEDATA/DASHHUD/PC/HUD.FSH", gameDir).GlobalPalette();
            return palette == null ? null
                : new PauseArt(Fsh.Load($"GAMEDATA/PAUSE/PAUSE{Suffix[Math.Clamp(language, 0, 5)]}.QFS", gameDir), palette);
        }
        catch (Exception e) when (e is IOException or DirectoryNotFoundException or InvalidDataException) { return null; }
    }

    FshImage? Image(string name)
    {
        if (!_images.TryGetValue(name, out var image))
            _images[name] = image = _fsh.Find(name) is { } e ? Decode(e) : null;
        return image;
    }

    FshImage? Decode(FshEntry e)
    {
        if (e.Main.Code != 0x7B) return null;
        var idx = _fsh.Indices(e);
        var rgba = new byte[idx.Length * 4];
        for (int i = 0; i < idx.Length; i++)
        {
            if (idx[i] == 0xFF) continue; // transparent
            _palette.AsSpan(idx[i] * 4, 3).CopyTo(rgba.AsSpan(i * 4));
            rgba[i * 4 + 3] = 255;
        }
        var b = e.Main;
        return new FshImage(e.Name, b.Width, b.Height, rgba, b.CenterX, b.CenterY, b.PosX, b.PosY);
    }

    void Shape(FrameBuffer fb, string name)
    {
        if (Image(name) is { } image) OriginalHud.Blit(fb, image, image.PosX, image.PosY);
    }

    void Mirrored(FrameBuffer fb, FshImage image, int ox, int oy, bool flipX, bool flipY)
    {
        for (int sy = 0; sy < image.Height; sy++)
            for (int sx = 0; sx < image.Width; sx++)
            {
                int o = (sy * image.Width + sx) * 4;
                if (image.Rgba[o + 3] == 0) continue;
                int px = ox + (flipX ? image.Width - 1 - sx : sx), py = oy + (flipY ? image.Height - 1 - sy : sy);
                if ((uint)px < fb.Width && (uint)py < fb.Height)
                    fb.Set(px, py, (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]));
            }
    }

    public void Draw(FrameBuffer fb, PauseMenu menu)
    {
        fb.Clear(0, 0, 0);
        if (Image("back") is { } back)
        {
            Mirrored(fb, back, 0, 0xF0, false, false);
            Mirrored(fb, back, 0, 0, false, true);
            Mirrored(fb, back, 0x140, 0, true, true);
            Mirrored(fb, back, 0x140, 0xF0, true, false);
        }
        Shape(fb, "head");
        // powr frames at (0x152,0x8A) and (0x152,0xB6), slid bars at (0x16C,0x94) / (0x16C,0xC0).
        if (Image("powr") is { } powr)
        {
            OriginalHud.Blit(fb, powr, 0x152, 0x8A);
            OriginalHud.Blit(fb, powr, 0x152, 0xB6);
        }
        DrawSlider(fb, 0x94, menu.MusicVolume);
        DrawSlider(fb, 0xC0, menu.EffectsVolume);
        for (int i = 0; i < 6; i++)
        {
            string name = i == 1 && !menu.RestartEnabled ? "ps12" : $"ps{i}{(i == menu.Selected ? 1 : 0)}";
            Shape(fb, name);
        }
        Shape(fb, "logo");
        if (menu.Confirming)
        {
            Shape(fb, menu.RaceType == 2 ? "qtb1" : "qtbx");
            Shape(fb, menu.ConfirmYes ? "noo2" : "noo1");
            Shape(fb, menu.ConfirmYes ? "yes1" : "yes2");
        }
    }

    void DrawSlider(FrameBuffer fb, int y, int volume)
    {
        if (Image("slid") is { } slid) OriginalHud.Blit(fb, slid, 0x16C, y);
        // paints the uncovered right part (160 - vol*160/127 px) with palette index 9. [ref:1325]
        int cover = 0xA0 - volume * 0xA0 / 0x7F;
        uint colour = (uint)(_palette[9 * 4] << 16 | _palette[9 * 4 + 1] << 8 | _palette[9 * 4 + 2]);
        for (int yy = y; yy < y + 14; yy++)
            for (int x = 0x20C - cover; x < 0x20C; x++) fb.Set(x, yy, colour);
    }
}
