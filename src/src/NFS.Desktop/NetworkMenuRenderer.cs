using NFS.Net.Client;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Draws the network screens (change network-game-sessions, task 5.2) as a pure function of <see cref="NetworkMenuController"/>
/// state (and the session view it reads) onto a <see cref="FrameBuffer"/>: <see cref="NetworkMenuLayout"/> builds the list of
/// elements of the frame, <see cref="ClassicNetBackend"/> draws it (change network-menu-look). It does not know the window.
/// <see cref="NetScreen.Race"/> draws only a small status box (the race itself is drawn by the host).
/// </summary>
internal static class NetworkMenuRenderer
{
    // The list and the layout (with its caches) of the drawing thread: reused, so that a warm frame allocates nothing.
    [ThreadStatic] static NetDrawList? _list;
    [ThreadStatic] static NetworkMenuLayout? _layout;

    static NetDrawList List => _list ??= new NetDrawList();
    static NetworkMenuLayout Layout => _layout ??= new NetworkMenuLayout();

    /// <summary>Draws the current screen. <paramref name="blinkTick"/> drives the edit cursor (any monotonic counter, ~60 Hz);
    /// <paramref name="background"/> fills the whole buffer with the dark chequer first (otherwise only panels are drawn over
    /// what is already in the buffer, e.g. the menu scene).</summary>
    public static void Draw(FrameBuffer fb, NetworkMenuController menu, int blinkTick = 0, bool background = true,
        NetworkChat? chat = null, long nowMicros = 0, NetSkin? skin = null)
    {
        var list = List;
        list.Clear();
        var scene = skin is { UsesOriginal: true } ? skin.Scene : null;
        // the backdrop of the game; a missing picture means classic for the whole frame (design D1)
        if (scene != null && background && !scene.DrawNetworkBackdrop(fb)) scene = null;
        if (background && scene == null)
        {
            list.Backdrop();
            ClassicNetBackend.Draw(fb, list);
            list.Clear();
        }
        bool caret = blinkTick / 30 % 2 == 0;
        if (menu.Screen == NetScreen.Summary && DrawVictory(fb, menu))
        {
            Layout.BuildBanner(list, menu, fb.Width, scene?.Fonts);
            DrawList(fb, list, scene);
            return;
        }
        Layout.Build(list, menu, chat, caret, fb.Width, scene?.Fonts);
        DrawList(fb, list, scene);
    }

    static void DrawList(FrameBuffer fb, NetDrawList list, MenuScene? scene)
    {
        if (scene != null) OriginalNetBackend.Draw(fb, list, scene);
        else ClassicNetBackend.Draw(fb, list);
    }

    /// <summary>The victory screen of the local winner; a missing art ends it at once so the summary is never blocked. [ref:1310]</summary>
    static bool DrawVictory(FrameBuffer fb, NetworkMenuController menu)
    {
        if (menu.VictoryWinner is not { } winner) return false;
        try
        {
            if (menu.VictoryArt?.Invoke() is not { } art) { menu.EndVictory(); return false; }
            art.Draw(fb, winner, NetworkMenuController.TrackIdOfMenuTrack(menu.Lobby?.Track ?? 0), menu.VictoryTicks, animated: true);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            menu.EndVictory();
            return false;
        }
    }

    /// <summary>What goes over the race picture (drawn by the host): the exit confirmation, the reconnect, straggler and spectator banners.</summary>
    public static void DrawRaceOverlay(FrameBuffer fb, NetworkMenuController menu, NetworkChat? chat = null, long nowMicros = 0, NetSkin? skin = null)
    {
        var list = List;
        list.Clear();
        var scene = skin is { UsesOriginal: true } ? skin.Scene : null;
        Layout.BuildRaceOverlay(list, menu, chat, nowMicros, fb.Width, scene?.Fonts);
        DrawList(fb, list, scene);
    }

    /// <summary>The network pause over the race picture: the dialog window of the game in the original look, else the classic panel.
    /// <paramref name="selection"/> 0 = resume, 1 = leave race.</summary>
    public static void DrawPause(FrameBuffer fb, int selection, NetSkin? skin = null)
    {
        var list = List;
        list.Clear();
        var scene = skin is { UsesOriginal: true } ? skin.Scene : null;
        Layout.BuildPause(list, selection, fb.Width, scene?.Fonts);
        DrawList(fb, list, scene);
    }
}
