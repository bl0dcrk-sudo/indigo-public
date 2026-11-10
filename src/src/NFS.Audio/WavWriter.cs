using System.Buffers.Binary;

namespace NFS.Audio;

/// <summary>Minimal RIFF/WAVE 16-bit PCM writer (headless export; no playback).</summary>
public static class WavWriter
{
    public static byte[] Encode(ReadOnlySpan<short> pcm, int sampleRate, int channels)
    {
        int dataLen = pcm.Length * 2;
        var b = new byte[44 + dataLen];
        var s = b.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataLen);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataLen);
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(s[(44 + 2 * i)..], pcm[i]);
        return b;
    }

    public static void Write(string path, ReadOnlySpan<short> pcm, int sampleRate, int channels) =>
        File.WriteAllBytes(path, Encode(pcm, sampleRate, channels));
}
