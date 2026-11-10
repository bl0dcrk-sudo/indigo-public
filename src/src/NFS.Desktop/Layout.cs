namespace NFS.Desktop;

internal static class Layout
{
    /// <summary>Largest rectangle with the src aspect ratio centred in the destination (letterbox/pillarbox).</summary>
    public static (int X, int Y, int W, int H) Fit(int dstW, int dstH, int srcW, int srcH)
    {
        if (dstW <= 0 || dstH <= 0) return (0, 0, 0, 0);
        int w = dstW, h = (int)((long)dstW * srcH / srcW);
        if (h > dstH) { h = dstH; w = (int)((long)dstH * srcW / srcH); }
        return ((dstW - w) / 2, (dstH - h) / 2, w, h);
    }
}
