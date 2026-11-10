using System.Numerics;
using NFS.Render;
using Xunit;

namespace NFS.Tests.Render;

public class DriverLabelsTests
{
    static Camera Camera() { var c = new Camera(); c.LookAlong(Vector3.UnitZ, Vector3.UnitY); return c; }

    static DriverLabels.Car Car(float x, float z, string text, bool bot = false) =>
        new(new Vector3(x, 0, z), Vector3.UnitY, text, bot);

    [Fact]
    public void NamesComeFromTheRoster_EverythingElseIsABot()
    {
        var names = new Dictionary<int, string> { [3] = "Anna", [4] = "   " };
        Assert.Equal("Anna", DriverLabels.NameOf(names, 3));
        Assert.Equal("[bot]", DriverLabels.NameOf(names, 4));   // blank entry: no player
        Assert.Equal("[bot]", DriverLabels.NameOf(names, 5));   // AI car
        Assert.Equal("[bot]", DriverLabels.NameOf(null, 0));
        Assert.True(DriverLabels.IsPlayer(names, 3));
        Assert.False(DriverLabels.IsPlayer(names, 4));
        Assert.False(DriverLabels.IsPlayer(null, 3));
    }

    [Fact]
    public void AccentedNamesAreFoldedToAsciiForTheBuiltInFont()
    {
        Assert.Equal("Zolw", DriverLabels.AsciiLabel("Żółw"));
        Assert.Equal("LUKASZ", DriverLabels.AsciiLabel("ŁUKASZ"));
        Assert.Equal("Soren", DriverLabels.AsciiLabel("Søren"));
        Assert.Equal("ANNA", DriverLabels.AsciiLabel("ANNA"));
        var label = Assert.Single(DriverLabels.Layout(Camera(), [Car(0, 50, "Żółw")]));
        Assert.Equal("Zolw", label.Text);
    }

    [Fact]
    public void LayoutCentresTheNameAboveTheCarRoof()
    {
        // Camera at the origin looking down +Z; the label sits HeightMetres up in the car's own up axis.
        var camera = Camera();
        var labels = DriverLabels.Layout(camera, [Car(0, 50, "ANNA")]);
        var label = Assert.Single(labels);
        Assert.Equal("ANNA", label.Text);
        Assert.False(label.Bot);
        // projection: x = 320, y = 240 − 1.025·Fy/50 = 233.2; text is 30 px wide and 10 px tall
        Assert.Equal((305, 223), (label.X, label.Y));
        Assert.InRange(label.Distance, 50f, 51f);
    }

    [Fact]
    public void LayoutSkipsCarsBehindTheCamera_FartherThanTheRange_OrOffScreen()
    {
        var camera = Camera();
        Assert.Empty(DriverLabels.Layout(camera, [Car(0, -50, "BEHIND")]));
        Assert.Empty(DriverLabels.Layout(camera, [Car(0, DriverLabels.MaxDistanceMetres + 10, "FAR")]));
        Assert.Empty(DriverLabels.Layout(camera, [Car(500, 20, "SIDE")]));
        Assert.Empty(DriverLabels.Layout(camera, [Car(0, 0.01f, "CLOSE")])); // text would not fit
    }

    [Fact]
    public void LayoutReturnsFarToNear_SoNearerNamesAreDrawnLast()
    {
        var camera = Camera();
        var labels = DriverLabels.Layout(camera, [Car(0, 20, "NEAR"), Car(0, 100, "FAR")]);
        Assert.Equal(["FAR", "NEAR"], labels.Select(l => l.Text));
    }

    [Fact]
    public void DrawBlendsPlayersWhiteAndBotsGreyOverTheScene_AndTheBotTagRendersItsBrackets()
    {
        var camera = Camera();
        var fb = new FrameBuffer();
        fb.Clear(0x10, 0x20, 0x30);
        DriverLabels.Draw(fb, camera, [Car(0, 50, "ANNA"), Car(20, 60, "[bot]", bot: true)]);

        // Glyphs are semi-transparent (TextAlpha = 140), so each texel is the colour mixed with the background.
        // 'A' row 0 has only the middle column set: (305 + 2, 223); white over 0x102030:
        Assert.Equal(0x939AA2u, fb.Get(307, 223));
        // '[' row 0 is a full three-pixel bar: top-left pixel of the bot text at (408, 224); grey 0xB0B0B0 over 0x102030:
        Assert.Equal(0x686F76u, fb.Get(408, 224));
        Assert.Equal(0x102030u, fb.Get(400, 400));
        Assert.NotEqual(DriverLabels.PlayerColor, fb.Get(307, 223)); // not the opaque glyph colour
        Assert.NotEqual(DriverLabels.BotColor, fb.Get(408, 224));
    }
}
