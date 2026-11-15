// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game;
using ClassicUO.Game.Data;

namespace ClassicUO.UnitTests.Ecs
{
    /// <summary>
    /// T9 (ecs_layer_rules deleted): the client's former hard-coded equipment layering rules, frozen here as the reference
    /// the data rules (Uo.Pack.LayerRules, LayerRulesDefault) are tested against. Copied unchanged from PaperdollOrder
    /// (Build, ApplyDirectionCloak and the reorder primitives), AnimationRules (IsCovered = MobileView.IsCovered on tables,
    /// FixGargoyleEquipment = MobileView.FixGargoyleEquipments) and LayerRuleEval.RemapPaperdollHard
    /// (PaperDollInteractable.GetAnimID's gargoyle-ghost shroud). The client never uses it. Do not change it to follow the
    /// data: a difference is what the tests are there to catch.
    /// </summary>
    internal static class LegacyLayerRulesReference
    {
        private const int N = PaperdollOrder.N;

        // --- base tables, 25 layers each (leading Invalid is a sentinel) ---------
        // T1: arms drawn late
        private static readonly Layer[] T1 =
        {
            Layer.Invalid, Layer.Cloak, Layer.Shirt, Layer.Pants, Layer.Shoes, Layer.Legs,
            Layer.Torso, Layer.Tunic, Layer.Ring, Layer.Bracelet, Layer.Face, Layer.Arms,
            Layer.Gloves, Layer.Skirt, Layer.Robe, Layer.Waist, Layer.Necklace, Layer.Hair,
            Layer.Beard, Layer.Earrings, Layer.Helmet, Layer.OneHanded, Layer.TwoHanded,
            Layer.Backpack, Layer.Talisman
        };

        // T2: DEFAULT — arms early (matches the old _layerOrder)
        private static readonly Layer[] T2 =
        {
            Layer.Invalid, Layer.Cloak, Layer.Shirt, Layer.Pants, Layer.Shoes, Layer.Legs,
            Layer.Arms, Layer.Torso, Layer.Tunic, Layer.Ring, Layer.Bracelet, Layer.Face,
            Layer.Gloves, Layer.Skirt, Layer.Robe, Layer.Waist, Layer.Necklace, Layer.Hair,
            Layer.Beard, Layer.Earrings, Layer.Helmet, Layer.OneHanded, Layer.TwoHanded,
            Layer.Backpack, Layer.Talisman
        };

        // T3: torso pulled to front (female-style chest-under)
        private static readonly Layer[] T3 =
        {
            Layer.Invalid, Layer.Cloak, Layer.Torso, Layer.Shirt, Layer.Pants, Layer.Shoes,
            Layer.Legs, Layer.Tunic, Layer.Ring, Layer.Bracelet, Layer.Face, Layer.Arms,
            Layer.Gloves, Layer.Skirt, Layer.Robe, Layer.Waist, Layer.Necklace, Layer.Hair,
            Layer.Beard, Layer.Earrings, Layer.Helmet, Layer.OneHanded, Layer.TwoHanded,
            Layer.Backpack, Layer.Talisman
        };

