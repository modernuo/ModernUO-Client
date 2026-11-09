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

        /// <summary>Tests: the endpoint switch for this World (null: <c>ecs_effect_endpoints</c>, read once at startup).</summary>
        internal bool? EffectEndpointsOverride;

        /// <summary>Tests: the endpoint checker for this World (null: <c>CUO_EFFECT_ENDPOINT_CHECK=1</c>).</summary>
        internal bool? EffectEndpointsCheckOverride;

        /// <summary>T8 ownership (<c>ecs_effect_endpoints</c>, default off): effects reference their source and target by mirror
        /// record and serial, and read their positions from the ECS. Decided per effect when it registers.</summary>
        public bool EffectEndpointsOn => EffectEndpointsOverride ?? EffectSystem.GlobalEndpoints;

        public bool EffectEndpointsCheckOn => EffectEndpointsCheckOverride ?? EffectSystem.GlobalEndpointCheck;

        /// <summary>Tests: the motion switch for this World (null: <c>ecs_effect_motion</c>, read once at startup).</summary>
        internal bool? EffectMotionOverride;

        /// <summary>Tests: the motion checker for this World (null: <c>CUO_EFFECT_MOTION_CHECK=1</c>).</summary>
        internal bool? EffectMotionCheckOverride;

        /// <summary>T8 ownership phase 2 (<c>ecs_effect_motion</c>, default off; needs <c>ecs_effect_endpoints</c>): the kinds'
        /// motion is <see cref="EffectSystem"/>'s motion step over the effect's state.
        /// Decided per effect when it registers.</summary>
        public bool EffectMotionOn => EffectEndpointsOn && (EffectMotionOverride ?? EffectSystem.GlobalMotion);

        public bool EffectMotionCheckOn => EffectMotionCheckOverride ?? EffectSystem.GlobalMotionCheck;

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

        // T9 (ecs_active_items_source) ----------------------------------------------------------------------------------------
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ECS_ACTIVE_ITEMS_SOURCE=1|0) and the checker
        private static readonly bool s_activeItemsSource = Environment.GetEnvironmentVariable("CUO_ECS_ACTIVE_ITEMS_SOURCE") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.EcsActiveItemsSource
        };

        private static readonly bool s_activeItemsSourceCheck = Environment.GetEnvironmentVariable("CUO_ACTIVE_ITEMS_SOURCE_CHECK") == "1";
#else
        private static readonly bool s_activeItemsSource = Configuration.Settings.GlobalSettings.EcsActiveItemsSource;
        private const bool s_activeItemsSourceCheck = false;
