using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Gate (set to 1 at an icon animation step, cleared on entry; [ref:2794]
/// the main and setup menu loops: when the flag is 1, the iteration only [ref:2795]
/// forces a redraw). Evidence that the port matches this: icons advance one step per drawn frame (not per time), and the clock
/// of the bars/title stands still in the frame after the icon step, and after the gate is released it makes at most one overdue step.</summary>
public class MenuIconGateTests
{
    const string Main = "main";

    /// <summary>Number of frames with a step: step 0x3C while the phase &lt; 0x400 - from the original constants, not from measurement. [ref:2796]</summary>
    static int ExpectedStepFrames()
    {
        int n = 0;
        for (int anim = 0; anim < MenuScene.IconAnimFull; anim += MenuScene.IconAnimStep) n++;
        return n;
    }

    static FrameBuffer Draw(MenuScene scene) { var fb = new FrameBuffer(); scene.Render(fb); return fb; }

    /// <summary>Main menu after 0x28+ ticks since the value changed (the first frame remembers the icon values).</summary>
    static MenuScene Ready()
    {
        var scene = new MenuScene(MenuController.Load(null)) { AnimateChrome = true };
        Assert.Equal(Main, scene.Controller.CurrentScreen.Id);
        Draw(scene);                                   // anim = 0x400 (full), nothing moves
        Assert.False(scene.IconStepPending);
        return scene;
    }

    [Fact]
    public void SuspendedClockOnlyAccruesOneOverdueStep()
    {
        var a = new MenuAnimation();
        a.Advance(MenuAnimation.StepSeconds);
        Assert.Equal(1, a.Steps);
        for (int i = 0; i < 18; i++) a.Advance(MenuAnimation.StepSeconds, keyPressed: true, suspended: true);
        Assert.Equal(1, a.Steps);                       // not called: no step and no key pause [ref:2797]
        Assert.False(a.Paused);
        a.Advance(0);                                   // gate released: overdue deadline -> one step immediately [ref:2798]
        Assert.Equal(2, a.Steps);
        a.Advance(0);                                   // deadline = now + 0x14: there is no further overdue step
        Assert.Equal(2, a.Steps);
        a.Advance(MenuAnimation.StepSeconds);
        Assert.Equal(3, a.Steps);
    }

    [Fact]
    public void SuspendedClockStillRunsDownAnActivePause()
    {
        var a = new MenuAnimation();
        a.Advance(0, keyPressed: true);
        Assert.True(a.Paused);
        a.Advance(MenuAnimation.StepSeconds, suspended: true);   // the deadline is absolute - time keeps running [ref:2799]
        Assert.False(a.Paused);
        a.Advance(0);
        Assert.Equal(1, a.Steps);
    }

    [GameDataFact]
    public void IconPulseStepsOncePerDrawnFrameAndTheBarClockStandsStillMeanwhile()
    {
        var scene = Ready();
        scene.Advance(0.4);                            // > 0x28 ticks since the value changed
        long steps = scene.Animation.Steps;
        Assert.True(steps > 0);
        int stepFrames = 0;
        for (int i = 0; i < 40; i++)
        {
            Draw(scene);
            if (!scene.IconStepPending) break;
            stepFrames++;
            // Time 0: the icon pace depends on drawn frames, not on the clock (the original loop: one step per redraw).
            // A frame after an icon step (flag 1) does not count a bar step, even if more than 0x14 ticks have passed.
            scene.Advance(MenuAnimation.StepSeconds * 3);
            Assert.Equal(steps, scene.Animation.Steps);
        }
        Assert.Equal(ExpectedStepFrames(), stepFrames);
        scene.Advance(0);                                  // flag cleared on entry - the bar clock runs, 1 overdue step [ref:2800]
        Assert.Equal(steps + 1, scene.Animation.Steps);
        Assert.False(scene.IconStepPending);
    }

    [GameDataFact]
    public void IconPulseIgnoresWallClockBetweenFrames()
    {
        var fast = Ready();
        var slow = Ready();
        fast.Advance(0.4);
        slow.Advance(0.4);
        for (int i = 0; i < ExpectedStepFrames() + 2; i++)
        {
            fast.Advance(0.001);
            slow.Advance(0.2);
            Draw(fast); Draw(slow);
            Assert.Equal(fast.IconStepPending, slow.IconStepPending);
        }
        Assert.False(fast.IconStepPending);
    }

    [GameDataFact]
    public void KeyDuringIconStepDoesNotPauseTheBars()
    {
        var scene = Ready();
        scene.Advance(0.4);
        Draw(scene);
        Assert.True(scene.IconStepPending);
        scene.Advance(0.0, false, false, keyPressed: true);   // the key is read only when there is one, and there is none here [ref:2801]
        Assert.False(scene.Animation.Paused);
    }

    [GameDataFact]
    public void RendersMenuFramesAcrossTheIconPulseToPng()
    {
        string root = FindRoot();
        string dir = Path.Combine(root, "out", "gfx_check");
        Directory.CreateDirectory(dir);
        var scene = Ready();
        scene.Advance(0.4);
        var frames = new List<FrameBuffer>();
        for (int i = 0; i < ExpectedStepFrames() + 1; i++)
        {
            var fb = Draw(scene);
            if (i is 0 or 4 or 8) { string p = Path.Combine(dir, $"menu_anim_{i:D2}.png"); fb.SavePng(p); Assert.True(new FileInfo(p).Length > 1000); }
            frames.Add(fb);
            scene.Advance(MenuAnimation.StepSeconds);
        }
        Assert.NotEqual(frames[0].Sha256(), frames[4].Sha256());
        Assert.NotEqual(frames[4].Sha256(), frames[8].Sha256());
    }

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, Path.Combine("src", "NFS2SE.sln")))) return d.FullName;
        return Path.GetTempPath();
    }
}
