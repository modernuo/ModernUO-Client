// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
// CLEANUP(release): checks present_on_change_anim_due's skipped frames against what the frame actually submits.
using System;
using System.Diagnostics;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Loop
{
    /// <summary>
    /// CUO_ANIM_DUE_CHECK=1 (with present_on_change): the loop keeps drawing at display rate (idle frames every interval), and
    /// each drawn frame is one present_on_change_anim_due would have presented (a change was pending: net, input, motion, a
    /// continuous source or a due visual tick) or skipped. Every frame is signed (the batcher's vertices, textures, transform
    /// and effect, and the GPU world pass's uniforms, chunk table, dynamic instances, overrides and translucent list). A skipped
    /// frame whose signature differs from the last frame the new signal would have presented is a mismatch: a change the
    /// signal would have missed. The first mismatches are logged with the sources and the due tick; a summary every 5 s
    /// gives the switch state, frames, would-present and would-skip counts, mismatches and mismatch runs.
    /// </summary>
    internal static class AnimDueCheck
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CUO_ANIM_DUE_CHECK") == "1";

        private const int Logged = 30;
        private static long _start, _frames, _wouldPresent, _wouldSkip, _mismatches, _runs, _logged, _totalMismatches;
        private static ulong _reference;
        private static bool _haveReference, _inRun;
        private static bool _wouldDraw;
        private static string _wouldWhy = "";
        private static long _dueAtDraw;

        internal static long TotalMismatches => _totalMismatches;

        /// <summary>Draw start: whether the new signal would present this frame, why (for the log), and the due tick the
        /// schedule held then (the logged next_due is the one after this frame).</summary>
        public static void BeginFrame(bool wouldPresent, string why, long dueAtDraw)
        {
            _wouldDraw = wouldPresent;
            _wouldWhy = why;
            _dueAtDraw = dueAtDraw;
        }

        /// <summary>After the frame was submitted: its signature and sprite count.</summary>
        public static void EndFrame(long now, ulong signature, long sprites, bool switchOn, VisualSource continuous, VisualSource nextSource,
            long nextTick, uint ticks)
        {
            if (_start == 0)
            {
                _start = now;
            }

            _frames++;

            if (_wouldDraw || !_haveReference)
            {
                _wouldPresent++;
                _reference = signature;
                _haveReference = true;
                _inRun = false;
            }
            else
            {
                _wouldSkip++;

                if (signature != _reference)
                {
                    _mismatches++;
                    _totalMismatches++;

                    if (!_inRun)
                    {
                        _runs++;
                        _inRun = true;

                        if (_logged++ < Logged)
                        {
                            Log.Warn(FormattableString.Invariant(
                                $"[anim-due-check] skipped frame differs from the last presented one: ticks={ticks} due_at_draw={(_dueAtDraw == long.MaxValue ? -1 : _dueAtDraw)} next_due={(nextTick == long.MaxValue ? -1 : nextTick)} next_source={nextSource} continuous={continuous} sprites={sprites}"));
                        }
                    }
                }
            }

            if (now - _start >= Stopwatch.Frequency * 5)
            {
                double sec = (now - _start) / (double)Stopwatch.Frequency;
                Log.Info(FormattableString.Invariant(
                    $"[anim-due-check] switch={switchOn} sec={sec:F3} frames={_frames} would_present={_wouldPresent} would_skip={_wouldSkip} would_present/s={_wouldPresent / sec:F1} mismatches={_mismatches} mismatch_runs={_runs} last_why={_wouldWhy}"));
                _start = now;
                _frames = _wouldPresent = _wouldSkip = _mismatches = _runs = 0;
            }
        }
    }
}
#endif
