using NFS.Core.Constants;
using NFS.Data.FrontEnd;

namespace NFS.Render;

/// <summary>alignment, shadow and original FFN coverage. [ref:2525]</summary>
public sealed class FrontEndFont(string? gameDir = null)
{
    readonly Dictionary<int, Ffn> _fonts = new();
    // SWISS36 contains only the 20 letters used by menu headings; SWISS36A is the complete original atlas.
    public Ffn Font(int size) => _fonts.TryGetValue(size, out var f) ? f : _fonts[size] = Ffn.Load(size, gameDir, complete: true);
    public void Draw(FrameBuffer fb, string text, int x, int y, int size = 18, int align = 0, int colour = 2) =>
        Draw(fb, text, x, y, size, align, RenderConstants.FrontEndTextColours[colour]);

    /// <summary>The same drawing with the colour given as RGB instead of an index of the colour table (<c>align &gt; 2</c> still adds the shadow).</summary>
    public void Draw(FrameBuffer fb, string text, int x, int y, int size, int align, uint rgb)
    {
        var font = Font(size);
        x -= (align % 3) switch { 1 => font.Measure(text), 2 => font.Measure(text) / 2, _ => 0 };
        if (align > 2) Paint(fb, font, text, x + 1, y + 1, 8); // passes RGB 0x000008, not colour-table index8. [ref:2526]
        Paint(fb, font, text, x, y, rgb);
    }
    static void Paint(FrameBuffer fb, Ffn font, string text, int x, int y, uint colour)
    {
        foreach (char c in text)
        {
            if (font.Find(c) is not { } g) continue;
            for (int sy = 0; sy < g.Height; sy++)
                for (int sx = 0; sx < g.Width; sx++)
                {
                    int px = x + g.Left + sx, py = y + g.Top + sy;
                    if ((uint)px >= fb.Width || (uint)py >= fb.Height) continue;
                    int a = font.Coverage[(g.Y + sy) * font.Width + g.X + sx];
                    if (a == 0) continue;
                    // ramp entry k (fg * k + bg * (15 - k)) >> 4 with bg = black (passes colour 0); [ref:2527]
                    // the glyph pixel REPLACES the screen pixel, it is not blended with what is under it.
                    uint rgb = 0;
                    for (int shift = 0; shift <= 16; shift += 8)
                        rgb |= (uint)((colour >> shift & 255) * a >> 4) << shift;
                    fb.Set(px, py, rgb);
                }
            x += g.Advance;
        }
    }
}
