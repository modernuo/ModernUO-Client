// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using Uo.Pack;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    // T3a (ecs-transition-plan.md, T3): a mobile's worn equipment by layer, owned by the ECS (WornLayers on the mobile's mirror
    // record: the first attached item per layer by child-order stamp). Entity.FindItemByLayer reads it for a mobile.
    /// <summary>T3a: whether World holds a mobile instance for its serial, as its <see cref="WornCache"/> last verified.</summary>
    internal enum WornHeld : byte
    {
        Unknown,
        /// <summary>World does not hold this instance for its serial: nothing worn (the preview's table for its preview).</summary>
        NotHeld,
        /// <summary>World holds it but the mirror has no record for the serial: nothing worn.</summary>
        NoRecord,
        /// <summary>World holds it; <see cref="WornCache.Entity"/> is its record.</summary>
        Record
    }

    /// <summary>
    /// T3a: a mobile's equipment read cache (<c>Mobile.EquipCache</c>, made at its first read): the
    /// verified record, and the Item objects of its WornLayers serials (null when nothing is worn), valid for the mirror
    /// epoch, slot-write count and WornLayers version it was filled at.
    /// </summary>
    internal sealed class WornCache
    {
        public EcsEntity Entity;
        public uint Serial, Epoch;
        public ulong Version;
        public long Writes;
        public WornHeld Held;
        public Item[] Items;

        /// <summary>T3b: the cached world layering answer (null until first used).</summary>
        public LayerDecision Layers;
    }

    internal sealed partial class WorldState
    {
        /// <summary>
        /// The ECS's worn item of <paramref name="mobile"/> on <paramref name="layer"/>: the item World holds under the serial
        /// WornLayers records (null when none). An instance World does not hold for its serial (the appearance gumps'
        /// detached preview mobile, serial 1; a replaced or destroyed mobile) never reads that serial's record: the preview
        /// reads its <see cref="PreviewEquipment"/> table, any other wears nothing. Layers WornLayers does not track (0: the
        /// trade boxes; above 0x1D) are the first child on the layer in the ECS's child order. Served from the mobile's
        /// <see cref="WornCache"/>: a hit is three compares and an array load.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Item WornItem(Mobile mobile, Layer layer)
        {
            uint index = (uint)layer - 1;
            WornCache c = Worn(mobile);

            if (index >= WornLayers.COUNT || c.Held == WornHeld.NotHeld)
            {
                return NotWorn(mobile, layer, c.Held);
            }

            return c.Items?[index];
        }

        // T9 child-list slices A and B: the reads WornLayers does not answer. The character creation's preview: its table.
        // Another instance World does not hold (a replaced or destroyed mobile): nothing. A held mobile's untracked layer: its
        // first child on the layer in the ECS's child order.
        private Item NotWorn(Mobile mobile, Layer layer, WornHeld held)
        {
            if (held == WornHeld.NotHeld)
            {
                return mobile.Preview?.Get(layer);
            }

            return ChildByLayer(mobile, layer);
        }

        /// <summary>
        /// T9 child-list slice A: <c>Entity.FindItemByLayer</c> for an item (a corpse's equipment, CorpseOrder, ItemView):
        /// its first non-destroyed child on <paramref name="layer"/> in the ECS's child order (the spell entries first, as
        /// <see cref="Children"/>). An instance World does not hold for its serial has none (no record of its own).
        /// </summary>
        public Item FindChild(Entity parent, Layer layer) => IsCurrent(parent) ? ChildByLayer(parent, layer) : null;

        private Item ChildByLayer(Entity parent, Layer layer)
        {
            foreach (Item entry in SpellEntries(parent.Serial))
            {
                if (!entry.IsDestroyed && entry.Layer == layer)
                {
                    return entry;
                }
            }

            foreach (uint serial in _world.Ecs.Mirror.Contents(parent.Serial))
            {
                Item it = _world.Items.Get(serial);

                // never the parent itself (an item under a mobile's serial, worn by it: see Children)
                if (it != null && !it.IsDestroyed && it.Layer == layer && !ReferenceEquals(it, parent))
                {
                    return it;
                }
            }

            return null;
        }

        /// <summary>
        /// MobileView's <c>!IsEmpty</c> guard before its equipment reads: whether the ECS records any worn item on a tracked
        /// layer (the layers the view reads). A mobile World does not hold: the preview's table, any other none.
        /// </summary>
        public bool HasWornItems(Mobile mobile)
        {
            WornCache c = Worn(mobile);

            if (c.Held != WornHeld.NotHeld)
            {
                return c.Items != null;
            }

            return mobile.Preview != null && mobile.Preview.Any;
        }

        // the mobile's cache, current: valid while the mirror's epoch (no record removed: every change of the table's instance
        // for a serial, and of the Item object for a serial, goes with a removal) and the mirror's count of slot writes are
        // what it saw; when only the writes moved, the mobile's own WornLayers version decides whether its items are re-read
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private WornCache Worn(Mobile mobile)
        {
            WorldMirror mirror = _world.Ecs.Mirror;
            WornCache c = mobile.EquipCache;

            if (c != null && c.Epoch == mirror.Epoch && c.Writes == mirror.WornWrites && c.Serial == mobile.Serial)
            {
                return c;
            }

            return Refresh(mobile, mirror);
        }

        private WornCache Refresh(Mobile mobile, WorldMirror mirror)
        {
            WornCache c = mobile.EquipCache ??= new WornCache();
            CacheRefreshes++;

            // Codex audit P3 (#4): a cached NoRecord is looked up again at every refresh (a record can be created for the serial,
            // also over attached children, without an epoch change: only removals move the epoch)
            if (c.Epoch != mirror.Epoch || c.Serial != mobile.Serial || c.Held == WornHeld.Unknown || c.Held == WornHeld.NoRecord)
            {
                c.Serial = mobile.Serial;
                c.Epoch = mirror.Epoch;
                c.Version = 0;
                c.Entity = default;

                if (!ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
                {
                    c.Held = WornHeld.NotHeld;
                    c.Items = null;
                }
                else if (mirror.TryGetEntity(mobile.Serial, out EcsEntity e) && e.HasComponent<EquipmentSlots>())
                {
                    c.Held = WornHeld.Record;
                    c.Entity = e;
                }
                else
                {
                    c.Held = WornHeld.NoRecord;
                    c.Items = null;
                }
            }

            if (c.Held == WornHeld.Record)
            {
                ref WornLayers w = ref c.Entity.GetComponent<EquipmentSlots>().Worn;

                if (w.Version != c.Version || c.Version == 0)
                {
                    CacheFills++;
                    c.Version = w.Version;
                    ReadOnlySpan<uint> serials = w.Serials;

                    if (serials.IndexOfAnyExcept(0u) < 0)
                    {
                        c.Items = null;
                    }
                    else
                    {
                        Item[] items = c.Items ??= new Item[WornLayers.COUNT];

                        for (int i = 0; i < serials.Length; i++)
                        {
                            items[i] = serials[i] == 0 ? null : _world.Items.Get(serials[i]);
                        }
                    }
                }
            }

            c.Writes = mirror.WornWrites;

            return c;
        }

        /// <summary>
        /// T3b: the mobile's cached world layering answer (<see cref="LayerDecision"/>), refilled when its
        /// key moved (the record's WornLayers Version or Look, the subject: body and flags, the rules). Null for an instance
        /// World does not hold for its serial or one without a record (the caller evaluates the rules directly).
        /// </summary>
        public LayerDecision WorldLayers(Mobile mobile, bool gargoyleDraw)
        {
            WornCache c = Worn(mobile);

            if (c.Held != WornHeld.Record)
            {
                return null;
            }

            LayerRules rules = LayerRuleEval.Rules;
            LayerSubject subject = LayerRuleEval.Subject(mobile.Graphic, mobile.IsFemale, gargoyleDraw);
            LayerDecision d = c.Layers ??= new LayerDecision();

            if (d.Lookup(rules, subject, c.Version, _world.Ecs.Mirror, c.Entity))
            {
#if CUO_DEVTOOLS
                if (LayerCacheCheck.Enabled)
                {
                    LayerCacheCheck.OnHit(mobile, d, rules, subject);
                }
#endif

                return d;
            }

            LayerTables t = default;
            t.Gather(mobile);
            d.Fill(ref t);

            return d;
        }

        /// <summary>Cache refreshes (a mirror change since the mobile's last read) and fills (its items re-read).</summary>
        public long CacheRefreshes, CacheFills;

        /// <summary>The read caches of the mobiles World holds (layout-based: <see cref="WornCache"/> 80 B, its items array
        /// 24 + 8 B per tracked layer = 256 B).</summary>
        public (int Caches, int WithItems, long Bytes) ReadCacheMemory()
        {
            int caches = 0, withItems = 0, withLayers = 0;

            foreach (Mobile m in _world.Mobiles.Values)
            {
                if (m.EquipCache != null)
                {
                    caches++;

                    if (m.EquipCache.Items != null)
                    {
                        withItems++;
                    }

                    if (m.EquipCache.Layers != null)
                    {
                        withLayers++;
                    }
                }
            }

            return (caches, withItems, caches * 80L + withItems * (24L + 8 * WornLayers.COUNT) + withLayers * (long)LayerDecision.Bytes);
        }

        /// <summary>Memory of WornLayers (layout-based: the component per mobile record; see <see cref="WorldMirror.WornLayersBytes"/>).</summary>
        public long WornLayersBytes => _world.Ecs.Mirror.WornLayersBytes;
    }
}
