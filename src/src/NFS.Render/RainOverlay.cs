using NFS.Core;
using NFS.Data;

namespace NFS.Render;

/// <summary>Draws the visible rain/snow streaks of <see cref="Weather"/> (render list cmd 0x11 lines, grey 0x848484) over the
/// 3-D view. [ref:2629]</summary>
public static class RainOverlay
{
    public static void Draw(FrameBuffer fb, Weather w, int first, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var s = w.Streaks[first + i];
            Line(fb, s.X0, s.Y0, s.X1, s.Y1, (uint)Weather.Gray);
        }
    }

    /// <summary>Windscreen drops of <see cref="Weather.Drops"/> (render list cmd 0x14 sprites at x,y; shapes smrn / mdrn of
    /// SFX.FSH; every 5th drop is the medium one) drawn after the 3-D view, before the dash. [ref:2630]</summary>
    public static void DrawDrops(FrameBuffer fb, Weather w, int first, int count, DropSprites sprites)
    {
        for (int i = first; i < first + count && i < Weather.DropTotal; i++)
        {
            var d = w.Drops[i];
            OriginalHud.Blit(fb, d.Large ? sprites.Medium : sprites.Small, d.X, d.Y);
        }
    }

    static void Line(FrameBuffer fb, int x0, int y0, int x1, int y1, uint rgb)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        for (int n = 0; n < 4096; n++)
        {
            if ((uint)x0 < (uint)fb.Width && (uint)y0 < (uint)fb.Height) fb.Set(x0, y0, rgb);
            if (x0 == x1 && y0 == y1) return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}

/// <summary>smrn / mdrn of GAMEDATA/RENDER/PC/SFX.FSH (index 255 transparent like every cmd 0x13/0x14 sprite).</summary>
public sealed class DropSprites
{
    public FshImage Small { get; }
    public FshImage Medium { get; }

    public DropSprites(string? gameDir = null)
    {
        var fsh = Fsh.Load("GAMEDATA/RENDER/PC/SFX.FSH", gameDir);
        var pal = fsh.GlobalPalette();
        FshImage Load(string name)
        {
            var e = fsh.Find(name)!;
            var img = fsh.Decode(e, pal)!;
            if (e.Main.Code == 0x7B)
            {
                var idx = fsh.Indices(e);
                for (int i = 0; i < idx.Length; i++) if (idx[i] == 255) img.Rgba[4 * i + 3] = 0;
            }
            return img;
        }
        Small = Load("smrn");
        Medium = Load("mdrn");
    }
}
