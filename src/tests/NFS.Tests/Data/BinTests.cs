using NFS.Data;

namespace NFS.Tests.Data;

public class BinTests
{
    [Fact]
    public void ReadsLittleAndBigEndian()
    {
        byte[] b = [0x01, 0x02, 0x03, 0x04, 0xFF, 0xFE];
        Assert.Equal(0x0201, Bin.U16(b, 0));
        Assert.Equal(0x0102, Bin.U16BE(b, 0));
        Assert.Equal(0x010203, Bin.U24BE(b, 0));
        Assert.Equal(0x04030201u, Bin.U32(b, 0));
        Assert.Equal(0x01020304u, Bin.U32BE(b, 0));
        Assert.Equal(-257, Bin.S16(b, 4));
        var r = new BinReader(b);
        Assert.Equal(0x0201, r.U16());
        Assert.Equal(0x0304, r.U16BE());
        Assert.Equal(2, r.Remaining);
        Assert.Throws<EndOfStreamException>(() => r.U32());
    }

    [Fact]
    public void CStrings()
    {
        byte[] b = "ab\0cd\0"u8.ToArray();
        Assert.Equal("ab", Bin.CString(b, 0, 6));
        Assert.Equal("cd", Bin.CString(b, 3, out int end));
        Assert.Equal(6, end);
    }

    [Fact]
    public void PngHasSignatureAndIhdr()
    {
        var png = PngWriter.EncodeRgba(2, 1, new byte[8]);
        Assert.Equal(0x89, png[0]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2u, Bin.U32BE(png, 16));
    }
}

public class GameFilesTests
{
    [GameFact]
    public void ResolvesCaseInsensitively()
    {
        Assert.NotNull(GameFiles.ResolveIn(GameData.Dir!, "gamedata/tracks/se/tr00.trk"));
        Assert.True(GameFiles.Exists("FEDATA/PC/TEXT/TEXT.ENG", GameData.Dir));
        Assert.False(GameFiles.Exists("nope/none.bin", GameData.Dir));
    }
}
