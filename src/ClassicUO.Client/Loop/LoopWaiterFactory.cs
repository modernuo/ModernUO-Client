// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Loop
{
    /// <summary>The event loop's wait, per OS.</summary>
    internal enum LoopWaiterKind
    {
        /// <summary>No waiter on this OS (or its switch is off): the legacy loop runs.</summary>
        None,
        /// <summary><see cref="Win32LoopWaiter"/>: MsgWaitForMultipleObjectsEx.</summary>
        Win32,
        /// <summary><see cref="LinuxEpollLoopWaiter"/>: epoll (event_loop_linux).</summary>
        LinuxEpoll
    }

    internal static class LoopWaiterFactory
    {
        /// <summary>
        /// The waiter for this OS: Windows always has one; Linux has epoll while event_loop_linux is on; elsewhere none yet.
        /// </summary>
        public static LoopWaiterKind Choose(bool isWindows, bool isLinux, bool linuxSwitch)
        {
            if (isWindows)
            {
                return LoopWaiterKind.Win32;
            }

            if (isLinux && linuxSwitch)
            {
                return LoopWaiterKind.LinuxEpoll;
            }

            return LoopWaiterKind.None;
        }

        /// <summary>Creates (and probes) the waiter; <paramref name="window"/> is the SDL window (Linux reads its display).</summary>
        public static LoopWaiter Create(LoopWaiterKind kind, nint window) => kind switch
        {
            LoopWaiterKind.Win32 => new Win32LoopWaiter(),
            LoopWaiterKind.LinuxEpoll => new LinuxEpollLoopWaiter(window),
            _ => null
        };

        /// <summary>Why the legacy loop runs when there is no waiter (for the log).</summary>
        public static string NoWaiterReason(bool isLinux) => isLinux ? "event_loop_linux is off" : "no event loop on this OS";
    }
}
