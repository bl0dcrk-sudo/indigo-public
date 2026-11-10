using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>The port options screen (F7 / touch GFX): the model (rows, selection, value changes → new <see cref="GraphicsSettings"/>), the rules
/// for opening it, saving through port.json and the picture on the 2D layer. No window.</summary>
public class PortOptionsTests
{
    static GraphicsSettings Enhanced => new(GraphicsMode.Enhanced, 0);

    static PortOptionsScreen Screen(GraphicsSettings? g = null) => new(g ?? Enhanced);

    static PortOptionsScreen Select(PortOptionsScreen s, PortOption o)
    {
        for (int i = 0; i < PortOptionsScreen.Rows.Length && s.SelectedOption != o; i++) s.Handle(MenuKey.Down);
        Assert.Equal(o, s.SelectedOption);
        return s;
    }

    [Fact]
    public void RowsAreInTheSpecifiedOrderEndingWithBack()
    {
        Assert.Equal(["GRAPHICS", "SCALE", "PERSPECTIVE", "FILTER", "PIXEL FOG", "SSAA", "CAR LIGHT", "SHADOW", "TRACK SHADE", "RANGE", "DISPLAY", "WIDE VIEW", "2D SMOOTH", "BATTERY SAVER", "PAD RUMBLE", "LANGUAGE", "BACK"],
            PortOptionsScreen.Rows.Select(PortOptionsScreen.Label));
        Assert.Equal(PortOption.Graphics, Screen().SelectedOption);
    }

    [Fact]
    public void UpDownMoveTheSelectionAndWrapAround()
    {
        var s = Screen();
        Assert.False(s.Handle(MenuKey.Up).Changed);
        Assert.Equal(PortOption.Back, s.SelectedOption);          // up from the first row wraps to BACK
        s.Handle(MenuKey.Down);
        Assert.Equal(PortOption.Graphics, s.SelectedOption);      // down from BACK wraps to the first row
        s.Handle(MenuKey.Down);
        Assert.Equal(PortOption.Scale, s.SelectedOption);
        Assert.Equal(Enhanced, s.Settings);                        // moving never changes a value
    }

    [Fact]
    public void ValueTextsShowTheCurrentSettings()
    {
        var g = new GraphicsSettings(GraphicsMode.Enhanced, 3)
        { Filter = TextureFilter.Bilinear, Ssaa = 1, PixelFog = false, ExtendedRange = true, Display = DisplayFilterKind.Crt };
        var s = Screen(g);
        string V(PortOption o) => s.Value(o);
        Assert.Equal("ENHANCED", V(PortOption.Graphics));
        Assert.Equal("3", V(PortOption.Scale));
        Assert.Equal("ON", V(PortOption.Perspective));
        Assert.Equal("BILINEAR", V(PortOption.Filter));
        Assert.Equal("OFF", V(PortOption.PixelFog));
        Assert.Equal("OFF", V(PortOption.Ssaa));
        Assert.Equal("ON", V(PortOption.CarLight));
        Assert.Equal("ON", V(PortOption.Shadow));
        Assert.Equal("EXTENDED", V(PortOption.Range));
        Assert.Equal("CRT", V(PortOption.Display));
        Assert.Equal("", V(PortOption.Back));
        Assert.Equal("AUTO", Screen().Value(PortOption.Scale));
        Assert.Equal("AUTO", Screen().Value(PortOption.Ssaa));
        Assert.Equal("MIP", Screen().Value(PortOption.Filter));
        Assert.Equal("NORMAL", Screen().Value(PortOption.Range));
        Assert.Equal("SHARP", Screen().Value(PortOption.Display));
    }

    [Fact]
    public void GraphicsRowTogglesTheModeBothWaysAndReportsAChange()
    {
        var s = Screen();
        var r = s.Handle(MenuKey.Right);
        Assert.True(r.Changed); Assert.False(r.Close);
        Assert.Equal(GraphicsMode.Vanilla, s.Settings.Mode);
        Assert.Equal("VANILLA", s.Value(PortOption.Graphics));
        Assert.True(s.Handle(MenuKey.Left).Changed);
        Assert.Equal(GraphicsMode.Enhanced, s.Settings.Mode);
        Assert.True(s.Handle(MenuKey.Enter).Changed);              // Enter = forward
        Assert.Equal(GraphicsMode.Vanilla, s.Settings.Mode);
    }

