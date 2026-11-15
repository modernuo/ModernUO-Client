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

        // mismatch runs logged per 5 s summary window, so a later phase is not hidden by an earlier one
        private const int Logged = 3;
        private static long _start, _frames, _wouldPresent, _wouldSkip, _mismatches, _runs, _logged, _totalMismatches;
        private static ulong _reference, _referenceBatch, _referenceWorld;
        private static long _referenceSprites;
        private static uint _referenceTicks;
        private static ChangeReason _referenceWhy;
        private static bool _haveReference, _inRun, _runLogged;
        private static long _runFrames, _runMismatches;
        private static bool _wouldDraw;
        private static ChangeReason _wouldWhy;
        private static long _dueAtDraw;

        internal static long TotalMismatches => _totalMismatches;

        /// <summary>Draw start: whether the new signal would present this frame, why (for the log), and the due tick the
        /// schedule held then (the logged next_due is the one after this frame).</summary>
        public static void BeginFrame(bool wouldPresent, ChangeReason why, long dueAtDraw)
        {
            _wouldDraw = wouldPresent;
            _wouldWhy = why;
            _dueAtDraw = dueAtDraw;
        }

        /// <summary>After the frame was submitted: its signature (batcher and GPU world parts) and sprite count.</summary>
        public static void EndFrame(long now, ulong batchSignature, ulong worldSignature, long sprites, bool switchOn, VisualSource continuous,
            VisualSource nextSource, long nextTick, uint ticks)
        {
            ulong signature = batchSignature * 31 ^ worldSignature;

            if (_start == 0)
            {
                _start = now;
            }

            _frames++;

            if (_wouldDraw || !_haveReference)
            {
                _wouldPresent++;

                if (_inRun && _runLogged)
                {
                    Log.Warn(FormattableString.Invariant(
                        $"[anim-due-check] run ended: frames={_runFrames} differing={_runMismatches} by={_wouldWhy} ticks={ticks}"));
                }

                _reference = signature;
                _referenceBatch = batchSignature;
                _referenceWorld = worldSignature;
                _referenceSprites = sprites;
                _referenceTicks = ticks;
                _referenceWhy = _wouldWhy;
                _haveReference = true;
                _inRun = false;
            }
            else
            {
                _wouldSkip++;

                if (_inRun)
                {
                    _runFrames++;
                }

                if (signature != _reference)
                {
                    _mismatches++;
                    _totalMismatches++;

                    if (!_inRun)
                    {
                        _runs++;
                        _inRun = true;
                        _runFrames = 1;
                        _runMismatches = 0;
                        _runLogged = _logged++ < Logged;

                        if (_runLogged)
                        {
                            Log.Warn(FormattableString.Invariant(
                                $"[anim-due-check] skipped frame differs from the last presented one: ticks={ticks} due_at_draw={(_dueAtDraw == long.MaxValue ? -1 : _dueAtDraw)} next_due={(nextTick == long.MaxValue ? -1 : nextTick)} next_source={nextSource} continuous={continuous} sprites={sprites} ref_ticks={_referenceTicks} ref_by={_referenceWhy} ref_sprites={_referenceSprites} batch_differs={batchSignature != _referenceBatch} world_differs={worldSignature != _referenceWorld}"));
                        }
                    }

                    _runMismatches++;
                }
            }

            if (now - _start >= Stopwatch.Frequency * 5)
            {
                double sec = (now - _start) / (double)Stopwatch.Frequency;
                Log.Info(FormattableString.Invariant(
                    $"[anim-due-check] switch={switchOn} sec={sec:F3} frames={_frames} would_present={_wouldPresent} would_skip={_wouldSkip} would_present/s={_wouldPresent / sec:F1} mismatches={_mismatches} mismatch_runs={_runs} last_why={_wouldWhy}"));
                _start = now;
                _frames = _wouldPresent = _wouldSkip = _mismatches = _runs = _logged = 0;
            }
        }
    }
}
#endif
