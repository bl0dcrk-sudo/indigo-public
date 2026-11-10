using System.Numerics;
using NFS.Core;

namespace NFS.Desktop;

/// <summary>Presentation-only pose in metres; never written back into CarObj.</summary>
public readonly record struct RenderPose(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward)
{
    public static RenderPose From(Vec3 p, Mat3 o) => new(RaceScene.ToWorld(p),
        RaceScene.ToWorld(o.Row0), RaceScene.ToWorld(o.Row1), RaceScene.ToWorld(o.Row2));

    Quaternion Rotation => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
        Right.X, Right.Y, Right.Z, 0, Up.X, Up.Y, Up.Z, 0,
        Forward.X, Forward.Y, Forward.Z, 0, 0, 0, 0, 1)));

    public static RenderPose Interpolate(RenderPose previous, RenderPose current, double fraction)
    {
        if (fraction <= 0) return previous;
        if (fraction == 1) return current;
        if (fraction > 1 && previous == current) return current; // no extrapolation after a snap/reset
        var q = Quaternion.Slerp(previous.Rotation, current.Rotation, (float)fraction);
        return new(Vector3.Lerp(previous.Position, current.Position, (float)fraction),
            Vector3.Transform(Vector3.UnitX, q), Vector3.Transform(Vector3.UnitY, q), Vector3.Transform(Vector3.UnitZ, q));
    }
}

/// <summary>Last two published 32 Hz states. Local rendering may sample past the newest state to cover the current render
/// interval; a changed epoch or an unannounced jump &gt; 8 m snaps instead of blending or predicting across the discontinuity.</summary>
public sealed class PoseHistory(RenderPose initial)
{
    RenderPose _previous = initial, _current = initial;
    int _epoch;

    public void Snap(RenderPose pose) => _previous = _current = pose;
    public void Snap(RenderPose pose, int epoch) { Snap(pose); _epoch = epoch; }

    public bool Push(RenderPose pose, bool discontinuity = false)
    {
        // 8 m per 1/32 s exceeds normal racing speeds; catches unannounced teleports too.
        bool snap = discontinuity || Vector3.DistanceSquared(_current.Position, pose.Position) > 64;
        _previous = snap ? pose : _current;
        _current = pose;
        return snap;
    }

    /// <summary>Pushes the pose of a published frame; a pose epoch different from the previous one is a discontinuity.</summary>
    public bool Push(RenderPose pose, int epoch)
    {
        bool jumped = epoch != _epoch;
        _epoch = epoch;
        return Push(pose, jumped);
    }

    public RenderPose Sample(double fraction) => RenderPose.Interpolate(_previous, _current, fraction);
}
