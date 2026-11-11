// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Loop
{
    /// <summary>How the main loop spends the time between frames.</summary>
    internal enum LoopMode
    {
        /// <summary>The original loop: skip the draw and Thread.Sleep(1) (or 12 ms) until the frame interval has passed.</summary>
        Legacy,
        /// <summary>event_loop: one wait for the socket, window messages, a wake and the next deadline.</summary>
        Event
    }

    /// <summary>The event loop's pure decisions (no clock, no OS calls), so they can be unit tested.</summary>
    internal static class LoopTiming
    {
        /// <summary>A draw this close to its deadline (ms) is made now instead of after one more short wait.</summary>
        public const double DrawSlackMs = 0.5;

        /// <summary>
        /// The loop mode for this iteration. The event loop needs the switch, Windows (its wait is
        /// MsgWaitForMultipleObjectsEx), a working waiter, and no late-wake backoff in force; otherwise the legacy loop runs.
        /// </summary>
        public static LoopMode Decide(bool switchOn, bool isWindows, bool waiterReady, bool lateWakeSuspended)
        {
            if (!switchOn || !isWindows || !waiterReady || lateWakeSuspended)
            {
                return LoopMode.Legacy;
            }

            return LoopMode.Event;
        }

        /// <summary>
        /// Event-loop frame cap: whether this iteration draws, given the time accumulated since the last draw and the frame
        /// interval (ms, 0 = uncapped). A draw within <see cref="DrawSlackMs"/> of its deadline is made now, and its lead is
        /// carried into the next interval (the accumulator goes slightly negative) so the average rate stays at the cap; after a
        /// stall longer than an interval the remainder is kept modulo the interval, as the legacy loop does.
        /// </summary>
        public static bool DrawDue(double totalElapsedMs, double intervalMs, out double newTotalMs)
        {
            if (intervalMs <= 0)
            {
                newTotalMs = 0;

                return true;
            }

            if (totalElapsedMs + DrawSlackMs < intervalMs)
            {
                newTotalMs = totalElapsedMs;

                return false;
            }

            newTotalMs = totalElapsedMs - intervalMs;

            if (newTotalMs >= intervalMs)
            {
                newTotalMs %= intervalMs;
            }

            return true;
        }

        /// <summary>
        /// Milliseconds until the next draw is due, measured from now: the interval less the accumulated time and the time since
        /// that accumulation was taken (the Update start). <see cref="double.PositiveInfinity"/> when no draw is due (minimized)
        /// and 0 when uncapped.
        /// </summary>
        public static double MsUntilDraw(bool drawSkipped, double intervalMs, double totalElapsedMs, double msSinceUpdate)
        {
            if (drawSkipped)
            {
                return double.PositiveInfinity;
            }

            if (intervalMs <= 0)
            {
                return 0;
            }

            return Math.Max(0, intervalMs - totalElapsedMs - msSinceUpdate);
        }

        /// <summary>
        /// The wait for this iteration, in ms: the earliest of the next draw, the next timer (both from now) and the cap (the
        /// longest the loop may go without an Update, for timers it does not track). 0 = do not wait.
        /// </summary>
        public static double WaitMs(double msUntilDraw, double msUntilTimer, double maxWaitMs)
        {
            double wait = Math.Min(msUntilDraw, Math.Min(msUntilTimer, maxWaitMs));

            // a wait shorter than the slack would cost more in wake-up error than it saves
            return wait <= DrawSlackMs ? 0 : wait;
        }

        /// <summary>
        /// Milliseconds from now until a game-time due tick (the client fires a due action once Time.Ticks is past it, i.e. at
        /// due + 1), given the game time at the Update start and the time since. Infinity when there is none.
        /// </summary>
        public static double MsUntilTick(bool has, long dueTick, uint ticksAtUpdate, double msSinceUpdate)
        {
            if (!has)
            {
                return double.PositiveInfinity;
            }

            return Math.Max(0, dueTick + 1 - (double)ticksAtUpdate - msSinceUpdate);
        }

        /// <summary>
        /// Event loop: the per-drain byte and packet caps scale with the time since the previous drain (one Update per frame
        /// instead of one per ~1 ms), at least the base cap and at most four times it. The time cap stays per drain.
        /// </summary>
        public static int ScaleCap(int baseCap, double msSinceLastDrain, double frameMs = 1000.0 / 60)
        {
            double k = msSinceLastDrain / frameMs;

            if (!(k > 1))
            {
                return baseCap;
            }

            if (k > 4)
            {
                k = 4;
            }

            long v = (long)(baseCap * k);

            return v > int.MaxValue ? int.MaxValue : (int)v;
        }
    }
}
