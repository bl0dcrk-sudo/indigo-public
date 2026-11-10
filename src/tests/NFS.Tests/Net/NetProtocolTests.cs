using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Tests.Net;

/// <summary>Stage 5a.3 protocol without game data: wire decoding of foreign/malformed data, the per-seat input rules (window,
/// hold → neutral, actions applied exactly once), the bounded event inbox and the exact tyre-mark delta stream.</summary>
public class NetProtocolTests
{
    static readonly byte[] Fp = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Varints_RoundTripExtremesAndRejectOverlongOrTruncatedData()
    {
        var w = new WireWriter(256);
        long[] values = [0, 1, -1, 63, -64, 64, int.MaxValue, int.MinValue, long.MaxValue / 3, -(long.MaxValue / 3)];
        foreach (var v in values) w.VarI(v);
        var r = new WireReader(w.Written);
        foreach (var v in values) Assert.Equal(v, r.VarI());
        r.End();
        Assert.Throws<NetProtocolException>(() => { var x = new WireReader(Enumerable.Repeat((byte)0xFF, 11).ToArray()); x.VarU(); });
        Assert.Throws<NetProtocolException>(() => { var x = new WireReader([0x80]); x.VarU(); });
        Assert.Throws<NetProtocolException>(() => { var x = new WireReader([0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2]); x.VarU(); });
        Assert.Throws<NetProtocolException>(() => { var x = new WireReader([1, 2]); x.U8(); x.End(); });
        Assert.Throws<InvalidOperationException>(() => new WireWriter(2).U32(5));
    }

