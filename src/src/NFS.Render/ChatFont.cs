using StbTrueTypeSharp;

namespace NFS.Render;

/// <summary>
/// A common system font (TrueType) for the network chat: the menu fonts of the game (SWISS FFN) and the 3x5 bitmap font of
/// <see cref="HudOverlay"/> lack a number of the printable ASCII characters (<c>) ? @</c> ...), so chat text is drawn with a font every
/// machine has. The glyphs of ASCII 32..126 are rasterized once at load (anti-aliased coverage, no per-frame allocation) with
/// StbTrueTypeSharp, a pure C# rasterizer, so this works on every platform the port builds for. When no font file is found
/// <see cref="Shared"/> is null and the caller keeps its previous behaviour.
/// </summary>
public sealed class ChatFont
{
    /// <summary>Em size in pixels: capitals are about 8 px tall (Arial), so lines 10 px apart do not touch.</summary>
    public const float EmPixels = 11f;

    const int First = 32, Last = 126;

    readonly struct Glyph(int advance, int left, int top, int width, int height, byte[] coverage)
    {
        public readonly int Advance = advance, Left = left, Top = top, Width = width, Height = height;
        public readonly byte[] Coverage = coverage;
    }

    readonly Glyph[] _glyphs = new Glyph[Last - First + 1];

    /// <summary>Path of the file the font was loaded from.</summary>
    public string Path { get; }

    /// <summary>Distance from the top of a line (ascent) to the baseline, in pixels.</summary>
    public int Ascent { get; }

    /// <summary>Height of a capital letter, in pixels.</summary>
    public int CapHeight { get; }

    ChatFont(string path, int ascent, int capHeight) { Path = path; Ascent = ascent; CapHeight = capHeight; }

    // Fits are cached per (text, room, tail): a long chat line is cut once, not on every frame.
    [ThreadStatic] static Dictionary<(string Text, int Room, bool Tail), string>? _cut;

    static readonly Lazy<ChatFont?> SharedFont = new(() => Load(Candidates()));

    // A scope of a test (or of a host that wants the old look): replaces <see cref="Shared"/> on the current thread only.
    [ThreadStatic] static bool _hasOverride;
    [ThreadStatic] static ChatFont? _override;

    /// <summary>The font of the chat on this machine (the first of <see cref="Candidates"/> that loads), or null when there is none.</summary>
    public static ChatFont? Shared => _hasOverride ? _override : SharedFont.Value;

    /// <summary>Replaces <see cref="Shared"/> (null = no font) on the current thread until the returned scope is disposed.</summary>
    public static IDisposable Override(ChatFont? font)
    {
        bool had = _hasOverride;
        var old = _override;
        _hasOverride = true;
        _override = font;
        return new Scope(had, old);
    }

    sealed class Scope(bool had, ChatFont? old) : IDisposable
    {
        public void Dispose() { _hasOverride = had; _override = old; }
    }

    /// <summary>Font files to try, in order: Windows (Arial, Segoe UI, Consolas), Linux (DejaVu Sans, Liberation Sans), macOS (Arial),
    /// Android (Roboto).</summary>
    public static IEnumerable<string> Candidates()
    {
        string win = Environment.GetEnvironmentVariable("WINDIR") ?? "";
        if (win.Length > 0)
            foreach (string f in new[] { "arial.ttf", "segoeui.ttf", "consola.ttf", "tahoma.ttf", "verdana.ttf" })
                yield return System.IO.Path.Combine(win, "Fonts", f);
        foreach (string dir in new[] { "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/TTF",
                     "/usr/share/fonts/dejavu-sans-fonts", "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/liberation" })
            foreach (string f in new[] { "DejaVuSans.ttf", "LiberationSans-Regular.ttf" })
                yield return System.IO.Path.Combine(dir, f);
        yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
        yield return "/Library/Fonts/Arial.ttf";
        yield return "/system/fonts/Roboto-Regular.ttf";
        yield return "/system/fonts/DroidSans.ttf";
    }

