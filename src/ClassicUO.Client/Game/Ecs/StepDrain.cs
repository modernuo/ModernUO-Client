// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Ecs;
using ClassicUO.Game.Data;
using ClassicUO.Utility.Logging;
using StepsComponent = ClassicUO.Ecs.Steps;

namespace ClassicUO.Game
{
    /// <summary>
    /// Switch <c>step_drain</c> (default off): a non-local mobile's step lasts the server's learned step pace instead of the
    /// fixed MovementSpeed table time with the old "treat as mounted when steps are queued" guess. ModernUO paces NPCs on
    /// their AI move clock (max(50 ms, the move speed), with the Running bit whenever that is faster than a walk), so their
    /// steps come faster or slower than the table, and the old timing backed the queue up to MAX_STEP_COUNT and snapped.
    /// Players are held to the table by the server, so their learned pace is the table.
    /// <para>
    /// The pace is learned from the arrival times of move packets (<see cref="NoteArrival"/>, from WorldWriter.UpsertMobile)
    /// and fixed for the front step the first time it runs (<see cref="Duration"/>, WorldWriter.FixStepDuration); StepRules
    /// then completes the step after exactly that duration, and the next queued step starts where it was due (at most one
    /// frame of carry), so the frame quantisation does not stretch a walk. <c>CUO_STEP_DRAIN=1|0</c> overrides the switch and
    /// <c>CUO_STEP_DRAIN_CHECK=1</c> runs the old timing next to the new one, in developer builds.
    /// </para>
    /// </summary>
    internal static class StepDrain
    {
        // a step never shorter than this (ModernUO's AI move floor is 50 ms)
        public const int MIN_MS = 50;

        // move packets closer than this are one server tick's burst: no sample
        public const int BURST_MS = 25;

        // a longer gap between move packets is a pause: the samples restart
        public const int RESET_GAP_MS = 1000;

        // samples before a learned pace is used (the table until then)
        public const int LEARN_SAMPLES = 5;

        // the upper quartile is over the newest RECENT samples, the mean over all of them (StepCadence.SAMPLES)
        public const int RECENT = 5;

        // a learned pace at or above this share of the table is the table: arrival jitter around a table-paced player.
        // LEARN_SAMPLES back-to-back samples put the mean within 2 * jitter / 5 of the true pace, so a player held to the
        // table reaches 70 % only with arrival jitter over 3/4 of the table (75 ms when mounted and running)
        public const int SNAP_PCT = 70;

        // a step with a move queued behind it, while late, lasts this share of its pace
        public const int DRAIN_PCT = 75;

        /// <summary>Tests: the switch (null: the setting, or CUO_STEP_DRAIN in developer builds).</summary>
        internal static bool? OverrideForTests;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_STEP_DRAIN") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static bool Check = Environment.GetEnvironmentVariable("CUO_STEP_DRAIN_CHECK") == "1";
#else
        public const bool Check = false;
#endif

        public static bool On
        {
            get
            {
                if (OverrideForTests.HasValue)
                {
                    return OverrideForTests.Value;
                }

#if CUO_DEVTOOLS
                if (s_env.HasValue)
                {
                    return s_env.Value;
                }
#endif
                return Settings.GlobalSettings != null && Settings.GlobalSettings.StepDrain;
            }
        }

        /// <summary>Arrivals are recorded for the new path or its checker (off: the record is as before the switch).</summary>
        public static bool Learning => On || Check;

        public static byte Mode(bool run, bool mounted) => (byte)(1 | (run ? 2 : 0) | (mounted ? 4 : 0));

        /// <summary>
        /// One accepted packet of a non-local mobile at <paramref name="now"/>. A move samples the time since the sample
        /// anchor (the last sampled move) and moves the anchor there. A move right after a turn-only packet (ModernUO lets
        /// a player's step after a turn go at once) or within <see cref="BURST_MS"/> of the anchor (one server tick) is
        /// folded: no sample, the anchor stays, so the next sample spans it as if it were no move. The samples are thus
        /// back-to-back spans: their mean is the anchors' span over their count, which arrival jitter cannot shorten by more
        /// than twice the jitter over the count, and which a folded move only lengthens. A pause or a new run/mount mode
        /// restarts the samples.
        /// </summary>
        public static void NoteArrival(ref StepCadence c, uint now, bool moved, bool run, bool mounted)
        {
            if (!moved)
            {
                c.TurnSinceMove = c.Mode != 0;

                return;
            }

            byte mode = Mode(run, mounted);
            int pause = (int)(now - c.LastMove);
            int gap = (int)(now - c.Anchor);

            if (c.Mode != mode || pause < 0 || pause > RESET_GAP_MS || gap < 0)
            {
                c.Samples = default;
                c.Count = 0;
                c.Next = 0;
                c.Mode = mode;
                c.Anchor = now;
            }
            else if (gap >= BURST_MS && !c.TurnSinceMove)
            {
                c.Anchor = now;
                c.Samples[c.Next] = (ushort)Math.Min(gap, ushort.MaxValue);
                c.Next = (byte)((c.Next + 1) % StepCadence.SAMPLES);

                if (c.Count < StepCadence.SAMPLES)
                {
                    c.Count++;
                }
            }

            c.LastMove = now;
            c.TurnSinceMove = false;
        }

