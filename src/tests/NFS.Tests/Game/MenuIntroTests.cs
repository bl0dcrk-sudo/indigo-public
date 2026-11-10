using NFS.Game.FrontEnd;
using NFS.Tests.Data;

namespace NFS.Tests.Game;

/// <summary>The list panel slide-in, the vertical “cancel” step and the track description screen slide-in. [ref:3156]</summary>
public class MenuIntroTests
{
    [Fact]
    public void ListPanelSlidesOutByTwelvePixelsUntilPastTwoHundredTen()
    {
        var exit = FrontEndDialog.ListExitFrames();               // 0, 12, … 204, then 216 > 0xD2 ends the loop [ref:3157]
        Assert.Equal(18, exit.Count);
        Assert.Equal(0, exit[0]);
        Assert.Equal(204, exit[^1]);
        Assert.All(exit.Zip(exit.Skip(1)), p => Assert.Equal(12, p.Second - p.First));
    }

    [GameFact]
    public void InputBlockGateMakesTheMenuIgnoreKeys()
    {
        var menu = MenuController.Load(null);
        int before = menu.SelectedIndex;
        bool blocked = true;
        menu.InputBlocked = () => blocked;
        menu.HandleKey(MenuKey.Down);
        Assert.Equal(before, menu.SelectedIndex);
        blocked = false;
        menu.HandleKey(MenuKey.Down);
        Assert.NotEqual(before, menu.SelectedIndex);
    }

    [Fact]
    public void ListPanelSlidesInByEightPixelsThenTheTabByFourWithoutReachingZeroEarly()
    {
        var frames = FrontEndDialog.ListIntroFrames(202);       // width of “bsld”
        var panel = frames.TakeWhile(f => f.TabOffset is null).ToArray();
        var tab = frames.Skip(panel.Length).ToArray();
        Assert.Equal(26, panel.Length);                         // 202, 194, … 2: the original stops at a negative offset
        Assert.Equal(202, panel[0].PanelOffset);
        Assert.Equal(2, panel[^1].PanelOffset);
        Assert.All(panel.Zip(panel.Skip(1)), p => Assert.Equal(8, p.First.PanelOffset - p.Second.PanelOffset));
        Assert.Equal(11, tab.Length);                           // 0x28, 0x24, … 0
        Assert.Equal(0x28, tab[0].TabOffset);
        Assert.Equal(0, tab[^1].TabOffset);
        Assert.All(tab, f => Assert.Equal(0, f.PanelOffset));   // panel stoi na miejscu
        Assert.All(tab.Zip(tab.Skip(1)), p => Assert.Equal(4, p.First.TabOffset - p.Second.TabOffset));
    }

    [Fact]
    public void ListPanelOfMultipleOfEightEndsExactlyAtZero()
    {
        var panel = FrontEndDialog.ListIntroFrames(200).TakeWhile(f => f.TabOffset is null).ToArray();
        Assert.Equal(26, panel.Length);
        Assert.Equal(0, panel[^1].PanelOffset);
    }

    [Theory]
    [InlineData(39, 23)]    // SWISS18 (czcionka 0x12): 39 · 0.6 = 23.4
    [InlineData(28, 16)]    // SWISS13: 16.8
    [InlineData(79, 47)]    // SWISS36: 47.4
    [InlineData(35, 20)]    // double 0.6 is just below 3/5, so the product 21 − ε truncates to 20 (FRNDINT in truncation mode)
    [InlineData(0, 0)]
    public void CancelLetterStepIsTheTruncatedFontHeightTimesZeroPointSix(int height, int step) =>
        Assert.Equal(step, FrontEndDialog.VerticalLetterStep(height));

    [Fact]
    public void TrackInfoSlidesTheSliderThenFadesTheBackgroundInFiveSteps()
    {
        var frames = TrackInfoIntro.Frames(2);
        var slide = frames.TakeWhile(f => f.FadeLevel is null).ToArray();
        var fade = frames.Skip(slide.Length).ToArray();
        Assert.Equal(27, slide.Length);                          // x = −0x138 … 0 after 0xC
        Assert.Equal(-0x138, slide[0].PanelX);
        Assert.Equal(0, slide[^1].PanelX);
        Assert.All(slide.Zip(slide.Skip(1)), p => Assert.Equal(0xC, p.Second.PanelX - p.First.PanelX));
        Assert.Equal([0, 4, 8, 12, 16], fade.Select(f => f.FadeLevel!.Value));
        Assert.All(fade, f => Assert.Null(f.PanelX));            // panel already in place (flag 0x1000)
    }

    [Fact]
    public void TrackInfoWithoutDetailHasNoIntro() => Assert.Empty(TrackInfoIntro.Frames(0));
}
