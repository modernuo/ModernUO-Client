// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Utility;

namespace ClassicUO.Game
{
    /// <summary>
    /// The pure parts of ClassicUO's mobile drawing (Mobile, MobileView, Animations), ported for the ECS sprite extract.
    /// Each method names the legacy code it reproduces; the parity harness (<see cref="EcsParity"/>) checks them in game.
    /// </summary>
    internal static class AnimationRules
    {
        public const int SIT_OFFSET_Y = 4;

        // Mobile.IsHuman
        public static bool IsHumanBody(ushort g) =>
            g >= 0x0190 && g <= 0x0193
            || g >= 0x00B7 && g <= 0x00BA
            || g >= 0x025D && g <= 0x0260
            || g == 0x029A
            || g == 0x029B
            || g == 0x02B6
            || g == 0x02B7
            || g == 0x03DB
            || g == 0x03DF
            || g == 0x03E2
            || g == 0x02E8
            || g == 0x02E9
            || g == 0x04E5;

        // MobileView's isGargoyle (CV_7000+)
        public static bool IsGargoyleDrawBody(ushort g, bool cv7000) => cv7000 && (g == 666 || g == 667 || g == 0x02B7 || g == 0x02B6);

        // Mobile.IsGargoyle, operator precedence included
        public static bool IsGargoyleProp(ushort g, bool cv7000) => cv7000 && g == 0x029A || g == 0x029B;

        // Mobile.IsDead without the server-set flag
        public static bool IsDeadBody(ushort g) => g == 0x0192 || g == 0x0193 || (g >= 0x025F && g <= 0x0260) || g == 0x2B6 || g == 0x02B7;

        // Mobile.GetGraphicForAnimation
        public static ushort BodyForAnimation(ushort g)
        {
            switch (g)
            {
                case 0x0192:
                case 0x0193:
                    return (ushort)(g - 2);

                case 0x02B6:
                    return 667;

                case 0x02B7:
                    return 666;
            }

            return g;
        }

        // Item.GetGraphicForAnimation for an item on Layer.Mount
        public static ushort MountBody(ushort itemGraphic, ushort animId)
        {
            // ethereal unicorn
            if (itemGraphic == 0x3E9B || itemGraphic == 0x3E9D)
            {
                return 0x00C0;
            }

            // ethereal kirin
            if (itemGraphic == 0x3E9C)
            {
                return 0x00BF;
            }

            ushort graphic = itemGraphic;

            if (Mounts.TryGet(itemGraphic, out MountInfo info))
            {
                graphic = info.Graphic;
            }

            if (animId != 0)
            {
                graphic = animId;
            }

            return graphic;
        }

        /// <summary>Mobile.IsMounted for its mount-layer item (0 = none): not a boat's tiller (IsDrivingBoat), and a mount
        /// body (Item.GetGraphicForAnimation) other than 0xFFFF.</summary>
        public static bool IsMountedBy(uint mountItem, ushort mountGraphic, ushort mountAnimId) =>
            mountItem != 0 && mountGraphic != 0x3E96 && MountBody(mountGraphic, mountAnimId) != 0xFFFF;

        // MobileView: the rider is drawn this many pixels lower on the mount (Mounts table, keyed by the mount item graphic)
        public static sbyte MountOffsetY(ushort itemGraphic) => Mounts.TryGet(itemGraphic, out MountInfo info) ? info.OffsetY : (sbyte)0;

        /// <summary>
        /// Animations.GetAnimDirection: UO direction 0..7 to the stored direction 0..4 plus mirroring. Directions 3 and 7
        /// leave <paramref name="mirror"/> as it was, which is why the ECS keeps it as persistent state.
        /// </summary>
        public static void MapDirection(ref byte dir, ref bool mirror)
        {
            switch (dir)
            {
                case 2:
                case 4:
                    mirror = dir == 2;
                    dir = 1;

                    break;

                case 1:
                case 5:
                    mirror = dir == 1;
                    dir = 2;

                    break;

                case 0:
                case 6:
                    mirror = dir == 0;
                    dir = 3;

                    break;

                case 3:
                    dir = 0;

                    break;

                case 7:
                    dir = 4;

                    break;
            }
        }

