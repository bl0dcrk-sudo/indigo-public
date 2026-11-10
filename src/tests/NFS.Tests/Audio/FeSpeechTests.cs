using NFS.Audio;
using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Audio;

/// <summary>Front-end voice-over: the archive FEDATA/PC/AUDIO/&lt;language&gt;/&lt;language&gt;.VIV, clip selection and the chain [ref:3007]
/// of variants, clip decoding (EA-XA mono) and playback through <see cref="MenuAudio"/>. [ref:3008]</summary>
public class FeSpeechTests
{
    static readonly Lazy<FeSpeech?> English = new(() => GameData.Available ? FeSpeech.TryOpen(0, GameData.Dir) : null);

    [Theory]
    [InlineData(0, "FEDATA/PC/AUDIO/ENGLISH/ENGLISH.VIV")]
    [InlineData(1, "FEDATA/PC/AUDIO/GERMAN/GERMAN.VIV")]
    [InlineData(2, "FEDATA/PC/AUDIO/FRENCH/FRENCH.VIV")]
    [InlineData(3, "FEDATA/PC/AUDIO/SPANISH/SPANISH.VIV")]
    [InlineData(4, "FEDATA/PC/AUDIO/ITALIAN/ITALIAN.VIV")]
    [InlineData(5, "FEDATA/PC/AUDIO/ENGLISH/ENGLISH.VIV")]   // language 5 (Swedish) → 0 [ref:3009]
    public void VivPath_FollowsLanguageTable(int language, string expected) => Assert.Equal(expected, FeSpeech.VivPath(language));

