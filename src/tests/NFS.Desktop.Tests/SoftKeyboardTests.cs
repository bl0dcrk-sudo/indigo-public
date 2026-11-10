using NFS.Game.FrontEnd;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>On-screen keyboard (Android): show/hide hook at the start and end of text-field editing, and queued characters as from a keyboard.</summary>
[Collection(nameof(SoftKeyboardTests))]   // static queue
public sealed class SoftKeyboardTests
{
    [Fact]
    public void HookCalledOnlyOnChanges()
    {
        var calls = new List<bool>();
        var kb = new SoftKeyboard(calls.Add);
        kb.Update(false);
        kb.Update(true);
        kb.Update(true);
        kb.Update(false);
        kb.Update(false);
        Assert.Equal([true, false], calls);
        Assert.False(kb.Shown);
    }

    [Fact]
    public void WithoutPlatformHookNothingHappens()
    {
        var kb = new SoftKeyboard();
        kb.Update(true);                         // Desktop: SoftKeyboard.Platform == null
        Assert.True(kb.Shown);
    }

    [Fact]
    public void QueuedKeysBecomeTypedCharsAndEnter()
    {
        var input = new DesktopInput(default, [MenuKey.Up], false, Typed: ['x']);
        Assert.Equal(input, SoftKeyboard.Merge(input));      // empty queue – unchanged
        foreach (char c in "Ab\b\r") SoftKeyboard.Type(c);
        var merged = SoftKeyboard.Merge(input);
        Assert.Equal(['x', 'A', 'b', '\b'], merged.Typed);
        Assert.Equal([MenuKey.Up, MenuKey.Enter], merged.Menu);
        Assert.Equal(input, SoftKeyboard.Merge(input));      // queue emptied
    }

    [Fact]
    public void NetworkFieldsAndChatAreEditing()
    {
        using var host = new NetworkMenuHost(null, () => -1, "Alice");
        MenuController? menu = null;                         // in the network menu only the host counts
        Assert.False(SoftKeyboard.Editing(menu, host));
        host.Menu.Key(MenuKey.Enter);                        // first row: nickname
        Assert.True(SoftKeyboard.Editing(menu, host));
        host.Menu.Key(MenuKey.Enter);
        Assert.False(SoftKeyboard.Editing(menu, host));
    }

    [GameDataFact]
    public void NameEditorStartAndEndShowAndHideTheKeyboard()
    {
        var calls = new List<bool>();
        var kb = new SoftKeyboard(calls.Add);
        var loop = new GameLoop();
        var menu = loop.Menu.Controller;
        menu.Config.GameType = 1;
        kb.Update(SoftKeyboard.Editing(menu, null));
        NameEditorSceneTests.PressRace(loop);
        Assert.NotNull(menu.NameEditing);
        kb.Update(SoftKeyboard.Editing(menu, null));
        loop.Frame(0, new DesktopInput(default, [MenuKey.Escape], false));
        Assert.Null(menu.NameEditing);
        kb.Update(SoftKeyboard.Editing(menu, null));
        Assert.Equal([true, false], calls);
    }
}
