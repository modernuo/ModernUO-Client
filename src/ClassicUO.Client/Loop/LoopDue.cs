// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Loop
{
    /// <summary>What needs an Update at a known time, or at the old cadence, while the event loop waits longer when idle.</summary>
    [Flags]
    internal enum LoopDueSource : uint
    {
        None = 0,
        NotInGame = 1 << 0,
        Input = 1 << 1,
        Walk = 1 << 2,
        Macro = 1 << 3,
        UseQueue = 1 << 4,
        DelayedClick = 1 << 5,
        Opl = 1 << 6,
        Assets = 1 << 7,
        Music = 1 << 8,
        WorldMap = 1 << 9,
        Ping = 1 << 10,
        Driver = 1 << 11
    }

    /// <summary>
    /// event_loop_idle_wait: the non-visual work Update polls, gathered right before each event-loop wait
    /// (GameController.CollectLoopDue). <see cref="At"/> is a deadline (the work runs in the first Update with
    /// <c>Time.Ticks</c> past the tick); <see cref="Now"/> is a state that polls every Update (input held, a macro, pending
    /// decodes), which keeps the wait at event_loop_max_wait_ms. The visual deadlines are VisualDue's. Statics, no allocation.
    /// </summary>
    internal static class LoopDue
    {
        public static long NextTick { get; private set; } = long.MaxValue;

        public static LoopDueSource NextSource { get; private set; }

        public static LoopDueSource Busy { get; private set; }

        public static void Begin()
        {
            NextTick = long.MaxValue;
            NextSource = Busy = LoopDueSource.None;
        }

        public static void At(long tick, LoopDueSource source)
        {
            if (tick < NextTick)
            {
                NextTick = tick;
                NextSource = source;
            }
        }

        public static void Now(LoopDueSource source) => Busy |= source;
    }
}
