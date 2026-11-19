// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using ClassicUO.Loop;
using static SDL3.SDL;

namespace ClassicUO
{
    internal unsafe partial class GameController
    {
        private DisplayRateSchedule _displaySchedule;
        private long _displayProbeAt, _displayPeriod;
        private uint _displayId;
        private double _displayHz;
        private bool _displayCapActive;
        private bool _displayDrawDue;
        private uint _totalUpdates;
        internal bool DisplayCapActive => _displayCapActive;
#if CUO_DEVTOOLS
        internal ulong PresentedFrameCount { get; private set; }
#endif

        private bool DisplayDrawDue(bool inactive, bool minimized)
        {
            bool observe = false;
#if CUO_DEVTOOLS
            observe = PresentCapCheck.On;
#endif
            if (!Settings.GlobalSettings.PresentAtDisplayRate && !observe)
            {
                _displayCapActive = false;
                _displaySchedule = default;
                return true;
            }

            long now = Stopwatch.GetTimestamp();
            if (now >= _displayProbeAt)
            {
                _displayProbeAt = now + Stopwatch.Frequency;
                uint display = SDL_GetDisplayForWindow(Window.Handle);
                var mode = (SDL_DisplayMode*)SDL_GetCurrentDisplayMode(display);
                double hz = mode == null ? 0 : mode->refresh_rate;
                if (!double.IsFinite(hz) || hz <= 0) hz = 0;
                if (display != _displayId || hz != _displayHz)
                {
                    _displaySchedule = default;
                    _displayId = display;
                    _displayHz = hz;
                    _displayPeriod = DisplayRateSchedule.PeriodTicks(hz, Stopwatch.Frequency);
                }
            }

            _displayCapActive = !minimized && _displayPeriod > 0 && DisplayRateSchedule.Enabled(
                Settings.GlobalSettings.PresentAtDisplayRate, Settings.GlobalSettings.FPS,
                GraphicManager.SynchronizeWithVerticalRetrace, inactive);
            bool due = _displaySchedule.Due(now, _displayCapActive ? _displayPeriod : 0);
#if CUO_DEVTOOLS
            if (PresentCapCheck.On)
                PresentCapCheck.Update(now, Settings.GlobalSettings.PresentAtDisplayRate, _displayCapActive, _displayHz, _displayPeriod);
#endif
            return due;
        }

        // ---- present_on_change (evloop-2) ----
        private bool _presentOnChange; // this tick: event loop and display-rate cap active
        private bool _changePending, _continuous;
        private long _lastDrawTs, _pendingNetTs, _drawnNetTs, _motionSeen, _partialReadTs;

        // ---- the next visual change (evloop-4, baked in after quiet-1115) ----
        // _visualDueTick: the earliest visual due tick seen by the last RefreshChange (long.MaxValue: none); once Time.Ticks is
        // past it the change has happened in this Update. _netEchoTick: one present 250 ms after a dispatched packet, for the
        // UI that polls packet-fed data (status 250 ms, info bar 125 ms, counter bar 100 ms) and shows it after the packet's own
        // present.
        private long _visualDueTick = long.MaxValue, _netEchoTick = long.MaxValue;
        private const int NetEchoMs = 250;

        private long NextVisualTick => Math.Min(VisualDue.NextTick, _netEchoTick);

        private double MsUntilVisualDue()
        {
            long next = NextVisualTick;

            return LoopTiming.MsUntilTick(next != long.MaxValue, next, Time.Ticks, Stopwatch.GetElapsedTime(_updateStartTs).TotalMilliseconds);
        }

        private double DisplayIntervalMs => _displayPeriod * 1000.0 / Stopwatch.Frequency;

        // The rate limit runs from the last draw start, not the present end: anchored on the present, every frame would add
        // its own draw time to the interval (busy GPU frames fell to ~50/s in bench-000791).
        private double MsSinceLastPresent(long now)
            => _lastDrawTs == 0 ? double.PositiveInfinity : (now - _lastDrawTs) * 1000.0 / Stopwatch.Frequency;

        // A drain read bytes at ts: a change, and the oldest read not yet drawn starts the packet-to-present latency.
        private void NoteNetChange(long ts)
        {
            _changePending = true;

            if (_pendingNetTs == 0)
            {
                _pendingNetTs = ts;
            }
        }

