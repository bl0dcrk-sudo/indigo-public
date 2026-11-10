using NFS.Audio;
using NFS.Core;
using NFS.Data;
using NFS.Data.Track;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;

namespace NFS.Tests.Audio;

/// <summary>Track ambient sounds, XBID 17. [ref:3020] [ref:3468]</summary>
public class TrackAmbienceTests
{
    private const int M = 65536;
    private static readonly (int, int, int) Right = (M, 0, 0);

    private static TrackAmbience Synthetic(AmbientSource?[] sources, ushort[][] neighbors, Func<uint>? random = null) =>
        new(sources, neighbors, random ?? (() => 0));

    [Theory]
    [InlineData(0, 127)] [InlineData(10, 127)] [InlineData(45, 127)]
    [InlineData(100, 26)] [InlineData(300, 2)] [InlineData(512, 1)] [InlineData(513, 0)] [InlineData(1000, 0)]
    public void Volume_FallsWithDistanceAndVanishesOutOfRange(int metres, int expected) =>
        Assert.Equal(expected, TrackAmbience.VolumeForDistance(metres * M));

    [Fact]
    public void Volume_NeverIncreasesWithDistance()
    {
        int last = 127;
        for (int m = 0; m <= 700; m++)
        {
            int v = TrackAmbience.VolumeForDistance(m * M);
            Assert.InRange(v, 0, last);
            last = v;
        }
    }

    [Fact]
    public void LoopingSource_PlaysLouderCloseAndSilentFar()
    {
        var amb = Synthetic([new AmbientSource(0, 0, 0, 3, 0)], [[0]]);
        int near = amb.Update(0, (-10 * M, 0, 0), Right, 0).Loop!.Value.Volume;
        int mid = amb.Update(0, (-200 * M, 0, 0), Right, 1).Loop!.Value.Volume;
        int far = amb.Update(0, (-700 * M, 0, 0), Right, 2).Loop!.Value.Volume;
        Assert.True(near > mid && mid > far, $"{near} {mid} {far}");
        Assert.Equal(0, far);   // out of range: the voice keeps running with volume 0 (inaudible)
        Assert.Equal(3, amb.Update(0, (0, 0, 0), Right, 3).Loop!.Value.Sample);
    }

    [Fact]
    public void Pan_FollowsSideOfSource_AndBlockWithoutSourceStopsLoop()
    {
        var amb = Synthetic([new AmbientSource(50 * M, 0, 0, 1, 0), null], [[0], [1]]);
        Assert.True(amb.Update(0, (0, 0, 0), Right, 0).Loop!.Value.Pan >= 127);                     // to the right: 64 + 64
        Assert.Equal(0, TrackAmbience.DistancePan((100 * M, 0, 0), Right, (50 * M, 0, 0)).Pan);    // to the left
        Assert.Equal(64, TrackAmbience.DistancePan((0, 0, 0), Right, (0, 0, 50 * M)).Pan);         // straight ahead
        Assert.Null(amb.Update(1, (0, 0, 0), Right, 1).Loop);
        Assert.Equal(default, amb.Update(7, (0, 0, 0), Right, 2));                                  // block outside the table
    }

    [Fact]
    public void OnlyFirstLoopOfNeighbourListPlays_RandomShotsDoNotBlockIt()
    {
        var amb = Synthetic([new AmbientSource(0, 0, 0, 5, 1), new AmbientSource(M, 0, 0, 6, 0), new AmbientSource(2 * M, 0, 0, 7, 0)],
            [[0, 1, 2]]);
        var f = amb.Update(0, (0, 0, 0), Right, 8);   // (8 & 0x3F) >> 3 == 1 == initial [ref:3021]
        Assert.Equal(6, f.Loop!.Value.Sample);
        Assert.Equal(5, f.Shot!.Value.Sample);
    }

    [Fact]
    public void RandomShot_FiresInPhaseWindow_ThenRerolls()
    {
        var amb = Synthetic([new AmbientSource(0, 0, 0, 2, 1)], [[0]], () => 3);
        Assert.Null(amb.Update(0, (0, 0, 0), Right, 0).Shot);                 // window 0, phase 1
        Assert.NotNull(amb.Update(0, (0, 0, 0), Right, 9).Shot);              // window 1 -> fires, new phase 3
        Assert.Null(amb.Update(0, (0, 0, 0), Right, 10).Shot);                // window 1 again: phase is 3 now
        Assert.Null(amb.Update(0, (0, 0, 0), Right, 64 * 5 + 16).Shot);       // window 2
        Assert.NotNull(amb.Update(0, (0, 0, 0), Right, 64 * 7 + 24).Shot);    // window 3 of a later period
    }

