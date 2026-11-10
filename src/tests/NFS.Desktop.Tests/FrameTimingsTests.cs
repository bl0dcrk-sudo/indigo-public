using NFS.Desktop;

namespace NFS.Desktop.Tests;

public class FrameTimingsTests
{
    [Fact]
    public void PerfSwitchParses()
    {
        Assert.False(DesktopOptions.Parse([]).Perf);
        Assert.True(DesktopOptions.Parse(["--perf"]).Perf);
        Assert.Contains("--perf", DesktopOptions.Usage);
    }

    [Fact]
    public void GfxNoticeSwitchParses()
    {
        Assert.False(DesktopOptions.Parse([]).GfxNotice);
        Assert.True(DesktopOptions.Parse(["--gfx-notice"]).GfxNotice);
        Assert.Contains("--gfx-notice", DesktopOptions.Usage);
    }

    [Fact]
    public void CamOptionParsesViews0To3()
    {
        Assert.Null(DesktopOptions.Parse([]).Cam);
        Assert.Equal(0, DesktopOptions.Parse(["--cam", "0"]).Cam);
        Assert.Equal(3, DesktopOptions.Parse(["--cam", "3"]).Cam);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--cam", "4"]));
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--cam", "-1"]));
        Assert.Contains("--cam", DesktopOptions.Usage);
    }

    [Fact]
    public void MainMenuNetworkHintFitsThe640Layer()
    {
        int w = NFS.Render.HudOverlay.TextWidth(Game1.NetworkHint);
        Assert.InRange(w, 1, 640 - 2 * 16);   // centred with a margin, previously 657 px from x=130 (cut at the right edge)
    }

    [Fact]
    public void ReportsMeanP95MaxPerStageAndSkipsWarmUp()
    {
        var t = new FrameTimings();
        // warm-up frame (skipped), then render 1..20 ms; upload is two SetData calls per frame
        t.Add(FrameTimings.Stage.Render, 500); t.EndFrame();
        for (int i = 1; i <= 20; i++)
        {
            t.Add(FrameTimings.Stage.Render, i);
            t.Add(FrameTimings.Stage.Upload, 1); t.Add(FrameTimings.Stage.Upload, 0.5);
            t.EndFrame();
        }
        Assert.Equal(21, t.Frames);
        string r = t.Report(skip: 1);
        Assert.StartsWith("perf: 20 frames", r);
        Assert.Contains("render mean 10.50 p95 19.00 max 20.00 ms", r);
        Assert.Contains("upload mean 1.50 p95 1.50 max 1.50 ms", r);
        t.Clear();
        Assert.Equal(0, t.Frames);
    }
}
