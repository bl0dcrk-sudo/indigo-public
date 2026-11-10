using System.Buffers.Binary;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.Mods;

namespace NFS.Game.Race;

/// <summary>Builds the original replay.rpy packet stream from the player inputs consumed by RaceRunner.
/// Desktop simulation has no separate simqueue; inputs supplied to <see cref="Record"/> are therefore the
/// post-queue values actually consumed by the simulation (the replay equivalent of the simqueue). [ref:2348]</summary>
public sealed class ReplayRecorder
{
    private readonly RaceSetup _setup;
    private readonly int _initialRaceTick;
    private readonly List<PlayerInput>[] _inputs;

    private readonly CarCatalog? _catalog;
    private readonly IReadOnlyList<LoadedMod>? _mods;

    /// <param name="catalog">Needed only to name mod cars: a race with a mod car (<see cref="GameDataMode.Modded"/>) gets the mods header
    /// (<see cref="ModsReplayHeader"/>) behind the stream; without mod cars the bytes are exactly the original format.</param>
    public ReplayRecorder(RaceSetup setup, int initialRaceTick = 0, CarCatalog? catalog = null, IReadOnlyList<LoadedMod>? mods = null)
    {
        _catalog = catalog; _mods = mods;
        _initialRaceTick = initialRaceTick;
        _setup = new RaceSetup();
        setup.Block.CopyTo(_setup.Block, 0);
        setup.KeyBindings.CopyTo(_setup.KeyBindings, 0);
        _setup.Set(0x1C, 2); // writes replay mode 2 [ref:2349]
        _inputs = Enumerable.Range(0, Math.Clamp(_setup.NumPlayerCars, 1, 2)).Select(_ => new List<PlayerInput>()).ToArray();
    }

    public int Players => _inputs.Length;
    public int Samples => _inputs[0].Count;

    public void Record(PlayerInput player1, PlayerInput? player2 = null)
    {
        _inputs[0].Add(player1);
        if (_inputs.Length > 1) _inputs[1].Add(player2 ?? default);
    }

    public Replay Build(int raceTicks64)
    {
        if (_initialRaceTick != 0)
            throw new InvalidOperationException("Replay recording must begin at race tick 0; non-countdown starts cannot be exported.");
        int samples = Math.Min(Samples, Math.Max(0, raceTicks64 / 2));
        int packets = (samples + ReplayPacket.Ticks - 1) / ReplayPacket.Ticks;
        var bytes = new List<byte>(Replay.StreamOffset + packets * Players * 24 + 1);
        bytes.AddRange(_setup.Block);
        Span<byte> ticks = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(ticks, raceTicks64);
        bytes.AddRange(ticks.ToArray());
        for (int p = 0; p < packets; p++)
            for (int player = 0; player < Players; player++)
            {
                var steer = new byte[ReplayPacket.Ticks];
                var gas = new byte[ReplayPacket.Ticks];
                var brake = new byte[ReplayPacket.Ticks];
                var buttons = new byte[ReplayPacket.Ticks];
                for (int i = 0; i < ReplayPacket.Ticks; i++)
                {
                    int index = p * ReplayPacket.Ticks + i;
                    var input = index < samples ? _inputs[player][index] : default;
                    steer[i] = unchecked((byte)(input.Steer / 4 + 0x40));
                    gas[i] = (byte)(input.Gas >> 3);
                    brake[i] = (byte)(input.Brake >> 3);
                    buttons[i] = input.Buttons;
                }
                AddSubPacket(bytes, steer); AddSubPacket(bytes, gas); AddSubPacket(bytes, brake); AddSubPacket(bytes, buttons);
            }
        bytes.Add(0);
        if (_catalog != null
            && ModsReplayHeader.For(_catalog, _setup.Cars.Select(c => c.CarType), _mods ?? ModSession.Current.Mods) is { } header)
            bytes.AddRange(header.ToTrailer());
        return Replay.Parse(bytes.ToArray());
    }

    private static void AddSubPacket(List<byte> output, byte[] samples)
    {
        var encoded = new List<byte>(samples.Length);
        for (int i = 0; i < samples.Length;)
        {
            int run = 1;
            while (i + run < samples.Length && samples[i + run] == samples[i] && run < 255) run++;
            if (run >= 3 || samples[i] == 0xFF)
            {
                encoded.Add(0xFF); encoded.Add((byte)run); encoded.Add(samples[i]); i += run;
            }
            else
            {
                for (int j = 0; j < run; j++) encoded.Add(samples[i++]);
            }
        }
        output.Add(checked((byte)(encoded.Count + 1)));
        output.AddRange(encoded);
    }
}
