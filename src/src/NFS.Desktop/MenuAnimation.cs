using NFS.Core;

namespace NFS.Desktop;

/// <summary>State machine of the flickering bars and the title cross-fade (cosmetic, own [ref:1114]
/// <see cref="EaRandom"/> stream seeded like the original's initial state). The original steps it every 0x14 ticks of the 128 Hz
/// timer, i.e. 0x14 ticks = 0.15625 s. Data: counter (mod 0x140), four bars with [ref:1115] [ref:3392]
/// level (start 0x28), speed, offset x / y and shape variant. [ref:1116]</summary>
public sealed class MenuAnimation
{
    public const double StepSeconds = 0x14 / 128.0;
    public const int CounterModulo = 0x140, TitleSteps = 0x50, TitleCount = 4;
    /// <summary>A key/event pauses the animation for 0x14 ticks of 128 Hz. [ref:1117] [ref:3393]</summary>
    public const int PauseTicks = 0x14;
    const int Rest = 0x28;
    const double TimerHz = 128;

    readonly EaRandom _rng = new();
    double _accumulator;
    double _pauseTicks;

    /// <summary>0..0x13F; the title colour is Counter / 0x50, its cross-fade level (Counter % 0x50) * 255 / 0x50. [ref:1118]</summary>
    public int Counter { get; private set; }
    /// <summary>Level per bar: 0x28 at rest; drawn as a translucency level 0..255. [ref:1119]</summary>
    public int[] Level { get; } = [Rest, Rest, Rest, Rest];
    public int[] Speed { get; } = new int[4];
    public int[] OffsetX { get; } = new int[4];
    public int[] OffsetY { get; } = new int[4];
    public int[] Variant { get; } = new int[4];
    public long Steps { get; private set; }

    public int TitleIndex => Counter / TitleSteps;
    public int TitleFade => Counter % TitleSteps * 255 / TitleSteps;

    /// <summary>Gate (animates when 0 or below the 128 Hz clock); after a key it is closed [ref:1120] [ref:3394]
    /// until <see cref="PauseTicks"/> ticks have passed.</summary>
    public bool Paused => _pauseTicks > 0;

    /// <summary>Menu clock step. <paramref name="keyPressed"/> = the keyboard state changed in this frame
    /// or an event arrived: the pause timer is set to PauseTicks, so the step is taken [ref:1121] [ref:3395]
    /// the next one happens only when the gate opens (pause time is not remembered). <paramref name="suspended"/> =
    /// flag (the config icons took a step in the previous frame): not called. [ref:1122]</summary>
    public void Advance(double seconds, bool keyPressed = false, bool suspended = false)
    {
        bool paused = _pauseTicks > 0;
        if (suspended)
        {
            // Gate: in the iteration after the icon step the original does not call – [ref:1123]
            // neither the bar step nor the keyboard read (pause). Time keeps running (it is an absolute deadline): [ref:1124]
            // after the gate opens at most ONE overdue step is due, and the deadline is again now + 0x14.
            if (paused)
            {
                _pauseTicks = Math.Max(0, _pauseTicks - seconds * TimerHz);
                if (_pauseTicks == 0) _accumulator = StepSeconds;     // time gate expired: the deferred step runs once the gate is released
            }
            else _accumulator = Math.Min(_accumulator + seconds, StepSeconds);
            return;
        }
        if (keyPressed) _pauseTicks = PauseTicks;
        else if (paused) _pauseTicks = Math.Max(0, _pauseTicks - seconds * TimerHz);
        if (_pauseTicks > 0) { _accumulator = 0; return; }
        _accumulator += paused ? StepSeconds : seconds;
        while (_accumulator >= StepSeconds) { _accumulator -= StepSeconds; Step(); }
    }

    public void Step()
    {
        Steps++;
        Counter = (Counter + 1) % CounterModulo;
        for (int i = 0; i < 4; i++)
        {
            int v = Speed[i];
            if (v > 0)
            {
                Level[i] += v;
                if (i == 3 ? Level[i] > 0x6D : Level[i] > 0xB3) Speed[i] = 0;
            }
            else if (v < 0)
            {
                Level[i] += v;
                if (Level[i] <= Rest) { Speed[i] = 0; Level[i] = Rest; }
            }
            else if (i == 3)
            {
                int even = Variant[3] & 0xFE;
                if (Level[3] > Rest) { Speed[3] = -3; Variant[3] = (int)(_rng.Next() & 1) + even; }
                else { Speed[3] = 3; Variant[3] = (int)(_rng.Next() & 1) + (even + 2) % 6; }
            }
            else
            {
                Variant[i] = (int)(_rng.Next() & 1);
                if (Level[i] > Rest) Speed[i] = -(int)(_rng.Next() % 10) - 10;
                else if (_rng.Next() % 5 == 0)
                {
                    Speed[i] = (int)(_rng.Next() % 10) + 10;
                    OffsetX[i] = (int)(_rng.Next() % 0x18) - 0xC;
                    OffsetY[i] = (int)(_rng.Next() % 0x18) - 0xC;
                    if (i == 2) OffsetY[i] -= 0x1E;
                }
            }
        }
    }
}