    [Fact]
    public void ScaleCyclesAutoThrough4AndBackwards()
    {
        var s = Select(Screen(), PortOption.Scale);
        var seen = new List<int> { s.Settings.Scale };
        for (int i = 0; i < 5; i++) { s.Handle(MenuKey.Right); seen.Add(s.Settings.Scale); }
        Assert.Equal([0, 1, 2, 3, 4, 0], seen);
        s.Handle(MenuKey.Left);
        Assert.Equal(InternalScale.Max, s.Settings.Scale);          // backwards from AUTO = the largest
        s.Handle(MenuKey.Enter);
        Assert.Equal(0, s.Settings.Scale);
    }

    [Fact]
    public void SsaaCyclesAutoOff2To4AndKeepsTheFactorValid()
    {
        var s = Select(Screen(), PortOption.Ssaa);
        var texts = new List<string> { s.Value(PortOption.Ssaa) };
        for (int i = 0; i < 5; i++) { s.Handle(MenuKey.Right); texts.Add(s.Value(PortOption.Ssaa)); }
        Assert.Equal(["AUTO", "OFF", "2X", "3X", "4X", "AUTO"], texts);
        s.Handle(MenuKey.Left);
        Assert.Equal("4X", s.Value(PortOption.Ssaa));
        Assert.InRange(s.Settings.Ssaa, 0, InternalScale.Max);
    }

    [Fact]
    public void FilterAndDisplayCycleThroughAllValuesBothWays()
    {
        var s = Select(Screen(), PortOption.Filter);
        var f = new List<TextureFilter> { s.Settings.Filter };
        for (int i = 0; i < 3; i++) { s.Handle(MenuKey.Right); f.Add(s.Settings.Filter); }
        Assert.Equal([TextureFilter.Trilinear, TextureFilter.Nearest, TextureFilter.Bilinear, TextureFilter.Trilinear], f);
        s.Handle(MenuKey.Left);
        Assert.Equal(TextureFilter.Bilinear, s.Settings.Filter);

        s = Select(Screen(), PortOption.Display);
        var d = new List<DisplayFilterKind> { s.Settings.Display };
        for (int i = 0; i < 3; i++) { s.Handle(MenuKey.Right); d.Add(s.Settings.Display); }
        Assert.Equal([DisplayFilterKind.Sharp, DisplayFilterKind.Fsr, DisplayFilterKind.Crt, DisplayFilterKind.Sharp], d);
        s.Handle(MenuKey.Left);
        Assert.Equal(DisplayFilterKind.Crt, s.Settings.Display);
    }

    [Theory]
    [InlineData("Perspective")]
    [InlineData("PixelFog")]
    [InlineData("CarLight")]
    [InlineData("Shadow")]
    [InlineData("TrackShade")]
    [InlineData("Range")]
    [InlineData("Smooth2D")]
    public void SwitchRowsFlipOnEveryKeyAndOnlyTheirOwnValueChanges(string rowName)
    {
        var row = Enum.Parse<PortOption>(rowName);
        var s = Select(Screen(), row);
        var before = s.Settings;
        string v0 = s.Value(row);
        Assert.True(s.Handle(MenuKey.Left).Changed);
        string v1 = s.Value(row);
        Assert.NotEqual(v0, v1);
        s.Handle(MenuKey.Right);
        Assert.Equal(v0, s.Value(row));
        Assert.Equal(before, s.Settings);                           // two flips = back to the start
        s.Handle(MenuKey.Enter);
        Assert.Equal(v1, s.Value(row));
        foreach (var o in PortOptionsScreen.Rows.Where(o => o != row))   // no other row changed
            Assert.Equal(Screen().Value(o), s.Value(o));
    }

    [Fact]
    public void EscapeClosesWithoutChangingAnything_AndEnterOnBackCloses()
    {
        var s = Screen();
        var r = s.Handle(MenuKey.Escape);
        Assert.True(r.Close); Assert.False(r.Changed);
        Select(s, PortOption.Back);
        var back = s.Handle(MenuKey.Enter);
        Assert.True(back.Close); Assert.False(back.Changed);
        Assert.False(s.Handle(MenuKey.Left).Changed);               // left/right on BACK do nothing
        Assert.False(s.Handle(MenuKey.Right).Close);
        Assert.Equal(Enhanced, s.Settings);
    }

