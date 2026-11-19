// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
// vsync_present_on_change is a scheduling change: the old path's decision is "draw every tick", so there is no value to compare.
// CUO_VSYNC_PRESENT_CHECK=1 counts what the switch changes instead (draws the old path would have made, presents, waits, time
// blocked in Present, missed vblanks) and logs a summary every 5 s. invalid counts a present without a draw or a draw the
// schedule did not allow while engaged; it is the mismatch count. vsync_mailbox engages the same schedule under vsync (mailbox=True
// in the summary; the old path, normal vsync, also drew every tick): there present_block_ms should stay near zero.
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    internal static class VsyncPresentCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_VSYNC_PRESENT_CHECK") == "1";

        private static long _start, _updates, _draws, _oldDraws, _presents, _waits, _blockSum, _blockMax, _blocked, _missed;
        private static long _last, _maxGap, _period, _invalid;
        private static bool _switch, _vsync, _mailbox, _engaged, _drawPending, _drawContinuous, _lastContinuous;
        private static double _hz;

        internal static long Mismatches => _invalid;

        /// <summary>A present that waited more than half a period for the swapchain: the frames in flight were all queued.</summary>
        public static bool Blocked(long block, long period) => period > 0 && block * 2 > period;

        /// <summary>A frame due one interval after a continuous one that presented more than 1.5 periods later.</summary>
        public static bool MissedVblank(long gap, long period, bool previousContinuous)
            => previousContinuous && period > 0 && gap * 2 > period * 3;

        public static void Update(long now, bool on, bool vsync, bool mailbox, bool engaged, double hz, long period)
        {
            if (_start == 0 || on != _switch || vsync != _vsync || mailbox != _mailbox || engaged != _engaged || hz != _hz)
            {
                _start = now;
                Reset();
                _last = 0;
                _drawPending = false;
                _switch = on; _vsync = vsync; _mailbox = mailbox; _engaged = engaged; _hz = hz; _period = period;
            }

            _updates++;

            if (now - _start < Stopwatch.Frequency * 5)
            {
                return;
            }

            double s = (now - _start) / (double)Stopwatch.Frequency;
            double f = 1000.0 / Stopwatch.Frequency;
            Log.Info(FormattableString.Invariant(
                $"[vsync-present-check] switch={on} vsync={vsync} mailbox={mailbox} engaged={engaged} refresh_hz={hz:F3} sec={s:F3} updates/s={_updates / s:F1} draws/s={_draws / s:F1} presents/s={_presents / s:F1} old_draws={_oldDraws} avoided={_oldDraws - _draws} waits/s={_waits / s:F1} present_block_ms(mean/max)={(_presents > 0 ? _blockSum * f / _presents : 0):F3}/{_blockMax * f:F3} blocked={_blocked} missed_vblanks={_missed} max_gap_ms={_maxGap * f:F3} period_ms={period * f:F3} invalid={_invalid} mismatches={_invalid}"));
            _start = now;
            Reset();
        }

        private static void Reset() => _updates = _draws = _oldDraws = _presents = _waits = _blockSum = _blockMax = _blocked = _missed = _maxGap = _invalid = 0;

        /// <summary>The tick's decision: the old vsync path draws every tick the new one would have drawn or skipped.</summary>
        public static void Decision(bool oldDraws, bool draws, bool due)
        {
            if (oldDraws) _oldDraws++;
            if (_engaged && draws && !due) _invalid++;
        }

        public static void Draw(bool continuous)
        {
            if (_drawPending) _invalid++;
            _drawPending = true;
            _drawContinuous = continuous;
            _draws++;
        }

        public static void Present(long start, long end)
        {
            if (!_drawPending) _invalid++;
            _drawPending = false;
            _presents++;
            long block = end - start;
            _blockSum += block;
            _blockMax = Math.Max(_blockMax, block);
            if (Blocked(block, _period)) _blocked++;

            if (_last != 0)
            {
                long gap = end - _last;
                _maxGap = Math.Max(_maxGap, gap);
                if (MissedVblank(gap, _period, _lastContinuous)) _missed++;
            }

            _last = end;
            _lastContinuous = _drawContinuous;
        }

        public static void Wait() => _waits++;
    }
}
#endif
