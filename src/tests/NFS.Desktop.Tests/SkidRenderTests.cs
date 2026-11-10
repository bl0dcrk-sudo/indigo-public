using NFS.Core;
using NFS.Desktop;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Tyre marks are drawn on the road (DTEXTURE skd0..3). [ref:2932]</summary>
public class SkidRenderTests
{
    static RaceDrive Drive(out string? pngDir)
    {
        pngDir = Environment.GetEnvironmentVariable("NFS_SKID_PNG_DIR");
        return new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
    }

    static int Diff(FrameBuffer a, FrameBuffer b)
    {
        int n = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            if (a.Rgba[i] != b.Rgba[i] || a.Rgba[i + 1] != b.Rgba[i + 1] || a.Rgba[i + 2] != b.Rgba[i + 2]) n++;
        return n;
    }

    [GameDataFact]
    public void HandbrakeSlide_MarksAppearInTheFrame_NormalDrivingLeavesTheFrameUntouched()
    {
        var d = Drive(out var png);
        for (int i = 0; i < 32 * 4; i++) d.Step(Autopilot.Input(d.Runner, 255));
        d.Skids.Buffer.Reset();                                // forget the launch (wheelspin) marks
        var off = new RenderOptions { Skids = false };
        var fb0 = new FrameBuffer(); var fb1 = new FrameBuffer();
        d.SnapToHost(); d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        d.Render(fb0, off); d.Render(fb1);
        Assert.Equal(0, d.Skids.Buffer.Added);
        Assert.Equal(0, Diff(fb0, fb1));                       // nothing drawn without marks

        for (int i = 0; i < 24; i++)
            d.Step(Autopilot.Input(d.Runner, 255) with { Buttons = PlayerInput.BtnHandbrake });
        d.SnapToHost();
        d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        // look back at the marks from above and behind the car
        d.Render(fb0, off); d.Render(fb1);
        int changed = Diff(fb0, fb1);
        Assert.True(d.Skids.Buffer.Added > 5);
        Assert.True(changed > 50, $"marks must cover pixels, changed {changed}");
        if (png != null) { Directory.CreateDirectory(png); fb0.SavePng(Path.Combine(png, "slide_off.png")); fb1.SavePng(Path.Combine(png, "slide_on.png")); }
        if (png != null)
        {
            // from ahead of the car, looking back along the slide
            var p = RaceScene.ToWorld(d.Car.Pos); var f = RaceScene.ToWorld(d.Car.Orient.Row2);
            var back = new Camera(640, 480);
            back.LookAlong(System.Numerics.Vector3.Normalize(-f - new System.Numerics.Vector3(0, 0.35f, 0)), System.Numerics.Vector3.UnitY);
            back.Position = p + System.Numerics.Vector3.Normalize(f) * 6f + System.Numerics.Vector3.UnitY * 2.2f;
            var a = new FrameBuffer(); var b = new FrameBuffer();
            d.Scene.Track.Render(a, back, off, d.Scene.Cars, d.Runner.DynObjects.Knocked, d.Runner.Tick, d.Skids.Buffer);
            d.Scene.Track.Render(b, back, new RenderOptions(), d.Scene.Cars, d.Runner.DynObjects.Knocked, d.Runner.Tick, d.Skids.Buffer);
            a.SavePng(Path.Combine(png, "back_off.png")); b.SavePng(Path.Combine(png, "back_on.png"));
            var tx = d.Scene.Track.SkidTextures;
            var sheet = new FrameBuffer(640, 160);
            sheet.Clear(90, 90, 90);
            for (int k = 0; k < 4; k++)
                if (tx[k] is { } t)
                    for (int y = 0; y < t.Height; y++)
                        for (int x = 0; x < t.Width; x++)
                            if (!t.Transparent[y * t.Width + x]) for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++) sheet.Set(k * 150 + x * 2 + sx, y * 2 + sy, t.Rgb[y * t.Width + x]);
            sheet.SavePng(Path.Combine(png, "textures.png"));
            Console.WriteLine(string.Join(" ", tx.Select(t => t == null ? "null" : $"{t.Width}x{t.Height}")));
        }
    }
}
