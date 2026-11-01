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
        /// <c>Entity.FindItemByLayer</c> for a mobile: with <see cref="ReadEquipment"/> the ECS's worn item on the layer
        /// (<see cref="WornItem"/>), else the child list's first item on it. Under <c>CUO_EQUIP_READ_CHECK=1</c> every call
        /// compares the two.
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
        /// track (0: the trade boxes; above 0x1D) are read from the list too.
        /// </summary>
        public Item WornItem(Mobile mobile, Layer layer)
        {
            if (!WornLayers.Tracked((byte)layer))
            {
                return mobile.FindItemByLayerInList(layer);
            }

            switch (Resolve(mobile, out EcsEntity e))
            {
                case Held.Record:
                    uint serial = e.GetComponent<WornLayers>().Serials[(byte)layer - 1];

                    return serial == 0 ? null : _world.Items.Get(serial);

                case Held.NoRecord:
                    return null;

                default:
                    return mobile.FindItemByLayerInList(layer);
            }
        }

        /// <summary>
        /// MobileView's <c>!IsEmpty</c> guard before its equipment reads: with <see cref="ReadEquipment"/> whether the ECS
        /// records any worn item on a tracked layer (the layers the view reads; a mobile World does not hold: its list), else
        /// whether the child list has any child.
        /// </summary>
        public bool HasWornItems(Mobile mobile)
        {
            if (!ReadEquipment)
            {
                return !mobile.IsEmpty;
            }

            switch (Resolve(mobile, out EcsEntity e))
            {
                case Held.Record:
                    ref WornLayers worn = ref e.GetComponent<WornLayers>();
                    ReadOnlySpan<uint> serials = worn.Serials;

                    return serials.IndexOfAnyExcept(0u) >= 0;

                case Held.NoRecord:
                    return false;

                default:
                    return !mobile.IsEmpty;
            }
        }

        private enum Held : byte
        {
            /// <summary>World does not hold this instance for its serial: its own list answers.</summary>
            NotHeld,
            /// <summary>World holds it but the mirror has no record for the serial: nothing worn.</summary>
            NoRecord,
            /// <summary>World holds it; the entity is its record.</summary>
            Record
        }

        // the instance's mirror record, verified to be the instance World holds for its serial. The verification is cached on
        // the mobile with the mirror's epoch (Mobile.EquipEntity*: its own fields, never E5's, which the Gpu world fills
        // without the identity check): every change of the table's instance for a serial goes with a removal of the serial's
        // record (RemoveMobile, the re-key, a clear), which bumps the epoch, so a hit costs two compares and no lookup
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Held Resolve(Mobile mobile, out EcsEntity e)
        {
            WorldMirror mirror = _world.Ecs.Mirror;

            if (mobile.EquipEntityEpoch == mirror.Epoch && mobile.EquipEntitySerial == mobile.Serial && mobile.EquipEntitySerial != 0)
            {
                e = mobile.EquipEntity;

                return Held.Record;
            }

            return ResolveSlow(mobile, mirror, out e);
        }

        private Held ResolveSlow(Mobile mobile, WorldMirror mirror, out EcsEntity e)
        {
            e = default;

            if (!ReferenceEquals(_world.Mobiles.Get(mobile.Serial), mobile))
            {
                return Held.NotHeld;
            }

            if (!mirror.TryGetEntity(mobile.Serial, out e) || !e.HasComponent<WornLayers>())
            {
                return Held.NoRecord;
            }

            mobile.EquipEntity = e;
            mobile.EquipEntitySerial = mobile.Serial;
            mobile.EquipEntityEpoch = mirror.Epoch;

            return Held.Record;
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

            if (ls != es)
            {
                EquipMismatches++;
                string message = $"[equip] mismatch (FindItemByLayer) 0x{mobile.Serial:X8} layer {layer} (0x{(byte)layer:X2}): World 0x{ls:X8} | ECS 0x{es:X8} | read_equipment={(ReadEquipment ? "on" : "off")}";
                EquipMismatchObserver?.Invoke(message);

                if (EquipMismatches <= MAX_LOGGED_MISMATCHES)
                {
                    Log.Warn(message);
                }
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
            Log.Info($"[equip] checks reads={EquipReads} mismatches={EquipMismatches} | read_equipment={(ReadEquipment ? "on" : "off")} mobiles={m.MobileCount} worn bytes={m.WornLayersBytes} writes={m.WornWrites} rescans={m.WornRescans}");
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
