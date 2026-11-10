// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    // T9 (ecs-transition-plan.md §T9, survey t9-world-index-2026-11-08): World.Get, GetItem, GetMobile, FindItem and
    // FindMobile from the mirror's serial index; Holds (the identity guards) from the tables' marks on the object. With the
    // switch on, the tables' dictionaries are read only by World's own table bookkeeping (GetOrCreate*, the sweeps), the
    // switch's off paths, the checker and EcsInspector. Slice 4: Items.Values walks the item table's slots (the dictionary's
    // order, kept without it) with the switch on; the checker compares both tables' slot order with their dictionaries'
    internal sealed partial class World
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_WORLD_ENTITY_INDEX=1|0) and the checker
        private static readonly bool s_entityIndex = Environment.GetEnvironmentVariable("CUO_ECS_WORLD_ENTITY_INDEX") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.WorldEntityIndex
        };

        private static readonly bool s_entityIndexCheck = Environment.GetEnvironmentVariable("CUO_ENTITY_INDEX_CHECK") == "1";
#else
        private static readonly bool s_entityIndex = Configuration.Settings.GlobalSettings.WorldEntityIndex;
        private const bool s_entityIndexCheck = false;
#endif

        /// <summary>Tests: the switch for this World (null: the setting, read once at startup).</summary>
        internal bool? EntityIndexOverride;

        /// <summary>Tests: the checker for this World (null: <c>CUO_ENTITY_INDEX_CHECK=1</c>).</summary>
        internal bool? EntityIndexCheckOverride;

        /// <summary>T9 (<c>world_entity_index</c>, default on since slice 5): <see cref="Get"/>, <see cref="GetItem"/> and
        /// <see cref="GetMobile"/> answer from the mirror's serial index, not from the Items and Mobiles tables.</summary>
        public bool EntityIndexOn => EntityIndexOverride ?? s_entityIndex;

        public bool EntityIndexCheckOn => EntityIndexCheckOverride ?? s_entityIndexCheck;

        /// <summary>CUO_ENTITY_INDEX_CHECK counts: lookups compared, mismatches found.</summary>
        public long EntityIndexChecks, EntityIndexMismatches;

        /// <summary>CUO_ENTITY_INDEX_CHECK: <see cref="FindItem"/>/<see cref="FindMobile"/> calls where the table answered a
        /// destroyed object not yet swept (the index answers no object, or the live one now under the serial). The expected
        /// difference of the switch (owner decision 2026-11-08), logged as <c>[entity-index-unswept]</c> with the caller.</summary>
        public long EntityIndexUnswept;

        /// <summary>CUO_ENTITY_INDEX_CHECK counts for <see cref="Holds(Entity)"/> (still the tables) against the index, for a
        /// live object: guards compared, guards where the tables hold an object the mirror has no record for yet (an object
        /// created inside a funnel operation, before its commit), and other differences (logged).</summary>
        public long EntityIndexHoldsChecks, EntityIndexHoldsUncommitted, EntityIndexHoldsDiffs;

        /// <summary>Tests: sees each mismatch report (lookups), each Holds difference, and each unswept find.</summary>
        internal Action<string> EntityIndexObserver, EntityIndexHoldsObserver, EntityIndexUnsweptObserver;

        private uint _entityIndexNextLog;

        /// <summary>The live item under <paramref name="serial"/> (null: none, or destroyed). Kind-checked: a mobile under the
        /// serial is not an item. With <c>world_entity_index</c> on, from the mirror's serial index; otherwise from
        /// <see cref="Items"/>. Differs only where an item and a mobile share a serial (an item sent with a mobile's serial, or a
        /// corpse under a death-rekeyed mobile's): the index holds the kind written last.</summary>
        public Item GetItem(uint serial)
        {
            if (EntityIndexCheckOn)
            {
                return CheckedGetItem(serial);
            }

            return EntityIndexOn ? GetIndexedItem(serial) : GetHeldItem(serial);
        }

        /// <summary>The live mobile under <paramref name="serial"/>; see <see cref="GetItem"/>.</summary>
        public Mobile GetMobile(uint serial)
        {
            if (EntityIndexCheckOn)
            {
                return CheckedGetMobile(serial);
            }

            return EntityIndexOn ? GetIndexedMobile(serial) : GetHeldMobile(serial);
        }

        /// <summary>
        /// T9 slice 3: the reader form of <c>Items.Get(serial)</c> for callers that did not reject destroyed objects. Switch off:
        /// the table's answer, a destroyed object not yet swept included (unchanged). <c>world_entity_index</c> on:
        /// <see cref="GetItem"/>'s live answer, so a destroyed object not yet swept does not exist (owner decision
        /// 2026-11-08). Becomes <see cref="GetItem"/> when the switch's off path is deleted.
        /// </summary>
        public Item FindItem(uint serial)
        {
            if (EntityIndexCheckOn)
            {
                return CheckedFindItem(serial);
            }

            return EntityIndexOn ? GetIndexedItem(serial) : Items.Get(serial);
        }

        /// <summary>The reader form of <c>Mobiles.Get(serial)</c>; see <see cref="FindItem"/>.</summary>
        public Mobile FindMobile(uint serial)
        {
            if (EntityIndexCheckOn)
            {
                return CheckedFindMobile(serial);
            }

            return EntityIndexOn ? GetIndexedMobile(serial) : Mobiles.Get(serial);
        }

        /// <summary>
        /// T9 slice 3 (WorldEcs' commit guard, <c>Entity.FindScalarEntity</c>): the record under <paramref name="serial"/> when
        /// it stands for this live instance. Switch off: World holds the instance under the serial
        /// (<see cref="Holds(Entity, uint)"/>) and the record is of its kind (two lookups). <c>world_entity_index</c> on: the
        /// record's handle is the instance (one lookup). They differ only where <see cref="Holds(Entity, uint)"/> differs from
        /// the index, which CUO_ENTITY_INDEX_CHECK reports (with the checker on, the off path runs as well).
        /// </summary>
        internal bool TryGetLiveRecord(Entity e, uint serial, out Friflo.Engine.ECS.Entity record)
        {
            if (e.IsDestroyed)
            {
                record = default;

                return false;
            }

            if (EntityIndexOn && !EntityIndexCheckOn)
            {
                return Ecs.Mirror.TryGetEntityOf(serial, e, out record);
            }

            bool held = Holds(e, serial) & Ecs.Mirror.TryGetEntity(serial, e is Mobile, out record);

            return EntityIndexOn ? Ecs.Mirror.TryGetEntityOf(serial, e, out record) : held;
        }

        /// <summary>
        /// T9 slice 3 part 2 (<c>WorldEcs.OnDestroyed</c>): a live object of <paramref name="e"/>'s kind, other than
        /// <paramref name="e"/>, under its serial (a newer object owns the serial: its record must stay); null when none.
        /// Switch off: the table of e's kind; <c>world_entity_index</c> on: the index's handle, kind-checked (a newer object
        /// not yet committed has no handle, but then e's record is gone already). CUO_ENTITY_INDEX_CHECK compares both.
        /// </summary>
        internal Entity NewerOwner(Entity e)
        {
            bool on = EntityIndexOn, check = EntityIndexCheckOn;
            Entity tables = null, indexed = null;

            if (!on || check)
            {
                Entity c = e is Item ? Items.Get(e.Serial) : Mobiles.Get(e.Serial);
                tables = c != null && !ReferenceEquals(c, e) && !c.IsDestroyed ? c : null;
            }

            if (on || check)
            {
                object h = Ecs.Mirror.GetHandle(e.Serial);
                Entity c = e is Item ? h as Item : h as Mobile;
                indexed = c != null && !ReferenceEquals(c, e) && !c.IsDestroyed ? c : null;
            }

            if (check)
            {
                CountCheck(e.Serial, tables, indexed, "newer owner");
            }

            return on ? indexed : tables;
        }

        /// <summary>
        /// T9 slice 3 part 2 (<c>HouseManager.IsHouseInRange</c>): the multi whose range decides its house. Switch off: the item
        /// the table holds under <paramref name="multi"/>'s serial (a destroyed multi not yet swept included: the range sweep
        /// asks for one); <c>world_entity_index</c> on: <paramref name="multi"/> itself (every caller passes the item it
        /// holds: the sweep's item from <c>Items.Values</c>, an item checking its own graphic). Compared by the checker.
        /// </summary>
        internal Item HouseMulti(Item multi)
        {
            bool on = EntityIndexOn, check = EntityIndexCheckOn;

            if (on && !check)
            {
                return multi;
            }

            Item held = Items.Get(multi.Serial);

            if (check)
            {
                CountCheck(multi.Serial, held, multi, "house multi");
            }

            return on ? multi : held;
        }

        internal Item GetIndexedItem(uint serial) => Ecs.Mirror.GetHandle(serial) is Item it && !it.IsDestroyed ? it : null;

        internal Mobile GetIndexedMobile(uint serial) => Ecs.Mirror.GetHandle(serial) is Mobile m && !m.IsDestroyed ? m : null;

        internal Item GetHeldItem(uint serial)
        {
            Item it = Items.Get(serial);

            return it == null || it.IsDestroyed ? null : it;
        }

        internal Mobile GetHeldMobile(uint serial)
        {
            Mobile m = Mobiles.Get(serial);

            return m == null || m.IsDestroyed ? null : m;
        }

        /// <summary>
        /// Whether the Items or Mobiles table (by the object's kind) holds this very instance under its serial: the identity
        /// guard of the funnel and the ECS writes. Destroyed objects not yet swept are still held: the tables keep them until
        /// <see cref="Update"/> drops them, since loops over <c>Values</c> destroy objects as they go (the sweep window).
        /// T9 slice 3 part 2: with <c>world_entity_index</c> on, answered from the tables' marks on the object
        /// (<see cref="ItemTable.Holds(Item, uint)"/>, <see cref="MobileTable.Holds"/>) instead of a serial lookup, so no guard
        /// reads the tables' dictionaries; the marks are table membership, which the ordered tables of slice 4 keep.
        /// CUO_ENTITY_INDEX_CHECK compares the marks with the lookup at every call (destroyed objects included), and the
        /// lookup with the index for live objects.
        /// </summary>
        public bool Holds(Entity e) => e != null && Holds(e, e.Serial);

        /// <summary><see cref="Holds(Entity)"/> under <paramref name="serial"/> (a serial the object had or is about to have).</summary>
        public bool Holds(Entity e, uint serial)
        {
            if (e == null)
            {
                return false;
            }

            if (EntityIndexCheckOn)
            {
                return CheckedHolds(e, serial);
            }

            return EntityIndexOn ? HoldsMarked(e, serial) : HoldsLookup(e, serial);
        }

        private bool HoldsLookup(Entity e, uint serial) => e is Mobile ? ReferenceEquals(Mobiles.Get(serial), e) : ReferenceEquals(Items.Get(serial), e);

        private bool HoldsMarked(Entity e, uint serial) => e is Mobile m ? Mobiles.Holds(m, serial) : e is Item it && Items.Holds(it, serial);

        /// <summary>CUO_ENTITY_INDEX_CHECK counts for <see cref="Holds(Entity, uint)"/>'s marks (the switch-on answer) against
        /// the tables' lookup, for every object (destroyed ones included): guards compared, differences (logged).</summary>
        public long EntityIndexMarkChecks, EntityIndexMarkDiffs;

        private bool CheckedHolds(Entity e, uint serial)
        {
            bool held = HoldsLookup(e, serial);
            bool marked = HoldsMarked(e, serial);
            EntityIndexMarkChecks++;

            if (held != marked)
            {
                EntityIndexMarkDiffs++;

                if (WillReport(EntityIndexMarkDiffs, EntityIndexHoldsObserver))
                {
                    ReportEntityIndex(serial, $"holds mark {Describe(e)}{(e.IsDestroyed ? " (destroyed)" : "")}: tables {(held ? "yes" : "no")}, marks {(marked ? "yes" : "no")}", EntityIndexMarkDiffs, EntityIndexHoldsObserver, "entity-index-holds");
                }
            }

            if (!e.IsDestroyed)
            {
                CheckHolds(e, serial, held);
            }

            return EntityIndexOn ? marked : held;
        }

        /// <summary>CUO_ENTITY_INDEX_CHECK counts (T9 slice 4): world updates whose tables' slot order was compared with their
        /// dictionaries' order (<see cref="ItemTable.Values"/> walks the slots with the switch on), and differences (logged).</summary>
        public long EntityIndexOrderChecks, EntityIndexOrderDiffs;

        /// <summary>
        /// CUO_ENTITY_INDEX_CHECK, once per world update (T9 slice 4): the items' and mobiles' slot order against the order of
        /// their key dictionaries (the old enumeration order). Both walks are struct enumerators: no allocation.
        /// </summary>
        internal void CheckTableOrder()
        {
            EntityIndexOrderChecks++;
            bool items = Items.OrderMatches(out int itemAt);
            bool mobiles = Mobiles.OrderMatches(out int mobileAt);

            if (!items || !mobiles)
            {
                EntityIndexOrderDiffs++;

                if (WillReport(EntityIndexOrderDiffs, EntityIndexObserver))
                {
                    string text = $"table order: items {(items ? "same" : $"differ at {itemAt}")}, mobiles {(mobiles ? "same" : $"differ at {mobileAt}")} (items {Items.Count}, mobiles {Mobiles.Count})";

                    if (EntityIndexOrderDiffs <= 20)
                    {
                        Log.Warn($"[entity-index-order] {text}");
                    }

                    EntityIndexObserver?.Invoke(text);
                }
            }

            LogEntityIndexSummary();
        }

        // CUO_ENTITY_INDEX_CHECK: both lookups at every call, the switch's answer returned
        private Entity CheckedGet(uint serial)
        {
            Entity held = GetHeld(serial);
            Entity indexed = GetIndexed(serial);
            CountCheck(serial, held, indexed, "get");

            return EntityIndexOn ? indexed : held;
        }

        private Item CheckedGetItem(uint serial)
        {
            Item held = GetHeldItem(serial);
            Item indexed = GetIndexedItem(serial);
            CountCheck(serial, held, indexed, "item");

            return EntityIndexOn ? indexed : held;
        }

        private Mobile CheckedGetMobile(uint serial)
        {
            Mobile held = GetHeldMobile(serial);
            Mobile indexed = GetIndexedMobile(serial);
            CountCheck(serial, held, indexed, "mobile");

            return EntityIndexOn ? indexed : held;
        }

        private Item CheckedFindItem(uint serial)
        {
            Item held = Items.Get(serial);
            Item indexed = GetIndexedItem(serial);
            CountFind(serial, held, indexed, "find item");

            return EntityIndexOn ? indexed : held;
        }

        private Mobile CheckedFindMobile(uint serial)
        {
            Mobile held = Mobiles.Get(serial);
            Mobile indexed = GetIndexedMobile(serial);
            CountFind(serial, held, indexed, "find mobile");

            return EntityIndexOn ? indexed : held;
        }

        // a destroyed object the table still holds is the switch's expected difference (counted as unswept, logged at Info);
        // any other difference is a mismatch, as for the Get* lookups
        private void CountFind(uint serial, Entity held, Entity indexed, string what)
        {
            EntityIndexChecks++;

            if (!ReferenceEquals(held, indexed))
            {
                if (held != null && held.IsDestroyed)
                {
                    EntityIndexUnswept++;

                    if (WillReport(EntityIndexUnswept, EntityIndexUnsweptObserver))
                    {
                        ReportEntityIndex(serial, $"{what}: tables {Describe(held)} (destroyed), index {Describe(indexed)}", EntityIndexUnswept, EntityIndexUnsweptObserver, "entity-index-unswept", false);
                    }
                }
                else
                {
                    EntityIndexMismatches++;

                    if (WillReport(EntityIndexMismatches, EntityIndexObserver))
                    {
                        ReportEntityIndex(serial, $"{what}: tables {Describe(held)}, index {Describe(indexed)}", EntityIndexMismatches, EntityIndexObserver, "entity-index-check", true);
                    }
                }
            }

            LogEntityIndexSummary();
        }

        private void CountCheck(uint serial, Entity held, Entity indexed, string what)
        {
            EntityIndexChecks++;

            if (!ReferenceEquals(held, indexed))
            {
                EntityIndexMismatches++;

                if (WillReport(EntityIndexMismatches, EntityIndexObserver))
                {
                    ReportEntityIndex(serial, $"{what}: tables {Describe(held)}, index {Describe(indexed)}", EntityIndexMismatches, EntityIndexObserver, "entity-index-check");
                }
            }

            LogEntityIndexSummary();
        }

        private void CheckHolds(Entity e, uint serial, bool held)
        {
            EntityIndexHoldsChecks++;
            object handle = Ecs.Mirror.GetHandle(serial);
            bool indexed = ReferenceEquals(handle, e);

            if (held != indexed)
            {
                if (held && !Ecs.Mirror.TryGetEntity(serial, out _))
                {
                    EntityIndexHoldsUncommitted++;
                }
                else
                {
                    EntityIndexHoldsDiffs++;

                    if (WillReport(EntityIndexHoldsDiffs, EntityIndexHoldsObserver))
                    {
                        ReportEntityIndex(serial, $"holds {Describe(e)}: tables {(held ? "yes" : "no")}, index {Describe(handle as Entity)}", EntityIndexHoldsDiffs, EntityIndexHoldsObserver, "entity-index-holds");
                    }
                }
            }

            LogEntityIndexSummary();
        }

        private void LogEntityIndexSummary()
        {
            if (Time.Ticks >= _entityIndexNextLog)
            {
                _entityIndexNextLog = Time.Ticks + 5000;
                Log.Info($"[entity-index-check] {EntityIndexChecks} checks, {EntityIndexMismatches} mismatches; unswept {EntityIndexUnswept}; holds {EntityIndexHoldsChecks} checks, {EntityIndexHoldsUncommitted} uncommitted, {EntityIndexHoldsDiffs} different; marks {EntityIndexMarkChecks} checks, {EntityIndexMarkDiffs} different; order {EntityIndexOrderChecks} checks, {EntityIndexOrderDiffs} different; items {Items.Count}, mobiles {Mobiles.Count}, records {Ecs.Mirror.Count} | world_entity_index={(EntityIndexOn ? "on" : "off")}");
            }
        }

        /// <summary>Tests: messages formatted (the first 20 per kind, or every one with an observer).</summary>
        internal long EntityIndexReportsFormatted;

        // checked before any string is built: a persistent difference must not allocate every frame once logging stops
        // (Codex audit of slice 2, P3)
        private static bool WillReport(long count, Action<string> observer) => count <= 20 || observer != null;

        private void ReportEntityIndex(uint serial, string what, long count, Action<string> observer, string tag, bool warn = true)
        {
            EntityIndexReportsFormatted++;

            // the caller tells which funnel window (or reader) saw the difference; only on a mismatch. Frames: this,
            // CountCheck/CountFind/CheckHolds, Checked*/Holds(e, serial), Get*/Find*/Holds(e), then the caller (best effort: a
            // direct Holds(e, serial) call names its caller's caller; slice 3 adds the type, since readers share method names)
            System.Reflection.MethodBase m = new System.Diagnostics.StackTrace(4, false).GetFrame(0)?.GetMethod();
            string caller = m == null ? "?" : $"{m.DeclaringType?.Name}.{m.Name}";
            string text = $"0x{serial:X8} {what}, record {(Ecs.Mirror.TryGetEntity(serial, out _) ? "yes" : "no")}, caller {caller}";

            if (count <= 20)
            {
                if (warn)
                {
                    Log.Warn($"[{tag}] {text}");
                }
                else
                {
                    // the switch's expected difference: Info, so a gate's warning scan does not count it
                    Log.Info($"[{tag}] {text}");
                }
            }

            observer?.Invoke(text);
        }

        private static string Describe(Entity e) => e == null ? "none" : $"{(e is Mobile ? "mobile" : "item")} 0x{e.Serial:X8}";
    }
}
