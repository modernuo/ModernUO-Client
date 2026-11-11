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
    public uint Light;    // FlagLit: 4 x u8, 170 = 1.0; otherwise byte 0 = 255 - alpha, bits 8-9 HueMode, 10-11 blend, 12-15 Light* bits, 16-31 angle / wobble trim;
                          // FlagZTest statics: bits 16-31 = tile x & 0xFF | (tile y & 0xFF) << 8 (the out-of-range tint)
    public uint Corners;  // 4 x s8 corner z (top, right, left, bottom); FlagIndexed: palette id (u16); FlagZTest statics: z | height | Z* bits;
                          // FlagDeform: bits 16-23 s8 shear, 24-31 source rows

    public const uint FlagHued = 1, FlagPartial = 2, FlagStretch = 4, FlagLit = 8;
    /// <summary>Draw the rect flipped horizontally (mirrored anim directions).</summary>
    public const uint FlagMirror = 16;
    /// <summary>Page is in the Indexed8 anim array; Corners holds the palette id.</summary>
    public const uint FlagIndexed = 32;
    /// <summary>Baked land or static that the frame's max-Z / roof / circle-of-transparency uniforms apply to. Land: z in
    /// Corners byte 0 (stretched land: its top corner, which is the tile's z). Statics: Corners = (u8)z | height &lt;&lt; 8 |
    /// Z* bits &lt;&lt; 16.</summary>
    public const uint FlagZTest = 64;
    /// <summary>Dynamic sprite drawn deformed (ClassicUO's seated-character sections, Batcher2D.DrawCharacterSitted): the
    /// quad's top edge is shifted right by the s8 in Corners bits 16-23 (the bottom edge stays), and the texture's
    /// <see cref="Height"/>-pixel-tall quad samples the number of rows in Corners bits 24-31 (0 = Height). The palette id
    /// of indexed anims stays in Corners bits 0-15.</summary>
    public const uint FlagDeform = 128;

    /// <summary>Corners bits 16-23 of <see cref="FlagZTest"/> instances.</summary>
    public const uint ZLand = 1, ZRoof = 2, ZCotCapable = 4, ZNoCot = 8;

    /// <summary>Corners bits 16-23 of <see cref="FlagZTest"/> statics (clip-2 depth model): the static is a wall (tiledata Wall:
    /// vertical faces on the tile's back edges, GeoDepth.Wall); without it a box over the tile (GeoDepth.Box).</summary>
    public const uint ZGeoWall = 16;

    /// <summary>Corners bits 16-23 of <see cref="FlagZTest"/> statics (clip-2 depth model): a flat (height 0, Background or
    /// Surface): its plane, lowered like land (GeoDepth.Flat).</summary>
    public const uint ZGeoFlat = 32;

    /// <summary>Light bits 13 and 15 together (<see cref="LightRotated"/> | <see cref="LightWobble"/>, never combined
    /// otherwise): a mobile layer carrying its composite's anchor for the clip-2 depth model (<see cref="GeoMobile"/>).</summary>
    public const uint LightGeoMobile = 0xA000;

    /// <summary>Marks an animation instance as a layer of a mobile composite whose anchor (the body's anchor pixel) lies
    /// (<paramref name="adx"/>, <paramref name="ady"/>) from the instance's top-left, at tile z <paramref name="z"/>:
    /// Light bits 16-27 = adx (s12), 28-31 = z bits 0-3; Corners bits 16-27 = ady (s12), 28-31 = z bits 4-7. Not for
    /// <see cref="FlagDeform"/> (its Corners bits 16-31) or rotated/wobbling instances. False (unchanged) when out of range.</summary>
    public bool GeoMobile(int adx, int ady, int z)
    {
        if (adx < -2048 || adx > 2047 || ady < -2048 || ady > 2047 || (Flags & (FlagDeform | FlagLit)) != 0) return false;
        uint zb = (uint)(byte)(sbyte)Math.Clamp(z, -128, 127);
        Light = (Light & 0x5FFFu) | LightGeoMobile | ((uint)adx & 0xFFF) << 16 | (zb & 0xF) << 28;
        Corners = (Corners & 0xFFFFu) | ((uint)ady & 0xFFF) << 16 | (zb >> 4) << 28;
        return true;
    }

    /// <summary>The anchor and z <see cref="GeoMobile"/> stored, or false when the instance carries none.</summary>
    public readonly bool TryGeoMobile(out int adx, out int ady, out int z)
    {
        if ((Flags & FlagLit) != 0 || (Light & LightGeoMobile) != LightGeoMobile)
        {
            adx = ady = z = 0;
            return false;
        }
        adx = (int)(Light << 4) >> 20;
        ady = (int)(Corners << 4) >> 20;
        z = (sbyte)(byte)((Light >> 28) | (Corners >> 28) << 4);
        return true;
    }

    /// <summary>Light bits of non-lit instances: <see cref="LightCot"/> = the circle of transparency applies (dynamic
    /// sprites; baked statics derive it from their z); <see cref="LightRotated"/> = rotate the quad about its top-left
    /// corner by bits 16-31 (u16 turns); <see cref="LightAdditive"/> = add to the target (ClassicUO's Screen effect blend).</summary>
    public const uint LightCot = 0x1000, LightRotated = 0x2000, LightAdditive = 0x4000;

    /// <summary>ClassicUO's animated-water copy (View.DrawStatic / LandView with AnimatedWaterEffect): the quad is scaled by
    /// the frame's <see cref="FrameUniforms.WaterScaleX"/>/Y about its untrimmed top-left, which lies (bits 16-23, bits 24-31)
    /// pixels up-left of X/Y (the trim offset). Never combined with <see cref="LightRotated"/>.</summary>
    public const uint LightWobble = 0x8000;

    /// <summary>Light bits 16-31 of a baked (<see cref="FlagZTest"/>) static: the low bytes of its tile, which the shader's
    /// out-of-range tint (<see cref="FrameUniforms.RangeTintRow"/>) compares with the player's tile modulo 256. Land needs
    /// none: its tile follows from X/Y and z.</summary>
    public static uint StaticTileBits(int tileX, int tileY) => (uint)(byte)tileX << 16 | (uint)(byte)tileY << 24;

    /// <summary>The tile of a baked land or static instance as the shader derives it (out-of-range tint): land exactly from
    /// X/Y (and z for flat land); statics from an estimate off the sprite's rectangle (the art's horizontal centre and its
    /// bottom edge sit within a few tiles of the tile's anchor), corrected to the low bytes of <see cref="StaticTileBits"/>.
    /// Exact while the estimate is within 128 tiles.</summary>
    public readonly void Tile(out int tileX, out int tileY)
    {
        uint flags = Flags;
        int z = (sbyte)Corners;
        if ((flags & FlagStretch) != 0 || (Corners >> 16 & ZLand) != 0)
        {
            if ((flags & FlagStretch) != 0) z = 0;
            int d = FloorDiv(X + 33, 22), s = FloorDiv(Y + 33 + z * 4, 22);
            tileX = (s + d) >> 1;
            tileY = (s - d) >> 1;
        }
        else
        {
            // View.DrawStatic: the untrimmed art is centred on x = (tx - ty)·22 and ends at y = (tx + ty)·22 - z·4 + 22
            int d = FloorDiv(X + (Width >> 1) + 11, 22), s = FloorDiv(Y + Height + z * 4 - 11, 22);
            int ex = (s + d) >> 1, ey = (s - d) >> 1;
            tileX = ex + (int)(((Light >> 16) - (uint)ex + 128u) & 0xFF) - 128;
            tileY = ey + (int)(((Light >> 24) - (uint)ey + 128u) & 0xFF) - 128;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    /// <summary>Packs <see cref="LightWobble"/> with the trim offset (clamped to 255).</summary>
    public static uint WobbleBits(int trimX, int trimY)
        => LightWobble | (uint)Math.Clamp(trimX, 0, 255) << 16 | (uint)Math.Clamp(trimY, 0, 255) << 24;

    /// <summary>Packs <see cref="FlagDeform"/>'s Corners bits 16-31 onto a palette id.</summary>
    public static uint DeformCorners(uint palette, int shear, int sourceRows)
        => (palette & 0xFFFF) | (uint)(byte)(sbyte)Math.Clamp(shear, -128, 127) << 16 | (uint)Math.Clamp(sourceRows, 0, 255) << 24;

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
        readonly get => (Whf >> 24 & FlagLit) != 0 ? HueMode.Normal : (HueMode)((Light >> 8) & 0x3);
        set { if ((Whf >> 24 & FlagLit) == 0) Light = (Light & ~0x300u) | (((uint)value & 0x3) << 8); }
    }

    /// <summary>The blend the translucent pass draws this instance with (ClassicUO's effect blends). 3-bit code: Light bit 14
    /// (<see cref="LightAdditive"/>) is bit 2, Light bits 10-11 are bits 0-1.</summary>
    public InstanceBlend Blend
    {
        readonly get => (Whf >> 24 & FlagLit) != 0 ? InstanceBlend.Normal : (InstanceBlend)((Light >> 10 & 3) | (Light >> 12 & 4));
        set { if ((Whf >> 24 & FlagLit) == 0) Light = (Light & ~(0xC00u | LightAdditive)) | ((uint)value & 3) << 10 | ((uint)value & 4) << 12; }
    }

    /// <summary>Sets <see cref="LightCot"/>/<see cref="LightAdditive"/> bits (non-lit instances only).</summary>
    public void SetLightBits(uint bits)
    {
        if ((Whf >> 24 & FlagLit) == 0) Light |= bits;
    }

    /// <summary>Rotates the quad by <paramref name="radians"/> (clockwise on screen, as Batcher2D) about its top-left corner.</summary>
    public void SetRotation(float radians)
    {
        if ((Whf >> 24 & FlagLit) != 0) return;
        float turns = radians / (2 * MathF.PI);
        turns -= MathF.Floor(turns);
        Light = (Light & 0xFFFFu) | LightRotated | ((uint)(turns * 65536f + 0.5f) & 0xFFFF) << 16;
    }

    public readonly bool Additive => Blend == InstanceBlend.Screen;

    /// <summary>Whole-sprite hue (ClassicUO's highlight: HUED, not partial). <paramref name="hue"/> is the 1-based UO hue.</summary>
    public void SetHighlight(ushort hue)
    {
        if (hue == 0) return;
        PageHue = (PageHue & 0xFFFF) | ((uint)(hue - 1) << 16);
        Whf = (Whf | (FlagHued << 24)) & ~(FlagPartial << 24);
    }

    public void SetMirror(bool mirror) => Whf = mirror ? Whf | (FlagMirror << 24) : Whf & ~(FlagMirror << 24);

    public readonly bool Translucent => Alpha < 255 || Mode is HueMode.Spectral or HueMode.Shadow || Blend != InstanceBlend.Normal;
}

