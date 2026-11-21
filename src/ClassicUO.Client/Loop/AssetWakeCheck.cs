// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    /// <summary>
    /// CUO_ASSET_WAKE_CHECK=1 (CLEANUP(release)): asset_ready_wake's checker, both switch states. Per presented frame:
    /// "stale" = a load that completed before the frame started is still drawn as nothing at its end (Gpu: a loaded page the
    /// frame touched but did not place; Legacy: a decode published before the draw and not uploaded by its end); "late" = the
    /// first completion after an incomplete present was presented more than one display interval (plus 2 ms) later. Both
    /// should be 0 with the switch on. Every 5 s: a summary with the switch state; the first mismatches are logged one by one.
    /// A fallback present (no completion came) is a completion signal missing: it is counted per cause of the incomplete
    /// frame before it (AssetWake.Cause*), so the source that does not signal is named.
    /// </summary>
    internal static class AssetWakeCheck
    {
#if CUO_DEVTOOLS
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_ASSET_WAKE_CHECK") == "1";

        private const int DetailLogs = 20;
        private static long _start, _frames, _incomplete, _stale, _late, _waits, _maxLateTicks, _latencySum, _latencyCount, _fallbacks, _scheduled;
        private static int _logged, _lastCauses;
        private static bool _switch;

        /// <summary>DevBench plan: one summary per plan step (<see cref="Step"/>) instead of every 5 s.</summary>
        public static bool StepWindows;

        /// <summary>DevBench: the window ends; <paramref name="tag"/> null discards it (a step starts), else it is printed.</summary>
        public static void Step(string tag)
        {
            StepWindows = true;

            if (tag != null)
            {
                Print(" step=" + tag);
            }

            Reset(Stopwatch.GetTimestamp(), _switch);
        }

        private static void Print(string step)
        {
            double meanMs = _latencyCount == 0 ? 0 : _latencySum * 1000.0 / Stopwatch.Frequency / _latencyCount;
            Log.Info(FormattableString.Invariant(
                $"[asset-wake-check]{step} switch={(_switch ? "on" : "off")} frames={_frames} incomplete={_incomplete} stale={_stale} late={_late} completions_waited={_waits} mean_ms={meanMs:F1} max_late_ms={_maxLateTicks * 1000.0 / Stopwatch.Frequency:F1} fallbacks={_fallbacks} scheduled={_scheduled} mismatches={_stale + _late}") + FallbackCauses());
        }
        private static readonly long[] _fallbackCause = new long[7];
        private static readonly string[] _causeNames = { "chunks", "pages", "anim_pages", "map_draw", "held", "decodes", "anim_decodes" };

        /// <summary>Whether a completion at <paramref name="signalTs"/> presented at <paramref name="drawTs"/> is late.</summary>
        public static bool IsLate(long signalTs, long drawTs, double intervalMs, long frequency)
            => signalTs != 0 && (drawTs - signalTs) * 1000.0 / frequency > intervalMs + 2.0;

        /// <summary>Draw start: <paramref name="signalTs"/> = the first completion after the last incomplete present (0: none).</summary>
        /// <paramref name="scheduled"/>: presented at the known resume time of budget-deferred uploads (not a fallback).
        public static void Draw(long signalTs, long drawTs, double intervalMs, bool on, bool fallback, bool scheduled = false)
        {
            if (_start == 0 || on != _switch)
            {
                Reset(drawTs, on);
            }

            if (scheduled)
            {
                _scheduled++;
            }

            if (fallback)
            {
                _fallbacks++;

                for (int b = 0; b < _fallbackCause.Length; b++)
                {
                    if ((_lastCauses & (1 << b)) != 0)
                    {
                        _fallbackCause[b]++;
                    }
                }
            }

            if (signalTs == 0)
            {
                return;
            }

            long ticks = drawTs - signalTs;
            _waits++;
            _latencySum += ticks;
            _latencyCount++;

            if (IsLate(signalTs, drawTs, intervalMs, Stopwatch.Frequency))
            {
                _late++;
                _maxLateTicks = Math.Max(_maxLateTicks, ticks);

                if (_logged++ < DetailLogs)
                {
                    Log.Warn(FormattableString.Invariant($"[asset-wake-check] late: a load completed {ticks * 1000.0 / Stopwatch.Frequency:F1} ms before this frame (display interval {intervalMs:F1} ms), switch={(on ? "on" : "off")}"));
                }
            }
        }

        /// <summary>Draw end: the frame's state.</summary>
        public static void EndDraw(bool incomplete, int stale, string what, int causes)
        {
            _frames++;
            _lastCauses = incomplete ? causes : 0;

            if (incomplete)
            {
                _incomplete++;
            }

            if (stale > 0)
            {
                _stale++;

                if (_logged++ < DetailLogs)
                {
                    Log.Warn($"[asset-wake-check] stale: {stale} {what} completed before this frame and still drawn as nothing, switch={(_switch ? "on" : "off")}");
                }
            }

            long now = Stopwatch.GetTimestamp();

            if (!StepWindows && now - _start >= Stopwatch.Frequency * 5)
            {
                Print("");
                Reset(now, _switch);
            }
        }

        private static string FallbackCauses()
        {
            if (_fallbacks == 0)
            {
                return "";
            }

            var sb = new System.Text.StringBuilder(" fallback_causes");

            for (int b = 0; b < _fallbackCause.Length; b++)
            {
                sb.Append(' ').Append(_causeNames[b]).Append('=').Append(_fallbackCause[b]);
            }

            return sb.ToString();
        }

        private static void Reset(long now, bool on)
        {
            Array.Clear(_fallbackCause);

            _start = now;
            _switch = on;
            _frames = _incomplete = _stale = _late = _waits = _maxLateTicks = _latencySum = _latencyCount = _fallbacks = _scheduled = 0;
        }
#else
        public const bool On = false;
#endif
    }
}
