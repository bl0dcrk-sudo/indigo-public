using NFS.Data;

namespace NFS.Tests.Data;

public class EaCompressionTests
{
    [Fact]
    public void RefPackLiteralsAndBackReference()
    {
        // 10 FB size=8 ; E0 = 4 literals "abcd" ; 0x00-op: lit 0, len 3, off 4 -> "abc" ; FD = 1 literal 'x' + end
        byte[] src = [0x10, 0xFB, 0, 0, 8, 0xE0, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x00, 0x03, 0xFD, (byte)'x'];
        Assert.Equal("abcdabcx", System.Text.Encoding.ASCII.GetString(EaCompression.Unpack(src)));
    }

    [Fact]
    public void NotCompressedIsPassedThrough()
    {
        byte[] b = "SHPI"u8.ToArray();
        Assert.False(EaCompression.IsCompressed(b));
        Assert.Same(b, EaCompression.UnpackIfCompressed(b));
    }

    /// <summary>All 394 compressed game files unpack to the declared size and byte-identical to the reference [ref:3484]
    /// (hashes from a reference file). [ref:3485]</summary>
    [GameFact]
    public void AllGameFilesMatchPythonReference()
    {
        // Reference hashes (sha256, size, path of each unpacked file) next to this test.
        string refFile = Path.Combine(GameData.ProjectRoot, "tests", "NFS.Tests", "Data", "unpack_sha256.txt");
        Assert.True(File.Exists(refFile), "reference hash file not found: " + refFile);
        var expected = File.ReadAllLines(refFile).Where(l => l.Length > 0)
            .Select(l => l.Split(' ', 3)).ToDictionary(p => p[2].ToUpperInvariant(), p => (Sha: p[0], Size: int.Parse(p[1])));
        int count = 0;
        var codecs = new Dictionary<int, int>();
        foreach (var rel in GameData.Files(null))
        {
            var raw = GameData.ReadRaw(rel);
            if (!EaCompression.IsCompressed(raw)) continue;
            var o = EaCompression.Unpack(raw);
            Assert.Equal(EaCompression.UnpackedSize(raw), o.Length);
            var e = expected[rel.ToUpperInvariant()];
            Assert.Equal(e.Size, o.Length);
            Assert.True(e.Sha == GameData.Sha256(o), rel);
            codecs[raw[0]] = codecs.GetValueOrDefault(raw[0]) + 1;
            count++;
        }
        Assert.Equal(394, count);
        Assert.True(codecs.ContainsKey(0x10) && codecs.ContainsKey(0x30) && codecs.ContainsKey(0x32));
    }
}
