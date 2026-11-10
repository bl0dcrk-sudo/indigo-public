namespace NFS.Desktop.Tests;

/// <summary>Change network-menu-look, task 1.3: <see cref="NetText.Safe"/> (design D9) and the caches of the texts the menu layout
/// builds every frame.</summary>
[Collection("Allocations")]
public sealed class NetTextTests
{
    [Theory]
    [InlineData("Masło", "Maslo")]
    [InlineData("Zażółć gęślą jaźń", "Zazolc gesla jazn")]
    [InlineData("ĄĆĘŁŃÓŚŹŻ", "ACELNOSZZ")]
    [InlineData("ąćęłńóśźż", "acelnoszz")]
    [InlineData("Müller Ærø Crème Brûlée Škoda Çağrı", "Muller AEro Creme Brulee Skoda Cagri")]
    [InlineData("straße", "strasse")]
    [InlineData("Plain 123 ~!", "Plain 123 ~!")]
    [InlineData("", "")]
    public void Safe_MapsLettersWithDiacriticsToTheirBaseLetters(string text, string expected) => Assert.Equal(expected, NetText.Safe(text));

    [Theory]
    [InlineData("a😀b", "a?b")]                  // one '?' per character, not per UTF-16 half
    [InlineData("世界", "??")]
    [InlineData("tab\there", "tab?here")]
    [InlineData("line\nbreak", "line?break")]
    [InlineData("\u007F", "?")]
    [InlineData("€5", "?5")]
    [InlineData("Привет", "??????")]
    [InlineData("́", "?")]                  // a lone combining mark
    public void Safe_ReplacesWhatTheFontCannotDrawWithAQuestionMark(string text, string expected) => Assert.Equal(expected, NetText.Safe(text));

    [Fact]
    public void Safe_OfNullIsEmpty_AndOfPlainAsciiIsTheSameString()
    {
        Assert.Equal("", NetText.Safe(null));
        string plain = new string('x', 5) + "Y z";
        Assert.Same(plain, NetText.Safe(plain));
    }

    [Fact]
    public void Safe_ResultOnlyHoldsPrintableAscii()
    {
        var all = new string(Enumerable.Range(0, 0x250).Select(i => (char)i).ToArray());
        Assert.All(NetText.Safe(all), c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void RepeatedCalls_AllocateNothingAndReturnTheCachedString()
    {
        string name = "Masło Żółw", cut = new string('a', 40), lower = "mixed Case";
        string a = NetText.Safe(name), j = NetText.Join("CAR: ", a, " LOCKED"), c = NetText.Cut(cut, 12), u = NetText.Upper(lower), i = NetText.Int(5000), t = NetText.Time(64 * 95);
        Assert.Same(a, NetText.Safe(name));
        Assert.Same(j, NetText.Join("CAR: ", a, " LOCKED"));
        Assert.Same(c, NetText.Cut(cut, 12));
        Assert.Same(u, NetText.Upper(lower));
        Assert.Same(i, NetText.Int(5000));
        Assert.Same(t, NetText.Time(64 * 95));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 100; n++)
        {
            NetText.Safe(name); NetText.Join("CAR: ", a, " LOCKED"); NetText.Cut(cut, 12); NetText.Upper(lower); NetText.Int(5000); NetText.Time(64 * 95);
            NetText.Safe("plain"); NetText.Int(7); NetText.Cut("short", 12);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Helpers_GiveTheSameTextAsTheDirectCall()
    {
        Assert.Equal("ab", NetText.Join("a", "b"));
        Assert.Equal("abc", NetText.Join("a", "b", "c"));
        Assert.Equal("b", NetText.Join("", "b", ""));
        Assert.Equal("", NetText.Join("", ""));
        Assert.Equal("abc", NetText.Cut("abcdef", 3));
        Assert.Equal("abc", NetText.Cut("abc", 3));
        Assert.Equal("ABC DÉ", NetText.Upper("abc dé"));
        Assert.Equal("0", NetText.Int(0));
        Assert.Equal("999", NetText.Int(999));
        Assert.Equal("1000", NetText.Int(1000));
        Assert.Equal("-1", NetText.Int(-1));
        Assert.Equal(NFS.Render.HudOverlay.TimeText(64 * 95 + 3), NetText.Time(64 * 95 + 3));
        Assert.Equal(NFS.Render.HudOverlay.TimeText(-5), NetText.Time(-5));
    }

    [Fact]
    public void TheCachesStayBounded()
    {
        for (int n = 0; n < 10_000; n++) Assert.Equal("v" + n, NetText.Join("v", NetText.Int(n)));      // far past the limit: still right
        Assert.Equal("v7", NetText.Join("v", NetText.Int(7)));
    }
}
