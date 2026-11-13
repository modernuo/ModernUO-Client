// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;
using ClassicUO.Configuration;
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
    }
}
