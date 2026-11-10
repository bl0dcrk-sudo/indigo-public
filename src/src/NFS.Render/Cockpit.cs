using NFS.Core;
using NFS.Core.Constants;
using NFS.Data;

namespace NFS.Render;

public readonly record struct CockpitValues(int Speed, int Rpm, int Redline, int Steer, int Gear,
    bool Metric = true, bool Mirrored = false);

/// <summary>
/// Per-car cockpit description read by <see cref="Cockpit"/> (the render side of <c>CarTraits.Cockpit</c>; NFS.Render cannot reference NFS.Game,
/// so the caller converts the trait to this struct). <see cref="Vanilla"/> is the original per-number rule and the single source for VanillaCarSource.
/// </summary>
/// <param name="Digital">Digital dash (original car 4): bar tachometer and numbers instead of needles.</param>
/// <param name="DashFile">DSHxx.QFS in DASHHUD/PC.</param>
/// <param name="ExtraFile">Extra HD file, painted with the HSV paint, plus the ext0/ext1 background images (original car 7).</param>
/// <param name="Tinted">Dash file recoloured with the HSV paint (original car 5).</param>
/// <param name="LeftMirror">Second mirror image "mir1" and the left mirror mask (original car 0).</param>
/// <param name="NoBitOverlays">No bit1/bit2 hubs before the wheel (original cars 3, 4).</param>
/// <param name="GearLetters">The gear is drawn as an image r/n/1.. (original car 2).</param>
/// <param name="MirrorOffsetX">Offset of the rear-view strip in the dash (original: 5 -> 495,49; 9 -> 504,62).</param>
public readonly record struct CockpitStyle(bool Digital, string DashFile, string? ExtraFile = null, bool Tinted = false,
    bool LeftMirror = false, bool NoBitOverlays = false, bool GearLetters = false, int MirrorOffsetX = 0, int MirrorOffsetY = 0)
{
    /// <summary>The vanilla rule of car type <paramref name="carType"/> (must satisfy <see cref="Cockpit.HasDashboard"/>).</summary>
    public static CockpitStyle Vanilla(int carType) => new(carType == 4, $"DSH{carType:00}.QFS",
        ExtraFile: carType == 7 ? "DSH07HD.FSH" : null, Tinted: carType == 5, LeftMirror: carType == 0, NoBitOverlays: carType is 3 or 4,
        GearLetters: carType == 2,
        MirrorOffsetX: carType == 5 ? 495 : carType == 9 ? 504 : 0, MirrorOffsetY: carType == 5 ? 49 : carType == 9 ? 62 : 0);
}

/// <summary>Software dashboard. Reads presentation values only. [ref:2496]
/// Rear view is supplied separately as the original 640x70 strip.</summary>
public sealed class Cockpit
{
    const string Root = "GAMEDATA/DASHHUD/PC/";
    readonly Dictionary<string, FshImage> _images = new();
    readonly FrameBuffer _background = new();
    readonly FrameBuffer _scene = new();
    readonly (int Source, int Destination, int Length)[] _view;
    readonly (int Source, int Destination, int Length)[][] _mirrorViews;
    readonly (int Source, int Destination, int Length)[] _mirror, _mirrorLeft;
    readonly Needle? _tach, _speed;
    readonly int[] _layout;
    readonly (int X, int Y)[] _lineOffsets;
    readonly string _gearChars;
    readonly CockpitStyle _style;
    public int CarType { get; }
    public int ViewBottom { get; }
    public (int X, int Y, int Width, int Height) ViewRect { get; }
    public bool Digital => _style.Digital;
    public bool MirrorEnabled { get; private set; }

    // HUDCON snapshot: the mirror flag is read at a fixed offset. [ref:2497]
    public static bool InitialMirror(int carType, ReadOnlySpan<byte> hudcon) =>
        HasDashboard(carType) && InitialMirror(carType == 4, hudcon);

    /// <summary>The same for an analogue (<paramref name="digital"/> false) or digital dash with a dashboard.</summary>
    public static bool InitialMirror(bool digital, ReadOnlySpan<byte> hudcon) =>
        !digital && (hudcon.Length != 0x98 || Bin.S32(hudcon, 0x80) != 0);

