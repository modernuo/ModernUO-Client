// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Game
{
    /// <summary>
    /// W3 for world items (ItemView.Draw): the <see cref="SpriteInstance"/>s the Gpu world's view capture produces —
    /// for art items and multis the stack's second copy, the animated-art frame, the per-frame hue rules (selection,
    /// container highlight, field replacement, out of range, dead gray, hidden, the multi placement preview) from the pack's
    /// art table; for corpses the body and the worn layers in paint order from the pack's animation tables (DrawCorpse) —
    /// with no atlas and no batcher. Like <see cref="MobileAnimationSystem"/> it samples the Item until the ECS
    /// owns the world update (W5). Side effects of the view kept: the light request and the art entry's draw offset that
    /// picking reads (ArtEntry.Width/Height), and a corpse's mirror bit (GetAnimDirection history; corpse picking reads it).
    /// </summary>
    internal sealed class ItemSpriteExtract
    {
        private const int RANK_ITEM = 6;

        private SpriteInstance[] _out = new SpriteInstance[8];
        private int _count;

        public ReadOnlySpan<SpriteInstance> Instances => new ReadOnlySpan<SpriteInstance>(_out, 0, _count);

        /// <summary>The view would add a light for this item at (LightX, LightY) (GameScene.AddLight(item, item, …)).</summary>
        public bool Light { get; private set; }

        public int LightX { get; private set; }

        public int LightY { get; private set; }

        /// <summary>True when the extract reproduces this item's view: every world item (art items, multis and corpses).</summary>
        public static bool Handles(Item item) => true;

        /// <summary>ItemView.Draw for one item at screen position (<paramref name="posX"/>, <paramref name="posY"/>). False = not drawn.</summary>
        public bool Emit(Item item, PackSet pack, int offsetX, int offsetY, int posX, int posY, float depth)
        {
            _count = 0;
            Light = false;

            if (item.IsDestroyed)
            {
                return false;
            }

            ushort displayed = item.DisplayedGraphic;

            if (item.ItemData.IsLight || displayed >= 0x3E02 && displayed <= 0x3E0B || displayed >= 0x3914 && displayed <= 0x3929)
            {
                Light = true;
                LightX = posX + 22;
                LightY = posY + 22;
            }

            if (!item.AllowedToDraw)
            {
                return false;
            }

            posX += (int)item.Offset.X;
            posY += (int)(item.Offset.Y + item.Offset.Z);
            float alpha = item.AlphaHue / 255f;
            Profile profile = ProfileManager.CurrentProfile;
            World world = item.World;

            if (item.IsCorpse)
            {
                return EmitCorpse(item, pack, offsetX, offsetY, posX, posY - 3, ShaderHueTranslator.GetHueVector(0, false, alpha).Z, depth, profile);
            }

            bool selected = ReferenceEquals(SelectedObject.Object, item);

            var hueInput = new HueInput
            {
                Hue = item.Hue,
                Graphic = displayed,
                RawGraphic = item.Graphic,
                Partial = item.ItemData.IsPartialHue,
                OnGround = item.OnGround,
                ReplaceFields = item.ItemData.IsAnimated && profile.FieldsType == 2,
                SelectedContainer = item.ItemData.IsContainer && SelectedObject.SelectedContainer == item,
                HighlightSelected = profile.HighlightGameObjects && selected,
                OutOfRange = profile.NoColorObjectsOutOfRange && item.Distance > world.ClientViewRange,
                PlayerDeadGray = world.Player.IsDead && profile.EnableBlackWhiteEffect,
                Selected = selected,
                Locked = item.IsLocked,
                Multi = item.IsMulti,
                Hidden = item.IsHidden
            };

            ChooseHue(in hueInput, out ushort graphic, out ushort hue, out bool partial);

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);
            int objX = item.X;

            if (!item.IsMulti && !item.IsCoin && item.Amount > 1 && item.ItemData.IsStackable)
            {
                Art(pack, graphic, posX - 5, posY - 5, hueVec, depth, objX, offsetX, offsetY);
            }

            // the house placement preview (a local multi item without a serial) is drawn half transparent
            if (!SerialHelper.IsValid(item.Serial) && item.IsMulti && world.TargetManager.TargetingState == CursorTarget.MultiPlacement)
            {
                hueVec.Z = 0.5f;
            }

            Art(pack, graphic, posX, posY, hueVec, depth, objX, offsetX, offsetY);

            return true;
        }

        /// <summary>What ItemView.Draw's art hue choice reads from the item, the profile and the selection.</summary>
        internal struct HueInput
        {
            public ushort Hue;
            public ushort Graphic;       // DisplayedGraphic
            public ushort RawGraphic;    // Item.Graphic, which the field tests use
            public bool Partial;
            public bool OnGround;
            public bool ReplaceFields;   // animated art and the Fields option is 2
            public bool SelectedContainer;
            public bool HighlightSelected;
            public bool OutOfRange;
            public bool PlayerDeadGray;
            public bool Selected;
            public bool Locked;
            public bool Multi;
            public bool Hidden;
        }

        /// <summary>ItemView.Draw's graphic, hue and partial-hue choice: field replacement and the selected container (ground
        /// items), then the first that applies of highlight, out of range, player dead gray, mouse selection and hidden.</summary>
        internal static void ChooseHue(in HueInput i, out ushort graphic, out ushort hue, out bool partial)
        {
            hue = i.Hue;
            graphic = i.Graphic;
            partial = i.Partial;

            if (i.OnGround)
            {
                if (i.ReplaceFields)
                {
                    ushort g = i.RawGraphic;

                    if (StaticFilters.IsFireField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0020;
                    }
                    else if (StaticFilters.IsParalyzeField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0058;
                    }
                    else if (StaticFilters.IsEnergyField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0070;
                    }
                    else if (StaticFilters.IsPoisonField(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x0044;
                    }
                    else if (StaticFilters.IsWallOfStone(g))
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                        hue = 0x038A;
                    }
                }

                if (i.SelectedContainer)
                {
                    hue = 0x0035;
                    partial = false;
                }
            }

            if (i.HighlightSelected)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (i.OutOfRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (i.PlayerDeadGray)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else if (!i.Locked && !i.Multi && i.Selected)
            {
                hue = 0x0035;
            }
            else if (i.Hidden)
            {
                hue = 0x038E;
            }
        }

        // ItemView.DrawCorpse: the body, then the worn layers in paint order (PaperdollOrder.BuildInWorld)
        private bool EmitCorpse(Item item, PackSet pack, int offsetX, int offsetY, int posX, int posY, float alpha, float depth, Profile profile)
        {
            World world = item.World;

            if (world.CorpseManager.Exists(item.Serial, 0) || pack == null || !pack.HasAnims)
            {
                return false;
            }

            posX += 22;
            posY += 22;

            byte direction = (byte)((byte)item.Layer & 0x7F & 7);
            bool flipped = item.ViewFlipped;
            var animations = Client.Game.UO.Animations;
            animations.GetAnimDirection(ref direction, ref flipped);
            item.ViewFlipped = flipped;

            ushort graphic = item.GetGraphicForAnimation();
            ushort converted = graphic;
            animations.ConvertBodyIfNeeded(ref converted, isCorpse: true);
            AnimationsLoader anims = Client.Game.UO.FileManager.Animations;
            byte group = anims.GetDeathAction(converted, animations.GetAnimFlags(converted), animations.GetAnimType(converted), item.UsedLayer);
            bool human = IsHumanCorpse(item.Amount);
            int x = posX + offsetX, y = posY + offsetY;

            CorpseLayer(item, pack, anims, Layer.Invalid, item.AnimIndex, human, item.Hue, flipped, alpha, group, direction, x, y, depth, profile);

            Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
            int layerCount = PaperdollOrder.BuildInWorld(item, altTorsoTable: false, direction, layers);

            for (int i = 0; i < layerCount; i++)
            {
                CorpseLayer(item, pack, anims, layers[i], item.AnimIndex, human, 0, flipped, alpha, group, direction, x, y, depth, profile);
            }

            return true;
        }

        private static bool IsHumanCorpse(ushort body) =>
            body >= 0x0190 && body <= 0x0193 || body >= 0x00B7 && body <= 0x00BA || body >= 0x025D && body <= 0x0260 ||
            body >= 0x029A && body <= 0x029B || body >= 0x02B6 && body <= 0x02B7 ||
            body == 0x03DB || body == 0x03DF || body == 0x03E2 || body == 0x02E8 || body == 0x02E9;

        // ItemView.DrawLayer: one layer's frame at target pixel (x, y) (screen position + draw offset), in strips
        private void CorpseLayer(Item owner, PackSet pack, AnimationsLoader anims, Layer layer, byte animIndex, bool human, ushort color,
            bool flipped, float alpha, byte group, byte dir, int x, int y, float depth, Profile profile)
        {
            bool partial = false;
            bool hasConv = false;
            ushort convColor = 0;
            ushort graphic;

            if (layer == Layer.Invalid)
            {
                graphic = owner.GetGraphicForAnimation();
            }
            else if (human)
            {
                Item equip = owner.FindItemByLayer(layer);

                if (equip == null)
                {
                    return;
                }

                graphic = equip.ItemData.AnimID;
                partial = equip.ItemData.IsPartialHue;

                // as the view: the conversion table is looked up by the item's animation id twice
                if (anims.EquipConversions.TryGetValue(graphic, out var map) && map.TryGetValue(graphic, out EquipConvData data))
                {
                    hasConv = true;
                    convColor = data.Color;
                    graphic = data.Graphic;
                }

                color = equip.Hue;
            }
            else
            {
                return;
            }

            if (!MobileSpriteExtract.TryGetFrames(anims, graphic, group, dir, layer != Layer.Invalid, layer == Layer.Invalid,
                    out uint first, out int count, out ushort fileHue))
            {
                return;
            }

            if (color == 0)
            {
                color = fileHue;
            }

            if (animIndex >= count)
            {
                animIndex = (byte)(count - 1);
            }

            uint index = first + animIndex;
            ref readonly AnimFrameEntry fe = ref pack.AnimFrames[index];
            int w = fe.Sprite.OrigW, h = fe.Sprite.OrigH;

            // no texture
            if (w == 0 || h == 0)
            {
                return;
            }

            x -= flipped ? w - fe.CenterX : fe.CenterX;
            y -= h + fe.CenterY;

            if (color == 0 && hasConv)
            {
                color = convColor;
                partial = false;
            }

            Vector3 hueVec;
            World world = owner.World;

            if (profile.NoColorObjectsOutOfRange && owner.Distance > world.ClientViewRange)
            {
                hueVec = ShaderHueTranslator.GetHueVector(Constants.OUT_RANGE_COLOR + 1, false, 1);
            }
            else if (world.Player.IsDead && profile.EnableBlackWhiteEffect)
            {
                hueVec = ShaderHueTranslator.GetHueVector(Constants.DEAD_RANGE_COLOR + 1, false, 1);
            }
            else
            {
                if (profile.GridLootType > 0 && SelectedObject.CorpseObject == owner)
                {
                    color = 0x0034;
                }
                else if (profile.HighlightGameObjects && ReferenceEquals(SelectedObject.Object, owner))
                {
                    color = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                }

                hueVec = ShaderHueTranslator.GetHueVector(color, partial, alpha);
            }

            // the view's strips: rows [0, value) and [value, h) with value = max(1, h + centre y); later strips are empty
            int value = Math.Max(1, h + fe.CenterY);
            int strips = Math.Max(h / value + 1, 2);
            int srcY = 0, srcH = Math.Min(value, h), remains = h - srcH;

            for (int i = 0; i < strips; i++)
            {
                MobileSpriteExtract.WriteFrame(pack, ref _out, ref _count, index, x, y + srcY, srcY, srcH, hueVec, flipped, depth + 1f + i * 2, owner.X, RANK_ITEM);
                srcY += srcH;
                srcH = remains;
                remains -= srcH;
            }
        }

        // View.DrawStaticAnimated (no shadow, not wet) + GpuWorldRenderer.Emit for a whole art sprite
        private void Art(PackSet pack, ushort graphic, int x, int y, Vector3 hue, float depth, int objX, int offsetX, int offsetY)
        {
            var arts = Client.Game.UO.FileManager.Arts;
            graphic = (ushort)(graphic + arts.GetEntry(graphic + 0x4000).AnimOffset);
            uint idx = (uint)graphic + 0x4000;

            if (idx >= pack.Sprites.Length)
            {
                return;
            }

            ref readonly SpriteEntry sp = ref pack.Sprites[idx];
            int ow = sp.OrigW, oh = sp.OrigH;

            if (ow == 0 || oh == 0)
            {
                return;
            }

            // the draw offset the view stores on the art entry; item picking reads it
            ref ArtEntry index = ref arts.GetEntry(graphic + 0x4000);
            index.Width = (short)((ow >> 1) - 22);
            index.Height = (short)(oh - 44);
            x -= index.Width;
            y -= index.Height;

            float alpha = hue.Z > 1f ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f || !sp.Present)
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
                    if (row >= 0 && row < pack.HueCount)
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

            if (_count >= _out.Length)
            {
                Array.Resize(ref _out, _out.Length * 2);
            }

            ref SpriteInstance inst = ref _out[_count++];
            inst.X = x + offsetX + sp.TrimX;
            inst.Y = y + offsetY + sp.TrimY;
            inst.Whf = SpriteInstance.PackWhf(sp.W, sp.H, flags);
            inst.Uv = (uint)sp.X | ((uint)sp.Y << 16);
            inst.PageHue = (uint)sp.Page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round((depth + 0.5f) * 100f) - 50, objX, RANK_ITEM);
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
    }
}
