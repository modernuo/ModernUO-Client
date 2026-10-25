// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Ecs;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>
    /// Turns a mobile's ECS animation components into Uo.Render <see cref="SpriteInstance"/>s straight from the pack's
    /// animation tables — the same instances GpuWorldRenderer's view capture produces for MobileView.Draw (shadows, mount,
    /// body, equipment in paint order, strips, trim, mirroring, hue mode, depth key), with no atlas and no batcher.
    /// Screen position and base depth are inputs (W2's ScreenPos); not yet the renderer's source (see EcsParity).
    /// </summary>
    internal sealed class MobileSpriteExtract
    {
        private const int RANK_MOBILE = 7;
        private const int MAX_STRIPS = 512;

        private readonly AnimationsLoader _anims;
        private PackSet _pack;
        private SpriteInstance[] _out = new SpriteInstance[256];
        private int _count;
        private int _offsetX, _offsetY;
        private int _objX;

        public MobileSpriteExtract(AnimationsLoader anims)
        {
            _anims = anims;
        }

        public ReadOnlySpan<SpriteInstance> Instances => new ReadOnlySpan<SpriteInstance>(_out, 0, _count);

        public int Count => _count;

        public void Begin(PackSet pack, int offsetX, int offsetY)
        {
            _pack = pack;
            _offsetX = offsetX;
            _offsetY = offsetY;
            _count = 0;
        }

        public void Clear() => _count = 0;

        /// <summary>Appends the instances of one mobile drawn at screen position (<paramref name="posX"/>, <paramref name="posY"/>) with the scene's base depth.</summary>
        public void Emit(EcsEntity e, int posX, int posY, float depth)
        {
            ref readonly AnimationState s = ref e.GetComponent<AnimationState>();

            if ((s.Flags & AnimStateFlags.Visible) == 0 || _pack == null || !_pack.HasAnims)
            {
                return;
            }

            ref readonly MobileRenderState rs = ref e.GetComponent<MobileRenderState>();
            ref readonly EquipmentSlots eq = ref e.GetComponent<EquipmentSlots>();
            _objX = e.GetComponent<Position>().X;
            ushort bodyHue = e.GetComponent<Hue>().Value;

            int drawX = posX + rs.OffsetX + 22;
            int drawY = posY - 3 + rs.OffsetY + 22;
            float alpha = rs.Alpha / 255f;
            bool mirror = (s.Flags & AnimStateFlags.Mirror) != 0;
            bool shadow = (s.Flags & AnimStateFlags.Shadow) != 0;
            bool charSitting = (s.Flags & AnimStateFlags.CharSitting) != 0;
            byte frame = s.Frame;
            int mountOffset = s.MountOffsetY;
            ushort over = rs.OverrideHue;

            if ((s.Flags & AnimStateFlags.MountDrawn) != 0)
            {
                if (shadow)
                {
                    Layer(s.Body, s.Group, s.Dir, frame, false, true, drawX, drawY + 10, mirror, alpha, depth, mountOffset, over, 0, false, false, false, 0, charSitting);
                    Layer(s.MountBody, s.MountGroup, s.Dir, frame, false, true, drawX, drawY, mirror, alpha, depth, mountOffset, over, 0, false, false, false, 0, charSitting);
                }

                Layer(s.MountBody, s.MountGroup, s.Dir, frame, false, false, drawX, drawY, mirror, alpha, depth, mountOffset, over, eq.MountHue, true, true, false, 0, charSitting);
                drawY += mountOffset;
            }
            else if ((s.Flags & AnimStateFlags.Sitting) != 0)
            {
                drawX += s.SitX;
                drawY += s.SitY;
            }
            else if (shadow)
            {
                Layer(s.Body, s.Group, s.Dir, frame, false, true, drawX, drawY, mirror, alpha, depth, mountOffset, over, 0, false, false, false, 0, charSitting);
            }

            Layer(s.Body, s.Group, s.Dir, frame, false, false, drawX, drawY, mirror, alpha, depth, mountOffset, over, bodyHue, false, false, false, 0, charSitting);

            int n = eq.Count;

            if (n == 0)
            {
                return;
            }

            // the facing-dependent cloak move (PaperdollOrder.ApplyDirectionCloak) on the slot indices
            Span<byte> order = stackalloc byte[EquipmentSlots.MAX];
            Span<byte> layerOf = stackalloc byte[EquipmentSlots.MAX];

            for (int i = 0; i < n; i++)
            {
                order[i] = (byte)i;
                layerOf[i] = eq.Slots[i].Layer;
            }

            n = AnimationRules.ApplyDirectionCloak(order, n, layerOf, s.Facing);

            for (int i = 0; i < n; i++)
            {
                ref readonly EquipSlot slot = ref eq.Slots[order[i]];
                byte group = slot.Group == 0xFF ? s.Group : slot.Group;

                Layer
                (
                    slot.DrawBody,
                    group,
                    s.Dir,
                    frame,
                    true,
                    false,
                    drawX,
                    drawY,
                    mirror,
                    alpha,
                    depth,
                    mountOffset,
                    over,
                    slot.Hue,
                    false,
                    true,
                    (slot.Bits & EquipSlotBits.PartialHue) != 0,
                    slot.ConvColor,
                    charSitting
                );
            }
        }

        /// <summary>The frames of (body, action, stored dir) as Animations.GetAnimationFrames resolves them.</summary>
        public bool TryGetFrames(ushort id, byte action, byte dir, bool isEquip, out uint first, out int count, out ushort hueFromFile)
        {
            first = 0;
            count = 0;
            hueFromFile = 0;

            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS || id >= _anims.BodyCount)
            {
                return false;
            }

            id = _anims.ResolveBody(id, false, out hueFromFile);
            var type = (AnimationGroupsType)_anims.GetBody(id).Type;

            if (isEquip && type != AnimationGroupsType.Equipment && type != AnimationGroupsType.Human)
            {
                return false;
            }

            if (!_anims.TryGetDirection(id, action, dir + 3, out AnimDirEntry entry))
            {
                return false;
            }

            first = entry.FirstFrame;
            count = entry.FrameCount;

            return count > 0;
        }

        // MobileView.DrawInternal
        private void Layer
        (
            ushort id,
            byte group,
            byte dir,
            byte frameIndex,
            bool isEquip,
            bool isShadow,
            int x,
            int y,
            bool mirror,
            float alpha,
            float depth,
            int mountOffset,
            ushort overrideHue,
            ushort layerHue,
            bool isMount,
            bool isItem,
            bool itemPartial,
            ushort convColor,
            bool charSitting
        )
        {
            if (id >= _anims.BodyCount || !TryGetFrames(id, group, dir, isEquip, out uint first, out int count, out ushort hueFromFile))
            {
                return;
            }

            if (frameIndex >= count)
            {
                frameIndex = (byte)(count - 1);
            }

            uint index = first + frameIndex;
            ref readonly AnimFrameEntry fe = ref _pack.AnimFrames[index];
            int w = fe.Sprite.OrigW, h = fe.Sprite.OrigH;

            // no texture: nothing is drawn (the seated body's SKIP path draws nothing either)
            if (w == 0 || h == 0)
            {
                return;
            }

            x -= mirror ? w - fe.CenterX : fe.CenterX;
            y -= h + fe.CenterY;

            if (isShadow)
            {
                Write(index, x, y, 0, h, new Vector3(0, ShaderHueTranslator.SHADER_SHADOW, 1f), mirror, depth);

                return;
            }

            ushort hue = AnimationRules.LayerHue(overrideHue, layerHue, isMount, isItem, itemPartial, hueFromFile, convColor, out bool partial);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);

            if (charSitting)
            {
                Write(index, x, y, 0, h, hueVec, mirror, depth + 1f);

                return;
            }

            Span<(int Y, int H)> strips = stackalloc (int, int)[Math.Min(AnimationRules.MaxStrips(h), MAX_STRIPS)];
            int n = AnimationRules.Strips(h, fe.CenterY, mountOffset, strips);

            for (int i = 0; i < n; i++)
            {
                if (strips[i].H > 0)
                {
                    Write(index, x, y + strips[i].Y, strips[i].Y, strips[i].H, hueVec, mirror, depth + 1f + i * 2);
                }
            }
        }

        // GpuWorldRenderer.Emit for an animation frame drawn from source rows [srcY, srcY + srcH) at (px, py)
        private void Write(uint index, int px, int py, int srcY, int srcH, Vector3 hue, bool flip, float depth)
        {
            float alpha = hue.Z > 1f ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f)
            {
                return;
            }

            uint flags = 0;
            uint hueRow = 0;
            var hueMode = HueMode.Normal;
            int type = (int)hue.Y;
            int row = (int)hue.X;

            switch (type)
            {
                case ShaderHueTranslator.SHADER_NONE:
                    break;

                case ShaderHueTranslator.SHADER_HUED:
                case ShaderHueTranslator.SHADER_PARTIAL_HUED:
                case ShaderHueTranslator.SHADER_EFFECT_HUED:
                    if (row >= 0 && row < _pack.HueCount)
                    {
                        hueRow = (uint)row;
                        flags = SpriteInstance.FlagHued | (type == ShaderHueTranslator.SHADER_PARTIAL_HUED ? SpriteInstance.FlagPartial : 0);
                    }

                    if (type == ShaderHueTranslator.SHADER_EFFECT_HUED)
                    {
                        hueMode = HueMode.EffectHued;
                    }

                    break;

                case ShaderHueTranslator.SHADER_SPECTRAL:
                    hueMode = HueMode.Spectral;

                    break;

                case ShaderHueTranslator.SHADER_SHADOW:
                    hueMode = HueMode.Shadow;

                    break;

                default:
                    return;
            }

            ref readonly SpriteEntry sp = ref _pack.AnimFrames[index].Sprite;

            if (!sp.Present)
            {
                return;
            }

            int page = _pack.AnimPageBase + sp.Page;
            uint palette = 0;

            if (_pack.AnimIndexed)
            {
                flags |= SpriteInstance.FlagIndexed;
                palette = _pack.FramePalette[index];
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
            px += _offsetX;
            py += _offsetY;
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

            if (_count >= _out.Length)
            {
                Array.Resize(ref _out, _out.Length * 2);
            }

            ref SpriteInstance inst = ref _out[_count++];
            inst.X = x;
            inst.Y = y;
            inst.Whf = SpriteInstance.PackWhf(w, h, flags | (flip ? SpriteInstance.FlagMirror : 0));
            inst.Uv = (uint)(sp.X + (ix0 - sp.TrimX)) | ((uint)(sp.Y + (iy0 - sp.TrimY)) << 16);
            inst.PageHue = (uint)page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, _objX, RANK_MOBILE);
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
