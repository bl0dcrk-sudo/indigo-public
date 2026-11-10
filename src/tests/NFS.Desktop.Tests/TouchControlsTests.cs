using NFS.Core;
using NFS.Game.FrontEnd;
using NFS.Render;
using NFS.Touch;

namespace NFS.Desktop.Tests;

/// <summary>On-screen controls (stage Android): layout, multitouch mapping, merging into <see cref="DesktopInput"/> as a keyboard
/// would, and the overlay picture. The simulation itself is untouched: touch only fills <see cref="DriveKeys"/>.</summary>
public class TouchControlsTests
{
    static readonly (int W, int H)[] Screens = [(1200, 540), (1280, 720), (960, 540), (800, 600), (1024, 768), (640, 360), (1200, 450)];

    static TouchPoint Tap(int id, RectF r) => new(id, r.CenterX, r.CenterY);
    static RectF Of(TouchLayout l, TouchButtons b) => l.Find(b)!.Rect;
    static DesktopInput Idle => new(default, [], false);

    // ---- layout ----

    [Theory]
    [InlineData(TouchMode.Race)]
    [InlineData(TouchMode.Menu)]
    public void LayoutStaysOnScreenAndNeverOverlapsForCommonAspectRatios(TouchMode mode)
    {
        foreach (var (w, h) in Screens)
        {
            var l = TouchLayout.Create(w, h, mode);
            Assert.NotEmpty(l.Controls);
            var rects = l.Controls.Select(c => (c.Id, c.Rect)).ToList();
            if (l.Steer is { } s) { rects.Add((TouchButtons.None, s.Track)); }
            foreach (var (id, r) in rects)
            {
                Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= w && r.Bottom <= h, $"{id} off screen at {w}x{h}: {r}");
                Assert.True(r.W > 0 && r.H > 0);
            }
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                {
                    var a = rects[i].Item2; var b = rects[j].Item2;
                    bool overlap = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
                    Assert.False(overlap, $"{rects[i].Item1} overlaps {rects[j].Item1} at {w}x{h}");
                }
            // the steering hit zone never swallows a button
            if (l.Steer is { } z)
                foreach (var c in l.Controls)
                    Assert.False(z.Hit.Contains(c.Rect.CenterX, c.Rect.CenterY), $"{c.Id} inside the steering zone at {w}x{h}");
        }
    }

    [Fact]
    public void LayoutScalesWithResolution()
    {
        var small = TouchLayout.Create(640, 360, TouchMode.Race);
        var big = TouchLayout.Create(1280, 720, TouchMode.Race);
        var a = Of(small, TouchButtons.Gas); var b = Of(big, TouchButtons.Gas);
        Assert.Equal(a.W * 2, b.W, 1);
        Assert.Equal(a.H * 2, b.H, 1);
        Assert.Equal(a.X * 2, b.X, 1);
        Assert.Equal(a.Y * 2, b.Y, 1);
    }

    [Fact]
    public void RaceLayoutHasSteeringOnTheLeftAndPedalsOnTheRight()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Race);
        Assert.True(l.Steer!.Track.CenterX < 600);
        foreach (var b in new[] { TouchButtons.Gas, TouchButtons.Brake, TouchButtons.Handbrake })
            Assert.True(Of(l, b).CenterX > 600, b.ToString());
        foreach (var b in new[] { TouchButtons.Camera, TouchButtons.LookBehind, TouchButtons.Hud, TouchButtons.Reset, TouchButtons.Horn, TouchButtons.Pause })
            Assert.True(Of(l, b).CenterY < 540 / 4f, b + " not in the top bar");
        Assert.Null(TouchLayout.Create(1200, 540, TouchMode.Menu).Steer);
        Assert.Empty(TouchLayout.Create(1200, 540, TouchMode.Hidden).Controls);
    }

    // ---- touch points -> controls ----

    [Fact]
    public void SteeringIsAnalogWithDeadZoneAndFullLockAtTheEnds()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Race);
        var t = l.Steer!.Track;
        var tracker = new TouchTracker();
        float Steer(float x) => tracker.Update(l, [new TouchPoint(1, x, t.CenterY)]).Steer;
        Assert.Equal(0f, Steer(t.CenterX));
        tracker.Update(l, []);
        Assert.Equal(0f, Steer(t.CenterX + 0.03f * t.W / 2));   // inside the dead zone
        tracker.Update(l, []);
        Assert.Equal(1f, Steer(t.Right));
        tracker.Update(l, []);
        Assert.Equal(-1f, Steer(t.X));
        tracker.Update(l, []);
        float half = Steer(t.CenterX + t.W / 4);
        Assert.InRange(half, 0.65f, 0.80f);   // half of the throw = ~3/4 lock (sensitivity 1.5)
        Assert.True(half > 0);   // right = positive, like the keyboard / pad
        tracker.Update(l, []);
        Assert.Equal(1f, Steer(t.CenterX + 0.70f * t.W / 2));   // full lock well before the end of the track
        tracker.Update(l, []);
        float small = Steer(t.CenterX + 0.20f * t.W / 2), smallBefore = (0.20f - SteerZone.DeadZone) / (1f - SteerZone.DeadZone);
        Assert.Equal(1.5f * smallBefore, small, 3);           // 50% more steering for the same finger travel
        Assert.Equal(127, new TouchControls(1f, TouchButtons.None).SteerByte);
        Assert.Equal(-127, new TouchControls(-1f, TouchButtons.None).SteerByte);
    }

    [Fact]
    public void SteeringFingerKeepsSteeringOutsideTheZoneUntilLifted()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Race);
        var t = l.Steer!.Track;
        var tracker = new TouchTracker();
        tracker.Update(l, [new TouchPoint(7, t.CenterX, t.CenterY)]);
        var gas = Of(l, TouchButtons.Gas);
        // the same finger wandering over the gas pedal still only steers (full right at x beyond the track), and presses nothing
        var c = tracker.Update(l, [new TouchPoint(7, gas.CenterX, gas.CenterY)]);
        Assert.Equal(1f, c.Steer);
        Assert.False(c.Has(TouchButtons.Gas));
        Assert.Equal(0f, tracker.Update(l, []).Steer);   // lifted
        // after lifting, a new finger on the gas pedal presses it
        Assert.True(tracker.Update(l, [new TouchPoint(8, gas.CenterX, gas.CenterY)]).Has(TouchButtons.Gas));
    }

    [Fact]
    public void MultitouchHoldsSteeringGasHandbrakeAndCameraAtOnce()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Race);
        var tracker = new TouchTracker();
        var c = tracker.Update(l, [
            new TouchPoint(1, l.Steer!.Track.Right, l.Steer.Track.CenterY),
            Tap(2, Of(l, TouchButtons.Gas)), Tap(3, Of(l, TouchButtons.Handbrake)), Tap(4, Of(l, TouchButtons.Camera))]);
        Assert.Equal(1f, c.Steer, 2);
        Assert.Equal(TouchButtons.Gas | TouchButtons.Handbrake | TouchButtons.Camera, c.Held);
    }

    [Fact]
    public void SlidingFromGasToBrakeSwitchesPedals()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Race);
        var tracker = new TouchTracker();
        var a = tracker.Update(l, [Tap(1, Of(l, TouchButtons.Gas))]);
        var b = tracker.Update(l, [Tap(1, Of(l, TouchButtons.Brake))]);
        Assert.Equal(TouchButtons.Gas, a.Held);
        Assert.Equal(TouchButtons.Brake, b.Held);
        Assert.Equal(TouchButtons.None, tracker.Update(l, [new TouchPoint(1, 600, 270)]).Held);   // empty middle of the screen
    }

    [Fact]
    public void HiddenModeIgnoresTouches()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Hidden);
        Assert.Equal(TouchControls.None, new TouchTracker().Update(l, [new TouchPoint(1, 100, 100)]));
    }

    // ---- merge into DesktopInput (the keyboard's input type) ----

    [Fact]
    public void RaceTouchFillsTheSameDriveKeysAsTheKeyboard()
    {
        var input = new TouchInput();
        var held = TouchButtons.Gas | TouchButtons.Handbrake | TouchButtons.Horn | TouchButtons.Camera | TouchButtons.Hud
                   | TouchButtons.LookBehind | TouchButtons.Reset | TouchButtons.GearUp;
        var r = input.Apply(Idle, new TouchControls(0.5f, held), TouchMode.Race, false);
        var k = r.Drive;
        Assert.True(k.Up && k.Handbrake && k.Horn && k.CycleCamera && k.ToggleHud && k.LookBehind && k.Reset && k.GearUp);
        Assert.False(k.Down || k.GearDown || k.Left || k.Right);
        Assert.Equal(64, k.AnalogSteer);   // analog like the pad stick
        Assert.False(r.Pause);

        var brake = input.Apply(Idle, new TouchControls(-1f, TouchButtons.Brake | TouchButtons.GearDown), TouchMode.Race, false).Drive;
        Assert.True(brake.Down && brake.GearDown);
        Assert.False(brake.Up);
        Assert.Equal(-127, brake.AnalogSteer);
    }

    [Fact]
    public void KeyboardKeysSurviveTheMergeAndTouchDoesNotOverrideAnalogWithZero()
    {
        var kb = new DesktopInput(new DriveKeys(true, false, true, false, false, false, false, AnalogSteer: 40), [], false);
        var r = new TouchInput().Apply(kb, TouchControls.None, TouchMode.Race, false);
        Assert.Equal(kb.Drive, r.Drive);
    }

    [Fact]
    public void PauseButtonAndSystemBackPauseOncePerPress()
    {
        var input = new TouchInput();
        var press = new TouchControls(0, TouchButtons.Pause);
        Assert.True(input.Apply(Idle, press, TouchMode.Race, false).Pause);
        Assert.False(input.Apply(Idle, press, TouchMode.Race, false).Pause);          // held: no repeat
        Assert.False(input.Apply(Idle, TouchControls.None, TouchMode.Race, false).Pause);
        Assert.True(input.Apply(Idle, TouchControls.None, TouchMode.Race, true).Pause);  // system Back
        Assert.False(input.Apply(Idle, TouchControls.None, TouchMode.Race, true).Pause); // still held
        Assert.False(input.Apply(Idle, TouchControls.None, TouchMode.Race, false).Pause);
        Assert.True(input.Apply(Idle, TouchControls.None, TouchMode.Race, true).Pause);
    }

    [Fact]
    public void MenuPadProducesEdgeTriggeredMenuKeysAndBackIsEscape()
    {
        var input = new TouchInput();
        IReadOnlyList<MenuKey> Keys(TouchButtons b, bool back = false) => input.Apply(Idle, new TouchControls(0, b), TouchMode.Menu, back).Menu;
        Assert.Equal([MenuKey.Down], Keys(TouchButtons.MenuDown));
        Assert.Empty(Keys(TouchButtons.MenuDown));   // held = no repeat
        Assert.Empty(Keys(TouchButtons.None));
        Assert.Equal([MenuKey.Up, MenuKey.Enter], Keys(TouchButtons.MenuUp | TouchButtons.MenuOk));
        Assert.Equal([MenuKey.Left], Keys(TouchButtons.MenuLeft));
        Assert.Equal([MenuKey.Right], Keys(TouchButtons.MenuRight));
        Assert.Equal([MenuKey.Escape], Keys(TouchButtons.MenuBack));
        Assert.Empty(Keys(TouchButtons.None));
        Assert.Equal([MenuKey.Escape], Keys(TouchButtons.None, back: true));   // Android Back in the menu = Esc
        // keyboard menu keys are kept
        var withKey = new DesktopInput(default, [MenuKey.Enter], false);
        Assert.Contains(MenuKey.Enter, input.Apply(withKey, TouchControls.None, TouchMode.Menu, false).Menu);
        // pedals do nothing in the menu
        var drive = input.Apply(Idle, new TouchControls(1, TouchButtons.Gas), TouchMode.Menu, false).Drive;
        Assert.Equal(default, drive);
    }

    [Fact]
    public void GfxButtonIsInTheTopRightOfTheMenuLayoutOnly()
    {
        foreach (var (w, h) in Screens)
        {
            var menu = TouchLayout.Create(w, h, TouchMode.Menu);
            var r = Of(menu, TouchButtons.Options);
            Assert.True(r.CenterX > w * 0.75f && r.CenterY < h * 0.25f, $"GFX not in the top right at {w}x{h}: {r}");
            Assert.Equal("GFX", menu.Find(TouchButtons.Options)!.Label);
            Assert.Null(TouchLayout.Create(w, h, TouchMode.Race).Find(TouchButtons.Options));   // never while driving
        }
    }

    [Fact]
    public void GfxButtonOpensThePortOptionsOncePerPressAndDoesNotPressMenuKeys()
    {
        var input = new TouchInput();
        var press = new TouchControls(0, TouchButtons.Options);
        var first = input.Apply(Idle, press, TouchMode.Menu, false);
        Assert.True(first.PortOptions);
        Assert.Empty(first.Menu);
        Assert.False(first.Pause);
        Assert.False(input.Apply(Idle, press, TouchMode.Menu, false).PortOptions);              // held: no repeat
        Assert.False(input.Apply(Idle, TouchControls.None, TouchMode.Menu, false).PortOptions);
        Assert.True(input.Apply(Idle, press, TouchMode.Menu, false).PortOptions);               // next tap
        Assert.False(input.Apply(Idle, press, TouchMode.Race, false).PortOptions);
    }

    [Fact]
    public void TouchTrackerPressesGfxAndTheCrossStillDrivesTheOptionsScreen()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Menu);
        Assert.Equal(TouchButtons.Options, new TouchTracker().Update(l, [Tap(1, Of(l, TouchButtons.Options))]).Held);
        // cross / OK / BACK = arrows / Enter / Esc, which is what the options screen reads
        var input = new TouchInput();
        var s = new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Enhanced, 0));
        void Press(TouchButtons b)
        {
            foreach (var key in input.Apply(Idle, new TouchControls(0, b), TouchMode.Menu, false).Menu) s.Handle(key);
            input.Apply(Idle, TouchControls.None, TouchMode.Menu, false);
        }
        Press(TouchButtons.MenuDown);
        Assert.Equal(PortOption.Scale, s.SelectedOption);
        Press(TouchButtons.MenuRight);
        Assert.Equal(1, s.Settings.Scale);
        Press(TouchButtons.MenuLeft);
        Assert.Equal(0, s.Settings.Scale);
        Press(TouchButtons.MenuOk);
        Assert.Equal(1, s.Settings.Scale);
        Assert.Equal([MenuKey.Escape], input.Apply(Idle, new TouchControls(0, TouchButtons.MenuBack), TouchMode.Menu, false).Menu);
    }

    [Fact]
    public void ModeFollowsTheRaceState()
    {
        Assert.Equal(TouchMode.Race, TouchHost.ModeFor(true, false, false, false));
        Assert.Equal(TouchMode.Menu, TouchHost.ModeFor(false, false, false, false));
        Assert.Equal(TouchMode.Menu, TouchHost.ModeFor(true, true, false, false));    // pause menu
        Assert.Equal(TouchMode.Menu, TouchHost.ModeFor(true, false, true, false));    // results
        Assert.Equal(TouchMode.Menu, TouchHost.ModeFor(true, false, false, true));    // replay playback
    }

    [Fact]
    public void PlatformBackIsConsumedOncePerRequest()
    {
        PlatformBack.Consume();
        Assert.False(PlatformBack.Consume());
        PlatformBack.Request();
        Assert.True(PlatformBack.Consume());
        Assert.False(PlatformBack.Consume());
    }

    [Fact]
    public void DesktopOptionsParseTouchFlag()
    {
        Assert.True(DesktopOptions.Parse(["--touch"]).Touch);
        Assert.False(DesktopOptions.Parse([]).Touch);
    }

    // ---- the same simulation: touch input = pad-like analog keys ----

    [GameDataFact]
    public void TouchDrivingMatchesEquivalentDriveKeysBitForBit()
    {
        static long[] Run(Func<int, DesktopInput> frame)
        {
            var d = new RaceDrive(new RaceScene("replay"), countdown: false);
            for (int i = 0; i < 160; i++) d.Frame(1.0 / 32, frame(i).Drive);
            return d.Runner.Cars.SelectMany(c => CarDump.Slots.Select(s => s.Get(c.Obj))).ToArray();
        }
        var touch = new TouchInput();
        var viaTouch = Run(i =>
        {
            float steer = i is >= 60 and < 100 ? 0.5f : 0f;
            var held = i < 120 ? TouchButtons.Gas : TouchButtons.Brake;
            return touch.Apply(Idle, new TouchControls(steer, held), TouchMode.Race, false);
        });
        var viaKeys = Run(i =>
        {
            sbyte steer = i is >= 60 and < 100 ? (sbyte)64 : (sbyte)0;
            return new DesktopInput(new DriveKeys(i < 120, i >= 120, false, false, false, false, false, AnalogSteer: steer), [], false);
        });
        Assert.Equal(viaKeys, viaTouch);
        Assert.Contains(viaTouch, v => v != 0);
    }

    // ---- overlay picture ----

    [Fact]
    public void OverlayDrawsPressedButtonsBrighterAndSteeringKnobFollowsTheFinger()
    {
        var l = TouchLayout.Create(1200, 540, TouchMode.Race);
        var idle = new TouchOverlay(1200, 540); idle.Draw(l, TouchControls.None);
        var held = new TouchOverlay(1200, 540); held.Draw(l, new TouchControls(1f, TouchButtons.Gas));
        var g = Of(l, TouchButtons.Gas);
        int Alpha(TouchOverlay o, float x, float y) => o.Pixels[((int)y * o.Width + (int)x) * 4 + 3];
        Assert.True(Alpha(held, g.X + 8, g.CenterY) > Alpha(idle, g.X + 8, g.CenterY));
        Assert.Equal(0, Alpha(idle, 600, 270));   // empty centre of the screen stays transparent
        var t = l.Steer!.Track;
        float knobRight = t.CenterX + (t.W / 2 - 0.095f * l.Unit * 0.6f);
        Assert.True(Alpha(held, knobRight, t.CenterY) > Alpha(idle, knobRight, t.CenterY));
        Assert.NotEqual(idle.Pixels, held.Pixels);
    }

    [Theory]
    [InlineData(1200, 540)]
    [InlineData(800, 600)]
    [InlineData(400, 180)]
    public void OverlayKnobOnlyRedrawGivesTheSamePixelsAsAFullRedraw(int w, int h)
    {
        var l = TouchLayout.Create(w, h, TouchMode.Race);
        var partial = new TouchOverlay(w, h);
        partial.Draw(l, new TouchControls(0f, TouchButtons.Gas));
        float prev = 0f;
        foreach (float steer in new[] { 0.7f, -0.4f, -1f, 1f, 0.02f, 0f })
        {
            var c = new TouchControls(steer, TouchButtons.Gas);
            var area = partial.RedrawSteer(l, c, prev);
            prev = steer;
            Assert.True(area.W > 0 && area.H > 0 && area.W * area.H < w * h / 4, $"area {area}");   // a small part of the overlay
            var full = new TouchOverlay(w, h); full.Draw(l, c);
            Assert.Equal(full.Pixels, partial.Pixels);
        }
    }

    [Theory]
    [InlineData(1200, 540, TouchMode.Race)]
    [InlineData(800, 600, TouchMode.Race)]
    [InlineData(400, 180, TouchMode.Race)]
    [InlineData(1200, 540, TouchMode.Menu)]
    public void OverlayButtonPressAndReleaseRedrawOnlyTheirAreaAndGiveTheSamePixelsAsAFullRedraw(int w, int h, TouchMode mode)
    {
        // Pressing or releasing a button used to redraw and upload the whole overlay – a visible freeze on phones.
        var l = TouchLayout.Create(w, h, mode);
        var partial = new TouchOverlay(w, h);
        var prev = TouchControls.None;
        partial.Draw(l, prev);
        var all = l.Controls.Select(c => c.Id).ToArray();
        var rng = new Random(27);
        var areas = new (int X, int Y, int W, int H)[32];
        for (int step = 0; step < 40; step++)
        {
            int first = rng.Next(all.Length);
            var held = prev.Held ^ all[first];                                       // one press or release
            if (step % 5 == 0 && all.Length > 1)                                     // sometimes two at once
                held ^= all[(first + 1 + rng.Next(all.Length - 1)) % all.Length];
            float steer = step % 3 == 0 ? (float)(rng.NextDouble() * 2 - 1) : prev.Steer;
            var c = new TouchControls(steer, held);
            int n = partial.Redraw(l, c, prev, areas);
            Assert.True(n > 0, $"step {step}: nothing redrawn");
            long px = 0;
            for (int i = 0; i < n; i++) px += (long)areas[i].W * areas[i].H;
            Assert.True(px < (long)w * h / 3, $"step {step}: {px} px redrawn of {w * h}");
            var full = new TouchOverlay(w, h); full.Draw(l, c);
            Assert.Equal(full.Pixels, partial.Pixels);
            prev = c;
        }
    }

    [Fact]
    public void OverlayRedrawReportsMissingRoomInsteadOfDrawingHalf()
    {
        var l = TouchLayout.Create(800, 600, TouchMode.Race);
        var o = new TouchOverlay(800, 600);
        o.Draw(l, TouchControls.None);
        var before = o.Pixels.ToArray();
        var everything = l.Controls.Aggregate(TouchButtons.None, (a, c) => a | c.Id);
        Assert.Equal(-1, o.Redraw(l, new TouchControls(0, everything), TouchControls.None, new (int, int, int, int)[1]));
        Assert.Equal(before, o.Pixels);
    }

    [Fact]
    public void OverlayPicturesForReview()
    {
        // Review pictures: NFS_TOUCH_REVIEW_DIR=<dir> writes PNGs over a dark background (race, race with fingers, menu).
        if (Environment.GetEnvironmentVariable("NFS_TOUCH_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        void Save(string name, int W, int H, TouchMode mode, TouchControls c)
        {
            var bg = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)   // stand-in for a frame: sky above, striped road below
                {
                    int o = (y * W + x) * 4;
                    bg[o] = (byte)(y < H / 2 ? 70 : 55 + (x / 40 % 2) * 6); bg[o + 1] = (byte)(y < H / 2 ? 90 + y / 6 : 58);
                    bg[o + 2] = (byte)(y < H / 2 ? 170 : 62); bg[o + 3] = 255;
                }
            var l = TouchLayout.Create(W, H, mode);
            var o2 = new TouchOverlay(W, H); o2.Draw(l, c);
            NFS.Data.PngWriter.WriteRgba(Path.Combine(dir, name), W, H, o2.CompositeOver(bg));
        }
        Save("touch_race_idle.png", 1200, 540, TouchMode.Race, TouchControls.None);
        Save("touch_race_pressed.png", 1200, 540, TouchMode.Race, new TouchControls(-0.6f, TouchButtons.Gas | TouchButtons.Handbrake | TouchButtons.Camera | TouchButtons.GearUp));
        Save("touch_menu.png", 1200, 540, TouchMode.Menu, new TouchControls(0, TouchButtons.MenuDown));
        Save("touch_race_4x3.png", 800, 600, TouchMode.Race, new TouchControls(0.3f, TouchButtons.Brake));
    }
}