/// <summary>Translucent-pass blends (ClassicUO GameEffect blend states; the shader's output is premultiplied (rgb·a, a), as
/// IsometricWorld.fx's, so each is the FNA BlendState's factors). <see cref="Normal"/> and <see cref="Screen"/> share the
/// premultiplied pipeline (Screen outputs alpha 0); the others need their own pipeline, drawn as runs of the sorted list.</summary>
public enum InstanceBlend : byte
{
    Normal = 0,
    /// <summary>ShadowBlue: dst·(1 - src) - src·src (reverse subtract).</summary>
    ShadowBlue = 1,
    /// <summary>Normal (premultiplied pipeline, opaque output), but drawn in the translucent pass: effects, so that
    /// effects at equal depth keep their painter order whatever their blends (an opaque effect in the opaque pass would
    /// write depth and hide a later blended one at the same depth under the strict depth test).</summary>
    NormalOrdered = 2,
    /// <summary>Screen / ScreenMore: dst + src (One, One).</summary>
    Screen = 4,
    /// <summary>Multiply: dst·src (Zero, SrcColor).</summary>
    Multiply = 5,
    /// <summary>ScreenLess: src·dst + dst·(1 - a) (DstColor, InvSrcAlpha).</summary>
    ScreenLess = 6,
    /// <summary>NormalHalfTransparent: 2·src·dst (DstColor, SrcColor).</summary>
    NormalHalf = 7,
}

