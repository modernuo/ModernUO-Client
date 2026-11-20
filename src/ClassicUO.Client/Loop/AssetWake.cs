// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Threading;
using ClassicUO.Configuration;

namespace ClassicUO.Loop
{
    /// <summary>
    /// asset_ready_wake (gpu-popin-2): under present on change, a frame that drew a missing sprite or chunk scheduled no
    /// further frame, so the sprite appeared only with the next unrelated change (0.25 s to seconds). Now a frame presented
    /// incomplete while a load is pending arms the wake: every worker completion (Legacy decodes through
    /// <see cref="ClassicUO.Renderer.AssetSignal"/>, Gpu page loads and chunk bakes through Uo.Render's WorkerPool) ends the
    /// event-loop wait and is a visual change, so the next presentable frame uploads and draws it. If no completion comes
    /// (a load that never started), <see cref="FallbackMs"/> after the incomplete present is a visual due. Nothing changes
    /// while frames are complete. Main thread except <see cref="Signal"/>. No allocation.
    /// </summary>
    internal static class AssetWake
    {
        /// <summary>The fallback present after an incomplete one when no completion arrived (two 60 Hz intervals).</summary>
        public const int FallbackMs = 33;

#if CUO_DEVTOOLS
        // the gate's override of the setting (CUO_ASSET_READY_WAKE=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ASSET_READY_WAKE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.AssetReadyWake ?? false);

        private static readonly Action _signal = Signal;
        private static LoopWaiter _waiter;
        private static int _completions;
        private static long _firstSignalTs;
        private static volatile bool _armed, _wake;

        // main thread
        private static bool _on, _listening, _incomplete;
        private static int _seen;
        private static long _fallbackTick = long.MaxValue;

        /// <summary>The scene's draw: this frame missed a visible sprite or chunk while a load is pending (cleared at each draw start).</summary>
        public static bool SceneIncomplete;

        /// <summary>The scene's draw: Gpu pages whose load completed before this frame and that it still drew as nothing.</summary>
        public static int SceneStale;

        /// <summary>What an incomplete frame waits for (CUO_ASSET_WAKE_CHECK names the cause of each fallback present):
        /// Gpu chunk bakes or uploads, Gpu art or animation pages, client chunks the draw walk asked for, a held look, Legacy
        /// worker decodes.</summary>
        public const int CauseChunks = 1, CausePages = 2, CauseAnimPages = 4, CauseMapDraw = 8, CauseHeld = 16, CauseDecodes = 32;

        /// <summary>The scene's draw: the <c>Cause*</c> bits of this frame (cleared at each draw start).</summary>
        public static int SceneCauses;

        /// <summary>Completions so far (any thread; wraps).</summary>
        public static int Completions => Volatile.Read(ref _completions);

        /// <summary>The last presented frame was incomplete.</summary>
        public static bool LastIncomplete => _incomplete;

        /// <summary>A visual due for the fallback present (long.MaxValue: none).</summary>
        public static long FallbackTick => _fallbackTick;

        /// <summary>A completion arrived since the incomplete frame's draw started: present it.</summary>
        public static bool ChangeDue => _on && _incomplete && Volatile.Read(ref _completions) != _seen;

        public static void SetWaiter(LoopWaiter waiter) => _waiter = waiter;

        /// <summary>Any thread: a worker load completed.</summary>
        public static void Signal()
        {
            Interlocked.Increment(ref _completions);

            if (_armed)
            {
                if (Volatile.Read(ref _firstSignalTs) == 0)
                {
                    Interlocked.CompareExchange(ref _firstSignalTs, Stopwatch.GetTimestamp(), 0);
                }

                if (_wake)
                {
                    _waiter?.Wake();
                }
            }
        }

        /// <summary>Draw start: what completed so far is in this frame. Returns the first completion's time after the last
        /// incomplete present (Stopwatch ticks; 0: none).</summary>
        public static long BeginDraw()
        {
            _on = Enabled;
            bool listen = _on || AssetWakeCheck.On;

            if (listen != _listening)
            {
                _listening = listen;
                Volatile.Write(ref ClassicUO.Renderer.AssetSignal.Ready, listen ? _signal : null);
                Volatile.Write(ref Uo.Render.WorkerPool.JobCompleted, listen ? _signal : null);
                Volatile.Write(ref ClassicUO.Assets.MapLoader.RegionLoaded, listen ? _signal : null);
            }

            _wake = _on;
            _seen = Volatile.Read(ref _completions);
            SceneIncomplete = false;
            SceneStale = 0;
            SceneCauses = 0;

            return Interlocked.Exchange(ref _firstSignalTs, 0);
        }

        /// <summary>Draw end (before the present): whether the frame is incomplete; arms the wake and the fallback.</summary>
        public static void EndDraw(bool incomplete, long nowTick)
        {
            _incomplete = incomplete;
            _fallbackTick = incomplete && _on ? nowTick + FallbackMs : long.MaxValue;
            _armed = incomplete && _listening;
        }

        /// <summary>Tests: back to the initial state.</summary>
        internal static void ResetForTests()
        {
            _armed = _wake = _on = _listening = _incomplete = false;
            _seen = 0;
            _completions = 0;
            _firstSignalTs = 0;
            _fallbackTick = long.MaxValue;
            _waiter = null;
            Volatile.Write(ref ClassicUO.Renderer.AssetSignal.Ready, null);
            Volatile.Write(ref Uo.Render.WorkerPool.JobCompleted, null);
            Volatile.Write(ref ClassicUO.Assets.MapLoader.RegionLoaded, null);
        }
    }
}
