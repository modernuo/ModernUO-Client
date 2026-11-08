// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using MathHelper = ClassicUO.Utility.MathHelper;

namespace ClassicUO.Game
{
    // T5c (ecs-transition-plan.md): the 50 ms range sweep and the world-map pass as a system over the ECS (RangeSweepSystem).
    // The sweep decides from Position (the last step's tile for stepping mobiles) and removes through the funnel in table
    // order; the world-map pass takes its allies from MobileInputs.Notoriety plus the party list. It serves the mobile loop
    // and the item loop. T9: the legacy rule (world_sweep_system off) and its checker (CUO_SWEEP_CHECK) are deleted.
    internal sealed partial class World
    {
        private readonly List<uint> _sweepBeyond = new List<uint>();
        private readonly List<uint> _sweepStepping = new List<uint>();
        private readonly List<Mobile> _sweepMobiles = new List<Mobile>();
        private readonly HashSet<Item> _sweepItemSet = new HashSet<Item>();

        // ---- mobiles ------------------------------------------------------------------------------------------------

        private static readonly Comparison<WMapPick> s_pickByOrdinal = (a, b) => a.Mobile.Ordinal.CompareTo(b.Mobile.Ordinal);

        private readonly struct WMapPick
        {
            public readonly Mobile Mobile;
            public readonly bool Guild;

            public WMapPick(Mobile mobile, bool guild)
            {
                Mobile = mobile;
                Guild = guild;
            }
        }

        /// <summary>The sweep's selection of mobiles, in <see cref="Mobiles"/> order, from the ECS.</summary>
        internal List<Mobile> SelectSweptMobiles()
        {
            _sweepBeyond.Clear();
            _sweepStepping.Clear();
            _sweepMobiles.Clear();

            Mobile player = Player;
            Ecs.Sweep.CollectMobiles(RangeSize.X, RangeSize.Y, ClientViewRange, player?.Serial ?? 0, _sweepBeyond, _sweepStepping);

            for (int i = 0; i < _sweepBeyond.Count; i++)
            {
                Mobile m = Mobiles.Get(_sweepBeyond[i]);

                if (m != null && !m.IsDestroyed && !ReferenceEquals(m, player))
                {
                    _sweepMobiles.Add(m);
                }
            }

            for (int i = 0; i < _sweepStepping.Count; i++)
            {
                Mobile m = Mobiles.Get(_sweepStepping[i]);

                if (m == null || m.IsDestroyed || ReferenceEquals(m, player))
                {
                    continue;
                }

                // GameObject.Distance: the last queued step's tile while the mobile has steps
                int x = m.X, y = m.Y;

                if (m.Steps.Count != 0)
                {
                    ref readonly Mobile.Step step = ref m.Steps.Back();
                    x = step.X;
                    y = step.Y;
                }

                if (ClassicUO.Ecs.RangeSweepRules.Beyond(x, y, RangeSize.X, RangeSize.Y, ClientViewRange))
                {
                    _sweepMobiles.Add(m);
                }
            }

            SortInMobilesOrder(_sweepMobiles);

            return _sweepMobiles;
        }

        // the mobile loop's range pass: the system's selection, removed through the funnel in table order
        internal void SweepMobiles()
        {
            List<Mobile> selected = SelectSweptMobiles();

            for (int i = 0; i < selected.Count; i++)
            {
                Writer.SweepMobile(selected[i]);
            }
        }

        // ---- items --------------------------------------------------------------------------------------------------

        // called before the item loop's range removals: the system's selection of ground items beyond the range
        internal void PrepareItemSweep()
        {
            _sweepBeyond.Clear();
            _sweepItemSet.Clear();
            Ecs.Sweep.CollectItems(RangeSize.X, RangeSize.Y, ClientViewRange, _sweepBeyond);

            for (int i = 0; i < _sweepBeyond.Count; i++)
            {
                if (Items.TryGetValue(_sweepBeyond[i], out Item item))
                {
                    _sweepItemSet.Add(item);
                }
            }
        }

        // a multi that was destroyed but is still in the Items table has no ECS entity, so the system cannot select it; the
        // legacy loop still runs its range and house check (HouseManager.TryToRemove), and so does the system path
        private bool DestroyedHeldMultiBeyond(Item item)
        {
            return item.IsDestroyed && item.IsMulti && item.OnGround && item.Distance > ClientViewRange;
        }

        // the item loop's range test: the system's selection
        internal bool ItemBeyond(Item item)
        {
            return _sweepItemSet.Contains(item) || DestroyedHeldMultiBeyond(item);
        }

        // the item loop's removal of an out-of-range item (a multi only once its house is out of range too)
        internal void SweepItemBeyond(Item item)
        {
            if (!item.IsMulti || HouseManager.TryToRemove(item, ClientViewRange))
            {
                Writer.SweepItem(item); // T1 (funnel part 5): the sweep's removal
            }
        }

        internal void FinishItemSweep()
        {
            _sweepItemSet.Clear();
        }

        // ---- the world-map pass -------------------------------------------------------------------------------------

        private readonly List<WMapPick> _picks = new List<WMapPick>();
        private readonly HashSet<uint> _allySet = new HashSet<uint>();
        private readonly List<uint> _allies = new List<uint>();

        private void SelectWorldMap()
        {
            _picks.Clear();
            _allySet.Clear();
            _allies.Clear();
            Ecs.Sweep.CollectAllies((byte)NotorietyFlag.Ally, _allies);

            for (int i = 0; i < _allies.Count; i++)
            {
                Mobile mob = Mobiles.Get(_allies[i]);

                if (mob != null && !mob.IsDestroyed && _allySet.Add(mob.Serial))
                {
                    _picks.Add(new WMapPick(mob, true));
                }
            }

            if (Party.Leader != 0)
            {
                PartyMember[] members = Party.Members;

                for (int i = 0; i < members.Length; i++)
                {
                    PartyMember member = members[i];

                    if (member == null || _allySet.Contains(member.Serial))
                    {
                        continue;
                    }

                    Mobile mob = Mobiles.Get(member.Serial);

                    if (mob != null && !mob.IsDestroyed)
                    {
                        _picks.Add(new WMapPick(mob, false));
                    }
                }
            }

            _picks.Sort(s_pickByOrdinal);
        }

        private static string Call(Mobile mob, int map, bool guild)
        {
            return $"{mob.Serial:X8} {mob.X},{mob.Y} hp {MathHelper.PercetangeOf(mob.Hits, mob.HitsMax)} map {map} guild {guild} name {mob.Name}";
        }

        /// <summary>Tests: sees each world-map call the pass makes (as text).</summary>
        internal Action<string> MapCallObserver;

        private void MapCall(Mobile mob, bool guild)
        {
            MapCallObserver?.Invoke(Call(mob, MapIndex, guild));
            WMapManager.AddOrUpdate(mob.Serial, mob.X, mob.Y, MathHelper.PercetangeOf(mob.Hits, mob.HitsMax), MapIndex, guild, mob.Name);
        }

        // the mobile loop's world-map pass: the system's picks (allies from MobileInputs, then the party list) in table order
        internal void WorldMapPass()
        {
            SelectWorldMap();

            for (int i = 0; i < _picks.Count; i++)
            {
                MapCall(_picks[i].Mobile, _picks[i].Guild);
            }
        }
    }
}