        // MobileView.FixGargoyleEquipments
        public static ushort FixGargoyleEquipment(ushort graphic)
        {
            switch (graphic)
            {
                case 0x01D5: return 0x0156; // robe
                case 0x03CA: return 0x0223; // dead shroud
                case 0x03D8: return 329;    // spellbook
                case 0x0372: return 330;    // necrobook
                case 0x0374: return 328;    // chivalry book
                case 0x036F: return 327;    // bushido book
                case 0x036E: return 328;    // ninjitsu book
                case 0x0426: return 0x042B; // masteries book
                case 1529: return 1531;     // pirate shield, male
                case 1530: return 1532;     // pirate shield, female
            }

            return graphic;
        }

        /// <summary>
        /// PaperdollOrder.ApplyDirectionCloak on a slot index list: the cloak goes on top facing north (0), behind facing
        /// south (3), else just below the helmet. MobileView moves the cloak on the full layer order, where Helmet is always
        /// listed, and skips empty layers afterwards; the slots hold only drawn layers, so "below the helmet" is
        /// <paramref name="helmetAnchor"/>, the number of drawn non-cloak slots before Helmet's place in the full order
        /// (<see cref="CloakAnchor"/>). With no helmet drawn the cloak still goes under the weapons, as in MobileView.
        /// </summary>
        public static int ApplyDirectionCloak(Span<byte> order, int count, ReadOnlySpan<byte> layerOf, byte dir, byte helmetAnchor)
        {
            int idx = -1;

            for (int i = 0; i < count; i++)
            {
                if (layerOf[order[i]] == (byte)Layer.Cloak)
                {
                    idx = i;

                    break;
                }
            }

            if (idx < 0)
            {
                return count;
            }

            byte cloak = order[idx];

            for (int i = idx; i < count - 1; i++)
            {
                order[i] = order[i + 1];
            }

            count--;

            int insert;

            if (dir == 0)
            {
                insert = count;
            }
            else if (dir == 3)
            {
                insert = 0;
            }
            else
            {
                insert = Math.Min(helmetAnchor, count);
            }

            for (int i = count; i > insert; i--)
            {
                order[i] = order[i - 1];
            }

            order[insert] = cloak;

            return count + 1;
        }

        /// <summary>
        /// The cloak's "below the helmet" slot for <see cref="ApplyDirectionCloak"/>: how many drawn layers
        /// (<paramref name="slotLayers"/>), not counting the cloak, come before Helmet in <paramref name="fullOrder"/>, the
        /// full layer order (PaperdollOrder.Build plus Filter, which always lists Helmet).
        /// </summary>
        public static byte CloakAnchor(ReadOnlySpan<Layer> fullOrder, ReadOnlySpan<byte> slotLayers)
        {
            int anchor = 0;

            foreach (Layer layer in fullOrder)
            {
                if (layer == Layer.Helmet)
                {
                    break;
                }

                if (layer != Layer.Cloak && slotLayers.IndexOf((byte)layer) >= 0)
                {
                    anchor++;
                }
            }

            return (byte)anchor;
        }

        /// <summary>
        /// MobileView.DrawInternal's hue choice for one layer. Returns the hue (0x8000 already folded into
        /// <paramref name="partial"/>) to pass to ShaderHueTranslator.GetHueVector.
        /// </summary>
        public static ushort LayerHue(ushort overrideHue, ushort layerHue, bool isMount, bool isItem, bool itemPartial, ushort hueFromFile, ushort convColor, out bool partial)
        {
            partial = false;
            ushort hue = overrideHue;

            if (hue == 0)
            {
                hue = layerHue;
                partial = !isMount && isItem && itemPartial;

                if ((hue & 0x8000) != 0)
                {
                    partial = true;
                    hue &= 0x7FFF;
                }

                if (hue == 0)
                {
                    hue = hueFromFile;

                    if (hue == 0)
                    {
                        hue = convColor;
                    }

                    partial = false;
                }
            }

            return hue;
        }