    [Fact]
    public void VanillaDimsTheEnhancedRowsButTheyStayEditable()
    {
        var s = Screen(Enhanced.Toggled());
        Assert.True(s.IsActive(PortOption.Graphics));
        Assert.True(s.IsActive(PortOption.Back));
        foreach (var o in PortOptionsScreen.Rows.Where(o => o is not (PortOption.Graphics or PortOption.BatterySaver or PortOption.Rumble or PortOption.Language or PortOption.Back)))
            Assert.False(s.IsActive(o), o.ToString());
        Assert.True(s.IsActive(PortOption.BatterySaver));           // frame caps do not depend on the graphics mode
        Select(s, PortOption.Shadow);
        Assert.True(s.Handle(MenuKey.Right).Changed);               // remembered for the next switch to Enhanced
        Assert.False(s.Settings.CarShadow);
        Assert.Equal(GraphicsMode.Vanilla, s.Settings.Mode);
        Select(s, PortOption.Graphics);
        s.Handle(MenuKey.Right);
        Assert.Equal(GraphicsMode.Enhanced, s.Settings.Mode);
        Assert.False(s.Settings.CarShadow);                         // value survived the round trip
        Assert.All(PortOptionsScreen.Rows, o => Assert.True(s.IsActive(o)));
    }

    [Fact]
    public void ChangesReachTheRendererOptionsLikeAfterF8()
    {
        var s = Select(Screen(), PortOption.PixelFog);
        s.Handle(MenuKey.Right);
        var opt = new RenderOptions();
        s.Settings.ApplyTo(opt);
        Assert.False(opt.PixelFog);
        Assert.True(opt.PerspectiveCorrect);
        Select(s, PortOption.Graphics); s.Handle(MenuKey.Right);    // Vanilla: everything off
        s.Settings.ApplyTo(opt);
        Assert.False(opt.PerspectiveCorrect);
        Assert.Equal(TextureFilter.Nearest, opt.Filter);
    }

    [Fact]
    public void BatterySaverTogglesAndOnlyItsOwnValueChanges()
    {
        var s = Select(Screen(), PortOption.BatterySaver);
        Assert.Equal("OFF", s.Value(PortOption.BatterySaver));
        var before = s.Settings;
        var r = s.Handle(MenuKey.Right);
        Assert.False(r.Changed);                                    // not a graphics change: the host must not call ApplyGraphics
        Assert.True(r.BatteryChanged);
        Assert.True(s.BatterySaver);
        Assert.Equal("ON", s.Value(PortOption.BatterySaver));
        Assert.Equal(before, s.Settings);
        s.Handle(MenuKey.Left);
        Assert.False(s.BatterySaver);
        s.Handle(MenuKey.Enter);
        Assert.True(s.BatterySaver);
        Assert.Equal("ON", new PortOptionsScreen(Enhanced, batterySaver: true).Value(PortOption.BatterySaver));
    }

    [Fact]
    public void BatterySaverIsUnavailableWithoutPortSettings()
    {
        var s = Select(new PortOptionsScreen(Enhanced, batterySaver: true, batterySaverAvailable: false), PortOption.BatterySaver);
        Assert.Equal("OFF", s.Value(PortOption.BatterySaver));      // never shows a state that is not in effect
        Assert.False(s.IsActive(PortOption.BatterySaver));
        var r = s.Handle(MenuKey.Right);
        Assert.False(r.Changed); Assert.False(r.BatteryChanged);
        Assert.False(s.BatterySaver);
        Assert.False(s.Handle(MenuKey.Enter).BatteryChanged);
    }

    [Fact]
    public void BatterySaverLimitsFramesThroughPortJsonAndKeepsOldFilesWorking()
    {
        var port = new PortSettings();
        Assert.False(FramePacing.IsBatterySaver(port));
        FramePacing.SetBatterySaver(port, true);
        Assert.True(FramePacing.IsBatterySaver(port));
        Assert.Equal(30, FramePacing.TargetFps(port, racing: true, windowActive: true));
        Assert.Equal(20, FramePacing.TargetFps(port, racing: false, windowActive: true));
        string path = Path.Combine(Path.GetTempPath(), "nfs2se-battery-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(port.Save(path));
            Assert.True(FramePacing.IsBatterySaver(PortSettings.Load(path)));
            FramePacing.SetBatterySaver(port, false);
            Assert.Null(port.Save(path));
            var off = PortSettings.Load(path);
            Assert.False(FramePacing.IsBatterySaver(off));
            Assert.Equal(60, FramePacing.TargetFps(off, true, true));
            Assert.Equal(30, FramePacing.TargetFps(off, false, true));
        }
        finally { File.Delete(path); }
        // old / hand-written files: no cap and the default are "off", an explicit low cap is "on"
        Assert.False(FramePacing.IsBatterySaver(new PortSettings { MaxFps = 0 }));
        Assert.False(FramePacing.IsBatterySaver(new PortSettings { MaxFps = 120 }));
        Assert.True(FramePacing.IsBatterySaver(new PortSettings { MaxFps = 25 }));
        Assert.False(FramePacing.IsBatterySaver(null));
    }

