using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class TrackVisibilityTests
{
    // Actual chase/cockpit cameras captured by the 0f sweep. At the crossings
    // another road is closer in XZ but lies over 100 m above/below this road.
    [GameTheory]
    [InlineData(6, 1128, false, -49.633816f, -210.37341f, 264.79538f,
        -0.19611685f, -0.38391653f, -0.90230054f, -0.08154118f, 0.92336786f, -0.37515724f)]
    [InlineData(6, 1128, true, -49.7733f, -212.02388f, 264.98892f,
        -0.15008631f, -0.13428774f, -0.9795106f, -0.040077694f, 0.99074477f, -0.12968698f)]
    [InlineData(7, 1420, false, -372.03183f, 165.79785f, -405.13055f, 0.90844107f, -0.38436142f, -0.16432017f, 0.37822384f, 0.9231827f, -0.06841369f)]
    public void CrossingKeepsTheRoadInTheLowerHalf(int trackNo, int approachSlice, bool cockpit,
        float x, float y, float z, float fx, float fy, float fz, float ux, float uy, float uz)
    {
        var track = new TrackRenderer(trackNo, GameData.Dir);
        var picture = new FrameBuffer();
        var options = new RenderOptions { Sky = false, Objects = false };
        // Approach on the same continuous road, as the view locator does in the original. [ref:3310]
        track.Render(picture, track.CameraAtSlice(approachSlice), options);
        var camera = new Camera(centreYOffset: cockpit ? -40 : 0) { Position = new(x, y, z) };
        camera.LookAlong(new(fx, fy, fz), new(ux, uy, uz));
        track.Render(picture, camera, options);
        AssertRoad(picture, trackNo);
    }

    [GameFact]
    public void MirrorDoesNotMoveTheMainViewOntoTheOtherLevelOfTheCrossing()
    {
        var track = new TrackRenderer(7, GameData.Dir);
        var picture = new FrameBuffer();
        var options = new RenderOptions { Sky = false, Objects = false };
        track.Render(picture, track.CameraAtSlice(1420), options);
        track.Render(picture, track.CameraAtSlice(1274), options, rearMirror: true);
        var camera = new Camera { Position = new(-372.03183f, 165.79785f, -405.13055f) };
        camera.LookAlong(new(0.90844107f, -0.38436142f, -0.16432017f),
            new(0.37822384f, 0.9231827f, -0.06841369f));
        track.Render(picture, camera, options);
        AssertRoad(picture, 7);
    }

    static void AssertRoad(FrameBuffer picture, int trackNo)
    {
        int road = 0;
        for (int p = picture.Width * (picture.Height / 2) * 4; p < picture.Rgba.Length; p += 4)
            if ((picture.Rgba[p] | picture.Rgba[p + 1] | picture.Rgba[p + 2]) != 0) road++;
        int pixels = picture.Width * picture.Height / 2;
        Assert.True(road > pixels * 0.9, $"TR{trackNo:00}: road covers {road}/{pixels} lower-half pixels.");
    }
}
