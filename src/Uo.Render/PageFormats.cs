using Uo.Pack;
using static SDL3.SDL;

namespace Uo.Render;

/// <summary>Atlas page pixel formats the renderer supports (pack v1: one format per pack).</summary>
public static class PageFormats
{
    /// <summary>PixelFormat value 2: 16-bit B5G5R5A1 (bits 0-4 B, 5-9 G, 10-14 R, 15 A). Local constant until Uo.Pack's enum has it.</summary>
    public const PixelFormat B5G5R5A1 = (PixelFormat)2;

    public static string Name(PixelFormat f) => f switch
    {
        PixelFormat.Rgba8 => "RGBA8",
        PixelFormat.Bc7 => "BC7",
        B5G5R5A1 => "B5G5R5A1",
        PixelFormat.Indexed8 => "Indexed8",
        _ => $"format {(int)f}",
    };

    public static SDL_GPUTextureFormat Gpu(PixelFormat f) => f switch
    {
        PixelFormat.Rgba8 => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
        PixelFormat.Bc7 => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_BC7_RGBA_UNORM,
        B5G5R5A1 => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_B5G5R5A1_UNORM,
        // palette indices, read with Load in the shader (manual palette lookup + filtering)
        PixelFormat.Indexed8 => SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8_UINT,
        _ => throw new NotSupportedException($"page format {(int)f}"),
    };

    /// <summary>Bytes of one mip level (BC7: ceil(s/4)² × 16; 16-bit: s² × 2; RGBA8: s² × 4).</summary>
    public static int MipBytes(int size, int mip, PixelFormat f)
    {
        int s = Math.Max(1, size >> mip);
        return f switch
        {
            PixelFormat.Rgba8 => s * s * 4,
            PixelFormat.Bc7 => ((s + 3) / 4) * ((s + 3) / 4) * 16,
            B5G5R5A1 => s * s * 2,
            PixelFormat.Indexed8 => s * s,
            _ => throw new NotSupportedException($"page format {(int)f}"),
        };
    }

    public static int ChainBytes(int size, int mips, PixelFormat f)
    {
        int total = 0;
        for (int m = 0; m < mips; m++) total += MipBytes(size, m, f);
        return total;
    }

    /// <summary>BC7 partial-hue gray tolerance in 1/255 units (chosen by matching the RGBA8 hue sheet; see report).</summary>
    public const float Bc7GrayTolerance = 4;

    /// <summary>Shader behaviour flags per format (see sprite.hlsl FLAG_UNPREMUL / FLAG_GRAYTOL).</summary>
    public static uint ShaderFlags(PixelFormat f) => f switch
    {
        // Un-premultiply (flag 1) is off for every format: the RGBA8 mips are straight alpha with fractional edge
        // alpha, so dividing the filtered colour by alpha over-weights partly transparent texels and produced bright
        // edge sparkle at zoom < 1 (A/B screenshots); the 0.5 alpha test already rejects texels that mix heavily
        // with transparent black, and no dark fringe was visible without it.
        PixelFormat.Rgba8 => 0u,
        // BC7: block compression perturbs exact grays, so the partial-hue gray test needs a tolerance
        // (transparent texels carry the block's mean colour, set by the converter).
        PixelFormat.Bc7 => 2u,
        // 16-bit: 5-bit channels decode exactly, so gray stays exact.
        B5G5R5A1 => 0u,
        _ => 0u,
    };

    /// <summary>Reads only the page header (Kind 0, 1 or 2; compressed pages decode just the header's 16 bytes).</summary>
    public static PageHeader ReadHeader(string path) => AtlasPage.ReadHeader(path);
}
