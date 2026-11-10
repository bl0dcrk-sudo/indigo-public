using NFS.Audio;
using NFS.Tests.Data;

namespace NFS.Tests.Audio;

public class MusMapTests
{
    static byte[] Synthetic()
    {
        // 3 sections, 2 events, 2 rows
        var d = new byte[12 + 3 * 28 + 2 * 2 + 3 * 4];
        "PFDx"u8.CopyTo(d);
        d[5] = 0; d[6] = 3; d[7] = 2; d[0xB] = 2;
        void Sec(int i, byte row, params (sbyte min, sbyte max, byte next)[] br)
        {
            int o = 12 + i * 28;
            d[o] = row; d[o + 1] = (byte)br.Length; d[o + 2] = 0xFF; d[o + 3] = 0xFF;
            for (int j = 0; j < br.Length; j++) { d[o + 4 + 3 * j] = (byte)br[j].min; d[o + 5 + 3 * j] = (byte)br[j].max; d[o + 6 + 3 * j] = br[j].next; }
        }
        Sec(0, 0, (0, 50, 1), (51, 127, 2));
        Sec(1, 1, (0, 127, 0));
        Sec(2, 0);
        int jt = 12 + 3 * 28;
        d[jt + 0] = 2; d[jt + 1] = 1;   // row 0: ev0→2, ev1→1
        d[jt + 2] = 0; d[jt + 3] = 2;   // row 1: ev0→0, ev1→2
        int offs = jt + 4;
        for (int i = 0; i < 3; i++) { d[offs + 4 * i + 2] = (byte)(i + 1); }   // BE 0x100, 0x200, 0x300
        return d;
    }

    [Fact]
    public void Parse_Synthetic()
    {
        var m = MusMap.Parse(Synthetic());
        Assert.Equal(3, m.Sections.Count);
        Assert.Equal(2, m.Sections[0].Branches.Length);
        Assert.Equal(0x200u, m.Sections[1].MusOffset);
        Assert.Equal(0xFFFF, m.Sections[0].Field2);
        Assert.Equal(2, m.Jump(1, 1));
    }

    [Fact]
    public void Sequencer_IntensityBranches()
    {
        var s = new MusSequencer(MusMap.Parse(Synthetic()));
        Assert.True(s.SetIntensity(10));
        Assert.Equal(1, s.Next());
        Assert.Equal(0, s.Next());
        Assert.True(s.SetIntensity(100));
        Assert.Equal(2, s.Next());
        Assert.False(s.Playing);          // section 2 has no branches and no event pending
        Assert.False(s.SetIntensity(128));
        Assert.Equal(2, s.Next());        // no matching branch → same section
    }

    [Fact]
    public void Sequencer_EventsUseJumpTable_AndQueue()
    {
        var s = new MusSequencer(MusMap.Parse(Synthetic()));
        Assert.False(s.PostEvent(2, 0));  // event out of range
        Assert.True(s.PostEvent(1, 1));   // immediate (mode 1)
        Assert.True(s.PostEvent(1, 0));   // queued behind mode 1
        Assert.Equal(1, s.Next());        // row 0, ev 1 → 1
        Assert.Equal(2, s.Next());        // queued: row 1, ev 1 → 2
        Assert.False(s.Playing);
    }

    [GameFact]
    public void AllMaps_Parse_AndOffsetsHitSectionsInTempMus()
    {
        int n = 0;
        foreach (var f in GameData.Files("GAMEDATA/AUDIO/PC", ".MAP"))
        {
            var m = MusMap.Parse(GameData.ReadRaw(f));
            Assert.InRange(m.StartSection, 0, m.Sections.Count - 1);
            Assert.All(m.Sections, s => Assert.All(s.Branches, b => Assert.InRange(b.Next, 0, m.Sections.Count - 1)));
            n++;
        }
        Assert.True(n > 10);

        var map = MusMap.Parse(GameData.ReadRaw("GAMEDATA/AUDIO/PC/TEMP.MAP"));
        Assert.Equal(9, map.Sections.Count);
        var starts = AsfStream.ParseAll(GameData.ReadRaw("GAMEDATA/AUDIO/PC/TEMP.MUS")).Select(s => s.Start).ToHashSet();
        foreach (var s in map.Sections.Where(s => s.Branches.Length > 0))
            Assert.Contains((int)s.MusOffset, starts);
    }

    [GameFact]
    public void TempMap_LoopsBackFromSeven()
    {
        var seq = new MusSequencer(MusMap.Parse(GameData.ReadRaw("GAMEDATA/AUDIO/PC/TEMP.MAP")));
        seq.SetIntensity(0x40);
        var path = new List<int> { seq.Current };
        for (int i = 0; i < 10; i++) path.Add(seq.Next());
        int i7 = path.IndexOf(7);
        Assert.True(i7 >= 0, string.Join(",", path));
        Assert.Equal(2, path[i7 + 1]);   // doc 5.5: 7 → 2
        Assert.True(seq.Playing);
    }
}

public class MixerTests
{
    [Fact]
    public void PanGains_CentreAndHard()
    {
        Assert.Equal((1f, 1f), Mixer.PanGains(0x40));
        Assert.Equal((1f, 0f), Mixer.PanGains(0));
        Assert.Equal((0f, 1f), Mixer.PanGains(127));
    }

    [Fact]
    public void Mix_SameRate_CopiesMonoToBothSides()
    {
        var m = new Mixer(22050);
        m.Play(new Sound([100, 200, 300], 1, 22050));
        var o = m.Mix(5);
        Assert.Equal(new short[] { 100, 100, 200, 200, 300, 300, 0, 0, 0, 0 }, o);
        Assert.Empty(m.Voices);   // finished
    }

    [Fact]
    public void Mix_Resamples_Linearly()
    {
        var m = new Mixer(22050);
        m.Play(new Sound([0, 1000, 2000], 1, 11025), pan: 0);
        var o = m.Mix(4);
        Assert.Equal(new short[] { 0, 0, 500, 0, 1000, 0, 1500, 0 }, o);
    }

    [Fact]
    public void Mix_Volume_AndClamp()
    {
        var m = new Mixer(100);
        m.Play(new Sound([30000, 30000], 1, 100));
        m.Play(new Sound([30000, -20000], 1, 100), volume: 0);
        m.Play(new Sound([30000, 0], 1, 100));
        var o = m.Mix(2);
        Assert.Equal(short.MaxValue, o[0]);
        Assert.Equal(30000, o[2]);
    }

    [Fact]
    public void Mix_Loop_Wraps()
    {
        var m = new Mixer(10);
        var v = m.Play(new Sound([1, 2, 3, 4], 1, 10, LoopStart: 1, LoopEnd: 3));
        var o = m.Mix(6);
        Assert.Equal(new short[] { 1, 2, 3, 2, 3, 2 }, o.Where((_, i) => i % 2 == 0).ToArray());
        Assert.True(v.Active);
    }

    [Fact]
    public void Mix_Stereo_KeepsChannels()
    {
        var m = new Mixer(10);
        m.Play(new Sound([10, -10, 20, -20], 2, 10));
        Assert.Equal(new short[] { 10, -10, 20, -20 }, m.Mix(2));
    }
}
