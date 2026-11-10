using System.Diagnostics;

namespace NFS.Desktop;

/// <summary>Frame-rate caps of the host (battery / CPU use). Only the presentation is paced; the race simulation keeps its own
/// 64 Hz steps (frame time in, steps out), so a lower frame rate never changes the physics or the network protocol.
/// Settings: <c>port.json</c> keys <c>maxFps</c> (race), <c>menuFps</c> (menus, lobby; both switched together by BATTERY SAVER on the port options screen), <see cref="InactiveFps"/> is fixed.</summary>
public static class FramePacing
{
    public const int DefaultRaceFps = 60, DefaultMenuFps = 30, InactiveFps = 10, MinFps = 10, MaxFpsLimit = 240;
    /// <summary>"BATTERY SAVER" of the port options screen: race / menu caps while it is on.</summary>
    public const int BatteryRaceFps = 30, BatteryMenuFps = 20;

    /// <summary>True when the configured race cap is 1..<see cref="BatteryRaceFps"/> (set by <see cref="SetBatterySaver"/> or by hand in port.json).
    /// 0 (no cap) and the default 60 count as off.</summary>
    public static bool IsBatterySaver(PortSettings? settings) => TargetFps(settings, racing: true, windowActive: true) is > 0 and <= BatteryRaceFps;

    /// <summary>On: <c>maxFps</c> 30, <c>menuFps</c> 20. Off: both keys removed (defaults 60 / 30; a hand-written custom cap is not remembered).</summary>
    public static void SetBatterySaver(PortSettings settings, bool on)
    {
        settings.MaxFps = on ? BatteryRaceFps : null;
        settings.MenuFps = on ? BatteryMenuFps : null;
    }

    /// <summary>Target frames per second for the current screen; 0 = no cap (vsync only). A configured value is clamped to
    /// [<see cref="MinFps"/>, <see cref="MaxFpsLimit"/>]; the unfocused window always gets <see cref="InactiveFps"/> at most.</summary>
    public static int TargetFps(PortSettings? settings, bool racing, bool windowActive)
    {
        int configured = racing ? settings?.MaxFps ?? DefaultRaceFps : settings?.MenuFps ?? DefaultMenuFps;
        int fps = configured <= 0 ? 0 : Math.Clamp(configured, MinFps, MaxFpsLimit);
        if (!windowActive) fps = fps == 0 ? InactiveFps : Math.Min(fps, InactiveFps);
        return fps;
    }
}

/// <summary>Sleeps the rest of the frame period (the clock and the sleep are injectable for tests). Sleeps whole milliseconds
/// (never spins), so the achieved rate may be a little above the target.</summary>
public sealed class FrameLimiter(Func<long>? ticks = null, Action<int>? sleep = null)
{
    readonly Func<long> _ticks = ticks ?? (() => Stopwatch.GetTimestamp());
    readonly Action<int> _sleep = sleep ?? (ms => Thread.Sleep(ms));
    readonly long _frequency = ticks == null ? Stopwatch.Frequency : TimeSpan.TicksPerSecond;
    long _last;
    bool _started;

    /// <summary>Call once per frame after presenting; sleeps when the frame was shorter than 1/<paramref name="fps"/> s.
    /// Returns the milliseconds slept.</summary>
    public int Wait(int fps)
    {
        long now = _ticks();
        int slept = 0;
        if (fps > 0 && _started)
        {
            double remaining = 1000.0 / fps - (now - _last) * 1000.0 / _frequency;
            if (remaining >= 1)
            {
                slept = (int)remaining;
                _sleep(slept);
                now = _ticks();
            }
        }
        _last = now;
        _started = true;
        return slept;
    }
}
