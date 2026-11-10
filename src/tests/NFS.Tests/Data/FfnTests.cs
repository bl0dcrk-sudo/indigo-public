using NFS.Data.FrontEnd;

namespace NFS.Tests.Data;

public class FfnTests
{
    [GameFact]
    public void Swiss18GlyphMetricsAndAtlasMatchFile()
    {
        var font = Ffn.Load(18, GameData.Dir);
        Assert.Equal(new Ffn.Glyph(10, 19, 96, 132, 9, 0, 5), font.Find('A'));
        Assert.Equal(136, font.Width);
        Assert.Equal(136 * 368, font.Coverage.Length);
        Assert.Equal(13, font.Measure("A "));
        Assert.Null(font.Find('\u2603'));
    }

    [GameFact]
    public void TextHeightIsTheSumOfTheTwoHeaderBytesLikeDatCa7ec()
    {
        // = byte(+0x12) + byte(+0x13); drives the vertical “cancel” step of the list dialog. [ref:3119]
        Assert.Equal([28, 34, 39, 79], new[] { 13, 16, 18, 36 }.Select(size => Ffn.Load(size, GameData.Dir).TextHeight));
    }

    [Fact]
    public void InvalidHeaderIsRejected() => Assert.Throws<InvalidDataException>(() => Ffn.Parse(new byte[32]));
}
