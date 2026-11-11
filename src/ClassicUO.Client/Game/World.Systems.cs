// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Ecs;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Friflo.Engine.ECS;
using EcsEntity = Friflo.Engine.ECS.Entity;

namespace ClassicUO.Game
{
    /// <summary>T5d: the stages of <c>World.Update</c>, in the order they run. <see cref="EffectsText"/> is the FrameStats phase
    /// around <see cref="Effects"/>, <see cref="WorldText"/> and <see cref="WorldMapPrune"/> and is not reported itself.</summary>
    internal enum WorldStage : byte
    {
        ChunkLoading,
        ObjectToRemove,

        /// <summary>Steps (T6, legacy), the mobile clock and advance (T5b), the range sweep and the world-map pass (T5c).</summary>
        Mobiles,
        ActiveItems,
        EffectsText,
        Effects,
        WorldText,
        WorldMapPrune,
        EcsBookkeeping
    }

    // T5d (ecs-transition-plan.md): the stage runner of World.Update (a refactor: same behaviour with every switch) and the
    // active item system. The system keeps the update list's rules (append when queued, swap-remove when the item no longer
    // needs a frame or left Items) and adds the ItemActive tag on the item's mirror entity as the ECS-side membership.
    // CUO_ACTIVE_ITEMS_CHECK=1 compares tags and list every frame. T9 deleted world_active_items_system (the list without tags).
    internal sealed partial class World
    {
        /// <summary>Tests: sees each stage as it begins (null in the client).</summary>
        internal Action<WorldStage> StageObserver;

        /// <summary>Tests: the checker for this World (null: <c>CUO_ACTIVE_ITEMS_CHECK=1</c>).</summary>
        internal bool? ActiveItemsCheckOverride;

#if CUO_DEVTOOLS
        // CLEANUP(release): the checker
        private static readonly bool s_activeItemsCheck = Environment.GetEnvironmentVariable("CUO_ACTIVE_ITEMS_CHECK") == "1";
#else
        private const bool s_activeItemsCheck = false;
#endif

        // T5b-items: a due corpse in the active item list advances in the ECS (T9 deleted world_item_anim_system's off path)
        private readonly ItemAnimationAdvanceSystem _itemAnimAdvance = new ItemAnimationAdvanceSystem();

        /// <summary>Tests: sees each item the active item stage visits, in order, before its update (null in the client).</summary>
        internal Action<Item> ActiveVisitObserver;

        /// <summary>T5b-items: the per-item step of the active item stage: the ECS advance for a corpse, else <c>Item.Update</c>.</summary>
        private void UpdateActiveItem(Item item)
        {
            ActiveVisitObserver?.Invoke(item);

            if (_itemAnimAdvance.TryRun(this, item))
            {
                return;
            }

            item.Update();
        }

        /// <summary>Tests: the T8 checker for this World (null: <c>CUO_EFFECT_CHECK=1</c>).</summary>
        internal bool? EffectsCheckOverride;

        public bool EffectsCheckOn => EffectsCheckOverride ?? EffectSystem.GlobalCheck;

        public bool ActiveItemsCheckOn => ActiveItemsCheckOverride ?? s_activeItemsCheck;

        /// <summary>Checker counts: frames compared, mismatches found.</summary>
        public long ActiveItemsChecks, ActiveItemsMismatches;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal Action<string> ActiveItemsObserver;

        private ArchetypeQuery _activeTagQuery;
        private readonly HashSet<Item> _activeSeen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        private uint _activeCheckNextLog;

        /// <summary>
        /// Called at the start of the active item stage: the list against the tags. Every listed item with a mirror entity
        /// carries the tag and every tagged entity is a listed item; each listed item has its flag and appears once.
        /// </summary>
        private void CheckActiveItemsSystem()
        {
            ActiveItemsChecks++;
            _activeSeen.Clear();
            int withEntity = 0;

            int count = ActiveItemCount;

            for (int i = 0; i < count; i++)
            {
                Item item = ActiveItemAt(i);

                if (!_activeSeen.Add(item))
                {
                    ActiveMismatch($"item 0x{item.Serial:X8} listed twice");
                }

                if (!IsItemListed(item))
                {
                    ActiveMismatch($"item 0x{item.Serial:X8} listed without its flag");
                }

                if (item.IsDestroyed)
                {
                    continue; // swept at the end of this stage's loop
                }

                if (item.TryScalarEntity(out Friflo.Engine.ECS.Entity e))
                {
                    withEntity++;

                    if (!e.Tags.Has<ItemActive>())
                    {
                        ActiveMismatch($"item 0x{item.Serial:X8} listed, tag missing");
                    }
                }
            }

            // the set is scratch: emptied now so the items it saw, destroyed later, are not kept reachable until the next check
            _activeSeen.Clear();

            _activeTagQuery ??= Ecs.Mirror.Store.Query().AllTags(Tags.Get<ItemActive>());
            int tagged = _activeTagQuery.Count;

            if (tagged != withEntity)
            {
                ActiveMismatch($"{tagged} tagged entities, expected {withEntity} (list {count})");
            }

            if (Time.Ticks >= _activeCheckNextLog)
            {
                _activeCheckNextLog = Time.Ticks + 5000;
                Log.Info($"[active-items-check] {ActiveItemsChecks} checks, {ActiveItemsMismatches} mismatches; list {count}, tagged {tagged}");
            }
        }

        private void ActiveMismatch(string text)
        {
            ActiveItemsMismatches++;

            if (ActiveItemsMismatches <= 10)
            {
                Log.Warn($"[active-items-check] {text}");
            }

            ActiveItemsObserver?.Invoke(text);
        }

        /// <summary>The active item stage: the visiting order is rebuilt from the <see cref="ActiveItemEntry"/> slots (T9 deleted
        /// <c>ecs_active_items_source</c>' off path with CUO_ACTIVE_ITEMS_SOURCE_CHECK, which compared the two lists). Broken
        /// slots are reported like CUO_ACTIVE_ITEMS_CHECK's mismatches, checker or not.</summary>
        private void RebuildActiveOrder()
        {
            if (!ActiveStore.Rebuild(out string error))
            {
                ActiveMismatch($"entry slots broken: {error}");
            }
        }
    }
}
