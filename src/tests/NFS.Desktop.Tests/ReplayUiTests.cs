using NFS.Game.Race;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Command model of the replay strip (its commit step), without devices or rendering. [ref:2895]</summary>
public sealed class ReplayUiTests
{
    static ReplayUi Ui(int carType = 0, bool split = false) => new(carType, split);

    static void Run(ReplayUi ui, int ticks)
    {
        for (int i = 0; i < ticks; i++) ui.Tick();
    }

    static void Press(ReplayUi ui, ReplayUiKey key, int ticks = 1)
    {
        ui.Press(key);
        Run(ui, ticks);
    }

    [Fact]
    public void Start_SelectsThePauseIconAtOneTimesSpeedWithTheDirector()
    {
        var ui = Ui();
        Assert.Equal(0, ui.Selected);
        Assert.Equal(2, ui.SpeedIndex);
        Assert.Equal(1.0, ui.SpeedMultiplier);
        Assert.Equal(0, ui.CameraIndex);
        Assert.True(ui.AutoCamera);
        Assert.False(ui.PauseFlag);
        Assert.False(ui.Paused);
        Assert.Equal(0, ui.PressTicks);
    }

    [Fact]
    public void LeftRight_MoveOverTheFourIconsWithoutWrapping()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Left);
        Assert.Equal(0, ui.Selected);
        for (int expected = 1; expected <= 3; expected++)
        {
            Press(ui, ReplayUiKey.Right);
            Assert.Equal(expected, ui.Selected);
        }
        Press(ui, ReplayUiKey.Right);
        Assert.Equal(3, ui.Selected);
        Press(ui, ReplayUiKey.Left);
        Assert.Equal(2, ui.Selected);
        Assert.Equal(0, ui.PressTicks); // moving the selection never presses a button
    }

    [Fact]
    public void KeyEdges_AreConsumedByTheNextTickOnly()
    {
        var ui = Ui();
        ui.Press(ReplayUiKey.Right);
        Run(ui, 1);
        Run(ui, 5);
        Assert.Equal(1, ui.Selected); // the edge is not repeated
    }

    [Fact]
    public void Left_WinsOverRightAndSelectInTheSameTick()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Right);
        ui.Press(ReplayUiKey.Left);
        ui.Press(ReplayUiKey.Right);
        ui.Press(ReplayUiKey.Select);
        Run(ui, 1);
        Assert.Equal(0, ui.Selected);
        Assert.Equal(0, ui.PressTicks);
        Assert.Equal(2, ui.SpeedIndex);
    }

    [Fact]
    public void Pause_FlagIsSetAtOnceAndTheCommitComesWhenTheCounterReachesFour()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Select);
        Assert.True(ui.PauseFlag);
        Assert.False(ui.Paused);
        Assert.Equal(8, ui.PressTicks);
        for (int expected = 7; expected >= 5; expected--)
        {
            Run(ui, 1);
            Assert.Equal(expected, ui.PressTicks);
            Assert.False(ui.Paused);
        }
        Run(ui, 1); // the counter hits 4: commits and resets it [ref:2896]
        Assert.True(ui.Paused);
        Assert.Equal(0, ui.PressTicks);
        // Toggle again.
        Press(ui, ReplayUiKey.Select, 5);
        Assert.False(ui.PauseFlag);
        Assert.False(ui.Paused);
    }

    [Fact]
    public void KeysAreIgnoredWhileACommandIsLockedIn()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Select); // pause pressed, counter 8
        Press(ui, ReplayUiKey.Right);  // dropped
        Assert.Equal(0, ui.Selected);
        Press(ui, ReplayUiKey.Select); // dropped
        Assert.True(ui.PauseFlag);
        Run(ui, 3);
        Assert.Equal(0, ui.PressTicks);
        Assert.Equal(0, ui.Selected);
        Assert.True(ui.Paused);
    }

    [Fact]
    public void Speed_CyclesThroughFourValuesFromOneTimes()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Right);
        var seen = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            Press(ui, ReplayUiKey.Select);
            seen.Add(ui.SpeedMultiplier);
            Run(ui, 6);
        }
        Assert.Equal(new[] { 2.0, 0.25, 0.5, 1.0, 2.0 }, seen);
    }

    [Fact]
    public void Speed_InSplitScreenCyclesThroughThreeValues()
    {
        var ui = Ui(split: true);
        Press(ui, ReplayUiKey.Right);
        var seen = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            Press(ui, ReplayUiKey.Select);
            seen.Add(ui.SpeedIndex);
            Run(ui, 6);
        }
        Assert.Equal(new[] { 0, 1, 2, 0 }, seen); // 2 -> (2+1)%3 = 0 -> 1 -> 2 -> 0
    }

    [Fact]
    public void Eject_LocksForTwentyTicksAndThenLeavesTheReplay()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Right);
        Press(ui, ReplayUiKey.Right);
        Assert.Equal(2, ui.Selected);
        Press(ui, ReplayUiKey.Select);
        Assert.Equal(0x18, ui.PressTicks);
        Run(ui, 19);
        Assert.False(ui.EjectRequested);
        Run(ui, 1);
        Assert.True(ui.EjectRequested);
        Assert.False(ui.Paused);
    }

    [Fact]
    public void Camera_CyclesThroughTheOriginalTableAndNineIcons()
    {
        Assert.Equal(new[] { 10, 4, 6, 5, 7, 2, 3, 1, 0 }, ReplayUi.CameraModes);
        var ui = Ui();
        for (int i = 0; i < 3; i++) Press(ui, ReplayUiKey.Right);
        Assert.Equal(3, ui.Selected);
        var modes = new List<int>();
        for (int i = 0; i < 9; i++)
        {
            ui.Press(ReplayUiKey.Select);
            var applied = ui.Tick();                           // the director takes effect at once
            for (int t = 0; t < 6; t++) applied ??= ui.Tick(); // any other icon is applied by the next call
            Assert.NotNull(applied);
            Run(ui, 6);                                        // let the lock expire
            modes.Add(applied!.Value.Mode);
            Assert.Equal(applied.Value.Mode == ReplayUi.AutoCameraMode, applied.Value.Auto);
        }
        Assert.Equal(new[] { 4, 6, 5, 7, 2, 3, 1, 0, 10 }, modes);
        Assert.True(ui.AutoCamera);
        Assert.Equal(0, ui.CameraIndex);
    }

    [Fact]
    public void Camera_OtherIconsApplyOnTheTickAfterThePressAndTheDirectorImmediately()
    {
        var ui = Ui();
        for (int i = 0; i < 3; i++) Press(ui, ReplayUiKey.Right);
        ui.Press(ReplayUiKey.Select);
        Assert.Null(ui.Tick());                  // pressed: the icon index changes, the camera does not yet
        Assert.Equal(1, ui.CameraIndex);
        Assert.False(ui.AutoCamera);
        var next = ui.Tick();
        Assert.Equal(new ReplayUiCamera(4, false), next);
        Assert.Null(ui.Tick());                  // applied once
    }

    [Theory]
    [InlineData(0, 9)]
    [InlineData(14, 9)]
    [InlineData(15, 8)]
    public void Camera_CockpitIconExistsOnlyForPlayerCars(int carType, int slots)
    {
        var ui = Ui(carType);
        for (int i = 0; i < 3; i++) Press(ui, ReplayUiKey.Right);
        var seen = new HashSet<int>();
        for (int i = 0; i < 20; i++)
        {
            Press(ui, ReplayUiKey.Select, 6);
            seen.Add(ui.CameraIndex);
        }
        Assert.Equal(slots, seen.Count);
        Assert.Equal(slots - 1, seen.Max());
    }

    [Fact]
    public void Camera_SplitScreenHasEightIcons()
    {
        var ui = Ui(split: true);
        for (int i = 0; i < 3; i++) Press(ui, ReplayUiKey.Right);
        for (int i = 0; i < 8; i++) Press(ui, ReplayUiKey.Select, 6);
        Assert.Equal(0, ui.CameraIndex);
    }

    [Fact]
    public void Camera_IconThreeFollowsTheCameraWhenItSitsInModeSeven()
    {
        var ui = Ui();
        for (int i = 0; i < 3; i++) Press(ui, ReplayUiKey.Right);
        for (int i = 0; i < 3; i++) Press(ui, ReplayUiKey.Select, 6); // icon 3 = mode 5
        Assert.Equal(3, ui.CameraIndex);
        ui.Tick(cameraMode: 7);
        Assert.Equal(4, ui.CameraIndex); // mode 5 selected while the camera is in 7 -> icon of mode 7 [ref:2897]
    }

    [Fact]
    public void CycleCamera_IsThePortShortcutForTheCameraCommand()
    {
        var ui = Ui();
        var first = ui.CycleCamera();
        Assert.Equal(new ReplayUiCamera(4, false), first);
        Assert.Equal(3, ui.Selected);
        Assert.Equal(1, ui.CameraIndex);
        for (int i = 0; i < 8; i++) first = ui.CycleCamera();
        Assert.Equal(new ReplayUiCamera(ReplayUi.AutoCameraMode, true), first);
        Assert.True(ui.AutoCamera);
    }

    [Fact]
    public void Strip_ReportsTheStateTheIconsDrawFrom()
    {
        var ui = Ui();
        Press(ui, ReplayUiKey.Select);
        var strip = ui.Strip(100, 36000);
        Assert.Equal(0, strip.Selected);
        Assert.True(strip.Pressed);
        Assert.True(strip.Paused);
        Assert.Equal(2, strip.SpeedIndex);
        Assert.Equal(0, strip.CameraIndex);
        Assert.Equal((100, 36000), (strip.RaceTick64, strip.ReplayTicks64));
    }
}
