using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class CockpitFlickerTests
{
    [GameDataFact]
    public void ConsecutiveCockpitFramesKeepTheRoadAtTheBottomOfTheView()
    {
        var setup = RaceInfo.Setup("replay");
        setup.Set(0x24, 4);
        var drive = new RaceDrive(new RaceScene(new RaceLoader().Load(setup)),
            withAi: true, automatic: true, countdown: false) { ShowHud = false };
        var cameras = new[] { new RaceCamera(setup.Car(0).CarType), new RaceCamera(setup.Car(0).CarType) };
        for (int mode = 0; mode < cameras.Length; mode++)
            while (cameras[mode].Mode != mode)
                cameras[mode].Cycle(RenderPose.From(drive.Car.Pos, drive.Car.Orient), drive.Car);
        var frames = cameras.Select(_ => new[] { new FrameBuffer(), new FrameBuffer(), new FrameBuffer() }).ToArray();
        int[] worst = new int[cameras.Length];
        // 64 consecutive 32 Hz frames at speed, including the wide road quads crossing
        // both sides of the view. No teleports or simulation changes to force the failure.
        for (int step = 0; step < 304; step++)
        {
            drive.Step(Autopilot.Input(drive.Runner));
            if (step < 240) continue;
            drive.SnapToHost();
            for (int mode = 0; mode < cameras.Length; mode++)
            {
                cameras[mode].Snap(RenderPose.From(drive.Car.Pos, drive.Car.Orient), drive.Car);
                drive.Scene.Track.Render(frames[mode][step % 3], cameras[mode].Camera(), new RenderOptions(),
                    drive.Scene.RenderCars.Where((_, i) => i != drive.Scene.PlayerInstance),
                    drive.Runner.DynObjects.Knocked, drive.Runner.Tick, drive.Skids.Buffer);
                if (step < 242) continue;
                int spikes = BottomSpikes(frames[mode][(step - 2) % 3], frames[mode][(step - 1) % 3], frames[mode][step % 3]);
                worst[mode] = Math.Max(worst[mode], spikes);
            }
        }
        // Allow texture motion and thin moving edges; a missing road used to replace
        // almost the whole 51,200-pixel band with sky for a single frame (>45,000 spikes).
        for (int mode = 0; mode < worst.Length; mode++)
            Assert.True(worst[mode] < 1000, $"TR04 mode {mode}: {worst[mode]}/51200 single-frame bottom spikes.");
    }

    static int BottomSpikes(FrameBuffer previous, FrameBuffer current, FrameBuffer next)
    {
        int spikes = 0;
        for (int p = (current.Height - 80) * current.Width * 4; p < current.Rgba.Length; p += 4)
        {
            int before = 0, after = 0, neighbors = 0;
            for (int k = 0; k < 3; k++)
            {
                before += Math.Abs(previous.Rgba[p + k] - current.Rgba[p + k]);
                after += Math.Abs(next.Rgba[p + k] - current.Rgba[p + k]);
                neighbors += Math.Abs(previous.Rgba[p + k] - next.Rgba[p + k]);
            }
            if (before > 180 && after > 180 && neighbors < 60) spikes++;
        }
        return spikes;
    }
}
