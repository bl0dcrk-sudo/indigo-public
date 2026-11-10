using NFS.Core;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>(option 0x40, RAIN code): zones, intensity, streak count, length, [ref:3112]
/// streak records. Without the option nothing exists. [ref:3113]</summary>
public class WeatherTests
{
    private static Weather Rain(int track, int seed = 0) => new(Weather.OptRain, track, new EaRandom(seed));

    [Fact]
    public void WithoutTheOptionThereIsNoWeather()
    {
        var w = new Weather(0x8 | 0x100 | 0x800 | 0x400, 5, new EaRandom());
        Assert.False(w.Enabled);
        Assert.Empty(w.Zones);
        w.Update(0x269, 2000, 0);
        Assert.Equal(0, w.Intensity); Assert.Equal(0, w.Type);
        Assert.Equal((0, 0), w.VisibleStreaks());
    }

    [Theory]
    [InlineData(1, 0)] [InlineData(2, 1)] [InlineData(3, 2)] [InlineData(4, 3)] [InlineData(6, 4)]
    public void OnlyTracks0_5_7HaveZones(int track, int _) => Assert.Empty(Rain(track).Zones);

    [Fact]
    public void Track5HasTheTwoFixedZonesAndOneRandom()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var z = Rain(5, seed).Zones;
            Assert.Equal(3, z.Count);
            Assert.Equal(new Weather.Zone(0x269, 2, 3), z[0]);
            Assert.Equal(new Weather.Zone(0x274, 5, 3), z[1]);
            Assert.Equal(1, z[2].Type);
            Assert.True(z[2].Center is >= 0x32 and <= 0x5AA);
        }
    }

    [Theory]
    [InlineData(0, 0x32, 0x3E8)]
    [InlineData(5, 0x32, 0x5AA)]
    [InlineData(7, 0xC8, 0x6F0)]
    public void RandomZonesStayInsideTheirRangesWithHalfWidth60To159(int track, int lo, int hi)
    {
        for (int seed = 0; seed < 50; seed++)
            foreach (var z in Rain(track, seed).Zones.Where(z => z.Type != 3 && z.Center != 0x6F0))
            {
                Assert.InRange(z.HalfWidth, 0x3C, 0x3C + 99);
                Assert.True(z.Center - z.HalfWidth >= lo - 2 && z.Center + z.HalfWidth <= hi + 2, $"{z}");
            }
    }

    [Fact]
    public void Track7HasTwoOrThreeZones_Track0One()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            Assert.Single(Rain(0, seed).Zones);
            int n = Rain(7, seed).Zones.Count;
            Assert.True(n is 2 or 3);
            if (n == 3) Assert.Equal(new Weather.Zone(0x6F0, 2, 2), Rain(7, seed).Zones[2]);
        }
    }

    [Fact]
    public void ZonesAreDeterministicPerSeed() =>
        Assert.Equal(Rain(7, 42).Zones, Rain(7, 42).Zones);

    [Fact]
    public void Intensity_IsOneAtTheCentreFallsLinearlyAndIsZeroOutside()
    {
        var w = Rain(0, 3);
        var z = w.Zones[0];
        w.Update(z.Center, 2000, 0);
        Assert.Equal(0x10000, w.Intensity); Assert.Equal(z.Type, w.Type);
        w.Update(z.Center + z.HalfWidth / 2, 2000, 0);
        Assert.Equal((int)(((long)(z.HalfWidth - z.HalfWidth / 2) << 16) / z.HalfWidth), w.Intensity);
        w.Update(z.Center + z.HalfWidth, 2000, 0);
        Assert.Equal(0, w.Intensity); Assert.Equal(0, w.Type);
        w.Update(z.Center + z.HalfWidth + 500, 2000, 0);
        Assert.Equal(0, w.Intensity);
    }

    [Theory]
    [InlineData(0x20)] [InlineData(0x02)] [InlineData(0x22)]
    public void Intensity_NoRainInTunnels(byte surroundings)
    {
        var w = Rain(0, 3);
        w.Update(w.Zones[0].Center, 2000, surroundings);
        Assert.Equal(0, w.Intensity);
    }

    [Fact]
    public void Intensity_WrapsAroundTheLapEnd()
    {
        var w = Rain(0, 1);                       // a single zone: always the nearest
        int centre = w.Zones[0].Center, half = w.Zones[0].HalfWidth;
        int lap = centre + half - 1;              // lap end inside the zone: slice 0 is half-1 slices past the centre (circular)
        w.Update(0, lap, 0);
        Assert.Equal((int)(0x10000L / half), w.Intensity);
        w.Update(0, centre + half + 50, 0);       // lap end beyond the zone: plain |0 - centre| >= half
        Assert.Equal(0, w.Intensity);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0x1FFF, 0)] [InlineData(0x2000, 1)] [InlineData(0x10000, 8)] [InlineData(0xF000, 7)]
    public void VisibleStreaksFollowEighthsOfIntensity(int intensity, int level)
    {
        var w = Rain(0, 3);
        var z = w.Zones[0];
        // intensity (half - d)/half: pick d so that the result is just the requested one (rounded down)
        int d = z.HalfWidth - (int)(((long)intensity * z.HalfWidth + 0xFFFF) >> 16);
        w.Update(z.Center + Math.Max(d, 0), 2000, 0);
        int k = (int)((long)w.Intensity * 8 >> 16);
        var (first, count) = w.VisibleStreaks();
        if (w.Intensity == 0) { Assert.Equal((0, 0), (first, count)); return; }
        Assert.Equal(k * 5, count);
        Assert.Equal((8 - k) * 5, first);
        Assert.Equal(40, first + count);
        _ = level;
    }

    [Fact]
    public void StreakLen_ClampsAndDependsOnTheCameraComponent()
    {
        Assert.Equal(2, Weather.StreakLen(0, 0x10000));
        Assert.Equal(5, Weather.StreakLen(1, 0));                   // (0 + 7)/2 + 2
        Assert.Equal(9, Weather.StreakLen(1, 0x10000));             // (7 + 7)/2 + 2 = 9
        Assert.Equal(2, Weather.StreakLen(1, -0x40000));            // clamp low
        Assert.Equal(5, Weather.StreakLen(2, 0x10000));             // clamp 5 for other types
        Assert.Equal(2, Weather.StreakLen(2, -0x40000));
    }

    [Fact]
    public void ResetStreaks_ArePseudoRandomAndInRange()
    {
        var w = Rain(0, 9);
        Assert.All(w.Streaks, s => { Assert.InRange((int)s.Y, 0, 0x1DF); Assert.InRange((int)s.Speed, 2, 9); });
        Assert.True(w.Streaks.Select(s => s.Y).Distinct().Count() > 20);
    }

    [Fact]
    public void UpdateStreaks_MoveDownBy3xSpeedAndKeepLinesConsistentAndDeterministic()
    {
        Weather Make()
        {
            var w = Rain(0, 5);
            var z = w.Zones[0];
            w.Update(z.Center, 2000, 0);          // full intensity, type 1
            return w;
        }
        var a = Make(); var b = Make();
        var (first, count) = a.VisibleStreaks();
        Assert.Equal((0, 40), (first, count));
        short y0 = a.Streaks[3].Y, sp = a.Streaks[3].Speed;
        a.UpdateStreaks(first, count, 0x100000, 0x8000, 0x4000);
        b.UpdateStreaks(first, count, 0x100000, 0x8000, 0x4000);
        Assert.Equal(a.Streaks, b.Streaks);
        // rain line: from (x0,y) down to y + speed/2; the record then falls by 3 * speed (when it did not respawn)
        var s = a.Streaks[3];
        Assert.Equal(sp / 2, s.Y1 - s.Y0);
        if (s.Y == y0 + 3 * sp) Assert.Equal(y0, s.Y0);                 // no respawn: moved on by 3 * speed
        else Assert.InRange((int)s.Y, 0, 0x1F + 3 * 9);                  // respawned at the top (y 0..31) and moved by 3 * new speed
    }

    [Fact]
    public void UpdateStreaks_RespawnAtTheTopWhenTheyLeaveTheView()
    {
        var w = Rain(0, 5);
        w.Update(w.Zones[0].Center, 2000, 0);
        for (int i = 0; i < 400; i++) w.UpdateStreaks(0, 40, 0x400000, 0, 0);
        Assert.All(w.Streaks, s => Assert.InRange((int)s.Y, 0, 0x168 + 40));
    }

    [Fact]
    public void UpdateStreaks_DoesNothingWithoutWeather()
    {
        var w = new Weather(0, 0, new EaRandom());
        w.UpdateStreaks(0, 40, 0, 0, 0);
        Assert.All(w.Streaks, s => Assert.Equal(default, s));
    }

    // ---- windscreen drops ---- [ref:3114]

    private static Weather InZone(int track, int seed, int dashType = -1)
    {
        var w = new Weather(Weather.OptRain, track, new EaRandom(seed), dashType);
        w.Update(w.Zones[0].Center, 5000, 0);   // full intensity at the zone centre
        return w;
    }

    [Fact]
    public void Drops_StartInTheWindscreenArea_EveryFifthIsLarge_AndAreDeterministic()
    {
        var a = Rain(0, 5);
        var b = Rain(0, 5);
        Assert.Equal(Weather.DropTotal, a.Drops.Length);
        for (int i = 0; i < Weather.DropTotal; i++)
        {
            Assert.InRange(a.Drops[i].X, 100, 599); Assert.InRange(a.Drops[i].Y, 0x50, 0x50 + 199);
            Assert.InRange((int)a.Drops[i].Age, 0xB4 - 99, 0xB4);
            Assert.Equal(i % 5 == 0, a.Drops[i].Large);
            Assert.Equal(a.Drops[i], b.Drops[i]);
        }
        Assert.NotEqual(Rain(0, 6).Drops[0], a.Drops[0]);
        var dash5 = Rain(0, 5);   // default dash type: same stream, other area only with type 5
        var w5 = new Weather(Weather.OptRain, 0, new EaRandom(5), 5);
        Assert.All(w5.Drops, d => { Assert.InRange(d.X, 150, 649); Assert.InRange(d.Y, 0x5F, 0x5F + 0xB8); });
        Assert.Equal(dash5.Drops[7].X - 100, w5.Drops[7].X - 150);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0x10000, 100)] [InlineData(0x8000, 50)] [InlineData(0x1999, 9)]
    public void VisibleDrops_AreTheLastIntensityTimes100(int intensity, int count)
    {
        var w = InZone(0, 1);
        int half = w.Zones[0].HalfWidth;
        // pick the distance so that intensity is (just) the requested value
        int d = half - (int)(((long)intensity * half + 0xFFFF) >> 16);
        w.Update(w.Zones[0].Center + Math.Max(d, 0), 5000, 0);
        if (w.Intensity == 0) { Assert.Equal((0, 0), w.VisibleDrops()); return; }
        var (first, n) = w.VisibleDrops();
        Assert.Equal((int)((long)w.Intensity * 100 >> 16), n);
        Assert.Equal(100 - n, first);
        if (intensity == 0x10000) Assert.Equal((0, 100), (first, n));
        _ = count;
    }

    [Fact]
    public void Drops_NotWithoutRain_NotOutsideTheZone_NotInTunnels()
    {
        var off = new Weather(0x8, 0, new EaRandom());
        Assert.Equal((0, 0), off.UpdateDrops(0x100000));
        var w = InZone(0, 2);
        w.Update(w.Zones[0].Center, 5000, 0x20);   // tunnel
        Assert.Equal((0, 0), w.UpdateDrops(0x100000));
        w.Update(w.Zones[0].Center + 3000, 5000, 0);   // far from every zone
        Assert.Equal((0, 0), w.UpdateDrops(0x100000));
    }

    [Fact]
    public void RainDrops_AgeUpEachFrame_ThenRespawnWithinTheArea()
    {
        var w = InZone(0, 3);
        var before = w.Drops.Select(d => d).ToArray();
        w.UpdateDrops(0);
        Assert.All(Enumerable.Range(0, 100), i =>
        {
            if (before[i].Age < 0xB5) Assert.Equal(before[i].Age + 1, w.Drops[i].Age);
        });
        // run until some drop passed 0xB4: it respawns with age 0x51..0xB4 inside the area
        for (int f = 0; f < 200; f++) w.UpdateDrops(0);
        Assert.All(w.Drops, d => { Assert.InRange((int)d.Age, 0x51, 0xB5); Assert.InRange(d.X, 100, 599); Assert.InRange(d.Y, 0x50, 0x50 + 199 + 30); });   // standing: y + 1 every 8th age step
        // lifetime: 1 + (0xB4 - age at respawn) frames, so every drop got replaced at least once
        Assert.Contains(Enumerable.Range(0, 100), i => w.Drops[i].X != before[i].X || w.Drops[i].Y != before[i].Y);
    }

    [Fact]
    public void RainDrops_RunDownWhenStandingAndUpWhenFast()
    {
        var slow = InZone(0, 4); var fast = InZone(0, 4);
        var y0 = slow.Drops.Select(d => d.Y).ToArray();
        for (int f = 0; f < 8; f++) { slow.UpdateDrops(0); fast.UpdateDrops(0x640000); }   // 100 m/s = 223 mph
        int down = 0, up = 0;
        for (int i = 0; i < 100; i++)
        {
            if (slow.Drops[i].Y == y0[i] + 1) down++;   // below 10 mph every 8th age step: y + 1
            if (fast.Drops[i].Y < y0[i]) up++;          // above 80 mph: y - 1 each 2nd frame (7 - 223/40 < 2)
        }
        Assert.True(down >= 90, $"down {down}");
        Assert.True(up >= 90, $"up {up}");
    }

    [Fact]
    public void RainDrops_HoldStillAtMediumSpeed_AndPausedDoesNotMove()
    {
        var w = InZone(0, 7);
        var ys = w.Drops.Select(d => (d.X, d.Y, d.Age)).ToArray();
        for (int f = 0; f < 4; f++) w.UpdateDrops(0x1B0000);   // 27 m/s = 60 mph: neither branch (no respawn for ages < 0xB0)
        for (int i = 0; i < 100; i++)
            if (ys[i].Age < 0xB0) Assert.Equal((ys[i].X, ys[i].Y), (w.Drops[i].X, w.Drops[i].Y));
        var ages = w.Drops.Select(d => d.Age).ToArray();
        Assert.Equal(w.VisibleDrops(), w.UpdateDrops(0, paused: true));
        Assert.Equal(ages, w.Drops.Select(d => d.Age).ToArray());
    }

    [Fact]
    public void SnowDrops_RespawnAtOnce_AndDriftFastAboveSixtyMph()
    {
        var w = InZone(7, 1);
        w.Update(w.Zones.First(z => z.Type == 2).Center, 5000, 0);
        if (w.Type != 2) return;   // the snow zone of track 7 depends on the seed
        w.UpdateDrops(0);
        Assert.All(w.Drops.Skip(100 - w.VisibleDrops().Count), d => Assert.InRange((int)d.Age, 1, 101));
        var y0 = w.Drops.Select(d => d.Y).ToArray();
        for (int f = 0; f < 6; f++) w.UpdateDrops(0x640000);
        Assert.Contains(Enumerable.Range(0, 100), i => w.Drops[i].Y <= y0[i] - 0x1E);
    }
}
