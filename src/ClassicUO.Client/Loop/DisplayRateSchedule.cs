// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace ClassicUO.Loop
{
    internal struct DisplayRateSchedule
    {
        public long Next { get; private set; }
        private long _period;

        public static bool Enabled(bool on, int fps, bool vsync, bool inactive)
            => on && fps == 0 && !vsync && !inactive;

        public static long PeriodTicks(double hz, long frequency)
            => double.IsFinite(hz) && hz > 0 ? Math.Max(1, (long)Math.Round(frequency / hz)) : 0;

        public bool Due(long now, long period)
        {
            if (period <= 0)
            {
                this = default;
                return true;
            }

            if (_period != period || Next == 0)
            {
                _period = period;
                Next = now + period;
                return true;
            }

            if (now < Next) return false;

            // Carry phase through small delays; never catch up with a burst after missing a whole period.
            Next = now - Next >= period ? now + period : Next + period;
            return true;
        }
    }
}
