using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Buffers of one frame by graphics mode, without MonoGame types (testable without a window). [ref:3385]
/// <b>Vanilla:</b> one 640×480 buffer – 3D scene and 2D layer together, exactly like the old <c>_fb</c> (nothing is cleared between frames).
/// <b>Enhanced:</b> 3D scene at <c>640k×480k</c> (<see cref="Scene"/>) and a 2D layer 640×480 with an alpha mask (<see cref="Overlay"/>, cleared to
/// transparency at the start of each frame with a scene); in the menu (no scene) the whole image is an opaque 2D layer.
/// </summary>
internal sealed class FrameTargets
{
    FrameBuffer _overlay = new(640, 480);
    FrameBuffer? _scene;      // the picture shown (640k×480k)
    FrameBuffer? _wideOverlay; // panorama: the 2D layer centred in a ViewWidth×480 transparent frame (built by FinishOverlay)
    FrameBuffer? _composed;   // ComposeCpuReused result
    FrameBuffer? _smooth, _smoothSource; // SmoothOverlay: the MMPX 2× layer and the layer it was made from
    bool _smoothPremultiplied;
    FrameBuffer? _render;     // SSAA: the 3D scene drawn at 640k·s × 480k·s, resolved (box filter) into _scene
    bool _lastHadScene, _resolved;

    public GraphicsMode Mode { get; private set; } = GraphicsMode.Vanilla;
    /// <summary>Internal scale k of the 3D scene (always 1 in Vanilla).</summary>
    public int Scale { get; private set; } = 1;
    /// <summary>Supersampling (SSAA) factor s: the 3D scene is rendered at k·s and averaged down to k (1 = off, always 1 in Vanilla).</summary>
    public int Ssaa { get; private set; } = 1;
    /// <summary>Whether the last frame had a 3D scene (race, fly-through) – otherwise the whole image is in <see cref="Overlay"/>.</summary>
    public bool HasScene { get; private set; }

    /// <summary>Resolution factor the renderer draws at (k·s); cameras take <see cref="SceneWidth"/>×<see cref="SceneHeight"/>.</summary>
    public int RenderScale => Scale * Ssaa;
    public int SceneWidth => ViewWidth * RenderScale;
    public int SceneHeight => 480 * RenderScale;
    /// <summary>Width of the 3D frame at scale 1: 640, or wider in a panorama (<see cref="Panorama"/>); the height is always 480.</summary>
    public int ViewWidth { get; private set; } = 640;
    /// <summary>Panorama (Hor+) frame: the scene is <see cref="ViewWidth"/> &gt; 640 wide.</summary>
    public bool Wide => ViewWidth > 640;
    /// <summary>Panorama and a cockpit view: the dashboard / window edges are repeated into the side margins (see <see cref="FinishOverlay"/>).</summary>
    public bool ExtendEdges { get; set; }
    /// <summary>Warstwa 2D 640×480 (w Vanilla: ten sam bufor co scena).</summary>
    public FrameBuffer Overlay => _overlay;
    /// <summary>The 3D picture to show (640k×480k; in Vanilla: <see cref="Overlay"/>); with SSAA it is resolved from the render buffer on first use.</summary>
    public FrameBuffer Scene
    {
        get
        {
            if (_render != null && !_resolved && HasScene) { Compositor.DownsampleInto(_render, _scene!, Ssaa); _resolved = true; }
            return _scene ?? _overlay;
        }
    }

    /// <summary>Sets the mode, scale and SSAA (Vanilla ignores both; k·s ≤ <see cref="InternalScale.Max"/>); on a size change reallocates the buffers.</summary>
    /// <param name="viewWidth">Panorama: the 3D frame width at scale 1 (<see cref="Panorama.ViewWidth"/>), 640 = the original 4:3 frame; ignored in Vanilla.</param>
    public void Configure(GraphicsMode mode, int scale, int ssaa = 1, int viewWidth = 640)
    {
        int k = mode == GraphicsMode.Vanilla ? 1 : InternalScale.Clamp(scale);
        int s = mode == GraphicsMode.Vanilla ? 1 : Math.Clamp(ssaa, 1, Math.Max(1, InternalScale.Max / k));
        int vw = mode == GraphicsMode.Vanilla ? 640 : Math.Max(640, viewWidth);
        if (mode == Mode && k == Scale && s == Ssaa && vw == ViewWidth) return;
        Mode = mode; Scale = k; Ssaa = s; ViewWidth = vw;
        _scene = mode == GraphicsMode.Enhanced ? new FrameBuffer(vw * k, 480 * k) : null;
        _render = s > 1 ? new FrameBuffer(vw * k * s, 480 * k * s) : null;
        _wideOverlay = vw > 640 ? new FrameBuffer(vw, 480) : null;
        _overlay = new FrameBuffer(640, 480);
        _smooth = _smoothSource = null;
        _lastHadScene = false;
    }