    /// <summary>Loads the first font of <paramref name="paths"/> that exists and holds every glyph of ASCII 33..126; null when none does
    /// (a missing or broken file is skipped, never thrown).</summary>
    public static ChatFont? Load(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                if (Rasterize(path, File.ReadAllBytes(path)) is { } font) return font;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        }
        return null;
    }

    static unsafe ChatFont? Rasterize(string path, byte[] data)
    {
        var info = new StbTrueType.stbtt_fontinfo();
        fixed (byte* p = data)
        {
            int offset = StbTrueType.stbtt_GetFontOffsetForIndex(p, 0);
            if (offset < 0 || StbTrueType.stbtt_InitFont(info, p, offset) == 0) return null;
            float scale = StbTrueType.stbtt_ScaleForMappingEmToPixels(info, EmPixels);
            int ascent, descent, gap;
            StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &gap);
            int asc = (int)MathF.Round(ascent * scale);
            // the baseline of 'H' above its bottom gives the cap height
            int x0, y0, x1, y1;
            StbTrueType.stbtt_GetCodepointBitmapBox(info, 'H', scale, scale, &x0, &y0, &x1, &y1);
            var font = new ChatFont(path, asc, Math.Max(1, y1 - y0));
            for (int c = First; c <= Last; c++)
            {
                int adv, lsb;
                StbTrueType.stbtt_GetCodepointHMetrics(info, c, &adv, &lsb);
                int w, h, xo, yo;
                byte* bmp = StbTrueType.stbtt_GetCodepointBitmap(info, scale, scale, c, &w, &h, &xo, &yo);
                var cov = new byte[Math.Max(0, w * h)];
                if (bmp != null && cov.Length > 0) new ReadOnlySpan<byte>(bmp, cov.Length).CopyTo(cov);
                if (bmp != null) StbTrueType.stbtt_FreeBitmap(bmp, null);
                // a printable glyph without a single pixel means a font that lacks it (the .notdef box would still have some)
                if (c != ' ' && cov.AsSpan().IndexOfAnyExcept((byte)0) < 0) return null;
                font._glyphs[c - First] = new Glyph(Math.Max(1, (int)MathF.Round(adv * scale)), xo, asc + yo, w, h, cov);
            }
            return font;
        }
    }

    /// <summary>True when <paramref name="c"/> is one of the characters the font draws (ASCII 32..126).</summary>
    public static bool Has(char c) => c is >= (char)First and <= (char)Last;

    /// <summary>Width of one character in pixels (0 for a character outside ASCII 32..126).</summary>
    public int Advance(char c) => Has(c) ? _glyphs[c - First].Advance : 0;

    /// <summary>Number of pixels the glyph of <paramref name="c"/> covers (coverage above 0); 0 for a space or an unknown character.</summary>
    public int InkPixels(char c)
    {
        if (!Has(c)) return 0;
        int n = 0;
        foreach (byte b in _glyphs[c - First].Coverage) if (b != 0) n++;
        return n;
    }

    /// <summary>Width of <paramref name="text"/> in pixels.</summary>
    public int Measure(string text)
    {
        int sum = 0;
        foreach (char c in text) sum += Advance(c);
        return sum;
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its left edge at <paramref name="x"/>; <paramref name="y"/> is the top of the capital letters
    /// (<see cref="CapHeight"/> rows). The glyphs are blended over the buffer by their coverage and get a 1 px black shadow so they read
    /// over the race picture; <paramref name="alpha"/> scales the opacity.
    /// </summary>
    public void Draw(FrameBuffer fb, string text, int x, int y, uint rgb, int alpha = 255)
    {
        Paint(fb, text, x + 1, y + 1, 0, alpha * 3 / 4);
        Paint(fb, text, x, y, rgb, alpha);
    }

    void Paint(FrameBuffer fb, string text, int x, int y, uint rgb, int alpha)
    {
        // the ascent of the font puts the baseline at Ascent below the line top; y is the top of the capitals instead
        int top = y - (Ascent - CapHeight);
        foreach (char c in text)
        {
            if (!Has(c)) continue;
            ref readonly var g = ref _glyphs[c - First];
            for (int sy = 0; sy < g.Height; sy++)
            {
                int py = top + g.Top + sy;
                if ((uint)py >= (uint)fb.Height) continue;
                for (int sx = 0; sx < g.Width; sx++)
                {
                    int px = x + g.Left + sx;
                    if ((uint)px >= (uint)fb.Width) continue;
                    int a = g.Coverage[sy * g.Width + sx] * alpha / 255;
                    if (a > 0) fb.Blend(px, py, rgb, a);
                }
            }
            x += g.Advance;
        }
    }

    /// <summary>The text itself when it is at most <paramref name="room"/> pixels wide, else its longest beginning plus "..." that fits
    /// (with <paramref name="tail"/> the end behind a leading "..."); empty when not even "..." fits.</summary>
    public string Fit(string text, int room, bool tail = false)
    {
        if (Measure(text) <= room) return text;
        var cache = _cut ??= new();
        if (cache.TryGetValue((text, room, tail), out var cut)) return cut;
        if (tail)
        {
            int start = 1;
            while (start < text.Length && Measure(string.Concat("...", text.AsSpan(start))) > room) start++;
            cut = start < text.Length ? string.Concat("...", text.AsSpan(start)) : Measure("...") <= room ? "..." : "";
        }
        else
        {
            int n = text.Length;
            while (n > 0 && Measure(string.Concat(text.AsSpan(0, n).TrimEnd(), "...")) > room) n--;
            cut = n > 0 ? string.Concat(text.AsSpan(0, n).TrimEnd(), "...") : Measure("...") <= room ? "..." : "";
        }
        if (cache.Count > 512) cache.Clear();
        return cache[(text, room, tail)] = cut;
    }
}
