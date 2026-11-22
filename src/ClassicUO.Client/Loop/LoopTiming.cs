// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Loop
{
    /// <summary>How the main loop spends the time between frames.</summary>
    internal enum LoopMode
    {
        /// <summary>
        /// The original loop, the fallback where the event loop cannot run (no waiter for this OS or its switch is off, no
        /// working waiter, late-wake backoff): skip the draw and Thread.Sleep(1) (or 12 ms) until the frame interval has passed.
        /// </summary>
        Legacy,
        /// <summary>event loop: one wait for the socket, window messages, a wake and the next deadline.</summary>
        Event
    }

    /// <summary>The event loop's pure decisions (no clock, no OS calls), so they can be unit tested.</summary>
    internal static class LoopTiming
    {
        /// <summary>A draw this close to its deadline (ms) is made now instead of after one more short wait.</summary>
        public const double DrawSlackMs = 0.5;

        /// <summary>
        /// The loop mode for this iteration. The event loop needs a waiter for this OS (<see cref="LoopWaiterFactory.Choose"/>),
        /// that waiter working, and no late-wake backoff in force; otherwise the legacy loop runs.
        /// </summary>
        public static LoopMode Decide(bool hasWaiter, bool waiterReady, bool lateWakeSuspended)
        {
            if (!hasWaiter || !waiterReady || lateWakeSuspended)
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
        /// Present on change (evloop-2/4): ms until the next present, measured from now, rate-limited from the last present
        /// instead of a fixed grid. A pending change (a net, input or wake, a due timer, motion, a continuous visual source)
        /// presents one display interval after the last present, or at once if that much time has passed. With no change the
        /// next present is when the next visual change is due (<paramref name="msUntilVisualDue"/>, from now: an animation
        /// frame, an idle animation, a text expiry; infinity when nothing is scheduled), never sooner than one interval after
        /// the last present. A safety net presents at least every <see cref="AnimDueSafetyNetMs"/> when nothing is due, so a
        /// change no source reports stays on screen at most that long. <paramref name="msSinceLastPresent"/> is infinity before
        /// the first present.
        /// </summary>
        public static double MsUntilPresent(bool changePending, double msSinceLastPresent, double intervalMs, double msUntilVisualDue)
        {
            double limit = Math.Max(0, intervalMs - msSinceLastPresent);

            return changePending ? limit : Math.Max(limit, Math.Min(msUntilVisualDue, AnimDueSafetyNetMs - msSinceLastPresent));
        }

        /// <summary>
        /// Present on change: the time the next present's rate limit runs from, set at each draw start. A draw less than half
        /// an interval past its deadline (the previous anchor plus one interval) keeps the deadline as the anchor, so the wake's
        /// lateness and the Update that runs before the draw decision are not added to every interval: anchored on the draw
        /// start, a continuous scene (walking) presented at 1000 / (interval + lateness + Update) frames/s, below the display
        /// rate, and with vsync off the refreshes it missed showed the same frame twice. A later draw (after an idle screen or
        /// a stall) starts a new phase at <paramref name="now"/>, so no two presents are less than half an interval apart.
        /// Ticks of any unit; <paramref name="anchor"/> 0 = no present yet, <paramref name="period"/> 0 = no display rate.
        /// </summary>
        public static long PresentAnchor(long anchor, long now, long period)
        {
            if (anchor == 0 || period <= 0)
            {
                return now;
            }

            long deadline = anchor + period;

            return now - deadline < period / 2 ? deadline : now;
        }

        /// <summary>Present on change: the longest time between two presents (owner default ~1 s).</summary>
        public const double AnimDueSafetyNetMs = 1000;

        /// <summary>
        /// Whether this Update collects the next visual change (<see cref="VisualDue.Tracking"/>): only where present on change
        /// reads it, the event loop with the display-rate cap (fps 0, vsync off, window active). The cap state is the previous
        /// Update's (it is decided after the walks that report); a mode change catches up one Update later.
        /// </summary>
        public static bool TracksVisualDue(LoopMode mode, bool displayCapActive) => mode == LoopMode.Event && displayCapActive;

        /// <summary>
        /// Present on change: whether the visual change the previous Update scheduled has come due by this Update's game
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

        /// <summary>Present on change: whether this iteration draws (within <see cref="DrawSlackMs"/> of the deadline).</summary>
        public static bool PresentDue(bool changePending, double msSinceLastPresent, double intervalMs, double msUntilVisualDue)
            => MsUntilPresent(changePending, msSinceLastPresent, intervalMs, msUntilVisualDue) <= DrawSlackMs;

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
        /// event_loop_idle_wait: the longest wait. Off, or while something polls every Update (<paramref name="busy"/>), the
        /// event_loop_max_wait_ms cadence; on and idle, event_loop_idle_max_wait_ms (never shorter than the cadence).
        /// </summary>
        public static double MaxWaitMs(bool idleWait, bool busy, int maxWaitMs, int idleMaxWaitMs)
            => Math.Max(1, idleWait && !busy ? Math.Max(maxWaitMs, idleMaxWaitMs) : maxWaitMs);

        /// <summary>
        /// event_loop_idle_wait: ms from now until the earliest <see cref="LoopDue"/> deadline (infinity: none). A deadline
        /// that had already passed at the Update start was not served by that Update (its work waits on something else), so
        /// it is <paramref name="stale"/>: waking for it would spin, so the caller keeps the event_loop_max_wait_ms cadence
        /// instead, as before the switch.
        /// </summary>
        public static double MsUntilLoopDue(long dueTick, uint ticksAtUpdate, double msSinceUpdate, out bool stale)
        {
            bool has = dueTick != long.MaxValue;
            stale = has && dueTick + 1 <= ticksAtUpdate;

            return MsUntilTick(has && !stale, dueTick, ticksAtUpdate, msSinceUpdate);
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
        /// present-on-change: the ping's game-time deadline is a timer of its own. Without it the Update that crosses the
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
        /// present-on-change: whether a due ping is held until right after the next Present instead of sent now. The ping timer
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
