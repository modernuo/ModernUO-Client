// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// IORingGroup code used under the BSD 3-Clause License; its full text (conditions and disclaimer) is in
// licenses/IORingGroup-LICENSE, which every build and publish copies next to the executable.
//
// The kernel32/winmm event, timer and wait declarations below are copied from IORingGroup
// (IORingGroup/Windows/RIOInterop.cs, commit 1a54cff2389ba49619f6b3d33bc8d53263199940), BSD reuse approved by the owner
// (2026-11-11). MsgWaitForMultipleObjectsEx, WSAEventSelect and their constants are added here for the client's event loop.

using System.Runtime.InteropServices;

namespace ClassicUO.Loop
{
    internal static unsafe class LoopWin32
    {
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;
        public const uint WAIT_FAILED = 0xFFFFFFFF;
        public const uint INFINITE = 0xFFFFFFFF;

        public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        public const uint TIMER_ALL_ACCESS = 0x1F0003;

        // client additions
        public const uint QS_ALLINPUT = 0x04FF;
        public const uint MWMO_INPUTAVAILABLE = 0x0004;
        public const int FD_READ = 0x01;
        public const int FD_CLOSE = 0x20;

        [DllImport("kernel32.dll")]
        public static extern nint CreateEventW(void* lpEventAttributes, int bManualReset, int bInitialState, void* lpName);

        [DllImport("kernel32.dll")]
        public static extern int ResetEvent(nint hEvent);

        [DllImport("kernel32.dll")]
        public static extern int CloseHandle(nint hObject);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        public static extern int SetEvent(nint hEvent);

        // Returns 0 (TIMERR_NOERROR) on success; must be paired with timeEndPeriod.
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateWaitableTimerExW(void* lpTimerAttributes, void* lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll")]
        public static extern int SetWaitableTimer(
            nint hTimer,
            long* lpDueTime,
            int lPeriod,
            void* pfnCompletionRoutine,
            void* lpArgToCompletionRoutine,
            int fResume
        );

        // client additions: the one wait over the socket, wake and timer handles plus the thread's message queue, and the
        // socket readiness event
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint MsgWaitForMultipleObjectsEx(uint nCount, nint* pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        public static extern int WSAEventSelect(nint s, nint hEventObject, int lNetworkEvents);
    }
}
