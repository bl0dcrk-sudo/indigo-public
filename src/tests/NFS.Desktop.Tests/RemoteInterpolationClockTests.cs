using NFS.Game.Session;
using System.Net;

namespace NFS.Desktop.Tests;

public sealed class RemoteInterpolationClockTests
{
    static RaceViewFrame Frame(int steps, long sequence) => new()
    {
        RaceId = 8, SnapshotSequence = sequence, Kind = RaceViewFrameKind.Step,
        Steps = steps, SimulationTick64 = steps * 2, RaceStarted = true, NumLaps = 2, RacerCount = 2,
        Cars = [], Parked = [], Ranking = [], AppliedInput = [], Knocked = new Dictionary<ushort, NFS.Core.KnockedObject>(),
        Skids = new(),
    };

    [Fact]
    public void LoopbackAndLanUseTwoStepBuffersWhilePublicConnectionsKeepJitterMargin()
    {
        Assert.Equal(2, RemoteDesktopSession.PresentationDelayFor(new IPEndPoint(IPAddress.Loopback, 27100)));
        Assert.Equal(2, RemoteDesktopSession.PresentationDelayFor(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 27100)));
        Assert.Equal(4, RemoteDesktopSession.PresentationDelayFor(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 27100)));

        var loopback = new RemoteInterpolationClock(RemoteDesktopSession.LoopbackPresentationDelaySteps);
        loopback.Observe(Frame(10, 1), 1_000_000);
        loopback.Observe(Frame(11, 2), 1_031_250);
        Assert.Equal(.5, loopback.Fraction(1_078_125), 3); // half-way between steps 10 and 11

        var remoteDefault = new RemoteInterpolationClock();
        Assert.Equal(4, remoteDefault.DelaySteps); // explicit clock defaults remain available to other callers
    }

    [Theory]
    [InlineData(50, 0)]
    [InlineData(100, .2)]
    [InlineData(200, 1)]
    public void PresentationUsesServerStepsAndFreezesWhenTheBufferRunsDry(int elapsedMs, double expected)
    {
        var clock = new RemoteInterpolationClock();
        clock.Observe(Frame(10, 1), 1_000_000);
        clock.Observe(Frame(11, 2), 1_031_250);

        Assert.Equal(expected, clock.Fraction(1_031_250L + elapsedMs * 1_000L), 3);
    }

    [Fact]
    public void JitterAndDroppedSnapshotsInterpolateAcrossTheActualServerStepGap()
    {
        var clock = new RemoteInterpolationClock();
        clock.Observe(Frame(20, 1), 2_000_000);
        clock.Observe(Frame(25, 2), 2_081_000); // delayed packet, four snapshots were lost

        Assert.InRange(clock.Fraction(2_081_000), .19, .21); // the target uses server steps, not arrival spacing
        Assert.InRange(clock.Fraction(2_181_000), .83, .85);
        Assert.Equal(1, clock.Fraction(2_281_000)); // bounded at latest pose; no endless extrapolation
    }

    [Fact]
    public void DuplicateAndOutOfOrderFramesCannotMoveTheInterpolationClockBack()
    {
        var clock = new RemoteInterpolationClock();
        clock.Observe(Frame(4, 4), 100_000);
        clock.Observe(Frame(6, 6), 180_000);
        clock.Observe(Frame(5, 5), 190_000);

        Assert.Equal(6, clock.DisplayedSteps(305_000));
    }

    // Windows host: the server oversleeps to the next 15.625 ms timer tick, the client drains packets once per 60 Hz frame.
    // Counts render frames whose presented step did not advance (a freeze that the next frame turns into a jump).
    static int Freezes(RemoteInterpolationClock clock, long fromMicros, long toMicros, long slipAtMicros = long.MaxValue,
        long slipMicros = 0, long? stepOneEstimate = null)
    {
        const long serverTick = 15_625, frame = 16_667, step = RemoteInterpolationClock.StepMicros;
        int nextStep = 1, freezes = 0;
        double last = -1;
        for (long now = 0; now < toMicros; now += frame)
        {
            while (true)
            {
                long due = (nextStep - 1) * step + (nextStep * step >= slipAtMicros ? slipMicros : 0);
                long sent = (due + serverTick - 1) / serverTick * serverTick;
                if (sent > now) break;
                clock.Observe(Frame(nextStep, nextStep), now);
                nextStep++;
            }
            clock.Synchronize(stepOneEstimate);
            double target = clock.TargetStep(now);
            if (now >= fromMicros && target - last < .25) freezes++;
            last = target;
        }
        return freezes;
    }

    [Fact]
    public void LoopbackTimingJitterDoesNotStarveThePresentation()
    {
        var clock = new RemoteInterpolationClock(RemoteDesktopSession.LoopbackPresentationDelaySteps);
        Assert.Equal(0, Freezes(clock, 1_000_000, 10_000_000));
    }

    [Fact]
    public void AServerStallDoesNotEatTheBufferForTheRestOfTheRace()
    {
        // The server slips its schedule by 150 ms (bounded catch-up); snapshots arrive that much later from then on.
        var clock = new RemoteInterpolationClock(RemoteDesktopSession.LoopbackPresentationDelaySteps);
        Assert.Equal(0, Freezes(clock, 7_000_000, 12_000_000, slipAtMicros: 3_000_000, slipMicros: 150_000));
    }

    [Fact]
    public void TheStartEstimateOnlyStartsTheClockAndArrivalsSetTheBuffer()
    {
        // StepOneLocalTime subtracts half the round trip (server time); presentation must follow arrival time instead.
        var clock = new RemoteInterpolationClock(RemoteDesktopSession.LoopbackPresentationDelaySteps);
        Assert.Equal(0, Freezes(clock, 1_000_000, 10_000_000, stepOneEstimate: -20_000));
    }
}
