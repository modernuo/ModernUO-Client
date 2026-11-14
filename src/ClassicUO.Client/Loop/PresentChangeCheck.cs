// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
// CLEANUP(release): shadows the display-grid draw decision (present_at_display_rate) next to present_on_change.
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    /// <summary>
    /// CUO_PRESENT_CHANGE_CHECK=1: while present_on_change decides the draws, the old grid schedule still runs on the same
    /// Update. For each pending change it notes when the grid would have drawn and when the new policy drew; a new draw later
    /// than the grid's is logged (the first few with values) and counted. The rate limit runs between draw starts: a draw-to-draw
    /// gap shorter than the interval less the draw slack breaks it and is a mismatch. The present-to-present minimum is reported
    /// too (it also varies with the draw time).
    /// </summary>
    internal static class PresentChangeCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_PRESENT_CHANGE_CHECK") == "1";

        private const int LoggedLate = 20;
        private static long _start, _updates, _changes, _draws, _gridDraws, _later, _earlier, _same, _presents, _underGap, _logged;
        private static long _lastPresent, _minGap, _laterMax, _laterSum, _earlierSum, _period, _lastDraw, _minDrawGap;
        private static long _changeAt, _gridAt;
        private static bool _active;

        internal static long Mismatches => _underGap;
        internal static long Later => _later;

        /// <summary>A gap shorter than the interval less the draw slack (and 0.1 ms of timestamp jitter) breaks the rate limit.</summary>
        public static bool UnderGap(long gap, long period, long frequency)
            => gap < period - (long)((LoopTiming.DrawSlackMs + 0.1) * frequency / 1000.0);

        /// <summary>One Update: whether present_on_change decides it, the grid's and the new decision, and a pending change.</summary>
        public static void Tick(long now, bool active, bool gridDue, bool newDue, bool changePending, long period)
        {
            if (_start == 0 || active != _active || period != _period)
            {
                Reset(now);
                _active = active;
                _period = period;
            }

            _updates++;

            if (active)
            {
                if (gridDue)
                {
                    _gridDraws++;
                }

                if (changePending && _changeAt == 0)
                {
                    _changeAt = now;
                    _changes++;
                }

                if (_changeAt != 0 && gridDue && _gridAt == 0)
                {
                    _gridAt = now;
                }

                if (newDue)
                {
                    _draws++;

                    if (_lastDraw != 0)
                    {
                        long gap = now - _lastDraw;
                        _minDrawGap = _minDrawGap == 0 ? gap : Math.Min(_minDrawGap, gap);

                        if (UnderGap(gap, period, Stopwatch.Frequency))
                        {
                            _underGap++;
                        }
                    }

                    _lastDraw = now;

                    if (_changeAt != 0)
                    {
                        Compare(now);
                    }
                }
            }

            if (now - _start >= Stopwatch.Frequency * 5)
            {
                Report(now);
            }
        }

        private static void Compare(long now)
        {
            long half = Stopwatch.Frequency / 2000;

            if (_gridAt != 0 && now - _gridAt > half)
            {
                long by = now - _gridAt;
                _later++;
                _laterSum += by;
                _laterMax = Math.Max(_laterMax, by);

                if (_logged++ < LoggedLate)
                {
                    Log.Warn(FormattableString.Invariant(
                        $"[present-change-check] later than the grid by {by * 1000.0 / Stopwatch.Frequency:F3} ms (change seen {(now - _changeAt) * 1000.0 / Stopwatch.Frequency:F3} ms before the draw, since last present {(_lastPresent == 0 ? -1 : (now - _lastPresent) * 1000.0 / Stopwatch.Frequency):F3} ms)"));
                }
            }
            else if (_gridAt == 0)
            {
                // the grid had not come due since the change: the new policy drew first
                _earlier++;
                _earlierSum += now - _changeAt;
            }
            else
            {
                _same++;
            }

            _changeAt = _gridAt = 0;
        }

        public static void Present(long now)
        {
            _presents++;

            if (_lastPresent != 0)
            {
                long gap = now - _lastPresent;
                _minGap = _minGap == 0 ? gap : Math.Min(_minGap, gap);
            }

            _lastPresent = now;
        }

        private static void Report(long now)
        {
            double sec = (now - _start) / (double)Stopwatch.Frequency;
            double ms = 1000.0 / Stopwatch.Frequency;
            Log.Info(FormattableString.Invariant(
                $"[present-change-check] active={_active} sec={sec:F3} updates/s={_updates / sec:F1} presents/s={_presents / sec:F1} draws={_draws} grid_draws={_gridDraws} changes={_changes} earlier={_earlier} same={_same} later={_later} later_mean_ms={(_later == 0 ? 0 : _laterSum * ms / _later):F3} later_max_ms={_laterMax * ms:F3} earlier_change_to_draw_mean_ms={(_earlier == 0 ? 0 : _earlierSum * ms / _earlier):F3} min_draw_gap_ms={_minDrawGap * ms:F3} min_present_gap_ms={_minGap * ms:F3} period_ms={_period * ms:F3} under_gap={_underGap} mismatches={_underGap}"));
            long last = _lastPresent, lastDraw = _lastDraw;
            Reset(now);
            _lastPresent = last;
            _lastDraw = lastDraw;
        }

        private static void Reset(long now)
        {
            _start = now;
            _updates = _changes = _draws = _gridDraws = _later = _earlier = _same = _presents = _underGap = 0;
            _minGap = _laterMax = _laterSum = _earlierSum = _minDrawGap = 0;
            _lastDraw = 0;
            _changeAt = _gridAt = 0;
            _lastPresent = 0;
        }
    }
}
#endif
