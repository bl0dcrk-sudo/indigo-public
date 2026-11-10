using NFS.Desktop;

namespace NFS.Desktop.Tests;

// Headless tests only: no Game/GraphicsDevice is ever created here.
public class DesktopOptionsTests
{
    [Fact]
    public void Defaults()
    {
        var o = DesktopOptions.Parse([]);
        Assert.Equal(0, o.Track);
        Assert.Null(o.AutotestDir);
        Assert.Equal(1, o.Scale);
        Assert.False(o.Help);
    }

    [Fact]
    public void ParsesAll()
    {
        var o = DesktopOptions.Parse(["--track", "3", "--game-dir", @"C:\g", "--autotest", @"C:\out", "--frames", "7",
                                      "--slice", "120", "--speed", "12.5", "--scale", "2"]);
        Assert.Equal(3, o.Track);
        Assert.Equal(@"C:\g", o.GameDir);
        Assert.Equal(@"C:\out", o.AutotestDir);
        Assert.Equal(7, o.AutotestFrames);
        Assert.Equal(120, o.StartSlice);
        Assert.Equal(12.5f, o.Speed);
        Assert.Equal(2, o.Scale);
    }

    [Theory]
    [InlineData("--track")]
    [InlineData("--track", "x")]
    [InlineData("--bogus")]
    [InlineData("--track", "-1")]
    public void RejectsBadArgs(params string[] args) => Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(args));

    [Fact]
    public void ScaleClamped() => Assert.Equal(4, DesktopOptions.Parse(["--scale", "9"]).Scale);

    [Theory]
    [InlineData(true, "")]                              // game without arguments = menu
    [InlineData(true, "--menu")]
    [InlineData(false, "--track 4")]                    // modes and autotests are repeatable: without the user's file
    [InlineData(false, "--race replay")]
    [InlineData(false, "--autotest C:\\out")]
    [InlineData(false, "--menu --autotest C:\\out")]
    public void UserSettingsOnlyInTheMenuGame(bool expected, string args) =>
        Assert.Equal(expected, DesktopOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)).UsesUserSettings);
}

public class FlyThroughTests
{
    static readonly int[] Tracks = [0, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public void AdvancesAndWraps()
    {
        var f = new FlyThrough(Tracks, 3, sliceCount: 100, speed: 30f, startSlice: 95);
        Assert.Equal((95, 96, 0f), f.CameraSlice());
        f.Update(0.5);                 // +15 slices → 110 → wraps to 10
        var (s, n, fr) = f.CameraSlice();
        Assert.Equal(10, s);
        Assert.Equal(11, n);
        Assert.Equal(0f, fr, 3);
    }

    [Fact]
    public void LastSliceInterpolatesToFirst()
    {
        var f = new FlyThrough(Tracks, 0, 50, speed: 10f, startSlice: 49);
        f.Update(0.05);
        var (s, n, fr) = f.CameraSlice();
        Assert.Equal(49, s);
        Assert.Equal(0, n);
        Assert.Equal(0.5f, fr, 3);
    }

    [Fact]
    public void ReverseSpeedWrapsBackwards()
    {
        var f = new FlyThrough(Tracks, 0, 100, speed: -20f, startSlice: 5);
        f.Update(0.5);
        Assert.Equal(95, f.CameraSlice().Slice);
    }

    [Fact]
    public void NegativeStartSliceWraps()
    {
        var f = new FlyThrough(Tracks, 0, 100, startSlice: -1);
        Assert.Equal(99, f.CameraSlice().Slice);
    }

    [Fact]
    public void PausedDoesNotMove()
    {
        var f = new FlyThrough(Tracks, 0, 100, speed: 50f) { Paused = true };
        f.Update(1);
        Assert.Equal(0, f.CameraSlice().Slice);
    }

    [Fact]
    public void SpeedStepsAndClamps()
    {
        var f = new FlyThrough(Tracks, 0, 100, speed: FlyThrough.MaxSpeed - 5);
        f.Faster();
        Assert.Equal(FlyThrough.MaxSpeed, f.Speed);
        f.Slower();
        Assert.Equal(FlyThrough.MaxSpeed - FlyThrough.SpeedStep, f.Speed);
    }

    [Fact]
    public void TrackSwitchCyclesThroughAvailable()
    {
        var f = new FlyThrough(Tracks, 8, 100);
        Assert.Equal(0, f.ChangeTrack(+1));
        Assert.Equal(8, f.ChangeTrack(-1));
        Assert.Equal(7, f.ChangeTrack(-1));
    }

    [Fact]
    public void UnknownTrackFallsBackToFirst() => Assert.Equal(0, new FlyThrough(Tracks, 1, 10).Track);

    [GameDataFact]
    public void AvailableTracksFromGameData()
    {
        Assert.Equal(Tracks, FlyThrough.AvailableTracks(null));
    }
}

public class LayoutTests
{
    [Theory]
    [InlineData(640, 480, 0, 0, 640, 480)]
    [InlineData(1280, 960, 0, 0, 1280, 960)]
    [InlineData(1920, 1080, 240, 0, 1440, 1080)]
    [InlineData(800, 800, 0, 100, 800, 600)]
    public void FitKeepsAspect(int dw, int dh, int x, int y, int w, int h) =>
        Assert.Equal((x, y, w, h), Layout.Fit(dw, dh, 640, 480));
}

public sealed class GameDataFactAttribute : FactAttribute
{
    public GameDataFactAttribute() { if (NFS.Data.GameFiles.TryFindGameDir() is null) Skip = "game data (game_src/NFS2SE) not found"; }
}
