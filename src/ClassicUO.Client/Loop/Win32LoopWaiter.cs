// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
//
// Wait and Wake are adapted from IORingGroup's WindowsManagedRIOGroup.WaitForCompletion / Wake
// (IORingGroup/Windows/WindowsManagedRIOGroup.cs, commit 1a54cff2389ba49619f6b3d33bc8d53263199940), BSD reuse approved by
// the owner (2026-11-11): the high-resolution waitable timer with the timeBeginPeriod(1) fallback, the auto-reset wake event
// and the guarded SetEvent. The client's version waits with MsgWaitForMultipleObjectsEx so window messages end it too, and
// waits on a WSAEventSelect socket event instead of a RIO completion event.

using System;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;
using static ClassicUO.Loop.LoopWin32;

namespace ClassicUO.Loop
{
    /// <summary>Arm A: one MsgWaitForMultipleObjectsEx(QS_ALLINPUT) over [socket event, wake event, deadline timer].</summary>
    internal sealed unsafe class Win32LoopWaiter : LoopWaiter
    {
        private nint _netEvent, _wakeEvent, _timer;
        private bool _timerPeriodRaised, _probeOk, _netBound, _loggedWaitFailure;
        private int _boundVersion = -1;
        private int _wakeGuard;
        private volatile bool _disposed;

        public Win32LoopWaiter()
        {
            _netEvent = CreateEventW(null, 1, 0, null); // manual reset, like WSACreateEvent
            _wakeEvent = CreateEventW(null, 0, 0, null); // auto reset: a Wake made outside a wait ends the next one

            // High-res timer requires Windows 10 1803 / Server 2019; older hosts fall back to timeBeginPeriod(1) (SDL3 has
            // already raised it unless SDL_HINT_TIMER_RESOLUTION says otherwise) and the wait's own ms timeout.
            _timer = CreateWaitableTimerExW(null, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

            if (_timer == 0)
            {
                _timerPeriodRaised = timeBeginPeriod(1) == 0;
            }

            if (_netEvent == 0 || _wakeEvent == 0)
            {
                Log.Warn("event_loop: could not create the wait events; using the legacy loop");

                return;
            }

            _probeOk = Probe();
        }

        public override bool Ready => !_disposed && _netEvent != 0 && _wakeEvent != 0 && _probeOk;

        public override bool HighResolution => _timer != 0;

        public override string Name => _timer != 0 ? "win32-hrt" : "win32-ms";

        // SupportsHighResolutionWait-style check: three 1 ms waits must come back within the late-wake threshold on average.
        private bool Probe()
        {
            const int n = 3;
            double lateMs = 0;

            for (int i = 0; i < n; i++)
            {
                long start = Stopwatch.GetTimestamp();
                WaitHandles(1.0, includeMessages: false);
                lateMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds - 1.0;
            }

            lateMs /= n;
            bool ok = lateMs <= LateWakeMonitor.LateThresholdMs;
            Log.Info($"event_loop: wait probe {(ok ? "ok" : "too late")} ({Name}, 1 ms waits {lateMs:0.000} ms late on average)");

            return ok;
        }

        public override void BindNet(NetClient net)
        {
            int version = net.TransportVersion;

            if (version != _boundVersion)
            {
                _netBound = false;

                if (net.TryEventSelect(_netEvent))
                {
                    _boundVersion = version;
                    _netBound = true;
                }
            }

            if (_netBound)
            {
                // Re-armed before the drain: every recv re-enables FD_READ, so data left after the drain (a capped drain) or
                // arriving after it signals the event again.
                ResetEvent(_netEvent);
            }
        }

        public override WakeReason Wait(double ms) => _disposed ? WakeReason.Failed : WaitHandles(ms, includeMessages: true);

        private WakeReason WaitHandles(double ms, bool includeMessages)
        {
            var handles = stackalloc nint[3];
            uint count = 0;
            int netIndex = -1;

            if (_netBound)
            {
                netIndex = (int)count;
                handles[count++] = _netEvent;
            }

            int wakeIndex = (int)count;
            handles[count++] = _wakeEvent;

            // Prefer the high-resolution timer; the wait's own timeout quantises to the system timer resolution.
            uint waitMs = Math.Max(1u, (uint)ms);
            int timerIndex = -1;

            if (_timer != 0)
            {
                // Negative means relative, in 100ns units.
                long dueTime = -Math.Max(1L, (long)(ms * 10_000.0));

                if (SetWaitableTimer(_timer, &dueTime, 0, null, null, 0) != 0)
                {
                    timerIndex = (int)count;
                    handles[count++] = _timer;
                    waitMs = INFINITE;
                }
            }

            uint result = MsgWaitForMultipleObjectsEx(count, handles, waitMs, includeMessages ? QS_ALLINPUT : 0, includeMessages ? MWMO_INPUTAVAILABLE : 0);

            if (result == WAIT_TIMEOUT || (timerIndex >= 0 && result == WAIT_OBJECT_0 + (uint)timerIndex))
            {
                return WakeReason.Timer;
            }

            if (netIndex >= 0 && result == WAIT_OBJECT_0 + (uint)netIndex)
            {
                return WakeReason.Net;
            }

            if (result == WAIT_OBJECT_0 + (uint)wakeIndex)
            {
                return WakeReason.Wake;
            }

            if (result == WAIT_OBJECT_0 + count)
            {
                return WakeReason.Input;
            }

            if (!_loggedWaitFailure)
            {
                // the loop carries on without sleeping this time; logged once so it cannot flood
                _loggedWaitFailure = true;
                Log.Error($"event_loop: MsgWaitForMultipleObjectsEx returned 0x{result:X} (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
            }

            return WakeReason.Failed;
        }

        public override void Wake()
        {
            // Guarded so Dispose can drain in-flight calls before closing the handle.
            Interlocked.Increment(ref _wakeGuard);

            try
            {
                if (_disposed || _wakeEvent == 0)
                {
                    return;
                }

                SetEvent(_wakeEvent);
            }
            finally
            {
                Interlocked.Decrement(ref _wakeGuard);
            }
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            SpinWait spin = default;

            while (Volatile.Read(ref _wakeGuard) != 0)
            {
                spin.SpinOnce();
            }

            if (_netEvent != 0)
            {
                CloseHandle(_netEvent);
                _netEvent = 0;
            }

            if (_wakeEvent != 0)
            {
                CloseHandle(_wakeEvent);
                _wakeEvent = 0;
            }

            if (_timer != 0)
            {
                CloseHandle(_timer);
                _timer = 0;
            }

            if (_timerPeriodRaised)
            {
                timeEndPeriod(1);
                _timerPeriodRaised = false;
            }
        }
    }
}
