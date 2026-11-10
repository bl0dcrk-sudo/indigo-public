using NFS.Core.Constants;
using NFS.Data;

namespace NFS.Render;

public readonly record struct OriginalHudValues(int Speed, int Gear, int Lap, int Laps, int Place, int Racers,
    int LapTicks, int BestTicks, int Rpm, int Redline, int Tick, bool FlashLap, bool Metric, bool FlashBest = false, string? Rival = null,
    // band state: 0 = hud5, 1 = hud6 "WRONG WAY!" hazard (digits and LAP/POS labels hidden), 2 = hud7. [ref:2553]
    int Band = 0);

/// <summary>External-view HUD, using the original FSH artwork and layout attachments. CPU only.</summary>
public sealed class OriginalHud
{
    const string Root = "GAMEDATA/DASHHUD/PC/";
    readonly Dictionary<string, FshImage> _images = new();
    readonly (int X, int Y)[] _slots = new (int, int)[14];
    readonly string[] _digits;
    readonly sbyte[] _digitIndex, _digitWidth;
    readonly byte[] _gauge;
    readonly string[] _gaugeNames;
    readonly string[] _labels;
    readonly int _language;

    public OriginalHud(string? gameDir = null, int language = 0)
    {
        _language = language;
        var hud = Fsh.Load(Root + "HUD.FSH", gameDir);
        Load(hud);
        // HUDnn carries the centre band, depending on language. [ref:2554]
        Load(Fsh.Load(Root + $"HUD{language:00}.FSH", gameDir));
        Load(Fsh.Load("GAMEDATA/RENDER/PC/DTEXTURE.FSH", gameDir));
        Layout(hud, "hud0", [3, 4, 0, 8, 2, 7, 1]); // [ref:2555]
        Layout(hud, "hud2", [12, 11]);              // [ref:2556]
        Layout(hud, "hud3", [9]);
        _slots[5] = (0x214, 0x13E);
        _slots[10] = (0x140, 0x1D4);
        _slots[13] = (0x140, 0x24C);
        _digits = Enumerable.Range(0, 10).Select(i => $"gr0{i}").Append("grsp")
            .Concat(Enumerable.Range(0, 10).Select(i => $"re0{i}")).Append("resp")
            .Concat(Enumerable.Range(0, 10).Select(i => $"ye0{i}")).Append("yesp")
            .Concat(Enumerable.Range(0, 10).Select(i => $"spd{i}")).Append("spds")
            .Concat(["shfr", "shfn"]).Concat(Enumerable.Range(1, 6).Select(i => $"shf{i}")).ToArray();
        // uses the high byte of an unaligned dword, hence table VA +3. [ref:2557]
        _digitIndex = RenderConstants.HudDigitIndex.ToArray().Select(b => unchecked((sbyte)b)).ToArray();
        _digitWidth = RenderConstants.HudDigitWidth.ToArray().Select(b => unchecked((sbyte)b)).ToArray();
        _gauge = RenderConstants.HudGauge.ToArray();
        var names = RenderConstants.HudGaugeNames.ToArray();
        _gaugeNames = Enumerable.Range(0, 20).Select(i => System.Text.Encoding.ASCII.GetString(names, i * 5, 4)).ToArray();
        var text = GameFiles.ReadRaw(Root + RenderConstants.HudTextFiles[language], gameDir); // [ref:2558]
        _labels = Enumerable.Range(0, Bin.S32(text, 0) / 4).Select(i => Bin.CString(text, Bin.S32(text, i * 4), out _)).ToArray();
        for (int c = 0; c < _glyphs.Length; c++) _glyphs[c] = _images.GetValueOrDefault($"f{c:000}");
    }

    // glyph shapes f000..f255 by character and the centre band names: looked up once (formatting the names per frame allocated)
    readonly FshImage?[] _glyphs = new FshImage?[256];
    static readonly string[] BandNames = ["hud5", "hud6", "hud7"];
    FshImage? Glyph(char c) => c < _glyphs.Length ? _glyphs[c] : _images.GetValueOrDefault($"f{(int)c:000}");

