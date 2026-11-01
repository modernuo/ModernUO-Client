// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    // T3a (ecs-transition-plan.md, T3): a mobile's worn equipment by layer, owned by the ECS (WornLayers on the mobile's mirror
    // record: the first attached item per layer by child-order stamp). Entity.FindItemByLayer reads it for a mobile with
    // ecs_read_equipment on; the LinkedObject child list is still maintained (dual) until T9.
    /// <summary>T3a: whether World holds a mobile instance for its serial, as its <see cref="WornCache"/> last verified.</summary>
    internal enum WornHeld : byte
    {
        Unknown,
        /// <summary>World does not hold this instance for its serial: its own list answers.</summary>
        NotHeld,
        /// <summary>World holds it but the mirror has no record for the serial: nothing worn.</summary>
        NoRecord,
        /// <summary>World holds it; <see cref="WornCache.Entity"/> is its record.</summary>
        Record
    }

    /// <summary>
    /// T3a: a mobile's equipment read cache (<c>Mobile.EquipCache</c>, made at its first read with ecs_read_equipment on): the
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
    }

    internal sealed partial class WorldState
    {
        /// <summary>Tests: the switch for this World (null: the setting).</summary>
        internal bool? ReadEquipmentOverride;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_READ_EQUIPMENT=1|0)
        private static readonly bool? _envReadEquipment = Environment.GetEnvironmentVariable("CUO_ECS_READ_EQUIPMENT") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _envReadEquipment = null;
