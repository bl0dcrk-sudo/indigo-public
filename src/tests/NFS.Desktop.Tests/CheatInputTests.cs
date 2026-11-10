using Microsoft.Xna.Framework.Input;
using NFS.Desktop;

namespace NFS.Desktop.Tests;

/// <summary>Cheat codes from the menu, from the host's keyboard to the configuration: key characters, typing in the loop, saving of the unlock. [ref:2730]</summary>
public class CheatInputTests
{
    [Fact]
    public void LettersAndDigits_BecomeCharsOnTheRisingEdgeOnly()
    {
        var mapper = new InputMapping();
        Assert.Equal(['F'], mapper.Read(new KeyboardState(Keys.F), default).Chars);
        Assert.Empty(mapper.Read(new KeyboardState(Keys.F), default).Chars!);                 // a held key does not repeat
        Assert.Equal(['2', '7'], mapper.Read(new KeyboardState(Keys.D2, Keys.NumPad7), default).Chars);
        Assert.Empty(mapper.Read(new KeyboardState(Keys.Up, Keys.Enter), default).Chars!);    // menu keys are not characters
    }

    [GameDataFact]
    public void TypedCode_ReachesTheConfig_AndUnlockIsSavedImmediately()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-cheat-loop-").FullName;
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var loop = new GameLoop(null, false, path);
            foreach (char ch in "FZR2000") loop.Frame(0, new DesktopInput(default, [], false, [ch]));
            Assert.Equal(3, loop.Menu.Controller.Config[0x1D]);
            var saved = UserSettings(path);
            Assert.Equal(3, saved[0x1D]);                          // PersistRequested: without waiting for the game to exit
            Assert.Equal(0xE, saved.P1Car);
        }
        finally { Directory.Delete(dir, true); }
    }

    static NFS.Data.FrontEnd.FeConfig UserSettings(string path)
    {
        var c = new NFS.Data.FrontEnd.FeConfig(new byte[NFS.Data.FrontEnd.FeConfig.Size]);
        NFS.Game.FrontEnd.UserSettings.Load(path).ApplyTo(c);
        return c;
    }
}
