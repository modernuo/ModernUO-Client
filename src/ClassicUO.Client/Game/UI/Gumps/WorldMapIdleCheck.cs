// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
// worldmap_idle_loop (worldmap-audit P-1) lets the event loop wait past event_loop_max_wait_ms with the world map open; the
// old path is "an Update at least every event_loop_max_wait_ms while the map is open". With CUO_WORLDMAP_IDLE_CHECK=1 every
// world map Update is compared with that cadence:
// - late_image: a posted map image waited longer than the cadence (+ slack) past the wait it was posted in;
// - late_query: the 250 ms party/guild query came due during a wait and went out more than the cadence (+ slack) late;
// - state_after_idle: the map's own drawn state (WorldMapGump.DrawnStateHash: facet, view, texture, load, packet-only
//   entities) changed in an Update that a timer started after an idle wait (gap > cadence + slack) planned for something
//   other than the world map: a change the loop did not track, which the old cadence would have drawn earlier.
// Each is logged with its values and counted as a mismatch; a summary with the switch state goes out every 5 s while the map
// is open (updates/s and idle updates/s give the wake rate with the map open, off vs on).
using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Loop;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.UI.Gumps
{
    internal static class WorldMapIdleCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_WORLDMAP_IDLE_CHECK") == "1";

        public const double SlackMs = 2;

        private const int MaxLogs = 50;

        private static long _start, _lastUpdate, _waitStart, _updates, _idleUpdates, _changes, _lateImage, _lateQuery, _stateAfterIdle, _logs;
        private static uint _waitStartTicks;
        private static bool _switch, _timedWake, _plannedForMap, _idle;
        private static int _lastHash;
        private static double _gapMs;
        private static WeakReference<WorldMapGump> _gump = new WeakReference<WorldMapGump>(null);

        internal static long Mismatches => _lateImage + _lateQuery + _stateAfterIdle;

        /// <summary>A wait was planned (GameController.EventLoopWait): whether its deadline was the world map's.</summary>
        public static void Planned(bool forWorldMap)
        {
            _plannedForMap = forWorldMap;
            _waitStart = Stopwatch.GetTimestamp();
            _waitStartTicks = Time.Ticks;
            _timedWake = false;
        }

        /// <summary>The wait ended; a timer (not the socket, input or a wake) started the next Update.</summary>
        public static void Woke(bool timed) => _timedWake = timed;

        /// <summary>Work that came due inside a wait and ran more than the cadence (+ slack) after it came due.</summary>
        public static bool Late(bool dueDuringWait, double latenessMs, double cadenceMs) => dueDuringWait && latenessMs > cadenceMs + SlackMs;

        /// <summary>A drawn-state change the loop did not plan a wake for: seen after a timed idle wait meant for other work.</summary>
        public static bool UntrackedChange(bool changed, bool idle, bool timedWake, bool plannedForMap) => changed && idle && timedWake && !plannedForMap;

        /// <summary>The world map's Update, before it takes the image and sends the query.</summary>
        public static void BeforeUpdate(WorldMapGump gump, World world, bool on)
        {
            long now = Stopwatch.GetTimestamp();
            double cadence = Math.Max(1, Settings.GlobalSettings.EventLoopMaxWaitMs);

            if (!_gump.TryGetTarget(out WorldMapGump last) || last != gump)
            {
                // a new gump: its first Update follows the open, not a wait of the old one
                _gump.SetTarget(gump);
                _lastUpdate = 0;
                _lastHash = gump.DrawnStateHash();
            }

            if (_start == 0 || on != _switch)
            {
                _start = now;
                _switch = on;
                _lastHash = gump.DrawnStateHash();
                Reset();
            }

            _gapMs = _lastUpdate == 0 ? 0 : (now - _lastUpdate) * 1000.0 / Stopwatch.Frequency;
            _lastUpdate = now;
            _idle = _gapMs > cadence + SlackMs;
            _updates++;

            if (_idle)
            {
                _idleUpdates++;
            }

            long posted = gump.PostedAt;

            if (posted != 0 && gump.LoadPendingImage)
            {
                double waitedMs = (now - posted) * 1000.0 / Stopwatch.Frequency;

                if (Late(posted > _waitStart, waitedMs, cadence))
                {
                    _lateImage++;
                    Note($"late_image waited_ms={waitedMs:F1} gap_ms={_gapMs:F1} timed={_timedWake} planned_for_map={_plannedForMap}");
                }
            }

            var entities = world.WMapManager;

            if (world.InGame && entities.Enabled && Time.Ticks > entities.NextQueryTick)
            {
                long due = entities.NextQueryTick + 1;
                double lateness = Time.Ticks - due;

                if (Late(due > _waitStartTicks, lateness, cadence))
                {
                    _lateQuery++;
                    Note($"late_query due_tick={due} ticks={Time.Ticks} late_ms={lateness:F0} gap_ms={_gapMs:F1}");
                }
            }
        }

        /// <summary>The world map's Update ended: compare its drawn state with the previous Update's.</summary>
        public static void AfterUpdate(int hash)
        {
            bool changed = hash != _lastHash;

            if (changed)
            {
                _changes++;

                if (UntrackedChange(changed, _idle, _timedWake, _plannedForMap))
                {
                    _stateAfterIdle++;
                    Note($"state_after_idle gap_ms={_gapMs:F1} hash 0x{_lastHash:X8} -> 0x{hash:X8}");
                }
            }

            _lastHash = hash;
            _timedWake = false;

            long now = Stopwatch.GetTimestamp();

            if (now - _start < Stopwatch.Frequency * 5)
            {
                return;
            }

            double s = (now - _start) / (double)Stopwatch.Frequency;
            Log.Info(FormattableString.Invariant(
                $"[worldmap-idle-check] switch={_switch} sec={s:F3} updates/s={_updates / s:F1} idle_updates/s={_idleUpdates / s:F1} state_changes={_changes} late_image={_lateImage} late_query={_lateQuery} state_after_idle={_stateAfterIdle} mismatches={Mismatches}"));
            _start = now;
            Reset();
        }

        private static void Reset()
        {
            _updates = _idleUpdates = _changes = _lateImage = _lateQuery = _stateAfterIdle = 0;
        }

        private static void Note(string text)
        {
            if (_logs++ < MaxLogs)
            {
                Log.Warn("[worldmap-idle-check] " + text);
            }
        }
    }
}
#endif
