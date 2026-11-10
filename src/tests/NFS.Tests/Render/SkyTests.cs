using System.Numerics;
using NFS.Data;
using NFS.Data.Track;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>
/// Sky of the original game: a ring of 8 pixmap textures (mirrored copy) from TRxx0.QFS around the camera [ref:3301]
/// a flat sky-colour cone above, a flat horizon-base-colour cone below the pixmap base line (HRZ). The camera here hangs
/// 5 km above the track so the whole frame is sky (the track projects far below the screen).
/// </summary>
public class SkyTests
{
    public static TheoryData<int> Tracks() => new(0, 2, 3, 4, 5, 6, 7);

    /// <summary>A slice whose tunnel test (slice −8) and neighbours' are false, so the sky is drawn (TR05 slice 0 is [ref:3302]
    /// 8 slices after a tunnel).</summary>
    public static int OpenSlice(TrackRenderer r)
    {
        for (int i = 1; i < r.Slices.Length - 1; i++)
            if (!r.IsTunnelNear(i - 1, 0) && !r.IsTunnelNear(i, 0) && !r.IsTunnelNear(i + 1, 0)) return i;
        throw new InvalidOperationException("no open slice");
    }

    static Camera SkyCamera(TrackRenderer r, double yawDeg = 0, double pitchDeg = 0)
    {
        var s = r.Slices[OpenSlice(r)];
        var fw = Camera.Vec(s.Forward); fw.Y = 0; fw = Vector3.Normalize(fw);
        double a = yawDeg * Math.PI / 180, p = pitchDeg * Math.PI / 180;
        var dir = new Vector3((float)(fw.X * Math.Cos(a) + fw.Z * Math.Sin(a)), (float)Math.Tan(p),
            (float)(fw.Z * Math.Cos(a) - fw.X * Math.Sin(a)));
        var cam = new Camera { Position = Camera.SliceCentre(s) + new Vector3(0, 5000, 0) };
        cam.LookAlong(dir, Vector3.UnitY);
        return cam;
    }

    static FrameBuffer Frame(TrackRenderer r, Camera cam)
    {
        var fb = new FrameBuffer();
        r.Render(fb, cam, new RenderOptions { Objects = false });
        return fb;
    }

    static HashSet<uint> SkyTextureColours(int track)
    {
        var fsh = Fsh.Load($"GAMEDATA/TRACKS/PC/TR{track:00}0.QFS", GameData.Dir);
        var pal = fsh.GlobalPalette();
        var set = new HashSet<uint>();
        for (int i = 0; i < 8; i++)
            foreach (var c in Texture.FromFsh(fsh, fsh.Entries[i], pal)!.Rgb) set.Add(c);
        return set;
    }

    /// <summary>Row of the pixmap (above the horizon line) with the most distinct colours.</summary>
    static int BusiestRow(FrameBuffer fb, int hy)
    {
        int best = hy - 5, bestN = 0;
        for (int y = 0; y < Math.Min(fb.Height, hy - 3); y++)
        {
            var set = new HashSet<uint>();
            for (int x = 0; x < fb.Width; x++) set.Add(fb.Get(x, y) & 0xFFFFFF);
            if (set.Count > bestN) { bestN = set.Count; best = y; }
        }
        return best;
    }

    static int HorizonRow(Hrz h, Camera cam) =>
        (int)Math.Round(cam.Cy + (h.BaseHeight + h.PixmapBasePercent / 100.0 * h.Height) * cam.Fy / h.FlatDistance);

