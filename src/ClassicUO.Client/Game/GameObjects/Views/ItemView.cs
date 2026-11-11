// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MathHelper = ClassicUO.Utility.MathHelper;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Item
    {
        /// <summary>The draw offset the view (from the atlas UV) and ItemSpriteExtract (from the pack) store for art of this size.</summary>
        internal static void PickEntryFromSize(int width, int height, out int entryWidth, out int entryHeight)
        {
            entryWidth = (short)((width >> 1) - 22);
            entryHeight = (short)(height - 44);
        }

        /// <summary>
        /// The item pick test's reject: the mouse (relative to the art's top left) is outside the art drawn with that
        /// offset, (Width + 23) * 2 by Height + 44 pixels, including the stack's second sprite 5 px further.
        /// </summary>
        internal static bool OutsidePickBounds(int entryWidth, int entryHeight, int mouseX, int mouseY)
            => mouseX < -5 || mouseY < -5 || mouseX >= (entryWidth + 23) * 2 || mouseY >= entryHeight + 44;

        public override bool CheckMouseSelection()
        {
            if (!IsCorpse)
            {
                if (
                    ReferenceEquals(SelectedObject.Object, this)
                    || World.TargetManager.TargetingState == CursorTarget.MultiPlacement
                )
                {
                    return false;
                }

                ushort graphic = DisplayedGraphic;

                if (OnGround && ItemData.IsAnimated)
                {
                    if (
                        ProfileManager.CurrentProfile.FieldsType == 2
                        && (
                            StaticFilters.IsFireField(Graphic)
                            || StaticFilters.IsParalyzeField(Graphic)
                            || StaticFilters.IsEnergyField(Graphic)
                            || StaticFilters.IsPoisonField(Graphic)
                            || StaticFilters.IsWallOfStone(Graphic)
                        )
                    )
                    {
                        graphic = Constants.FIELD_REPLACE_GRAPHIC;
                    }
                    else
                    {
                        ref ArtEntry animEntry = ref Client.Game.UO.FileManager.Arts.GetEntry(
                            graphic + 0x4000
                        );

                        graphic += (ushort)animEntry.AnimOffset;
                    }
                }

                ref ArtEntry index = ref Client.Game.UO.FileManager.Arts.GetEntry(graphic + 0x4000);

                // an item seen for the first time has no stored draw offset yet: store the one the view and ItemSpriteExtract
                // store when they draw it (from the pack's sprite size), so the reject below works before the first draw
                // instead of decoding the art of every new item in the walk (its art decodes on a worker, FirstSightArt). A
                // sprite the pack lacks, and CUO_ART_SYNC=1, keep the old path
                var packSprites = Client.Game.UO.FileManager.Arts.PackSpriteEntries;
                uint spriteIdx = graphic + 0x4000u;

                // T7b part 2b: the offset the extract stored when it drew this graphic for this item last draw
                // (the Legacy sink, or the Gpu world's extracts), else the art entry's
                bool records = Scenes.SpritePickTable.TryItemArt(this, graphic, in index, out int entryW, out int entryH);

                if (!records)
                {
                    entryW = index.Width;
                    entryH = index.Height;
                }

                bool sized = (entryW | entryH) != 0;

                if (!sized && global::ClassicUO.Game.Map.Map.ArtWait && spriteIdx < (uint)packSprites.Length && packSprites[spriteIdx].OrigW != 0 && packSprites[spriteIdx].OrigH != 0)
                {
                    PickEntryFromSize(packSprites[spriteIdx].OrigW, packSprites[spriteIdx].OrigH, out entryW, out entryH);
                    sized = true;

                    if (!records)
                    {
                        index.Width = entryW;
                        index.Height = entryH;
                    }
                }

                Point position = RealScreenPosition;
                Vector3 offset = ItemOffsets.Read(this);
                position.X += (int)offset.X;
                position.Y += (int)(offset.Y + offset.Z);
                position.X -= entryW;
                position.Y -= entryH;

                // the drawn art is at most (Width + 23) * 2 by Height + 44 pixels (the view and ItemSpriteExtract store
                // those offsets when they draw it; 0, 0 = not drawn yet or 44 x 44, unless sized from the pack above), and
                // a stack is tested 5 px further:
                // reject the mouse outside that before the art lookup (most of the per-item picking cost in big scenes)
                int mouseX = SelectedObject.TranslatedMousePositionByViewport.X - position.X;
                int mouseY = SelectedObject.TranslatedMousePositionByViewport.Y - position.Y;

                if (sized && OutsidePickBounds(entryW, entryH, mouseX, mouseY))
                {
                    // not under the mouse: its art (the frame drawn now) decodes on a worker if it isn't in the atlas yet
                    // (an item whose graphic changed in place, an atlas reload); an item under the mouse decodes below
                    global::ClassicUO.Game.Map.FirstSightArt.RequestStatic(graphic, frames: false);

                    if (Scenes.PrepCheck.On)
                    {
                        Scenes.PrepCheck.PicksRejected++;

                        if (Client.Game.UO.Arts.GetArt(graphic).Texture != null && (Client.Game.UO.Arts.PixelCheck(graphic, mouseX, mouseY) ||
                            !IsMulti && !IsCoin && Amount > 1 && ItemData.IsStackable && Client.Game.UO.Arts.PixelCheck(graphic, mouseX + 5, mouseY + 5)))
                        {
                            Scenes.PrepCheck.PicksWrong++;
                            Scenes.PrepCheck.Sample ??= $"item 0x{Serial:X8} graphic 0x{graphic:X4} mouse {mouseX},{mouseY} entry {entryW},{entryH}";
                        }
                    }

                    return false;
                }

                if (Client.Game.UO.Arts.GetArt(graphic).Texture != null)
                {
                    if (
                        Client.Game.UO.Arts.PixelCheck(
                            graphic,
                            SelectedObject.TranslatedMousePositionByViewport.X - position.X,
                            SelectedObject.TranslatedMousePositionByViewport.Y - position.Y
                        )
                    )
                    {
                        return true;
                    }
                    else if (!IsMulti && !IsCoin && Amount > 1 && ItemData.IsStackable)
                    {
                        if (
                            Client.Game.UO.Arts.PixelCheck(
                                graphic,
                                SelectedObject.TranslatedMousePositionByViewport.X - position.X + 5,
                                SelectedObject.TranslatedMousePositionByViewport.Y - position.Y + 5
                            )
                        )
                        {
                            return true;
                        }
                    }
                }
            }
            else
            {
                if (!SerialHelper.IsValid(Serial))
                {
                    return false;
                }

                if (ReferenceEquals(SelectedObject.Object, this))
                {
                    return true;
                }

                var animations = Client.Game.UO.Animations;

                Point position = RealScreenPosition;
                position.X += 22;
                position.Y += 22;

                byte direction = (byte)((byte)Layer & 0x7F & 7);

                // T7b part 2b: the mirror bit from the last draw's records (the Legacy sink, or the Gpu world's extracts). The pick
                // doesn't store it back: GetAnimDirection is idempotent for the corpse's fixed direction, so the draw that
                // recorded it left the same bit. Without a record, the bit on the corpse, advanced as before.
                if (Scenes.SpritePickTable.TryCorpse(this, out bool flipped))
                {
                    animations.GetAnimDirection(ref direction, ref flipped);
                }
                else
                {
                    animations.GetAnimDirection(ref direction, ref IsFlipped);
                    flipped = IsFlipped;
                }

                byte animIndex = AnimIndex;
                bool ishuman =
                    MathHelper.InRange(Amount, 0x0190, 0x0193)
                    || MathHelper.InRange(Amount, 0x00B7, 0x00BA)
                    || MathHelper.InRange(Amount, 0x025D, 0x0260)
                    || MathHelper.InRange(Amount, 0x029A, 0x029B)
                    || MathHelper.InRange(Amount, 0x02B6, 0x02B7)
                    || Amount == 0x03DB
                    || Amount == 0x03DF
                    || Amount == 0x03E2
                    || Amount == 0x02E8
                    || Amount == 0x02E9;

                Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
                int layerCount = LayerRuleEval.CorpseOrder(this, Amount, direction, layers); // T3b: from the layering rules data

                for (int i = -1; i < layerCount; i++)
                {
                    // i == -1 draws the base body first, then each equipped layer
                    Layer layer = i == -1 ? Layer.Invalid : layers[i];

                    ushort graphic;

                    if (layer == Layer.Invalid)
                    {
                        graphic = GetGraphicForAnimation();
                    }
                    else if (ishuman)
                    {
                        Item itemEquip = FindItemByLayer(layer);

                        if (itemEquip == null)
                        {
                            continue;
                        }

                        graphic = itemEquip.ItemData.AnimID;

                        if (
                            Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                                graphic,
                                out Dictionary<ushort, EquipConvData> map
                            )
                        )
                        {
                            if (map.TryGetValue(graphic, out EquipConvData data))
                            {
                                graphic = data.Graphic;
                            }
                        }
                    }
                    else
                    {
                        continue;
                    }

                    animations.ConvertBodyIfNeeded(ref graphic, isCorpse: IsCorpse);
                    var animGroup = animations.GetAnimType(graphic);
                    var animFlags = animations.GetAnimFlags(graphic);
                    byte group = Client.Game.UO.FileManager.Animations.GetDeathAction(
                        graphic,
                        animFlags,
                        animGroup,
                        UsedLayer
                    );
                    var frames = animations.GetAnimationFrames(
                        graphic,
                        group,
                        direction,
                        out _,
                        out var isUOP,
                        false,
                        IsCorpse
                    );

                    if (frames.IsEmpty)
                    {
                        continue;
                    }

                    if (animIndex < 0)
                    {
                        animIndex = 0;
                    }

                    if (animIndex >= 0)
                    {
                        animIndex = (byte)(animIndex % frames.Length);
                    }

                    ref var spriteInfo = ref frames[animIndex];

                    if (spriteInfo.Texture != null)
                    {
                        int x =
                            position.X
                            - (
                                flipped
                                    ? spriteInfo.UV.Width - spriteInfo.Center.X
                                    : spriteInfo.Center.X
                            );
                        int y = position.Y - (spriteInfo.UV.Height + spriteInfo.Center.Y);

                        if (
                            animations.PixelCheck(
                                graphic,
                                group,
                                direction,
                                isUOP,
                                animIndex,
                                flipped
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

            return false;
        }
    }
}
