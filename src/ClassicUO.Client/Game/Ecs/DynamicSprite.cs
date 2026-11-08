// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Game
{
    internal enum DynamicSpriteKind : byte
    {
        /// <summary>A whole art sprite (View.DrawStaticAnimated): <see cref="DynamicSprite.Id"/> is the art graphic.</summary>
        Art,

        /// <summary>Rows of an animation frame (MobileView.DrawInternal, ItemView.DrawLayer): <see cref="DynamicSprite.Id"/> is
        /// the pack frame index of (<see cref="DynamicSprite.Body"/>, group, dir, frame).</summary>
        Animation
    }

    [Flags]
    internal enum DynamicSpriteFlags : byte
    {
        None = 0,
        Flip = 1,

        /// <summary>The seated body and its equipment (DrawCharacterSitted's sections, <see cref="DynamicSprite.SitMod"/>).</summary>
        Sitted = 2,

        /// <summary>The frame was selected as equipment (GetAnimationFrames' isEquip).</summary>
        Equip = 4,

        /// <summary>The frame was selected through the corpse alias (GetAnimationFrames' isCorpse).</summary>
        Corpse = 8,

        /// <summary>Art the pack has no pixels for (Legacy select only): drawn with Arts.GetArt's placeholder; X/Y are the
        /// position before the art's draw offset, which comes from the placeholder's size.</summary>
        MissingArt = 16
    }

    /// <summary>
    /// T7b: one dynamic sprite (mobile, world item, corpse) as the select half of the extract produces it: what to draw, where
    /// and how, in the batcher's own terms, with nothing renderer-specific. The Gpu world resolves it to a
    /// <see cref="SpriteInstance"/> (pack page and UV, <see cref="GpuSpriteResolve"/>); a Legacy sink resolves it to an FNA
    /// atlas texture and source rect through the pack-backed Art/Animations loaders, and draws it with the same depth and hue.
    /// The per-object side effects (FrameInfo, the mirror bit, lights, the art entry's picking offset) are the extract's
    /// outputs, not part of the record.
    /// </summary>
    internal struct DynamicSprite
    {
        public DynamicSpriteKind Kind;
        public DynamicSpriteFlags Flags;

        /// <summary>The depth key's object rank (items 6, mobiles 7).</summary>
        public byte Rank;

        /// <summary>Animation: the view's selection (the unresolved body or equipment animation id, action, stored
        /// direction, the clamped frame). Art: 0.</summary>
        public byte Group, Dir, Frame;

        public ushort Body;

        /// <summary>Art: the art graphic with its animated-art offset (a static index, without 0x4000). Animation: the pack
        /// frame index.</summary>
        public uint Id;

        /// <summary>Screen pixel the batcher draws at, without the scene's draw offset. Art: the sprite's untrimmed top-left.
        /// Animation: the untrimmed frame's left edge and row <see cref="SrcY"/>.</summary>
        public int X, Y;

        /// <summary>Animation: the source rows [SrcY, SrcY + SrcH). Art: 0 and the sprite's height.</summary>
        public int SrcY, SrcH;

        /// <summary>The batcher's hue vector (ShaderHueTranslator): X the hue row, Y the hue mode (none, hued, partial,
        /// effect, spectral, shadow), Z the alpha (above 1: alpha + 1 with the circle-of-transparency bit).</summary>
        public Vector3 Hue;

        /// <summary>The batcher depth (the strip's, the shadow's, or the art draw's depth + 0.5).</summary>
        public float Depth;

        /// <summary>The object's tile x (the Gpu depth key's tie break).</summary>
        public int ObjX;

        /// <summary><see cref="DynamicSpriteFlags.Sitted"/>: DrawCharacterSitted's section modifier.</summary>
        public Vector3 SitMod;

        public readonly bool Flipped => (Flags & DynamicSpriteFlags.Flip) != 0;
    }

    /// <summary>The pack tables the Gpu resolve reads (shared with the host's loaders through PackTables).</summary>
    internal readonly struct GpuSpriteTables
    {
        public readonly SpriteEntry[] Sprites;
        public readonly AnimFrameEntry[] AnimFrames;
        public readonly uint[] FramePalette;
        public readonly int HueCount;
        public readonly int AnimPageBase;
        public readonly bool AnimIndexed;

        public GpuSpriteTables(PackSet pack) : this(pack.Sprites, pack.AnimFrames, pack.FramePalette, pack.HueCount, pack.AnimPageBase, pack.AnimIndexed) { }

        public GpuSpriteTables(SpriteEntry[] sprites, AnimFrameEntry[] animFrames, uint[] framePalette, int hueCount, int animPageBase, bool animIndexed)
        {
            Sprites = sprites;
            AnimFrames = animFrames;
            FramePalette = framePalette;
            HueCount = hueCount;
            AnimPageBase = animPageBase;
            AnimIndexed = animIndexed;
        }
    }

    /// <summary>T7b: the Gpu half of the extract: a <see cref="DynamicSprite"/> to the <see cref="SpriteInstance"/>s
    /// GpuWorldRenderer.Emit produces for the same batcher call (none, one, or the seated sections).</summary>
    internal static class GpuSpriteResolve
    {
        public static void Resolve(in GpuSpriteTables t, in DynamicSprite s, int offsetX, int offsetY, ref SpriteInstance[] buffer, ref int count)
        {
            bool flip = s.Flipped;

            if (s.Kind == DynamicSpriteKind.Art)
            {
                Art(in t, s.Id, s.X + offsetX, s.Y + offsetY, s.Hue, s.Depth, s.ObjX, s.Rank, ref buffer, ref count);

                return;
            }

            int before = count;
            WriteFrame(in t, ref buffer, ref count, s.Id, s.X + offsetX, s.Y + offsetY, s.SrcY, s.SrcH, s.Hue, flip, s.Depth, s.ObjX, s.Rank);

            if ((s.Flags & DynamicSpriteFlags.Sitted) != 0 && count > before)
            {
                SitDeform.Split(ref buffer, ref count, before, s.Y + offsetY, s.SrcH, s.SitMod, flip);
            }
        }

        // View.DrawStaticAnimated (no shadow, not wet) + GpuWorldRenderer.Emit for a whole art sprite at (x, y) with the offset
        private static void Art(in GpuSpriteTables t, uint graphic, int x, int y, Vector3 hue, float depth, int objX, int rank,
            ref SpriteInstance[] buffer, ref int count)
        {
            ref readonly SpriteEntry sp = ref t.Sprites[graphic + 0x4000];
            float alpha = hue.Z > 1f ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f || !sp.Present)
            {
                return;
            }

            if (!HueBits(in t, hue, out uint flags, out uint hueRow, out HueMode hueMode))
            {
                return;
            }

            if (count >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            ref SpriteInstance inst = ref buffer[count++];
            inst.X = x + sp.TrimX;
            inst.Y = y + sp.TrimY;
            inst.Whf = SpriteInstance.PackWhf(sp.W, sp.H, flags);
            inst.Uv = (uint)sp.X | ((uint)sp.Y << 16);
            inst.PageHue = (uint)sp.Page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, objX, rank);
            inst.Light = 0;
            inst.Corners = 0;
            inst.Mode = hueMode;

            if (hue.Z > 1f)
            {
                inst.SetLightBits(SpriteInstance.LightCot);
            }

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }
        }

        // GpuWorldRenderer.Emit's hue translation; false = a hue type the world pass doesn't draw
        internal static bool HueBits(in GpuSpriteTables t, Vector3 hue, out uint flags, out uint hueRow, out HueMode hueMode)
        {
            flags = 0;
            hueRow = 0;
            hueMode = HueMode.Normal;
            int type = (int)hue.Y;
            int row = (int)hue.X;

            switch (type)
            {
                case ShaderHueTranslator.SHADER_NONE:
                    return true;

                case ShaderHueTranslator.SHADER_HUED:
                case ShaderHueTranslator.SHADER_PARTIAL_HUED:
                case ShaderHueTranslator.SHADER_EFFECT_HUED:
                    if (row >= 0 && row < t.HueCount)
                    {
                        hueRow = (uint)row;
                        flags = SpriteInstance.FlagHued | (type == ShaderHueTranslator.SHADER_PARTIAL_HUED ? SpriteInstance.FlagPartial : 0);
                    }

                    if (type == ShaderHueTranslator.SHADER_EFFECT_HUED)
                    {
                        hueMode = HueMode.EffectHued;
                    }

                    return true;

                case ShaderHueTranslator.SHADER_SPECTRAL:
                    hueMode = HueMode.Spectral;

                    return true;

                case ShaderHueTranslator.SHADER_SHADOW:
                    hueMode = HueMode.Shadow;

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// GpuWorldRenderer.Emit for pack animation frame <paramref name="index"/> drawn from source rows [srcY, srcY + srcH)
        /// at target pixel (px, py) (screen position plus the draw offset), appended to <paramref name="buffer"/>.
        /// </summary>
        internal static void WriteFrame(in GpuSpriteTables t, ref SpriteInstance[] buffer, ref int count, uint index, int px, int py, int srcY, int srcH,
            Vector3 hue, bool flip, float depth, int objX, int rank)
        {
            float alpha = hue.Z > 1f ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f)
            {
                return;
            }

            if (!HueBits(in t, hue, out uint flags, out uint hueRow, out HueMode hueMode))
            {
                return;
            }

            ref readonly SpriteEntry sp = ref t.AnimFrames[index].Sprite;

            if (!sp.Present)
            {
                return;
            }

            int page = t.AnimPageBase + sp.Page;
            uint palette = 0;

            if (t.AnimIndexed)
            {
                flags |= SpriteInstance.FlagIndexed;
                palette = t.FramePalette[index];
            }

            int rx0 = 0, ry0 = srcY;
            int rx1 = sp.OrigW, ry1 = srcY + srcH;
            int ix0 = Math.Max(rx0, sp.TrimX), iy0 = Math.Max(ry0, sp.TrimY);
            int ix1 = Math.Min(rx1, sp.TrimX + sp.W), iy1 = Math.Min(ry1, sp.TrimY + sp.H);

            if (ix1 <= ix0 || iy1 <= iy0)
            {
                return;
            }

            int w = ix1 - ix0, h = iy1 - iy0;
            int dx = flip ? rx1 - ix1 : ix0 - rx0;
            int dy = iy0 - ry0;
            int x, y;

            if (hueMode == HueMode.Shadow)
            {
                int fullH = ry1 - ry0;
                x = px + dx + (fullH - dy - h) / 2;
                y = py + (fullH + dy - h) / 2;
            }
            else
            {
                x = px + dx;
                y = py + dy;
            }

            if (count >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            ref SpriteInstance inst = ref buffer[count++];
            inst.X = x;
            inst.Y = y;
            inst.Whf = SpriteInstance.PackWhf(w, h, flags | (flip ? SpriteInstance.FlagMirror : 0));
            inst.Uv = (uint)(sp.X + (ix0 - sp.TrimX)) | ((uint)(sp.Y + (iy0 - sp.TrimY)) << 16);
            inst.PageHue = (uint)page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, objX, rank);
            inst.Light = 0;
            inst.Corners = palette;
            inst.Mode = hueMode;

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }
        }
    }
}