#endif

        /// <summary>T3a (<c>ecs_read_equipment</c>, default off): the equipment readers take a mobile's worn items from the ECS.</summary>
        public bool ReadEquipment => ReadEquipmentOverride ?? _envReadEquipment ?? Configuration.Settings.GlobalSettings.EcsReadEquipment;

        /// <summary>
        /// <c>Entity.FindItemByLayer</c> for a mobile under the checker: with <see cref="ReadEquipment"/> the ECS's worn item
        /// on the layer (<see cref="WornItem"/>), else the child list's first item on it; <c>CUO_EQUIP_READ_CHECK=1</c>
        /// compares the two at every call.
        /// </summary>
        public Item FindWorn(Mobile mobile, Layer layer)
        {
            Item found = ReadEquipment ? WornItem(mobile, layer) : mobile.FindItemByLayerInList(layer);
#if CUO_DEVTOOLS
            if (EquipCheckEnabled)
            {
                CheckWorn(mobile, layer, found);
            }
#endif

            return found;
        }

        /// <summary>
        /// The ECS's worn item of <paramref name="mobile"/> on <paramref name="layer"/>: the item World holds under the serial
        /// WornLayers records (null when none). An instance World does not hold for its serial (the appearance gumps'
        /// detached preview mobile, serial 1; a replaced or destroyed mobile) never reads that serial's record: its own
        /// list answers, as the legacy read did (its clothes are UI objects, not world state). Layers WornLayers does not
        /// track (0: the trade boxes; above 0x1D) are read from the list too. Served from the mobile's
        /// <see cref="WornCache"/>: a hit is three compares and an array load.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Item WornItem(Mobile mobile, Layer layer)
        {
            uint index = (uint)layer - 1;
            WornCache c = Worn(mobile);

            if (index >= WornLayers.COUNT || c.Held == WornHeld.NotHeld)
            {
                return mobile.FindItemByLayerInList(layer);
            }

            return c.Items?[index];
        }

        /// <summary>
        /// MobileView's <c>!IsEmpty</c> guard before its equipment reads: with <see cref="ReadEquipment"/> whether the ECS
        /// records any worn item on a tracked layer (the layers the view reads; a mobile World does not hold: its list), else
        /// whether the child list has any child.
        /// </summary>
        public bool HasWornItems(Mobile mobile)
        {
            bool found = ReadEquipment ? WornGuard(mobile) : !mobile.IsEmpty;
#if CUO_DEVTOOLS
            if (EquipCheckEnabled)
            {
                CheckGuard(mobile, found);
            }
#endif

            return found;
        }

        // the ECS's guard: any worn item on a tracked layer (a mobile World does not hold: its own list)
        private bool WornGuard(Mobile mobile)
        {
            WornCache c = Worn(mobile);

            return c.Held == WornHeld.NotHeld ? !mobile.IsEmpty : c.Items != null;
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

        /// <summary>Cache refreshes (a mirror change since the mobile's last read) and fills (its items re-read).</summary>
        public long CacheRefreshes, CacheFills;

        /// <summary>The read caches of the mobiles World holds (layout-based: <see cref="WornCache"/> 72 B, its items array
        /// 24 + 8 B per tracked layer = 256 B).</summary>
        public (int Caches, int WithItems, long Bytes) ReadCacheMemory()
        {
            int caches = 0, withItems = 0;

            foreach (Mobile m in _world.Mobiles.Values)
            {
                if (m.EquipCache != null)
                {
                    caches++;

                    if (m.EquipCache.Items != null)
                    {
                        withItems++;
                    }
                }
            }

            return (caches, withItems, caches * 72L + withItems * (24L + 8 * WornLayers.COUNT));
        }

        /// <summary>Memory of WornLayers (layout-based: the component per mobile record; see <see cref="WorldMirror.WornLayersBytes"/>).</summary>
        public long WornLayersBytes => _world.Ecs.Mirror.WornLayersBytes;

        // ---- CUO_EQUIP_READ_CHECK (CUO_DEVTOOLS builds only): every FindItemByLayer call on a mobile compares the child list's
        // first item on the layer with the ECS's (WornItem): the same serial (0 for none). CLEANUP(release): goes with the
        // equipment half of the child list (T9).
#if CUO_DEVTOOLS
        public const bool EquipCheckAvailable = true;

        public static readonly bool EquipCheckFromEnv = Environment.GetEnvironmentVariable("CUO_EQUIP_READ_CHECK") == "1";

        /// <summary>The checker is on (from <c>CUO_EQUIP_READ_CHECK=1</c>; tests set it per World).</summary>
        public bool EquipCheckEnabled;

        /// <summary>Checker counts: calls compared, mismatches found.</summary>
        public long EquipReads, EquipMismatches;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal Action<string> EquipMismatchObserver;

        private uint _equipLogAt;
        private long _loggedEquipReads = -1;

        private void CheckWorn(Mobile mobile, Layer layer, Item found)
        {
            EquipReads++;
            Item list = mobile.FindItemByLayerInList(layer);
            Item ecs = ReadEquipment ? found : WornItem(mobile, layer);
            uint ls = list?.Serial ?? 0, es = ecs?.Serial ?? 0;

            // Codex audit P3 (#5): the same object, not only the same serial (a stale instance under a serial is a divergence)
            if (ls != es || !ReferenceEquals(list, ecs))
            {
                string same = ls == es ? " (same serial, another instance)" : "";
                ReportEquip($"[equip] mismatch (FindItemByLayer) 0x{mobile.Serial:X8} layer {layer} (0x{(byte)layer:X2}): World 0x{ls:X8} | ECS 0x{es:X8}{same} | read_equipment={(ReadEquipment ? "on" : "off")}");
            }
        }

        // Codex audit P3 (#5): MobileView's guard. The ECS's answer against the list's tracked live equipment: a child World
        // holds (the same object), not destroyed, on a tracked layer. Not against !IsEmpty: a mobile whose children are all
        // on untracked layers (0, above 0x1D) has no worn item, and the view's guarded reads are tracked layers only.
        private void CheckGuard(Mobile mobile, bool found)
        {
            EquipReads++;
            bool ecs = ReadEquipment ? found : WornGuard(mobile);
            bool list = ListGuard(mobile);

            if (ecs != list)
            {
                ReportEquip($"[equip] mismatch (HasWornItems) 0x{mobile.Serial:X8}: World {list} | ECS {ecs} | read_equipment={(ReadEquipment ? "on" : "off")}");
            }
        }

        private bool ListGuard(Mobile mobile)
        {
            if (!ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
            {
                return !mobile.IsEmpty;
            }

            for (LinkedObject i = mobile.Items; i != null; i = i.Next)
            {
                if (i is Item it && !it.IsDestroyed && WornLayers.Tracked((byte)it.Layer) && ReferenceEquals(_world.Items.Get(it.Serial), it))
                {
                    return true;
                }
            }

            return false;
        }

        private void ReportEquip(string message)
        {
            EquipMismatches++;
            EquipMismatchObserver?.Invoke(message);

            if (EquipMismatches <= MAX_LOGGED_MISMATCHES)
            {
                Log.Warn(message);
            }
        }

        /// <summary>The checker's 5 s summary while its counts change (called every frame by <c>WorldEcs.Update</c>).</summary>
        public void LogEquipmentChecks()
        {
            if (!EquipCheckEnabled || Time.Ticks < _equipLogAt || EquipReads == _loggedEquipReads)
            {
                return;
            }

            _equipLogAt = Time.Ticks + 5000;
            _loggedEquipReads = EquipReads;
            LogEquipmentSummary();
        }

        public void LogEquipmentSummary()
        {
            WorldMirror m = _world.Ecs.Mirror;
            (int caches, int withItems, long cacheBytes) = ReadCacheMemory();
            Log.Info($"[equip] checks reads={EquipReads} mismatches={EquipMismatches} | read_equipment={(ReadEquipment ? "on" : "off")} mobiles={m.MobileCount} worn bytes={m.WornLayersBytes} writes={m.WornWrites} rescans={m.WornRescans} 0x78 batches written={m.WornBatchWrites} unchanged={m.WornBatchUnchanged} | read caches={caches} with items={withItems} bytes={cacheBytes} refreshes={CacheRefreshes} fills={CacheFills}");
        }
#else
        public const bool EquipCheckAvailable = false;

        public bool EquipCheckEnabled
        {
            get => false;
            set { }
        }

        public long EquipReads => 0;

        public long EquipMismatches => 0;

        internal Action<string> EquipMismatchObserver
        {
            get => null;
            set { }
        }

        public void LogEquipmentChecks() { }
#endif
    }
}
