namespace NFS.Desktop;

/// <summary>"Sharp bilinear" display filter without a shader: the image is first enlarged by a whole [ref:3384]
/// factor (nearest) to a size ≥ the display area, and only then scaled bilinearly to the window – pixel edges stay sharp, blending
/// applies only to the non-integer remainder of the scale.</summary>
internal static class DisplayFilter
{
    /// <summary>Largest size of the intermediate buffer (width), so as not to exceed GPU texture limits.</summary>
    public const int MaxIntermediateWidth = 5120;

    /// <summary>Factor n ≥ 1 by which an image of width <paramref name="srcW"/> is enlarged (nearest) before bilinear scaling to
    /// width <paramref name="dstW"/>: the smallest n with srcW·n ≥ dstW, capped by <see cref="MaxIntermediateWidth"/>.</summary>
    public static int IntegerFactor(int srcW, int dstW)
    {
        if (srcW <= 0 || dstW <= srcW) return 1;
        int n = (dstW + srcW - 1) / srcW;
        return Math.Clamp(MaxIntermediateWidth / srcW, 1, n);
    }
}
