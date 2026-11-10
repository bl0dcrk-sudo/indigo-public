using System.Numerics;
using Microsoft.Xna.Framework.Input;
using NFS.Core;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class LookBehindTests
{
    static readonly RenderPose Pose = new(new Vector3(10, 20, 30), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
    static CarObj Car() => new() { UpDotN = Fx.One };
    static RaceCamera Camera(int mode)
    {
        var camera = new RaceCamera(0);
        while (camera.Mode != mode) camera.Cycle(Pose, Car());
        camera.Snap(Pose, Car());
        return camera;
    }
    static void Near(Vector3 expected, Vector3 actual)
        => Assert.True(Vector3.Distance(expected, actual) < .00002f, $"expected {expected}, actual {actual}");

    [GameDataFact]
    public void MirrorUsesOriginalIntegerProjectionAndMainPositionButRawRearCarBasis()
    {
        var camera = Camera(0);
        var pose = new RenderPose(new Vector3(1, 2, 3), -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY);
        camera.Snap(pose, Car(), new CarAnim(0) { Pitch = 0x300, Roll = 0x400 });
        var mirror = camera.MirrorCamera(pose);
        Assert.Equal(camera.Position, mirror.Position);
        Near(-pose.Forward, mirror.Forward);
        Near(-pose.Right, mirror.Right);
        Near(pose.Up, mirror.Up);
        Assert.Equal(640, mirror.Width); Assert.Equal(70, mirror.Height);
        Assert.Equal(75, mirror.Cx); Assert.Equal(35, mirror.Cy);
        // Original quarter-sine table, horizontal: entry 193 = (60700, 24707) [ref:2768]
        // vertical: entry 156 = (53581, 37736); truncates signed integer quotients. [ref:2769]
        Assert.Equal(91, mirror.Fx);
        Assert.Equal(-73, mirror.Fy);
    }

    [Fact]
    public void KeyboardBIsHeldRatherThanToggled()
    {
        var input = new InputMapping();
        Assert.False(input.Read(default, default).Drive.LookBehind);
        Assert.True(input.Read(new KeyboardState(Keys.B), default).Drive.LookBehind);
        Assert.True(input.Read(new KeyboardState(Keys.B), default).Drive.LookBehind);
        Assert.False(input.Read(default, default).Drive.LookBehind);
        Assert.True(input.Read(new KeyboardState(Keys.B), default).Drive.LookBehind);
    }

    [Fact]
    public void RightStickIsHeldAndDisconnectReleasesWithoutResettingCar()
    {
        var input = new InputMapping();
        var held = new GamePadState(Microsoft.Xna.Framework.Vector2.Zero, Microsoft.Xna.Framework.Vector2.Zero,
            0, 0, [Buttons.RightStick]);
        Assert.True(input.Read(default, held).Drive.LookBehind);
        var again = input.Read(default, held);
        Assert.True(again.Drive.LookBehind);
        Assert.False(again.Drive.Reset);
        Assert.Empty(again.Menu);
        Assert.False(again.Pause);
        Assert.False(input.Read(default, default).Drive.LookBehind);
        Assert.True(input.Read(default, held).Drive.LookBehind);
    }

    [GameDataFact]
    public void AllFourModesImmediatelyReverseLocalZAndRestoreWithoutFlyby()
    {
        // 440/43ADB0/43B140: rear local Z is negated, local Y retained. [ref:2770]
        Vector3[] offsets = [new(0, 0x4CCC / 65536f, -.5f),
            new(0, 0x4CCC / 65536f, -0xE666 / 65536f), new(0, 2, 6),
            new(0, (0x10CCC + 0x4000) / 65536f, 0x4D999 / 65536f)];
        for (int mode = 0; mode < 4; mode++)
        {
            var c = Camera(mode);
            var originalPosition = c.Position; var originalForward = c.Forward; var originalUp = c.Up;
            c.SetLookBehind(true, Pose, Car());
            Assert.True(c.LookingBehind);
            Assert.Equal(mode, c.Mode);
            Near(Pose.Position + offsets[mode], c.Position);
            Assert.True(c.Forward.Z < 0);
            Near(-Vector3.UnitX, Vector3.Normalize(Vector3.Cross(c.Up, c.Forward)));
            Assert.Equal(240, c.Camera().Cy);
            c.Update(Pose, Car(), 0);
            Near(Pose.Position + offsets[mode], c.Position);
            c.SetLookBehind(false, Pose, Car());
            Assert.False(c.LookingBehind);
            Near(originalPosition, c.Position); Near(originalForward, c.Forward); Near(originalUp, c.Up);
            Assert.Equal(mode == 0 ? 200 : 240, c.Camera().Cy);
        }
    }

    [GameDataFact]
    public void RearUsesRotatedAndBankedCarBasisAndOriginalAnimationAngles()
    {
        var pose = new RenderPose(new Vector3(1, 2, 3), -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY);
        var anim = new CarAnim(0) { Pitch = 0x100, Roll = 0x200 };
        foreach (int mode in new[] { 0, 1 })
        {
            var c = Camera(mode); c.Snap(pose, Car(), anim);
            c.SetLookBehind(true, pose, Car(), anim);
            float z = mode == 0 ? -.5f : -0xE666 / 65536f;
            Near(pose.Position + pose.Up * (0x4CCC / 65536f) + pose.Forward * z, c.Position);
            float pitch = (mode == 0 ? 0x200 : 0x100 - 0x38E) * (2 * MathF.PI / 65536);
            Near(-pose.Forward * MathF.Cos(pitch) - pose.Up * MathF.Sin(pitch), c.Forward);
            float roll = (mode == 0 ? 0x200 : 0x100) * (2 * MathF.PI / 65536);
            Near((pose.Up * MathF.Cos(pitch) - pose.Forward * MathF.Sin(pitch)) * MathF.Cos(roll)
                + pose.Right * MathF.Sin(roll), c.Up);
        }
    }

    [GameDataFact]
    public void ChaseRearDoesNotOverwriteRetainedSmoothedOffset()
    {
        foreach (int mode in new[] { 2, 3 })
        {
            var c = Camera(mode);
            var rotated = Pose with { Right = -Vector3.UnitZ, Forward = Vector3.UnitX };
            c.Update(rotated, Car(), 1.0 / 32);
            var retained = c.Position;
            c.SetLookBehind(true, rotated, Car());
            for (int i = 0; i < 10; i++) c.Update(rotated, Car(), 1.0 / 32);
            Assert.True(c.Position.X > Pose.Position.X);
            Assert.True(c.Forward.X < 0);
            c.SetLookBehind(false, rotated, Car());
            Near(retained, c.Position);
        }
    }

    [GameDataFact]
    public void RaceFrameHandlesHoldAndReleaseWithoutSimulationStepAndPauseFreezesIt()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false);
        var front = d.Cam.Position;
        var keys = default(DriveKeys) with { LookBehind = true };
        Assert.Equal(0, d.Frame(0, keys));
        Assert.True(d.Cam.LookingBehind);
        var rear = d.Cam.Position;
        d.Frame(0, keys); Near(rear, d.Cam.Position);
        d.Frame(0, default); Near(front, d.Cam.Position);
        d.RunAutopilot(12);
        Assert.True(d.Pause());
        d.Frame(1, keys);
        Assert.False(d.Cam.LookingBehind);
        d.Resume(keys); d.Frame(0, keys);
        Assert.True(d.Cam.LookingBehind);
        d.Frame(0, default);
        Assert.False(d.Cam.LookingBehind);
    }
}