#endif

        /// <summary>Tests: the switch for this World (null: the setting, read once at startup). Set before the first item is listed.</summary>
        internal bool? ActiveItemsSourceOverride;

        /// <summary>Tests: the checker for this World (null: <c>CUO_ACTIVE_ITEMS_SOURCE_CHECK=1</c>). Set before the first item is listed.</summary>
        internal bool? ActiveItemsSourceCheckOverride;

        /// <summary>T9 (<c>ecs_active_items_source</c>, default off): the update list's membership and order are the
        /// <see cref="ActiveItemEntry"/> entities (<see cref="ActiveItemStore"/>), not <see cref="ActiveItemList"/>.</summary>
        public bool ActiveItemsSourceOn => ActiveItemsSourceOverride ?? s_activeItemsSource;

        public bool ActiveItemsSourceCheckOn => ActiveItemsSourceCheckOverride ?? s_activeItemsSourceCheck;

        /// <summary>Source checker counts: comparisons, mismatches found.</summary>
        public long ActiveSourceChecks, ActiveSourceMismatches;

        /// <summary>Tests: sees each source checker mismatch report.</summary>
        internal Action<string> ActiveSourceObserver;

        private uint _activeSourceNextLog;

        /// <summary>The active item stage: the ECS's visiting order is rebuilt from the entries' slots (the store is the source
        /// with the switch on, the checker's shadow with it off).</summary>
        private void RebuildActiveOrder()
        {
            if (!ActiveStore.Rebuild(ActiveItemsSourceOn, out string error))
            {
                ActiveSourceMismatch($"entry slots broken: {error}");
            }
        }

        // CUO_ACTIVE_ITEMS_SOURCE_CHECK: the other path follows every add and removal of the switch's path with its own rules
        private void ShadowAdd(Item item)
        {
            if (ActiveItemsSourceOn)
            {
                _activeItems.Add(item, false);
            }
            else
            {
                ActiveStore.Add(item, false);
            }
        }

        private Item ShadowAt(int index) => ActiveItemsSourceOn ? _activeItems[index] : ActiveStore[index];

        // the visit removed `item` from position `index`: the shadow must hold it there too (same order), and removes it there
        private void ShadowRemoveAt(int index, Item item)
        {
            bool source = ActiveItemsSourceOn;
            int count = source ? _activeItems.Count : ActiveStore.Count;
            int at = index;

            if (index >= count || !ReferenceEquals(ShadowAt(index), item))
            {
                ActiveSourceMismatch($"visit removed 0x{item.Serial:X8} at {index}; the {(source ? "list" : "ECS")} holds {(index < count ? $"0x{ShadowAt(index).Serial:X8}" : "nothing")} there");
                at = -1;

                for (int i = 0; i < count; i++)
                {
                    if (ReferenceEquals(ShadowAt(i), item))
                    {
                        at = i;

                        break;
                    }
                }

                if (at < 0)
                {
                    return;
                }
            }

            if (source)
            {
                _activeItems.SwapRemoveAt(at, false);
            }
            else
            {
                ActiveStore.SwapRemoveAt(at, false);
            }
        }

        /// <summary>
        /// CUO_ACTIVE_ITEMS_SOURCE_CHECK=1, before and after the active item visit: the list and the ECS (its order rebuilt from
        /// the entry slots before the visit) hold the same items in the same order; every entry names its item, serial and
        /// slot, the entity count equals the order's length, and each listed item's flag and entry handle are set.
        /// </summary>
        private void CheckActiveItemsSource(string when)
        {
            ActiveSourceChecks++;
            ActiveItemStore store = ActiveStore;
            int listCount = _activeItems.Count, ecsCount = store.Count;

            if (listCount != ecsCount)
            {
                ActiveSourceMismatch($"{when}: list holds {listCount} items, ECS {ecsCount}");
            }

            if (store.EntityCount != ecsCount)
            {
                ActiveSourceMismatch($"{when}: {store.EntityCount} entry entities, ECS order {ecsCount}");
            }

            int n = Math.Min(listCount, ecsCount);

            for (int i = 0; i < n; i++)
            {
                Item a = _activeItems[i], b = store[i];

                if (!ReferenceEquals(a, b))
                {
                    ActiveSourceMismatch($"{when}: position {i}: list 0x{a.Serial:X8}, ECS 0x{b.Serial:X8}");

                    break;
                }
            }

            for (int i = 0; i < ecsCount; i++)
            {
                Item item = store[i];
                EcsEntity e = store.EntryOf(item);

                if (e.IsNull || !e.HasComponent<ActiveItemEntry>())
                {
                    ActiveSourceMismatch($"{when}: 0x{item.Serial:X8} in the ECS order without its entry");

                    continue;
                }

                ref ActiveItemEntry entry = ref e.GetComponent<ActiveItemEntry>();

                if (!ReferenceEquals(store.ItemOf(entry.Handle), item) || entry.Slot != i || entry.Serial != item.Serial)
                {
                    ActiveSourceMismatch($"{when}: position {i} 0x{item.Serial:X8}: entry names 0x{entry.Serial:X8} slot {entry.Slot}");
                }
            }

            for (int i = 0; i < listCount; i++)
            {
                if (!_activeItems[i].InUpdateList)
                {
                    ActiveSourceMismatch($"{when}: 0x{_activeItems[i].Serial:X8} in the list without its flag");
                }
            }

            if (Time.Ticks >= _activeSourceNextLog)
            {
                _activeSourceNextLog = Time.Ticks + 5000;
                Log.Info($"[active-items-source-check] {ActiveSourceChecks} checks, {ActiveSourceMismatches} mismatches; list {listCount}, ECS {ecsCount} | ecs_active_items_source={(ActiveItemsSourceOn ? "on" : "off")}");
            }
        }

        private void ActiveSourceMismatch(string text)
        {
            ActiveSourceMismatches++;

            if (ActiveSourceMismatches <= 10)
            {
                Log.Warn($"[active-items-source-check] {text}");
            }

            ActiveSourceObserver?.Invoke(text);
        }
    }
}
