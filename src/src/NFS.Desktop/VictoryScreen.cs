using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>Victory screen of statdisp (mode 1). Background "back" from VICTORY.QFS, photo [ref:1542]
/// of the winner's car (shape from the table), congratulations text in the game language ("econ"…), track name (TEXT 0x3D + track) [ref:1543]
/// and the brand logo ("car7" or "cpg7" from the brand QFS, table) rotated like the Omega logo on the results screen: a frame every 10 ticks [ref:1544]
/// 128 Hz, squash phase +0x1E for the drawing up to 0x400, then a blit with alpha frame × 256 / 0x23. Rules (when, how long) –
/// <see cref="VictoryRules"/>.</summary>
public sealed class VictoryScreen
{
    readonly string? _gameDir;
    readonly int _language;
    readonly Fsh _art;
    readonly TextFile _text;
    readonly FrontEndFont _font;
    readonly Dictionary<string, FshImage?> _images = new();
    readonly Dictionary<int, FshImage?> _brands = new();

    public VictoryScreen(string? gameDir = null, int language = 0)
    {
        _gameDir = gameDir;
        _language = Math.Clamp(language, 0, 5);
        string[] languages = ["ENG", "GER", "FRE", "SPA", "ITA", "SWE"];
        _art = Fsh.Load("FEDATA/PC/ART/VICTORY.QFS", gameDir);   // ("victory") [ref:1545]
        _text = TextFile.Load(languages[_language], gameDir);
        _font = new FrontEndFont(gameDir);
    }

    FshImage? Image(string name)
    {
        if (!_images.TryGetValue(name, out var image))
            _images[name] = image = _art.Find(name) is { } e ? _art.Decode(e, _art.GlobalPalette()) : null;
        return image;
    }

    /// <summary>Shape of the brand logo <c>car7</c>/<c>cpg7</c> from the model's brand QFS (mode 1); null for model ≥ 0xC or when [ref:1546]
    /// the file or shape is missing (original: == 0 → return without drawing). [ref:1547]</summary>
    public FshImage? Brand(int model)
    {
        if (model is < 0 or >= VictoryRules.BrandModels) return null;
        if (_brands.TryGetValue(model, out var cached)) return cached;
        FshImage? image = null;
        try
        {
            var fsh = Fsh.Load($"FEDATA/PC/ART/{VictoryRules.BrandArt[model].ToUpperInvariant()}.QFS", _gameDir);
            if (fsh.Find(VictoryRules.BrandShape(model)) is { } entry) image = fsh.Decode(entry, fsh.GlobalPalette());
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
        return _brands[model] = image;
    }

    void Blit(FrameBuffer fb, string shape)
    {
        if (Image(shape) is { } image) OriginalHud.Blit(fb, image, image.PosX, image.PosY);
    }

    /// <summary>One frame of the loop after <paramref name="ticks"/> ticks at 128 Hz since entering the screen. [ref:1548]</summary>
    /// <param name="trackId">session (converted to the track index in the menu). [ref:1549]</param>
    /// <param name="unlockLevel">CONFIG +0x1D : models &lt; 0xC + level have a car photo. [ref:1550]</param>
    /// <param name="animated">config +0x12D != 0. [ref:1551]</param>
    public void Draw(FrameBuffer fb, RacerResult winner, int trackId, long ticks, bool animated, int unlockLevel = 0)
    {
        fb.Clear(0, 0, 0);
        Blit(fb, "back");
        int model = winner.CarType;
        if (model >= 0 && model < VictoryRules.CarPhotoShapes.Count && model < VictoryRules.BrandModels + unlockLevel)
            Blit(fb, VictoryRules.CarPhotoShapes[model]);
        Blit(fb, VictoryRules.CongratsShapes[_language]);
        _font.Draw(fb, _text[VictoryRules.TrackTextBase + VictoryRules.MenuTrack(trackId)], VictoryRules.TrackTextX,
            VictoryRules.TrackTextY, VictoryRules.TrackTextFont, VictoryRules.TrackTextAlign, VictoryRules.TrackTextColour);
        if (Brand(model) is { } logo) DrawBrand(fb, logo, model, VictoryRules.Frame(ticks, animated), animated);
    }

    /// <summary>Mode 1 value: shape position ((model), −3); during the phase &lt; 0x400, a scaled blit from the centre [ref:1552]
    /// then a plain blit with the frame's alpha. [ref:1553]</summary>
    static void DrawBrand(FrameBuffer fb, FshImage logo, int model, int frame, bool animated)
    {
        int x = logo.PosX + VictoryRules.BrandDx(model), y = logo.PosY + VictoryRules.BrandDy;
        int factor = animated ? MenuScene.SquashFactor(VictoryRules.Phase(frame)) : MenuScene.IconSquashFull;
        if (factor == MenuScene.IconSquashFull) { MenuScene.BlitLevel(fb, logo, x, y, VictoryRules.Level(frame)); return; }
        int w = (short)(factor * logo.Width / 64), half = w / 2;
        if (half < 1) half = -half;
        MenuScene.BlitScaled(fb, logo, x + logo.Width / 2 - half, y, w, logo.Height, MenuScene.ShowcaseFullLevel);
    }
}
