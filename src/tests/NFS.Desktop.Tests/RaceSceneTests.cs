using System.Numerics;
using NFS.Core;
using NFS.Desktop;

namespace NFS.Desktop.Tests;

// Headless: RaceScene's static helpers only (no window, no game data).
public class RaceSceneTests
{
    [Fact]
    public void ParsesRace()
    {
        var o = DesktopOptions.Parse(["--race", "replay", "--autotest", "out"]);
        Assert.Equal("replay", o.Race);
        Assert.Equal("out", o.AutotestDir);
        Assert.Null(DesktopOptions.Parse([]).Race);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--race"]));
        Assert.Contains("--race", DesktopOptions.Usage);
    }

    [Fact]
    public void ToWorldConvertsFixed()
    {
        Assert.Equal(new Vector3(1, -2, 0.5f), RaceScene.ToWorld(new Vec3(Fx.One, -2 * Fx.One, Fx.One / 2)));
    }

    [Fact]
    public void ChaseCameraBehindAndAboveIdentityCar()
    {
        var pos = new Vec3(10 * Fx.One, 0, 20 * Fx.One);
        var cam = RaceScene.ChaseCamera(pos, Mat3.Identity, back: 6, height: 2);
        Assert.Equal(new Vector3(10, 2, 14), cam.Position);
        Assert.Equal(Vector3.UnitZ, cam.Forward);
        var v = cam.ToView(RaceScene.ToWorld(pos));
        Assert.True(v.Z > 0, "car is in front of the camera");
        Assert.Equal(6f, v.Z, 3);
    }

    [Fact]
    public void ChaseCameraFollowsBackwardCar()
    {
        // backward race: right/forward negated (RaceLoader.PlaceOnSlice)
        var o = new Mat3(-Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, -Fx.One);
        var cam = RaceScene.ChaseCamera(new Vec3(0, 0, 0), o, back: 5, height: 1);
        Assert.Equal(new Vector3(0, 1, 5), cam.Position);
        Assert.Equal(-Vector3.UnitZ, cam.Forward);
    }

    static NFS.Game.Race.RaceCar Car(int index, int grid, CarFlags flags) =>
        new(index, new NFS.Game.Race.CarSetup(0, 1, 0, 0, 0), "X", new CarObj { Flags = flags },
            null, null, null, grid, 0);

    [Fact]
    public void PlayerCarIsTheLocalHumanNotGridSlotZero()
    {
        Assert.Null(RaceScene.PlayerCar([]));
        // KO rebuilds the grid (entrants − previous place), so slot 0 is the last finisher: car 0 won [ref:2885]
        // sits at slot 3, car 1 is the AI at slot 0. The camera/input must follow car 0 either way.
        var humanWon = new[]
        {
            Car(0, 3, CarFlags.Racer | CarFlags.LocalHuman),
            Car(1, 0, CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent),
        };
        Assert.Equal(0, RaceScene.PlayerCar(humanWon)!.Index);
        Assert.Equal(0, RaceScene.PlayerCar(humanWon.Reverse().ToArray())!.Index); // order-independent
        // no flagged human (e.g. demo grid): first car of the list, as before
        Assert.Equal(5, RaceScene.PlayerCar([Car(5, 2, CarFlags.Opponent), Car(1, 1, CarFlags.Opponent)])!.Index);
    }

    [GameDataFact]
    public void ReplayGridRendersWithCars()
    {
        var scene = new RaceScene("replay");
        Assert.Equal(4, scene.Session.TrackNumber);      // REPLAY.RPY: track 4, 8 cars
        Assert.Equal(8, scene.Cars.Count);
        var player = RaceScene.PlayerCar(scene.Session.Cars)!;
        Assert.Equal(0, player.GridSlot);
        var cam = scene.PlayerCamera();
        Assert.True(cam.ToView(RaceScene.ToWorld(player.Obj.Pos)).Z > 0);
        var withCars = new NFS.Render.FrameBuffer();
        scene.Render(withCars);
        var trackOnly = new NFS.Render.FrameBuffer();
        scene.Track.Render(trackOnly, cam, new NFS.Render.RenderOptions());
        Assert.NotEqual(trackOnly.Sha256(), withCars.Sha256());
    }
}
