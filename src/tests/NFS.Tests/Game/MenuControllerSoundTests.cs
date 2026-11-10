using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Front-end UI sounds: [ref:3151]
/// (0 = change/OK, 1 = move, 2 = Enter with target, 3 = Esc, 6 = race start roar).</summary>
public class MenuControllerSoundTests
{
    private static MenuController Menu() => MenuController.Load(GameData.Dir);
    private static List<MenuSound> Record(MenuController m)
    {
        var sounds = new List<MenuSound>();
        m.Sound += sounds.Add;
        return sounds;
    }
    private static void Open(MenuController m, string text)
    {
        for (int i = 0; i < m.CurrentScreen.Items.Count; i++)
        {
            if (m.CurrentScreen.Items[m.SelectedIndex].Text == text) break;
            m.HandleKey(MenuKey.Down);
        }
        m.HandleKey(MenuKey.Enter);
    }

    [GameFact]
    public void UpDown_EmitsMove()
    {
        var m = Menu(); var sounds = Record(m);
        m.HandleKey(MenuKey.Down);
        m.HandleKey(MenuKey.Up);
        Assert.Equal([MenuSound.Move, MenuSound.Move], sounds);
    }

    [GameFact]
    public void Escape_EmitsBack()
    {
        var m = Menu(); var sounds = Record(m);
        m.HandleKey(MenuKey.Escape);
        Assert.Equal([MenuSound.Back], sounds);
    }

    [GameFact]
    public void Enter_ScreenTarget_EmitsSelect()
    {
        var m = Menu(); var sounds = Record(m);
        m.HandleKey(MenuKey.Down);          // "location"
        sounds.Clear();
        m.HandleKey(MenuKey.Enter);
        Assert.Equal([MenuSound.Select], sounds);
        Assert.Equal("location", m.CurrentScreen.Id);
    }

    [GameFact]
    public void LeftRight_ValueChange_EmitsChange()
    {
        var m = Menu(); Open(m, "location"); var sounds = Record(m);
        m.HandleKey(MenuKey.Left);
        m.HandleKey(MenuKey.Right);
        Assert.Equal([MenuSound.Change, MenuSound.Change], sounds);
        Assert.Equal(0, m.Config.Track);   // left, then right → back to 0
    }

    [GameFact]
    public void Enter_Race_EmitsStartRaceOnly()
    {
        var m = Menu(); var sounds = Record(m);
        for (int i = 0; i < m.CurrentScreen.Items.Count; i++)
        {
            if (m.CurrentScreen.Items[m.SelectedIndex].Text == "RACE") break;
            m.HandleKey(MenuKey.Down);
        }
        sounds.Clear();
        m.HandleKey(MenuKey.Enter);
        Assert.Equal([MenuSound.StartRace], sounds);
        Assert.Equal(MenuStatus.Race, m.Status);
    }

    [GameFact]
    public void RoarLoopActive_OnSpeechAndSfxSliders()
    {
        var m = Menu();
        Open(m, "options");
        m.HandleKey(MenuKey.Down);       // "sound" (position 1)
        m.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.AudioScreen, m.CurrentScreen.Id);
        Assert.False(m.RoarLoopActive);  // position 0 = music
        m.HandleKey(MenuKey.Down);
        Assert.True(m.RoarLoopActive);   // position 1 = speech
        m.HandleKey(MenuKey.Down);
        Assert.True(m.RoarLoopActive);   // position 2 = SFX
        m.HandleKey(MenuKey.Down);
        Assert.False(m.RoarLoopActive);  // position 3
    }
}
