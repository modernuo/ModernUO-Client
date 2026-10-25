// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Uo.Pack;
using Uo.Render;

namespace ClassicUO.Game
{
    /// <summary>
    /// W3 for world items drawn as art (ItemView.Draw minus corpses and multis): the <see cref="SpriteInstance"/>s the
    /// Gpu world's view capture produces — the stack's second copy, the animated-art frame, the per-frame hue rules
    /// (selection, container highlight, field replacement, out of range, dead gray, hidden) — straight from the pack's art
    /// table, with no atlas and no batcher. Like <see cref="MobileAnimationSystem"/> it samples the Item until the ECS
    /// owns the world update (W5). Side effects of the view kept: the light request and the art entry's draw offset that
    /// picking reads (ArtEntry.Width/Height).
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

        /// <summary>True when the extract reproduces this item's view (art items; corpses and multis keep the capture).</summary>
        public static bool Handles(Item item) => !item.IsCorpse && !item.IsMulti;

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

            ushort hue = item.Hue;
            ushort graphic = displayed;
            bool partial = item.ItemData.IsPartialHue;

            if (item.OnGround)
            {
                if (item.ItemData.IsAnimated && profile.FieldsType == 2)
                {
                    ushort g = item.Graphic;

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

                if (item.ItemData.IsContainer && SelectedObject.SelectedContainer == item)
                {
                    hue = 0x0035;
                    partial = false;
                }
            }

            bool selected = ReferenceEquals(SelectedObject.Object, item);

            if (profile.HighlightGameObjects && selected)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (profile.NoColorObjectsOutOfRange && item.Distance > world.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (world.Player.IsDead && profile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else if (!item.IsLocked && selected)
            {
                hue = 0x0035;
            }
            else if (item.IsHidden)
            {
                hue = 0x038E;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);
            int objX = item.X;

            if (!item.IsCoin && item.Amount > 1 && item.ItemData.IsStackable)
            {
                Art(pack, graphic, posX - 5, posY - 5, hueVec, depth, objX, offsetX, offsetY);
            }

            Art(pack, graphic, posX, posY, hueVec, depth, objX, offsetX, offsetY);

            return true;
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
