namespace NFS.Core;

/// <summary>AI collision reactions and pursuit, with the original car state and slice-delta convention.</summary>
public static class AiReactions
{
    // Reverse is attempted even for humans and while already chasing. [ref:0152]
    public static void OnHit(CarObj c, int skill, int triggerCount, int psychoTime, int tick)
    {
        if (c.Impact == 0 || c.ImpactOther is not { } other) return;
        int speed = c.AiSpeed > 0 ? c.AiSpeed : unchecked(-c.AiSpeed);
        if (speed < 0xA0000) AiDriveModel.ReverseToggle(c, 0x60, tick);
        if (c.PsychoTicks > 0 || (other.Flags & CarFlags.LocalHuman) == 0
            || (c.Flags & CarFlags.Opponent) == 0 || skill == 0) return;
        c.PsychoTicks = 0;
        if (c.PsychoTarget != other) { c.PsychoHits = 0; c.PsychoTarget = other; }
        c.PsychoHits = unchecked(c.PsychoHits + 1);
        if (c.PsychoHits > triggerCount) c.PsychoTicks = psychoTime;
    }

    // Called after the impulse has been applied. [ref:0153]
    public static void OnWallHit(CarObj c, int tick)
    {
        if ((c.Flags & CarFlags.LocalHuman) != 0) return;
        int speed = c.AiSpeed > 0 ? c.AiSpeed : unchecked(-c.AiSpeed);
        if (speed < 0xA0000) AiDriveModel.ReverseToggle(c, 0x60, tick);
    }

    // signed distance, deliberately NOT absolute distance. [ref:0154]
    public static bool PsychoActive(CarObj c, int slices, bool reverse, bool raceStarted)
    {
        if (c.PsychoTarget is not { } target
            || AiSpeedPlanning.SliceDelta(c.Trk.Slice, target.Trk.Slice, slices) * (reverse ? -1 : 1) >= 0x4C
            || c.Rail != 0 || ((c.Flags & CarFlags.Racer) != 0 && c.RaceState == 2) || !raceStarted)
        {
            c.PsychoTicks = 0;
            return false;
        }
        return c.PsychoTicks > 0;
    }

    // baseSpeed is the base target speed, used when the chase conditions do not apply. [ref:0155]
    public static void PsychoChaseSpeed(CarObj c, int baseSpeed, int slices, bool reverse)
    {
        if (c.PsychoTarget is not { } target) return;
        int dir = reverse ? -1 : 1;
        int d = AiSpeedPlanning.SliceDelta(c.Trk.Slice, target.Trk.Slice, slices) * dir;
        int v = target.AiSpeed * dir;
        if (d > 100 || (v <= 0 && d > 0) || (-0x1E0000 < v && v < 0x1E0000 && d > 0)) c.AiFlags |= 0x1000;
        if (d < -100 || (v >= 0 && d < 0) || (-0x1E0000 < v && v < 0x1E0000 && d < 0)) c.AiFlags &= ~0x1000;
        bool wrongWay = (c.AiFlags & 0x1000) != 0;
        // The original uses strict bounds: -100 < d < 0 and v < -2. [ref:0156]
        int speed = (!wrongWay == reverse)
            ? (d > -100 && d < 0 && v < -0x20000 ? v + 0x20000 : baseSpeed)
            : (d < 100 && d > 0 && v >= 0 ? v - 0x80000 : baseSpeed);
        c.AiTargetSpeed = wrongWay != reverse ? -speed : speed;
    }

    // Psycho branch of the update: runs once per pre-think, regardless of the LOD tick count. [ref:0157]
    public static void UpdatePsycho(CarObj c, int slices, bool reverse, bool raceStarted,
        Action<CarObj, int, int, int>? hornRequest = null)
    {
        if (c.PsychoTicks <= 0) return;
        if (PsychoActive(c, slices, reverse, raceStarted))
        {
            PsychoChaseSpeed(c, c.AiTargetSpeed, slices, reverse);
            var (pattern, length, volume) = (c.Trk.Slice & 3) switch
            {
                0 => (2, 10, 5), 1 => (1, 50, 4), 2 => (2, 30, 2), _ => (3, 1, 40)
            };
            hornRequest?.Invoke(c, pattern, length, volume); // camera/audio service (not yet hosted) [ref:0158]
        }
        c.PsychoTicks--;
        if (c.PsychoTicks <= 0) { c.PsychoHits = 0; c.AiFlags &= ~0x1000; }
    }

    // First branch of the psycho target logic (called from Drive). [ref:0159]
    public static bool TargetPsycho(CarObj c, int slices)
    {
        if (c.PsychoTicks <= 0 || c.Direction != 1 || c.PsychoTarget is not { } target) return false;
        int d = AiSpeedPlanning.SliceDelta(c.Trk.Slice, target.Trk.Slice, slices);
        int abs = d > 0 ? d : -d;
        bool wrongWay = (c.AiFlags & 0x1000) != 0;
        if (abs >= 30 || !(wrongWay ? d > 0 : d < 0)) return false;
        c.ToTarget = target.Pos - c.Pos;
        c.SmoothedLaneOffset = target.LateralOffsetCur;
        return true;
    }
}