        // After a drain (or a flush): any dispatched packet, including one a plugin injected (pumped with an empty slice), and
        // any connection start or end is a change. Packet-to-present runs from the oldest read that contributed to a dispatched
        // packet: bytes of a packet split across reads keep their first read's time until the packet is dispatched.
        private void NoteDrainResult(long readTs, int bytes, int packets, int generationBefore)
        {
            bool connection = NetClient.Socket.ConnectionGeneration != generationBefore;
            bool pending = packets > 0 && !connection && PacketHandlers.Handler.PendingBytes > 0;

            if (LoopTiming.DrainChanged(ref _partialReadTs, readTs, bytes, packets, pending, connection, out long from))
            {
                if (from != 0)
                {
                    NoteNetChange(from);
                    _netEchoTick = Time.Ticks + NetEchoMs;
                }
                else
                {
                    _changePending = true;
                }
            }
        }

        // Visual changes the loop cannot see as a wake: mobile and boat steps (interpolation, completion, turns), sources that
        // change every frame while active, and the next due visual change (VisualDue, filled during Update and Draw).
        private void RefreshChange()
        {
            long motion = StepSystem.Interpolated + StepSystem.Completed + StepSystem.Turns
                + BoatStepSystem.Interpolated + BoatStepSystem.Completed + BoatStepSystem.Turns;

            if (motion != _motionSeen)
            {
                _motionSeen = motion;
                _changePending = _continuous = true;
            }

            if (VisualDue.Continuous != VisualSource.None)
            {
                _changePending = _continuous = true;
            }

            if (LoopTiming.VisualDueReached(_visualDueTick, Time.Ticks))
            {
                _changePending = true;
            }

            if (LoopTiming.VisualDueReached(_netEchoTick, Time.Ticks))
            {
                _netEchoTick = long.MaxValue;
            }

            _visualDueTick = NextVisualTick;
        }

        private bool PresentOnChangeDue()
        {
            RefreshChange();

            return LoopTiming.PresentDue(_changePending, MsSinceLastPresent(Stopwatch.GetTimestamp()), DisplayIntervalMs, MsUntilVisualDue());
        }

        private double MsUntilPresentOnChange()
            => LoopTiming.MsUntilPresent(_changePending, MsSinceLastPresent(Stopwatch.GetTimestamp()), DisplayIntervalMs, MsUntilVisualDue());

        // Draw start: what changed so far is in this frame; later changes stay pending for the next one. A frame drawn during
        // motion or effects keeps the next one due one interval later (the motion advances in Update, after the wait).
        private void BeginPresentedChange()
        {
            _lastDrawTs = Stopwatch.GetTimestamp();
            _drawnNetTs = _pendingNetTs;
            _pendingNetTs = 0;
            _changePending = _continuous;
            _continuous = false;
        }

        private void EndPresentedChange()
        {
            long now = Stopwatch.GetTimestamp();

            if (_drawnNetTs != 0)
            {
                if (LoopStats.On)
                {
                    LoopStats.NoteNetPresent(_drawnNetTs, now);
                }

                _drawnNetTs = 0;
            }
        }

        // present_on_change: a non-blocking drain and dispatch outside Update, right after Present and before the wait (never
        // between Draw and Present: a handler can change the scene or reset the device there), with the base caps; the socket
        // event is re-armed first so data left or arriving after it wakes the next wait. Sends are flushed after it.
        private void EventDrain()
        {
            long start = Stopwatch.GetTimestamp();
            int generation = NetClient.Socket.ConnectionGeneration;
#if CUO_DEVTOOLS
            if (Diagnostics.On) Diagnostics.LoopDrainStart(2, start, _waiter.NetSignaled); // before BindNet resets it
#endif
            _waiter.BindNet(NetClient.Socket);
            _parseSlice ??= ParseSlice;
            int packets = NetClient.Socket.Drain(_parseSlice, _netCapBytes, _netCapPackets, _netCapMs);
            int bytes = NetClient.Socket.LastDrainBytes;
#if CUO_DEVTOOLS
            if (Diagnostics.On) Diagnostics.LoopDrainEnd(bytes);
#endif
            _lastDrainTs = start;

            if (LoopStats.On)
            {
                LoopStats.NoteDrain(start, Stopwatch.GetTimestamp(), bytes);
            }

            if (bytes > 0)
            {
                if (FrameStats.On)
                {
                    FrameStats.Add(FrameCounter.NetBytes, bytes);
                    FrameStats.Add(FrameCounter.NetPackets, packets);
                }
            }

            NetClient.Socket.Statistics.TotalPacketsReceived += (uint)packets;
            // the send wake has done its job once the queue is flushed here: without this, every send (a ping) ends the next
            // wait at once for nothing
            _waiter.ClearWake();
            NetClient.Socket.Flush();
            // a failed send ends the connection inside Flush
            NoteDrainResult(start, bytes, packets, generation);
        }
    }
}
