// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;
using MathHelper = ClassicUO.Utility.MathHelper;

namespace ClassicUO.Game
{
    // T5c (ecs-transition-plan.md): the 50 ms range sweep and the world-map pass as a system over the ECS (RangeSweepSystem).
    // The sweep decides from Position (the last step's tile for stepping mobiles) and removes through the funnel in table
    // order; the world-map pass takes its allies from MobileInputs.Notoriety plus the party list. world_sweep_system picks the
    // path; CUO_SWEEP_CHECK=1 (CUO_DEVTOOLS builds) compares the system's selection and the removals and map calls actually
    // made with the legacy rule over every object in the same frame. The system serves the E4 mobile loop (the legacy mobile
    // loop interleaves the sweep with Mobile.Update and keeps its own code) and both item loops.
    internal sealed partial class World
    {
        /// <summary>Tests: the switch for this World (null: the setting, read once at startup).</summary>
        internal bool? SweepSystemOverride;

        /// <summary>Tests: the checker for this World (null: <c>CUO_SWEEP_CHECK=1</c>).</summary>
        internal bool? SweepCheckOverride;

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_WORLD_SWEEP_SYSTEM=1|0)
        private static readonly bool s_sweepSystem = Environment.GetEnvironmentVariable("CUO_WORLD_SWEEP_SYSTEM") switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings.WorldSweepSystem
        };

        private static readonly bool s_sweepCheck = Environment.GetEnvironmentVariable("CUO_SWEEP_CHECK") == "1";
#else
        private static readonly bool s_sweepSystem = Configuration.Settings.GlobalSettings.WorldSweepSystem;
        private const bool s_sweepCheck = false;
