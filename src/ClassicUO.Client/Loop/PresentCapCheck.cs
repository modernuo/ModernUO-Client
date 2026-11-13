// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
// CLEANUP(release): counts the legacy uncapped draw decision beside the new path; never executes a second GPU present.
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    internal static class PresentCapCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_PRESENT_CAP_CHECK") == "1";
        private static long _start, _updates, _draws, _presents, _oldDraws, _last, _maxGap, _late, _period;
        private static bool _active, _switch;
        private static double _hz;

        public static bool Late(long gap, long period, long frequency) => gap > period + frequency / 1000;
        public static bool OverRate(long presents, double seconds, double hz) => presents / seconds > hz * 1.05;

        public static void Update(long now, bool on, bool active, double hz, long period)
        {
            if (_start == 0 || active != _active || on != _switch || hz != _hz)
            {
                _start = now;
                _updates = _draws = _presents = _oldDraws = _last = _maxGap = _late = 0;
                _active = active; _switch = on; _hz = hz; _period = period;
            }
            _updates++;
            if (now - _start < Stopwatch.Frequency * 5) return;
            double seconds = (now - _start) / (double)Stopwatch.Frequency;
            bool over = active && OverRate(_presents, seconds, hz);
            Log.Info(FormattableString.Invariant($"[present-cap-check] switch={on} active={active} refresh_hz={hz:F3} sec={seconds:F3} draws/s={_draws / seconds:F1} presents/s={_presents / seconds:F1} updates/s={_updates / seconds:F1} old_draws={_oldDraws} avoided={_oldDraws - _draws} max_gap_ms={_maxGap * 1000.0 / Stopwatch.Frequency:F3} period_ms={period * 1000.0 / Stopwatch.Frequency:F3} late={_late} over_rate={over} mismatches={_late + (over ? 1 : 0)}"));
            _start = now;
            _updates = _draws = _presents = _oldDraws = _maxGap = _late = 0;
        }

        public static void Draw() => _draws++;
        public static void OldDecision(bool due) { if (due) _oldDraws++; }

        public static void Present(long now)
        {
            _presents++;
            if (_last != 0)
            {
                long gap = now - _last;
                _maxGap = Math.Max(_maxGap, gap);
                if (_active && Late(gap, _period, Stopwatch.Frequency))
                {
                    _late++;
                }
            }
            _last = now;
        }
    }
}
#endif