    public void ToggleMirror() { if (!Digital) MirrorEnabled = !MirrorEnabled; } // [ref:2498]
    /// <summary>Applies a saved HUDCON.DAT (+0x80) to a cockpit built from the game file (the user copy of the previous race).</summary>
    public void RestoreMirror(ReadOnlySpan<byte> hudcon) => MirrorEnabled = InitialMirror(Digital, hudcon);

    public static bool HasDashboard(int carType) => carType is >= 0 and <= 11; // [ref:2499]
    public bool Active(int cameraMode) => cameraMode == 0;

    /// <param name="style">Cockpit of the car's traits; null = the vanilla rule for <paramref name="carType"/> (then it must have a dashboard).
    /// The view tables (rect, bottom, colours, masks) are still indexed by <paramref name="carType"/> (a vanilla dash slot).</param>
    public Cockpit(int carType, string? gameDir = null, int paint = 0, CockpitStyle? style = null)
    {
        if (style is null && !HasDashboard(carType)) throw new ArgumentOutOfRangeException(nameof(carType));
        if ((uint)carType >= 12) throw new ArgumentOutOfRangeException(nameof(carType));
        _style = style ?? CockpitStyle.Vanilla(carType);
        CarType = carType;
        const string hudcon = "FEDATA/PC/CONFIG/HUDCON.DAT";
        MirrorEnabled = InitialMirror(_style.Digital, GameFiles.Exists(hudcon, gameDir) ? GameFiles.ReadRaw(hudcon, gameDir) : []);
        ViewBottom = RenderConstants.CockpitViewBottom[carType];
        var rect = RenderConstants.CockpitViewRect.Slice(carType * 8, 8).ToArray();
        ViewRect = (Bin.S16(rect, 0), Bin.S16(rect, 2), Bin.U16(rect, 4), Bin.U16(rect, 6));
        _gearChars = System.Text.Encoding.ASCII.GetString(RenderConstants.CockpitGearChars.ToArray());
        // four shadow strokes, centre last [ref:2500]
        _lineOffsets = Enumerable.Range(0, 5).Select(i => (RenderConstants.CockpitLineOffsets[i * 2], RenderConstants.CockpitLineOffsets[i * 2 + 1])).ToArray();
        var fsh = Fsh.Load(Root + _style.DashFile, gameDir);
        var hsv = CarPaint.Hsv(paint);
        Load(fsh, _style.Tinted ? hsv : null);
        if (_style.ExtraFile is { } extra) Load(Fsh.Load(Root + extra, gameDir), hsv);
        var attachment = fsh.Find("dash")!.Blocks.First(b => b.Code == 0x7C);
        _layout = Enumerable.Range(0, Digital ? 12 : 58).Select(i => Bin.S32(fsh.Data, attachment.Offset + 8 + 4 * i)).ToArray();
        if (!Digital)
        {
            var colours = RenderConstants.CockpitColours.Slice(carType * 6, 6).ToArray();
            uint Rgb(int o) => (uint)(colours[o] << 16 | colours[o + 1] << 8 | colours[o + 2]);
            _tach = new Needle(_layout, 0, 11, Rgb(0), Rgb(3));
            _speed = new Needle(_layout, 24, 16, Rgb(0), Rgb(3));
        }
        // .msk is a BIGF with 25 anonymous entries; Viv's named lookup [ref:2501]
        // deliberately skips empty names. Entry zero is the full-size view without HUD/mirror.
        var masks = Viv.Load(Root + "HUDDATA.VIV", gameDir).Get($"dsh{carType:00}.msk");
        byte[] Mask(int index)
        {
            int p = 16;
            for (int i = 0; i < index; i++) { p += 8; while (masks[p++] != 0) { } }
            int off = (int)Bin.U32BE(masks, p), size = (int)Bin.U32BE(masks, p + 4);
            return masks.AsSpan(off, size).ToArray();
        }
        _view = ReadMask(Mask(0));
        _mirrorViews = [ReadMask(Mask(10)), ReadMask(Mask(15))];
        _mirror = ReadMask(Mask(20), reflected: true);
        _mirrorLeft = _style.LeftMirror ? ReadMask(Mask(24), reflected: true) : [];
        _background.Clear(0, 0, 0);
        At(_background, "dash"); // [ref:2502]
        if (_style.ExtraFile != null) { At(_background, "ext0"); At(_background, "ext1"); }
    }

