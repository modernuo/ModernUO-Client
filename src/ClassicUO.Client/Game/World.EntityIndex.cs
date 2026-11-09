// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    // T9 (ecs-transition-plan.md §T9, survey t9-world-index-2026-11-08): World.Get from the mirror's serial index
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

        /// <summary>T9 (<c>world_entity_index</c>, default off): <see cref="Get"/> answers from the mirror's serial index
        /// (<see cref="GetIndexed"/>), not from the Items and Mobiles tables.</summary>
        public bool EntityIndexOn => EntityIndexOverride ?? s_entityIndex;

        public bool EntityIndexCheckOn => EntityIndexCheckOverride ?? s_entityIndexCheck;

        /// <summary>CUO_ENTITY_INDEX_CHECK counts: lookups compared, mismatches found.</summary>
        public long EntityIndexChecks, EntityIndexMismatches;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal Action<string> EntityIndexObserver;

        private uint _entityIndexNextLog;

        // CUO_ENTITY_INDEX_CHECK: both lookups at every call, the switch's answer returned
        private Entity CheckedGet(uint serial)
        {
            Entity held = GetHeld(serial);
            Entity indexed = GetIndexed(serial);
            EntityIndexChecks++;

            if (!ReferenceEquals(held, indexed))
            {
                EntityIndexMismatch(serial, held, indexed);
            }

            if (Time.Ticks >= _entityIndexNextLog)
            {
                _entityIndexNextLog = Time.Ticks + 5000;
                Log.Info($"[entity-index-check] {EntityIndexChecks} checks, {EntityIndexMismatches} mismatches; items {Items.Count}, mobiles {Mobiles.Count}, records {Ecs.Mirror.Count} | world_entity_index={(EntityIndexOn ? "on" : "off")}");
            }

            return EntityIndexOn ? indexed : held;
        }

        private void EntityIndexMismatch(uint serial, Entity held, Entity indexed)
        {
            EntityIndexMismatches++;

            if (EntityIndexMismatches > 20 && EntityIndexObserver == null)
            {
                return;
            }

            // the caller tells which funnel window (or reader) saw the difference; only on a mismatch
            string caller = new System.Diagnostics.StackTrace(3, false).GetFrame(0)?.GetMethod()?.Name ?? "?";
            string text = $"0x{serial:X8}: tables {Describe(held)}, index {Describe(indexed)}, record {(Ecs.Mirror.TryGetEntity(serial, out _) ? "yes" : "no")}, caller {caller}";

            if (EntityIndexMismatches <= 20)
            {
                Log.Warn($"[entity-index-check] {text}");
            }

            EntityIndexObserver?.Invoke(text);
        }

        private static string Describe(Entity e) => e == null ? "none" : $"{(e is Mobile ? "mobile" : "item")} 0x{e.Serial:X8}";
    }
}