        /// <summary>
        /// The learned pace in ms (0: not learned yet): the larger of the newest samples' upper quartile (the 4th of 5) and
        /// the mean of all samples, so a speed-up needs four of the five newest intervals short and the average short too;
        /// a short interval, a burst or symmetric arrival jitter does not move it.
        /// </summary>
        public static int Cadence(in StepCadence c)
        {
            int n = c.Count;

            if (n < LEARN_SAMPLES)
            {
                return 0;
            }

            int sum = 0;

            for (int i = 0; i < n; i++)
            {
                sum += c.Samples[i];
            }

            int m = Math.Min(n, RECENT);
            Span<ushort> v = stackalloc ushort[RECENT];

            for (int k = 0; k < m; k++)
            {
                v[k] = c.Samples[(c.Next - 1 - k + StepCadence.SAMPLES) % StepCadence.SAMPLES];
            }

            for (int i = 1; i < m; i++)
            {
                ushort x = v[i];
                int j = i - 1;

                for (; j >= 0 && v[j] > x; j--)
                {
                    v[j + 1] = v[j];
                }

                v[j + 1] = x;
            }

            return Math.Max(sum / n, v[m * 3 / 4]);
        }

        /// <summary>The moves queued behind the front step (a turn in place takes no time).</summary>
        public static int MovesBehind(in StepsComponent s)
        {
            int n = 0;

            for (int k = 1; k < s.Count; k++)
            {
                ref readonly StepEntry a = ref s.Ring[s.Head + k - 1];
                ref readonly StepEntry b = ref s.Ring[s.Head + k];

                if (a.X != b.X || a.Y != b.Y)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// The front step's duration: the table time for its run/mount mode, or the learned pace when that is clearly
        /// faster (below <see cref="SNAP_PCT"/>% of the table); never longer than the table, never below
        /// <see cref="MIN_MS"/>. Drained to <see cref="DRAIN_PCT"/>% when a move is queued behind it and the render is at
        /// least half a step late: the newest move arrived <paramref name="sinceLastMove"/> ms ago and the render reaches it
        /// <paramref name="movesBehind"/> steps after this one.
        /// </summary>
        public static int Duration(int table, int cadence, int movesBehind, int sinceLastMove, out bool drained)
        {
            int d = table;

            if (cadence > 0 && cadence * 100 < table * SNAP_PCT)
            {
                d = cadence;
            }

            d = Math.Max(MIN_MS, d);

            int lateness = sinceLastMove + movesBehind * d;
            drained = movesBehind > 0 && lateness >= d / 2;

            if (drained)
            {
                d = Math.Max(MIN_MS, d * DRAIN_PCT / 100);
            }

            return d;
        }

        /// <summary>The front step's duration from its record (the mount is the ECS's, as the step timing reads it).</summary>
        public static int Duration(in StepsComponent s, bool run, bool mounted, uint ticks, out bool drained)
        {
            int table = MovementSpeed.TimeToCompleteMovement(run, mounted);
            int cadence = s.Cadence.Mode == Mode(run, mounted) ? Cadence(in s.Cadence) : 0;
            int since = s.Cadence.Mode != 0 ? Math.Max(0, (int)(ticks - s.Cadence.LastMove)) : 0;

            return Duration(table, cadence, MovesBehind(in s), since, out drained);
        }

        /// <summary>When the step after a paced one starts: where it was due, at most one frame back.</summary>
        public static uint NextStepTime(uint lastStepTime, int duration, uint ticks, int frameDelay)
        {
            int over = (int)(ticks - (lastStepTime + (uint)duration));

            return over > 0 ? ticks - (uint)Math.Min(over, Math.Max(0, frameDelay)) : ticks;
        }

        // ---- counters (the summary) and CUO_STEP_DRAIN_CHECK

        public static long Fixed, Drained, Learned;

#if CUO_DEVTOOLS
        public static long CheckFrames, CheckDiffs;
        private static long _nextSummary;

        /// <summary>CUO_STEP_DRAIN_CHECK: the old decision next to the new one on the same input, every frame.</summary>
        public static void Compare(uint serial, in StepInput inp, int duration, uint ticks, int frameDelay)
        {
            StepInput old = inp;
            old.Duration = 0;
            StepInput neu = inp;
            neu.Duration = duration;
            StepRules.Decide(in old, ticks, frameDelay, out StepDecision a);
            StepRules.Decide(in neu, ticks, frameDelay, out StepDecision b);
            CheckFrames++;

            if (!a.Equals(in b))
            {
                CheckDiffs++;

                if (CheckDiffs <= 20)
                {
                    int table = MovementSpeed.TimeToCompleteMovement(inp.Front.Run, inp.Mounted);
                    Log.Info($"[step-drain-check] 0x{serial:X8} run {inp.Front.Run} mounted {inp.Mounted} queued {inp.Count} delay {(int)(ticks - inp.LastStepTime)}: table {table} ms, new {duration} ms | old {a} | new {b}");
                }
            }

            if (Time.Ticks >= _nextSummary)
            {
                _nextSummary = Time.Ticks + 5000;
                Log.Info(Summary());
            }
        }
#endif

        public static string Summary() =>
#if CUO_DEVTOOLS
            $"[step-drain-check] step_drain {(On ? "on" : "off")}: frames compared {CheckFrames}, decisions different {CheckDiffs}; " +
#else
            $"[step-drain] step_drain {(On ? "on" : "off")}: " +
#endif
            $"durations fixed {Fixed} (learned faster than the table {Learned}, drained {Drained})";
    }
}
