using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;
using NFS.Touch;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRect = Microsoft.Xna.Framework.Rectangle;

namespace NFS.Desktop;

/// <summary>MonoGame glue of the touch controls (Android, or <c>--touch</c> on the desktop with the mouse as one finger):
/// reads <see cref="TouchPanel"/>, runs <see cref="TouchTracker"/>, merges the result through <see cref="TouchInput"/> and
/// draws the overlay. The overlay is rendered at half of the back buffer resolution (cheap to regenerate and upload) and
/// stretched; it is rebuilt in full only when the mode or size changes. A moving steering knob (1/64 steps) or a pressed /
/// released button redraws and uploads only its own area – a full CPU redraw + upload froze phones for a moment on every
/// change (while steering, and on each button press and release).</summary>
internal sealed class TouchHost : IDisposable
{
    const int Downscale = 2;
    readonly TouchTracker _tracker = new();
    readonly TouchInput _input = new();
    readonly GraphicsDevice _gd;
    Texture2D? _tex;
    TouchOverlay? _overlay;
    TouchLayout? _layout;
    (TouchMode Mode, int W, int H, TouchButtons Held, int Steer) _drawn;
    TouchControls _controls;
    TouchControls _drawnControls;
    readonly (int X, int Y, int W, int H)[] _areas = new (int, int, int, int)[32];
    byte[] _scratch = [];
    readonly List<TouchPoint> _points = [];

    public TouchHost(GraphicsDevice gd, bool mouseAsTouch)
    {
        _gd = gd;
        TouchPanel.EnableMouseTouchPoint = mouseAsTouch;
    }

    /// <summary>Mode of the controls from the race state: the pedals only while a race is actually driven.</summary>
    public static TouchMode ModeFor(bool inRace, bool paused, bool resultsShown, bool replayPlayback) =>
        inRace && !paused && !resultsShown && !replayPlayback ? TouchMode.Race : TouchMode.Menu;

    public DesktopInput Apply(DesktopInput input, TouchMode mode, bool systemBack)
    {
        var pp = _gd.PresentationParameters;
        EnsureLayout(mode, Math.Max(1, pp.BackBufferWidth / Downscale), Math.Max(1, pp.BackBufferHeight / Downscale));
        _points.Clear();
        foreach (var t in TouchPanel.GetState())
            if (t.State is TouchLocationState.Pressed or TouchLocationState.Moved)
                _points.Add(new TouchPoint(t.Id, t.Position.X / Downscale, t.Position.Y / Downscale));
        _controls = _tracker.Update(_layout!, _points);
        return _input.Apply(input, _controls, mode, systemBack);
    }

    void EnsureLayout(TouchMode mode, int w, int h)
    {
        if (_layout != null && _layout.Mode == mode && _layout.Width == w && _layout.Height == h) return;
        _layout = TouchLayout.Create(w, h, mode);
        _tracker.Reset();
    }

    public void Draw(SpriteBatch sb)
    {
        if (_layout == null || _layout.Mode == TouchMode.Hidden) return;
        var key = (_layout.Mode, _layout.Width, _layout.Height, _controls.Held, Steer: (int)MathF.Round(_controls.Steer * 64));
        if (_tex == null || _overlay == null || _overlay.Width != _layout.Width || _overlay.Height != _layout.Height)
        {
            _tex?.Dispose();
            _overlay = new TouchOverlay(_layout.Width, _layout.Height);
            _tex = new Texture2D(_gd, _layout.Width, _layout.Height, false, SurfaceFormat.Color);
            _drawn = default;
        }
        if (key != _drawn)
        {
            int n = _drawn != default && key with { Steer = _drawn.Steer, Held = _drawn.Held } == _drawn
                ? _overlay.Redraw(_layout, _controls, _drawnControls, _areas) : -1;
            if (n >= 0)
                for (int i = 0; i < n; i++) UploadRegion(_areas[i]);
            else
            {
                _overlay.Draw(_layout, _controls);
                _tex.SetData(_overlay.Pixels);   // premultiplied RGBA = SpriteBatch's default AlphaBlend
            }
            _drawn = key;
            _drawnControls = _controls;
        }
        var pp = _gd.PresentationParameters;
        sb.Begin(samplerState: SamplerState.LinearClamp);
        sb.Draw(_tex, new XnaRect(0, 0, pp.BackBufferWidth, pp.BackBufferHeight), XnaColor.White);
        sb.End();
    }

    void UploadRegion((int X, int Y, int W, int H) r)
    {
        if (r.W <= 0 || r.H <= 0) return;
        int row = r.W * 4, n = row * r.H;
        if (_scratch.Length < n) _scratch = new byte[n];
        for (int y = 0; y < r.H; y++)
            Buffer.BlockCopy(_overlay!.Pixels, ((r.Y + y) * _overlay.Width + r.X) * 4, _scratch, y * row, row);
        _tex!.SetData(0, new XnaRect(r.X, r.Y, r.W, r.H), _scratch, 0, n);
    }

    public void Dispose() => _tex?.Dispose();
}
