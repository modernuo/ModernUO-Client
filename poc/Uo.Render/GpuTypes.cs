using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Uo.Render;

/// <summary>32-byte sprite instance, pulled by the vertex shader from a storage buffer. Layout documented in sprite.hlsl.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpriteInstance
{
    public int X, Y;
    public uint Whf;      // w | h << 12 | flags << 24
    public uint Uv;       // u | v << 16
    public uint PageHue;  // page | hueRow << 16
    public uint Depth;
    public uint Light;    // 4 x u8, 170 = 1.0
    public uint Corners;  // 4 x s8 corner z (top, right, left, bottom)

    public const uint FlagHued = 1, FlagPartial = 2, FlagStretch = 4, FlagLit = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PackWhf(int w, int h, uint flags) => (uint)(w & 0xFFF) | ((uint)(h & 0xFFF) << 12) | (flags << 24);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FrameUniforms
{
    public int CamPxX, CamPxY;
    public float CamFracX, CamFracY;
    public float ScaleX, ScaleY;
    public float InvViewportX, InvViewportY;
    public uint CamDepth;
    public uint ChunkCount;
    public float InvAtlas;
    public float DepthScale;
    public uint Sharp;
    public float AtlasSize;
    public uint FormatFlags; // PageFormats.ShaderFlags: 1 = un-premultiply edges, 2 = gray tolerance (BC7)
    public float GrayTolerance;
}

/// <summary>Entry of the per-frame visible chunk table: first instance in the big buffer and running count.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ChunkDraw
{
    public uint First;
    public uint Prefix;
}

internal static unsafe partial class Native
{
    [LibraryImport("SDL3")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte SDL_SetWindowTitle(IntPtr window, byte* title);

    [LibraryImport("SDL3")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_BeginGPURenderPass(IntPtr commandBuffer, SDL3.SDL.SDL_GPUColorTargetInfo* colorTargets, uint numColorTargets, SDL3.SDL.SDL_GPUDepthStencilTargetInfo* depthStencil);
}