        /// <summary>
        /// MobileView.DrawInternal splits an unseated sprite into horizontal strips (depth + 1 + 2i each) so a mobile
        /// walking between tiles sorts against statics. Writes (y, height) of each strip relative to the frame top;
        /// zero-height strips are kept (the batcher draws nothing for them). Returns the strip count.
        /// </summary>
        public static int Strips(int frameHeight, int centerY, int mountOffset, Span<(int Y, int H)> dest)
        {
            int diffY = frameHeight + centerY - mountOffset;
            int value = Math.Max(1, diffY);
            int count = Math.Max(frameHeight / value + 1, 2);

            int y = 0;
            int h = Math.Min(value, frameHeight);
            int remains = frameHeight - h;
            int n = Math.Min(count, dest.Length);

            for (int i = 0; i < n; i++)
            {
                dest[i] = (y, h);
                y += h;
                h = remains;
                remains -= h;
            }

            return n;
        }

        // the most strips Strips can produce for a frame of this height (count = height / 1 + 1 in the worst case)
        public static int MaxStrips(int frameHeight) => Math.Max(frameHeight + 1, 2);

        public struct OverrideHueInput
        {
            public bool HighlightSelected, IsHealthbarTarget, OutOfRange, PlayerDeadGray, Hidden, Dead, Human;
            public bool PoisonHighlight, Poisoned, ParalyzeHighlight, Paralyzed, InvulHighlight, YellowHits, Invulnerable;
            public bool IsPlayer, IsLastAttack, UnderMouseWhileTargeting;
            public ushort NotorietyHue, PoisonHue, ParalyzedHue, InvulnerableHue;
        }

        /// <summary>MobileView.Draw's overriding hue: highlight, healthbar target, range, death screen, hidden, dead, status highlights, attack/target notoriety.</summary>
        public static ushort OverrideHue(in OverrideHueInput i)
        {
            ushort hue;

            if (i.HighlightSelected)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
            }
            else if (i.IsHealthbarTarget)
            {
                hue = i.NotorietyHue;
            }
            else if (i.OutOfRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (i.PlayerDeadGray)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }
            else if (i.Hidden)
            {
                hue = 0x038E;
            }
            else
            {
                hue = 0;

                if (i.Dead)
                {
                    if (!i.Human)
                    {
                        hue = 0x0386;
                    }
                }
                else
                {
                    if (i.PoisonHighlight && i.Poisoned)
                    {
                        hue = i.PoisonHue;
                    }

                    if (i.ParalyzeHighlight && i.Paralyzed && !i.Invulnerable)
                    {
                        hue = i.ParalyzedHue;
                    }

                    if (i.InvulHighlight && !i.Invulnerable && i.YellowHits)
                    {
                        hue = i.InvulnerableHue;
                    }
                }
            }

            if (!i.IsPlayer && (i.IsLastAttack || i.UnderMouseWhileTargeting))
            {
                hue = i.NotorietyHue;
            }

            return hue;
        }

