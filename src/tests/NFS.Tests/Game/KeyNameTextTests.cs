using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Key names on the control screen. [ref:3146] [ref:3489]
/// the device label is TEXT 0x11D “keyboard”; the name is the TEXT.ENG record whose id comes from the scancode → id table
/// (0x39→0x121 … 0x4C→0x12F), or `sprintf("%c", toupper(ascii))` (toupper); no [ref:3147]
/// assignment (device 0) is TEXT 0x133 “________”. The id codes are not in the original's table – they exist only in the text records.</summary>
public class KeyNameTextTests
{
    /// <summary>Pairs of scancode → text id from (the original: 0x39→0x121, 0x1C→0x122, 0x52→0x123 [ref:3148]
    /// 0x53→0x124, 0x47→0x125, 0x4F→0x126, 0x49→0x127, 0x51→0x128, 0x48→0x129, 0x50→0x12A, 0x4B→0x12B,
    /// 0x4D→0x12C, 0x0E→0x12D, 0x0F→0x12E, 0x4C→0x12F).</summary>
    static readonly (int Scan, int TextId)[] SpecialKeys =
    [
        (0x39, 0x121), (0x1C, 0x122), (0x52, 0x123), (0x53, 0x124), (0x47, 0x125), (0x4F, 0x126),
        (0x49, 0x127), (0x51, 0x128), (0x48, 0x129), (0x50, 0x12A), (0x4B, 0x12B), (0x4D, 0x12C),
        (0x0E, 0x12D), (0x0F, 0x12E), (0x4C, 0x12F)
    ];

    [GameFact]
    public void KeyNameForKnownScancodes_IsTheTextRecordOfTheIdTheExeMapsTo()
    {
        var text = TextFile.Load("ENG", GameData.Dir);

        // Each scancode in the table gives the name = the TEXT.ENG record with the given id, and the label is always “keyboard”.
        foreach (var (scan, id) in SpecialKeys)
        {
            var (device, name) = KeyBindings.Display(KeyBindings.Word(scan, 0), text);
            Assert.Equal(text[0x11D], device);
            Assert.Equal(text[id], name);
            Assert.NotEqual(text[0x133], name);
        }

        // Spelling check: ids 0x121..0x12F are exactly these names, in the order of the original.
        string[] expected =
        [
            "space", "enter", "insert", "delete", "home", "end", "page up", "page down",
            "up", "down", "left", "right", "backspace", "tab", "center"
        ];
        Assert.Equal(expected, SpecialKeys.Select(p => text[p.TextId]));

        // A key without its own id: “%c” from the ASCII stored in the word, passed through toupper. [ref:3149]
        Assert.Equal((text[0x11D], "B"), KeyBindings.Display(KeyBindings.Word(0x30, 'B'), text));
        Assert.Equal((text[0x11D], "B"), KeyBindings.Display(KeyBindings.Word(0x30, 'b'), text));
        Assert.Equal((text[0x11D], ""), KeyBindings.Display(KeyBindings.Word(0x57, 0), text));

        // No assignment (device 0): “________” without a device label.
        Assert.Equal((null, text[0x133]), KeyBindings.Display(0, text));
    }
}
