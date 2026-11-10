using Microsoft.Xna.Framework.Input;
using NFS.Desktop;

namespace NFS.Desktop.Tests;

// Headless: full screen by default (Steam Deck), remembered in port.json, Alt+Enter back to the scaled window.
public class WindowModeTests
{
    static PortSettings Saved(bool? full) => new() { FullScreen = full };

    [Fact]
    public void FirstRunFullScreenThenRememberedModeCommandLineWins()
    {
        Assert.Equal((true, 1280, 800), WindowMode.Initial(DesktopOptions.Parse([]), null, 1280, 800));           // Steam Deck, first run
        Assert.Equal((true, 1280, 800), WindowMode.Initial(DesktopOptions.Parse([]), Saved(null), 1280, 800));
        Assert.Equal((false, 640, 480), WindowMode.Initial(DesktopOptions.Parse([]), Saved(false), 1280, 800));   // remembered window
        Assert.Equal((true, 1920, 1080), WindowMode.Initial(DesktopOptions.Parse(["--fullscreen"]), Saved(false), 1920, 1080));
        Assert.Equal((false, 1280, 960), WindowMode.Initial(DesktopOptions.Parse(["--windowed", "--scale", "2"]), Saved(true), 1280, 800));
        Assert.Equal((false, 640, 480), WindowMode.Initial(DesktopOptions.Parse(["--autotest", "out"]), Saved(true), 1920, 1080));
        Assert.False(DesktopOptions.Parse(["--autotest", "out"]).UsesPortSettings);
        Assert.True(DesktopOptions.Parse(["--track", "4"]).UsesPortSettings);
    }

    [Fact]
    public void WindowSizeOptionStartsWindowedAtThatSize()
    {
        Assert.Equal((1600, 900), DesktopOptions.Parse(["--window", "1600x900"]).Window);
        Assert.Equal((false, 1600, 900), WindowMode.Initial(DesktopOptions.Parse(["--window", "1600x900"]), Saved(true), 1920, 1080));
        Assert.Equal((false, 1280, 720), WindowMode.Initial(DesktopOptions.Parse(["--autotest", "out", "--window", "1280X720"]), null, 1920, 1080));
        Assert.Null(DesktopOptions.Parse([]).Window);
        foreach (var bad in new[] { "1600", "1600x", "x900", "100x100", "8000x900", "-1x900", "16OOx900" })
            Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--window", bad]));
        Assert.Contains("--window WxH", DesktopOptions.Usage);
    }

    [Fact]
    public void AltEnterTogglesBackToTheScaledWindowAndAgain()
    {
        var w = WindowMode.Toggle(true, 2, 1280, 800);
        Assert.Equal((false, 1280, 960), w);
        Assert.Equal((true, 1280, 800), WindowMode.Toggle(w.FullScreen, 2, 1280, 800));
        Assert.Equal((false, 640, 480), WindowMode.Toggle(false, 1, 0, 0));      // unknown display: stay windowed
    }

    [Fact]
    public void ToggleFiresOnceOnEnterDownWithEitherAlt()
    {
        var none = new KeyboardState();
        var altEnter = new KeyboardState(Keys.LeftAlt, Keys.Enter);
        Assert.True(WindowMode.IsToggle(altEnter, none));
        Assert.True(WindowMode.IsToggle(new KeyboardState(Keys.RightAlt, Keys.Enter), none));
        Assert.False(WindowMode.IsToggle(altEnter, altEnter));                   // held: once
        Assert.False(WindowMode.IsToggle(new KeyboardState(Keys.Enter), none));  // plain Enter is the menu's accept
        Assert.False(WindowMode.IsToggle(altEnter, new KeyboardState(Keys.Enter))); // Alt pressed while Enter held
    }

    [Fact]
    public void LetterboxKeepsFourByThreeOnSixteenByTen() =>
        Assert.Equal((107, 0, 1066, 800), Layout.Fit(1280, 800, 640, 480));

    [Fact]
    public void PortJsonRoundTripKeepsUnknownKeysAndSurvivesBrokenFile()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-port-json-").FullName;
        var path = Path.Combine(dir, "sub", PortSettings.FileName);
        try
        {
            Assert.Null(PortSettings.Load(path).FullScreen);                     // missing file → default
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "fullScreen": true, "future": 5 }""");
            var p = PortSettings.Load(path);
            Assert.True(p.FullScreen);
            p.FullScreen = false;
            Assert.Null(p.Save(path));
            var back = PortSettings.Load(path);
            Assert.False(back.FullScreen);
            Assert.True(back.Extra.ContainsKey("future"));                        // keys of a newer build survive
            Assert.DoesNotContain("\r", File.ReadAllText(path));
            File.WriteAllText(path, "{ broken");
            var broken = PortSettings.Load(path);
            Assert.Null(broken.FullScreen);                                       // → full screen default
            Assert.NotNull(broken.Error);
        }
        finally { Directory.Delete(dir, true); }
    }
}
