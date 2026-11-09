// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    // T9 (ecs-transition-plan.md §T9, survey t9-world-index-2026-11-08): World.Get, GetItem and GetMobile from the mirror's
    // serial index; Holds (the identity guards) stays on the tables until the deferred sweep goes (slice 3)
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

        /// <summary>T9 (<c>world_entity_index</c>, default off): <see cref="Get"/>, <see cref="GetItem"/> and
        /// <see cref="GetMobile"/> answer from the mirror's serial index, not from the Items and Mobiles tables.</summary>
        public bool EntityIndexOn => EntityIndexOverride ?? s_entityIndex;

        public bool EntityIndexCheckOn => EntityIndexCheckOverride ?? s_entityIndexCheck;

        /// <summary>CUO_ENTITY_INDEX_CHECK counts: lookups compared, mismatches found.</summary>
        public long EntityIndexChecks, EntityIndexMismatches;

        /// <summary>CUO_ENTITY_INDEX_CHECK counts for <see cref="Holds(Entity)"/> (still the tables) against the index, for a
        /// live object: guards compared, guards where the tables hold an object the mirror has no record for yet (an object
        /// created inside a funnel operation, before its commit), and other differences (logged).</summary>
        public long EntityIndexHoldsChecks, EntityIndexHoldsUncommitted, EntityIndexHoldsDiffs;

        /// <summary>Tests: sees each mismatch report (lookups), and each Holds difference.</summary>
        internal Action<string> EntityIndexObserver, EntityIndexHoldsObserver;

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
        /// guard of the funnel and the ECS writes. Destroyed objects not yet swept are still held. Stays on the tables until
        /// the deferred sweep goes (T9 slice 3); CUO_ENTITY_INDEX_CHECK compares it with the index for live objects.
        /// </summary>
        public bool Holds(Entity e) => e != null && Holds(e, e.Serial);

        /// <summary><see cref="Holds(Entity)"/> under <paramref name="serial"/> (a serial the object had or is about to have).</summary>
        public bool Holds(Entity e, uint serial)
        {
            bool held = e is Mobile ? ReferenceEquals(Mobiles.Get(serial), e) : e != null && ReferenceEquals(Items.Get(serial), e);

            if (EntityIndexCheckOn && e != null && !e.IsDestroyed)
            {
                CheckHolds(e, serial, held);
            }

            return held;
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

        private void CountCheck(uint serial, Entity held, Entity indexed, string what)
        {
            EntityIndexChecks++;

            if (!ReferenceEquals(held, indexed))
            {
                EntityIndexMismatches++;
                ReportEntityIndex(serial, $"{what}: tables {Describe(held)}, index {Describe(indexed)}", EntityIndexMismatches, EntityIndexObserver, "entity-index-check");
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
                    ReportEntityIndex(serial, $"holds {Describe(e)}: tables {(held ? "yes" : "no")}, index {Describe(handle as Entity)}", EntityIndexHoldsDiffs, EntityIndexHoldsObserver, "entity-index-holds");
                }
            }

            LogEntityIndexSummary();
        }

        private void LogEntityIndexSummary()
        {
            if (Time.Ticks >= _entityIndexNextLog)
            {
                _entityIndexNextLog = Time.Ticks + 5000;
                Log.Info($"[entity-index-check] {EntityIndexChecks} checks, {EntityIndexMismatches} mismatches; holds {EntityIndexHoldsChecks} checks, {EntityIndexHoldsUncommitted} uncommitted, {EntityIndexHoldsDiffs} different; items {Items.Count}, mobiles {Mobiles.Count}, records {Ecs.Mirror.Count} | world_entity_index={(EntityIndexOn ? "on" : "off")}");
            }
        }

        private void ReportEntityIndex(uint serial, string what, long count, Action<string> observer, string tag)
        {
            if (count > 20 && observer == null)
            {
                return;
            }

            // the caller tells which funnel window (or reader) saw the difference; only on a mismatch. Frames: this,
            // CountCheck/CheckHolds, Checked*/Holds(e, serial), Get*/Holds(e), then the caller (best effort: a direct
            // Holds(e, serial) call names its caller's caller)
            string caller = new System.Diagnostics.StackTrace(4, false).GetFrame(0)?.GetMethod()?.Name ?? "?";
            string text = $"0x{serial:X8} {what}, record {(Ecs.Mirror.TryGetEntity(serial, out _) ? "yes" : "no")}, caller {caller}";

            if (count <= 20)
            {
                Log.Warn($"[{tag}] {text}");
            }

            observer?.Invoke(text);
        }

        private static string Describe(Entity e) => e == null ? "none" : $"{(e is Mobile ? "mobile" : "item")} 0x{e.Serial:X8}";
    }
}