    void Load(Fsh fsh, int[]? hsv = null)
    {
        var pal = fsh.GlobalPalette();
        if (pal != null && hsv != null) Recolour(pal, hsv);
        foreach (var e in fsh.Entries)
            if (fsh.Decode(e, pal) is { } img)
            {
                if (e.Main.Code == 0x7B)
                {
                    var indices = fsh.Indices(e);
                    if (hsv != null && fsh.AttachedPalette(e) is { } attached)
                    {
                        Recolour(attached, hsv);
                        for (int i = 0; i < indices.Length; i++)
                            attached.AsSpan(indices[i] * 4, 4).CopyTo(img.Rgba.AsSpan(i * 4));
                    }
                    for (int i = 0; i < indices.Length; i++) img.Rgba[i * 4 + 3] = indices[i] == 255 ? (byte)0 : (byte)255;
                }
                _images[e.Name.TrimEnd()] = img;
            }
    }

    // type5 interior and type7 ext0/ext1 [ref:2503]
    // use the selected paint, with a separate highlight threshold from the original's HSV table (see CarPaint).
    public static void Recolour(byte[] pal, int[] hsv) => CarPaint.RecolourPalette(pal, hsv);

    // BAMF blit spans: each group adds 65536 to the 16-bit destination. [ref:2504]
    static (int Source, int Destination, int Length)[] ReadMask(byte[] data, bool reflected = false)
    {
        Bin.Require(Bin.HasTag(data, 0, "BAMF"), "not a dashboard mask");
        Bin.Require(Bin.S32(data, 12) == (reflected ? -65536 : 65536), "dashboard mask scale");
        var spans = new List<(int, int, int)>();
        int p = 20;
        for (int group = 0; group < Bin.U16(data, 8); group++)
        {
            int count = Bin.U16(data, p); p += 2;
            for (int i = 0; i < count; i++, p += 8)
                spans.Add((Bin.S32(data, p), Bin.U16(data, p + 4) + group * 65536, Bin.U16(data, p + 6)));
        }
        return spans.ToArray();
    }

    public bool SceneVisible(int x, int y) => _view.Any(s => y * 640 + x >= s.Destination && y * 640 + x < s.Destination + s.Length);

    // Actually selects the steering wheel, NOT the gear (the original name is misleading). [ref:2505]
    public static string WheelShape(int steer, bool mirrored = false)
    {
        int value = Math.Abs(steer);
        if (value < 3) return "00cc";
        string angle = value < 10 ? "04" : value < 40 ? "12" : value < 60 ? "18" : value <= 89 ? "27" : "38";
        // steer >= 0 and not mirrored keeps base 2 (cw shapes): a right turn (Steer) [ref:2506]
        // turns the wheel clockwise; negative steer (or mirrored) takes base 7 (cc).
        return angle + ((steer > 0) != mirrored ? "cw" : "cc");
    }

    public int SpeedAngle(int speed) => _speed?.Angle(Fx.Mul(Math.Max(0, speed), 0x1EB8)) ?? 0;
    public int TachAngle(int rpm) => _tach?.Angle(unchecked(Math.Max(0, rpm) * 0x41)) ?? 0;
    public (int X, int Y) SpeedTip(int speed) => _speed?.Tip(SpeedAngle(speed)) ?? default;
    public (int X, int Y) TachTip(int rpm) => _tach?.Tip(TachAngle(rpm)) ?? default;

