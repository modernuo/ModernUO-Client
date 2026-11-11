// SPDX-License-Identifier: BSD-2-Clause

using Uo.Pack;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Mobile
    {
        /// <summary>The aura Draw paints under the mobile (the Gpu world's ECS sprite path draws it through the batcher).</summary>
        internal void DrawAura(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (World.AuraManager.IsEnabled)
            {
                DrawAuraAt(batcher, posX + (int)Offset.X + 22, posY - 3 + (int)(Offset.Y - Offset.Z) + 22, depth);
            }
        }

        private void DrawAuraAt(UltimaBatcher2D batcher, int drawX, int drawY, float depth)
        {
            World.AuraManager.Draw(
                batcher,
                drawX,
                drawY,
                ProfileManager.CurrentProfile.PartyAura && World.Party.Contains(this)
                    ? ProfileManager.CurrentProfile.PartyAuraHue
                    : Notoriety.GetHue(NotorietyFlag),
                depth
            );
        }

        /// <summary>The hit-test graphic of a worn item: the remap from the layering rules data (T3b), then the equip conversion.</summary>
        private static ushort GetAnimationInfo(Mobile owner, Item item, LayerSubject subject, LayerDecision cache)
        {
            if (item.ItemData.AnimID != 0)
            {
                // the remap rules' conditions decide (the defaults need the gargoyle draw flag), then the equip conversion;
                // T3b cache: the worn layer's remap from the mobile's cached answer
                ushort graphic = cache != null ? cache.Remap(item.Layer) : LayerRuleEval.Remap(item.ItemData.AnimID, subject);

                if (
                    Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                        owner.Graphic,
                        out Dictionary<ushort, EquipConvData> map
                    )
                )
                {
                    if (map.TryGetValue(item.ItemData.AnimID, out EquipConvData data))
                    {
                        graphic = data.Graphic;
                    }
                }

                return graphic;
            }

            return 0xFFFF;
        }

        private static bool GetTexture(
            ushort graphic,
            byte animGroup,
            ref byte animIndex,
            byte direction,
            out SpriteInfo spriteInfo,
            out bool isUOP,
            bool sync = false
        )
        {
            spriteInfo = default;

            var frames = Client.Game.UO.Animations.GetAnimationFrames(
                graphic,
                animGroup,
                direction,
                out _,
                out isUOP,
                sync: sync
            );

            if (frames.Length == 0)
            {
                return false;
            }

            if (animIndex < 0)
            {
                animIndex = 0;
            }

            animIndex = (byte)(animIndex % frames.Length);

            spriteInfo = frames[animIndex];

            if (spriteInfo.Texture == null)
            {
                return false;
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            Point position = RealScreenPosition;
            position.Y -= 3;
            position.X += (int)Offset.X + 22;
            position.Y += (int)(Offset.Y - Offset.Z) + 22;

            // T7b part 2b: the last draw's frame rectangle and mirror bit from the extract's records when it drew this mobile
            // (the Legacy sink, or the Gpu world's extracts), else the ones the draw left on the mobile
            if (!Scenes.SpritePickTable.TryMobile(this, out Rectangle r, out bool pickFlipped))
            {
                r = FrameInfo;
                pickFlipped = IsFlipped;
            }

            r.X = position.X - r.X;
            r.Y = position.Y - r.Y;

            if (!r.Contains(SelectedObject.TranslatedMousePositionByViewport))
            {
                return false;
            }

            bool isHuman = IsHuman;
            bool isGargoyle =
                Client.Game.UO.Version >= ClientVersion.CV_7000
                && (Graphic == 666 || Graphic == 667 || Graphic == 0x02B7 || Graphic == 0x02B6);

            var animations = Client.Game.UO.Animations;

            byte dir = StepFacing();
            bool isFlipped = pickFlipped;
            animations.GetAnimDirection(ref dir, ref isFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            byte animGroupBackup = animGroup;
            byte animIndexBackup = animIndex;

            SpriteInfo spriteInfo;
            bool isUop;
            // anim_frame_count_tables: the advance no longer loads the body's current direction (it never loaded the mount's
            // or the equipment's), so the first hover does, synchronously: a worker load (first sight) would leave the drawn
            // mobile unpickable for a few frames in Gpu mode
            bool syncPick = AnimFrameCount.UseTables;

            if (isHuman)
            {
                Item mount = FindItemByLayer(Layer.Mount);
                if (mount != null)
                {
                    var mountGraphic = mount.GetGraphicForAnimation();

                    if (mountGraphic != 0xFFFF)
                    {
                        var animGroupMount = GetGroupForAnimation(this, mountGraphic);

                        if (
                            GetTexture(
                                mountGraphic,
                                animGroupMount,
                                ref animIndex,
                                dir,
                                out spriteInfo,
                                out isUop
                            )
                        )
                        {
                            int x =
                                position.X
                                - (
                                    isFlipped
                                        ? spriteInfo.UV.Width - spriteInfo.Center.X
                                        : spriteInfo.Center.X
                                );
                            int y = position.Y - (spriteInfo.UV.Height + spriteInfo.Center.Y);

                            if (
                                animations.PixelCheck(
                                    mountGraphic,
                                    animGroupMount,
                                    dir,
                                    isUop,
                                    animIndex,
                                    isFlipped
                                        ? x
                                            + spriteInfo.UV.Width
                                            - SelectedObject.TranslatedMousePositionByViewport.X
                                        : SelectedObject.TranslatedMousePositionByViewport.X - x,
                                    SelectedObject.TranslatedMousePositionByViewport.Y - y
                                )
                            )
                            {
                                return true;
                            }

                            position.Y += AnimationRules.MountOffsetY(mount.Graphic, Client.Game.UO.Animations.GetMountSeat(mountGraphic));
                        }
                    }
                }
            }

            if (GetTexture(graphic, animGroup, ref animIndex, dir, out spriteInfo, out isUop, syncPick))
            {
                int x =
                    position.X
                    - (isFlipped ? spriteInfo.UV.Width - spriteInfo.Center.X : spriteInfo.Center.X);
                int y = position.Y - (spriteInfo.UV.Height + spriteInfo.Center.Y);

                if (
                    animations.PixelCheck(
                        graphic,
                        animGroup,
                        dir,
                        isUop,
                        animIndex,
                        isFlipped
                            ? x
                                + spriteInfo.UV.Width
                                - SelectedObject.TranslatedMousePositionByViewport.X
                            : SelectedObject.TranslatedMousePositionByViewport.X - x,
                        SelectedObject.TranslatedMousePositionByViewport.Y - y
                    )
                )
                {
                    return true;
                }
            }

            // T3a: any worn item, from the ECS (WorldState.HasWornItems); perf-10: validated once for the loop (WornItems)
            WornItems worn = isHuman ? World.State.WornView(this) : default;

            if (isHuman && worn.Any)
            {
                // T3b: the covering and the gargoyle remaps from the layering rules data
                LayerTables layerTables = default;
                LayerSubject layerSubject;
                // T3b cache: the covering and the remaps from the mobile's cached answer (null: evaluate)
                LayerDecision layerCache = worn.Layers(isGargoyle);

                if (layerCache != null)
                {
                    layerSubject = layerCache.Subject;
                }
                else
                {
                    layerTables.Gather(this);
                    layerSubject = LayerRuleEval.Subject(Graphic, IsFemale, isGargoyle);
                }

                for (Layer layer = Layer.Invalid + 1; layer < Layer.Mount; ++layer)
                {
                    Item item = worn[layer];

                    if (item == null || (IsDead && (layer == Layer.Hair || layer == Layer.Beard)))
                    {
                        continue;
                    }

                    if (layerCache != null ? layerCache.IsCovered(layer) : LayerRuleEval.IsCovered(LayerRuleSet.World, layer, ref layerTables, layerSubject))
                    {
                        continue;
                    }

                    graphic = GetAnimationInfo(this, item, layerSubject, layerCache);

                    if (graphic != 0xFFFF)
                    {
                        animGroup = animGroupBackup;
                        animIndex = animIndexBackup;

                        if (
                            GetTexture(
                                graphic,
                                animGroup,
                                ref animIndex,
                                dir,
                                out spriteInfo,
                                out isUop
                            )
                        )
                        {
                            int x =
                                position.X
                                - (
                                    isFlipped
                                        ? spriteInfo.UV.Width - spriteInfo.Center.X
                                        : spriteInfo.Center.X
                                );
                            int y = position.Y - (spriteInfo.UV.Height + spriteInfo.Center.Y);

                            if (
                                animations.PixelCheck(
                                    graphic,
                                    animGroup,
                                    dir,
                                    isUop,
                                    animIndex,
                                    isFlipped
                                        ? x
                                            + spriteInfo.UV.Width
                                            - SelectedObject.TranslatedMousePositionByViewport.X
                                        : SelectedObject.TranslatedMousePositionByViewport.X - x,
                                    SelectedObject.TranslatedMousePositionByViewport.Y - y
                                )
                            )
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }
    }
}
