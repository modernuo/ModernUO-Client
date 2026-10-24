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
    public uint Light;    // FlagLit: 4 x u8, 170 = 1.0; otherwise byte 0 = 255 - alpha, byte 1 = HueMode
    public uint Corners;  // 4 x s8 corner z (top, right, left, bottom); FlagIndexed: palette id (u16)

    public const uint FlagHued = 1, FlagPartial = 2, FlagStretch = 4, FlagLit = 8;
    /// <summary>Draw the rect flipped horizontally (mirrored anim directions).</summary>
    public const uint FlagMirror = 16;
    /// <summary>Page is in the Indexed8 anim array; Corners holds the palette id.</summary>
    public const uint FlagIndexed = 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PackWhf(int w, int h, uint flags) => (uint)(w & 0xFFF) | ((uint)(h & 0xFFF) << 12) | (flags << 24);

    public readonly uint Flags => Whf >> 24;
    public readonly int Width => (int)(Whf & 0xFFF);
    public readonly int Height => (int)((Whf >> 12) & 0xFFF);
    public readonly int Page => (int)(PageHue & 0xFFFF);

    /// <summary>Translucency (255 = opaque). Not for FlagLit (stretched land): its Light holds the corner light.
    /// Translucent instances draw in the second, depth-sorted blended pass.</summary>
    public byte Alpha
    {
        readonly get => (Whf >> 24 & FlagLit) != 0 ? (byte)255 : (byte)(255 - (Light & 0xFF));
        set { if ((Whf >> 24 & FlagLit) == 0) Light = (Light & ~0xFFu) | (uint)(255 - value); }
    }

    public HueMode Mode
    {
        readonly get => (Whf >> 24 & FlagLit) != 0 ? HueMode.Normal : (HueMode)((Light >> 8) & 0xFF);
        set { if ((Whf >> 24 & FlagLit) == 0) Light = (Light & ~0xFF00u) | ((uint)value << 8); }
    }

    /// <summary>Whole-sprite hue (ClassicUO's highlight: HUED, not partial). <paramref name="hue"/> is the 1-based UO hue.</summary>
    public void SetHighlight(ushort hue)
    {
        if (hue == 0) return;
        PageHue = (PageHue & 0xFFFF) | ((uint)(hue - 1) << 16);
        Whf = (Whf | (FlagHued << 24)) & ~(FlagPartial << 24);
    }

    public void SetMirror(bool mirror) => Whf = mirror ? Whf | (FlagMirror << 24) : Whf & ~(FlagMirror << 24);

    public readonly bool Translucent => Alpha < 255 || Mode is HueMode.Spectral or HueMode.Shadow;
}

/// <summary>Per-instance colour mode beyond hued/partial (ClassicUO IsometricWorld.fx: SPECTRAL, SHADOW, EFFECT_HUED).</summary>
public enum HueMode : byte
{
    Normal = 0,
    /// <summary>Black, alpha = 1 - 1.5 r (translucent pass).</summary>
    Spectral = 1,
    /// <summary>Black at alpha 0.4, quad sheared like Batcher2D.DrawShadow (translucent pass).</summary>
    Shadow = 2,
    /// <summary>Hue shade from the green channel instead of red.</summary>
    EffectHued = 3,
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
    public float AnimAtlasSize, InvAnimAtlas; // Indexed8 anim pages
    public uint OverrideMask;  // override hash slots - 1, 0 = no overrides this frame
    public uint OverrideBase;  // first override slot in the frame table
    public uint TransBase;     // first translucent-pass entry in the frame table
    public uint Pass;          // 0 = opaque (chunk table), 1 = translucent (sorted index list)
    public uint Pad0, Pad1;
}

/// <summary>Slot of the per-frame override hash (in the frame table): <see cref="Index1"/> = instance index + 1
/// (0 = empty), <see cref="Value"/> = alpha | flags &lt;&lt; 8 | hue row &lt;&lt; 16.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OverrideEntry
{
    public uint Index1;
    public uint Value;

    public const uint Hidden = 1, Highlight = 2;
    public const int MaxProbes = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(uint i)
    {
        i ^= i >> 16;
        i *= 0x7FEB352Du;
        i ^= i >> 15;
        return i;
    }
}

/// <summary>Entry of the per-frame visible chunk table: first instance in the big buffer and running count.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ChunkDraw
{
    public uint First;
    public uint Prefix;
}

public static unsafe partial class Native
{
    [LibraryImport("SDL3")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte SDL_SetWindowTitle(IntPtr window, byte* title);

    [LibraryImport("SDL3")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial IntPtr SDL_BeginGPURenderPass(IntPtr commandBuffer, SDL3.SDL.SDL_GPUColorTargetInfo* colorTargets, uint numColorTargets, SDL3.SDL.SDL_GPUDepthStencilTargetInfo* depthStencil);
}