    private static readonly Dictionary<int, int> Expected = new()
    { [0] = 0, [2] = 0, [3] = 2, [4] = 1, [5] = 8, [6] = 3, [7] = 3, [8] = 0 };

    private static Trk LoadTrk(int t) =>
        Trk.Load(GameFiles.Exists($"GAMEDATA/TRACKS/SE/TR{t:00}.TRK", GameData.Dir) ? $"GAMEDATA/TRACKS/SE/TR{t:00}.TRK" : $"GAMEDATA/TRACKS/PC/TR{t:00}.TRK", GameData.Dir);

    [GameFact]
    public void Parser_CountsSourcesPerTrack_AndEachBlockSeesItself()
    {
        foreach (var (track, count) in Expected)
        {
            var trk = LoadTrk(track);
            var sources = AmbientSource.ForTrack(trk);
            Assert.Equal(count, sources.Count(s => s != null));
            var neighbors = AmbientSource.NeighborsForTrack(trk);
            for (int b = 0; b < sources.Length; b++)
            {
                if (sources[b] == null) continue;
                Assert.Contains((ushort)b, neighbors[b]);
                Assert.Equal(1, trk.Blocks[b].Xb(17)!.Count);
                Assert.True(GameFiles.Exists($"GAMEDATA/AUDIO/SFX/PC/TRAMB{track:00}.BNK", GameData.Dir));
            }
        }
    }

    [GameFact]
    public void Parser_ReadsPositionSampleAndType()
    {
        var s = AmbientSource.ForTrack(LoadTrk(4)).Single(x => x != null)!.Value;
        Assert.Equal((176, 201), (s.X / M, s.Y / M));
        Assert.Equal((4, 0), (s.Sample, s.Type));
        Assert.False(s.IsRandomShot);
        var tr3 = AmbientSource.ForTrack(LoadTrk(3)).Where(x => x != null).ToArray();
        Assert.All(tr3, x => Assert.True(x!.Value.IsRandomShot));   // TR03: two type-1 (random) sources
    }

    private static (RaceAudio Audio, RaceRunner Run, Vec3 Source) Race()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), countdown: false);
        var src = AmbientSource.ForTrack(run.Session.Trk).Single(x => x != null)!.Value;   // replay = TR04
        return (new RaceAudio(run, GameData.Dir), run, new Vec3(src.X, src.Y, src.Z));
    }

    [GameFact]
    public void RaceAudio_PlaysTrambLoopNearSource_AndStopsFarAway()
    {
        var (audio, run, source) = Race();
        using var _ = audio;
        audio.Listener = () => (source + new Vec3(-5 * M, 0, 0), new Vec3(M, 0, 0), new Vec3(0, 0, M));
        run.Step(default);
        var v = audio.Voices.Single(x => x.Channel == 14);
        Assert.Equal("TRAMB04", v.Bank);
        Assert.Equal(4, v.Sample);
        Assert.True(v.Pan > 64);
        audio.Listener = () => (run.Car.Pos, new Vec3(M, 0, 0), new Vec3(0, 0, M));   // the car on the grid is far from the source
        run.Step(default);
        Assert.DoesNotContain(audio.Voices, x => x.Channel == 14);
    }

    [GameFact]
    public void RaceAudio_MixContainsAmbient()
    {
        short[] Mix(bool ambience)
        {
            var (audio, run, source) = Race();
            using var _ = audio;
            audio.Ambience = ambience;
            audio.Listener = () => (source + new Vec3(-5 * M, 0, 0), new Vec3(M, 0, 0), new Vec3(0, 0, M));
            var pcm = new List<short>();
            audio.BufferReady += b => pcm.AddRange(b);
            for (int i = 0; i < 32; i++) run.Step(default);
            return pcm.ToArray();
        }
        var with = Mix(true); var without = Mix(false);
        Assert.Equal(without.Length, with.Length);
        long diff = 0;
        for (int i = 0; i < with.Length; i++) diff += Math.Abs(with[i] - without[i]);
        Assert.True(diff > 1000, $"ambient adds no energy: {diff}");
    }
}
