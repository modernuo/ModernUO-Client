// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// IORingGroup code used under the BSD 3-Clause License; its full text (conditions and disclaimer) is in
// licenses/IORingGroup-LICENSE, which every build and publish copies next to the executable.
//
// Wake, the wake eventfd set-up and Dispose's guarded drain are adapted from IORingGroup's LinuxEpollGroup (constructor, Wake,
// PollAndExecute's wake drain and Dispose; IORingGroup/EPoll/LinuxEpollGroup.cs, commit
// 86efd04cdca8b202a42f33fddd0d95b962e36f7b), BSD reuse approved by the owner (2026-11-19). The client's version adds a timerfd
// deadline, the display connection's fd and an SDL event watch so window input and SDL's cross-thread events end the wait, and
// waits on the game socket instead of ring connections.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;
using static ClassicUO.Loop.LoopLinux;
using static SDL3.SDL;

namespace ClassicUO.Loop
{
    /// <summary>
    /// event_loop_linux: one epoll_wait over [game socket, wake eventfd, deadline timerfd, display connection fd, SDL-event
    /// eventfd]. Before it blocks, SDL is pumped and the queues the fd cannot show are checked (SDL's own queue, Xlib's queue,
    /// libwayland's default queue); a non-empty one returns <see cref="WakeReason.Input"/> at once. Events SDL pushes from other
    /// threads (audio and device hotplug) reach the wait through an SDL event watch that writes the SDL-event eventfd. Input
    /// SDL reads from fds of its own (evdev/udev joysticks, IME) does not end the wait: it is picked up by the next Update, at
    /// most event_loop_max_wait_ms (event_loop_idle_max_wait_ms with event_loop_idle_wait) later. A future io_uring receive arm
    /// can keep this waiter as its fallback (many distributions disable io_uring).
    /// </summary>
    internal sealed unsafe class LinuxEpollLoopWaiter : LoopWaiter
    {
        private const uint TagNet = 1, TagWake = 2, TagTimer = 3, TagDisplay = 4, TagSdl = 5;
        private const int MaxEvents = 5;

        private enum DisplayKind
        {
            None,
            X11,
            Wayland
        }

        private int _epollFd = -1, _wakeFd = -1, _timerFd = -1, _sdlFd = -1;
        private int _netFd = -1, _boundVersion = -1;
        private bool _netBound, _probeOk, _watchAdded, _loggedWaitFailure;
        private readonly bool _sdl;
        private int _wakeGuard;
        private volatile bool _disposed;

        private readonly DisplayKind _display;
        private readonly nint _displayPtr;
        private readonly delegate* unmanaged<nint, int, int> _xEventsQueued;
        private readonly delegate* unmanaged<nint, int> _wlPrepareRead;
        private readonly delegate* unmanaged<nint, void> _wlCancelRead;

        // the waiter the SDL event watch signals (one per process; the watch is removed before it is cleared)
        private static LinuxEpollLoopWaiter _watchTarget;

        // FNA's binding (its dllmap finds libSDL3.so.0); the delegate is static so it outlives the watch
        private static readonly SDL_EventFilter _watch = OnSdlEvent;

        [ThreadStatic] private static bool _isLoopThread;

        /// <param name="window">The SDL window, whose display connection ends the wait on input.</param>
        /// <param name="sdl">False in unit tests: no SDL pump, event watch or display (socket, wake and deadline only).</param>
        public LinuxEpollLoopWaiter(nint window, bool sdl = true)
        {
            _isLoopThread = true;
            _sdl = sdl;

            _epollFd = epoll_create1(EPOLL_CLOEXEC);

            if (_epollFd < 0)
            {
                Log.Warn($"event loop: epoll_create1 failed (errno {Marshal.GetLastPInvokeError()}); using the legacy loop");

                return;
            }

            _wakeFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
            _sdlFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);

            if (_wakeFd < 0 || _sdlFd < 0 || !Add(_wakeFd, TagWake) || !Add(_sdlFd, TagSdl))
            {
                Log.Warn($"event loop: could not set up the wake eventfds (errno {Marshal.GetLastPInvokeError()}); using the legacy loop");

                return;
            }