    /// <param name="mirror">Rear view strip: the original 640x70, or (Enhanced, with <paramref name="scene"/>) 640K x 70K sharp strip rendered at the scene scale K.</param>
    /// <param name="scene">Enhanced 3D scene buffer (640K x 480K, K = strip width / 640, possibly wider = panorama): a K-times strip is written straight into it
    /// under the mirror mask, and the mask pixels of <paramref name="fb"/> (the 2D layer) are made transparent so the sharp strip shows through.</param>
    public void Draw(FrameBuffer fb, CockpitValues v, int cameraMode = 0, FrameBuffer? mirror = null, bool showHud = false, FrameBuffer? scene = null)
    {
        if (!Active(cameraMode)) return;
        if (fb.Width != 640 || fb.Height != 480) throw new ArgumentException("Dashboard requires 640x480", nameof(fb));
        v = v with { Speed = Math.Max(0, v.Speed) }; // clamp negative speed to 0 [ref:2507]
        fb.Rgba.CopyTo(_scene.Rgba, 0);
        _background.Rgba.CopyTo(fb.Rgba, 0);
        bool rear = mirror != null && MirrorEnabled;
        foreach (var s in rear ? _mirrorViews[showHud ? 1 : 0] : _view)
            _scene.Rgba.AsSpan(s.Source * 4, s.Length * 4).CopyTo(fb.Rgba.AsSpan(s.Destination * 4));
        if (rear)
        {
            At(fb, "mirr");
            if (_style.LeftMirror) At(fb, "mir1");
        }
        if (Digital) DrawDigital(fb, v);
        else
        {
            DrawNeedle(fb, _tach!, TachAngle(v.Rpm));
            DrawNeedle(fb, _speed!, SpeedAngle(v.Speed));
        }
        // hubs precede the wheel except on types 3/4 (style NoBitOverlays). [ref:2508]
        if (!_style.NoBitOverlays) { At(fb, "bit1"); At(fb, "bit2"); }
        At(fb, WheelShape(v.Steer, v.Mirrored));
        if (_style.GearLetters) At(fb, v.Gear == 0 ? "r" : v.Gear == 1 ? "n" : (v.Gear - 1).ToString());
        if (rear) DrawMirror(fb, mirror!, scene);
    }

    // Steps (-1/-2): source += 149, step = -1. [ref:2509]
    // The signed X scale reflects each span; offsets for MACH and F50 belong to the destination.
    void DrawMirror(FrameBuffer fb, FrameBuffer strip, FrameBuffer? scene)
    {
        if (scene != null && strip.Width != 640) { DrawMirrorScaled(fb, strip, scene); return; }
        if (strip.Width != 640 || strip.Height != 70) throw new ArgumentException("Mirror requires 640x70", nameof(strip));
        int offsetX = _style.MirrorOffsetX;
        int offsetY = _style.MirrorOffsetY;
        foreach (var spans in new[] { _mirrorLeft, _mirror })
            foreach (var s in spans)
                for (int i = 0; i < s.Length; i++)
                    strip.Rgba.AsSpan((s.Source + 149 - i) * 4, 4)
                        .CopyTo(fb.Rgba.AsSpan((s.Destination + offsetY * 640 + offsetX + i) * 4, 4));
    }

    /// <summary>Same span walk as the 640x70 path, but every dashboard pixel is a K x K block of the K-times strip written into the 3D scene (the
    /// reflection also flips the sub-pixels, so the picture is the exact K-times version of the original); the 2D layer pixel becomes transparent.
    /// The 2D layer is centred in a panorama scene, hence <c>x0</c>.</summary>
    void DrawMirrorScaled(FrameBuffer fb, FrameBuffer strip, FrameBuffer scene)
    {
        int k = strip.Width / 640;
        if (strip.Width != 640 * k || strip.Height != 70 * k) throw new ArgumentException("Mirror strip must be 640K x 70K", nameof(strip));
        if (scene.Height != 480 * k || scene.Width < 640 * k || (scene.Width - 640 * k) % (2 * k) != 0)
            throw new ArgumentException("Scene must be K times the dashboard layer (640K x 480K or a centred panorama)", nameof(scene));
        int x0 = (scene.Width - 640 * k) / 2;
        int offsetX = _style.MirrorOffsetX;
        int offsetY = _style.MirrorOffsetY;
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(strip.Rgba.AsSpan());
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(scene.Rgba.AsSpan());
        foreach (var spans in new[] { _mirrorLeft, _mirror })
            foreach (var s in spans)
                for (int i = 0; i < s.Length; i++)
                {
                    int from = s.Source + 149 - i, to = s.Destination + offsetY * 640 + offsetX + i;
                    int sx = from % 640 * k, sy = from / 640 * k, dx = x0 + to % 640 * k, dy = to / 640 * k;
                    for (int j = 0; j < k; j++)
                        for (int a = 0; a < k; a++)
                            dst[(dy + j) * scene.Width + dx + a] = src[(sy + j) * strip.Width + sx + k - 1 - a];
                    // panorama: also the edge columns stay transparent - FrameTargets.FinishOverlay repeats them into the side margins, and an opaque sample of the
                    // strip there smeared the last picture column across the whole margin; now the 3D scene shows next to the mirror
                    fb.Rgba.AsSpan(to * 4, 4).Clear();
                }
    }