    public (int X, int Y) Slot(int slot) => _slots[slot];
    /// <summary>HUDCON.DAT is a 0x98-byte snapshot; key fields are at [ref:2559]
    /// the glyph map at +0x0C and the text at +0x3C. This is separate from CONFIG.DAT.</summary>
    public static (bool Map, bool Text) Visibility(ReadOnlySpan<byte> hudcon) => hudcon.Length == 0x98
        ? (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hudcon[0x0C..]) != 0,
           System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hudcon[0x3C..]) != 0)
        : (true, true);
    static int Signed12(int n) => (n << 20) >> 20;
    void Load(Fsh fsh)
    {
        var pal = fsh.GlobalPalette();
        foreach (var e in fsh.Entries)
            if (fsh.Decode(e, pal) is { } img)
            {
                // rdr cmd 0x13/0x14: colour index FF is transparent even for the 2A palette.
                if (e.Main.Code == 0x7B)
                {
                    var idx = fsh.Indices(e);
                    for (int i = 0; i < idx.Length; i++) if (idx[i] == 255) img.Rgba[4 * i + 3] = 0;
                }
                _images.TryAdd(e.Name, img);
            }
    }
    void Layout(Fsh fsh, string name, int[] slots)
    {
        var e = fsh.Find(name)!;
        var attachment = e.Blocks.First(b => b.Code == 0x7C);
        for (int i = 0; i < slots.Length; i++)
            _slots[slots[i]] = (Signed12(e.Main.RawPosX) + Bin.S32(fsh.Data, attachment.Offset + 8 + i * 8),
                Signed12(e.Main.RawPosY) + Bin.S32(fsh.Data, attachment.Offset + 12 + i * 8));
    }

    public static string Time(int ticks) // absolute, minutes modulo 60, hundredths truncated [ref:2560]
    {
        Span<char> d = stackalloc char[8];
        return new string(d[..FormatTime(ticks, d)]);
    }

    /// <summary><see cref="Time"/> into <paramref name="d"/> (8 characters, "mm:ss.hh" with the minutes space padded); returns the length.</summary>
    static int FormatTime(int ticks, Span<char> d)
    {
        long t = Math.Abs((long)ticks);
        long m = t / 3840 % 60, sec = t / 64 % 60, h = t % 64 * 100 / 64;
        d[0] = m >= 10 ? (char)('0' + m / 10) : ' '; d[1] = (char)('0' + m % 10); d[2] = ':';
        d[3] = (char)('0' + sec / 10); d[4] = (char)('0' + sec % 10); d[5] = '.';
        d[6] = (char)('0' + h / 10); d[7] = (char)('0' + h % 10);
        return 8;
    }

    /// <summary>Appends <paramref name="value"/> at <paramref name="pos"/> like <c>{value,width}</c> (width &lt; 0: left aligned) of an interpolated string.</summary>
    static int Put(Span<char> d, int pos, int value, int width = 0, IFormatProvider? provider = null)
    {
        Span<char> tmp = stackalloc char[16];
        value.TryFormat(tmp, out int w, default, provider);
        int pad = Math.Max(0, Math.Abs(width) - w);
        if (width > 0) { d.Slice(pos, pad).Fill(' '); pos += pad; }
        tmp[..w].CopyTo(d[pos..]); pos += w;
        if (width < 0) { d.Slice(pos, pad).Fill(' '); pos += pad; }
        return pos;
    }
    /// <summary>Number n of the DTEXTURE shape <c>cnt{n}</c> drawn at <paramref name="tick"/>, or -1. The shapes come from the [ref:2561]
    /// table cnt1..cnt5 (blank plate, 3, 2, 1, GO!); draws table[4 − state] as the state counts 4 → 3 → 2 → 1 → 0 [ref:2562]
    /// and skips GO! for languages 1, 3 and 4. [ref:2563]</summary>
    public static int CountdownShape(int tick, int language = 0)
    {
        if (tick <= 0x120 || tick >= 0x220) return -1;
        int state = tick < 0x140 ? 4 : tick < 0x180 ? 3 : tick < 0x1C0 ? 2 : tick < 0x200 ? 1 : 0;
        if (state == 0 && language is 1 or 3 or 4) return -1;
        return 5 - state;
    }
    public static int GaugeLevel(int rpm, int redline) => redline <= 0 ? 0 : Math.Clamp(rpm * 14 / redline, 0, 14);

    public void Draw(FrameBuffer fb, OriginalHudValues v)
    {
        DrawCore(fb, v, -1);
        DrawCountdown(fb, v.Tick);
    }

    /// <summary>Split-screen HUD of one player (<paramref name="player"/> 0 = upper half, 1 = lower half): [ref:2564]
    /// draws the band, hud0, hud1 shapes shifted by <see cref="SplitLayout.HudOffsetY"/> (1 / 0xF1) and hud2 by [ref:2565]
    /// <see cref="SplitLayout.HudBottomOffsetY"/>, no hud3 plate and no rival name (stays 0 in split); [ref:2566]
    /// text slots are the hud0 layout + the same offset, slots 11/12 come from hud2. The countdown [ref:2567]
    /// is drawn once for the whole screen (<see cref="DrawCountdown"/>), not here.</summary>
    public void DrawSplit(FrameBuffer fb, OriginalHudValues v, int player)
    {
        _ = SplitLayout.HudOffsetY(player);   // validates the player
        DrawCore(fb, v with { Rival = null }, player);
    }

    /// <summary>Position of a text slot in the table of <paramref name="player"/> (-1 = the full-screen table).</summary>
    public (int X, int Y) Slot(int slot, int player)
    {
        var (x, y) = _slots[slot];
        if (player < 0) return (x, y);
        return slot is 11 or 12 ? (x, y + SplitLayout.HudBottomOffsetY(player)) : (x, y + SplitLayout.HudOffsetY(player));
    }

    void DrawCore(FrameBuffer fb, OriginalHudValues v, int player)
    {
        bool split = player >= 0;
        int dy = split ? SplitLayout.HudOffsetY(player) : 0;
        // first the five static shapes, then dynamic text. The second shape is [ref:2568]
        // the centre band of the driving direction: hud5 normally, hud6/hud7 while going the wrong way. [ref:2569]
        if (!split)
        {
            AtPosition(fb, "hud1");
            AtPosition(fb, v.Band is >= 0 and < 3 ? BandNames[v.Band] : $"hud{5 + v.Band}");
            AtPosition(fb, "hud2");
            AtPosition(fb, "hud3");
            AtPosition(fb, "hud0");
        }
        else
        {
            AtPosition(fb, v.Band is >= 0 and < 3 ? BandNames[v.Band] : $"hud{5 + v.Band}", dy);
            AtPosition(fb, "hud0", dy);
            AtPosition(fb, "hud1", dy);
            AtPosition(fb, "hud2", SplitLayout.HudBottomOffsetY(player));
        }
        // band 1 hides the digits of slots 0..2 (time, lap, position). [ref:2570]
        Span<char> buf = stackalloc char[40];
        if (v.Band != 1) Digits(fb, buf[..FormatTime(v.LapTicks, buf)], 0, v.Band, player);
        // band 1 hides the text of slots 7 and 8 (LAP and POS labels). [ref:2571]
        if (v.Band != 1) Text(fb, _labels[12], 7, player);
        if (v.Band != 1 && (!v.FlashLap || (v.Tick & 0x10) == 0))
        {
            int n = Put(buf, 0, v.Lap); buf[n++] = '/'; n = Put(buf, n, v.Laps);
            Digits(fb, buf[..n], 1, v.Band, player);
        }
        if (v.Band != 1) Text(fb, _labels[11], 8, player);
        if (v.Band != 1)
        {
            int n = Put(buf, 0, v.Place, 2); buf[n++] = '/'; n = Put(buf, n, v.Racers, -2);
            Digits(fb, buf[..n], 2, v.Band, player);
        }
        Digits(fb, buf[..Put(buf, 0, v.Gear, 0, System.Globalization.CultureInfo.InvariantCulture)], 3, v.Band, player);
        Digits(fb, buf[..Put(buf, 0, v.Speed, 3)], 4, v.Band, player);
        int level = GaugeLevel(v.Rpm, v.Redline);
        for (int i = 0; i < 3; i++) AtPosition(fb, _gaugeNames[_gauge[level + i * 15]], dy);
        Text(fb, _labels[13], 12, player);
        if (v.BestTicks != 0 && (!v.FlashBest || (v.Tick & 0x10) == 0)) Text(fb, buf[..FormatTime(v.BestTicks, buf)], 11, player);
        // after the record, slot 9 (hud3 layout) shows the name of the rival picked for this race. [ref:2572]
        if (!string.IsNullOrEmpty(v.Rival)) Text(fb, v.Rival, 9, player);
    }

    public void DrawCountdown(FrameBuffer fb, int tick)
    {
        int countdown = CountdownShape(tick, _language);
        if (countdown >= 0) Blit(fb, _images[$"cnt{countdown}"], 191, 89); // full 640×480 viewport [ref:2573]
    }

    void AtPosition(FrameBuffer fb, string name, int dy = 0)
    {
        var img = _images[name];
        Blit(fb, img, Signed12(img.PosX), Signed12(img.PosY) + dy);
    }
    void Digits(FrameBuffer fb, ReadOnlySpan<char> text, int slot, int band = 0, int player = -1)
    {
        var (x, y) = Slot(slot, player);
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i] == ' ' ? '-' : text[i] == ',' ? '.' : text[i] == '\'' ? ';' : text[i];
            int n = c - '-';
            if ((uint)n >= 15) continue;
            // slot 3 uses the shf font, slot 4 the speed font, slot 0 the red one; any other slot uses [ref:2574]
            // the grey one, or the yellow one (+0x16) while the wrong-way band is up.
            int index = slot == 3 ? n + 41 : _digitIndex[n] + (slot == 4 ? 33 : band != 0 ? 22 : slot == 0 ? 11 : 0);
            if (index >= 0) Blit(fb, _images[_digits[index]], x, y);
            x -= slot is 3 or 4 ? 29 : _digitWidth[n];
        }
    }
    int Advance(char c) => Glyph(c) is { } g ? g.Width + Signed12(g.PosX) : 10;
    void Text(FrameBuffer fb, ReadOnlySpan<char> raw, int slot, int player = -1)
    {
        // slots 0..8 are right aligned; slots >8 centred, glyph posY is relative to baseline. [ref:2575]
        var (x, y) = Slot(slot, player);
        Span<char> stack = stackalloc char[64];
        ReadOnlySpan<char> text = stack;   // raw.ToUpperInvariant()
        if (raw.Length <= stack.Length) { raw.ToUpperInvariant(stack); text = stack[..raw.Length]; }
        else text = raw.ToString().ToUpperInvariant();
        int count = text.Length;
        if (slot > 8)
        {
            // Add glyph advances while below the limit (slot 9: 0x62 px, others 0x280); at the [ref:2576]
            // limit the last counted char is dropped but the advance subtracted is that of the NEXT char (10 at NUL).
            int limit = slot == 9 ? 0x62 : 0x280, i = 0, w = 0;
            while (i < text.Length && w < limit) w += Advance(text[i++]);
            if (w >= limit) { w -= i < text.Length ? Advance(text[i]) : 10; i--; }
            count = Math.Max(i, 0);
            x += w >> 1;
        }
        for (int i = count - 1; i >= 0; i--)
            if (Glyph(text[i]) is { } g)
            {
                x -= g.Width;
                Blit(fb, g, x, y + Signed12(g.PosY));
                x -= Signed12(g.PosX);
            }
            else x -= 10;
    }
    public static void Blit(FrameBuffer fb, FshImage image, int x, int y)
    {
        for (int sy = 0; sy < image.Height; sy++)
            for (int sx = 0; sx < image.Width; sx++)
            {
                int px = x + sx, py = y + sy, o = (sy * image.Width + sx) * 4;
                if ((uint)px >= fb.Width || (uint)py >= fb.Height || image.Rgba[o + 3] == 0) continue;
                fb.Set(px, py, (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]));
            }
    }
}
