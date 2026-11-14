// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Network;
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

        private double MsUntilDisplayDraw()
            => Math.Max(0, (_displaySchedule.Next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);

        // ---- present_on_change (evloop-2) ----
        private bool _presentOnChange; // this tick: event loop, display-rate cap active and the switch on
        private bool _changePending, _continuous;
        private long _lastPresentTs, _pendingNetTs, _drawnNetTs, _motionSeen;

        private static bool PresentOnChangeSwitch => Settings.GlobalSettings.PresentOnChange;

        private double DisplayIntervalMs => _displayPeriod * 1000.0 / Stopwatch.Frequency;

        private static double IdleRedrawMs => Math.Max(1, Settings.GlobalSettings.EventLoopIdleRedrawMs);

        private double MsSinceLastPresent(long now)
            => _lastPresentTs == 0 ? double.PositiveInfinity : (now - _lastPresentTs) * 1000.0 / Stopwatch.Frequency;

        // A drain read bytes at ts: a change, and the oldest read not yet drawn starts the packet-to-present latency.
        private void NoteNetChange(long ts)
        {
            _changePending = true;

            if (_pendingNetTs == 0)
            {
                _pendingNetTs = ts;
            }
        }

        // Continuous motion the loop cannot see as a wake: mobile and boat steps (interpolation, completion, turns) and live
        // effects. Anything else that animates is covered by the idle redraw.
        private void RefreshChange()
        {
            long motion = StepSystem.Interpolated + StepSystem.Completed + StepSystem.Turns
                + BoatStepSystem.Interpolated + BoatStepSystem.Completed + BoatStepSystem.Turns;

            if (motion != _motionSeen)
            {
                _motionSeen = motion;
                _changePending = _continuous = true;
            }

            if (UO.World is { InGame: true } world && !world.Effects.IsEmpty)
            {
                _changePending = _continuous = true;
            }
        }

        private bool PresentOnChangeDue(bool gridDue)
        {
            RefreshChange();
            long now = Stopwatch.GetTimestamp();
            bool due = LoopTiming.PresentDue(_changePending, MsSinceLastPresent(now), DisplayIntervalMs, IdleRedrawMs);
#if CUO_DEVTOOLS
            if (PresentChangeCheck.On)
                PresentChangeCheck.Tick(now, true, gridDue, due, _changePending, _displayPeriod);
#endif
            return due;
        }

        private double MsUntilPresentOnChange()
            => LoopTiming.MsUntilPresent(_changePending, MsSinceLastPresent(Stopwatch.GetTimestamp()), DisplayIntervalMs, IdleRedrawMs);

        // Draw start: what changed so far is in this frame; later changes stay pending for the next one. A frame drawn during
        // motion or effects keeps the next one due one interval later (the motion advances in Update, after the wait).
        private void BeginPresentedChange()
        {
            _drawnNetTs = _pendingNetTs;
            _pendingNetTs = 0;
            _changePending = _continuous;
            _continuous = false;
        }

        private void EndPresentedChange()
        {
            long now = Stopwatch.GetTimestamp();
            _lastPresentTs = now;

            if (_drawnNetTs != 0)
            {
                if (LoopStats.On)
                {
                    LoopStats.NoteNetPresent(_drawnNetTs, now);
                }

                _drawnNetTs = 0;
            }
#if CUO_DEVTOOLS
            if (PresentChangeCheck.On) PresentChangeCheck.Present(now);
#endif
        }

        // present_on_change: a non-blocking drain outside Update (before Present and before the wait), with the base caps; the
        // socket event is re-armed first so data left or arriving after it wakes the next wait. Sends are flushed after it.
        private void EventDrain()
        {
            long start = Stopwatch.GetTimestamp();
            _waiter.BindNet(NetClient.Socket);
            _parseSlice ??= ParseSlice;
            int packets = NetClient.Socket.Drain(_parseSlice, _netCapBytes, _netCapPackets, _netCapMs);
            int bytes = NetClient.Socket.LastDrainBytes;
            _lastDrainTs = start;

            if (LoopStats.On)
            {
                LoopStats.NoteDrain(start, Stopwatch.GetTimestamp(), bytes);
            }

            if (bytes > 0)
            {
                NoteNetChange(start);

                if (FrameStats.On)
                {
                    FrameStats.Add(FrameCounter.NetBytes, bytes);
                    FrameStats.Add(FrameCounter.NetPackets, packets);
                }
            }

            NetClient.Socket.Statistics.TotalPacketsReceived += (uint)packets;
            NetClient.Socket.Flush();
        }
    }
}
