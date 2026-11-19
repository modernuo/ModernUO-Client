// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Network;

namespace ClassicUO.Loop
{
    /// <summary>
    /// The event loop's wait between frames. Arm A (<see cref="Win32LoopWaiter"/>) waits on the socket's own readiness event and
    /// drains inline; <see cref="MacLoopWaiter"/> does the same on macOS through SDL's Cocoa wait and a kqueue. Phase-2 arms plug in here: a receive thread (arm B) or a ring receive (arm C) would read in
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

        /// <summary>
        /// Main thread, right before a flush: drops a pending <see cref="Wake"/>. A send queued before the flush goes out with
        /// it; one queued after it wakes again.
        /// </summary>
        public abstract void ClearWake();

        /// <summary>Diagnostics: 1 if the socket's readiness event is set now, 0 if not, -1 if no socket is bound.</summary>
        public virtual int NetSignaled => -1;

        public abstract void Dispose();

        /// <summary>The waiter for this OS, or null where the event loop has none (the legacy loop runs).</summary>
        public static LoopWaiter Create(bool macOSSwitch) => Choose(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), macOSSwitch) switch
        {
            WaiterKind.Win32 => new Win32LoopWaiter(),
            WaiterKind.MacOS => new MacLoopWaiter(),
            _ => null
        };

        internal enum WaiterKind { None, Win32, MacOS }

        internal static WaiterKind Choose(bool windows, bool macOS, bool macOSSwitch)
            => windows ? WaiterKind.Win32 : macOS && macOSSwitch ? WaiterKind.MacOS : WaiterKind.None;
    }
}
