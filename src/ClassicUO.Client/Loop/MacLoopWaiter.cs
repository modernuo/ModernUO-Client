// SPDX-License-Identifier: BSD-3-Clause
// Copyright (c) 2025, ModernUO
// IORingGroup code used under the BSD 3-Clause License; its full text (conditions and disclaimer) is in
// licenses/IORingGroup-LICENSE, which every build and publish copies next to the executable.
//
// MacKqueue's EVFILT_USER registration, Wake (the guarded NOTE_TRIGGER kevent) and Dispose (drain in-flight wakes, then
// close) are adapted from IORingGroup's DarwinIORingGroup constructor, Wake and Dispose (IORingGroup/Darwin/DarwinIORingGroup.cs,
// commit 86efd04cdca8b202a42f33fddd0d95b962e36f7b), BSD reuse approved by the owner (2026-11-19). The client's version adds a
// level-triggered EVFILT_READ on the game socket and a zero-timeout poll that reports which of the two is pending.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ClassicUO.Network;
using ClassicUO.Utility.Logging;
using SDL3;
using static ClassicUO.Loop.LoopMacOS;

namespace ClassicUO.Loop
{
    /// <summary>
    /// One kqueue with the game socket (EVFILT_READ, level-triggered: readable while data or a close is unread) and a wake
    /// (EVFILT_USER with EV_CLEAR: latched until a poll retrieves it, so a wake made outside a wait is not lost). Its fd is
    /// readable while either is pending. No SDL or CoreFoundation here, so it can be tested on its own.
    /// </summary>
    internal sealed unsafe class MacKqueue : IDisposable
    {
        private const nint WakeIdent = 1;

        private int _fd = -1;
        private nint _socket = -1;
        private bool _wakeRegistered, _loggedWakeFailure;
        private int _wakeGuard;
        private volatile bool _disposed;

        public MacKqueue()
        {
            _fd = kqueue();

            if (_fd < 0)
            {
                Log.Warn($"event loop: kqueue() failed (errno {Marshal.GetLastPInvokeError()})");

                return;
            }

            kevent_t reg = new kevent_t { ident = WakeIdent, filter = EVFILT_USER, flags = EV_ADD | EV_CLEAR };
            _wakeRegistered = kevent(_fd, &reg, 1, null, 0, null) >= 0;

            if (!_wakeRegistered)
            {
                Log.Warn($"event loop: EVFILT_USER registration failed (errno {Marshal.GetLastPInvokeError()})");
            }
        }

        public int Fd => _fd;

        public bool Valid => !_disposed && _fd >= 0 && _wakeRegistered;

        public bool SocketBound => _socket >= 0;

        /// <summary>
        /// Watches <paramref name="fd"/> (-1: none) for reads instead of the previous socket. Called on every new connection:
        /// closing a socket drops its registration, and a new socket may reuse the number.
        /// </summary>
        public bool SetSocket(nint fd)
        {
            if (_fd < 0)
            {
                return false;
            }

            if (_socket >= 0 && _socket != fd)
            {
                // fails harmlessly (ENOENT/EBADF) when the old socket is already closed
                kevent_t del = new kevent_t { ident = _socket, filter = EVFILT_READ, flags = EV_DELETE };
                kevent(_fd, &del, 1, null, 0, null);
            }

            _socket = -1;

            if (fd < 0)
            {
                return true;
            }

            kevent_t add = new kevent_t { ident = fd, filter = EVFILT_READ, flags = EV_ADD };

            if (kevent(_fd, &add, 1, null, 0, null) < 0)
            {
                Log.Warn($"event loop: kevent EVFILT_READ registration failed (errno {Marshal.GetLastPInvokeError()})");

                return false;
            }

            _socket = fd;

            return true;
        }

        /// <summary>
        /// Zero-timeout poll: whether the socket is readable and whether a wake was pending (retrieving the wake clears it;
        /// the socket stays pending until its data is read).
        /// </summary>
        public void Poll(out bool net, out bool wake)
        {
            net = wake = false;

            if (_disposed || _fd < 0)
            {
                return;
            }

            kevent_t* events = stackalloc kevent_t[4];
            timespec_t zero = default;
            int n = kevent(_fd, null, 0, events, 4, &zero);

            for (int i = 0; i < n; i++)
            {
                if (events[i].filter == EVFILT_USER)
                {
                    wake = true;
                }
                else if (events[i].filter == EVFILT_READ && (events[i].flags & EV_ERROR) == 0)
                {
                    net = true;
                }
            }
        }