    /// <summary>Synthetic BIGF archive with entries for the given names (empty blocks) – checks only the name chain.</summary>
    static FeSpeech Synthetic(params string[] names)
    {
        using var ms = new MemoryStream();
        int dir = 16 + names.Sum(n => 8 + n.Length + 1);
        void Be(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        ms.Write("BIGF"u8); Be(dir); Be(names.Length); Be(dir);
        foreach (var n in names) { Be(dir); Be(0); ms.Write(System.Text.Encoding.ASCII.GetBytes(n)); ms.WriteByte(0); }
        return FeSpeech.Open(ms.ToArray());
    }

    [Fact]
    public void Sequence_CommentaryClipsPlayAlone_OthersChainThroughVariants()
    {
        var s = Synthetic("c00", "c01", "h00", "h01", "h02", "h04", "050");
        Assert.Equal(["c00"], s.Sequence("c00"));                 // = (name[0] != 'c') → no chain [ref:3010]
        Assert.Equal(["h00", "h01", "h02"], s.Sequence("h00"));   // h03 missing → end (h04 is not appended)
        Assert.Equal(["050"], s.Sequence("050"));
        Assert.Empty(s.Sequence("t00"));                          // no entry → silence
        Assert.Equal(["H00", "H01", "H02"], s.Sequence("H00"));   // names are case-insensitive (stricmp)
    }

    [GameFact]
    public void EnglishArchive_HasAllClipNamesUsedByTheExe()
    {
        var s = English.Value!;
        Assert.Equal(169, s.Clips.Count());
        // Names from the original game's tables: victory c00 (network c01/c02), history h%d0, track t%d0, car %02d0 (0..11 and 12). [ref:3011]
        foreach (var n in new[] { "c00", "c01", "c02", "c37" }) Assert.True(s.Contains(n), n);
        foreach (var n in new[] { 0, 1, 2, 3, 4, 6, 7 }) Assert.True(s.Contains($"h{n}0"), $"h{n}0");
        foreach (var t in new[] { 0, 2, 3, 4, 5, 6, 7, 8 }) Assert.True(s.Contains($"t{t}0"), $"t{t}0");
        for (int car = 0; car <= 12; car++) Assert.True(s.Contains($"{car:D2}0"), $"{car:D2}0");
    }

    [GameFact]
    public void EnglishArchive_VariantChains()
    {
        var s = English.Value!;
        Assert.Equal(["c00"], s.Sequence("c00"));
        Assert.Equal(["h00", "h01", "h02", "h03", "h04", "h05", "h06"], s.Sequence("h00"));
        Assert.Equal(["t00", "t01", "t02"], s.Sequence("t00"));
        Assert.Equal(["050", "051", "052", "053", "054", "055", "056"], s.Sequence("050"));
    }

    [GameFact]
    public void Clip_DecodesToNonEmptyMonoPcmOfHeaderLength()
    {
        var s = English.Value!;
        var c00 = s.Load("c00")!;
        Assert.Equal(1, c00.Channels);
        Assert.Equal(22050, c00.SampleRate);            // PT without tag 0x84 → default 22050 [hypothesis: like the SFX banks]
        Assert.Equal(51795, c00.Frames);                // PT tag 0x85 = 0x00CA53 samples (≈ 2.35 s)
        Assert.Contains(c00.Pcm, v => v != 0);
        Assert.InRange(c00.Pcm.Max(v => Math.Abs((int)v)), 1000, 32768);
        Assert.False(c00.Loops);
        foreach (var name in new[] { "000", "c01", "h00", "t00", "120" })
        {
            var clip = s.Load(name)!;
            Assert.True(clip.Frames > 22050, name);     // longer than a second
            Assert.Contains(clip.Pcm, v => v != 0);
        }
    }

    [GameFact]
    public void Missing_LanguageArchive_YieldsNoSpeech()
    {
        // The installation has only ENGLISH.VIV; the folders of the other languages are empty.
        Assert.Null(FeSpeech.TryOpen(1, GameData.Dir));
        Assert.Null(FeSpeech.TryOpen(4, GameData.Dir));
        Assert.NotNull(FeSpeech.TryOpen(5, GameData.Dir));    // 5 → English
    }

    [GameFact]
    public void MenuAudio_PlaySpeech_MixesClipThenStops()
    {
        using var a = new MenuAudio(GameData.Dir);
        a.MasterVolume = 127;
        Assert.True(a.PlaySpeech("c00", 0));
        Assert.True(a.SpeechActive);
        Assert.Equal(127, a.Mixer.Voices.Single().Volume);
        long energy = 0; int frames = 0;
        a.BufferReady += p => { frames += p.Length / 2; foreach (var v in p) energy += Math.Abs((long)v); };
        for (int i = 0; i < 20; i++) a.Service(0.25);          // 5 s ≥ 2.35 s clip
        Assert.True(frames > 51795 / 2);
        Assert.True(energy > 0);
        Assert.False(a.SpeechActive);
        Assert.False(a.HasActiveVoices);
    }

    [GameFact]
    public void MenuAudio_PlaySpeech_VolumeFromConfigAndNoLanguageNoSound()
    {
        using var a = new MenuAudio(GameData.Dir);
        a.MasterVolume = 64;
        Assert.True(a.PlaySpeech("c00", 0));
        Assert.Equal(64, a.Mixer.Voices.Single().Volume);       // no halving by 2, unlike the SFX banks
        Assert.False(a.PlaySpeech("c00", 2));                   // no FRENCH.VIV → silence, previous speech stopped
        Assert.False(a.SpeechActive);
        a.MasterVolume = 0;
        Assert.False(a.PlaySpeech("c00", 0));                   // volume 0 → nothing plays
        a.MasterVolume = 127;
        Assert.False(a.PlaySpeech("zzz", 0));                   // no clip
    }

    [GameFact]
    public void MenuAudio_PlaySpeech_ChainsVariantsAndReplacesPrevious()
    {
        using var a = new MenuAudio(GameData.Dir);
        a.MasterVolume = 127;
        Assert.True(a.PlaySpeech("t00", 0));                    // t00 → t01 → t02
        var viv = English.Value!;
        double total = new[] { "t00", "t01", "t02" }.Sum(n => viv.Load(n)!.Frames / 22050.0);
        double played = 0;
        bool sawVoiceChange = false;
        var first = a.Mixer.Voices.Single();
        while (a.SpeechActive && played < total + 5)
        {
            a.Service(0.25); played += 0.25;
            if (a.Mixer.Voices.Count == 1 && !ReferenceEquals(a.Mixer.Voices[0], first)) sawVoiceChange = true;
        }
        Assert.True(sawVoiceChange);
        Assert.InRange(played, total - 1, total + 1);           // the whole chain, not just the first clip
        // New speech replaces the one still playing.
        Assert.True(a.PlaySpeech("h00", 0));
        Assert.True(a.PlaySpeech("c00", 0));
        a.Service(0.01);
        Assert.Single(a.Mixer.Voices);
    }

    [GameFact]
    public void MenuAudio_StartRace_StopsSpeech()
    {
        using var a = new MenuAudio(GameData.Dir);
        a.MasterVolume = 127;
        Assert.True(a.PlaySpeech("000", 0));
        a.Play(MenuSound.StartRace);                            // [hypothesis] the VIV archive is closed on this path [ref:3012]
        Assert.False(a.SpeechActive);
    }
}
