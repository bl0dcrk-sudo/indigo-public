using NFS.Core;
using NFS.Data;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class CockpitTests
{
    [GameDataFact]
    public void AllTwelvePlayerDashboardsLoadAndRender()
    {
        // checks <12: includes Calà/Isdera; bonus types >=12 have no dashboard. [ref:2731]
        for (int type = 0; type < 12; type++)
        {
            var dash = new Cockpit(type);
            Assert.True(Cockpit.HasDashboard(type));
            Assert.Equal(type, dash.CarType);
            Assert.Equal(dash.ViewRect.Y + dash.ViewRect.Height, dash.ViewBottom);
            foreach (int steer in new[] { -127, 0, 127 })
            {
                var fb = new FrameBuffer(); fb.Clear(1, 2, 3);
                dash.Draw(fb, new(40 * Fx.One, 4000, 7000, steer, 4));
                Assert.NotEqual(0x010203u, fb.Get(320, 479));
                Assert.Equal(0x010203u, fb.Get(320, 180));
            }
        }
        foreach (int type in new[] { -1, 12, 15, 31 }) Assert.False(Cockpit.HasDashboard(type));
    }

    [GameDataFact]
    public void AnalogNeedlesMatchAsmWithRealF355CalibrationPoints()
    {
        // DSH02/dash +0x7C attachment: tach pivot 379,333; speed pivot 261,333.
        // Independent calculation from the original atan table
        // and original integer arithmetic, including the signed angle wrap.
        var dash = new Cockpit(2);
        foreach (var (speed, angle, tip) in new[] {
            (0, -380, (242,353)), (1, -388, (241,352)), (25, 457, (236,324)),
            (50, 205, (269,307)), (100, -128, (280,352)), (200, -128, (280,352)) })
        {
            Assert.Equal(angle, dash.SpeedAngle(speed * Fx.One));
            Assert.Equal(tip, dash.SpeedTip(speed * Fx.One));
        }
        foreach (var (rpm, angle, tip) in new[] {
            (0, -384, (361,351)), (500, -419, (357,347)), (1000, -455, (355,342)),
            (3500, 384, (361,315)), (7000, 92, (401,319)), (12000, -141, (396,353)) })
        {
            Assert.Equal(angle, dash.TachAngle(rpm));
            Assert.Equal(tip, dash.TachTip(rpm));
        }
        Assert.Equal(dash.SpeedTip(0), dash.SpeedTip(-Fx.One));
        Assert.Equal(dash.TachTip(0), dash.TachTip(-1000));
        var fb = new FrameBuffer();
        dash.Draw(fb, new(25 * Fx.One, 3500, 7000, 0, 1));
        Assert.Equal(0xF9F5EBu, fb.Get(236, 324));
        Assert.Equal(0xF9F5EBu, fb.Get(361, 315));
    }

    [Fact]
    public void WheelUsesOriginalThresholdsAndMirroredDirection()
    {
        // <3 neutral, <10/40/60/90 select 04/12/18/27/38; +Steer (right turn) → cw, −Steer → cc. [ref:2732]
        foreach (var (steer, shape) in new[] {
            (-127,"38cc"), (-90,"38cc"), (-89,"27cc"), (-60,"27cc"), (-59,"18cc"),
            (-40,"18cc"), (-39,"12cc"), (-10,"12cc"), (-9,"04cc"), (-3,"04cc"),
            (-2,"00cc"), (0,"00cc"), (2,"00cc"), (3,"04cw"), (9,"04cw"), (10,"12cw"),
            (39,"12cw"), (40,"18cw"), (59,"18cw"), (60,"27cw"), (89,"27cw"), (90,"38cw"), (127,"38cw") })
        {
            Assert.Equal(shape, Cockpit.WheelShape(steer));
            Assert.Equal(Cockpit.WheelShape(-steer), Cockpit.WheelShape(steer, true));
        }
    }

    [GameDataFact]
    public void ViewMaskStopsSceneAtDashboardAndKeepsWindshieldPixels()
    {
        for (int type = 0; type < 12; type++)
        {
            var dash = new Cockpit(type);
            var a = new FrameBuffer(); a.Clear(13, 31, 47);
            var b = new FrameBuffer(); b.Clear(71, 83, 97);
            dash.Draw(a, new(0, 0, 7000, 0, 1));
            dash.Draw(b, new(0, 0, 7000, 0, 1));
            int visible = 0;
            for (int y = 0; y < 480; y++)
                for (int x = 0; x < 640; x++)
                    if (!dash.SceneVisible(x, y)) Assert.Equal(a.Get(x, y), b.Get(x, y));
                    else if (a.Get(x, y) != b.Get(x, y)) visible++;
            Assert.True(visible > 100000);
            Assert.True(dash.SceneVisible(320, 180));
            Assert.False(dash.SceneVisible(320, dash.ViewBottom));
        }
    }

    [GameDataFact]
    public void ExternalAndHoodCamerasDoNotDrawDashboard()
    {
        var dash = new Cockpit(0);
        foreach (int camera in new[] { 1, 2, 3 })
        {
            var fb = new FrameBuffer(); fb.Clear(1, 2, 3);
            string before = fb.Sha256();
            dash.Draw(fb, new(0, 0, 7000, 127, 1), camera);
            Assert.Equal(before, fb.Sha256());
        }
    }

    [GameDataFact]
    public void IndigoHasDigitalSpeedGearAndProportionalRevBar()
    {
        var dash = new Cockpit(4);
        Assert.True(dash.Digital);
        foreach (int rpm in new[] { 0, 3500, 7000, 14000 })
        {
            var fb = new FrameBuffer(); fb.Clear(1, 2, 3);
            dash.Draw(fb, new(50 * Fx.One, rpm, 7000, 0, 1));
            int end = 266 + Math.Clamp(rpm * 105 / 7000, 0, 105); // DSH04 attachment
            if (end > 266) Assert.Equal(fb.Get(266, 332), fb.Get(end - 1, 332));
            if (end is > 266 and < 371) Assert.NotEqual(fb.Get(end - 1, 332), fb.Get(end, 332));
        }
        var metric = new FrameBuffer(); var imperial = new FrameBuffer(); var gear = new FrameBuffer();
        dash.Draw(metric, new(50 * Fx.One, 3500, 7000, 0, 1));
        dash.Draw(imperial, new(50 * Fx.One, 3500, 7000, 0, 1, false));
        dash.Draw(gear, new(50 * Fx.One, 3500, 7000, 0, 4));
        Assert.NotEqual(metric.Sha256(), imperial.Sha256());
        Assert.NotEqual(metric.Sha256(), gear.Sha256());
        var reverse = new FrameBuffer(); var stopped = new FrameBuffer();
        dash.Draw(reverse, new(-Fx.One, 3500, 7000, 0, 0));
        dash.Draw(stopped, new(0, 3500, 7000, 0, 0));
        Assert.Equal(stopped.Sha256(), reverse.Sha256());
    }

    [GameDataFact]
    public void PaintRecoloursOnlyType5AndType7InteriorUsingExeShadowAndHighlightRanges()
    {
        // Paint0 HSV=(0,0,210), threshold 80. Real palette keys: [ref:2733]
        // DSH05/dash[512,479]=(0,175,0); DSH07HD ext0=(0,230,0), ext1=(0,181,0).
        var grey = new FrameBuffer(); var dark = new FrameBuffer();
        new Cockpit(5, paint: 0).Draw(grey, new(0, 0, 7000, 0, 1));
        new Cockpit(5, paint: 1).Draw(dark, new(0, 0, 7000, 0, 1));
        Assert.Equal(0x7B7B7Bu, grey.Get(512, 479)); // 210*47/80
        Assert.Equal(0x191919u, dark.Get(512, 479)); // paint1: 60*47/110
        new Cockpit(7, paint: 0).Draw(grey, new(0, 0, 7000, 0, 1));
        Assert.Equal(0xE7E7E7u, grey.Get(93, 309)); // 210+(255-210)*22/47
        Assert.Equal(0x8B8B8Bu, grey.Get(639, 286)); // 210*53/80
    }

    [GameDataFact]
    public void DriveReadsCarValuesAndKeepsDashboardWhenHudIsOff()
    {
        var setup = RaceInfo.Setup("replay"); setup.Set(0x24, 4); setup.Set(0x78, 2);
        var scene = new RaceScene(new RaceLoader().Load(setup));
        var drive = new RaceDrive(scene, countdown: false) { ShowHud = false };
        drive.ToggleHud(); // HUD off -> on+map
        drive.ToggleHud(); drive.ToggleHud(); // off, map off
        var pose = scene.PresentPoses(1);
        while (drive.Cam.Mode != 0) drive.Cam.Cycle(pose, drive.Car);
        drive.Car.Steer = -127; drive.Car.Rpm = 3500; drive.Car.LocalVel = new(0, 0, 50 * Fx.One);
        drive.SnapToHost();   // stage 5a.2: the dashboard reads the published frame, not the live car
        var state = (drive.Car.Pos, drive.Car.Orient, drive.Car.LocalVel, drive.Car.Steer, drive.Car.Rpm, drive.Car.Gear, drive.Runner.Tick);
        var fb = new FrameBuffer(); drive.Render(fb);
        Assert.Equal(state, (drive.Car.Pos, drive.Car.Orient, drive.Car.LocalVel, drive.Car.Steer, drive.Car.Rpm, drive.Car.Gear, drive.Runner.Tick));
        var expected = new FrameBuffer(); expected.Clear(1, 2, 3);
        scene.Cockpit!.Draw(expected, new(50 * Fx.One, 3500, drive.Runner.Specs.Redline, -127, drive.Car.Gear,
            drive.HudMetric, scene.Session.Setup.Mirrored));
        Assert.Equal(expected.Get(320, 479), fb.Get(320, 479));
        Assert.Equal(0, drive.Steps);
    }
}