    [Fact]
    public void EveryChangeSurvivesPortJsonRoundTrip()
    {
        var s = Screen();
        foreach (var o in PortOptionsScreen.Rows.Where(o => o != PortOption.Back))
        {
            Select(s, o);
            s.Handle(MenuKey.Right);
            if (o is PortOption.Scale or PortOption.Ssaa) s.Handle(MenuKey.Right);
        }
        string path = Path.Combine(Path.GetTempPath(), "nfs2se-portopt-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var port = new PortSettings();
            s.Settings.WriteTo(port);
            Assert.Null(port.Save(path));
            var loaded = GraphicsSettings.Resolve(DesktopOptions.Parse([]), PortSettings.Load(path));
            Assert.Equal(s.Settings, loaded);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    // network, menuHost, raceActive, racePaused, replay, textEntry, expected
    [InlineData(false, true, false, false, false, false, true)]    // game menu
    [InlineData(false, true, true, true, false, false, true)]      // pause menu of a race
    [InlineData(false, true, true, false, false, false, false)]    // race running: never (the simulation must not be disturbed)
    [InlineData(false, true, true, true, true, false, false)]      // replay playback
    [InlineData(true, true, false, false, false, false, false)]    // network menu
    [InlineData(false, true, false, false, false, true, false)]    // typing a name / dialog / key capture
    [InlineData(false, false, false, false, false, false, false)]  // fly-through (no menu host)
    public void OpensOnlyInTheMenuAndThePauseMenu(bool net, bool host, bool race, bool paused, bool replay, bool text, bool expected) =>
        Assert.Equal(expected, PortOptionsScreen.CanOpen(net, host, race, paused, replay, text));

    // ---- the picture ----

    static FrameBuffer Panel(PortOptionsScreen s, bool menuBackground = true)
    {
        var fb = new FrameBuffer(640, 480);
        if (menuBackground)
            for (int y = 0; y < 480; y++)
                for (int x = 0; x < 640; x++) fb.Set(x, y, (uint)((60 + y / 6) << 16 | (90 + x / 10) << 8 | 160));
        else fb.ClearTransparent();
        PortOptionsRenderer.Draw(fb, s, 2);
        return fb;
    }

    [Fact]
    public void PanelHasADarkBorderOverABrightBackground()
    {
        var fb = Panel(Screen());
        int y = 240;
        static int Lum(uint rgb) => (int)((rgb >> 16 & 0xFF) * 3 + (rgb >> 8 & 0xFF) * 6 + (rgb & 0xFF));
        int x0 = (640 - PortOptionsRenderer.PanelW) / 2;
        Assert.True(Lum(fb.Get(x0, y)) < Lum(fb.Get(x0 - 1, y)), "border darker than the picture around");
        Assert.True(Lum(fb.Get(x0, y)) < Lum(fb.Get(x0 + 10, y)), "border darker than the translucent fill");
    }

    [Fact]
    public void PanelIsTranslucentOverTheBackgroundAndKeepsTheLayerMaskCorrect()
    {
        var plain = new FrameBuffer(640, 480);
        var with = new FrameBuffer(640, 480);
        for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++) { plain.Set(x, y, 0x6080C0); with.Set(x, y, 0x6080C0); }
        PortOptionsRenderer.Draw(with, Screen(), 2);
        int dark = 0, same = 0;
        for (int i = 0; i < 640 * 480; i++)
        {
            int a = plain.Rgba[i * 4], b = with.Rgba[i * 4];
            if (b < a) dark++; else if (b == a) same++;
        }
        Assert.True(dark > 30000, $"panel darkens the background: {dark}");
        Assert.True(same > 100000, $"outside the panel the picture is untouched: {same}");
        Assert.Equal(0x6080C0u, with.Get(2, 2));                    // far corner untouched
        // on a transparent 2D layer (Enhanced race scene below) the panel becomes partly opaque pixels, not holes
        var layer = Panel(Screen(), menuBackground: false);
        int centre = (240 * 640 + 320) * 4 + 3;
        Assert.InRange((int)layer.Rgba[centre], 150, 255);
        Assert.Equal(0, layer.Rgba[3]);                             // outside the panel the layer stays transparent
    }

    [Fact]
    public void SelectionAndDimmedRowsChangeThePicture()
    {
        var a = Panel(Screen());
        var s2 = Screen(); s2.Handle(MenuKey.Down);
        var b = Panel(s2);
        Assert.NotEqual(a.Sha256(), b.Sha256());
        var v = Panel(Screen(Enhanced.Toggled()));
        Assert.NotEqual(a.Sha256(), v.Sha256());
    }

    [Fact]
    public void ReviewPictures()
    {
        // Review pictures: NFS_GFX_CHECK_DIR=<dir> (usually out/gfx_check) writes port_options_*.png.
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        Panel(Screen()).SavePng(Path.Combine(dir, "port_options_enhanced.png"));
        var g = new GraphicsSettings(GraphicsMode.Enhanced, 2) { Filter = TextureFilter.Bilinear, Ssaa = 3, ExtendedRange = true, Display = DisplayFilterKind.Crt, CarShadow = false };
        var mid = Screen(g); Select(mid, PortOption.Ssaa);
        Panel(mid).SavePng(Path.Combine(dir, "port_options_changed.png"));
        var bat = new PortOptionsScreen(Enhanced, batterySaver: true); Select(bat, PortOption.BatterySaver);
        Panel(bat).SavePng(Path.Combine(dir, "port_options_fps.png"));
        var vanBat = new PortOptionsScreen(Enhanced.Toggled(), batterySaver: true); Select(vanBat, PortOption.BatterySaver);
        Panel(vanBat).SavePng(Path.Combine(dir, "port_options_fps_vanilla.png"));
        var van = Screen(Enhanced.Toggled()); Select(van, PortOption.Filter);
        Panel(van).SavePng(Path.Combine(dir, "port_options_vanilla.png"));
        var back = Screen(); Select(back, PortOption.Back);
        Panel(back).SavePng(Path.Combine(dir, "port_options_back.png"));
        Panel(Screen(), menuBackground: false).SavePng(Path.Combine(dir, "port_options_layer.png"));
    }
    [Fact]
    public void LanguageRowCyclesAllSixAndWraps()
    {
        var s = Select(Screen(), PortOption.Language);
        Assert.Equal("ENG", s.Value(PortOption.Language));
        string[] codes = ["GER", "FRE", "SPA", "ITA", "SWE", "ENG"];
        foreach (var c in codes)
        {
            var r = s.Handle(MenuKey.Right);
            Assert.True(r.LanguageChanged); Assert.False(r.Changed); Assert.False(r.Close);
            Assert.Equal(c, s.Value(PortOption.Language));
        }
        Assert.Equal(0, s.LanguageIndex);
        s.Handle(MenuKey.Left);
        Assert.Equal(5, s.LanguageIndex); Assert.Equal("SWE", s.Value(PortOption.Language));
        s.Handle(MenuKey.Enter);
        Assert.Equal(0, s.LanguageIndex);
    }

    [Fact]
    public void LanguageRowUnavailableWithoutPortJson()
    {
        var s = Select(new PortOptionsScreen(Enhanced, language: 3, languageAvailable: false), PortOption.Language);
        Assert.False(s.IsActive(PortOption.Language));
        Assert.Equal("ENG", s.Value(PortOption.Language));
        Assert.False(s.Handle(MenuKey.Right).LanguageChanged);
        Assert.Equal(0, s.LanguageIndex);
        Assert.Equal("SPA", new PortOptionsScreen(Enhanced, language: 3).Value(PortOption.Language));
        Assert.Equal("ENG", new PortOptionsScreen(Enhanced, language: 9).Value(PortOption.Language));
    }

    [Fact]
    public void LanguageChoiceSavedToPortJsonAndReadBack()
    {
        string path = Path.Combine(Path.GetTempPath(), "nfs2se_lang_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var port = new PortSettings();
            var s = Select(new PortOptionsScreen(Enhanced, language: port.LanguageIndex), PortOption.Language);
            s.Handle(MenuKey.Right); s.Handle(MenuKey.Right);   // GER, FRE
            port.SetLanguage(s.LanguageIndex);
            Assert.Null(port.Save(path));
            Assert.Contains("\"language\": \"french\"", File.ReadAllText(path));
            var back = PortSettings.Load(path);
            Assert.Equal(2, back.LanguageIndex);
            Assert.Equal("FRE", new PortOptionsScreen(Enhanced, language: back.LanguageIndex).Value(PortOption.Language));
        }
        finally { File.Delete(path); }
    }
}