#endif

        /// <summary>T5c (<c>world_sweep_system</c>, default off): the range sweep and the world-map pass run as a system.</summary>
        public bool SweepSystemOn => SweepSystemOverride ?? s_sweepSystem;

        public bool SweepCheckOn => SweepCheckOverride ?? s_sweepCheck;

        /// <summary>Checker counts: comparisons made, mismatches found, removals and map calls seen.</summary>
        public long SweepChecks, SweepMismatches, SweepRemovedMobiles, SweepRemovedItems, SweepMapCalls;

        /// <summary>Tests: sees each mismatch report.</summary>
        internal Action<string> SweepObserver;

        private readonly List<uint> _sweepBeyond = new List<uint>();
        private readonly List<uint> _sweepStepping = new List<uint>();
        private readonly List<Mobile> _sweepMobiles = new List<Mobile>();
        private readonly HashSet<Item> _sweepItemSet = new HashSet<Item>();
        private bool _sweepItemsBySystem;

        // checker
        private readonly List<uint> _expected = new List<uint>();
        private readonly List<uint> _actual = new List<uint>();
        private readonly List<uint> _selected = new List<uint>();
        private readonly List<uint> _checkRemovals = new List<uint>();
        private bool _recordItems;

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

        // the E4 loop's range pass
        internal void SweepMobiles()
        {
            bool system = SweepSystemOn;
            bool check = SweepCheckOn;

            if (!system && !check)
            {
                foreach (Mobile mob in Mobiles.Values)
                {
                    if (mob.Distance > ClientViewRange)
                    {
                        Writer.SweepMobile(mob); // T1 (funnel part 5): the sweep's removal
                    }
                }

                return;
            }

            List<Mobile> selected = SelectSweptMobiles();

            _expected.Clear();
            _selected.Clear();
            _actual.Clear();

            if (check)
            {
                foreach (Mobile mob in Mobiles.Values)
                {
                    if (!mob.IsDestroyed && mob.Distance > ClientViewRange)
                    {
                        _expected.Add(mob.Serial);
                    }
                }

                for (int i = 0; i < selected.Count; i++)
                {
                    _selected.Add(selected[i].Serial);
                }

                CompareSweep("mobiles selected", _expected, _selected);
            }

            if (system)
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    Mobile mob = selected[i];

                    if (check && !mob.IsDestroyed)
                    {
                        _actual.Add(mob.Serial);
                    }

                    Writer.SweepMobile(mob);
                }
            }
            else
            {
                foreach (Mobile mob in Mobiles.Values)
                {
                    if (mob.Distance > ClientViewRange)
                    {
                        if (!mob.IsDestroyed)
                        {
                            _actual.Add(mob.Serial);
                        }

                        Writer.SweepMobile(mob);
                    }
                }
            }

            if (check)
            {
                SweepRemovedMobiles += _actual.Count;
                CompareSweep("mobiles removed", _expected, _actual);
            }
        }

        // ---- items --------------------------------------------------------------------------------------------------

        // called before an item loop's range removals: selects from the ECS and, with the checker, compares it with the legacy rule
        internal void PrepareItemSweep()
        {
            bool system = SweepSystemOn;
            bool check = SweepCheckOn;
            _sweepItemsBySystem = system;
            _recordItems = check;

            if (!system && !check)
            {
                return;
            }

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

            _actual.Clear();

            if (!check)
            {
                return;
            }

            // both lists in table order, destroyed items left out; the expected removals also go through the house rule
            _expected.Clear();
            _selected.Clear();
            _checkRemovals.Clear();

            foreach (Item item in Items.Values)
            {
                if (item.IsDestroyed)
                {
                    continue;
                }

                if (item.OnGround && item.Distance > ClientViewRange)
                {
                    _expected.Add(item.Serial);

                    if (!item.IsMulti || !HouseManager.IsHouseInRange(item.Serial, ClientViewRange))
                    {
                        _checkRemovals.Add(item.Serial);
                    }
                }

                if (_sweepItemSet.Contains(item))
                {
                    _selected.Add(item.Serial);
                }
            }

            CompareSweep("items selected", _expected, _selected);
        }

        // the loops' range test: the system's selection, else the legacy rule
        internal bool ItemBeyond(Item item)
        {
            return _sweepItemsBySystem ? _sweepItemSet.Contains(item) : item.OnGround && item.Distance > ClientViewRange;
        }

        // the loops' removal of an out-of-range item (a multi only once its house is out of range too)
        internal void SweepItemBeyond(Item item)
        {
            if (item.IsMulti)
            {
                if (HouseManager.TryToRemove(item, ClientViewRange))
                {
                    RecordItemSweep(item);
                    Writer.SweepItem(item); // T1 (funnel part 5): the sweep's removal
                }
            }
            else
            {
                RecordItemSweep(item);
                Writer.SweepItem(item); // T1 (funnel part 5): the sweep's removal
            }
        }

        private void RecordItemSweep(Item item)
        {
            if (_recordItems && !item.IsDestroyed)
            {
                _actual.Add(item.Serial);
            }
        }

        internal void FinishItemSweep()
        {
            if (_recordItems)
            {
                SweepRemovedItems += _actual.Count;
                CompareSweep("items removed", _checkRemovals, _actual);
                _recordItems = false;
            }

            _sweepItemsBySystem = false;
            _sweepItemSet.Clear();
        }

        // ---- the world-map pass -------------------------------------------------------------------------------------

        private readonly List<WMapPick> _picks = new List<WMapPick>();
        private readonly HashSet<uint> _allySet = new HashSet<uint>();
        private readonly List<uint> _allies = new List<uint>();
        private readonly List<string> _legacyCalls = new List<string>();
        private readonly List<string> _systemCalls = new List<string>();

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

        /// <summary>Tests: sees each world-map call the pass makes (in the checker's text form).</summary>
        internal Action<string> MapCallObserver;

        private void MapCall(Mobile mob, bool guild)
        {
            MapCallObserver?.Invoke(Call(mob, MapIndex, guild));
            WMapManager.AddOrUpdate(mob.Serial, mob.X, mob.Y, MathHelper.PercetangeOf(mob.Hits, mob.HitsMax), MapIndex, guild, mob.Name);
        }

        // the E4 loop's world-map pass: the legacy rule over every mobile, or the system's picks. Without issue (the map is not
        // accepting updates) only the checker runs: it compares what the two rules would call
        internal void WorldMapPass(bool issue = true)
        {
            // the allies come from MobileInputs, which are pushed only with mobile_push_inputs
            bool system = SweepSystemOn && Configuration.Settings.GlobalSettings.MobilePushInputs;
            bool check = SweepCheckOn;

            if (system || check)
            {
                SelectWorldMap();
            }

            if (check)
            {
                _legacyCalls.Clear();
                _systemCalls.Clear();

                foreach (Mobile mob in Mobiles.Values)
                {
                    if (mob.IsDestroyed)
                    {
                        continue;
                    }

                    if (mob.NotorietyFlag == NotorietyFlag.Ally)
                    {
                        _legacyCalls.Add(Call(mob, MapIndex, true));
                    }
                    else if (Party.Leader != 0 && Party.Contains(mob))
                    {
                        _legacyCalls.Add(Call(mob, MapIndex, false));
                    }
                }

                for (int i = 0; i < _picks.Count; i++)
                {
                    _systemCalls.Add(Call(_picks[i].Mobile, MapIndex, _picks[i].Guild));
                }

                SweepMapCalls += _systemCalls.Count;
                CompareLines("world map calls", _legacyCalls, _systemCalls);
            }

            if (!issue)
            {
                return;
            }

            if (system)
            {
                for (int i = 0; i < _picks.Count; i++)
                {
                    MapCall(_picks[i].Mobile, _picks[i].Guild);
                }

                return;
            }

            foreach (Mobile mob in Mobiles.Values)
            {
                if (mob.IsDestroyed)
                {
                    continue;
                }

                if (mob.NotorietyFlag == NotorietyFlag.Ally)
                {
                    MapCall(mob, true);
                }
                else if (Party.Leader != 0 && Party.Contains(mob))
                {
                    MapCall(mob, false);
                }
            }
        }

        // ---- the checker --------------------------------------------------------------------------------------------

        private void CompareSweep(string what, List<uint> expected, List<uint> actual)
        {
            SweepChecks++;
            bool same = expected.Count == actual.Count;

            for (int i = 0; same && i < expected.Count; i++)
            {
                same = expected[i] == actual[i];
            }

            if (!same)
            {
                var sb = new StringBuilder();
                sb.Append("[sweep] mismatch (").Append(what).Append(") legacy ").Append(expected.Count).Append(": ");
                AppendSerials(sb, expected);
                sb.Append(" | system ").Append(actual.Count).Append(": ");
                AppendSerials(sb, actual);
                ReportSweepMismatch(sb.ToString());
            }
        }

        private static void AppendSerials(StringBuilder sb, List<uint> list)
        {
            for (int i = 0; i < list.Count && i < 12; i++)
            {
                sb.Append(i == 0 ? "" : " ").Append(list[i].ToString("X8"));
            }

            if (list.Count > 12)
            {
                sb.Append(" ...");
            }
        }

        private void CompareLines(string what, List<string> expected, List<string> actual)
        {
            SweepChecks++;
            bool same = expected.Count == actual.Count;

            for (int i = 0; same && i < expected.Count; i++)
            {
                same = expected[i] == actual[i];
            }

            if (!same)
            {
                var sb = new StringBuilder();
                sb.Append("[sweep] mismatch (").Append(what).Append(") legacy ").Append(expected.Count).Append(", system ").Append(actual.Count);

                for (int i = 0; i < Math.Max(expected.Count, actual.Count) && i < 6; i++)
                {
                    string a = i < expected.Count ? expected[i] : "-";
                    string b = i < actual.Count ? actual[i] : "-";

                    if (a != b)
                    {
                        sb.Append(" | #").Append(i).Append(" legacy ").Append(a).Append(" system ").Append(b);
                    }
                }

                ReportSweepMismatch(sb.ToString());
            }
        }

        private int _sweepLogged;
        private uint _sweepLogAt;
        private long _sweepLoggedChecks = -1;

        private void ReportSweepMismatch(string message)
        {
            SweepMismatches++;
            SweepObserver?.Invoke(message);

            if (_sweepLogged < 25)
            {
                _sweepLogged++;
                Log.Warn(message);
            }
        }

        /// <summary>The checker's 5 s summary (called each frame from the ECS update).</summary>
        internal void LogSweepSummary()
        {
            if (SweepCheckOn && Time.Ticks >= _sweepLogAt && SweepChecks != _sweepLoggedChecks)
            {
                _sweepLogAt = Time.Ticks + 5000;
                _sweepLoggedChecks = SweepChecks;
                Log.Info($"[sweep] checks {SweepChecks}, mismatches {SweepMismatches} | removed mobiles {SweepRemovedMobiles}, items {SweepRemovedItems}; world map calls {SweepMapCalls} | world_sweep_system={(SweepSystemOn ? "on" : "off")}");
            }
        }
    }
}