    [GameTheory]
    [MemberData(nameof(Tracks))]
    public void Sky_IsNotAFlatGradient_AndComesFromTheTrackTextures(int t)
    {
        var r = new TrackRenderer(t, GameData.Dir);
        var own = SkyTextureColours(t);
        int maxDistinct = 0, inOwn = 0, total = 0;
        for (int yaw = 0; yaw < 360; yaw += 45) // the panorama is not uniform in every direction
        {
            var cam = SkyCamera(r, yaw);
            var fb = Frame(r, cam);
            int hy = HorizonRow(r.Hrz!, cam);
            int row = BusiestRow(fb, hy);
            var seen = new HashSet<uint>();
            for (int x = 0; x < fb.Width; x++) { uint c = fb.Get(x, row) & 0xFFFFFF; seen.Add(c); total++; if (own.Contains(c)) inOwn++; }
            maxDistinct = Math.Max(maxDistinct, seen.Count);
        }
        Assert.True(maxDistinct >= 10, $"TR{t:00}: at most {maxDistinct} distinct colours in a pixmap row (flat gradient?)");
        Assert.True(inOwn >= total * 0.9, $"TR{t:00}: only {inOwn}/{total} pixels from the track's sky textures");
    }

    [GameTheory]
    [MemberData(nameof(Tracks))]
    public void Sky_Colours_AboveAndBelowThePixmap_FollowTheHrz(int t)
    {
        var r = new TrackRenderer(t, GameData.Dir);
        var h = r.Hrz!;
        var cam = SkyCamera(r);
        var fb = Frame(r, cam);
        uint sky = (uint)(h.SkyRgb[0] << 16 | h.SkyRgb[1] << 8 | h.SkyRgb[2]);
        uint hor = (uint)(h.HorizonRgb[0] << 16 | h.HorizonRgb[1] << 8 | h.HorizonRgb[2]);
        int hy = HorizonRow(h, cam);
        Assert.InRange(hy, 100, 470);
        for (int x = 0; x < fb.Width; x += 37)
        {
            Assert.Equal(sky, fb.Get(x, 0) & 0xFFFFFF);                 // above the pixmap: flat sky colour
            Assert.Equal(hor, fb.Get(x, hy + 3) & 0xFFFFFF);            // below the pixmap base line: horizon colour
            Assert.Equal(hor, fb.Get(x, fb.Height - 1) & 0xFFFFFF);
            Assert.NotEqual(hor, fb.Get(x, hy - 3) & 0xFFFFFF);         // just above the line: still pixmap, not the ground colour
        }
    }

    [GameTheory]
    [MemberData(nameof(Tracks))]
    public void Sky_Pixmap_ScrollsWithYaw_AndHorizonFollowsPitch(int t)
    {
        var r = new TrackRenderer(t, GameData.Dir);
        var h = r.Hrz!;
        var cam0 = SkyCamera(r);
        var a = Frame(r, cam0);
        int hy = HorizonRow(h, cam0), diff = 0, n = 0;
        var b = Frame(r, SkyCamera(r, yawDeg: 75));
        for (int y = 0; y < hy - 5; y += 3)
            for (int x = 0; x < a.Width; x += 4) { n++; if (a.Get(x, y) != b.Get(x, y)) diff++; }
        Assert.True(diff > n * 0.1, $"TR{t:00}: sky barely changed after a 75° turn ({diff}/{n})");
        // a full turn returns to the same picture
        Assert.Equal(a.Sha256(), Frame(r, SkyCamera(r, yawDeg: 360)).Sha256());

        // pitching the camera down by 10° moves the horizon (pixmap base line) up the screen
        var fbUp = Frame(r, SkyCamera(r, 0, -10));
        uint hor = (uint)(h.HorizonRgb[0] << 16 | h.HorizonRgb[1] << 8 | h.HorizonRgb[2]);
        int first = 0;
        for (int y = 0; y < fbUp.Height; y++) if ((fbUp.Get(320, y) & 0xFFFFFF) == hor) { first = y; break; }
        Assert.True(first > 0 && first < hy - 20, $"TR{t:00}: horizon at {first} after pitch down, was {hy}");
    }

    [GameFact]
    public void BlackHorizonTrack_IsBlack()
    {
        var r = new TrackRenderer(8, GameData.Dir);
        Assert.Equal(1, r.Hrz!.BlackHorizon);
        var fb = Frame(r, SkyCamera(r));
        Assert.Equal(0u, fb.Get(100, 100) & 0xFFFFFF);
        Assert.Equal(0u, fb.Get(500, 300) & 0xFFFFFF);
    }
}