    /// <summary>The layer is smoothed whenever it is magnified (<paramref name="boxWidth"/> wider than <paramref name="layerWidth"/>): MMPX 2× and then the
    /// linear filter to any size. A second MMPX pass (4×) was tried for big screens and left out: the font keeps its square shapes and the menu
    /// photos turn into flat blocks, while 2× + linear keeps them soft.</summary>
    public static bool Smooths(int layerWidth, int boxWidth) => boxWidth > layerWidth;

    /// <summary>2D SMOOTH (task 08j): the layer of <see cref="FinishOverlay"/> magnified 2× by <see cref="PixelArtScaler.Mmpx2x"/>, premultiplied over a
    /// scene. Kept while the layer does not change (<paramref name="changed"/> false: nothing to upload); no allocation once the size repeats.</summary>
    public FrameBuffer SmoothOverlay(out bool changed)
    {
        var layer = FinishOverlay();
        bool premultiply = HasScene;
        if (_smooth == null || _smoothSource == null || _smoothSource.Width != layer.Width)
        {
            _smooth = new FrameBuffer(layer.Width * 2, layer.Height * 2);
            _smoothSource = new FrameBuffer(layer.Width, layer.Height);
        }
        else if (premultiply == _smoothPremultiplied && layer.Rgba.AsSpan().SequenceEqual(_smoothSource.Rgba))
        {
            changed = false;
            return _smooth;
        }
        layer.Rgba.CopyTo(_smoothSource.Rgba, 0);
        _smoothPremultiplied = premultiply;
        PixelArtScaler.Mmpx2x(layer, _smooth, premultiply);
        changed = true;
        return _smooth;
    }

    /// <summary>The 2D layer to show over the 3D scene: <see cref="Overlay"/>, or in a panorama frame (scene wider than 640) that layer centred in a
    /// <see cref="ViewWidth"/>×480 transparent frame; with <see cref="ExtendEdges"/> (cockpit) its left / right edge column is repeated into the margins, so the
    /// dashboard pillars and windows continue to the sides. Call after everything has been drawn into <see cref="Overlay"/>.</summary>
    public FrameBuffer FinishOverlay()
    {
        if (_wideOverlay == null || !HasScene) return _overlay;
        var w = _wideOverlay;
        w.ClearTransparent();
        int x0 = (ViewWidth - 640) / 2;
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_overlay.Rgba.AsSpan());
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(w.Rgba.AsSpan());
        for (int y = 0; y < 480; y++)
        {
            src.Slice(y * 640, 640).CopyTo(dst.Slice(y * ViewWidth + x0, 640));
            if (!ExtendEdges) continue;
            dst.Slice(y * ViewWidth, x0).Fill(src[y * 640]);
            dst.Slice(y * ViewWidth + x0 + 640, ViewWidth - x0 - 640).Fill(src[y * 640 + 639]);
        }
        return w;
    }

    /// <summary>Start of a frame with a 3D scene: returns the buffer the renderer draws into (cameras must use <see cref="SceneWidth"/>×<see cref="SceneHeight"/>);
    /// in Enhanced the 2D layer is cleared to transparency.</summary>
    public FrameBuffer BeginScene()
    {
        HasScene = true;
        _lastHadScene = true;
        _resolved = false;
        if (Mode == GraphicsMode.Enhanced) _overlay.ClearTransparent();
        return _render ?? Scene;
    }

    /// <summary>Start of a frame without a 3D scene (menu, network screens): the whole image goes to <see cref="Overlay"/>. After a frame with a scene
    /// the layer is cleared once to black (no transparency is left from it); in Vanilla the buffer stays as in the old port.</summary>
    public FrameBuffer BeginMenu()
    {
        HasScene = false;
        if (Mode == GraphicsMode.Enhanced && _lastHadScene) _overlay.Clear(0, 0, 0);
        _lastHadScene = false;
        return _overlay;
    }

    /// <summary>Frame image composed on the CPU (tests, PNG dumps, autotest): Vanilla = 640×480 buffer; Enhanced = scene + 2D layer ×k.</summary>
    public FrameBuffer ComposeCpu()
    {
        if (Mode == GraphicsMode.Vanilla) return _overlay;
        return HasScene ? Compositor.Compose(Scene, FinishOverlay()) : Compositor.UpscaleNearest(_overlay, Scale);
    }

    /// <summary><see cref="ComposeCpu"/> into a buffer kept by this object (no allocation once the size repeats; host display filter, task 08i).
    /// The result is overwritten by the next call – use it before the next frame.</summary>
    public FrameBuffer ComposeCpuReused()
    {
        if (Mode == GraphicsMode.Vanilla) return _overlay;
        var scene = HasScene ? Scene : null;
        int w = scene?.Width ?? _overlay.Width * Scale, h = scene?.Height ?? _overlay.Height * Scale;
        if (_composed == null || _composed.Width != w || _composed.Height != h) _composed = new FrameBuffer(w, h);
        if (scene != null) Compositor.ComposeInto(scene, FinishOverlay(), _composed);
        else Compositor.UpscaleNearestInto(_overlay, _composed, Scale);
        return _composed;
    }
}
