using NFS.Desktop;

namespace NFS.Desktop.Tests;

public class ResetBlinkTests
{
    [Fact]
    public void WithoutCooldownTheCarIsAlwaysDrawn()
    {
        var b = new ResetBlink();
        for (int t = 1; t <= 20; t++) Assert.True(b.Visible(0, t, false));
    }

    [Fact]
    public void CooldownSkipsEveryOtherRaceTickAsInAsm()
    {
        var b = new ResetBlink();
        // counter starts at 0 and increments on each new tick: tick1 -> 1 (skip), tick2 -> 2 (draw), ...
        for (int t = 1; t <= 10; t++) Assert.Equal(t % 2 == 0, b.Visible(0x30, t, false));
    }

    [Fact]
    public void RepeatedFramesInTheSameTickKeepTheSameVisibility()
    {
        var b = new ResetBlink();
        bool first = b.Visible(0x30, 5, false);
        for (int i = 0; i < 4; i++) Assert.Equal(first, b.Visible(0x30, 5, false));
    }

    [Fact]
    public void PausedAlwaysDraws()
    {
        var b = new ResetBlink();
        for (int t = 1; t <= 6; t++) Assert.True(b.Visible(0x30, t, true));
    }
}