    void At(FrameBuffer fb, string name)
    {
        var img = _images[name];
        OriginalHud.Blit(fb, img, img.PosX << 20 >> 20, img.PosY << 20 >> 20);
    }

    void DrawDigital(FrameBuffer fb, CockpitValues v)
    {
        // Indigo bar, gear R/N/1..6, units selected speed, fixed 62°C. [ref:2510]
        int width = _layout[2] - _layout[0];
        int fill = v.Redline <= 0 ? 0 : Math.Clamp(v.Rpm * width / v.Redline, 0, width);
        uint colour = (uint)(_images["tach"].Rgba[0] << 16 | _images["tach"].Rgba[1] << 8 | _images["tach"].Rgba[2]);
        for (int y = _layout[1]; y < _layout[3]; y++)
            for (int x = _layout[0]; x < _layout[0] + fill; x++) fb.Set(x, y, colour);
        Number(fb, _gearChars[Math.Clamp(v.Gear, 0, 7)].ToString(), _layout[8], _layout[9]);
        Number(fb, (Fx.Mul(v.Speed, v.Metric ? 0x39999 : 0x23CA5) / 65536).ToString(), _layout[6], _layout[7]);
        Number(fb, "62°", _layout[4], _layout[5]);
    }

    void Number(FrameBuffer fb, string text, int x, int y)
    {
        foreach (char c in text.Reverse())
        {
            var img = _images[c == '°' ? "digC" : $"dig{c}"];
            x -= img.Width;
            OriginalHud.Blit(fb, img, x, y);
        }
    }

    void DrawNeedle(FrameBuffer fb, Needle n, int angle)
    {
        var tip = n.Tip(angle);
        for (int i = 0; i < 5; i++)
        {
            var o = _lineOffsets[i];
            Line(fb, n.X + o.X, n.Y + o.Y, tip.X + o.X, tip.Y + o.Y, i == 4 ? n.Colour : n.Shadow);
        }
    }

    static void Line(FrameBuffer fb, int x, int y, int tx, int ty, uint colour)
    {
        // sort along the major axis; half-error, advance minor only at error <0. [ref:2511]
        // Needle endpoints from DSH are inside the screen, so no endpoint clipping is needed.
        int dx = Math.Abs(tx - x), dy = Math.Abs(ty - y);
        if (dy < dx)
        {
            if (tx < x) { (x, tx) = (tx, x); (y, ty) = (ty, y); }
            int error = dx / 2, step = ty < y ? -1 : 1;
            while (x <= tx)
            {
                fb.Set(x++, y, colour);
                error -= dy;
                if (error < 0) { y += step; error += dx; }
            }
        }
        else
        {
            if (ty < y) { (x, tx) = (tx, x); (y, ty) = (ty, y); }
            int error = dy / 2, step = tx < x ? -1 : 1;
            while (y <= ty)
            {
                fb.Set(x, y++, colour);
                error -= dx;
                if (error < 0) { x += step; error += dy; }
            }
        }
    }

    sealed class Needle
    {
        public readonly int X, Y, Radius;
        public readonly uint Colour, Shadow;
        readonly int[] _angles;
        public Needle(int[] data, int offset, int count, uint colour, uint shadow)
        {
            X = data[offset]; Y = data[offset + 1]; Colour = colour; Shadow = shadow;
            int dx = data[offset + 2] - X, dy = data[offset + 3] - Y;
            Radius = (int)Math.Sqrt(dx * dx + dy * dy); // integer sqrt [ref:2512]
            _angles = Enumerable.Range(0, count).Select(i => FxMath.Atan2(Y - data[offset + 3 + i * 2], data[offset + 2 + i * 2] - X)).ToArray();
        }
        public int Angle(int value) // wrap delta to 8 bits, truncate fraction [ref:2513]
        {
            int i = value / 65536;
            return i >= _angles.Length - 1 ? _angles[^1] : _angles[i] - (((_angles[i] - _angles[i + 1]) & 255) * (value & 65535) / 65536);
        }
        public (int X, int Y) Tip(int angle)
        {
            FxMath.SinCosTab(angle, out int sin, out int cos);
            return (X + Fx.Mul(cos, Radius), Y - Fx.Mul(sin, Radius));
        }
    }
}