        /// <summary>
        /// Build the layer draw-order into <paramref name="order"/> (must be >= N).
        /// <paramref name="graphic"/> is indexed by layer (0x00..0x18); 0 = nothing
        /// equipped on that layer. <paramref name="altTorsoTable"/> gates the
        /// gargoyle/female torso variant. The result is back-to-front paint order —
        /// later index = painted on top. No heap allocation.
        /// </summary>
        public static void Build(ReadOnlySpan<ushort> graphic, bool altTorsoTable, Span<Layer> order)
        {
            // 1) base table selection --------------------------------------------
            // An Arms-graphic alternate match locks the Alternate table and skips the
            // Torso/race checks entirely.
            Layer[] table = T2; // default

            uint arms = graphic[(int)Layer.Arms];
            bool armsAlternate;
            if (arms < 0x3d0)
            {
                armsAlternate = arms == 0x3cf || arms == 0x210 || arms == 0x3b3;
            }
            else
            {
                armsAlternate = arms == 0x3dd;
            }

            if (armsAlternate)
            {
                table = T1;
            }
            else
            {
                uint torso = graphic[(int)Layer.Torso];
                if (torso == 0x21a) table = T1;
                else if (torso - 0x399 < 5 && altTorsoTable) table = T3;
            }

            table.AsSpan(0, N).CopyTo(order);
            Span<Layer> o = order.Slice(0, N);

            // 2) per-graphic reorder rules ---------------------------------------
            // shirt present + pants gfx 0x398 -> move Pants to Shirt's slot
            if (graphic[(int)Layer.Shirt] != 0 && graphic[(int)Layer.Pants] == 0x398)
            {
                MoveTo(o, Layer.Pants, Layer.Shirt);
            }

            uint pants = graphic[(int)Layer.Pants];
            bool skipFinalPantsCheck = false;
            if (pants < 0x201)
            {
                if (pants == 0x200 || pants == 0x1eb || pants == 0x1fa)
                {
                    // ensure Shoes before Pants: if Pants currently precedes Shoes, swap
                    int iShoes = IndexOf(o, Layer.Shoes), iPants = IndexOf(o, Layer.Pants);
                    if (iShoes >= 0 && iPants >= 0 && iPants < iShoes)
                    {
                        o[iShoes] = Layer.Pants;
                        o[iPants] = Layer.Shoes;
                    }
                }
            }
            else if (pants - 0x513u < 2)
            {
                if (graphic[(int)Layer.Shoes] != 0) MoveTo(o, Layer.Pants, Layer.Shoes);
                skipFinalPantsCheck = true; // skip the 0x3e4 check below
            }

            if (!skipFinalPantsCheck && graphic[(int)Layer.Shoes] != 0 && graphic[(int)Layer.Pants] == 0x3e4)
            {
                MoveTo(o, Layer.Pants, Layer.Shoes);
            }

            // tunic gfx 0x238 -> Tunic after Waist; +robe in set -> Robe after Neck
            if (graphic[(int)Layer.Tunic] == 0x238)
            {
                MoveAfter(o, Layer.Tunic, Layer.Waist);
                if (graphic[(int)Layer.Robe] != 0)
                {
                    uint r = graphic[(int)Layer.Robe];
                    if (r == 0x4e8 || r == 0x4e9 || r == 0x4ea || r == 0x4eb ||
                        r == 0x5e2 || r == 0x5e3 || r == 0x5e4 || r == 0x5e5)
                    {
                        MoveTo(o, Layer.Robe, Layer.Necklace);
                    }
                }
            }

            // cloak/quiver gfx 0x380/0x5F3 -> Cloak after Robe (the quiver fix)
            uint cloak = graphic[(int)Layer.Cloak];
            if (cloak == 0x380 || cloak == 0x5f3) MoveAfter(o, Layer.Cloak, Layer.Robe);

            // helmet/neck interplay
            uint helm = graphic[(int)Layer.Helmet];
            if (helm < 0x202)
            {
                uint neck = graphic[(int)Layer.Necklace];
                if ((helm == 0x201 || helm == 0x1a9) &&
                    (neck == 0x1c8 || (neck > 0x1d6 && neck < 0x1d9)))
                {
                    MoveAfter(o, Layer.Necklace, Layer.Helmet);
                    return; // done — helmet covers the neck item
                }
            }
            else if (helm - 0x5e9u < 2 && graphic[(int)Layer.Robe] != 0 && (graphic[(int)Layer.Robe] - 0x5e2u) < 4)
            {
                MoveTo(o, Layer.Robe, Layer.Helmet);
            }
        }

        /// <summary>
        /// Reposition Cloak for the in-world views, which the official client keys on
        /// facing direction rather than the paperdoll cloak rules: dir 0 (North/away)
        /// draws the cloak on top, dir 3 (facing viewer) behind, every other dir places
        /// it just below the helmet. Operates in place; returns the (unchanged) count.
        /// </summary>
        public static int ApplyDirectionCloak(Span<Layer> layers, int count, byte dir)
        {
            int idx = -1;
            for (int i = 0; i < count; i++)
            {
                if (layers[i] == Layer.Cloak) { idx = i; break; }
            }

            if (idx < 0) return count;

            // remove cloak
            for (int i = idx; i < count - 1; i++) layers[i] = layers[i + 1];
            count--;

            int insert;
            if (dir == 0)
            {
                insert = count; // painted last = on top
            }
            else if (dir == 3)
            {
                insert = 0; // painted first = behind
            }
            else
            {
                insert = count;
                for (int i = 0; i < count; i++)
                {
                    if (layers[i] == Layer.Helmet) { insert = i; break; }
                }
            }

            // open a slot at 'insert' and drop cloak in
            for (int i = count; i > insert; i--) layers[i] = layers[i - 1];
            layers[insert] = Layer.Cloak;
            return count + 1;
        }

        // --- reorder primitives (exact memmove semantics) -----------------------
        private static int IndexOf(ReadOnlySpan<Layer> a, Layer v)
        {
            for (int i = 0; i < N; i++)
            {
                if (a[i] == v) return i;
            }

            return -1;
        }

        /// Relocate value A so it lands on B's index.
        private static void MoveTo(Span<Layer> a, Layer A, Layer B)
        {
            int i1 = IndexOf(a, A); if (i1 < 0) return;
            int i2 = IndexOf(a, B); if (i2 < 0 || i2 == i1) return;
            if (i2 < i1) { for (int k = i1; k > i2; k--) a[k] = a[k - 1]; a[i2] = A; }
            else { for (int k = i1; k < i2; k++) a[k] = a[k + 1]; a[i2] = A; }
        }

        /// Relocate value A so it lands immediately AFTER B.
        private static void MoveAfter(Span<Layer> a, Layer A, Layer B)
        {
            int i1 = IndexOf(a, A); if (i1 < 0) return;
            int i2 = IndexOf(a, B); if (i2 < 0 || i2 == i1) return;
            if (i2 < i1) { for (int k = i1; k > i2 + 1; k--) a[k] = a[k - 1]; a[i2 + 1] = A; }
            else { for (int k = i1; k < i2; k++) a[k] = a[k + 1]; a[i2] = A; }
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

        /// <summary>PaperDollInteractable.GetAnimID's remap: the gargoyle ghost's shroud on 7.0.0.0+.</summary>
        public static ushort RemapPaperdoll(ushort anim, ushort body, bool cv7000) =>
            cv7000 && anim == 0x03CA && (body == 0x02B7 || body == 0x02B6) ? (ushort)0x0223 : anim;
    }
}