            // Without a timerfd the deadline is epoll_wait's own ms timeout (rounded up to whole ms).
            _timerFd = timerfd_create(CLOCK_MONOTONIC, TFD_NONBLOCK | TFD_CLOEXEC);

            if (_timerFd >= 0 && !Add(_timerFd, TagTimer))
            {
                close(_timerFd);
                _timerFd = -1;
            }

            int displayFd = -1;

            if (sdl)
            {
                _display = OpenDisplay(window, out _displayPtr, out displayFd, out _xEventsQueued, out _wlPrepareRead, out _wlCancelRead);
            }

            if (_display != DisplayKind.None && !Add(displayFd, TagDisplay))
            {
                Log.Warn($"event loop: could not add the display fd to epoll (errno {Marshal.GetLastPInvokeError()}); window input waits for the next Update");
                _display = DisplayKind.None;
            }

            if (_display == DisplayKind.None && sdl)
            {
                Log.Warn("event loop: no X11 or Wayland display fd; window input is picked up by the next Update (event_loop_max_wait_ms)");
            }

            if (sdl)
            {
                _watchTarget = this;
                _watchAdded = SDL_AddEventWatch(_watch, 0);
            }

            if (sdl && !_watchAdded)
            {
                _watchTarget = null;
                Log.Warn("event loop: SDL_AddEventWatch failed; events SDL pushes from other threads wait for the next Update");
            }

            _probeOk = Probe();
        }

        public override bool Ready => !_disposed && _epollFd >= 0 && _wakeFd >= 0 && _sdlFd >= 0 && _probeOk;

        public override bool HighResolution => _timerFd >= 0;

        public override string Name => _display switch
        {
            DisplayKind.X11 => _timerFd >= 0 ? "epoll-x11" : "epoll-x11-ms",
            DisplayKind.Wayland => _timerFd >= 0 ? "epoll-wayland" : "epoll-wayland-ms",
            _ => _timerFd >= 0 ? "epoll-nodisplay" : "epoll-nodisplay-ms"
        };

        private bool Add(int fd, uint tag)
        {
            byte* ev = stackalloc byte[16];
            WriteEvent(ev, EPOLLIN, tag);

            return epoll_ctl(_epollFd, EPOLL_CTL_ADD, fd, (nint)ev) == 0;
        }

        private static DisplayKind OpenDisplay(nint window, out nint displayPtr, out int fd, out delegate* unmanaged<nint, int, int> xEventsQueued,
            out delegate* unmanaged<nint, int> wlPrepareRead, out delegate* unmanaged<nint, void> wlCancelRead)
        {
            displayPtr = 0;
            fd = -1;
            xEventsQueued = null;
            wlPrepareRead = null;
            wlCancelRead = null;

            if (window == 0)
            {
                return DisplayKind.None;
            }

            uint props = SDL_GetWindowProperties(window);
            nint x11 = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, 0);

            // SDL loaded these libraries already; TryLoad finds the same copy
            if (x11 != 0 && NativeLibrary.TryLoad("libX11.so.6", out nint libX11)
                && NativeLibrary.TryGetExport(libX11, "XConnectionNumber", out nint xConnectionNumber)
                && NativeLibrary.TryGetExport(libX11, "XEventsQueued", out nint xQueued))
            {
                displayPtr = x11;
                fd = ((delegate* unmanaged<nint, int>)xConnectionNumber)(x11);
                xEventsQueued = (delegate* unmanaged<nint, int, int>)xQueued;

                return fd >= 0 ? DisplayKind.X11 : DisplayKind.None;
            }

            nint wl = SDL_GetPointerProperty(props, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, 0);

            if (wl != 0 && NativeLibrary.TryLoad("libwayland-client.so.0", out nint libWl)
                && NativeLibrary.TryGetExport(libWl, "wl_display_get_fd", out nint getFd)
                && NativeLibrary.TryGetExport(libWl, "wl_display_prepare_read", out nint prepare)
                && NativeLibrary.TryGetExport(libWl, "wl_display_cancel_read", out nint cancel))
            {
                displayPtr = wl;
                fd = ((delegate* unmanaged<nint, int>)getFd)(wl);
                wlPrepareRead = (delegate* unmanaged<nint, int>)prepare;
                wlCancelRead = (delegate* unmanaged<nint, void>)cancel;

                return fd >= 0 ? DisplayKind.Wayland : DisplayKind.None;
            }

