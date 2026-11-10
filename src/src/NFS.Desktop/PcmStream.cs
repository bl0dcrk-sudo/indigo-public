using System.Buffers.Binary;
using Microsoft.Xna.Framework.Audio;

namespace NFS.Desktop;

/// <summary>Host-thread PCM transport. The source advances only with simulation, never in an audio callback.</summary>
public sealed class PcmBufferQueue
{
    readonly Queue<byte[]> _buffers = [];
    public int Count => _buffers.Count;
    public void Enqueue(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length == 0) return;
        if ((pcm.Length & 1) != 0) throw new ArgumentException("stereo PCM requires complete frames", nameof(pcm));
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), pcm[i]);
        if (_buffers.Count == 4) _buffers.Dequeue();
        _buffers.Enqueue(bytes);
    }
    public byte[]? Dequeue() => _buffers.TryDequeue(out var pcm) ? pcm : null;
    public void Clear() => _buffers.Clear();
}

internal sealed class PcmStream(int sampleRate) : IDisposable
{
    readonly DynamicSoundEffectInstance _device = new(sampleRate, AudioChannels.Stereo);
    readonly PcmBufferQueue _queue = new();
    bool _paused;
    public void Enqueue(short[] pcm) => _queue.Enqueue(pcm);
    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        if (paused) _device.Pause();
        else if (_device.State == SoundState.Paused) _device.Resume();
    }
    public void Pump()
    {
        if (_paused) return;
        while (_device.PendingBufferCount < 3 && _queue.Dequeue() is { } bytes) _device.SubmitBuffer(bytes);
        if (_device.PendingBufferCount > 0 && _device.State != SoundState.Playing) _device.Play();
    }
    public void Dispose() { _queue.Clear(); _device.Stop(); _device.Dispose(); }
}