/// <summary>A run of the translucent pass's sorted list drawn with one blend: list entries [Start, Start + Count).</summary>
public struct TransRun
{
    public int Start, Count;
    public InstanceBlend Blend;
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
    // ClassicUO's max-Z and roof hiding for FlagZTest instances (ZFlags: 1 = on, 2 = hide roofs)
    public int MaxZ, MaxGroundZ;
    public uint ZFlags;
    // circle of transparency (shader mode): centre and radius in target pixels, CotZ = player z + 5; CotRadius 0 = off
    public int CotZ;
    public float CotCenterX, CotCenterY, CotRadius;
    public uint TintRow;       // whole-sprite hue row + 1 for every FlagZTest instance (ClassicUO's death screen), 0 = none
    public float WaterScaleX, WaterScaleY; // LightWobble instances' scale this frame (ClassicUO's animated water), 1 = off
    // ClassicUO's NoColorObjectsOutOfRange for FlagZTest instances: whole-sprite hue row + 1 (0 = off) where the tile's
    // Chebyshev distance to (RangeX, RangeY) exceeds RangeDist. Wins over TintRow, as in ApplyMeshHue.
    public uint RangeTintRow;
    public int RangeX, RangeY, RangeDist;
    // clip-2 depth model (switch depth_model, default off): 1 = per-pixel geometric depth (sprite.hlsl PSMainGeo, GeoDepth).
    // GeoOff = CamPxY / 22 - the tile sum CamDepth was made from, so geometric and key depths share one origin; GeoScale maps
    // tile-sum units to target depth.
    public uint DepthModel;
    public float GeoOff;
    public float GeoScale;
    public uint GeoPad;
}

/// <summary>Slot of the per-frame override hash (in the frame table): <see cref="Index1"/> = instance index + 1
/// (0 = empty), <see cref="Value"/> = alpha | flags &lt;&lt; 8 | hue row &lt;&lt; 16.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct OverrideEntry
{
    public uint Index1;
    public uint Value;

    /// <summary><see cref="ForceShow"/>: draw even where the max-Z / roof test would hide it (a roof still fading out).
    /// The alpha replaces the baked one (translucent statics bake 178), unless <see cref="KeepAlpha"/>.</summary>
    public const uint Hidden = 1, Highlight = 2, ForceShow = 4, KeepAlpha = 8;
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