        /// <summary>From any thread.</summary>
        public void Wake()
        {
            // Guarded so Dispose can drain in-flight calls before closing the kqueue fd.
            Interlocked.Increment(ref _wakeGuard);

            try
            {
                if (_disposed || !_wakeRegistered)
                {
                    return;
                }

                // Straight to kevent with a change list on the stack: called from other threads.
                kevent_t trigger = new kevent_t { ident = WakeIdent, filter = EVFILT_USER, fflags = NOTE_TRIGGER };

                if (kevent(_fd, &trigger, 1, null, 0, null) < 0 && !_loggedWakeFailure)
                {
                    // Logged once; a silently lost wake leaves nothing to point at.
                    _loggedWakeFailure = true;
                    Log.Error($"event loop: wake kevent failed (errno {Marshal.GetLastPInvokeError()})");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _wakeGuard);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Drain in-flight Wake() calls before closing the kqueue fd.
            SpinWait spin = default;

            while (Volatile.Read(ref _wakeGuard) != 0)
            {
                spin.SpinOnce();
            }

            if (_fd >= 0)
            {
                close(_fd);
                _fd = -1;
            }
        }
    }

    /// <summary>
    /// macOS (event_loop_macos): window and input events reach the main thread through the Cocoa run loop, which only a Cocoa
    /// wait serves, so the wait is SDL's own (SDL_WaitEventTimeoutNS, which waits in nextEventMatchingMask:untilDate:). The
    /// socket and the cross-thread wake share one kqueue (<see cref="MacKqueue"/>) whose fd is a CFFileDescriptor source of
    /// the main run loop: its callback runs on the main thread inside that wait and pushes one SDL user event, which ends it
    /// through SDL's own wake path. No thread of ours sits between the socket and the wait.
    /// </summary>
    internal sealed unsafe class MacLoopWaiter : LoopWaiter
    {
        private const uint SdlPollSentinel = 0x7F00;

        private readonly MacKqueue _kq;
        private readonly delegate* unmanaged<SDL.SDL_Event*, long, byte> _waitEventTimeoutNS;
        private readonly uint _userType;
        private nint _cffd, _source, _runLoop, _modes;
        private GCHandle _self;
        private bool _probeOk, _callbacksEnabled, _loggedFailure;
        private int _boundVersion = -1;

        // main thread only: set by the run-loop callback, read and cleared by Wait/BindNet/ClearWake
        private bool _inWait, _eventPending, _netFired, _wakeFired;
        private volatile bool _disposed;

        public MacLoopWaiter()
        {
            _kq = new MacKqueue();

            if (!_kq.Valid)
            {
                Log.Warn("event loop: no kqueue; using the legacy loop");

                return;
            }

            // FNA's assembly maps "SDL3" to the bundled libSDL3.0.dylib (DllMap); SDL3-CS has no SDL_WaitEventTimeoutNS
            if (!NativeLibrary.TryLoad("SDL3", typeof(SDL).Assembly, null, out nint sdl)
                || !NativeLibrary.TryGetExport(sdl, "SDL_WaitEventTimeoutNS", out nint wait))
            {
                Log.Warn("event loop: SDL_WaitEventTimeoutNS not found; using the legacy loop");

                return;
            }

            _waitEventTimeoutNS = (delegate* unmanaged<SDL.SDL_Event*, long, byte>)wait;
            _userType = SDL.SDL_RegisterEvents(1);

            if (_userType == 0 || _userType == uint.MaxValue)
            {
                Log.Warn("event loop: SDL_RegisterEvents failed; using the legacy loop");

                return;
            }

            _modes = CommonModes();
            _runLoop = CFRunLoopGetMain();
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
            var ctx = new CFFileDescriptorContext { info = GCHandle.ToIntPtr(_self) };
            _cffd = CFFileDescriptorCreate(0, _kq.Fd, 0, &OnKqueueReadable, &ctx);

            if (_cffd != 0)
            {
                _source = CFFileDescriptorCreateRunLoopSource(0, _cffd, 0);
            }

            if (_modes == 0 || _runLoop == 0 || _cffd == 0 || _source == 0)
            {
                Log.Warn("event loop: could not add the kqueue to the main run loop; using the legacy loop");

                return;
            }

            CFRunLoopAddSource(_runLoop, _source, _modes);
            EnableCallbacks();
            _probeOk = Probe();
        }

        public override bool Ready => !_disposed && _probeOk;

        // SDL's Cocoa wait takes an NSDate deadline (sub-ms); it is checked by the probe and LateWakeMonitor
        public override bool HighResolution => true;

        public override string Name => "macos-kqueue";

        private bool Probe()
        {
            const int n = 3;
            double lateMs = 0;
            int timed = 0;

            for (int i = 0; i < n; i++)
            {
                long start = Stopwatch.GetTimestamp();

                // an early return (a window event already queued) says nothing about the deadline
                if (Wait(1.0) == WakeReason.Timer)
                {
                    lateMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds - 1.0;
                    timed++;
                }
            }

            lateMs = timed > 0 ? lateMs / timed : 0;
            bool ok = lateMs <= LateWakeMonitor.LateThresholdMs;
            Log.Info($"event loop: wait probe {(ok ? "ok" : "too late")} ({Name}, {timed} timed 1 ms waits {lateMs:0.000} ms late on average)");

            return ok;
        }

