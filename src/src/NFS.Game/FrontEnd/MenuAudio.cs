using NFS.Audio;
using NFS.Data;
using NFS.Data.FrontEnd;

namespace NFS.Game.FrontEnd;

/// <summary>Front-end sound effects: the single FESFX.BNK bank (GAMEDATA/AUDIO/SFX/PC/FESFX.BNK, FE slot 0) through the
/// port's software mixer at 22050 Hz. UI samples are one-shots (<see cref="Play(int,int)"/>); slot 7 is the looping
/// "vehicle roar" of the sound-options screen (<see cref="SetRoar"/>), slot 6 the start-race rev. Master volume is the
/// SFX level (CONFIG.DAT +0x5E), clamped to 0..127; it scales the volume of each sound. [ref:1710]</summary>
public sealed class MenuAudio : IDisposable
{
    public const string BankPath = "GAMEDATA/AUDIO/SFX/PC/FESFX.BNK";
    private readonly BnkBank _bank;
    private readonly string _gameDir;
    private readonly Dictionary<int, Sound> _sounds = [];
    private Voice? _roar;
    private FeSpeech? _speech;
    private int _speechLanguage = -1;
    private bool _speechTried;
    private readonly Queue<Sound> _speechQueue = new();
    private Voice? _speechVoice;
    private double _seconds;
    private int _mixedFrames;
    private bool _disposed;

    /// <summary>Port mixer output headroom (independent of the original voice controls, like <see cref="RaceAudio.Mixer"/>).</summary>
    public Mixer Mixer { get; } = new() { OutputGain = 0.5f };
    public int MasterVolume { get; set; } = 127;
    public event Action<short[]>? BufferReady;
    /// <summary>True while a sound is still playing (start-race roar draining after the menu).</summary>
    public bool HasActiveVoices => Mixer.Voices.Any(v => v.Active);

    public MenuAudio(string? gameDir = null)
    {
        var dir = GameFiles.FindGameDir(gameDir);
        _gameDir = dir;
        if (GameFiles.Exists(FeConfig.FilePath, dir))
            MasterVolume = Math.Min((int)FeConfig.Load(dir)[0x5E], 127);
        _bank = BnkBank.Parse(GameFiles.ReadRaw(BankPath, dir));
    }

    /// <summary>Bank slot of a front-end UI sound. [ref:1711]</summary>
    public static int Slot(MenuSound sound) => sound switch
    {
        MenuSound.Change => 0,
        MenuSound.Move => 1,
        MenuSound.Select => 2,
        MenuSound.Back => 3,
        MenuSound.StartRace => 6,
        _ => 0,
    };

    /// <summary>One-shot at <paramref name="slot"/> (volume scaled by the master SFX level). [ref:1712]</summary>
    public void Play(int slot, int volume = 127)
    {
        if (_disposed || (uint)slot >= (uint)_bank.Slots.Count || _bank.Slots[slot] is not { } sample) return;
        if (!_sounds.TryGetValue(slot, out var sound))
            _sounds[slot] = sound = Sound.FromBank(_bank, sample);
        Mixer.Play(sound, volume * MasterVolume / 127, 0x40);
    }

    public void Play(MenuSound sound, int volume = 127)
    {
        // [hypothesis] closes the speech archive before the start – speech does not carry over into the race. [ref:1713]
        if (sound == MenuSound.StartRace) StopSpeech();
        Play(Slot(sound), volume);
    }

    /// <summary>True while a speech clip (or the rest of its variant chain) is still playing.</summary>
    public bool SpeechActive => _speechVoice is { Active: true } || _speechQueue.Count > 0;

    /// <summary>Starts the narrator's speech: the previous speech is replaced; the clip and its variants [ref:1714]
    /// (see <see cref="FeSpeech.Sequence"/>) play one after another. Volume = level from CONFIG +0x5E (<see cref="MasterVolume"/>, in the original
    /// <c>min(0x7F)</c> in the stream parameters), centre pan, without halving as for the SFX banks. [ref:1715]
    /// Returns false when there is no language archive or clip, or the volume is 0 (in the original the stream starts silent). The language archive
    /// is opened on first use; a missing file (apart from ENGLISH.VIV there are no others) = no speech, without an error.</summary>
    public bool PlaySpeech(string clip, int language)
    {
        if (_disposed) return false;
        if (!_speechTried || _speechLanguage != language)
        {
            _speechTried = true;
            _speechLanguage = language;
            _speech = null;
            try { _speech = FeSpeech.TryOpen(language, _gameDir); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        StopSpeech();
        if (_speech is not { } speech || MasterVolume <= 0) return false;
        IReadOnlyList<Sound> sounds;
        try { sounds = speech.LoadSequence(clip); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException) { return false; }
        if (sounds.Count == 0) return false;
        foreach (var s in sounds) _speechQueue.Enqueue(s);
        StartNextSpeech();
        return true;
    }

    public void StopSpeech()
    {
        _speechQueue.Clear();
        _speechVoice?.Stop();
        _speechVoice = null;
    }

    private void StartNextSpeech()
    {
        if (_speechQueue.Count == 0) { _speechVoice = null; return; }
        _speechVoice = Mixer.Play(_speechQueue.Dequeue(), Math.Clamp(MasterVolume, 0, 127), 0x40);
    }

    /// <summary>Looping "vehicle roar" (slot 7) of the sound-options screen while the speech or SFX slider is selected
    /// the volume tracks the SFX level / 2 live. <paramref name="on"/> false stops it. [ref:1716]</summary>
    public void SetRoar(bool on, int sfxLevel)
    {
        int vol = Math.Clamp(sfxLevel / 2, 0, 127);
        if (!on) { _roar?.Stop(); _roar = null; return; }
        if (_roar is { Active: true }) { _roar.Volume = vol; return; }
        if ((uint)7 < (uint)_bank.Slots.Count && _bank.Slots[7] is { } sample)
        {
            if (!_sounds.TryGetValue(7, out var sound)) _sounds[7] = sound = Sound.FromBank(_bank, sample);
            _roar = Mixer.Play(sound, vol, 0x40);
        }
    }

    /// <summary>One menu clock tick (~display rate): mixes out the PCM for the elapsed wall-clock time. [ref:1717]</summary>
    public void Service(double elapsedSeconds)
    {
        if (_disposed) return;
        _seconds += Math.Min(elapsedSeconds, 0.25);
        int end = (int)(_seconds * Mixer.OutputRate);
        if (end <= _mixedFrames) return;
        var pcm = Mixer.Mix(end - _mixedFrames);
        _mixedFrames = end;
        if (_speechVoice is { Active: false }) StartNextSpeech();   // next variant after the end of the clip [ref:1718]
        BufferReady?.Invoke(pcm);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _roar?.Stop();
        StopSpeech();
        foreach (var voice in Mixer.Voices) voice.Stop();
    }
}
