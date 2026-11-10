using NFS.ModApi;
using NFS.Audio;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Data;
using NFS.Data.Track;
using NFS.Game.Cars;
using NFS.Game.Session;

namespace NFS.Game.Race;

/// <summary>A read-only race observer. No sound device, no writes to CarObj or the race RNGs.
/// Original API controls are exposed independently of the port's software mixer.
/// Stage 5a.2: driven by published <see cref="RaceViewFrame"/>s and <see cref="RaceEvent"/>s (<see cref="Present"/>), one sound frame
/// per Step frame (runs once per 32 Hz step). The <see cref="RaceAudio(RaceRunner, string?)"/> constructor [ref:2069]
/// is a bare-runner adapter for headless: it publishes its own frames at the runner's sound frame. [ref:3429]</summary>
public sealed class RaceAudio : IDisposable
{
    public sealed record SoundVoice(int Channel, string Bank, int Sample, int Pitch, int Volume, int Pan);
    private readonly RaceSession _session;
    private readonly RaceSessionDescription _desc;
    private readonly RaceRunner? _legacyRace;
    private readonly RaceViewPublisher? _legacyPublisher;
    private RaceViewFrame? _frame;
    private readonly string _dir;
    private readonly CarCatalog _catalog;   // per-car traits (engine banks), cached per game dir by RaceLoader
    private readonly byte[] _curve;
    private readonly float[] _pitchFactors;
    private readonly Dictionary<string, BnkBank> _banks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int), Sound> _sounds = [];
    private readonly Dictionary<int, (SoundVoice Spec, Voice Voice)> _channels = [];
    private readonly Dictionary<int, int> _distances = [];   // by CarId
    private readonly List<(int Car, int Kind, int Material, int Strength, Vec3 Position)> _impacts = [];
    private readonly EaRandom _soundRandom = new(); // isolated render/sound stream [ref:3430]
    private int _throttle, _throttle2, _frames, _selectionTick = -8, _lastImpactSample = -1;   // _throttle2:[1] (second human) [ref:2070]
    private int[] _nearby = [];   // CarIds
    private int? _hornCar;
    private int _hornTime, _hornLength, _hornGap, _hornCount;
    private bool _disposed;
    private readonly TrackAmbience? _ambience;   // ambient sounds (XBID 17); null = the track has no sources / no TRAMBnn bank [ref:2071]
    private string? _ambienceBank;
    private int _ambienceFrame;
    /// <summary>Track ambient sounds (XBID 17); switch off to hear the bare race mix.</summary>
    public bool Ambience { get; set; } = true;
    // Port mixer output headroom: EA's envelope/mixer gain curves are not yet emulated.
    public Mixer Mixer { get; } = new() { OutputGain = 0.25f };
    public int MasterVolume { get; set; } = 127;
    public event Action<short[]>? BufferReady;
    public event Action<SoundVoice>? OneShot;
    public IReadOnlyList<SoundVoice> Voices => _channels.Values.Where(x => x.Voice.Active).Select(x => x.Spec).ToArray();
    /// <summary>Listener right/forward are the original camera view axes, in 16.16.</summary>
    public Func<(Vec3 Position, Vec3 Right, Vec3 Forward)>? Listener { get; set; }
    /// <summary>Split screen: listener of the second human (<c>(1)</c>); without it the car of player 2. [ref:2072]</summary>
    public Func<(Vec3 Position, Vec3 Right, Vec3 Forward)>? Listener2 { get; set; }
    /// <summary>Split screen (<c>1</c>, two humans): the session has a second local seat. [ref:2073]</summary>
    public bool IsSplit => _desc.SecondPlayerCarId != null;

    /// <summary>Bare-runner adapter (headless tools, unit tests): frames are published at the runner's sound frame.</summary>
    public RaceAudio(RaceRunner race, string? gameDir = null) : this(race.Session, new RaceViewPublisher(race, skids: false), gameDir)
    {
        _legacyRace = race;
        _legacyPublisher!.Published += Present;
        race.OnSoundFrame += LegacySoundFrame;
    }

    private RaceAudio(RaceSession session, RaceViewPublisher publisher, string? gameDir) : this(session, publisher.Description, gameDir)
        => _legacyPublisher = publisher;

    private void LegacySoundFrame() => _legacyPublisher!.Publish(RaceViewFrameKind.Step, 0);

    public RaceAudio(RaceSession session, RaceSessionDescription description, string? gameDir = null)
    {
        _session = session;
        _desc = description;
        _dir = GameFiles.FindGameDir(gameDir);
        _catalog = session.Catalog ?? new RaceLoader(_dir).Catalog;   // the catalog the race was loaded with (mod cars of a custom catalog included)
        if (GameFiles.Exists(NFS.Data.FrontEnd.FeConfig.FilePath, _dir))
            MasterVolume = Math.Min((int)NFS.Data.FrontEnd.FeConfig.Load(_dir)[0x5E], 127); // [ref:2074]
        _curve = AudioConstants.Curve.ToArray();
        _pitchFactors = AudioConstants.PitchFactors.ToArray();
        // Load required banks up front, so malformed/missing assets fail before the PCM device starts.
        if (IsSplit) { Bank(SplitBank(CarType(_desc.PlayerCarId))); Bank(SplitBank(CarType(_desc.SecondPlayerCarId!.Value))); Bank("GEN"); Bank(CountdownBank()); }
        else { Bank(PlayerBank()); Bank("GEN"); Bank(CountdownBank()); Bank(OpponentBank()); }
        string ambient = $"TRAMB{_session.TrackNumber:00}";   // "%stramb%02d" format (= bank 5) [ref:2075]
        var sources = AmbientSource.ForTrack(_session.Trk);
        if (sources.Any(x => x != null) && GameFiles.Exists($"GAMEDATA/AUDIO/SFX/PC/{ambient}.BNK", _dir))
        {
            _ambienceBank = ambient; Bank(ambient);
            _ambience = new TrackAmbience(sources, AmbientSource.NeighborsForTrack(_session.Trk), _soundRandom.Next);
        }
    }

    /// <summary>block of the listener = block of the nearest slice centre (locates the camera on the track). [ref:2076]</summary>
    private int ListenerBlock(Vec3 pos)
    {
        var slices = _session.Slices;
        long best = long.MaxValue; int block = 0;
        foreach (var s in slices)
        {
            long dx = (s.X - pos.X) >> 8, dy = (s.Y - pos.Y) >> 8, dz = (s.Z - pos.Z) >> 8, d = dx * dx + dy * dy + dz * dz;
            if (d < best) { best = d; block = s.BlockNumber; }
        }
        return block;
    }

    private void AmbientFrame((Vec3 Position, Vec3 Right, Vec3 Forward) listener)
    {
        if (_ambience == null || !Ambience) { Stop(14); return; }
        var f = _ambience.Update(ListenerBlock(listener.Position), (listener.Position.X, listener.Position.Y, listener.Position.Z),
            (listener.Right.X, listener.Right.Y, listener.Right.Z), _ambienceFrame++);
        if (f.Loop is { } l) Set(14, _ambienceBank!, l.Sample, l.Pitch, l.Volume * MasterVolume / 127 / 2, l.Pan);   // voice 0xE
        else Stop(14);
        if (f.Shot is { } s) Set(15, _ambienceBank!, s.Sample, s.Pitch, s.Volume * MasterVolume / 127 / 2, s.Pan, shot: true);   // voice 0xF
    }

    private int CarType(int carId) => _session.Cars[carId].Setup.CarType;
    private CarView View(int carId) => _frame!.Cars[carId].State;
    private int Tick => _frame!.SimulationTick64;

    /// <summary>Consumes one published frame with its events (in EventId order): countdown beeps, AI horn requests and impacts of the
    /// step, then - for a Step frame - the sound frame of that step.</summary>
    public void Present(RaceViewFrame frame, IReadOnlyList<RaceEvent> events)
    {
        if (_disposed) return;
        _frame = frame;
        foreach (var e in events)
        {
            switch (e)
            {
                case CountdownBeepEvent c: Countdown(c.Sample); break;
                case HornRequestEvent h: RequestHorn(h.CarId, h.Count, h.Length, h.Gap); break;
                case CarImpactEvent i: CaptureImpact(i); break;
            }
        }
        if (frame.Kind == RaceViewFrameKind.Step) SoundFrame();
    }

    private string CountdownBank() => _session.Setup.Get(0x3C) switch
    { 1 => "CNTGER", 2 => "CNTFRN", 3 => "CNTSPN", 4 => "CNTITL", _ => "CNTENG" }; // [ref:2077]
    private CarTraits TraitsOf(int model) => _catalog.TryGetByNumber(model, out var d) ? d.Traits : throw new InvalidOperationException($"car {model} not in catalog");
    /// <summary>player engine bank (cheat cars use the generic engine banks), from <see cref="EngineBank"/> of the car. [ref:2078]</summary>
    private string PlayerBank() => TraitsOf(CarType(_desc.PlayerCarId)).EngineBank.Player;
    /// <summary>In split screen (<c>1</c>): bank 0 = engine of human 1, bank 1 = engine of human 2 (the "s" tables). [ref:2079]</summary>
    private string SplitBank(int model) => TraitsOf(model).EngineBank.Split;
    /// <summary>loads ONE opponent bank, from the other human's config (offline default = model 0; unknown model = 0). [ref:2080]</summary>
    private string OpponentBank()
    {
        int model = _session.Setup.Car(_session.Cars[_desc.PlayerCarId].Index == 0 ? 1 : 0).CarType;
        if (!_catalog.TryGetByNumber(model, out _)) model = 0;
        return TraitsOf(model).EngineBank.Opponent;
    }
    private BnkBank Bank(string name)
    {
        if (!_banks.TryGetValue(name, out var bank))
            _banks[name] = bank = BnkBank.Parse(GameFiles.ReadRaw($"GAMEDATA/AUDIO/SFX/PC/{name}.BNK", _dir));
        return bank;
    }
    private Sound? Sample(string bankName, int slot)
    {
        var bank = Bank(bankName);
        if ((uint)slot >= (uint)bank.Slots.Count || bank.Slots[slot] is not { } sample) return null;
        if (!_sounds.TryGetValue((bankName, slot), out var sound))
            _sounds[(bankName, slot)] = sound = Sound.FromBank(bank, sample);
        return sound;
    }
    private void Set(int channel, string bank, int sample, int pitch, int volume, int pan, bool shot = false, bool mirror = true)
    {
        pitch = Math.Clamp(pitch, 0, 127); volume = Math.Clamp(volume, 0, 127); pan = Math.Clamp(pan, 0, 127);
        if (mirror && _session.Setup.Mirrored) pan = 127 - pan; // (the engine loops set the pan directly, without mirroring) [ref:2081]
        var sound = Sample(bank, sample);
        if (sound == null) { Stop(channel); return; } // BNK empty slots are intentionally silent.
        var header = Bank(bank).Slots[sample]!.Header;
        double rate = RaceSoundMath.PitchMultiplier(RaceSoundMath.PitchCents(header, pitch), _pitchFactors);
        // PT tag 8 is sample volume. Tag 0x91 selects resampler routines, NOT gain. [ref:2082]
        int pcmVolume = volume * Math.Clamp(header.Get(8, 127), 0, 127) / 127;
        var spec = new SoundVoice(channel, bank, sample, pitch, volume, pan);
        if (shot)
        {
            Mixer.Play(sound, pcmVolume, pan, rate);
            OneShot?.Invoke(spec);
        }
        else
        {
            if (!_channels.TryGetValue(channel, out var old) || !old.Voice.Active || old.Spec.Bank != bank || old.Spec.Sample != sample)
            {
                Stop(channel);
                _channels[channel] = (spec, Mixer.Play(sound, pcmVolume, pan, rate));
            }
            else
            {
                old.Voice.Volume = pcmVolume; old.Voice.Pan = pan; old.Voice.Pitch = rate;
                _channels[channel] = (spec, old.Voice);
            }
        }
    }
    private void Stop(int channel)
    {
        if (_channels.Remove(channel, out var v)) v.Voice.Stop();
    }
    private void Countdown(int sample) => Set(-1, CountdownBank(), sample, 64, MasterVolume, 64, shot: true); // [ref:2083]

    private void CaptureImpact(CarImpactEvent e)
    {
        // the host queued it before Impact/ImpactType were cleared, only while shake was idle. [ref:2084]
        int type = (e.ImpactType & unchecked((int)0xEFFF0000)) >> 16;
        int material = e.ImpactType & 255;
        if (type == 4) material = AudioConstants.MaterialByWallFlags[material];
        else if (type <= 3) material = AudioConstants.MaterialBySurface[material];
        int kind = AudioConstants.ImpactKind[type];
        if (type == 3 && e.UpDotN < 0xE666) kind = 1;
        _impacts.Add((e.CarId, kind, material, e.Impact / 8, e.Position));
    }

    private (int Distance, int Squared, int Pan, int Doppler) Spatial(int carId, (Vec3 Position, Vec3 Right, Vec3 Forward) listener)
    {
        var d = View(carId).Pos - listener.Position;
        int distance = RaceSoundMath.Distance(d.X, d.Y, d.Z);
        int pan = distance == 0 ? 64 : Math.Clamp(Fx.Div(Vec3.Dot(d, listener.Right), distance), -65536, 65536) / 1024 + 64;
        int doppler = RaceSoundMath.Doppler(distance, _distances.TryGetValue(carId, out var previous) ? previous : null);
        _distances[carId] = distance;
        int m = distance / 65536;
        return (distance, m * m, pan, doppler);
    }

    private void SoundFrame()
    {
        if (IsSplit) { SplitSoundFrame(); MixOut(); return; }
        int player = _desc.PlayerCarId;
        var c = View(player);
        var listener = Listener?.Invoke() ?? (c.Pos, c.Orient.Row0, c.Orient.Row2);
        // two nearest non-rail, non-static cars <75 m, selection refreshed every 8 ticks. [ref:2085]
        if (Tick - _selectionTick >= 8)
        {
            _selectionTick = Tick;
            _nearby = _desc.SimulatedCars.Where(x => x != player && View(x).Rail == 0 && (View(x).Flags & CarFlags.Static) == 0)
                .OrderBy(x => { var d = View(x).Pos - listener.Item1; return RaceSoundMath.Distance(d.X, d.Y, d.Z); })
                .Where(x => { var d = View(x).Pos - listener.Item1; return RaceSoundMath.Distance(d.X, d.Y, d.Z) < 0x4B0000; })
                .Take(2).ToArray();
        }
        var p = Spatial(player, listener);
        int pitch = RaceSoundMath.RpmPitch(c.Rpm, _desc.Redline[player], p.Doppler);
        // TrackSlice +0x1E: either nibble == 2 means tunnel. [ref:2086]
        byte lanes = _session.Slices[c.Slice].Surroundings;
        bool tunnel = (lanes >> 4) == 2 || (lanes & 15) == 2;
        var levels = RaceSoundMath.PlayerEngine(pitch, c.ThrottleRamped, _throttle, c.LocalVel.Z,
            c.SurfaceType, tunnel, p.Squared, _curve, MasterVolume);
        _throttle = levels.Throttle;
        Set(0, PlayerBank(), 0, levels.Pitch, levels.OnLoad, p.Pan);
        Set(2, PlayerBank(), 1, levels.Pitch, levels.OffLoad, p.Pan);
        Set(10, "GEN", 0, levels.RoadPitch, levels.RoadVolume, p.Pan);
        Tyres(c, 6, 8, p.Squared, p.Pan);
        if (c.Horn != 0 && (byte)c.TurboReady == 0 && c.TurboCounter == 0)
            Set(12, PlayerBank(), 3, Math.Clamp(p.Doppler * 64 / 65536, 0, 127),
                RaceSoundMath.Attenuation(p.Squared) * MasterVolume / 127 / 2, p.Pan);
        else Stop(12);
        for (int i = 0; i < 2; i++)
        {
            int channel = 4 + i;
            if (i >= _nearby.Length) { Stop(channel); Stop(16 + i); continue; }
            int other = _nearby[i]; var s = Spatial(other, listener);
            int rp = RaceSoundMath.RpmPitch(View(other).Rpm, _desc.Redline[other], s.Doppler, nearby: true);
            Set(channel, OpponentBank(), 0, rp, RaceSoundMath.Attenuation(s.Squared) * MasterVolume / 127 / 2, s.Pan);
            Tyres(View(other), 16 + i, 16 + i, s.Squared, s.Pan);
        }
        UpdateAiHorn(listener);
        AmbientFrame(listener);
        foreach (var hit in _impacts)
        {
            if (hit.Car != player && !_nearby.Contains(hit.Car)) continue;
            var d = View(hit.Car).Pos - listener.Item1;
            int meters = RaceSoundMath.Distance(d.X, d.Y, d.Z) / 65536;
            int strength = hit.Car == player ? hit.Strength : hit.Strength >> 4; // [ref:2087]
            int sample = ImpactSample(hit.Kind, hit.Material, RaceSoundMath.StrengthPitch(strength));
            int volume = RaceSoundMath.Attenuation(meters * meters * 2) * MasterVolume / 127;
            // local one-shots pan at LastHit; nearby events at the car position. [ref:2088]
            var source = hit.Car == player ? hit.Position : View(hit.Car).Pos;
            var delta = source - listener.Item1;
            int distance = RaceSoundMath.Distance(delta.X, delta.Y, delta.Z);
            int pan = distance == 0 ? 64 : Math.Clamp(Fx.Div(Vec3.Dot(delta, listener.Item2), distance), -65536, 65536) / 1024 + 64;
            Set(20, "GEN", sample, 64, volume / 2, pan, shot: true); // halves non-speech banks [ref:2089]
            if (sample == 31) Set(20, "GEN", 35, 64, Math.Min(127, volume * RaceSoundMath.StrengthPitch(strength) / 127 * 2) / 2, pan, shot: true);
        }
        _impacts.Clear();
        MixOut();
    }

    private void MixOut()
    {
        // Exact sample count even when the output rate is not divisible by 32.
        int end = (int)((long)(_frames + 1) * Mixer.OutputRate / 32);
        int start = (int)((long)_frames++ * Mixer.OutputRate / 32);
        var pcm = Mixer.Mix(end - start);
        BufferReady?.Invoke(pcm);
    }

    /// <summary>
    /// In split screen (<c>1</c>): for each human p (0, 1) a listener <c>(p) [ref:2090]</c>
    /// for the car of that human only - (nearby opponents, channels 4/5) and the AI horn are not called, impacts of [ref:2091]
    /// other cars stay silent. The engine loops use the channel sets 0/2/10 (human 1) and 1/3/11 (human 2) with the banks of [ref:2092]
    /// their own cars (<see cref="SplitBank"/>); the pan of everything is fixed: human 1 = 0x7F (right, <c>[0] = 0x10000</c>) [ref:3431]
    /// human 2 = 0 (left, <c>0xFFFF0000</c>); tyres 6/8 and 7/9, horn 12 and 13, impacts at the distance of the listener [ref:2093]
    /// to its own car. Not ported (as for one player): ambient sounds and the commentary, and the wrong-way voice. [ref:2094]
    /// </summary>
    private void SplitSoundFrame()
    {
        var humans = new[] { _desc.PlayerCarId, _desc.SecondPlayerCarId!.Value };
        for (int p = 0; p < 2; p++)
        {
            int id = humans[p]; var c = View(id);
            var listener = (p == 0 ? Listener : Listener2)?.Invoke() ?? (c.Pos, c.Orient.Row0, c.Orient.Row2);
            int pan = SplitPan(p);
            var sp = Spatial(id, listener);
            int pitch = RaceSoundMath.RpmPitch(c.Rpm, _desc.Redline[id], sp.Doppler);
            byte lanes = _session.Slices[c.Slice].Surroundings;
            bool tunnel = (lanes >> 4) == 2 || (lanes & 15) == 2;
            var levels = RaceSoundMath.PlayerEngine(pitch, c.ThrottleRamped, p == 0 ? _throttle : _throttle2, c.LocalVel.Z,
                c.SurfaceType, tunnel, sp.Squared, _curve, MasterVolume);
            if (p == 0) _throttle = levels.Throttle; else _throttle2 = levels.Throttle;
            string bank = SplitBank(CarType(id));
            Set(p, bank, 0, levels.Pitch, levels.OnLoad, pan, mirror: false);
            Set(2 + p, bank, 1, levels.Pitch, levels.OffLoad, pan, mirror: false);
            Set(10 + p, "GEN", 0, levels.RoadPitch, levels.RoadVolume, pan, mirror: false);
            Tyres(c, 6 + p, 8 + p, sp.Squared, pan);
            if (c.Horn != 0 && (byte)c.TurboReady == 0 && c.TurboCounter == 0)
                Set(12 + p, bank, 3, Math.Clamp(sp.Doppler * 64 / 65536, 0, 127),
                    RaceSoundMath.Attenuation(sp.Squared) * MasterVolume / 127 / 2, pan);
            else Stop(12 + p);
        }
        foreach (int channel in new[] { 4, 5, 16, 17, 19 }) Stop(channel);   // no nearby opponents, no AI horn
        foreach (var hit in _impacts)
        {
            int p = hit.Car == humans[0] ? 0 : hit.Car == humans[1] ? 1 : -1;
            if (p < 0) continue;   // not called in split screen [ref:2095]
            var o = View(hit.Car);
            var listener = (p == 0 ? Listener : Listener2)?.Invoke() ?? (o.Pos, o.Orient.Row0, o.Orient.Row2);
            var d = o.Pos - listener.Position;
            int meters = RaceSoundMath.Distance(d.X, d.Y, d.Z) / 65536;
            int sample = ImpactSample(hit.Kind, hit.Material, RaceSoundMath.StrengthPitch(hit.Strength));
            int volume = RaceSoundMath.Attenuation(meters * meters * 2) * MasterVolume / 127;
            Set(20, "GEN", sample, 64, volume / 2, SplitPan(p), shot: true);
            if (sample == 31) Set(20, "GEN", 35, 64, Math.Min(127, volume * RaceSoundMath.StrengthPitch(hit.Strength) / 127 * 2) / 2, SplitPan(p), shot: true);
        }
        _impacts.Clear();
    }

    /// <summary>Fixed pan of a human in split screen: human 1 right (0x7F), human 2 left (0). [ref:2096]</summary>
    public static int SplitPan(int human) => human == 0 ? 127 : 0;

    private readonly Dictionary<int, (int Sample, int Pitch)> _tyrePitch = [];
    private void Tyres(CarView c, int front, int rear, int squared, int pan)
    {
        int a = 0, b = 0;
        int material = AudioConstants.MaterialBySurface[c.SurfaceType];
        int kind = 2;
        // sound-only projection of the event queue. [ref:2097]
        if (c.UpDotN < 0x3333)
        {
            kind = 1;
            if (c.HeightAboveRoad < 0x3333 && c.SpeedApprox > 0x20000) b = Math.Max(c.Skid328, 0xA0000);
        }
        else if (c.DistToPlayer < 0x3C0000 && c.HeightAboveRoad < 0x6666 && c.CrashSlice == 0)
        {
            a = Math.Max(0, c.Skid324 - 0x4CCC);
            b = Math.Max(0, c.Skid328 / 4 - 0x4CCC);
            if (c.SurfaceType is 13 or 5 && c.SpeedApprox > 0x2C9999) b = 65536;
        }
        Loop(front, 2, a); if (rear != front) Loop(rear, kind, b); else if (a == 0) Loop(rear, kind, b);
        void Loop(int channel, int eventKind, int strength)
        {
            if (channel >= 16) strength >>= 4; // nearby effects [ref:2098]
            if (strength <= 0) { Stop(channel); _tyrePitch.Remove(channel); return; }
            int sample = RaceSoundMath.TyreSample(eventKind, material);
            int pitch = RaceSoundMath.StrengthPitch(strength);
            if (channel is not (6 or 7) && _tyrePitch.TryGetValue(channel, out var old) && old.Sample == sample) pitch = old.Pitch;
            _tyrePitch[channel] = (sample, pitch);
            int control = sample == 44 ? Math.Max(0, pitch - 60) : pitch;
            int volume = RaceSoundMath.Attenuation(squared * 2);
            if (channel is 6 or 7) volume = volume * RaceSoundMath.StrengthPitch(strength) / 127;
            Set(channel, "GEN", sample, control, volume * MasterVolume / 127 / 2, pan);
        }
    }

    // sample variation uses the isolated sound stream, never the AI LCG. [ref:2099]
    public int ImpactSample(int kind, int material, int strength)
    {
        int sample;
        bool bit() => (_soundRandom.Next() & 1) != 0;
        if (kind == 7) return 2;
        if (kind == 2 && material == 0) sample = 31;
        else if (kind == 1 && material == 10) sample = 30;
        else if (kind == 1 && material is 11 or 5) sample = 29;
        else if (kind == 1 && material == 4) sample = strength > 110 ? 33 : (_soundRandom.Next() & (uint)kind) != 0 ? 30 : 32;
        else if (kind == 1 && material == 12) sample = (_soundRandom.Next() & 3) switch { 0 => 35, 1 => 34, 2 => 32, _ => 37 };
        else if (kind == 1 && material is 0 or 3)
        {
            sample = strength >= 81 ? 33 : strength >= 23 ? 34 : strength >= 17 ? 32 : 29;
            if (sample == _lastImpactSample && sample != 33)
                sample = sample switch { 29 => bit() ? 34 : 32, 32 => bit() ? 29 : 34, _ => bit() ? 29 : 32 };
        }
        else
        {
            sample = strength >= 91 ? 33 : strength >= 71 ? 32 : strength >= 51 ? 37 : strength >= 26 ? 34 : 30;
            if (sample == _lastImpactSample && sample != 33)
                sample = sample switch { 30 => 34, 32 => bit() ? 37 : 34, 34 => bit() ? 30 : 37, _ => bit() ? 30 : 34 };
        }
        if (material is 10 or 11 or 4 or 12 && _session.TrackNumber == 8) sample = 4;
        return _lastImpactSample = sample;
    }

    private void RequestHorn(int car, int count, int length, int gap)
    {
        // one shared AI horn, only the two selected cars. [ref:2100]
        if (_hornCar != null || !_nearby.Contains(car)) return;
        _hornCar = car; _hornCount = count; _hornLength = length; _hornGap = -gap; _hornTime = length;
    }
    private void UpdateAiHorn((Vec3 Position, Vec3 Right, Vec3 Forward) listener)
    {
        if (_hornCar is not { } c) return;
        if (!_nearby.Contains(c)) { Stop(19); _hornCar = null; return; }
        if (_hornTime > 0)
        {
            if (--_hornTime == 0)
            {
                Stop(19);
                if (--_hornCount == 0) _hornCar = null;
                else _hornTime = _hornGap;
                return;
            }
            var d = View(c).Pos - listener.Position;
            int distance = RaceSoundMath.Distance(d.X, d.Y, d.Z), m = distance / 65536;
            int pan = distance == 0 ? 64 : Math.Clamp(Fx.Div(Vec3.Dot(d, listener.Right), distance), -65536, 65536) / 1024 + 64;
            int model = CarType(c);
            int pitch = AudioConstants.CarPitch[model];
            Set(19, OpponentBank(), 1, pitch, RaceSoundMath.Attenuation(m * m) * MasterVolume / 127 / 2, pan);
        }
        else if (++_hornTime == 0) _hornTime = _hornLength;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_legacyRace != null) _legacyRace.OnSoundFrame -= LegacySoundFrame;
        if (_legacyPublisher != null) { _legacyPublisher.Published -= Present; _legacyPublisher.Dispose(); }
        foreach (var voice in Mixer.Voices) voice.Stop();
        _channels.Clear();
    }
}
