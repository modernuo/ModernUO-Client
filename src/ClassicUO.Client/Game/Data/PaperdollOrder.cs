// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Data
{
    /// <summary>
    /// The equipment layer order's key and filter. The order itself comes from the layering rules data
    /// (<see cref="LayerRuleEval"/>, T3b); the hard-coded tables that were here went with <c>ecs_layer_rules</c> (T9).
    /// </summary>
    internal static class PaperdollOrder
    {
        public const int N = 0x19; // 25 layers scanned by the order rules

        /// <summary>
        /// Fill <paramref name="gfx"/> (length >= N) with the entity's per-layer equip
        /// AnimIDs (0x00..0x18); empty layers are left at 0. The base-table selection and
        /// reorder rules are keyed on the item AnimID (the gump/equip display id), NOT the
        /// world tile graphic — e.g. bone-armor arms 0x1410/0x1417 both resolve to AnimID
        /// 0x210, which selects the arms-late table.
        /// </summary>
        public static void GraphicsFromEntity(Entity entity, Span<ushort> gfx)
        {
            gfx.Slice(0, N).Clear();

            for (int layer = (int)Layer.OneHanded; layer <= (int)Layer.Legs; layer++)
            {
                Item item = entity.FindItemByLayer((Layer)layer);
                if (item != null)
                {
                    gfx[layer] = item.ItemData.AnimID;
                }
            }
        }

        /// <summary>
        /// Copy a raw N-entry order into <paramref name="dest"/>, dropping the sentinel
        /// and Mount. Backpack is kept only when <paramref name="includeBackpack"/> is
        /// set. Returns the entry count.
        /// </summary>
        public static int Filter(ReadOnlySpan<Layer> order, bool includeBackpack, Span<Layer> dest)
        {
            int c = 0;
            foreach (Layer layer in order)
            {
                if (layer == Layer.Invalid || layer == Layer.Mount) continue;
                if (layer == Layer.Backpack && !includeBackpack) continue;
                dest[c++] = layer;
            }

            return c;
        }
    }
}
