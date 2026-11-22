// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    /// <summary>
    /// CUO_MOTION_RATE_CHECK=1: motion_above_display_rate. At each present-on-change draw it runs the display-rate decision
    /// (the display period from the same anchor) beside the one in force and counts the draws only the motion rate made
    /// (extra), the draws closer than half the period in force to the previous one (too_close: PresentAnchor never allows
    /// it), and, with the switch off, any extra draw. A [motion-rate-check] line every 5 s gives the switch, present mode,
    /// refresh, draws/s, motion draws/s and the interval spread.
    /// </summary>
    internal static class MotionRateCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_MOTION_RATE_CHECK") == "1";

        private static long _start, _last, _draws, _motion, _extra, _tooClose, _mismatches, _minGap = long.MaxValue, _maxGap;
        private static bool _switch;
        private static PresentModeKind _mode;
        private static double _hz;

        internal static long Mismatches => _mismatches;

        /// <summary>The display-rate decision: due within the slack of one display period after the anchor.</summary>
        public static bool DisplayRateDue(long now, long anchor, long displayPeriod, long frequency)
            => anchor == 0 || (now - anchor) * 1000.0 / frequency >= displayPeriod * 1000.0 / frequency - LoopTiming.DrawSlackMs;

        public static void Draw(long now, long anchor, long period, long displayPeriod, bool on, PresentModeKind mode, double hz)
        {
            if (_start == 0 || on != _switch || mode != _mode || hz != _hz)
            {
                Reset(now);
                _switch = on;
                _mode = mode;
                _hz = hz;
            }

            _draws++;
            bool motion = period != displayPeriod;

            if (motion)
            {
                _motion++;
            }

            if (!DisplayRateDue(now, anchor, displayPeriod, Stopwatch.Frequency))
            {
                _extra++;

                if (!on || !motion)
                {
                    _mismatches++;
                    Log.Warn($"[motion-rate-check] draw the display-rate decision would skip: switch={on} motion={motion} since_anchor_ms={(now - anchor) * 1000.0 / Stopwatch.Frequency:F3} period_ms={period * 1000.0 / Stopwatch.Frequency:F3}");
                }
            }

            if (_last != 0)
            {
                long gap = now - _last;
                _minGap = Math.Min(_minGap, gap);
                _maxGap = Math.Max(_maxGap, gap);

                if (period > 0 && gap + (long)(LoopTiming.DrawSlackMs * Stopwatch.Frequency / 1000) < period / 2)
                {
                    _tooClose++;
                    _mismatches++;
                }
            }

            _last = now;

            if (now - _start >= Stopwatch.Frequency * 5)
            {
                double sec = (now - _start) / (double)Stopwatch.Frequency;
                double f = 1000.0 / Stopwatch.Frequency;
                Log.Info(FormattableString.Invariant(
                    $"[motion-rate-check] switch={_switch} present={_mode} refresh_hz={_hz:F3} sec={sec:F1} draws/s={_draws / sec:F1} motion_draws/s={_motion / sec:F1} extra={_extra} too_close={_tooClose} gap_ms min/max={(_minGap == long.MaxValue ? 0 : _minGap * f):F2}/{_maxGap * f:F2} mismatches={_mismatches}"));
                long mismatches = _mismatches;
                Reset(now);
                _mismatches = mismatches;
            }
        }

        private static void Reset(long now)
        {
            _start = now;
            _draws = _motion = _extra = _tooClose = _maxGap = 0;
            _minGap = long.MaxValue;
        }
    }
}
#endif
