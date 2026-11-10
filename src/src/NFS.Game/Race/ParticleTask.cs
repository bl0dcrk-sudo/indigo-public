using NFS.Core;
using NFS.Render;

namespace NFS.Game.Race;

/// <summary>
/// Effect particles of one race (presentation): feeds <see cref="ParticleSystem"/> from the cars around [ref:2055]
/// (<see cref="RaceRunner.BeforeCarPost"/> / <see cref="RaceRunner.AfterCarPost"/>) and runs the 4-tick update once per tick. [ref:2056]
/// It reads CarObj only ("Particles (sfx)"); the random numbers come from the system's own stream, not from the simulation. [ref:2057]
/// </summary>
public sealed class ParticleTask : IDisposable
{
    readonly RaceRunner _race;
    bool _wasCrashing;
    int _updatedTick = -1;

    public ParticleSystem Particles { get; }

    public ParticleTask(RaceRunner race, int seed = 0)
    {
        _race = race;
        Particles = new ParticleSystem(seed);
        Particles.Reset(race.Tick);
        race.BeforeCarPost += OnBefore;
        race.AfterCarPost += OnAfter;
    }

    void OnBefore(SimCar car)
    {
        int tick = _race.Tick;
        if (_updatedTick != tick) { _updatedTick = tick; Particles.Update(tick); }
        _wasCrashing = ParticleEmitter.BeforePost(car.Obj, Particles, tick);
    }

    void OnAfter(SimCar car) => ParticleEmitter.AfterPost(car.Obj, Particles, _race.Tick, _wasCrashing);

    public void Dispose()
    {
        _race.BeforeCarPost -= OnBefore;
        _race.AfterCarPost -= OnAfter;
    }
}
