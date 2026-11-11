// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Network;

namespace ClassicUO.Loop
{
    /// <summary>
    /// The event loop's wait between frames. Arm A (<see cref="Win32LoopWaiter"/>) waits on the socket's own readiness event and
    /// drains inline. Phase-2 arms plug in here: a receive thread (arm B) or a ring receive (arm C) would read in
    /// <see cref="BindNet"/>'s place and call <see cref="Wake"/> when data is ready.
    /// </summary>
    internal abstract class LoopWaiter : IDisposable
    {
        /// <summary>Created and probed: the wait returns on time.</summary>
        public abstract bool Ready { get; }

        /// <summary>The deadline is a high-resolution waitable timer (else the wait's own ms timeout).</summary>
        public abstract bool HighResolution { get; }

        /// <summary>A short name for the log.</summary>
        public abstract string Name { get; }

        /// <summary>
        /// Before the drain, on the main thread: makes the current connection's readiness wake the wait, and re-arms it so
        /// data that arrives after the drain wakes the next wait.
        /// </summary>
        public abstract void BindNet(NetClient net);

        /// <summary>Waits up to <paramref name="ms"/> for the socket, window messages, a <see cref="Wake"/> or the deadline.</summary>
        public abstract WakeReason Wait(double ms);

        /// <summary>From any thread: ends the current wait, or the next one if none is in progress (sticky).</summary>
        public abstract void Wake();

        public abstract void Dispose();
    }
}
