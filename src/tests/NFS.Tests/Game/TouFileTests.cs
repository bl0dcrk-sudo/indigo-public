using NFS.Data.FrontEnd;
using NFS.Game.Race;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Tournament `.tou` save: format of 0x2C8 bytes, slots in the user directory. [ref:3247] [ref:3493]
/// No game data needed; temporary directories.</summary>
public class TouFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("nfs-tou-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static byte[] Sample()
    {
        var d = new byte[TouFile.Size];
        for (int i = 0; i < d.Length; i++) d[i] = (byte)(i * 7 + 3);
        d[0] = 0xC8; d[1] = 0x02;                       // znacznik 0x2C8
        d[0x2C0] = 3; d[0x2C1] = 0;                     // popcount(maska)
        d[0x2C4] = 0b0010101; d[0x2C5] = d[0x2C6] = d[0x2C7] = 0;
        return d;
    }

    static RaceResults Results(int humanPlace)
    {
        var order = Enumerable.Range(1, 7).ToList(); order.Insert(humanPlace - 1, 0);
        return new RaceResults(Enumerable.Range(0, 8).Select(i => new RacerResult(order.IndexOf(i) + 1, i, $"CAR{i}",
            i == 0, false, 6000 + order.IndexOf(i) * 300, [], null)).ToArray());
    }

    [Fact]
    public void ParseThenWriteReproducesTheBytesExactly()
    {
        var bytes = Sample();
        var file = TouFile.Parse(bytes);
        Assert.Equal(bytes, file.ToBytes());
        Assert.Equal(TouFile.Size, file.ToBytes().Length);
        Assert.Equal(0b0010101, file.Mask);
        Assert.Equal(3, file.TrackCount);
        Assert.Equal(bytes[0x2B0] | bytes[0x2B1] << 8, (ushort)file.Points[0]);
        Assert.Equal(bytes.AsSpan(4, FeConfig.Size).ToArray(), file.Config);   // copy of the config from +4
        Assert.Equal(bytes.AsSpan(2, 2).ToArray(), file.Unused2);               // unused bytes preserved
    }

    [Fact]
    public void ShortFileOrWrongLengthMarkerIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => TouFile.Parse(new byte[TouFile.Size - 1]));
        var bad = Sample(); bad[0] = 0xC9;
        Assert.Throws<InvalidDataException>(() => TouFile.Parse(bad));
        var longer = Sample().Concat(new byte[10]).ToArray();                   // the original reads only 0x2C8
        Assert.Equal(Sample(), TouFile.Parse(longer).ToBytes());
    }

    [Fact]
    public void SeriesSavedAfterARaceRestoresPointsMaskAndLineup()
    {
        var series = new TournamentSeries(2);
        series.Complete(Results(3));
        var config = new FeConfig(new byte[FeConfig.Size]); config[0x1F] = 5;
        var file = series.ToTou(config);
        Assert.Equal(1, file.TrackCount);
        Assert.Equal(series.CompletedMask, file.Mask);
        Assert.Equal(config.Raw, file.Config);
        var loaded = TournamentSeries.Restore(TouFile.Parse(file.ToBytes()), series.Track);
        Assert.Equal(series.Points, loaded.Points);
        Assert.Equal(series.CompletedMask, loaded.CompletedMask);
        Assert.Equal(1, loaded.CompletedTracks);
        Assert.DoesNotContain(2, loaded.AvailableTracks);          // a completed track does not come back
        Assert.False(loaded.ResultsApplied);
        loaded.Complete(Results(1));                               // seria gra dalej
        Assert.Equal(2, loaded.CompletedTracks);
        Assert.Equal(series.Points[0] + 30, loaded.Points[0]);
    }

    [Fact]
    public void NamedSavesRoundTripAtomicallyAndBadFilesFailCleanly()
    {
        var series = new TournamentSeries(0); series.Complete(Results(2));
        var file = series.ToTou(new FeConfig(new byte[FeConfig.Size]));
        Assert.False(TournamentSaves.Exists(_dir, "alpha"));
        TournamentSaves.Save(_dir, "alpha", file);
        TournamentSaves.Save(_dir, "alpha", file);                  // nadpisanie
        Assert.Equal(file.ToBytes(), File.ReadAllBytes(Path.Combine(_dir, "alpha.tou")));   // `%s%s.tou`
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(file.ToBytes(), TournamentSaves.Load(_dir, "alpha").ToBytes());
        Assert.Throws<FileNotFoundException>(() => TournamentSaves.Load(_dir, "beta"));
        File.WriteAllBytes(TournamentSaves.PathFor(_dir, "short"), new byte[100]);
        Assert.Throws<InvalidDataException>(() => TournamentSaves.Load(_dir, "short"));
        var inconsistent = file.ToBytes(); inconsistent[0x2C0] = 5;   // licznik != popcount maski
        File.WriteAllBytes(TournamentSaves.PathFor(_dir, "bad"), inconsistent);
        Assert.Throws<InvalidDataException>(() => TournamentSaves.Load(_dir, "bad"));
        Assert.True(TournamentSaves.Delete(_dir, "alpha"));
        Assert.False(TournamentSaves.Delete(_dir, "alpha"));
        Assert.Throws<ArgumentException>(() => TournamentSaves.PathFor(_dir, "..\\x"));
        Assert.Throws<ArgumentException>(() => TournamentSaves.PathFor(_dir, ""));
    }

    [Fact]
    public void ListHoldsOnlyTheExtensionSortedWithoutIt()
    {
        File.WriteAllBytes(Path.Combine(_dir, "zeta.tou"), []);
        File.WriteAllBytes(Path.Combine(_dir, "Alpha.TOU"), []);
        File.WriteAllBytes(Path.Combine(_dir, "mid.rpy"), []);
        File.WriteAllBytes(Path.Combine(_dir, "old.tou.bak"), []);
        Assert.Equal(["Alpha", "zeta"], TournamentSaves.List(_dir));
        Assert.Equal(["mid"], SaveFiles.List(_dir, ".rpy"));
        Assert.Empty(TournamentSaves.List(Path.Combine(_dir, "missing")));
    }
}
