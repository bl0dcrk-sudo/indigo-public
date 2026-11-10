using NFS.Core;
using NFS.Data.FrontEnd;

namespace NFS.Game.Race;

/// <summary>
/// Player inputs from replay.rpy (5.1), one sample per 32 Hz sim tick, decoded as follows [ref:2340]
/// (called from the player driver task through <c>car+0x370</c>): steer = (s8)(b&lt;&lt;2), gas/brake = b&lt;&lt;3,
/// buttons unchanged. Quantisation is the same as live (<see cref="PlayerDriver.Apply"/>: gas &amp;~7, steer /4*4).
/// <para>Delay: the recorder stores <i>after</i>, i.e. inputs that already [ref:2341]
/// went through the simqueue (4-tick prefill, <see cref="InputQueue"/>). Playback reads the file directly in the driver
/// task (the input thread only bumps ticks-available, <see cref="InputQueue.Prefill"/> with replayPlayback), so sample
/// <c>i</c> is applied at 32 Hz sim tick <c>i</c> with no extra delay – the 4-tick delay is baked into the recording.</para>
/// Packets of the human players are interleaved in fill order (player p = packet index mod number of humans).
/// </summary>
public sealed class ReplayInputSource
{
    private readonly PlayerInput[][] _samples;

    public Replay Replay { get; }
    public int NumPlayers => _samples.Length;
    /// <summary>Race length from the header (64 Hz). [ref:2342]</summary>
    public int RaceTicks64 => Replay.RaceTicks;
    /// <summary>Race length in 32 Hz sim ticks (= input samples needed).</summary>
    public int RaceTicks32 => Replay.RaceTicks / 2;

    public ReplayInputSource(Replay replay)
    {
        Replay = replay;
        int n = Math.Max(1, replay.Config.NumPlayerCars);
        var lists = Enumerable.Range(0, n).Select(_ => new List<PlayerInput>()).ToArray();
        for (int k = 0; k < replay.Packets.Count; k++)
        {
            var p = replay.Packets[k];
            int cnt = new[] { p.Steer.Length, p.Gas.Length, p.Brake.Length, p.Buttons.Length }.Min();
            for (int i = 0; i < cnt; i++)
                lists[k % n].Add(new PlayerInput(p.SteerAt(i), (byte)p.GasAt(i), (byte)p.BrakeAt(i), p.Buttons[i]));
        }
        _samples = lists.Select(l => l.ToArray()).ToArray();
    }

    /// <summary>Number of recorded samples (32 Hz ticks) of a player.</summary>
    public int Length(int player = 0) => _samples[player].Length;

    /// <summary>Input of sim tick <paramref name="tick32"/> (32 Hz); past the end = no input (state 4 restarts). [ref:2343]</summary>
    public PlayerInput At(int tick32, int player = 0)
    {
        var s = _samples[player];
        return (uint)tick32 < (uint)s.Length ? s[tick32] : default;
    }

    /// <summary>Input for the runner's current race tick (64 Hz). [ref:2344]</summary>
    public PlayerInput ForRaceTick(int raceTick64, int player = 0) => At(raceTick64 >> 1, player);
}
