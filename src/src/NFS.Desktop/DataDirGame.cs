using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using NFS.Render;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace NFS.Desktop;

/// <summary>Minimal MonoGame host of <see cref="DataDirScreen"/>: draws the 640×480 screen letterboxed and turns a finger lift
/// (or a mouse click with <c>--pick-data</c> on the desktop, where the mouse is one finger) into a tap in screen coordinates.
/// Android: runs in its own activity before the game; <see cref="Finished"/> starts the game activity with the saved folder.</summary>
internal sealed class DataDirGame : Microsoft.Xna.Framework.Game
{
    readonly DataDirScreen _screen;
    readonly FrameBuffer _fb = new(DataDirScreen.W, DataDirScreen.H);
    readonly bool _mouseAsTouch;
    SpriteBatch _sb = null!;
    Texture2D _tex = null!;
    bool _done;

    /// <summary>Raised once when the screen ends (accepted folder or quit), before the game exits.</summary>
    public event Action<DataDirScreen.Outcome, string?>? Finished;

    public DataDirGame(DataDirScreen screen, bool mouseAsTouch)
    {
        _screen = screen;
        _mouseAsTouch = mouseAsTouch;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 960, PreferredBackBufferHeight = 720 };
        IsMouseVisible = true;
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        _tex = new Texture2D(GraphicsDevice, DataDirScreen.W, DataDirScreen.H, false, SurfaceFormat.Color);
        TouchPanel.EnableMouseTouchPoint = _mouseAsTouch;
    }

    protected override void Update(GameTime gameTime)
    {
        _screen.Update();
        if (PlatformBack.Consume() || Keyboard.GetState().IsKeyDown(Keys.Escape) && !_escHeld) _screen.Back();
        _escHeld = Keyboard.GetState().IsKeyDown(Keys.Escape);
        var pp = GraphicsDevice.PresentationParameters;
        var (fx, fy, fw, fh) = Layout.Fit(pp.BackBufferWidth, pp.BackBufferHeight, DataDirScreen.W, DataDirScreen.H);
        foreach (var t in TouchPanel.GetState())
        {
            if (t.State != TouchLocationState.Released || fw == 0) continue;
            _screen.Tap((int)((t.Position.X - fx) * DataDirScreen.W / fw), (int)((t.Position.Y - fy) * DataDirScreen.H / fh));
        }
        if (!_done && _screen.Result != DataDirScreen.Outcome.None)
        {
            _done = true;
            Finished?.Invoke(_screen.Result, _screen.ChosenDir);
            Exit();
        }
        base.Update(gameTime);
    }

    bool _escHeld;

    protected override void Draw(GameTime gameTime)
    {
        _screen.Render(_fb);
        _tex.SetData(_fb.Rgba);
        GraphicsDevice.Clear(XnaColor.Black);
        var pp = GraphicsDevice.PresentationParameters;
        var (x, y, w, h) = Layout.Fit(pp.BackBufferWidth, pp.BackBufferHeight, DataDirScreen.W, DataDirScreen.H);
        _sb.Begin(samplerState: SamplerState.PointClamp);
        _sb.Draw(_tex, new Microsoft.Xna.Framework.Rectangle(x, y, w, h), XnaColor.White);
        _sb.End();
        base.Draw(gameTime);
    }
}
