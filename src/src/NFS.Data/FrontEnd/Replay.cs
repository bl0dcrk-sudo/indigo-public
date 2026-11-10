namespace NFS.Data.FrontEnd;

/// <summary>Race configuration block (0x5B0 B) = header of replay.rpy. [ref:1025]
/// The comments give the original tag numbers. [ref:1026]</summary>
public sealed class RaceConfig
{
    public const int Size = 0x5B0;
    public const int CarRecordSize = 0x40;
    public byte[] Raw { get; }

    public RaceConfig(byte[] raw)
    {
        Bin.Require(raw.Length >= Size, "race config shorter than 0x5B0");
        Raw = raw[..Size];
    }

    private int I(int o) => Bin.S32(Raw, o);
    public int RaceType => I(0x00);        // 2 RACETYPE
    public int NumLaps => I(0x04);         // 3
    public int Skill => I(0x08);           // 4
    public int Style => I(0x0C);           // 5 [ref:1027]
    public int CommMode => I(0x10);        // 6
    public int LocalCar => I(0x14);        // 8
    public int CatchUpLogic => I(0x18);    // 9
    public int ReplayMode => I(0x1C);      // (2 = replay) [ref:1028]
    public int Sgge => I(0x20);            // 0x14 cheat flags
    public int Track => I(0x24);           // 0xA (FE mapping)
    public int Time => I(0x28);            // 0xB unused
    public int Random => I(0x2C);          // 0xC [ref:1029]
    public int Backward => I(0x30);        // 0x1F
    public int Mirrored => I(0x34);        // 0x20
    public int WildArcade => I(0x38);      // 0x21
    public int Language => I(0x3C);       // 0x13
    public int Units => I(0x40);           // 0x1E
    public int BestLap => I(0x44);         // 0x1D
    public int NumCars => I(0x68);         // 0xD
    public int NumPlayerCars => I(0x6C);   // 0x10
    public int NumOpponentCars => I(0x70); // 0x11
    public int OpponentCarType => I(0x74); // 0x12
    public int NumSmackables => I(0x4F8);  // 0xF

    /// <summary>[16] (0x40 B, at offset 0x78): +0 carType, +4 carClass (1 human, 2 AI, 4 traffic, 8 cop) [ref:1030]
    /// +8 transmission, +0xC colour, +0x10..+0x18 ramps, +0x1C/+0x20 downforce, +0x24 steering, +0x28 brake bias,
    /// +0x2C gear ratio, +0x30 AI personality, +0x34 name.</summary>
    public RaceCar Car(int i)
    {
        int o = 0x78 + CarRecordSize * i;
        return new RaceCar(I(o), I(o + 4), I(o + 8), I(o + 0xC), Bin.CString(Raw, o + 0x34, 12), Raw.AsSpan(o, CarRecordSize).ToArray());
    }

    /// <summary>0x22 LINEUP (at offset 0x478): [car index]. [ref:1031]</summary>
    public int GridSlot(int car) => I(0x478 + 4 * car);
}

public sealed record RaceCar(int CarType, int CarClass, int Transmission, int Colour, string Name, byte[] Raw);

/// <summary>One 32-tick (32 Hz) input packet of one player: 4 RLE sub-packets steering, gas, brake, buttons.</summary>
public sealed record ReplayPacket(int Offset, byte[] Steer, byte[] Gas, byte[] Brake, byte[] Buttons)
{
    public const int Ticks = 32;
    /// <summary>Decoded inputs as: steer (s8)(b&lt;&lt;2), gas = b&lt;&lt;3, brake = b&lt;&lt;3. [ref:1032]</summary>
    public sbyte SteerAt(int i) => (sbyte)(Steer[i] << 2);
    public int GasAt(int i) => Gas[i] << 3;
    public int BrakeAt(int i) => Brake[i] << 3;
}

/// <summary>replay.rpy: 0x000 race config (0x5B0 B), 0x5B0 u32 race length in 64 Hz ticks, 0x5B4 packet stream
/// (0x1E000 B; zero byte = end). Sub-packet: u8 len (including itself), then RLE data: <c>0xFF n v</c> = n × v,
/// other byte = literal; each decodes to 32 samples. Packets of players are interleaved in fill order.</summary>
public sealed class Replay
{
    public const int StreamOffset = 0x5B4;
    public RaceConfig Config { get; }
    public int RaceTicks { get; }
    public IReadOnlyList<ReplayPacket> Packets { get; }
    public int StreamEnd { get; }
    public byte[] RawData { get; }

    private Replay(byte[] d)
    {
        Bin.Require(d.Length >= StreamOffset, "replay: truncated header");
        RawData = d.ToArray();
        Config = new RaceConfig(d);
        RaceTicks = Bin.S32(d, RaceConfig.Size);
        Bin.Require(RaceTicks > 0 && RaceTicks <= 10_000_000,
            $"replay: invalid race tick count {RaceTicks}");
        int players = Config.NumPlayerCars;
        Bin.Require(players is >= 1 and <= 2, $"replay: invalid player count {players}");
        Bin.Require(Config.NumCars is >= 1 and <= RaceSetupMaxCars && Config.Track is >= 0 and <= 12,
            "replay: invalid race header geometry");
        var list = new List<ReplayPacket>();
        int p = StreamOffset;
        while (p < d.Length && d[p] != 0)
        {
            int start = p;
            var subs = new byte[4][];
            for (int s = 0; s < 4; s++)
            {
                Bin.Require(p < d.Length, $"replay: truncated packet at 0x{p:X}");
                int len = d[p];
                Bin.Require(len >= 1 && p + len <= d.Length, $"replay: bad sub-packet length at 0x{p:X}");
                subs[s] = DecodeRle(d.AsSpan(p + 1, len - 1), p);
                Bin.Require(subs[s].Length == ReplayPacket.Ticks, $"replay: sub-packet has {subs[s].Length} samples at 0x{p:X}");
                p += len;
            }
            list.Add(new ReplayPacket(start, subs[0], subs[1], subs[2], subs[3]));
        }
        Bin.Require(p < d.Length && d[p] == 0, "replay: missing end marker");
        Bin.Require(list.Count % players == 0, "replay: incomplete interleaved player packet group");
        int maxPackets = ((RaceTicks / 2 + ReplayPacket.Ticks - 1) / ReplayPacket.Ticks) * players;
        Bin.Require(list.Count <= maxPackets, "replay: packet stream exceeds the race length");
        StreamEnd = p;
        Packets = list;
    }

    private const int RaceSetupMaxCars = 16;

    public static Replay Parse(byte[] data) => new(data);
    public static Replay Load(string relative = "REPLAY.RPY", string? gameDir = null) => new(GameFiles.ReadRaw(relative, gameDir));

    public static byte[] DecodeRle(ReadOnlySpan<byte> src, int where = 0)
    {
        var o = new List<byte>(ReplayPacket.Ticks);
        for (int i = 0; i < src.Length;)
        {
            if (src[i] == 0xFF)
            {
                Bin.Require(i + 2 < src.Length, $"replay: truncated RLE run at 0x{where:X}");
                for (int k = 0; k < src[i + 1]; k++) o.Add(src[i + 2]);
                Bin.Require(o.Count <= ReplayPacket.Ticks, $"replay: RLE sub-packet exceeds 32 samples at 0x{where:X}");
                i += 3;
            }
            else { o.Add(src[i++]); Bin.Require(o.Count <= ReplayPacket.Ticks, $"replay: sub-packet exceeds 32 samples at 0x{where:X}"); }
        }
        return o.ToArray();
    }
}
