// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// IORingGroup code used under the BSD 3-Clause License; its full text (conditions and disclaimer) is in
// licenses/IORingGroup-LICENSE, which every build and publish copies next to the executable.
//
// The libc epoll, eventfd, read, write and close declarations and the epoll constants below are copied from IORingGroup
// (IORingGroup/EPoll/LinuxEpollGroup.Syscalls.cs and IORingGroup/EPoll/Structs.cs, commit
// 86efd04cdca8b202a42f33fddd0d95b962e36f7b), BSD reuse approved by the owner (2026-11-19). timerfd, the epoll_event layout
// helpers and the conversions below are added here for the client's event loop.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClassicUO.Loop
{
    internal static unsafe partial class LoopLinux
    {
        public const int EPOLL_CLOEXEC = 0x80000;
        public const int EFD_NONBLOCK = 0x800;
        public const int EFD_CLOEXEC = 0x80000;
        public const int TFD_NONBLOCK = 0x800;
        public const int TFD_CLOEXEC = 0x80000;
        public const int CLOCK_MONOTONIC = 1;
        public const int EINTR = 4;
        public const int EEXIST = 17;

        public const int EPOLL_CTL_ADD = 1;
        public const int EPOLL_CTL_DEL = 2;
        public const int EPOLL_CTL_MOD = 3;

        public const uint EPOLLIN = 0x001;
        public const uint EPOLLERR = 0x008;
        public const uint EPOLLHUP = 0x010;
        public const uint EPOLLRDHUP = 0x2000;
        public const uint EPOLLONESHOT = 0x40000000;

        /// <summary>struct epoll_event is packed (12 bytes) on x86_64 and naturally aligned (16 bytes) elsewhere.</summary>
        public static readonly int EpollEventSize = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 12 : 16;

        /// <summary>Writes one epoll_event (the low 32 bits of its data union carry <paramref name="tag"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteEvent(byte* ev, uint events, uint tag)
        {
            // zeroed in full: the kernel copies the padding on arm64 too
            *(ulong*)ev = 0;
            *(ulong*)(ev + 8) = 0;
            *(uint*)ev = events;
            *(uint*)(ev + (EpollEventSize == 12 ? 4 : 8)) = tag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadEvents(byte* ev) => *(uint*)ev;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadTag(byte* ev) => *(uint*)(ev + (EpollEventSize == 12 ? 4 : 8));

        [StructLayout(LayoutKind.Sequential)]
        public struct Timespec
        {
            public long Sec;
            public long Nsec;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Itimerspec
        {
            public Timespec Interval;
            public Timespec Value;
        }

        [LibraryImport("libc", SetLastError = true)]
        public static partial int epoll_create1(int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int epoll_ctl(int epfd, int op, int fd, nint ev);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int epoll_wait(int epfd, nint events, int maxevents, int timeout);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int close(int fd);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int eventfd(uint initval, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial long read(int fd, nint buf, nuint count);

        [LibraryImport("libc", SetLastError = true)]
        public static partial long write(int fd, nint buf, nuint count);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int timerfd_create(int clockid, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static partial int timerfd_settime(int fd, int flags, Itimerspec* newValue, Itimerspec* oldValue);

        /// <summary>
        /// A relative timerfd deadline for a wait of <paramref name="ms"/>. A zero it_value disarms a timerfd, so the shortest
        /// deadline is 1 µs (the waits the loop makes are longer than <see cref="LoopTiming.DrawSlackMs"/> anyway).
        /// </summary>
        public static Timespec ToRelativeTimespec(double ms)
        {
            long ns = double.IsNaN(ms) ? 1000 : ms >= 9.2e12 ? long.MaxValue : Math.Max(1000L, (long)(ms * 1_000_000.0));

            return new Timespec { Sec = ns / 1_000_000_000L, Nsec = ns % 1_000_000_000L };
        }

        /// <summary>The epoll_wait timeout (ms, rounded up) when there is no timerfd; -1 = no timeout.</summary>
        public static int ToEpollTimeoutMs(double ms)
        {
            if (double.IsPositiveInfinity(ms) || ms >= int.MaxValue)
            {
                return -1;
            }

            return ms <= 0 || double.IsNaN(ms) ? 0 : (int)Math.Ceiling(ms);
        }
    }
}
