namespace NFS.Render;

/// <summary>Rectangle in screen pixels (x, y = top-left corner).</summary>
public readonly record struct ViewRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// Split-screen layout (<c>1</c>), pure logic with no host dependency. [ref:2645] [ref:3442]
/// Original: 3D scene view of player 0 (0,0,640,240), player 1 (0,240,640,240); [ref:2646]
/// the projection has origin (x, y − h/2) and a 640×(2h) window with a 90° angle: the same focal lengths as the full screen, centred on the middle of the half; [ref:2647]
/// clipping to the view rectangle cuts out the middle 240 lines. [ref:2648]
/// </summary>
public static class SplitLayout
{
    public const int ScreenWidth = 640, ScreenHeight = 480;
    public const int Players = 2;

    /// <summary>Screen half of a player (0 = top, 1 = bottom).</summary>
    public static ViewRect View(int player)
    {
        CheckPlayer(player);
        int h = ScreenHeight / Players;
        return new ViewRect(0, player * h, ScreenWidth, h);
    }

    /// <summary>Projection window of a player: (x, y − h/2, w, 2h) – a full-screen frame centred on the half. [ref:2649]</summary>
    public static ViewRect ProjectionWindow(int player)
    {
        var v = View(player);
        return new ViewRect(v.X, v.Y - v.Height / 2, v.Width, v.Height * 2);
    }

    /// <summary>Vertical offset of the player's HUD elements (the HUD table): player 1 = dy 1, player 2 = dy 0xF1. [ref:2650]</summary>
    public static int HudOffsetY(int player)
    {
        CheckPlayer(player);
        return player == 0 ? 1 : 0xF1;
    }

    /// <summary>Offset of the <c>hud2</c> shape (bottom HUD strip): player 1 y − 0xF0 (at the bottom of its half), [ref:2651]
    /// player 2 has no offset (at the bottom of the screen).</summary>
    public static int HudBottomOffsetY(int player)
    {
        CheckPlayer(player);
        return player == 0 ? -0xF0 : 0;
    }

    /// <summary>Camera of a player's view for rendering directly into the 640×240 buffer: horizontal focal length as in the full screen (45°), vertical as in the
    /// 640×480 frame (36°); projection centre (320, 120).</summary>
    public static Camera CreateCamera(int scale = 1, int? width = null)
    {
        int half = ScreenHeight / Players;
        double fy = -(ScreenHeight * scale / 2.0) / Math.Tan(36 * Math.PI / 180);
        // panorama (width > 640·scale): the same focal length as the 4:3 view, the horizontal angle grows (Hor+)
        int w = Math.Max(ScreenWidth * scale, width ?? ScreenWidth * scale);
        return new Camera(w, half * scale, 45, 36, focalY: fy, baseWidth: w > ScreenWidth * scale ? ScreenWidth * scale : null);
    }

    /// <summary>Drawing of both views (640×240 each) into the 640×480 screen buffer; in Enhanced mode the buffers are k× larger
    /// (<paramref name="scale"/> = k: views 640k×240k on a 640k×480k screen). Panorama: views and screen wider than 640k (<paramref name="viewWidth"/>
    /// = width of the view in scene pixels).</summary>
    public static void Compose(FrameBuffer screen, FrameBuffer player1, FrameBuffer player2, int scale = 1, int? viewWidth = null)
    {
        Blit(screen, player1, Scaled(View(0), scale, viewWidth));
        Blit(screen, player2, Scaled(View(1), scale, viewWidth));
    }

    static ViewRect Scaled(ViewRect r, int k, int? width = null) => new(r.X * k, r.Y * k, width ?? r.Width * k, r.Height * k);

    static void Blit(FrameBuffer screen, FrameBuffer view, ViewRect rect)
    {
        int w = Math.Min(rect.Width, Math.Min(view.Width, screen.Width - rect.X));
        for (int y = 0; y < rect.Height && y < view.Height && rect.Y + y < screen.Height; y++)
            Buffer.BlockCopy(view.Rgba, y * view.Width * 4, screen.Rgba, ((rect.Y + y) * screen.Width + rect.X) * 4, w * 4);
    }

    static void CheckPlayer(int player)
    {
        if ((uint)player >= Players) throw new ArgumentOutOfRangeException(nameof(player), "split screen has two players");
    }
}
