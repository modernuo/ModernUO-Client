// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
// event_loop_idle_wait is a scheduling change: the old path is "an Update at least every event_loop_max_wait_ms". With
// CUO_LOOP_IDLE_CHECK=1 every Update is compared with that cadence:
// - late_due: a LoopDue deadline the wait was planned for, served more than the cadence (+ slack) after it came due;
// - untracked_send: a packet (not the ping) sent by an Update that a timed wake started more than the cadence (+ slack)
//   after the previous Update, with no tracked deadline due in it: work the loop does not track, which the old cadence
//   would have run up to (gap - cadence) ms earlier;
// - music_underrun: a playing track went without a queued buffer.
// Each is logged with the source, packet id or gap, and counted as a mismatch; a summary goes out every 5 s.
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    internal static class LoopIdleCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_LOOP_IDLE_CHECK") == "1";

        /// <summary>Lateness (ms) beyond the cadence that counts: the OS wake-up error of a timed wait.</summary>
        public const double SlackMs = 2;

        private const int MaxLogs = 50;

        private static long _start, _lastUpdate, _updates, _waits, _longWaits, _maxWait, _lateDue, _untracked, _underruns, _logs;
        private static long _byBusy, _byDue, _byCap, _byOther, _byStale;
        private static readonly long[] _bySource = new long[16];
        private static bool _switch, _engaged, _timedWake, _starved, _dueReached;
        private static long _plannedDue = long.MaxValue;
        private static LoopDueSource _plannedSource;
        private static double _gapMs;
        private static int _mainThread;
        private static int _sends;
        private static byte _firstSend;

        internal static long Mismatches => _lateDue + _untracked + _underruns;

        /// <summary>A deadline served more than the cadence (+ slack) after it came due.</summary>
        public static bool Late(double latenessMs, double cadenceMs) => latenessMs > cadenceMs + SlackMs;

        /// <summary>
        /// A send the old cadence would have made earlier: from an Update a timed wake started, more than the cadence (+ slack)
        /// after the previous one, with nothing tracked due in it.
        /// </summary>
        public static bool UntrackedSend(int sends, bool timedWake, double gapMs, double cadenceMs, bool trackedDue)
            => sends > 0 && timedWake && !trackedDue && gapMs > cadenceMs + SlackMs;

        /// <summary>At the Update start: the deadline the last wait was planned for, and the music state.</summary>
        public static void UpdateStart(long now, uint ticks, bool on, bool engaged, double cadenceMs, bool musicStarved)
        {
            if (_start == 0 || on != _switch)
            {
                _start = now;
                _switch = on;
                _mainThread = Environment.CurrentManagedThreadId;
                Reset();
            }

            _engaged = engaged;
            _gapMs = _lastUpdate == 0 ? 0 : (now - _lastUpdate) * 1000.0 / Stopwatch.Frequency;
            _lastUpdate = now;
            _updates++;
            _sends = 0;
            _dueReached = false;

            if (_plannedDue != long.MaxValue && ticks > _plannedDue)
            {
                _dueReached = true;
                double lateness = ticks - (_plannedDue + 1);

                if (Late(lateness, cadenceMs))
                {
                    _lateDue++;
                    Note($"late_due source={_plannedSource} due_tick={_plannedDue} ticks={ticks} late_ms={lateness:F0} gap_ms={_gapMs:F1}");
                }

                _plannedDue = long.MaxValue;
            }

            if (musicStarved && !_starved)
            {
                _underruns++;
                Note($"music_underrun gap_ms={_gapMs:F1}");
            }

            _starved = musicStarved;
        }

        /// <summary>A send (NetClient.Send) on the main thread.</summary>
        public static void Send(byte id)
        {
            if (Environment.CurrentManagedThreadId != _mainThread || id == 0x73)
            {
                return; // another thread, or the ping (a tracked deadline)
            }

            if (_sends++ == 0)
            {
                _firstSend = id;
            }
        }

        /// <summary>
        /// Before a wait (or at its end when the tick does not wait): judge this Update's sends, then record the wait plan.
        /// <paramref name="trackedDue"/>: a queued action ran in this Update (a LoopDue deadline reached is known here).
        /// </summary>
        public static void Wait(long now, double cadenceMs, bool trackedDue, double waitMs, bool busy, LoopDueSource busySources,
            bool byDue, bool byCap, long dueTick, LoopDueSource dueSource)
        {
            if (UntrackedSend(_sends, _timedWake, _gapMs, cadenceMs, trackedDue || _dueReached))
            {
                _untracked++;
                Note($"untracked_send first_id=0x{_firstSend:X2} sends={_sends} gap_ms={_gapMs:F1} up_to_late_ms={_gapMs - cadenceMs:F1}");
            }

            _sends = 0;
            _timedWake = false;
            _waits++;

            if (waitMs > cadenceMs + SlackMs)
            {
                _longWaits++;
            }

            long w = (long)waitMs;

            if (w > _maxWait)
            {
                _maxWait = w;
            }

            if (busy)
            {
                if (busySources == LoopDueSource.None) _byStale++;

                for (int i = 0; i < _bySource.Length; i++)
                {
                    if (((uint)busySources & (1u << i)) != 0) _bySource[i]++;
                }
            }

            if (busy) _byBusy++;
            else if (byDue) _byDue++;
            else if (byCap) _byCap++;
            else _byOther++;

            // a cap wake at the deadline (due == cap, e.g. the world map's 250 ms party query with event_loop_idle_max_wait_ms
            // 250) serves it too: without it, that Update's send counted as untracked
            _plannedDue = byDue || byCap ? dueTick : long.MaxValue;
            _plannedSource = dueSource;

            if (now - _start < Stopwatch.Frequency * 5)
            {
                return;
            }

            double s = (now - _start) / (double)Stopwatch.Frequency;
            Log.Info(FormattableString.Invariant(
                $"[loop-idle-check] switch={_switch} engaged={_engaged} sec={s:F3} updates/s={_updates / s:F1} waits/s={_waits / s:F1} long_waits/s={_longWaits / s:F1} max_wait_ms={_maxWait} wait_by(busy/due/cap/other)={_byBusy}/{_byDue}/{_byCap}/{_byOther} late_due={_lateDue} untracked_send={_untracked} music_underrun={_underruns} busy_by={BusyBy()} mismatches={Mismatches}"));
            _start = now;
            Reset();
        }

        /// <summary>The wait ended: a timer (not a socket, input or send wake) started the next Update.</summary>
        public static void Woke(bool timed) => _timedWake = timed;

        private static string BusyBy()
        {
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < _bySource.Length; i++)
            {
                if (_bySource[i] != 0) sb.Append(sb.Length == 0 ? "" : ",").Append((LoopDueSource)(1u << i)).Append(':').Append(_bySource[i]);
            }

            if (_byStale != 0) sb.Append(sb.Length == 0 ? "" : ",").Append("stale:").Append(_byStale);

            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static void Reset()
        {
            Array.Clear(_bySource);
            _byStale = 0;
            _updates = _waits = _longWaits = _maxWait = _lateDue = _untracked = _underruns = 0;
            _byBusy = _byDue = _byCap = _byOther = 0;
        }

        private static void Note(string text)
        {
            if (_logs++ < MaxLogs)
            {
                Log.Warn("[loop-idle-check] " + text);
            }
        }
    }
}
#endif
