using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>User statistics: `stats` directory next to the settings (`UserStats`), "clear records" (31 × 20 bytes [ref:3271]
/// of zeros), and user-copy precedence over game data when reading track records and personal stats.
/// All files are created in a temporary directory – game data is only read.</summary>
public class UserStatsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("nfs-stats-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ClearedBytesAreTheOriginals31EmptyRecords()
    {
        // 31 entries × 20 bytes, empty name (""), car/time/level 0 → 620 zeros. [ref:3272] [ref:3494]
        var bytes = TrackRecords.ClearedBytes();
        Assert.Equal(620, bytes.Length);
        Assert.Equal(new byte[620], bytes);
        var file = StatsFile.Parse(bytes);
        Assert.Equal(31, file.Records.Count);
        Assert.All(file.Records, r =>
        { Assert.Equal("", r.Name); Assert.Equal(0, r.Car); Assert.Equal(0, r.Time); Assert.Equal(0, r.Skill); });
    }

    static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    static byte[] StatBytes(string name, short car, int time, short skill)
    {
        var d = new byte[StatsFile.RecordSize];
        Encoding.ASCII.GetBytes(name).CopyTo(d, 0);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(10), car);
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(12), time);
        BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(16), skill);
        return d;
    }

    static byte[] PersonalStatBytes(int lapTime)
    {
        var d = new byte[2 * PersonalStats.RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(8), lapTime);      // entry 0: lap time (+8)
        return d;
    }

    [GameFact]
    public void ClearWritesTheUserCopyAndLeavesGameDataUntouched()
    {
        string gameFile = TrackRecords.FilePath(0);
        string before = Sha(GameFiles.ReadRaw(gameFile, GameData.Dir));

        TrackRecords.Clear(0, _dir);

        string userFile = UserStats.PathFor(_dir, gameFile);
        Assert.True(File.Exists(userFile), $"no user copy {userFile}");
        Assert.Equal(TrackRecords.ClearedBytes(), File.ReadAllBytes(userFile));
        Assert.False(File.Exists(userFile + ".tmp"), "atomic save leaves no temporary file");
        Assert.Equal(before, Sha(GameFiles.ReadRaw(gameFile, GameData.Dir)));   // game directory untouched
    }

    [GameFact]
    public void TrackRecordReadPrefersTheUserCopyOverGameData()
    {
        UserStats.Write(_dir, TrackRecords.FilePath(0), StatBytes("TESTUSER", 5, 9999, 1));

        var user = TrackRecords.Load(0, GameData.Dir, _dir)!;
        Assert.Equal(9999, user.Records[0].Time);
        Assert.Equal("TESTUSER", user.Records[0].Name.TrimEnd());
        Assert.Equal(9999, TrackRecords.Lap(0, GameData.Dir, _dir)!.Value.Time);

        // without the user directory the game data file is used (OVAL.STF: lap record 3168)
        Assert.Equal(3168, TrackRecords.Lap(0, GameData.Dir)!.Value.Time);
    }

    [GameFact]
    public void PersonalStatsReadPrefersTheUserCopyOverGameData()
    {
        string path = PersonalStats.FilePath(0, backward: false);
        UserStats.Write(_dir, path, PersonalStatBytes(lapTime: 1234));

        var user = PersonalStats.Load(0, backward: false, gameDir: GameData.Dir, statsDir: _dir)!;
        Assert.Equal(1234, user.Records[0].LapTime);

        var game = PersonalStats.Load(0, backward: false, gameDir: GameData.Dir)!;
        Assert.NotEqual(1234, game.Records[0].LapTime);
    }
}