    [Fact]
    public void Hello_RoundTripsAndRejectsForeignData()
    {
        var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, Fp, "a very long name indeed");
        var back = NetHello.Decode(hello.Encode());
        Assert.Equal(NetProtocol.Version, back.Version);
        Assert.Equal(Fp, back.DataFingerprint);
        Assert.Equal("a very long ", back.Name); // bounded name
        var bytes = hello.Encode();
        bytes[0] ^= 1;
        Assert.Throws<NetProtocolException>(() => NetHello.Decode(bytes));
        Assert.Throws<NetProtocolException>(() => NetHello.Decode(hello.Encode().AsSpan(0, 20)));
        Assert.Null(NetReject.TryDecode([1, 2, 3]));
        Assert.Equal(NetRejectReason.RaceFull, NetReject.TryDecode(new NetReject(NetRejectReason.RaceFull).Encode())!.Reason);
    }

    [Fact]
    public void Hello_CarriesTheMenuModelAndRejectsUnknownTypes()
    {
        Assert.Equal(NetProtocol.Version, 19); // v19: purpose byte in the hello (play / mod download); v18: car keys in the lobby car/member; v17: mods fingerprint in the hello; v16: operator cancels the countdown; v15: create = name + limit + mode, lobby options/skill/catch-up/style/colour, cars 0..11; v14: car type in the result rows; v13: spectator flag of the welcome (v12: knockout mode, series state, straggler countdown)
        var explicitChoice = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, Fp, "alice", 9);
        Assert.Equal(9, NetHello.Decode(explicitChoice.Encode()).CarType);
        Assert.Equal(NetProtocol.MaxCarType, NetHello.Decode((explicitChoice with { CarType = NetProtocol.MaxCarType }).Encode()).CarType);
        Assert.Equal(0, NetHello.Decode((explicitChoice with { CarType = 0 }).Encode()).CarType);
        Assert.Equal(NetProtocol.NoCarType, NetHello.Decode((explicitChoice with { CarType = NetProtocol.NoCarType }).Encode()).CarType);
        Assert.True(NetProtocol.IsCarTypeOrNone(NetProtocol.NoCarType));
        Assert.False(NetProtocol.IsCarTypeOrNone(NetProtocol.NoCarType - 1));
        Assert.False(NetProtocol.IsCarTypeOrNone(NetProtocol.MaxCarType + 1));
        Assert.True(NetProtocol.IsCarNumberOrNone(0x35));

        // malformed / out-of-range model values never reach a server seat
        Assert.Throws<ArgumentOutOfRangeException>(() => (explicitChoice with { CarType = NetProtocol.MaxCarNumber + 1 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (explicitChoice with { CarType = NetProtocol.NoCarType - 1 }).Encode());
        var encoded = explicitChoice.Encode();
        int carByte = encoded.Length - 3 - 33; // before the empty resume token length, the purpose byte and the empty mods block (32-byte fingerprint + count)
        Assert.Equal(15, NetHello.Decode([.. encoded[..carByte], 15, 0, 0, .. encoded[^33..]]).CarType); // a number of the receiver's catalog (checked by the server)
        Assert.Throws<NetProtocolException>(() => NetHello.Decode([.. encoded[..carByte], 0x80, 0, 0, .. encoded[^33..]]));
        Assert.Equal(NetProtocol.NoCarType, NetHello.Decode([.. encoded[..carByte], 0xFF, 0, 0, .. encoded[^33..]]).CarType);
        Assert.Throws<NetProtocolException>(() => NetHello.Decode(encoded.AsSpan(0, encoded.Length - 1))); // no resume token length
    }

    [Fact]
    public void Hello_CarriesTheApplicationVersionAndAnOptionalResumeToken()
    {
        var custom = new Version(1, 2, 3, 4);
        var token = Enumerable.Range(1, NetProtocol.ResumeTokenLength).Select(i => (byte)i).ToArray();
        var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, Fp, "alice", 9, custom, token);
        var back = NetHello.Decode(hello.Encode());
        Assert.Equal(custom, back.AppVersion);
        Assert.Equal(token, back.ResumeToken);

        var fresh = NetHello.Decode(new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, Fp, "bob").Encode());
        Assert.Equal(BuildInfo.AppVersion, fresh.AppVersion);
        Assert.Null(fresh.ResumeToken);

        Assert.Throws<ArgumentException>(() => (hello with { ResumeToken = new byte[5] }).Encode());
        var badLength = hello.Encode();
        badLength[badLength.Length - 1 - NetProtocol.ResumeTokenLength] = 5;
        Assert.Throws<NetProtocolException>(() => NetHello.Decode(badLength));
    }

    [Fact]
    public void Reject_CarriesTheServerApplicationVersion()
    {
        var custom = new Version(9, 8, 7, 6);
        var back = NetReject.TryDecode(new NetReject(NetRejectReason.AppVersionMismatch, NetProtocol.Version, NetProtocol.PhysicsVersion, custom).Encode())!;
        Assert.Equal(NetRejectReason.AppVersionMismatch, back.Reason);
        Assert.Equal(custom, back.ServerAppVersion);
        Assert.Equal(BuildInfo.AppVersion, NetReject.TryDecode(new NetReject(NetRejectReason.RaceFull).Encode())!.ServerAppVersion);
    }

    [Fact]
    public void Reject_IsReadableAcrossProtocolVersions()
    {
        var legacy = new NetReject(NetRejectReason.ProtocolMismatch).Encode(clientVersion: 7);
        Assert.Equal(9, legacy.Length);                                  // the layout a v7 client can parse
        var back = NetReject.TryDecode(legacy)!;
        Assert.Equal(NetRejectReason.ProtocolMismatch, back.Reason);
        Assert.Equal(NetProtocol.Version, back.ServerVersion);
        Assert.Equal(new Version(0, 0, 0, 0), back.ServerAppVersion);
        byte[] newer = [.. new NetReject(NetRejectReason.RaceFull).Encode(), 1, 2, 3];   // fields a newer server appends
        Assert.Equal(NetRejectReason.RaceFull, NetReject.TryDecode(newer)!.Reason);
    }

    [Fact]
    public void ClientRejectsAnOutOfRangeMenuModelBeforeSendingAnything()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaceClient(null, Fp, "alice", carType: NetProtocol.MaxCarType + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaceClient(null, Fp, "alice", carType: NetProtocol.NoCarType - 1));
        var client = new RaceClient(null, Fp, "alice", carType: 7);
        Assert.Equal(7, NetHello.Decode(client.Hello()).CarType);
    }

    [Fact]
    public void ClientHelloCarriesTheCurrentApplicationVersion()
    {
        var client = new RaceClient(null, Fp, "alice");
        Assert.Equal(BuildInfo.AppVersion, NetHello.Decode(client.Hello()).AppVersion);
    }

    static NetInputPacket Packet(long firstSeq, long firstTarget, int count, params NetAction[] actions) =>
        new(7, 9, 0, Enumerable.Range(0, count).Select(i => new NetInputSample(firstSeq + i, firstTarget + i, (sbyte)i, 200, 0, 0)).ToArray(), actions);

    [Fact]
    public void Welcome_RejectsInvalidSetupSeatMappingAndClockBeforeLoadingData()
    {
        var valid = new NetWelcome(7, 9, 0, new[] { 0, 1 }, 2, 0, new[] { 0, 1 }, new[] { 100, 100 }, true, 2,
            NetProtocol.StepMicros, NetProtocol.MaxFutureSteps, NetProtocol.HoldSteps, NetProtocol.ActionExpirySteps,
            NetProtocol.InputRedundancy, NetProtocol.MaxDatagram, new RaceSetup().Block, Fp, Fp);
        Assert.Equal(0, NetWelcome.Decode(valid.Encode()).Seat);
        foreach (var bad in new[] { valid with { SetupBlock = [1] }, valid with { Redline = [] },
                     valid with { SeatCarIds = new[] { 0, 0 } }, valid with { StepMicros = 0 }, valid with { Epoch = 0 } })
            Assert.Throws<NetProtocolException>(() => NetWelcome.Decode(bad.Encode()));
    }

    static NetInputPacket Decode(byte[] bytes)
    {
        var r = new WireReader(bytes);
        var h = NetHeader.Read(ref r);
        return NetInputPacket.Decode(h, ref r);
    }

    [Fact]
    public void InputPacket_RoundTripsAndRejectsOneShotBitsInHeldSamples()
    {
        var p = Packet(100, 40, 8, new NetAction(3, NetActionKind.Reset, 44));
        var back = Decode(p.Encode());
        Assert.Equal(p.Samples, back.Samples);
        Assert.Equal(p.Actions, back.Actions);
        Assert.True(p.Encode().Length < 64, "8 redundant samples + one action stay small");
        // a gear/reset bit inside a held sample would repeat while the sample is held: rejected as malformed
        var bad = p with { Samples = [new NetInputSample(1, 1, 0, 0, 0, PlayerInput.BtnGearUp)] };
        Assert.Throws<NetProtocolException>(() => Decode(bad.Encode()));
        var bytes = p.Encode();
        Assert.Throws<NetProtocolException>(() => Decode([.. bytes, 0])); // trailing byte
        Assert.Throws<NetProtocolException>(() => Decode(bytes[..^3]));     // truncated
    }

    [Fact]
    public void Scheduler_AcceptsOnlyTheWindowAndCountsLateFutureStaleAndDuplicates()
    {
        var s = new SeatInputScheduler();
        s.Accept(Packet(1, 10, 3), nextStep: 11);                                  // target 10 is late
        Assert.Equal(1, s.Late);
        s.Accept(Packet(10, 11 + NetProtocol.MaxFutureSteps, 1), nextStep: 11);    // beyond the window
        Assert.Equal(1, s.Future);
        s.Accept(Packet(2, 11, 2), nextStep: 11);                                  // duplicates of seq 2,3
        Assert.Equal(2, s.Duplicates);
        var (input, seq) = s.Next(11);
        Assert.Equal((2L, (sbyte)1), (seq, input.Steer));
        s.Accept(Packet(1, 10, 3), nextStep: 12);                                  // already applied → stale, newer one is a duplicate
        Assert.True(s.Stale >= 2);
        Assert.Equal(2, s.AppliedSequence);
    }

    [Fact]
    public void Scheduler_HoldsContinuousControlsBrieflyThenDrivesNeutral()
    {
        var s = new SeatInputScheduler();
        s.Accept(new NetInputPacket(7, 9, 0, [new NetInputSample(1, 1, 50, 255, 0, PlayerInput.BtnHandbrake)], []), 1);
        Assert.Equal(255, s.Next(1).Input.Gas);
        for (int i = 0; i < NetProtocol.HoldSteps; i++)
        {
            var (held, seq) = s.Next(2 + i);
            Assert.Equal((255, (sbyte)50, PlayerInput.BtnHandbrake, 0L), (held.Gas, held.Steer, held.Buttons, seq));
        }
        Assert.Equal(default, s.Next(2 + NetProtocol.HoldSteps).Input);           // release by timeout: no endless gas
        Assert.Equal((NetProtocol.HoldSteps, 1L), ((int)s.HeldSteps, s.NeutralSteps));
    }

    [Fact]
    public void Scheduler_AppliedSequenceNeverRegressesAfterReordering()
    {
        var s = new SeatInputScheduler();
        s.Accept(Packet(5, 2, 1), 1);
        s.Accept(Packet(10, 1, 1), 1);
        Assert.Equal(10, s.Next(1).Sequence);
        Assert.Equal(0, s.Next(2).Sequence);
        Assert.Equal(10, s.AppliedSequence);
    }

    [Fact]
    public void RealtimeControls_LatestSequenceWinsAcrossReorderingThenHoldsAndGoesNeutral()
    {
        var s = new SeatInputScheduler();
        static NetInputPacket Realtime(long sequence, sbyte steer, byte gas) =>
            new(7, 9, 0, [new NetInputSample(sequence, 0, steer, gas, 0, 0)], []);

        var encoded = Realtime(20, 40, 220).Encode();
        var decoded = Decode(encoded);
        Assert.True(decoded.IsRealtimeControls);

        s.Accept(decoded, nextStep: 101);                 // new control datagram arrives
        s.Accept(Realtime(19, -40, 0), nextStep: 101);    // delayed older packet cannot roll it back
        var first = s.Next(101);
        Assert.Equal((220, (sbyte)40, 20L), (first.Input.Gas, first.Input.Steer, first.Sequence));
        // Latest controls are held for the configured short timeout; then missing heartbeats release all controls.
        for (int i = 0; i < NetProtocol.HoldSteps; i++)
            Assert.Equal(220, s.Next(102 + i).Input.Gas);
        Assert.Equal(default, s.Next(102 + NetProtocol.HoldSteps).Input);
        Assert.Equal(1, s.NeutralSteps);
    }

    [Fact]
    public void RealtimePacketRequiresSingleZeroTargetSample()
    {
        var invalid = new NetInputPacket(7, 9, 0,
            [new NetInputSample(1, 0, 1, 2, 3, 0), new NetInputSample(2, 1, 1, 2, 3, 0)], []);
        Assert.Throws<ArgumentException>(() => invalid.Encode());
    }

    [Fact]
    public void RealtimeActionsAreAppliedAtNextStepAndRemainIdempotent()
    {
        var s = new SeatInputScheduler();
        var sample = new NetInputSample(1, 0, 0, 255, 0, 0);
        var packet = new NetInputPacket(7, 9, 0, [sample], [new NetAction(1, NetActionKind.GearUp, 0)]);
        s.Accept(packet, nextStep: 42);
        s.Accept(packet, nextStep: 42);
        Assert.Equal((PlayerInput.BtnGearUp, 1L), (s.Next(42).Input.Buttons, s.ActionsApplied));
        Assert.Equal(0, s.Next(43).Input.Buttons);
        Assert.Equal(1, s.ActionsApplied);
    }

    [Fact]
    public void Scheduler_FarFutureActionCannotBlockCurrentAction()
    {
        var s = new SeatInputScheduler();
        s.Accept(Packet(1, 1, 1, new NetAction(1, NetActionKind.Reset, 10000)), 1);
        s.Accept(Packet(2, 2, 1, new NetAction(2, NetActionKind.GearUp, 2)), 1);
        Assert.Equal(PlayerInput.BtnGearUp, s.Next(2).Input.Buttons);
        Assert.Equal(1, s.Future);
        Assert.Equal(0, s.PendingActions);
    }

    [Fact]
    public void Scheduler_AppliesEachActionOnceOnePerStepAndExpiresStaleOnes()
    {
        var s = new SeatInputScheduler();
        NetAction gear = new(1, NetActionKind.GearUp, 5), reset = new(2, NetActionKind.Reset, 5);
        var p = Packet(1, 5, 1, gear, reset);
        for (int i = 0; i < 4; i++) s.Accept(p, 5);                                  // duplicated datagrams
        var buttons = Enumerable.Range(5, 4).Select(t => s.Next(t).Input.Buttons).ToArray();
        Assert.Equal([PlayerInput.BtnGearUp, PlayerInput.BtnReset, 0, 0], buttons);
        s.Accept(p, 9);                                                              // late resend after the ACK
        Assert.Equal(0, s.Next(9).Input.Buttons);
        Assert.Equal((2L, 2L), (s.ActionsApplied, s.LastActionId));
        s.Accept(Packet(9, 10, 1, new NetAction(3, NetActionKind.GearDown, 10)), 10);
        Assert.Equal(0, s.Next(10 + NetProtocol.ActionExpirySteps + 1).Input.Buttons); // too old to shift now
        Assert.Equal((1L, 3L), (s.ActionsExpired, s.LastActionId));
    }

    [Fact]
    public void EventInbox_MemoryIsBoundedDuplicatesDroppedAndSoundExpiresButResultNever()
    {
        var inbox = new RaceEventInbox(5, maxTracked: 64, expiryTicks64: 100);
        for (int i = 1; i <= 5000; i++)
        {
            inbox.Accept([new CountdownBeepEvent(5, i, i, i * 2, 0)]);
            inbox.TakeThrough(i, i * 2);
        }
        Assert.True(inbox.Tracked <= 64, $"tracked {inbox.Tracked}");
        inbox.Accept([new CountdownBeepEvent(5, 10, 10, 20, 0), new CountdownBeepEvent(6, 6000, 1, 1, 0), new CountdownBeepEvent(5, 5000, 5000, 1, 0)]);
        Assert.Equal(3, inbox.Rejected);                                             // old id below the floor, other race, duplicate
        var results = new RaceResults([]);
        inbox.Accept([new HornRequestEvent(5, 6001, 6001, 0, 1, 1, 1, 1), new RaceFinishedEvent(5, 6002, 6001, 0, results)]);
        var got = inbox.TakeThrough(6001, simulationTick64: 1000);                   // presented long after both events
        Assert.Equal([6002L], got.Select(e => e.EventId));                           // the horn expired, the result did not
        Assert.Equal(1, inbox.Expired);
    }

    static SkidBuffer Marks(int quads, int seed)
    {
        var b = new SkidBuffer();
        var rng = new Random(seed);
        for (int i = 0; i < quads; i++)
        {
            int x = rng.Next(-0x3000000, 0x3000000), z = rng.Next(-0x3000000, 0x3000000);
            b.Add(new Vec3(x, 0, z), new Vec3(x + 0x8000, 0, z + 0x10000), 1 + (i % 7 == 0 ? 1 : 0));
        }
        return b;
    }

    internal static void AssertSameSkids(SkidBuffer expected, SkidBuffer actual)
    {
        Assert.Equal((expected.Count, expected.Current, expected.Added), (actual.Count, actual.Current, actual.Added));
        for (int i = 0; i < expected.Count; i++)
        {
            SkidBuffer.Segment e = expected.Segments[i], a = actual.Segments[i];
            Assert.Equal((e.X, e.Y, e.Z, e.Quads), (a.X, a.Y, a.Z, a.Quads));
            Assert.Equal(e.Vertices.AsSpan(0, e.Quads * 12).ToArray(), a.Vertices.AsSpan(0, a.Quads * 12).ToArray());
            Assert.Equal(e.Texture.AsSpan(0, e.Quads).ToArray(), a.Texture.AsSpan(0, a.Quads).ToArray());
        }
    }

    [Fact]
    public void SkidDelta_StreamRebuildsTheRingExactlyIncludingWrapAround()
    {
        var host = new SkidBuffer();
        var mirror = new SkidBuffer();
        SkidBuffer? sent = null;
        var rng = new Random(3);
        int maxBytes = 0;
        for (int step = 0; step < 400; step++)
        {
            for (int k = rng.Next(0, 4); k > 0; k--)
            {
                int x = rng.Next(-0x3000000, 0x3000000), z = rng.Next(-0x400000, 0x400000);
                host.Add(new Vec3(x, 0, z), new Vec3(x + 0x8000, 0, z + 0x10000), rng.Next(1, 3));
            }
            var snapshot = host.Clone();
            if (NetSkidDelta.Diff(sent, snapshot) is { } delta)
            {
                var w = new WireWriter(NetProtocol.MaxReliableMessage);
                delta.Write(w);
                maxBytes = Math.Max(maxBytes, w.Length);
                var r = new WireReader(w.Written);
                NetSkidDelta.Read(ref r).Apply(mirror);
            }
            sent = snapshot;
            AssertSameSkids(host, mirror);
        }
        Assert.True(host.Added > 32 * 16, "ring wrapped");
        Assert.True(maxBytes < NetProtocol.MaxDatagram, $"per-step delta {maxBytes} B");
        AssertSameSkids(Marks(700, 1), ApplyAll(Marks(700, 1)));
    }

    static SkidBuffer ApplyAll(SkidBuffer b)
    {
        var mirror = new SkidBuffer();
        NetSkidDelta.Diff(null, b)!.Apply(mirror);
        return mirror;
    }
}
