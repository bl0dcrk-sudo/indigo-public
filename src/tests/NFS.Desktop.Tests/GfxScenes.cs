using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Deterministic scenes for the graphics-mode tests (stage 1): the same set for the golden hashes of the mode [ref:3448]
/// Vanilla and for Enhanced comparisons. Each scene = <see cref="Render"/> into the given <see cref="FrameTargets"/>.</summary>
internal static class GfxScenes
{
    public static readonly string[] Names = ["day", "tunnel", "car", "chase", "cockpit", "split", "rain", "menu"];

    static RaceScene MenuScene(byte gameType, bool rain, Action<FeConfig>? edit = null)
    {
        var menu = MenuController.Load();
        menu.Config.GameType = gameType; menu.Config.RaceType = 0; menu.Config.OpponentCount = 0;
        menu.Config.TrafficOff = 1; menu.Config.Track = 1;
        edit?.Invoke(menu.Config);
        menu.Cheats.Effects.Rain = rain;
        return new RaceScene(new RaceLoader().Load(menu.BuildSetup()));
    }

    static RaceDrive Warm(RaceDrive d, int frames = 40)
    {
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < frames; i++) d.Frame(1.0 / 32, gas);
        return d;
    }

    /// <summary>The first track with a tunnel (Surroundings byte = 2) and the slice in its middle.</summary>
    static (TrackRenderer Track, int Slice) Tunnel()
    {
        foreach (int t in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var tr = new TrackRenderer(t);
            for (int i = 0; i < tr.Slices.Length; i++)
            {
                byte b = tr.Slices[i].Surroundings;
                if ((b >> 4 == 2 || (b & 0xF) == 2) && tr.Slices[(i + 4) % tr.Slices.Length].Surroundings == b) return (tr, i + 2);
            }
        }
        throw new InvalidOperationException("no tunnel");
    }

    /// <summary>Builds the scene <paramref name="name"/> (data loading, warming up the drive) and returns the drawing of one frame into <see cref="FrameTargets"/>
    /// (after <c>Configure</c>); the drawing can be called repeatedly (timing the render without the cost of building the scene).</summary>
    public static Action<FrameTargets> Prepare(string name, RenderOptions? options = null)
    {
        var opt = options ?? new RenderOptions();
        switch (name)
        {
            case "day":
            {
                var tr = new TrackRenderer(2);
                return t => tr.Render(t.BeginScene(), Camera.FromSlice(tr.Slices[120], 1.5f, 0f, t.SceneWidth, t.SceneHeight), opt);
            }
            case "tunnel":
            {
                var (tr, slice) = Tunnel();
                return t => tr.Render(t.BeginScene(), Camera.FromSlice(tr.Slices[slice], 1.5f, 0f, t.SceneWidth, t.SceneHeight), opt);
            }
            case "car":
            {
                // car close up: camera just behind the player's car
                var scene = new RaceScene("replay");
                var d = new RaceDrive(scene, countdown: false, automatic: true);
                Warm(d);
                scene.PresentPoses(1);
                return t => scene.Track.Render(t.BeginScene(),
                    RaceScene.ChaseCamera(d.Car.Pos, d.Car.Orient, 4.2f, 1.1f, t.SceneWidth, t.SceneHeight), opt, scene.RenderCars);
            }
            case "chase":
            {
                var d = Warm(new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true));
                return t => d.Render(t.BeginScene(), t.Overlay, opt);
            }
            case "cockpit":
            {
                var setup = RaceInfo.Setup("replay"); setup.Set(0x24, 2); setup.Set(0x78, 2);
                var d = new RaceDrive(new RaceScene(new RaceLoader().Load(setup)), countdown: false);
                while (d.Cam.Mode != 0) d.Cam.Cycle(d.Scene.PresentPoses(1), d.Car);
                if (!d.Scene.Cockpit!.MirrorEnabled) d.Scene.Cockpit.ToggleMirror();
                Warm(d, 24);
                return t => d.Render(t.BeginScene(), t.Overlay, opt);
            }
            case "split":
            {
                var d = Warm(new RaceDrive(MenuScene(1, false), countdown: false, automatic: true));
                return t => d.Render(t.BeginScene(), t.Overlay, opt);
            }
            case "rain":
            {
                int track = Enumerable.Range(0, 8).First(tr => new RaceDrive(MenuScene(0, true, c => c.Track = (ushort)tr), countdown: false).Weather.Zones.Count > 0);
                var d = new RaceDrive(MenuScene(0, true, c => c.Track = (ushort)track), countdown: false, automatic: true);
                d.Car.Trk.Slice = d.Weather.Zones[0].Center; d.SnapToHost();
                return t => d.Render(t.BeginScene(), t.Overlay, opt);   // rain: the state of the drops depends on the number of frames – see Render
            }
            case "menu":
            {
                var scene = new MenuScene(MenuController.Load());
                return t => scene.Render(t.BeginMenu());
            }
            default: throw new ArgumentException(name);
        }
    }

    /// <summary>One frame of the scene <paramref name="name"/> into <paramref name="t"/> (rain after 4 frames, so the streaks develop).</summary>
    public static void Render(string name, FrameTargets t, RenderOptions? options = null)
    {
        var draw = Prepare(name, options);
        for (int i = 0; i < (name == "rain" ? 4 : 1); i++) draw(t);
    }
}
