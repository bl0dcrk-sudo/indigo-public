using NFS.Core;
using NFS.Core.Constants;

namespace NFS.Game.Race;

/// <summary>
/// Tyre marks of one race (presentation): feeds <see cref="SkidMarks"/> from the cars after the step [ref:2384]
/// (runs only for cars that are active, not on rails and closer than 90 m to a player). Reads CarObj only. [ref:2385]
/// </summary>
public sealed class SkidMarkTask : IDisposable
{
    readonly RaceRunner _race;
    public SkidBuffer Buffer { get; } = new();
    public SkidMarks Marks { get; }

    /// <summary><see cref="RaceConstants.SkidMarkSurfaces"/> : per surface type (0..63), non-zero = the surface keeps tyre marks. [ref:2386]</summary>
    public static bool[] SurfaceTable() => RaceConstants.SkidMarkSurfaces.ToArray().Select(v => v != 0).ToArray();

    public SkidMarkTask(RaceRunner race, bool[] surfaceMarks)
    {
        _race = race;
        Marks = new SkidMarks(Buffer, s => (uint)s < (uint)surfaceMarks.Length && surfaceMarks[s]);
        race.AfterCarPost += OnPost;
    }

    void OnPost(SimCar car)
    {
        var c = car.Obj;
        if (c.Rail != 0 || c.Active == 0 || c.DistToPlayer >= 0x5A0000) return;   // guards: on rails, inactive, or 90 m or more from a player [ref:2387]
        Marks.CarSkidFx(c, _race.Tick);
    }

    public void Dispose() => _race.AfterCarPost -= OnPost;
}
