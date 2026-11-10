using NFS.ModApi;

namespace NFS.Game.Cars;

public static class CockpitExtensions
{
    /// <summary>The render-side description (NFS.Render cannot reference NFS.Game); null = no cockpit.</summary>
    public static NFS.Render.CockpitStyle? ToStyle(this Cockpit c) => c.Kind == CockpitKind.None || c.DashFile is null ? null
        : new NFS.Render.CockpitStyle(c.Kind == CockpitKind.Digital, c.DashFile, c.ExtraFile, c.Tinted, c.LeftMirror, c.NoBitOverlays,
            c.GearLetters, c.MirrorOffsetX, c.MirrorOffsetY);
}
