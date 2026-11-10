using NFS.Game.FrontEnd;

namespace NFS.Tests.Game;

/// <summary>Front-end modal dialogs: file name field, message. [ref:3134]</summary>
public class FrontEndDialogTests
{
    static FrontEndDialog Confirm() => FrontEndDialog.Confirm(0x2F5, "Yy");
    static FrontEndDialog List(int n) => FrontEndDialog.List(Enumerable.Range(0, n).Select(i => $"file{i}").ToArray());

    [Fact]
    public void ConfirmStartsOnNoAndEnterKeepsTheSelection()
    {
        var d = Confirm();
        Assert.False(d.Yes);
        d.Key(MenuKey.Enter);
        Assert.True(d.Done); Assert.Equal(0, d.Result);
    }

    [Fact]
    public void ConfirmArrowsToggleAndEnterTakesTheCurrentAnswer()
    {
        var d = Confirm();
        d.Key(MenuKey.Right); Assert.True(d.Yes); Assert.False(d.Done);
        d.Key(MenuKey.Left); Assert.False(d.Yes);
        d.Key(MenuKey.Left); Assert.True(d.Yes);
        d.Key(MenuKey.Enter);
        Assert.Equal(1, d.Result);
    }

    [Fact]
    public void ConfirmEscapeAndNAnswerNoAndTheLanguageLetterAnswersYes()
    {
        var d = Confirm(); d.Key(MenuKey.Right); d.Key(MenuKey.Escape);
        Assert.True(d.Done); Assert.Equal(0, d.Result);
        d = Confirm(); d.Char('n');
        Assert.Equal(0, d.Result); Assert.True(d.Done);
        d = Confirm(); d.Char('Y');
        Assert.Equal(1, d.Result); Assert.True(d.Yes);
        d = Confirm(); d.Char('q');
        Assert.False(d.Done);
        d = FrontEndDialog.Confirm(0x2F5, "Jj"); d.Char('y');                  // niemiecki: tak = J
        Assert.False(d.Done);
        d.Char('j');
        Assert.Equal(1, d.Result);
    }

    [Fact]
    public void MessageClosesOnAnyKeyOrAfterTheTimeout()
    {
        var d = FrontEndDialog.Message(0x2ED); d.Key(MenuKey.Down);
        Assert.True(d.Done);
        d = FrontEndDialog.Message(0x2ED); d.Char('x');
        Assert.True(d.Done);
        d = FrontEndDialog.Message(0x2ED);
        d.Advance(FrontEndDialog.MessageTicks - 1); Assert.False(d.Done);
        d.Advance(1); Assert.True(d.Done);
    }

    [Fact]
    public void ListNavigationStopsAtTheEndsAndScrollsWithTheSelection()
    {
        var d = List(10);
        Assert.Equal(0, d.Selected);
        d.Key(MenuKey.Up); Assert.Equal(0, d.Selected);
        for (int i = 0; i < 7; i++) d.Key(MenuKey.Down);
        Assert.Equal(7, d.Selected); Assert.Equal(0, d.Scroll); Assert.False(d.CanScrollUp); Assert.True(d.CanScrollDown);
        d.Key(MenuKey.Down); d.Key(MenuKey.Down);
        Assert.Equal(9, d.Selected); Assert.Equal(2, d.Scroll); Assert.True(d.CanScrollUp); Assert.False(d.CanScrollDown);
        d.Key(MenuKey.Down); Assert.Equal(9, d.Selected);
        for (int i = 0; i < 9; i++) d.Key(MenuKey.Up);
        Assert.Equal(0, d.Selected); Assert.Equal(0, d.Scroll);
    }

    [Fact]
    public void ListLeftRightJumpToCancelAndBackToTheRememberedRow()
    {
        var d = List(5);
        d.Key(MenuKey.Down); d.Key(MenuKey.Down);
        d.Key(MenuKey.Right); Assert.Equal(-1, d.Selected);
        d.Key(MenuKey.Down); Assert.Equal(-1, d.Selected);                     // on “cancel” the vertical arrows do nothing
        d.Key(MenuKey.Left); Assert.Equal(2, d.Selected);
        d.Key(MenuKey.Enter);
        Assert.Equal(2, d.Result);
    }

    [Fact]
    public void ListEnterOnCancelAndEscapeReturnMinusOneAndAnEmptyListStartsOnCancel()
    {
        var d = List(3); d.Key(MenuKey.Left); d.Key(MenuKey.Enter);
        Assert.Equal(-1, d.Result);
        d = List(3); d.Key(MenuKey.Down); d.Key(MenuKey.Escape);
        Assert.Equal(-1, d.Result);
        d = List(0);
        Assert.Equal(-1, d.Selected);
        d.Key(MenuKey.Enter);
        Assert.Equal(-1, d.Result);
    }

    static FrontEndDialog Entry(string initial = "name") => FrontEndDialog.TextEntry(0x2E1, initial);
    static void Type(FrontEndDialog d, string s) { foreach (char c in s) d.Char(c); }

    [Fact]
    public void TextEntryFirstCharacterReplacesTheDefaultNameAndEnterAcceptsIt()
    {
        var d = Entry();
        Assert.Equal("name", d.Entry);
        Type(d, "ab");
        Assert.Equal("ab", d.Entry);
        d.Key(MenuKey.Enter);
        Assert.True(d.Done); Assert.Equal(1, d.Result);
    }

    [Fact]
    public void TextEntryBackspaceOnTheDefaultClearsItAndLaterBackspaceDeletesOneCharacter()
    {
        var d = Entry(); d.Char(8);
        Assert.Equal("", d.Entry);
        Type(d, "xyz"); d.Char(8);
        Assert.Equal("xy", d.Entry);
        d.Char(8); d.Char(8); d.Char(8);
        Assert.Equal("", d.Entry);
        d.Key(MenuKey.Enter);
        Assert.Equal(0, d.Result);                                              // empty name = cancel [ref:3135]
    }

    [Fact]
    public void TextEntryRejectsFileNameCharactersAndStopsAtEight()
    {
        var d = Entry();
        Type(d, "a b*c/d?e<f>g\"h:i\\j|k.l");
        Assert.Equal("abcdefgh", d.Entry);                                      // znaki ` */?<>":\|.` odrzucone, limit 8
        Type(d, "zzz");
        Assert.Equal("abcdefgh", d.Entry);
    }

    [Fact]
    public void TextEntryEscapeCancelsAndTheCursorBlinksEveryFifteenTicks()
    {
        var d = Entry(); Type(d, "abc"); d.Key(MenuKey.Escape);
        Assert.True(d.Done); Assert.Equal(0, d.Result);
        d = Entry();
        Assert.False(d.CursorVisible);
        d.Advance(0xE); Assert.False(d.CursorVisible);
        d.Advance(1); Assert.True(d.CursorVisible);
        d.Advance(0xF); Assert.False(d.CursorVisible);
    }
}
