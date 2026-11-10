using NFS.Data;

namespace NFS.Render;

/// <summary>State the replay strip shows (globals read by the strip): [ref:2633]
/// selected command (0 pause, 1 speed, 2 eject, 3 camera; -1 = none highlighted), press counter (non-zero = the [ref:2634]
/// selected button is drawn pressed), pause flag, speed index, camera icon index (0..8). [ref:2635]</summary>
public readonly record struct ReplayStripState(int Selected, bool Pressed, bool Paused, int SpeedIndex, int CameraIndex,
    int RaceTick64, int ReplayTicks64);

/// <summary>One shape placed by the strip: HUD.FSH name plus the offset added to the shape's own position. [ref:2636]</summary>
public readonly record struct ReplayIcon(string Shape, int Dx, int Dy);

/// <summary>Replay status strip, as in the original game. [ref:2637]</summary>
public sealed class ReplayStatusHud
{
    public const int ProgressWidth = 0x88;
    static readonly string[] SpeedShapes = ["cqtr", "chlf", "cnml", "cdbl"];
    /// <summary>Highlighted speed icons, local table = 13, 11, 15, 17 in the name order. [ref:2638]</summary>
    static readonly string[] SpeedHighlightShapes = ["hqtr", "hhlf", "hnml", "hdbl"];
    /// <summary>Camera icons (icon indices 18..26, table) and highlighted ones (27..35, table); names [ref:2639]
    /// them <c>cca1..9</c> / <c>hca1..9</c> (<c>%d</c> = index + 1).</summary>
    public const int CameraIcons = 9;
    public static string CameraShape(int cameraIndex, bool highlighted) => $"{(highlighted ? "hca" : "cca")}{cameraIndex + 1}";
    /// <summary>Horizontal offset of the pause and eject buttons from the speed button (-31, +31). [ref:2640]</summary>
    public const int ButtonSpacing = 31;
    readonly Dictionary<string, FshImage> _shapes;

    public ReplayStatusHud(string? gameDir = null)
    {
        var fsh = Fsh.Load("GAMEDATA/DASHHUD/PC/HUD.FSH", gameDir);
        var palette = fsh.GlobalPalette();
        _shapes = new Dictionary<string, FshImage>();
        foreach (string name in new[] { "repl", "play", "indi", "hcam", "dcam", "hbut", "dbut", "hpau", "hply", "heje" }
            .Concat(SpeedShapes).Concat(SpeedHighlightShapes)
            .Concat(Enumerable.Range(0, CameraIcons).SelectMany(i => new[] { CameraShape(i, false), CameraShape(i, true) })))
        {
            var entry = fsh.Find(name) ?? throw new InvalidDataException($"HUD.FSH missing replay shape '{name}'");
            var image = fsh.Decode(entry, palette) ?? throw new InvalidDataException($"HUD shape '{name}' is not a bitmap");
            if (entry.Main.Code == 0x7B)
            {
                var indices = fsh.Indices(entry);
                for (int i = 0; i < indices.Length; i++)
                    if (indices[i] == 255) image.Rgba[4 * i + 3] = 0;
            }
            _shapes.Add(name, image);
        }
    }

    /// <summary>Speed icon indices: 12, 10, 14, 16 (in table order). [ref:2641]</summary>
    public static string SpeedShape(int speedIndex) => SpeedShapes[Math.Clamp(speedIndex, 0, 3)];

    /// <summary>(current * 0x88) / replay_length, capped at 0x88. [ref:2642]</summary>
    public static int ProgressOffset(int raceTick64, int replayTicks64)
        => replayTicks64 <= 0 ? 0 : (int)Math.Clamp((long)Math.Max(0, raceTick64) * ProgressWidth / replayTicks64, 0, ProgressWidth);

    /// <summary>The shapes added for <paramref name="state"/>, in call order (the strip <c>repl [ref:2643]</c>
    /// first, then the four command slots, then the progress marker). The selected slot shows its highlighted button (<c>hbut</c>,
    /// <c>dbut</c> offset by (1,1) icons while pressed), the others their plain icon. [ref:2644]</summary>
    public static IReadOnlyList<ReplayIcon> Plan(in ReplayStripState state)
    {
        int speed = Math.Clamp(state.SpeedIndex, 0, 3), cam = Math.Clamp(state.CameraIndex, 0, CameraIcons - 1);
        var icons = new List<ReplayIcon> { new("repl", 0, 0) };
        for (int slot = 0; slot < 4; slot++)
        {
            if (slot == state.Selected)
            {
                // Pressed: dark button at (dx,0) with the highlighted glyph moved by (1,1); idle: highlighted button and glyph at (0,0).
                int glyph = state.Pressed ? 1 : 0;
                int dx = slot switch { 0 => -ButtonSpacing, 2 => ButtonSpacing, _ => 0 };
                string button = slot == 3 ? state.Pressed ? "dcam" : "hcam" : state.Pressed ? "dbut" : "hbut";
                icons.Add(new ReplayIcon(button, dx, 0));
                string shape = slot switch
                {
                    0 => state.Paused ? "hply" : "hpau",
                    1 => SpeedHighlightShapes[speed],
                    2 => "heje",
                    _ => CameraShape(cam, true),
                };
                icons.Add(new ReplayIcon(shape, glyph, glyph));
            }
            else if (slot == 0) { if (state.Paused) icons.Add(new ReplayIcon("play", 0, 0)); } // the strip supplies the pause glyph
            else if (slot == 1) icons.Add(new ReplayIcon(SpeedShapes[speed], 0, 0));
            else if (slot == 3) icons.Add(new ReplayIcon(CameraShape(cam, false), 0, 0));
        }
        if (state.ReplayTicks64 != 0) icons.Add(new ReplayIcon("indi", ProgressOffset(state.RaceTick64, state.ReplayTicks64), 0));
        return icons;
    }

    public void Draw(FrameBuffer fb, in ReplayStripState state)
    {
        foreach (var icon in Plan(state))
        {
            var image = _shapes[icon.Shape];
            OriginalHud.Blit(fb, image, Signed12(image.PosX) + icon.Dx, Signed12(image.PosY) + icon.Dy);
        }
    }

    static int Signed12(int value) => (value << 20) >> 20;
}
