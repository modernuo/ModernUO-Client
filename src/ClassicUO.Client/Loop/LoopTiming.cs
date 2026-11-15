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
        /// present_on_change: ms until the next present, measured from now, rate-limited from the last present instead of a
        /// fixed grid. A pending change (a net, input or wake, a due timer, motion or effects) presents one display interval
        /// after the last present, or at once if that much time has passed; with no change the screen is redrawn after
        /// <paramref name="idleRedrawMs"/> (never sooner than the interval) for the discrete animations the loop does not track.
        /// <paramref name="msSinceLastPresent"/> is infinity before the first present.
        /// </summary>
        public static double MsUntilPresent(bool changePending, double msSinceLastPresent, double intervalMs, double idleRedrawMs)
        {
            double target = changePending ? intervalMs : Math.Max(intervalMs, idleRedrawMs);

            return Math.Max(0, target - msSinceLastPresent);
        }

        /// <summary>
        /// present_on_change_anim_due: as <see cref="MsUntilPresent(bool, double, double, double)"/>, but with no change pending
        /// the next present is when the next visual change is due (<paramref name="msUntilVisualDue"/>, from now: an animation
        /// frame, an idle animation, a text expiry; infinity when nothing is scheduled), never sooner than one interval after
        /// the last present. The fixed idle redraw period is not used. A safety net presents at least every
        /// <see cref="AnimDueSafetyNetMs"/> when nothing is due, so a change no source reports stays on screen at most that long.
        /// </summary>
        public static double MsUntilPresentAnimDue(bool changePending, double msSinceLastPresent, double intervalMs, double msUntilVisualDue)
        {
            double limit = Math.Max(0, intervalMs - msSinceLastPresent);

            return changePending ? limit : Math.Max(limit, Math.Min(msUntilVisualDue, AnimDueSafetyNetMs - msSinceLastPresent));
        }

        /// <summary>present_on_change_anim_due: the longest time between two presents (owner default ~1 s).</summary>
        public const double AnimDueSafetyNetMs = 1000;

        /// <summary>
        /// present_on_change, both idle policies: <see cref="MsUntilPresentAnimDue"/> when <paramref name="animDue"/> is on,
        /// else the fixed idle redraw period.
        /// </summary>
        public static double MsUntilPresent(bool changePending, double msSinceLastPresent, double intervalMs, double idleRedrawMs,
            bool animDue, double msUntilVisualDue)
            => animDue
                ? MsUntilPresentAnimDue(changePending, msSinceLastPresent, intervalMs, msUntilVisualDue)
                : MsUntilPresent(changePending, msSinceLastPresent, intervalMs, idleRedrawMs);

        /// <summary>
        /// present_on_change_anim_due: whether the visual change the previous Update scheduled has come due by this Update's game
        /// time (sources advance once <c>Time.Ticks</c> is past their due tick, as <see cref="MsUntilTick"/> counts).
        /// <see cref="long.MaxValue"/> = nothing was scheduled.
        /// </summary>
        public static bool VisualDueReached(long dueTick, uint ticks) => dueTick != long.MaxValue && ticks > dueTick;

        /// <summary>
        /// After a drain: whether the screen may have changed (a packet was dispatched, or the connection started or ended), and
        /// the read time packet-to-present runs from. Bytes of a packet split across reads keep their first read's time in
        /// <paramref name="partialReadTs"/> until a dispatch completes it; bytes left pending after a dispatch start a new one.
        /// </summary>
        public static bool DrainChanged(ref long partialReadTs, long readTs, int bytes, int packets, bool pendingAfter,
            bool connectionChanged, out long presentFromTs)
        {
            presentFromTs = 0;

            if (connectionChanged)
            {
                partialReadTs = 0;

                return true;
            }

            if (bytes > 0 && partialReadTs == 0)
            {
                partialReadTs = readTs;
            }

            if (packets <= 0)
            {
                return false;
            }

            presentFromTs = partialReadTs != 0 ? partialReadTs : readTs;
            partialReadTs = pendingAfter ? readTs : 0;

            return true;
        }

        /// <summary>present_on_change: whether this iteration draws (within <see cref="DrawSlackMs"/> of the deadline).</summary>
        public static bool PresentDue(bool changePending, double msSinceLastPresent, double intervalMs, double idleRedrawMs)
            => MsUntilPresent(changePending, msSinceLastPresent, intervalMs, idleRedrawMs) <= DrawSlackMs;

        /// <summary>present_on_change, both idle policies (<paramref name="animDue"/>: present_on_change_anim_due).</summary>
        public static bool PresentDue(bool changePending, double msSinceLastPresent, double intervalMs, double idleRedrawMs,
            bool animDue, double msUntilVisualDue)
            => MsUntilPresent(changePending, msSinceLastPresent, intervalMs, idleRedrawMs, animDue, msUntilVisualDue) <= DrawSlackMs;

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
        /// present_on_change: the ping's game-time deadline is a timer of its own. Without it the Update that crosses the
        /// deadline is the one after the longest wait, i.e. the one that draws at the next display interval, so the reply
        /// arrived during that Draw and was read after Present (busy Legacy ping 2.6 ms against 0.4 ms with the grid,
        /// bench-000801/000805). Waking at the deadline sends the ping from an Update that does not draw; the reply then ends
        /// the wait as a net wake. Infinity when the switch is off, no ping is scheduled, or the deadline had already passed at
        /// the Update start (the scene did not service it, e.g. not in game: waking for it would spin).
        /// </summary>
        public static double MsUntilPing(bool presentOnChange, long pingDueTick, uint ticksAtUpdate, double msSinceUpdate)
            => MsUntilTick(presentOnChange && pingDueTick > 0 && pingDueTick + 1 > ticksAtUpdate, pingDueTick, ticksAtUpdate, msSinceUpdate);

        /// <summary>A present due this soon (ms) after the ping deadline holds the ping until after that present.</summary>
        public const double PingHoldLeadMs = 1.0;

        /// <summary>The longest a due ping is held for a present (ms past its deadline); past it the ping goes out at once.</summary>
        public const double PingHoldMaxMs = 50;

        /// <summary>
        /// present_on_change: whether a due ping is held until right after the next Present instead of sent now. The ping timer
        /// and the present grid drift slowly against each other, so a ping sent just before a draw had its reply wait out the
        /// Draw and Present on the single-threaded loop (~40% of pings, in streaks, bench-000843); the spin loop never did,
        /// since it sends from the Update right after a present. Held when this iteration draws or the next present is within
        /// <see cref="PingHoldLeadMs"/>, and never longer than <see cref="PingHoldMaxMs"/> past the deadline.
        /// </summary>
        public static bool HoldPingForPresent(bool presentOnChange, bool drawsNow, double msUntilPresent, double msPastDeadline)
            => presentOnChange && msPastDeadline < PingHoldMaxMs && (drawsNow || msUntilPresent <= PingHoldLeadMs);

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
