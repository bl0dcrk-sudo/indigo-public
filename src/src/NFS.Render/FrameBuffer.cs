using System.Security.Cryptography;
using NFS.Data;

namespace NFS.Render;

/// <summary>
/// 640×480 screen as RGBA32. The original renders 8bpp and remaps every texel through a per-texture palette
/// (`(slot, fogLevel)` → main palette + fog tables). Track textures each carry their own palette, so an [ref:2524]
/// RGBA buffer reproduces the same visible colours without rebuilding the remap/fog tables, and encodes straight to PNG.
/// Index = (y·Width + x)·4.
/// </summary>
public sealed class FrameBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public FrameBuffer(int width = 640, int height = 480)
    {
        Width = width; Height = height;
        Rgba = new byte[width * height * 4];
    }

    public void Clear(byte r, byte g, byte b)
    {
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Rgba.AsSpan()).Fill(r | (uint)g << 8 | (uint)b << 16 | 0xFF000000u);
    }

    public void Set(int x, int y, uint rgb)
    {
        int o = (y * Width + x) * 4;
        Rgba[o] = (byte)(rgb >> 16); Rgba[o + 1] = (byte)(rgb >> 8); Rgba[o + 2] = (byte)rgb; Rgba[o + 3] = 255;
    }

    /// <summary>Clears every pixel to fully transparent (alpha 0): the start of a 2D overlay layer composed over a separately rendered 3D
    /// scene (<see cref="Compositor"/>). <see cref="Set"/> then marks the pixels the 2D code draws (alpha 255).</summary>
    public void ClearTransparent() => Array.Clear(Rgba);

    /// <summary>Alpha blend of <paramref name="rgb"/> over the pixel (<paramref name="alpha"/> 0 = unchanged, 255 = opaque). Over an
    /// opaque pixel (every normal frame buffer) the result stays opaque; over a transparent or partly transparent overlay pixel the
    /// source over operator is used, so the overlay keeps a correct alpha mask.</summary>
    public void Blend(int x, int y, uint rgb, int alpha)
    {
        int o = (y * Width + x) * 4, inv = 255 - alpha;
        int da = Rgba[o + 3];
        if (da != 255)
        {
            if (alpha == 0) return;
            int oa = alpha + da * inv / 255;             // result alpha
            int wd = da * inv / 255;                      // weight of the old colour (premultiplied by its alpha)
            Rgba[o] = (byte)((Rgba[o] * wd + (int)(rgb >> 16 & 0xFF) * alpha + oa / 2) / oa);
            Rgba[o + 1] = (byte)((Rgba[o + 1] * wd + (int)(rgb >> 8 & 0xFF) * alpha + oa / 2) / oa);
            Rgba[o + 2] = (byte)((Rgba[o + 2] * wd + (int)(rgb & 0xFF) * alpha + oa / 2) / oa);
            Rgba[o + 3] = (byte)oa;
            return;
        }
        Rgba[o] = (byte)((Rgba[o] * inv + (int)(rgb >> 16 & 0xFF) * alpha + 127) / 255);
        Rgba[o + 1] = (byte)((Rgba[o + 1] * inv + (int)(rgb >> 8 & 0xFF) * alpha + 127) / 255);
        Rgba[o + 2] = (byte)((Rgba[o + 2] * inv + (int)(rgb & 0xFF) * alpha + 127) / 255);
        Rgba[o + 3] = 255;
    }

    public uint Get(int x, int y)
    {
        int o = (y * Width + x) * 4;
        return (uint)(Rgba[o] << 16 | Rgba[o + 1] << 8 | Rgba[o + 2]);
    }

    public string Sha256() => Convert.ToHexStringLower(SHA256.HashData(Rgba));
    public byte[] EncodePng() => PngWriter.EncodeRgba(Width, Height, Rgba);
    public void SavePng(string path) => PngWriter.WriteRgba(path, Width, Height, Rgba);
}
