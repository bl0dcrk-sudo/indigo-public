using NFS.Audio;
using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Audio;

/// <summary>The front-end bank FESFX.BNK (slot FE 0) and its wrapper <see cref="MenuAudio"/>: one shot,
/// the start roar (slot 6) and the looped SFX test roar (slot 7).</summary>
public class MenuAudioTests
{
    [Fact]
    public void Slot_Mapping_MatchesOriginalUiSamples()
    {
        Assert.Equal(0, MenuAudio.Slot(MenuSound.Change));
        Assert.Equal(1, MenuAudio.Slot(MenuSound.Move));
        Assert.Equal(2, MenuAudio.Slot(MenuSound.Select));
        Assert.Equal(3, MenuAudio.Slot(MenuSound.Back));
        Assert.Equal(6, MenuAudio.Slot(MenuSound.StartRace));
    }

    [GameFact]
    public void FesfxBank_MatchesOriginalSlots()
    {
        var b = BnkBank.Parse(GameData.ReadRaw(MenuAudio.BankPath));
        Assert.Equal(2, b.Version);
        Assert.Equal(8, b.Slots.Count);
        Assert.Equal(0, b.Slots[0]!.Codec);          // 16-bit PCM mono
        Assert.Equal(1, b.Slots[0]!.Channels);
        Assert.Equal(16000, b.Slots[0]!.SampleRate);
        Assert.Equal(1024, b.Slots[0]!.Samples);
        Assert.Equal(2048, b.Slots[1]!.Samples);
        Assert.Equal(22050, b.Slots[2]!.SampleRate);
        Assert.Equal(604, b.Slots[2]!.Samples);
        Assert.Equal(44100, b.Slots[6]!.SampleRate);
        Assert.Equal(87125, b.Slots[6]!.Samples);
        Assert.Equal(44100, b.Slots[7]!.SampleRate);
        Assert.Equal(87125, b.Slots[7]!.Samples);
        // Slot 6 = one-shot start roar; slot 7 = looped SFX test roar.
        Assert.False(Sound.FromBank(b, b.Slots[6]!).Loops);
        Assert.True(Sound.FromBank(b, b.Slots[7]!).Loops);
    }

    [GameFact]
    public void Play_OneShot_AddsVoiceAndServiceMixesPcm()
    {
        using var a = new MenuAudio(GameData.Dir);
        Assert.Equal(22050, a.Mixer.OutputRate);
        a.Play(MenuSound.Move);
        Assert.True(a.HasActiveVoices);
        short[]? pcm = null;
        a.BufferReady += p => pcm = p;
        a.Service(0.05);
        Assert.NotNull(pcm);
        Assert.Equal((int)(0.05 * 22050) * 2, pcm!.Length);
    }

    [GameFact]
    public void MasterVolume_ScalesOneShotVolume()
    {
        using var a = new MenuAudio(GameData.Dir);
        a.MasterVolume = 64;
        a.Play(MenuSound.Move);                       // volume 127 → 64
        Assert.Equal(64, a.Mixer.Voices.Single().Volume);
    }

    [GameFact]
    public void SetRoar_StartsLoopAndStops()
    {
        using var a = new MenuAudio(GameData.Dir);
        a.SetRoar(true, 100);
        Assert.True(a.HasActiveVoices);
        a.Service(1.0);                               // the loop survives a second of mixing
        Assert.True(a.HasActiveVoices);
        a.SetRoar(false, 0);
        Assert.False(a.HasActiveVoices);
        a.Service(0.01);                              // a stopped voice is removed on mixing
        Assert.False(a.HasActiveVoices);
    }
}
