using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace NFS.Desktop.Tests;

public class CameraInputTests
{
    [Fact]
    public void CameraKeyReportsPressHoldReleaseAndRepress()
    {
        var mapper = new InputMapping();
        Assert.False(mapper.Read(default, default).Drive.CycleCamera);
        Assert.True(mapper.Read(new KeyboardState(Keys.C), default).Drive.CycleCamera);
        Assert.True(mapper.Read(new KeyboardState(Keys.C), default).Drive.CycleCamera);
        Assert.False(mapper.Read(default, default).Drive.CycleCamera);
        Assert.True(mapper.Read(new KeyboardState(Keys.C), default).Drive.CycleCamera);
        Assert.False(mapper.Read(new KeyboardState(Keys.F1), default).Drive.CycleCamera);
    }

    [Fact]
    public void PadBackReportsPressHoldReleaseRepressAndDisconnection()
    {
        var mapper = new InputMapping();
        var pressed = new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, [Buttons.Back]);
        var released = new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, []);
        Assert.True(mapper.Read(default, pressed).Drive.CycleCamera);
        Assert.True(mapper.Read(default, pressed).Drive.CycleCamera);
        Assert.False(mapper.Read(default, released).Drive.CycleCamera);
        Assert.True(mapper.Read(default, pressed).Drive.CycleCamera);
        Assert.False(mapper.Read(default, default).Drive.CycleCamera);
        Assert.True(mapper.Read(default, pressed).Drive.CycleCamera);
        Assert.True(mapper.Read(new KeyboardState(Keys.C), default).Drive.CycleCamera);
    }

    [Fact]
    public void CameraControlPreservesOtherDrivingMenuAndPauseInputs()
    {
        var mapper = new InputMapping();
        var keyboard = new KeyboardState(Keys.Up, Keys.Left, Keys.A, Keys.Z, Keys.R, Keys.H, Keys.Space);
        var pad = new GamePadState(new Vector2(0.5f, 0), Vector2.Zero, 0.25f, 0.75f,
            [Buttons.Back]);
        var baseline = mapper.Read(keyboard, default);
        var camera = mapper.Read(new KeyboardState(Keys.Up, Keys.Left, Keys.A, Keys.Z, Keys.R, Keys.H,
            Keys.Space, Keys.C), pad);
        Assert.True(camera.Drive.CycleCamera);
        Assert.Equal(baseline.Drive with { CycleCamera = true, AnalogSteer = 64, AnalogGas = 191, AnalogBrake = 64 },
            camera.Drive);
        Assert.Empty(camera.Menu);
        Assert.False(camera.Pause);
    }

    [Fact]
    public void OriginalOrbitActivationRequiresHeldRightShiftAndCAtSample()
    {
        var mapper = new InputMapping();
        Assert.False(mapper.Read(new KeyboardState(Keys.C), default).OrbitCameraActivation);
        Assert.True(mapper.Read(new KeyboardState(Keys.RightShift, Keys.C), default).OrbitCameraActivation);
        Assert.False(mapper.Read(new KeyboardState(Keys.LeftShift, Keys.C), default).OrbitCameraActivation);
        Assert.False(mapper.Read(new KeyboardState(Keys.RightShift), default).OrbitCameraActivation);
        Assert.True(mapper.Read(new KeyboardState(Keys.RightShift, Keys.C), default).OrbitCameraActivation);
        Assert.True(InputMapping.OrbitCameraActivation(new KeyboardState(Keys.RightShift, Keys.C)));
        Assert.False(InputMapping.OrbitCameraActivation(new KeyboardState(Keys.LeftShift, Keys.C)));
    }

    [Fact]
    public void FixedCameraActivationRequiresHeldRightShiftAndBAtSample()
    {
        var mapper = new InputMapping();
        Assert.False(mapper.Read(new KeyboardState(Keys.B), default).FixedCameraActivation);
        Assert.True(mapper.Read(new KeyboardState(Keys.RightShift, Keys.B), default).FixedCameraActivation);
        Assert.False(mapper.Read(new KeyboardState(Keys.LeftShift, Keys.B), default).FixedCameraActivation);
        Assert.False(mapper.Read(new KeyboardState(Keys.RightShift), default).FixedCameraActivation);
        Assert.False(mapper.Read(new KeyboardState(Keys.RightShift, Keys.C), default).FixedCameraActivation);
        Assert.True(mapper.Read(new KeyboardState(Keys.RightShift, Keys.C, Keys.B), default).FixedCameraActivation);
        Assert.True(mapper.Read(new KeyboardState(Keys.RightShift, Keys.C, Keys.B), default).OrbitCameraActivation);
        Assert.True(InputMapping.FixedCameraActivation(new KeyboardState(Keys.RightShift, Keys.B)));
        Assert.False(InputMapping.FixedCameraActivation(new KeyboardState(Keys.LeftShift, Keys.B)));
    }
}
