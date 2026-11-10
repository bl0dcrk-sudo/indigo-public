using NFS.Core;
using NFS.Net.Protocol;

namespace NFS.Net.Server;

/// <summary>
/// Server-side input queue of one seat. Samples are keyed by their target host step and accepted only inside the window
/// [next step, next step + <see cref="NetProtocol.MaxFutureSteps"/>); older targets are "late", farther ones "future", already
/// applied sequences "stale". Each step takes exactly the sample of that step; when it is missing the last continuous controls are
/// held for <see cref="NetProtocol.HoldSteps"/> steps, then the seat drives neutral (no endless gas after a lost release).
/// One-shot actions (gear, reset) carry their own ids: each id is applied at most once, at most one action per step, never by
/// holding, and dropped when not applicable within <see cref="NetProtocol.ActionExpirySteps"/>.
/// </summary>
public sealed class SeatInputScheduler
{
    public const int MaxPendingActions = 16;

    readonly SortedDictionary<long, NetInputSample> _samples = [];
    readonly SortedDictionary<long, NetAction> _actions = [];
    NetInputSample? _last;
    NetInputSample? _realtimeLatest;
    int _realtimeHold;
    int _hold;

    /// <summary>InputSequence of the newest applied sample (the ACK published in snapshots).</summary>
    public long AppliedSequence { get; private set; }
    /// <summary>Id of the newest applied or expired action (the action ACK).</summary>
    public long LastActionId { get; private set; }
    public long Late { get; private set; }
    public long Future { get; private set; }
    public long Stale { get; private set; }
    public long Duplicates { get; private set; }
    public long HeldSteps { get; private set; }
    public long NeutralSteps { get; private set; }
    public long ActionsApplied { get; private set; }
    public long ActionsExpired { get; private set; }
    public long ActionsDropped { get; private set; }
    public int Buffered => _samples.Count;
    public int PendingActions => _actions.Count;

    /// <summary>Queues the samples/actions of one packet; <paramref name="nextStep"/> = the host step that runs next.</summary>
    public void Accept(NetInputPacket packet, long nextStep)
    {
        foreach (var s in packet.Samples)
        {
            if (s.TargetStep == 0)
            {
                // Latest-wins mailbox for real-time desktop controls. Sequence, not arrival order,
                // decides which datagram wins, so delayed/reordered packets cannot roll controls back.
                if (_realtimeLatest is { } latest && s.Sequence <= latest.Sequence) { Duplicates++; continue; }
                if (s.Sequence <= AppliedSequence) { Stale++; continue; }
                _realtimeLatest = s;
                _realtimeHold = NetProtocol.HoldSteps;
                continue;
            }
            if (s.Sequence <= AppliedSequence) { Stale++; continue; }
            if (s.TargetStep < nextStep) { Late++; continue; }
            if (s.TargetStep >= nextStep + NetProtocol.MaxFutureSteps) { Future++; continue; }
            if (_samples.TryGetValue(s.TargetStep, out var have))
            {
                if (s.Sequence <= have.Sequence) { Duplicates++; continue; }
            }
            _samples[s.TargetStep] = s;
        }
        foreach (var a in packet.Actions)
        {
            if (a.Id <= LastActionId || _actions.ContainsKey(a.Id)) { Duplicates++; continue; }
            if (a.TargetStep != 0 && a.TargetStep >= nextStep + NetProtocol.MaxFutureSteps) { Future++; continue; }
            if (_actions.Count >= MaxPendingActions) { ActionsDropped++; continue; }
            _actions[a.Id] = a.TargetStep == 0 ? a with { TargetStep = nextStep } : a;
        }
    }

    /// <summary>Input of host step <paramref name="step"/> and the sequence it applies (0 = no new sample).</summary>
    public (PlayerInput Input, long Sequence) Next(long step)
    {
        while (_samples.Count > 0 && _samples.First().Key < step) { _samples.Remove(_samples.First().Key); Late++; }
        long sequence = 0;
        PlayerInput input = default;
        if (_realtimeLatest is { } realtime && realtime.Sequence > AppliedSequence)
        {
            _last = realtime;
            _hold = 0;
            AppliedSequence = sequence = realtime.Sequence;
            input = realtime.ToInput();
        }
        else if (_realtimeLatest is not null && _realtimeHold > 0)
        {
            _realtimeHold--;
            HeldSteps++;
            input = _realtimeLatest.Value.ToInput();
        }
        else if (_samples.Remove(step, out var candidate) && candidate.Sequence > AppliedSequence)
        {
            var s = candidate;
            _last = s;
            _hold = NetProtocol.HoldSteps;
            AppliedSequence = sequence = s.Sequence;
            input = s.ToInput();
        }
        else if (_last is { } last && _hold > 0)
        {
            _hold--;
            HeldSteps++;
            input = last.ToInput();
        }
        else NeutralSteps++;

        byte action = 0;
        while (_actions.Count > 0)
        {
            var a = _actions.First().Value;
            if (a.TargetStep != 0 && a.TargetStep > step) break;
            _actions.Remove(a.Id);
            LastActionId = a.Id;
            if (step - a.TargetStep > NetProtocol.ActionExpirySteps) { ActionsExpired++; continue; }
            action = a.Button;
            ActionsApplied++;
            break;
        }
        return (action == 0 ? input : input with { Buttons = (byte)(input.Buttons | action) }, sequence);
    }

    /// <summary>The seat left: forget everything, drive neutral.</summary>
    public void Clear()
    {
        _samples.Clear();
        _actions.Clear();
        _last = null;
        _realtimeLatest = null;
        _realtimeHold = 0;
        _hold = 0;
    }

    /// <summary>
    /// A new client took over the seat (reconnect): besides <see cref="Clear"/> forget the applied sequence and action id,
    /// because the new client numbers its samples and actions from 1 again (otherwise all of them would be "stale").
    /// </summary>
    public void Restart()
    {
        Clear();
        AppliedSequence = 0;
        LastActionId = 0;
    }
}
