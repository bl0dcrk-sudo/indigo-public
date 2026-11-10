using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Rival name in HUD slot 9: picks the car (on the race order), draws its name. [ref:2900]</summary>
public class RivalIndicatorTests
{
    [Fact]
    public void RivalIsOnePlaceAheadOrBehindWhenLookingBack()
    {
        int[] order = [2, 0, 3, 1]; // car 2 leads, car 1 is last
        Assert.Equal(2, RaceHud.RivalIndex(order, 0, false));
        Assert.Equal(0, RaceHud.RivalIndex(order, 3, false));
        Assert.Null(RaceHud.RivalIndex(order, 2, false));  // leader: nobody ahead
        Assert.Equal(3, RaceHud.RivalIndex(order, 0, true));
        Assert.Equal(1, RaceHud.RivalIndex(order, 3, true));
        Assert.Null(RaceHud.RivalIndex(order, 1, true));   // last: nobody behind
        Assert.Null(RaceHud.RivalIndex(order, 7, false));  // not ranked
    }

    static RaceDrive Drive()
    {
        var setup = RaceInfo.Setup("replay");
        setup.Set(0x24, 4);
        for (int i = 0; i < 8; i++) // the recorded replay carries no driver names
            System.Text.Encoding.ASCII.GetBytes($"DRIVER{i}").CopyTo(setup.Block, 0x78 + i * NFS.Data.FrontEnd.RaceConfig.CarRecordSize + 0x34);
        return new RaceDrive(new RaceScene(new RaceLoader().Load(setup)), countdown: false, withAi: true);
    }

    // stage 5a.2: presentation reads published frames, so a direct host edit is republished
    static void Place(RaceDrive d, int car, int place) { d.Runner.Cars[car].Obj.RacePosition = place; d.SnapToHost(); }

    [GameDataFact]
    public void NetworkRivalUsesAuthoritativeNicknameWithoutChangingFrozenSetupOrAiNames()
    {
        var d = Drive();
        int rival = d.PlayerCarId == 0 ? 1 : 0;
        Place(d, rival, 1); Place(d, d.PlayerCarId, 2);
        var frozen = d.Scene.Session.Setup.Block.ToArray();
        string original = d.RivalName()!;
        d.DriverNames = new Dictionary<int, string> { [rival] = "Bob" };
        Assert.Equal("Bob", d.OriginalHudValues().Rival);
        Assert.Equal(frozen, d.Scene.Session.Setup.Block);
        d.DriverNames = new Dictionary<int, string> { [d.PlayerCarId] = "Alice" };
        Assert.Equal(original, d.RivalName());
    }

    [GameDataFact]
    public void NameComesFromTheRankedCarAheadAndFollowsLookBehind()
    {
        var d = Drive();
        int self = d.Runner.Cars.ToList().FindIndex(c => ReferenceEquals(c.Obj, d.Car));
        var others = Enumerable.Range(0, d.Runner.Cars.Count).Where(i => i != self).ToArray();
        // controlled ranking: self 3rd, others[0] 1st, others[1] 2nd, others[2] 4th
        Place(d, others[0], 1); Place(d, others[1], 2); Place(d, self, 3); Place(d, others[2], 4);
        string Name(int i) => RaceLoader.CarName(d.Scene.Session.Setup, d.Runner.Cars[i].Obj.PlayerIndex);
        Assert.Equal(Name(others[1]), d.RivalName());
        d.Frame(0, default(DriveKeys) with { LookBehind = true });
        Assert.Equal(Name(others[2]), d.RivalName());
        d.Frame(0, default(DriveKeys));
        Place(d, self, 1); Place(d, others[0], 3);
        Assert.Null(d.RivalName());                       // leading: nothing ahead
        Assert.Null(d.OriginalHudValues().Rival);
        d.Frame(0, default(DriveKeys) with { LookBehind = true });
        Assert.Equal(Name(others[1]), d.RivalName());
        Assert.NotEqual(Name(others[1]), Name(others[0]));
    }

    [GameDataFact]
    public void NameIsCentredInSlot9WithinNinetyEightPixelsAndOnlyThereChanges()
    {
        var hud = new OriginalHud();
        var values = new OriginalHudValues(100, 3, 1, 4, 3, 8, 0, 0, 5000, 7000, 1000, false, true);
        var without = new FrameBuffer(); hud.Draw(without, values);
        var with = new FrameBuffer(); hud.Draw(with, values with { Rival = "A VERY LONG DRIVER NAME" });
        var (sx, sy) = hud.Slot(9);
        int minX = int.MaxValue, maxX = int.MinValue, count = 0;
        for (int y = 0; y < with.Height; y++)
            for (int x = 0; x < with.Width; x++)
                if (with.Get(x, y) != without.Get(x, y))
                {
                    count++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    Assert.InRange(y, sy - 24, sy + 16);
                }
        Assert.True(count > 50, "name pixels drawn");
        Assert.InRange(minX, sx - 100, sx);               // text is centred on slot x with width < 98
        Assert.InRange(maxX - minX + 1, 20, 98);
        Assert.InRange((minX + maxX) / 2, sx - 6, sx + 6);
        var blank = new FrameBuffer(); hud.Draw(blank, values with { Rival = "" });
        Assert.Equal(without.Rgba, blank.Rgba);
    }

    [GameDataFact]
    public void F5TextModeDecidesWhetherTheNameIsDrawn()
    {
        var d = Drive();
        int self = d.Runner.Cars.ToList().FindIndex(c => ReferenceEquals(c.Obj, d.Car));
        var others = Enumerable.Range(0, d.Runner.Cars.Count).Where(i => i != self).ToArray();
        Place(d, others[0], 1); Place(d, self, 2);
        for (int i = 1; i < others.Length; i++) Place(d, others[i], i + 2);
        var visible = new FrameBuffer(); d.Render(visible);
        Place(d, self, 1); Place(d, others[0], 2);        // leader: no rival, otherwise identical state
        var leader = new FrameBuffer(); d.Render(leader);
        Assert.NotEqual(visible.Sha256(), leader.Sha256());
        // mode map+text -> text -> nothing: names and HUD vanish with the text mode
        d.ShowHud = false;
        Place(d, self, 2); Place(d, others[0], 1);
        var off1 = new FrameBuffer(); d.Render(off1);
        Place(d, self, 1); Place(d, others[0], 2);
        var off2 = new FrameBuffer(); d.Render(off2);
        Assert.Equal(off1.Rgba, off2.Rgba); // text mode off: ranking cannot reach the screen
    }

    [GameDataFact]
    public void WritesReviewFramesWhenAskedTo()
    {
        if (Environment.GetEnvironmentVariable("NFS_RIVAL_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var d = Drive();
        int self = d.Runner.Cars.ToList().FindIndex(c => ReferenceEquals(c.Obj, d.Car));
        var others = Enumerable.Range(0, d.Runner.Cars.Count).Where(i => i != self).ToArray();
        Place(d, others[0], 1); Place(d, self, 2);
        for (int i = 1; i < others.Length; i++) Place(d, others[i], i + 2);
        var fb = new FrameBuffer(); d.Render(fb); fb.SavePng(Path.Combine(dir, "tr04_rival_ahead.png"));
        d.Frame(0, default(DriveKeys) with { LookBehind = true });
        fb = new FrameBuffer(); d.Render(fb); fb.SavePng(Path.Combine(dir, "tr04_rival_behind_lookback.png"));
    }
}
