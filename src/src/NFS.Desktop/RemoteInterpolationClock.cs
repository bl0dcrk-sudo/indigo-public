using NFS.Game.Session;

namespace NFS.Desktop;

/// <summary>Maps local monotonic time to a bounded server-step presentation target (32 Hz).</summary>
/// <remarks>The clock follows snapshot <em>arrival</em>: step zero is the minimum of arrival − steps · step over the last
/// <see cref="ArrivalWindow"/> snapshots, so jitter is filtered while a server schedule slip (bounded catch-up) is followed
/// within about two seconds instead of eating the buffer for the rest of the race. The start estimate of the client
/// (server time, half a round trip earlier) only runs the clock until the first snapshot arrives.</remarks>
public sealed class RemoteInterpolationClock(int delaySteps = 4)
{
    public const long StepMicros = 31_250;
    public const int ArrivalWindow = 64;
    public int DelaySteps { get; } = Math.Max(0, delaySteps);
    readonly long[] _arrivals = new long[ArrivalWindow];
    int _arrivalNext, _arrivalCount;
    int _previous = -1, _latest = -1;
    long? _stepZeroMicros;
    double _target;

    public void Synchronize(long? stepOneLocalMicros)
    {
        if (_arrivalCount == 0 && stepOneLocalMicros is { } t)
            _stepZeroMicros = _stepZeroMicros is { } old ? Math.Min(old, t - StepMicros) : t - StepMicros;
    }

    public void Observe(RaceViewFrame frame, long arrivalMicros)
    {
        if (frame.Steps <= _latest) return;
        _previous = _latest;
        _latest = frame.Steps;
        // Step zero can sit in Ready for seconds: it does not describe a running clock.
        if (frame.Steps == 0) return;
        _arrivals[_arrivalNext] = arrivalMicros - frame.Steps * StepMicros;
        _arrivalNext = (_arrivalNext + 1) % ArrivalWindow;
        _arrivalCount = Math.Min(_arrivalCount + 1, ArrivalWindow);
        long zero = long.MaxValue;
        for (int i = 0; i < _arrivalCount; i++) zero = Math.Min(zero, _arrivals[i]);
        _stepZeroMicros = zero;
    }

    public double Fraction(long nowMicros)
    {
        if (_latest < 0 || _previous < 0) return 1;
        double targetStep = TargetStep(nowMicros);
        return Math.Clamp((targetStep - _previous) / (_latest - _previous), 0, 1);
    }

    public double TargetStep(long nowMicros)
    {
        double estimate = _stepZeroMicros is { } zero ? (nowMicros - zero) / (double)StepMicros - DelaySteps : 0;
        _target = Math.Max(_target, Math.Clamp(estimate, 0, Math.Max(0, _latest)));
        return _target;
    }

    public int DisplayedSteps(long nowMicros)
    {
        if (_latest < 0) return 0;
        double target = TargetStep(nowMicros);
        return Math.Clamp((int)Math.Floor(target), 0, _latest);
    }
}