        public override void BindNet(NetClient net)
        {
            if (_disposed)
            {
                return;
            }

            int version = net.TransportVersion;

            if (version != _boundVersion)
            {
                // not connected yet: retried on the next drain, as the Windows waiter does
                if (net.TryGetSocketFd(out nint fd))
                {
                    if (_kq.SetSocket(fd))
                    {
                        _boundVersion = version;
                    }
                }
                else if (_kq.SocketBound)
                {
                    _kq.SetSocket(-1);
                }
            }

            // the drain that follows reads what made the socket readable; the next Wait re-enables the callback, so data left
            // after the drain, or arriving later, fires again
            _netFired = false;
        }

        public override WakeReason Wait(double ms)
        {
            if (_disposed)
            {
                return WakeReason.Failed;
            }

            // fired outside a wait (in FNA's event poll, or before this Update's drain): sticky, like the Windows events
            if (_netFired || _wakeFired)
            {
                return Finish(true);
            }

            // re-armed here rather than in BindNet: before the drain the level-triggered read would fire once for nothing
            EnableCallbacks();
            long ns = Math.Max(1L, (long)(ms * 1_000_000.0));
            long start = Stopwatch.GetTimestamp();
            _inWait = true;
            byte got = _waitEventTimeoutNS(null, ns);
            _inWait = false;

            if (got == 0 && !_netFired && !_wakeFired && Stopwatch.GetElapsedTime(start).TotalMilliseconds < ms * 0.5)
            {
                // SDL returns false early only on an error (the event system shut down)
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Log.Error("event loop: SDL_WaitEventTimeoutNS returned early without an event");
                }

                Finish(false);

                return WakeReason.Failed;
            }

            return Finish(got != 0);
        }

        private WakeReason Finish(bool returned)
        {
            var reason = Classify(returned, _netFired, _wakeFired);
            _netFired = _wakeFired = false;

            if (_eventPending)
            {
                _eventPending = false;
                SDL.SDL_FlushEvent(_userType);
            }

            // The wait leaves SDL's poll sentinel queued, so FNA's next SDL_PollEvent would not pump the Cocoa queue; dropped,
            // the next poll pumps as it does without the wait.
            SDL.SDL_FlushEvent(SdlPollSentinel);

            return reason;
        }

        /// <summary>The wake reason from what the run-loop callback saw during the wait and whether SDL's wait returned an event.</summary>
        internal static WakeReason Classify(bool returned, bool netFired, bool wakeFired)
            => netFired ? WakeReason.Net : wakeFired ? WakeReason.Wake : returned ? WakeReason.Input : WakeReason.Timer;

        /// <summary>
        /// The callback's decisions: push the SDL user event (only inside a wait, once), and re-enable the one-shot callback
        /// (not while the socket is readable: the level-triggered read would fire again at once, before the drain; the next
        /// Wait re-enables it).
        /// </summary>
        internal static void OnFired(bool net, bool wake, bool inWait, bool eventPending, out bool push, out bool reenable)
        {
            push = (net || wake) && inWait && !eventPending;
            reenable = !net;
        }

        [UnmanagedCallersOnly]
        private static void OnKqueueReadable(nint f, nuint callBackTypes, nint info)
        {
            try
            {
                if (GCHandle.FromIntPtr(info).Target is MacLoopWaiter w)
                {
                    w.OnReadable();
                }
            }
            catch (Exception e)
            {
                Log.Error($"event loop: kqueue callback failed: {e.Message}");
            }
        }

        private void OnReadable()
        {
            // CF disables the callback each time it fires
            _callbacksEnabled = false;

            if (_disposed)
            {
                return;
            }

            _kq.Poll(out bool net, out bool wake);
            _netFired |= net;
            _wakeFired |= wake;
            OnFired(_netFired, _wakeFired, _inWait, _eventPending, out bool push, out bool reenable);

            if (push)
            {
                // inside SDL's wait: the push posts SDL's wake NSEvent, the wait pumps it and returns
                SDL.SDL_Event e = default;
                e.type = _userType;
                _eventPending = SDL.SDL_PushEvent(ref e);
            }

            if (reenable)
            {
                EnableCallbacks();
            }
        }

        private void EnableCallbacks()
        {
            if (!_callbacksEnabled && _cffd != 0)
            {
                _callbacksEnabled = true;
                CFFileDescriptorEnableCallBacks(_cffd, kCFFileDescriptorReadCallBack);
            }
        }

        public override void Wake() => _kq.Wake();

        public override void ClearWake()
        {
            if (_disposed)
            {
                return;
            }

            // retrieving the latched EVFILT_USER clears it; a socket found readable stays a pending net wake
            _kq.Poll(out bool net, out _);
            _wakeFired = false;
            _netFired |= net;
        }

        public override void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_source != 0)
            {
                CFRunLoopRemoveSource(_runLoop, _source, _modes);
                CFRelease(_source);
                _source = 0;
            }

            if (_cffd != 0)
            {
                CFFileDescriptorInvalidate(_cffd);
                CFRelease(_cffd);
                _cffd = 0;
            }

            _kq.Dispose();

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }
    }
}