            return DisplayKind.None;
        }

        // Runs on the thread that pushes the event, before SDL queues it. The loop thread's own pushes (SDL_PumpEvents) are
        // skipped: they happen while it is not waiting, and the check before the wait sees them. No allocation.
        private static bool OnSdlEvent(nint userdata, SDL_Event* evt)
        {
            if (!_isLoopThread)
            {
                Volatile.Read(ref _watchTarget)?.Signal(isSdl: true);
            }

            return true;
        }

        // SupportsHighResolutionWait-style check: three 1 ms waits must come back within the late-wake threshold on average.
        private bool Probe()
        {
            const int n = 3;
            double lateMs = 0;

            for (int i = 0; i < n; i++)
            {
                long start = Stopwatch.GetTimestamp();
                WaitFds(1.0);
                lateMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds - 1.0;
            }

            lateMs /= n;
            bool ok = lateMs <= LateWakeMonitor.LateThresholdMs;
            Log.Info($"event loop: wait probe {(ok ? "ok" : "too late")} ({Name}, 1 ms waits {lateMs:0.000} ms late on average)");

            return ok;
        }

        public override void BindNet(NetClient net)
        {
            int version = net.TransportVersion;

            if (version != _boundVersion)
            {
                if (_netFd >= 0)
                {
                    // fails harmlessly (EBADF/ENOENT) when the old socket is already closed: close removed it from the set
                    epoll_ctl(_epollFd, EPOLL_CTL_DEL, _netFd, 0);
                }

                _netBound = false;
                _netFd = -1;

                if (net.TryGetPollFd(out int fd))
                {
                    byte* ev = stackalloc byte[16];
                    WriteEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLONESHOT, TagNet);
                    int r = epoll_ctl(_epollFd, EPOLL_CTL_ADD, fd, (nint)ev);

                    if (r != 0 && Marshal.GetLastPInvokeError() == EEXIST)
                    {
                        r = epoll_ctl(_epollFd, EPOLL_CTL_MOD, fd, (nint)ev);
                    }

                    if (r == 0)
                    {
                        _netFd = fd;
                        _boundVersion = version;
                        _netBound = true;
                    }
                }

                return;
            }

            if (_netBound)
            {
                // Re-armed before the drain (one-shot, so a closed socket cannot spin the loop): epoll checks readiness again
                // on MOD, so data left after a capped drain, or arriving after it, ends the next wait.
                byte* ev = stackalloc byte[16];
                WriteEvent(ev, EPOLLIN | EPOLLRDHUP | EPOLLONESHOT, TagNet);

                if (epoll_ctl(_epollFd, EPOLL_CTL_MOD, _netFd, (nint)ev) != 0)
                {
                    _netBound = false;
                    _netFd = -1;
                }
            }
        }

        public override WakeReason Wait(double ms)
        {
            if (_disposed)
            {
                return WakeReason.Failed;
            }

            if (!_sdl)
            {
                return WaitFds(ms);
            }

            // Events already read off the display fd (into SDL's, Xlib's or libwayland's queue) do not show on it.
            SDL_PumpEvents();

            if (SDL_HasEvents((uint)SDL_EventType.SDL_EVENT_FIRST, (uint)SDL_EventType.SDL_EVENT_LAST) || DisplayQueued())
            {
                return WakeReason.Input;
            }

            return WaitFds(ms);
        }

        // Main thread, right after SDL_PumpEvents: whether the display library holds events the fd no longer shows.
        private bool DisplayQueued()
        {
            switch (_display)
            {
                case DisplayKind.X11:
                    // QueuedAfterFlush (2): flushes requests and reads what the fd has without blocking
                    return _xEventsQueued(_displayPtr, 2) > 0;

                case DisplayKind.Wayland:
                    // prepare_read fails while the default queue has events; it is cancelled at once so other readers
                    // (the GPU driver's WSI) are never held up by this wait
                    if (_wlPrepareRead(_displayPtr) != 0)
                    {
                        return true;
                    }

                    _wlCancelRead(_displayPtr);

                    return false;

                default:
                    return false;
            }
        }

        private WakeReason WaitFds(double ms)
        {
            int timeout = ToEpollTimeoutMs(ms);

            if (_timerFd >= 0 && timeout > 0)
            {
                Itimerspec spec = default;
                spec.Value = ToRelativeTimespec(ms);

                if (timerfd_settime(_timerFd, 0, &spec, null) == 0)
                {
                    timeout = -1;
                }
            }

            byte* events = stackalloc byte[MaxEvents * 16];
            long deadline = timeout > 0 ? Environment.TickCount64 + timeout : 0;
            int n;

            while (true)
            {
                n = epoll_wait(_epollFd, (nint)events, MaxEvents, timeout);

                if (n >= 0 || Marshal.GetLastPInvokeError() != EINTR)
                {
                    break;
                }

                // a signal (the runtime's thread suspension among them): wait again for what is left
                if (timeout > 0)
                {
                    timeout = (int)Math.Max(0, deadline - Environment.TickCount64);
                }
            }

            if (n < 0)
            {
                if (!_loggedWaitFailure)
                {
                    // the loop carries on without sleeping this time; logged once so it cannot flood
                    _loggedWaitFailure = true;
                    Log.Error($"event loop: epoll_wait failed (errno {Marshal.GetLastPInvokeError()})");
                }

                return WakeReason.Failed;
            }

            bool net = false, wake = false, timer = false, input = false, sdl = false;

            for (int i = 0; i < n; i++)
            {
                byte* ev = events + i * EpollEventSize;

                switch (ReadTag(ev))
                {
                    case TagNet: net = true; break;
                    case TagWake: wake = true; break;
                    case TagTimer: timer = true; break;
                    case TagDisplay: input = true; break;
                    case TagSdl: sdl = true; break;
                }
            }

            ulong drain;

            if (timer)
            {
                // the next wait re-arms it in any case
                read(_timerFd, (nint)(&drain), 8);
            }

            var reason = n == 0 ? WakeReason.Timer : PickReason(net, wake, timer, input || sdl);

            // Only the reported source is consumed (Windows' auto-reset wake event likewise stays set when the socket wins),
            // so a wake or SDL event not reported now ends the next wait.
            if (reason == WakeReason.Wake)
            {
                read(_wakeFd, (nint)(&drain), 8);
            }
            else if (reason == WakeReason.Input && sdl)
            {
                read(_sdlFd, (nint)(&drain), 8);
            }

            return reason;
        }

        /// <summary>
        /// The reason a wait reports when several sources are ready, in the Windows waiter's order: the socket, a Wake, the
        /// deadline, then window input (MsgWaitForMultipleObjectsEx returns the lowest signalled handle, messages last).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static WakeReason PickReason(bool net, bool wake, bool timer, bool input)
        {
            if (net) return WakeReason.Net;
            if (wake) return WakeReason.Wake;
            if (timer) return WakeReason.Timer;

            return input ? WakeReason.Input : WakeReason.Timer;
        }

        public override void Wake() => Signal(isSdl: false);

        private void Signal(bool isSdl)
        {
            // Guarded so Dispose can drain in-flight calls before closing the fds.
            Interlocked.Increment(ref _wakeGuard);

            try
            {
                int fd = isSdl ? _sdlFd : _wakeFd;

                if (_disposed || fd < 0)
                {
                    return;
                }

                // The counter latches, so a wake landing before the loop blocks is not lost.
                ulong one = 1;
                write(fd, (nint)(&one), 8);
            }
            finally
            {
                Interlocked.Decrement(ref _wakeGuard);
            }
        }

        public override void ClearWake()
        {
            if (!_disposed && _wakeFd >= 0)
            {
                ulong drain;
                read(_wakeFd, (nint)(&drain), 8);
            }
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_watchAdded)
            {
                SDL_RemoveEventWatch(_watch, 0);
                _watchAdded = false;
            }

            Interlocked.CompareExchange(ref _watchTarget, null, this);

            SpinWait spin = default;

            while (Volatile.Read(ref _wakeGuard) != 0)
            {
                spin.SpinOnce();
            }

            CloseFd(ref _timerFd);
            CloseFd(ref _sdlFd);
            CloseFd(ref _wakeFd);
            CloseFd(ref _epollFd);
        }

        private static void CloseFd(ref int fd)
        {
            if (fd >= 0)
            {
                close(fd);
                fd = -1;
            }
        }
    }
}
