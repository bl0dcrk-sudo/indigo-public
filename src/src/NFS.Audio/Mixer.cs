namespace NFS.Audio;

/// <summary>Decoded sound (interleaved int16) with optional loop (in frames).</summary>
public sealed record Sound(short[] Pcm, int Channels, int SampleRate, int LoopStart = -1, int LoopEnd = -1)
{
    public int Frames => Pcm.Length / Channels;
    public bool Loops => LoopStart >= 0 && LoopEnd > LoopStart;

    public static Sound FromBank(BnkBank bank, BnkSample s) =>
        new(bank.Decode(s), s.Channels, s.SampleRate, s.Header.LoopStart, s.Header.LoopEnd);
}

/// <summary>One mixer voice. Volume 0..127, pan 0..127 (0x40 = centre) as the EA API (doc 5.6); pitch = playback
/// rate multiplier.</summary>
public sealed class Voice
{
    public Sound Sound { get; }
    public int Volume { get; set; } = 127;
    public int Pan { get; set; } = 0x40;
    public double Pitch { get; set; } = 1.0;
    public bool Active { get; internal set; } = true;
    /// <summary>Position in source frames (fractional).</summary>
    public double Position { get; internal set; }

    internal Voice(Sound s) => Sound = s;
    public void Stop() => Active = false;
}

/// <summary>Simple software mixer for the port (not a 1:1 copy of the original mixer): [ref:0019]
/// N voices → interleaved stereo int16, linear resampling, linear pan, int32 accumulation with clamping.</summary>
public sealed class Mixer(int outputRate = 22050)
{
    public int OutputRate { get; } = outputRate;
    readonly List<Voice> voices = [];
    public IReadOnlyList<Voice> Voices => voices;
    public long ClippedSamples { get; private set; }
    /// <summary>PCM output headroom, independent of the original voice controls.</summary>
    public float OutputGain { get; set; } = 1;

    public Voice Play(Sound s, int volume = 127, int pan = 0x40, double pitch = 1.0)
    {
        var v = new Voice(s) { Volume = Math.Clamp(volume, 0, 127), Pan = Math.Clamp(pan, 0, 127), Pitch = pitch };
        voices.Add(v);
        return v;
    }

    /// <summary>Gains (0..1) for a pan value: centre = both full, hard left/right mutes the other side.</summary>
    public static (float L, float R) PanGains(int pan)
    {
        pan = Math.Clamp(pan, 0, 127);
        float l = pan <= 0x40 ? 1f : (127 - pan) / 63f;
        float r = pan >= 0x40 ? 1f : pan / 64f;
        return (l, r);
    }

    static float Sample(Sound s, int frame, int ch) => s.Pcm[frame * s.Channels + Math.Min(ch, s.Channels - 1)];

    /// <summary>Mixes <c>output.Length/2</c> stereo frames; finished voices are removed.</summary>
    public void Mix(Span<short> output)
    {
        int frames = output.Length / 2;
        var acc = new float[frames * 2];
        foreach (var v in voices)
        {
            if (!v.Active) continue;
            var s = v.Sound;
            double step = v.Pitch * s.SampleRate / OutputRate;
            var (gl, gr) = PanGains(v.Pan);
            float vol = v.Volume / 127f;
            double pos = v.Position;
            int end = s.Loops ? Math.Min(s.LoopEnd, s.Frames) : s.Frames;
            for (int i = 0; i < frames; i++)
            {
                if (pos >= end)
                {
                    if (s.Loops) pos = s.LoopStart + (pos - end) % (end - s.LoopStart);
                    else { v.Active = false; break; }
                }
                int i0 = (int)pos;
                float t = (float)(pos - i0);
                int i1 = i0 + 1 < end ? i0 + 1 : s.Loops ? s.LoopStart : i0;
                float l = Sample(s, i0, 0) * (1 - t) + Sample(s, i1, 0) * t;
                float r = Sample(s, i0, 1) * (1 - t) + Sample(s, i1, 1) * t;
                acc[2 * i] += l * vol * gl;
                acc[2 * i + 1] += r * vol * gr;
                pos += step;
            }
            v.Position = pos;
        }
        voices.RemoveAll(v => !v.Active);
        for (int i = 0; i < acc.Length; i++)
        {
            acc[i] *= OutputGain;
            if (acc[i] < short.MinValue || acc[i] > short.MaxValue) ClippedSamples++;
            output[i] = (short)Math.Clamp((int)MathF.Round(acc[i]), short.MinValue, short.MaxValue);
        }
    }

    public short[] Mix(int frames)
    {
        var o = new short[frames * 2];
        Mix(o);
        return o;
    }
}
