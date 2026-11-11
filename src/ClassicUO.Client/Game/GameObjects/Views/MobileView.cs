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
        private const int SIT_OFFSET_Y = 4;
        private static EquipConvData? _equipConvData;
        private static int _characterFrameStartY;
        private static int _startCharacterWaistY;
        private static int _startCharacterKneesY;
        private static int _startCharacterFeetY;
        private static int _characterFrameHeight;

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed || !AllowedToDraw)
            {
                return false;
            }

            bool charSitting = false;
            ushort overridedHue = 0;

            AnimationsLoader.SittingInfoData seatData = AnimationsLoader.SittingInfoData.Empty;
            _equipConvData = null;
            FrameInfo.X = 0;
            FrameInfo.Y = 0;
            FrameInfo.Width = 0;
            FrameInfo.Height = 0;

            posY -= 3;
            int drawX = posX + (int)Offset.X;
            int drawY = posY + (int)(Offset.Y - Offset.Z);

            drawX += 22;
            drawY += 22;

            bool hasShadow = !IsDead && !IsHidden && ProfileManager.CurrentProfile.ShadowsEnabled;

            if (World.AuraManager.IsEnabled)
            {
                DrawAuraAt(batcher, drawX, drawY, depth);
            }

            bool isHuman = IsHuman;

            bool isGargoyle =
                Client.Game.UO.Version >= ClientVersion.CV_7000
                && (Graphic == 666 || Graphic == 667 || Graphic == 0x02B7 || Graphic == 0x02B6);

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(0, false, AlphaHue / 255f);

            if (
                ProfileManager.CurrentProfile.HighlightGameObjects
                && ReferenceEquals(SelectedObject.Object, this)
            )
            {
                overridedHue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                hueVec.Y = 1;
            }
            else if (SelectedObject.HealthbarObject == this)
            {
                overridedHue = Notoriety.GetHue(NotorietyFlag);
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                overridedHue = Constants.OUT_RANGE_COLOR;
                hueVec.Y = 1;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                overridedHue = Constants.DEAD_RANGE_COLOR;
                hueVec.Y = 1;
            }
            else if (IsHidden)
            {
                overridedHue = 0x038E;
            }
            else
            {
                overridedHue = 0;

                if (IsDead)
                {
                    if (!isHuman)
                    {
                        overridedHue = 0x0386;
                    }
                }
                else
                {
                    if (ProfileManager.CurrentProfile.HighlightMobilesByPoisoned)
                    {
                        if (IsPoisoned)
                        {
                            overridedHue = ProfileManager.CurrentProfile.PoisonHue;
                        }
                    }
                    if (ProfileManager.CurrentProfile.HighlightMobilesByParalize)
                    {
                        if (IsParalyzed && NotorietyFlag != NotorietyFlag.Invulnerable)
                        {
                            overridedHue = ProfileManager.CurrentProfile.ParalyzedHue;
                        }
                    }
                    if (ProfileManager.CurrentProfile.HighlightMobilesByInvul)
                    {
                        if (NotorietyFlag != NotorietyFlag.Invulnerable && IsYellowHits)
                        {
                            overridedHue = ProfileManager.CurrentProfile.InvulnerableHue;
                        }
                    }
                }
            }

            bool isAttack = Serial == World.TargetManager.LastAttack;
            bool isUnderMouse =
                World.TargetManager.IsTargeting && ReferenceEquals(SelectedObject.Object, this);

            if (Serial != World.Player.Serial)
            {
                if (isAttack || isUnderMouse)
                {
                    overridedHue = Notoriety.GetHue(NotorietyFlag);
                }
            }

            ProcessSteps(out byte dir);
            byte layerDir = dir;

            Client.Game.UO.Animations.GetAnimDirection(ref dir, ref IsFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            Item mount = FindItemByLayer(Layer.Mount);
            int mountOffsetY = 0;

            if (isHuman && mount != null && mount.Graphic != 0x3E96)
            {
                ushort mountGraphic = mount.GetGraphicForAnimation();
                byte animGroupMount = 0;

                if (
                    mountGraphic != 0xFFFF
                    && mountGraphic < Client.Game.UO.Animations.MaxAnimationCount
                )
                {
                    mountOffsetY = AnimationRules.MountOffsetY(mount.Graphic, Client.Game.UO.Animations.GetMountSeat(mountGraphic));

                    if (hasShadow)
                    {
                        DrawInternal(
                            batcher,
                            this,
                            null,
                            drawX,
                            drawY + 10,
                            hueVec,
                            IsFlipped,
                            animIndex,
                            true,
                            graphic,
                            animGroup,
                            dir,
                            isHuman,
                            false,
                            false,
                            false,
                            depth,
                            mountOffsetY,
                            overridedHue,
                            charSitting
                        );

                        animGroupMount = GetGroupForAnimation(this, mountGraphic);

                        DrawInternal(
                            batcher,
                            this,
                            mount,
                            drawX,
                            drawY,
                            hueVec,
                            IsFlipped,
                            animIndex,
                            true,
                            mountGraphic,
                            animGroupMount,
                            dir,
                            isHuman,
                            false,
                            false,
                            false,
                            depth,
                            mountOffsetY,
                            overridedHue,
                            charSitting
                        );
                    }
                    else
                    {
                        animGroupMount = GetGroupForAnimation(this, mountGraphic);
                    }

                    DrawInternal(
                        batcher,
                        this,
                        mount,
                        drawX,
                        drawY,
                        hueVec,
                        IsFlipped,
                        animIndex,
                        false,
                        mountGraphic,
                        animGroupMount,
                        dir,
                        isHuman,
                        false,
                        true,
                        false,
                        depth,
                        mountOffsetY,
                        overridedHue,
                        charSitting
                    );

                    drawY += mountOffsetY;
                }
            }
            else
            {
                if (TryGetSittingInfo(out seatData))
                {
                    animGroup = (byte)PeopleAnimationGroup.Stand;
                    animIndex = 0;

                    ProcessSteps(out dir);

                    Client.Game.UO.FileManager.Animations.FixSittingDirection(
                        ref dir,
                        ref IsFlipped,
                        ref drawX,
                        ref drawY,
                        ref seatData
                    );

                    drawY += SIT_OFFSET_Y;

                    if (dir == 3)
                    {
                        if (IsGargoyle)
                        {
                            drawY -= 30 - SIT_OFFSET_Y;
                            animGroup = 42;
                        }
                        else
                        {
                            animGroup = 25;
                        }
                    }
                    else if (IsGargoyle)
                    {
                        animGroup = 42;
                    }
                    else
                    {
                        charSitting = true;
                    }
                }
                else if (hasShadow)
                {
                    DrawInternal(
                        batcher,
                        this,
                        null,
                        drawX,
                        drawY,
                        hueVec,
                        IsFlipped,
                        animIndex,
                        true,
                        graphic,
                        animGroup,
                        dir,
                        isHuman,
                        false,
                        false,
                        false,
                        depth,
                        mountOffsetY,
                        overridedHue,
                        charSitting
                    );
                }
            }

            DrawInternal(
                batcher,
                this,
                null,
                drawX,
                drawY,
                hueVec,
                IsFlipped,
                animIndex,
                false,
                graphic,
                animGroup,
                dir,
                isHuman,
                false,
                false,
                isGargoyle,
                depth,
                mountOffsetY,
                overridedHue,
                charSitting
            );

            // T3a: any worn item, from the ECS (WorldState.HasWornItems); perf-10: the mobile's worn items validated once for the
            // loop (WornItems) instead of per FindItemByLayer
            WornItems worn = World.State.WornView(this);

            if (worn.Any)
            {
                Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];

                // T3b: the order, the covering and the gargoyle remaps from the layering rules data (LayerRuleEval)
                LayerTables layerTables = default;
                LayerSubject layerSubject;
                // T3b cache: the mobile's whole answer, reused until its equipment, body or flags change (null: evaluate)
                LayerDecision layerCache = worn.Layers(isGargoyle);
                int layerCount;

                if (layerCache != null)
                {
                    ReadOnlySpan<Layer> cached = layerCache.Order(layerDir);
                    cached.CopyTo(layers);
                    layerCount = cached.Length;
                    layerSubject = layerCache.Subject;
                }
                else
                {
                    layerTables.Gather(this);
                    layerSubject = LayerRuleEval.Subject(Graphic, IsFemale, isGargoyle);
                    layerCount = LayerRuleEval.BuildInWorld(layerTables.Anim, layerSubject, layerDir, layers);
                }

                for (int i = 0; i < layerCount; i++)
                {
                    Layer layer = layers[i];

                    Item item = worn[layer];

                    if (item == null)
                    {
                        continue;
                    }

                    if (IsDead && (layer == Layer.Hair || layer == Layer.Beard))
                    {
                        continue;
                    }

                    if (isHuman)
                    {
                        if (layerCache != null ? layerCache.IsCovered(layer) : LayerRuleEval.IsCovered(LayerRuleSet.World, layer, ref layerTables, layerSubject))
                        {
                            continue;
                        }

                        if (item.ItemData.AnimID != 0)
                        {
                            // the remap rules' conditions decide (the defaults need the gargoyle draw flag)
                            graphic = layerCache != null ? layerCache.Remap(layer) : LayerRuleEval.Remap(item.ItemData.AnimID, layerSubject);

                            if (
                                Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                                    Graphic,
                                    out Dictionary<ushort, EquipConvData> map
                                )
                            )
                            {
                                if (map.TryGetValue(item.ItemData.AnimID, out EquipConvData data))
                                {
                                    _equipConvData = data;
                                    graphic = data.Graphic;
                                }
                            }

                            DrawInternal(
                                batcher,
                                this,
                                item,
                                drawX,
                                drawY,
                                hueVec,
                                IsFlipped,
                                animIndex,
                                false,
                                graphic,
                                isGargoyle /*&& item.ItemData.IsWeapon*/
                                && seatData.Graphic == 0
                                    ? GetGroupForAnimation(this, graphic, true)
                                    : animGroup,
                                dir,
                                isHuman,
                                true,
                                false,
                                isGargoyle,
                                depth,
                                mountOffsetY,
                                overridedHue,
                                charSitting
                            );
                        }
                        else
                        {
                            if (item.ItemData.IsLight)
                            {
                                Client.Game
                                    .GetScene<GameScene>()
                                    .AddLight(this, item, drawX, drawY);
                            }
                        }

                        _equipConvData = null;
                    }
                    else
                    {
                        if (item.ItemData.IsLight)
                        {
                            Client.Game.GetScene<GameScene>().AddLight(this, item, drawX, drawY);

                            /*DrawInternal
                            (
                                batcher,
                                this,
                                item,
                                drawX,
                                drawY,
                                IsFlipped,
                                animIndex,
                                false,
                                graphic,
                                animGroup,
                                dir,
                                isHuman,
                                false,
                                alpha: HueVector.Z
                            );
                            */
                            //break;
                        }
                    }
                }
            }

            //if (FileManager.Animations.SittingValue != 0)
            //{
            //    ref var sittingData = ref FileManager.Animations.SittingInfos[FileManager.Animations.SittingValue - 1];

            //    if (FileManager.Animations.Direction == 3 && sittingData.DrawBack &&
            //        HasEquipment && Equipment[(int) Layer.Cloak] == null)
            //    {

            //    }
            //}
            //

            FrameInfo.X = Math.Abs(FrameInfo.X);
            FrameInfo.Y = Math.Abs(FrameInfo.Y);
            FrameInfo.Width = FrameInfo.X + FrameInfo.Width;
            FrameInfo.Height = FrameInfo.Y + FrameInfo.Height;

            return true;
        }

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
                        _equipConvData = data;
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
            out bool isUOP
        )
        {
            spriteInfo = default;

            var frames = Client.Game.UO.Animations.GetAnimationFrames(
                graphic,
                animGroup,
                direction,
                out _,
                out isUOP
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

        private static void DrawInternal(
            UltimaBatcher2D batcher,
            Mobile owner,
            Item entity,
            int x,
            int y,
            Vector3 hueVec,
            bool mirror,
            byte frameIndex,
            bool hasShadow,
            ushort id,
            byte animGroup,
            byte dir,
            bool isHuman,
            bool isEquip,
            bool isMount,
            bool forceUOP,
            float depth,
            int mountOffset,
            ushort overridedHue,
            bool charIsSitting
        )
        {
            if (id >= Client.Game.UO.Animations.MaxAnimationCount || owner == null)
            {
                return;
            }

            var frames = Client.Game.UO.Animations.GetAnimationFrames(
                id,
                animGroup,
                dir,
                out var hueFromFile,
                out _,
                isEquip,
                false
            );

            if (hueFromFile == 0)
            {
                hueFromFile = overridedHue;
            }

            if (frames.Length == 0)
            {
                return;
            }

            if (frameIndex >= frames.Length)
            {
                frameIndex = (byte)(frames.Length - 1);
            }
            else if (frameIndex < 0)
            {
                frameIndex = 0;
            }

            ref var spriteInfo = ref frames[frameIndex % frames.Length];

            if (spriteInfo.Texture == null)
            {
                if (!(charIsSitting && entity == null && !hasShadow))
                {
                    return;
                }

                goto SKIP;
            }

            if (mirror)
            {
                x -= spriteInfo.UV.Width - spriteInfo.Center.X;
            }
            else
            {
                x -= spriteInfo.Center.X;
            }

            y -= spriteInfo.UV.Height + spriteInfo.Center.Y;

            SKIP:

            if (hasShadow)
            {
                batcher.DrawShadow(
                    spriteInfo.Texture,
                    new Vector2(x, y),
                    spriteInfo.UV,
                    mirror,
                    depth
                );
            }
            else
            {
                ushort hue = overridedHue;
                bool partialHue = false;

                if (hue == 0)
                {
                    hue = entity?.Hue ?? owner.Hue;
                    partialHue = !isMount && entity != null && entity.ItemData.IsPartialHue;

                    if ((hue & 0x8000) != 0)
                    {
                        partialHue = true;
                        hue &= 0x7FFF;
                    }

                    if (hue == 0)
                    {
                        hue = hueFromFile;

                        if (hue == 0 && _equipConvData.HasValue)
                        {
                            hue = _equipConvData.Value.Color;
                        }

                        partialHue = false;
                    }
                }

                hueVec = ShaderHueTranslator.GetHueVector(hue, partialHue, hueVec.Z);

                if (spriteInfo.Texture != null)
                {
                    Vector2 pos = new Vector2(x, y);
                    Rectangle rect = spriteInfo.UV;

                    if (charIsSitting)
                    {
                        Vector3 mod = CalculateSitAnimation(y, entity, isHuman, ref spriteInfo);

                        batcher.DrawCharacterSitted(
                            spriteInfo.Texture,
                            pos,
                            rect,
                            mod,
                            hueVec,
                            mirror,
                            depth + 1f
                        );
                    }
                    else
                    {
                        int diffY = (spriteInfo.UV.Height + spriteInfo.Center.Y) - mountOffset;

                        int value = Math.Max(1, diffY);
                        int count = Math.Max((spriteInfo.UV.Height / value) + 1, 2);

                        rect.Height = Math.Min(value, rect.Height);
                        int remains = spriteInfo.UV.Height - rect.Height;

                        int tiles = (byte)owner.Direction % 2 == 0 ? 2 : 2;

                        for (int i = 0; i < count; ++i)
                        {
                            batcher.Draw(
                                spriteInfo.Texture,
                                pos,
                                rect,
                                hueVec,
                                0f,
                                Vector2.Zero,
                                1f,
                                mirror ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                                depth + 1f + (i * tiles)
                            );

                            pos.Y += rect.Height;
                            rect.Y += rect.Height;
                            rect.Height = remains;
                            remains -= rect.Height;
                        }
                    }

                    int xx = -spriteInfo.Center.X;
                    int yy = -(spriteInfo.UV.Height + spriteInfo.Center.Y + 3);

                    if (mirror)
                    {
                        xx = -(spriteInfo.UV.Width - spriteInfo.Center.X);
                    }

                    if (xx < owner.FrameInfo.X)
                    {
                        owner.FrameInfo.X = xx;
                    }

                    if (yy < owner.FrameInfo.Y)
                    {
                        owner.FrameInfo.Y = yy;
                    }

                    if (owner.FrameInfo.Width < xx + spriteInfo.UV.Width)
                    {
                        owner.FrameInfo.Width = xx + spriteInfo.UV.Width;
                    }

                    if (owner.FrameInfo.Height < yy + spriteInfo.UV.Height)
                    {
                        owner.FrameInfo.Height = yy + spriteInfo.UV.Height;
                    }
                }

                if (entity != null && entity.ItemData.IsLight)
                {
                    Client.Game
                        .GetScene<GameScene>()
                        .AddLight(owner, entity, mirror ? x + spriteInfo.UV.Width : x, y);
                }
            }
        }

        private static Vector3 CalculateSitAnimation(
            int y,
            Item entity,
            bool isHuman,
            ref SpriteInfo spriteInfo
        )
        {
            Vector3 mod = new Vector3();

            const float UPPER_BODY_RATIO = 0.35f;
            const float MID_BODY_RATIO = 0.60f;
            const float LOWER_BODY_RATIO = 0.94f;

            if (entity == null && isHuman)
            {
                int frameHeight = spriteInfo.UV.Height;
                if (frameHeight == 0)
                {
                    frameHeight = 61;
                }

                _characterFrameStartY =
                    y - (spriteInfo.Texture != null ? 0 : frameHeight - SIT_OFFSET_Y);
                _characterFrameHeight = frameHeight;
                _startCharacterWaistY =
                    (int)(frameHeight * UPPER_BODY_RATIO) + _characterFrameStartY;
                _startCharacterKneesY = (int)(frameHeight * MID_BODY_RATIO) + _characterFrameStartY;
                _startCharacterFeetY =
                    (int)(frameHeight * LOWER_BODY_RATIO) + _characterFrameStartY;

                if (spriteInfo.Texture == null)
                {
                    return mod;
                }
            }

            mod.X = UPPER_BODY_RATIO;
            mod.Y = MID_BODY_RATIO;
            mod.Z = LOWER_BODY_RATIO;

            if (entity != null)
            {
                float itemsEndY = y + spriteInfo.UV.Height;

                if (y >= _startCharacterWaistY)
                {
                    mod.X = 0;
                }
                else if (itemsEndY <= _startCharacterWaistY)
                {
                    mod.X = 1.0f;
                }
                else
                {
                    float upperBodyDiff = _startCharacterWaistY - y;
                    mod.X = upperBodyDiff / spriteInfo.UV.Height;

                    if (mod.X < 0)
                    {
                        mod.X = 0;
                    }
                }

                if (_startCharacterWaistY >= itemsEndY || y >= _startCharacterKneesY)
                {
                    mod.Y = 0;
                }
                else if (_startCharacterWaistY <= y && itemsEndY <= _startCharacterKneesY)
                {
                    mod.Y = 1.0f;
                }
                else
                {
                    float midBodyDiff;

                    if (y >= _startCharacterWaistY)
                    {
                        midBodyDiff = _startCharacterKneesY - y;
                    }
                    else if (itemsEndY <= _startCharacterKneesY)
                    {
                        midBodyDiff = itemsEndY - _startCharacterWaistY;
                    }
                    else
                    {
                        midBodyDiff = _startCharacterKneesY - _startCharacterWaistY;
                    }

                    mod.Y = mod.X + midBodyDiff / spriteInfo.UV.Height;

                    if (mod.Y < 0)
                    {
                        mod.Y = 0;
                    }
                }

                if (itemsEndY <= _startCharacterKneesY)
                {
                    mod.Z = 0;
                }
                else if (y >= _startCharacterKneesY)
                {
                    mod.Z = 1.0f;
                }
                else
                {
                    float lowerBodyDiff = itemsEndY - _startCharacterKneesY;
                    mod.Z = mod.Y + lowerBodyDiff / spriteInfo.UV.Height;

                    if (mod.Z < 0)
                    {
                        mod.Z = 0;
                    }
                }
            }

            return mod;
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

            ProcessSteps(out byte dir);
            bool isFlipped = pickFlipped;
            animations.GetAnimDirection(ref dir, ref isFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            byte animGroupBackup = animGroup;
            byte animIndexBackup = animIndex;

            SpriteInfo spriteInfo;
            bool isUop;

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

            if (GetTexture(graphic, animGroup, ref animIndex, dir, out spriteInfo, out isUop))
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
