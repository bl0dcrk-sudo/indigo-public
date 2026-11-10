namespace NFS.Desktop;

/// <summary>While the car's +0x16C (CollisionCooldown, set to 0x30 by a manual reset and from simtune[8]) [ref:1489]
/// is non-zero, a render-side counter (+0x62C) advances once per race tick (last tick in +0x630) and the car is drawn [ref:1490]
/// only when its low bit is 0 (or the race is paused) - i.e. it blinks every other tick. Cooldown 0 always draws and leaves the counter alone.</summary>
public sealed class ResetBlink
{
    int _counter, _lastTick;

    public bool Visible(int cooldown, int raceTick, bool paused)
    {
        if (cooldown == 0) return true;
        if (raceTick != _lastTick) _counter++;
        _lastTick = raceTick;
        return (_counter & 1) == 0 || paused;
    }
}