        /// <summary>
        /// MobileView.IsCovered on per-layer tables (index = Layer): <paramref name="gfx"/> item graphics, <paramref name="anim"/>
        /// tiledata AnimIDs, <paramref name="present"/> whether the layer has an item.
        /// </summary>
        public static bool IsCovered(Layer layer, ReadOnlySpan<ushort> gfx, ReadOnlySpan<ushort> anim, ReadOnlySpan<bool> present, ushort body)
        {
            bool hasRobe = present[(int)Layer.Robe];
            ushort robeGfx = hasRobe ? gfx[(int)Layer.Robe] : (ushort)0;
            ushort robeAnim = hasRobe ? anim[(int)Layer.Robe] : (ushort)0;

            bool robeLeavesChestVisible =
                robeGfx == 0x9985 || robeGfx == 0x9986 || robeGfx == 0xA2CA
                || robeGfx == 0xA2CB || robeGfx == 0xA412 || robeGfx == 0xB1DE;

            switch (layer)
            {
                case Layer.Shoes:
                {
                    ushort pantsAnim = present[(int)Layer.Pants] ? anim[(int)Layer.Pants] : (ushort)0;

                    if (robeAnim != 0x504 && pantsAnim != 0x513 && pantsAnim != 0x514)
                    {
                        ushort pantsGfx = present[(int)Layer.Pants] ? gfx[(int)Layer.Pants] : (ushort)0;

                        if (pantsGfx < 0xAEB2)
                        {
                            if (pantsGfx == 0xAEB1 || pantsGfx == 0x1411)
                            {
                                return true;
                            }

                            if (pantsGfx != 0xAEA2)
                            {
                                return present[(int)Layer.Legs];
                            }
                        }
                        else
                        {
                            if (pantsGfx == 0xAEC0)
                            {
                                return true;
                            }

                            if (pantsGfx != 0xAECF)
                            {
                                return present[(int)Layer.Legs];
                            }
                        }
                    }

                    return true;
                }

                case Layer.Pants:
                {
                    if (present[(int)Layer.Legs] || robeAnim == 0x504)
                    {
                        return true;
                    }

                    ushort pantsAnim = present[(int)Layer.Pants] ? anim[(int)Layer.Pants] : (ushort)0;

                    if (pantsAnim != 0x1EB && pantsAnim != 0x1FA && pantsAnim != 0x200)
                    {
                        return false;
                    }

                    if (present[(int)Layer.Skirt])
                    {
                        ushort skirtAnim = anim[(int)Layer.Skirt];

                        if (skirtAnim != 0x1C7 && skirtAnim != 0x1E4)
                        {
                            return true;
                        }
                    }

                    if (!hasRobe)
                    {
                        return false;
                    }

                    if (robeAnim < 0x4EC)
                    {
                        if (robeAnim > 0x4E7)
                        {
                            return false;
                        }

                        return robeAnim != 0x229;
                    }

                    return (uint)(robeAnim - 0x5E2) > 3;
                }

                case Layer.Tunic:
                    if (present[(int)Layer.Tunic] && anim[(int)Layer.Tunic] == 0x0238)
                    {
                        return hasRobe && !robeLeavesChestVisible;
                    }

                    break;

                case Layer.Torso:
                {
                    if (robeGfx != 0 && !robeLeavesChestVisible)
                    {
                        return true;
                    }

                    if (present[(int)Layer.Tunic] && gfx[(int)Layer.Tunic] != 0x1541 && gfx[(int)Layer.Tunic] != 0x1542)
                    {
                        if (present[(int)Layer.Torso] && (gfx[(int)Layer.Torso] == 0x782A || gfx[(int)Layer.Torso] == 0x782B))
                        {
                            return true;
                        }
                    }

                    break;
                }

                case Layer.Arms:
                    return robeGfx != 0 && !robeLeavesChestVisible;

                case Layer.Necklace:
                {
                    if (!hasRobe)
                    {
                        return false;
                    }

                    if (robeAnim == 0x5F2 || robeAnim == 0x5F5
                        || (robeAnim >= 0x4E8 && robeAnim <= 0x4EB)
                        || (robeAnim >= 0x5E2 && robeAnim <= 0x5E5))
                    {
                        return false;
                    }

                    return present[(int)Layer.Necklace] && anim[(int)Layer.Necklace] == 0x5EC;
                }

                case Layer.Bracelet:
                    return present[(int)Layer.Bracelet] && gfx[(int)Layer.Bracelet] == 0xB1C0 && present[(int)Layer.Arms];

                case Layer.Hair:
                {
                    if (present[(int)Layer.Helmet] && (uint)(gfx[(int)Layer.Helmet] - 0xA42B) < 2)
                    {
                        return true;
                    }

                    goto case Layer.Helmet;
                }

                case Layer.Helmet:
                    if (robeGfx < 0x4B9E)
                    {
                        if (robeGfx != 0x4B9D)
                        {
                            if (robeGfx < 0x2FBA)
                            {
                                if (robeGfx != 0x2FB9)
                                {
                                    if (robeGfx > 0x2687)
                                    {
                                        return false;
                                    }

                                    if (robeGfx < 0x2683 && (robeGfx < 0x204E || robeGfx > 0x204F))
                                    {
                                        return false;
                                    }
                                }
                            }
                            else if (robeGfx != 0x3173)
                            {
                                return false;
                            }

                            return true;
                        }
                    }
                    else if (robeGfx < 0xA0B0)
                    {
                        if (robeGfx < 0xA0AB && robeGfx != 0x7816)
                        {
                            return false;
                        }
                    }
                    else if (robeGfx != 0xB2B7)
                    {
                        return false;
                    }

                    return !(body == 0x029A || body == 0x029B || body == 0x02B6 || body == 0x02B7);

                case Layer.Skirt:
                {
                    ushort skirtAnim = present[(int)Layer.Skirt] ? anim[(int)Layer.Skirt] : (ushort)0;

                    if (skirtAnim != 0x1C7 && skirtAnim != 0x1E4)
                    {
                        return false;
                    }

                    ushort pantsAnim = present[(int)Layer.Pants] ? anim[(int)Layer.Pants] : (ushort)0;

                    return pantsAnim == 0x1EB || pantsAnim == 0x1FA || pantsAnim == 0x200;
                }
            }

            return false;
        }
    }

    /// <summary>The frame-advance state of Mobile.ProcessAnimation.</summary>
    internal struct AnimationTimer
    {
        public byte Frame;
        public byte FrameCount;
        public byte Interval;
        public ushort RepeatMode;
        public ushort RepeatModeCount;
        public bool Repeat;
        public bool Forward;
        public bool FromServer;
    }

    internal enum AnimationAdvance : byte
    {
        Continue,

        // a server animation finished its repeats: the caller resets to the default action (SetAnimation(0xFF))
        Finished,

        // a normal animation wrapped to frame 0 (a dying mobile's corpse is removed then)
        Wrapped
    }

    internal static class AnimationTiming
    {
        /// <summary>
        /// One frame step of Mobile.ProcessAnimation for an action with <paramref name="frames"/> frames: updates the
        /// frame index and repeat state and returns the delay in ms until the next step.
        /// </summary>
        public static int Advance(ref AnimationTimer t, int frames, out AnimationAdvance result)
        {
            result = AnimationAdvance.Continue;
            int delay = Constants.CHARACTER_ANIMATION_DELAY;
            int fc = frames;
            int frameIndex = t.Frame + (t.FromServer && !t.Forward ? -1 : 1);

            if (t.FromServer)
            {
                delay += delay * (t.Interval + 1);

                if (t.FrameCount == 0)
                {
                    t.FrameCount = (byte)fc;
                }
                else
                {
                    fc = t.FrameCount;
                }

                bool ended = true;

                if (t.Forward && frameIndex >= fc)
                {
                    frameIndex = 0;
                }
                else if (!t.Forward && frameIndex < 0)
                {
                    frameIndex = fc == 0 ? 0 : (byte)(frames - 1);
                }
                else
                {
                    ended = false;
                }

                if (ended && t.RepeatMode != 0 && --t.RepeatMode == 0)
                {
                    if (t.Repeat)
                    {
                        t.RepeatModeCount = t.RepeatMode;
                        t.Repeat = false;
                    }
                    else
                    {
                        result = AnimationAdvance.Finished;
                    }
                }
            }
            else if (frameIndex >= fc)
            {
                frameIndex = 0;
                result = AnimationAdvance.Wrapped;
            }

            t.Frame = (byte)(frameIndex % frames);

            return delay;
        }
    }
}
