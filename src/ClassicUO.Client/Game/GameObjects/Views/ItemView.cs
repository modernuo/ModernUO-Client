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
        private static EquipConvData? _equipConvData;

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed)
            {
                return false;
            }

            if (
                ItemData.IsLight
                || DisplayedGraphic >= 0x3E02 && DisplayedGraphic <= 0x3E0B
                || DisplayedGraphic >= 0x3914 && DisplayedGraphic <= 0x3929
            )
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            if (!AllowedToDraw)
            {
                return false;
            }

            //Engine.DebugInfo.ItemsRendered++;
            Vector3 hueVec;

            posX += (int)Offset.X;
            posY += (int)(Offset.Y + Offset.Z);

            float alpha = AlphaHue / 255f;

            if (IsCorpse)
            {
                hueVec = ShaderHueTranslator.GetHueVector(0, false, alpha);
                return DrawCorpse(batcher, posX, posY - 3, hueVec, depth);
            }

            ushort hue = Hue;
            ushort graphic = DisplayedGraphic;
            bool partial = ItemData.IsPartialHue;

            if (OnGround)
            {
                if (ItemData.IsAnimated)
                {
                    if (ProfileManager.CurrentProfile.FieldsType == 2)
                    {
                        if (StaticFilters.IsFireField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0020;
                        }
                        else if (StaticFilters.IsParalyzeField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0058;
                        }
                        else if (StaticFilters.IsEnergyField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0070;
                        }
                        else if (StaticFilters.IsPoisonField(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x0044;
                        }
                        else if (StaticFilters.IsWallOfStone(Graphic))
                        {
                            graphic = Constants.FIELD_REPLACE_GRAPHIC;
                            hue = 0x038A;
                        }
                    }
                }

                if (ItemData.IsContainer && SelectedObject.SelectedContainer == this)
                {
                    hue = 0x0035;
                    partial = false;
                }
            }

            if (
                ProfileManager.CurrentProfile.HighlightGameObjects
                && ReferenceEquals(SelectedObject.Object, this)
            )
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else
            {
                if (!IsLocked && !IsMulti && ReferenceEquals(SelectedObject.Object, this))
                {
                    // TODO: check why i put this.
                    //isPartial = ItemData.Weight == 0xFF;
                    hue = 0x0035;
                }
                else if (IsHidden)
                {
                    hue = 0x038E;
                }
            }

            hueVec = ShaderHueTranslator.GetHueVector(hue, partial, alpha);

            if (!IsMulti && !IsCoin && Amount > 1 && ItemData.IsStackable)
            {
                DrawStaticAnimated(batcher, graphic, posX - 5, posY - 5, hueVec, false, depth);
            }

            if (
                !SerialHelper.IsValid(Serial)
                && IsMulti
                && World.TargetManager.TargetingState == CursorTarget.MultiPlacement
            )
            {
                hueVec.Z = 0.5f;
            }

            DrawStaticAnimated(batcher, graphic, posX, posY, hueVec, false, depth);

            return true;
        }

        private bool DrawCorpse(
            UltimaBatcher2D batcher,
            int posX,
            int posY,
            Vector3 hueVec,
            float depth
        )
        {
            if (IsDestroyed || World.CorpseManager.Exists(Serial, 0))
            {
                return false;
            }

            posX += 22;
            posY += 22;

            byte direction = (byte)((byte)Layer & 0x7F & 7);
            Client.Game.UO.Animations.GetAnimDirection(ref direction, ref IsFlipped);

            byte animIndex = (byte)AnimIndex;
            ushort graphic = GetGraphicForAnimation();

            Client.Game.UO.Animations.ConvertBodyIfNeeded(ref graphic, isCorpse: IsCorpse);
            var animGroup = Client.Game.UO.Animations.GetAnimType(graphic);
            var animFlags = Client.Game.UO.Animations.GetAnimFlags(graphic);
            byte group = Client.Game.UO.FileManager.Animations.GetDeathAction(
                graphic,
                animFlags,
                animGroup,
                UsedLayer
            );

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

            DrawLayer(
                batcher,
                posX,
                posY,
                this,
                Layer.Invalid,
                animIndex,
                ishuman,
                Hue,
                IsFlipped,
                hueVec.Z,
                group,
                direction,
                hueVec,
                depth
            );

            // draw order built from the layer algorithm (PaperdollOrder), keyed on
            // equipped item graphics; cloak repositioned by facing direction to match
            // the in-world tables. Stack-allocated, no GC churn.
            Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
            int layerCount = LayerRuleEval.CorpseOrder(this, Amount, direction, layers); // T3b: from data with ecs_layer_rules

            for (int i = 0; i < layerCount; i++)
            {
                Layer layer = layers[i];

                DrawLayer(
                    batcher,
                    posX,
                    posY,
                    this,
                    layer,
                    animIndex,
                    ishuman,
                    0,
                    IsFlipped,
                    hueVec.Z,
                    group,
                    direction,
                    hueVec,
                    depth
                );
            }

            return true;
        }

        private void DrawLayer(
            UltimaBatcher2D batcher,
            int posX,
            int posY,
            Item owner,
            Layer layer,
            byte animIndex,
            bool ishuman,
            ushort color,
            bool flipped,
            float alpha,
            byte animGroup,
            byte dir,
            Vector3 hueVec,
            float depth
        )
        {
            _equipConvData = null;
            bool ispartialhue = false;

            ushort graphic;

            if (layer == Layer.Invalid)
            {
                graphic = owner.GetGraphicForAnimation();
            }
            else if (ishuman)
            {
                Item itemEquip = owner.FindItemByLayer(layer);

                if (itemEquip == null)
                {
                    return;
                }

                graphic = itemEquip.ItemData.AnimID;
                ispartialhue = itemEquip.ItemData.IsPartialHue;

                if (
                    Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                        graphic,
                        out Dictionary<ushort, EquipConvData> map
                    )
                )
                {
                    if (map.TryGetValue(graphic, out EquipConvData data))
                    {
                        _equipConvData = data;
                        graphic = data.Graphic;
                    }
                }

                color = itemEquip.Hue;
            }
            else
            {
                return;
            }

            var frames = Client.Game.UO.Animations.GetAnimationFrames(
                graphic,
                animGroup,
                dir,
                out var newHue,
                out _,
                isEquip: layer != Layer.Invalid,
                isCorpse: layer == Layer.Invalid
            );

            if (color == 0)
            {
                color = newHue;
            }

            if (frames.Length == 0)
            {
                return;
            }

            int fc = frames.Length;

            if (fc > 0 && animIndex >= fc)
            {
                animIndex = (byte)(fc - 1);
            }

            if (animIndex < frames.Length)
            {
                ref var spriteInfo = ref frames[animIndex];

                if (spriteInfo.Texture == null)
                {
                    return;
                }

                if (flipped)
                {
                    posX -= spriteInfo.UV.Width - spriteInfo.Center.X;
                }
                else
                {
                    posX -= spriteInfo.Center.X;
                }

                posY -= spriteInfo.UV.Height + spriteInfo.Center.Y;

                if (color == 0)
                {
                    if ((color & 0x8000) != 0)
                    {
                        ispartialhue = true;
                        color &= 0x7FFF;
                    }

                    if (color == 0 && _equipConvData.HasValue)
                    {
                        color = _equipConvData.Value.Color;
                        ispartialhue = false;
                    }
                }

                if (
                    ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                    && owner.Distance > World.ClientViewRange
                )
                {
                    hueVec = ShaderHueTranslator.GetHueVector(
                        Constants.OUT_RANGE_COLOR + 1,
                        false,
                        1
                    );
                }
                else if (
                    World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect
                )
                {
                    hueVec = ShaderHueTranslator.GetHueVector(
                        Constants.DEAD_RANGE_COLOR + 1,
                        false,
                        1
                    );
                }
                else
                {
                    if (
                        ProfileManager.CurrentProfile.GridLootType > 0
                        && SelectedObject.CorpseObject == owner
                    )
                    {
                        color = 0x0034;
                    }
                    else if (
                        ProfileManager.CurrentProfile.HighlightGameObjects
                        && ReferenceEquals(SelectedObject.Object, owner)
                    )
                    {
                        color = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                    }

                    hueVec = ShaderHueTranslator.GetHueVector(color, ispartialhue, alpha);
                }

                Vector2 pos = new Vector2(posX, posY);
                Rectangle rect = spriteInfo.UV;

                int diffY = (spriteInfo.UV.Height + spriteInfo.Center.Y);
                int value = /*!isMounted && diffX <= 44 ? spriteInfo.UV.Height * 2 :*/
                Math.Max(1, diffY);
                int count = Math.Max((spriteInfo.UV.Height / value) + 1, 2);

                rect.Height = Math.Min(value, rect.Height);
                int remains = spriteInfo.UV.Height - rect.Height;

                int tiles = (byte)owner.Direction % 2 == 0 ? 2 : 2;

                for (int i = 0; i < count; ++i)
                {
                    //hueVec.Y = 1;
                    //hueVec.X = 0x44 + (i * 20);

                    batcher.Draw(
                        spriteInfo.Texture,
                        pos,
                        rect,
                        hueVec,
                        0f,
                        Vector2.Zero,
                        1f,
                        flipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                        depth + 1f + (i * tiles)
                    //depth + (i * tiles) + (owner.PriorityZ * 0.001f)
                    );

                    pos.Y += rect.Height;
                    rect.Y += rect.Height;
                    rect.Height = remains; // Math.Min(value, remains);
                    remains -= rect.Height;
                }
            }
        }

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
                bool sized = (index.Width | index.Height) != 0;

                if (!sized && global::ClassicUO.Game.Map.Map.ArtWait && spriteIdx < (uint)packSprites.Length && packSprites[spriteIdx].OrigW != 0 && packSprites[spriteIdx].OrigH != 0)
                {
                    PickEntryFromSize(packSprites[spriteIdx].OrigW, packSprites[spriteIdx].OrigH, out index.Width, out index.Height);
                    sized = true;
                }

                Point position = RealScreenPosition;
                position.X += (int)Offset.X;
                position.Y += (int)(Offset.Y + Offset.Z);
                position.X -= index.Width;
                position.Y -= index.Height;

                // the drawn art is at most (Width + 23) * 2 by Height + 44 pixels (the view and ItemSpriteExtract store
                // those offsets when they draw it; 0, 0 = not drawn yet or 44 x 44, unless sized from the pack above), and
                // a stack is tested 5 px further:
                // reject the mouse outside that before the art lookup (most of the per-item picking cost in big scenes)
                int mouseX = SelectedObject.TranslatedMousePositionByViewport.X - position.X;
                int mouseY = SelectedObject.TranslatedMousePositionByViewport.Y - position.Y;

                if (sized && OutsidePickBounds(index.Width, index.Height, mouseX, mouseY))
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
                            Scenes.PrepCheck.Sample ??= $"item 0x{Serial:X8} graphic 0x{graphic:X4} mouse {mouseX},{mouseY} entry {index.Width},{index.Height}";
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
                animations.GetAnimDirection(ref direction, ref IsFlipped);
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
                int layerCount = LayerRuleEval.CorpseOrder(this, Amount, direction, layers); // T3b: from data with ecs_layer_rules

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
                                _equipConvData = data;
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
                                IsFlipped
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
                                IsFlipped
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
