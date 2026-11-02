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
    // active item system (world_active_items_system, default on). The system keeps the update list's rules (append when queued,
    // swap-remove when the item no longer needs a frame or left Items) and adds the ItemActive tag on the item's mirror
    // entity as the ECS-side membership. CUO_ACTIVE_ITEMS_CHECK=1 compares tags and list every frame.
    internal sealed partial class World
    {
        /// <summary>Tests: sees each stage as it begins (null in the client).</summary>
        internal Action<WorldStage> StageObserver;

        /// <summary>Tests: the switch for this World (null: the setting, read once at startup).</summary>
        internal bool? ActiveItemsSystemOverride;

        /// <summary>Tests: the checker for this World (null: <c>CUO_ACTIVE_ITEMS_CHECK=1</c>).</summary>
        internal bool? ActiveItemsCheckOverride;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_WORLD_ACTIVE_ITEMS_SYSTEM=1|0) and the checker
        private static readonly bool s_activeItemsSystem = Environment.GetEnvironmentVariable("CUO_WORLD_ACTIVE_ITEMS_SYSTEM") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.WorldActiveItemsSystem
        };

        private static readonly bool s_activeItemsCheck = Environment.GetEnvironmentVariable("CUO_ACTIVE_ITEMS_CHECK") == "1";
#else
        private static readonly bool s_activeItemsSystem = Configuration.Settings.GlobalSettings.WorldActiveItemsSystem;
        private const bool s_activeItemsCheck = false;
#endif

        /// <summary>T5d (<c>world_active_items_system</c>, default on): the update list's membership is the <see cref="ItemActive"/> tag too.</summary>
        public bool ActiveItemsSystemOn => ActiveItemsSystemOverride ?? s_activeItemsSystem;

        // T5b-items (world_item_anim_system) ---------------------------------------------------------------------------------
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_WORLD_ITEM_ANIM_SYSTEM=1|0)
        private static readonly bool s_itemAnimSystem = Environment.GetEnvironmentVariable("CUO_WORLD_ITEM_ANIM_SYSTEM") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.WorldItemAnimSystem
        };
#else
        private static readonly bool s_itemAnimSystem = Configuration.Settings.GlobalSettings.WorldItemAnimSystem;
#endif

        /// <summary>Tests: the switch for this World (null: the setting, read once at startup).</summary>
        internal bool? ItemAnimSystemOverride;

        /// <summary>T5b-items: due corpses in the active item list advance in the ECS (<see cref="ItemAnimationAdvanceSystem"/>).</summary>
        internal bool ItemAnimSystem => ItemAnimSystemOverride ?? s_itemAnimSystem;

        private readonly ItemAnimationAdvanceSystem _itemAnimAdvance = new ItemAnimationAdvanceSystem();

        /// <summary>T5b-items: the per-item step of the active item stage: the ECS advance for a corpse, else <c>Item.Update</c>.</summary>
        private void UpdateActiveItem(Item item, bool itemAnim)
        {
            if (itemAnim && _itemAnimAdvance.TryRun(this, item))
            {
                return;
            }

            item.Update();
        }

        /// <summary>Tests: the T8 switch for this World (null: <c>ecs_effects</c>, read once at startup).</summary>
        internal bool? EffectsSystemOverride;

        /// <summary>Tests: the T8 checker for this World (null: <c>CUO_EFFECT_CHECK=1</c>).</summary>
        internal bool? EffectsCheckOverride;

        /// <summary>T8 (<c>ecs_effects</c>, default on): effects are ECS entities advanced by <see cref="EffectSystem"/>.</summary>
        public bool EffectsSystemOn => EffectsSystemOverride ?? EffectSystem.GlobalOn;

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
        /// Called at the start of the active item stage: the list against the tags. With the system on every listed item with a
        /// mirror entity carries the tag and every tagged entity is a listed item; with it off no entity carries it. In both
        /// cases each listed item has its flag, appears once and is not destroyed.
        /// </summary>
        private void CheckActiveItemsSystem()
        {
            ActiveItemsChecks++;
            bool on = ActiveItemsSystemOn;
            _activeSeen.Clear();
            int withEntity = 0;

            for (int i = 0; i < _activeItems.Count; i++)
            {
                Item item = _activeItems[i];

                if (!_activeSeen.Add(item))
                {
                    ActiveMismatch($"item 0x{item.Serial:X8} listed twice");
                }

                if (!item.InUpdateList)
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

                    if (on != e.Tags.Has<ItemActive>())
                    {
                        ActiveMismatch($"item 0x{item.Serial:X8} listed, tag {(on ? "missing" : "present")}");
                    }
                }
            }

            _activeTagQuery ??= Ecs.Mirror.Store.Query().AllTags(Tags.Get<ItemActive>());
            int tagged = _activeTagQuery.Count;

            if (tagged != (on ? withEntity : 0))
            {
                ActiveMismatch($"{tagged} tagged entities, expected {(on ? withEntity : 0)} (list {_activeItems.Count})");
            }

            if (Time.Ticks >= _activeCheckNextLog)
            {
                _activeCheckNextLog = Time.Ticks + 5000;
                Log.Info($"[active-items-check] {ActiveItemsChecks} checks, {ActiveItemsMismatches} mismatches; list {_activeItems.Count}, tagged {tagged} | world_active_items_system={(on ? "on" : "off")}");
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
    }
}
