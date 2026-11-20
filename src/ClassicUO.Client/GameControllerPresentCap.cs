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

        // ---- vsync_mailbox ----
        private PresentModeKind _presentMode;
        internal PresentModeKind PresentMode => _presentMode;

        /// <summary>Sets the hint FNA3D reads at device creation and at every backbuffer reset from vsync_mailbox.</summary>
        internal static void ApplyMailboxHint() => SDL_SetHint(PresentModeRule.MailboxHint, Settings.GlobalSettings.VsyncMailbox ? "1" : "0");

        /// <summary>Recomputes the present mode after the device is created or reset, on the inputs FNA3D used.</summary>
        private void RefreshPresentMode()
        {
            var interval = GraphicsDevice.PresentationParameters.PresentationInterval;
            bool vsync = interval == Microsoft.Xna.Framework.Graphics.PresentInterval.One || interval == Microsoft.Xna.Framework.Graphics.PresentInterval.Default;
            bool sdlGpu = _gpuDevice != IntPtr.Zero;
            bool hint = sdlGpu && vsync && SDL_GetHintBoolean(PresentModeRule.MailboxHint, false);
            _presentMode = PresentModeRule.Choose(vsync, sdlGpu, hint,
                hint && SDL_WindowSupportsGPUPresentMode(_gpuDevice, Window.Handle, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX),
                sdlGpu && !vsync && SDL_WindowSupportsGPUPresentMode(_gpuDevice, Window.Handle, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE));
        }

        /// <summary>vsync_mailbox at run time (the session driver): a backbuffer reset makes FNA3D read the hint again.</summary>
        internal void SetVsyncMailbox(bool value)
        {
            if (Settings.GlobalSettings.VsyncMailbox == value)
            {
                return;
            }

            Settings.GlobalSettings.VsyncMailbox = value;
            ApplyMailboxHint();

            if (GraphicManager.SynchronizeWithVerticalRetrace)
            {
                GraphicsDevice.Reset();
            }
        }

        private bool DisplayDrawDue(bool inactive, bool minimized)
        {
            bool observe = false;
#if CUO_DEVTOOLS
            observe = PresentCapCheck.On;
#endif
            bool mailbox = _presentMode == PresentModeKind.Mailbox;

            if (!Settings.GlobalSettings.PresentAtDisplayRate && !mailbox && !observe)
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

            // vsync_present_on_change needs the event loop: the legacy loop does not wait on skipped frames, so with vsync it
            // would spin instead of blocking in Present. Mailbox runs the schedule on both loops, as vsync off does: its Present
            // never blocked, so the legacy loop spins on skipped frames either way (2ff7d0fb2)
            _displayCapActive = !minimized && _displayPeriod > 0 && DisplayRateSchedule.Enabled(
                Settings.GlobalSettings.PresentAtDisplayRate, Settings.GlobalSettings.FPS,
                GraphicManager.SynchronizeWithVerticalRetrace, mailbox, inactive,
                Settings.GlobalSettings.VsyncPresentOnChange && _tickMode == LoopMode.Event);
            bool due = _displaySchedule.Due(now, _displayCapActive ? _displayPeriod : 0);
#if CUO_DEVTOOLS
            if (PresentCapCheck.On)
                PresentCapCheck.Update(now, Settings.GlobalSettings.PresentAtDisplayRate, _displayCapActive, _displayHz, _displayPeriod);
#endif
            return due;
        }

        // ---- present-on-change (evloop-2) ----
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

        private long NextVisualTick => Math.Min(Math.Min(VisualDue.NextTick, _netEchoTick), AssetWake.FallbackTick);
#if CUO_DEVTOOLS
        private int _assetPublishedAtDraw;
#endif

        // asset_ready_wake: the frame is incomplete when the scene missed something visible with a load pending (Gpu), or any
        // worker decode (art, texmap, gump; animation directions on the Legacy world) is still pending at its end (Legacy draws
        // those as nothing). On the Gpu world the Legacy directions decoding are frame counts and picking masks, never drawn
        // (its mobiles wait on anim pages, CauseAnimPages): counted, they made every body change incomplete until drained,
        // and the directions the upload budget deferred sent no completion (bench-000973: 80 fallbacks in the bodies step).
        private void EndAssetFrame()
        {
            int pending = (UO.Arts?.PendingDecodes ?? 0) + (UO.Texmaps?.PendingDecodes ?? 0) + (UO.Gumps?.PendingDecodes ?? 0);
            bool animPending = !AnimFrameCount.GpuWorld && (UO.Animations?.PendingDirections ?? 0) != 0;
            bool incomplete = AssetWake.SceneIncomplete || pending != 0 || animPending;
            long resumeTick = long.MaxValue;

            if (animPending && UO.Animations.DeferredDrainTimestamp is var resume and not 0)
            {
                long ts = Stopwatch.GetTimestamp();
                resumeTick = Time.Ticks + Math.Max(1L, (resume - ts) * 1000 / Stopwatch.Frequency + 1);
            }
#if CUO_DEVTOOLS
            if (AssetWakeCheck.On)
            {
                int ready = (UO.Arts?.ReadyDecodes ?? 0) + (UO.Texmaps?.ReadyDecodes ?? 0) + (UO.Gumps?.ReadyDecodes ?? 0)
                    + (AnimFrameCount.GpuWorld ? 0 : UO.Animations?.ReadyDirections ?? 0);
                // ready items published before this draw started are completed loads the frame did not use
                int legacyStale = Math.Max(0, ready - (ClassicUO.Renderer.AssetSignal.PublishedCount - _assetPublishedAtDraw));
                AssetWakeCheck.EndDraw(incomplete, legacyStale + AssetWake.SceneStale,
                    AssetWake.SceneStale != 0 ? "Gpu pages (and Legacy decodes)" : "Legacy decodes",
                    AssetWake.SceneCauses | (pending != 0 ? AssetWake.CauseDecodes : 0) | (animPending ? AssetWake.CauseAnimDecodes : 0));
            }
#endif
            AssetWake.EndDraw(incomplete, Time.Ticks, resumeTick);
        }

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

            if (AssetWake.ChangeDue)
            {
                _changePending = true;
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
#if CUO_DEVTOOLS
            if (VsyncPresentCheck.On) VsyncPresentCheck.Draw(_continuous);
#endif
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

        // present-on-change: a non-blocking drain and dispatch outside Update, right after Present and before the wait (never
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
