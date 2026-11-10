using System.Numerics;
using NFS.Render;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>
/// TR00 side-bank artefact: steep bank quads seen almost edge-on project to a twisted (bow-tie) quad whose two fan
/// triangles have opposite orientation. A quad is culled only when BOTH triangles [ref:3311]
/// (1,2,3) and (1,3,4) face away, so such a quad must still be drawn (sky holes showed through the grass otherwise).
/// </summary>
public class TwistedQuadTests
{
    const uint Colour = 0x336622;

    static Vector3 View(Camera cam, double sx, double sy, double z) =>
        new((float)((sx - cam.Cx) * z / cam.Fx), (float)((sy - cam.Cy) * z / cam.Fy), (float)z);

    // Real quad from TR00 (drive-frames, 26 s, slice 1802): screen corners / depths of the strip that left a hole.
    static (Vector3 A, Vector3 B, Vector3 C, Vector3 D) Strip(Camera cam) => (
        View(cam, 103.7, 242.2, 29.14), View(cam, 60.4, 196.3, 29.18),
        View(cam, 38.7, 199.3, 32.78), View(cam, 125.6, 242.5, 32.73));

    [Fact]
    public void TwistedQuad_WithOneFrontFacingTriangle_IsDrawn()
    {
        var cam = new Camera();
        var (a, b, c, d) = Strip(cam);
        var fb = new FrameBuffer(); fb.Clear(0, 0, 0);
        Assert.Equal(1, TrackRenderer.DrawFlatQuad(fb, cam, a, b, c, d, Colour));
        int lit = 0;
        for (int y = 0; y < fb.Height; y++) for (int x = 0; x < fb.Width; x++) if (fb.Get(x, y) == Colour) lit++;
        Assert.True(lit > 100, $"lit {lit}");
        // inside the bottom lobe of the bow-tie (tip near (97,235)..(110,235)), the pixel that was a hole
        Assert.Equal(Colour, fb.Get(104, 238));
    }

    [Fact]
    public void BackFacingQuad_IsStillCulled_AndFrontFacingDrawn()
    {
        var cam = new Camera();
        var fb = new FrameBuffer(); fb.Clear(0, 0, 0);
        // clockwise / counter-clockwise rectangles at 10 m: one orientation is a back face
        var p = new[] { View(cam, 200, 200, 10), View(cam, 300, 200, 10), View(cam, 300, 300, 10), View(cam, 200, 300, 10) };
        int front = TrackRenderer.DrawFlatQuad(fb, cam, p[0], p[1], p[2], p[3], Colour);
        int back = TrackRenderer.DrawFlatQuad(fb, cam, p[3], p[2], p[1], p[0], Colour);
        Assert.Equal(1, front + back); // exactly one orientation is drawn
        Assert.Equal(0, TrackRenderer.DrawFlatQuad(fb, cam, p[3], p[2], p[1], p[0], Colour, cull: true) * (back == 0 ? 1 : 0));
    }

    [Fact]
    public void TriangleArea2EqualsPolygonArea2()
    {
        // backface test of DrawQuad uses the triangle overload: must give exactly the span version (same summation order)
        var a = new ScreenVertex(10.25, 3.5, 0, 0); var b = new ScreenVertex(-7.125, 40.75, 0, 0); var c = new ScreenVertex(33.3, -12.9, 0, 0);
        Assert.Equal(Rasterizer.Area2([a, b, c]), Rasterizer.Area2(in a, in b, in c));
        Assert.Equal(-Rasterizer.Area2(in a, in b, in c), Rasterizer.Area2(in a, in c, in b), 9);
        Assert.Equal(0, Rasterizer.Area2(ReadOnlySpan<ScreenVertex>.Empty));
    }
}
