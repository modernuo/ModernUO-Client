// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// pick_skip_off_world: the world prepare makes no pick tests (CheckMouseSelection) in a frame whose mouse is not over the
    /// world (UIManager.IsMouseOverWorld false: over a gump, a modal, dragging a control, the SDL cursor hidden, outside the
    /// game viewport). GameScene.DrawUI clears such a frame's selection before anything outside the world draw reads it, so
    /// the selection the game sees is the same. What changes: the world draw no longer highlights the object under a gump
    /// (the old fill picked it and the draw tinted it until DrawUI cleared it). CUO_PICK_SKIP_CHECK=1 runs the old picking
    /// in every frame and judges the skip at DrawUI.
    /// </summary>
    internal static class PickSkip
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_PICK_SKIP_OFF_WORLD=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_PICK_SKIP_OFF_WORLD") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_PICK_SKIP_CHECK=1
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_PICK_SKIP_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.PickSkipOffWorld;

        /// <summary>This fill makes no pick tests (GameScene.CheckMouseSelectionTimed).</summary>
        public static bool Off;

        /// <summary>
        /// Start of a world fill: whether its pick tests are skipped. Under the checker they never are; the frame's skip
        /// decision is kept for the checker's Judge.
        /// </summary>
        public static void BeginFill(bool mouseOverWorld)
        {
            bool skip = Enabled && !mouseOverWorld;

#if CUO_DEVTOOLS
            if (CheckOverrideForTests ?? Check)
            {
                _wouldSkip = skip;
                _filled = true;
                skip = false;
            }
#endif

            Off = skip;
        }

#if CUO_DEVTOOLS
        private static bool _wouldSkip, _filled;
        private static long _frames, _skipped, _mismatches, _highlightOnly, _at;
        private static string _sample, _highlightSample;

        /// <summary>Tests: the checker on (null: CUO_PICK_SKIP_CHECK).</summary>
        internal static bool? CheckOverrideForTests;

        /// <summary>Tests: mismatches since start, draw highlights dropped since the last summary.</summary>
        internal static long Mismatches => _mismatches;
        internal static long HighlightOnly => _highlightOnly;

        /// <summary>Tests: back to the start state.</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckOverrideForTests = null;
            Off = _wouldSkip = _filled = false;
            _frames = _skipped = _mismatches = _highlightOnly = _at = 0;
            _sample = _highlightSample = null;
        }

        /// <summary>
        /// CUO_PICK_SKIP_CHECK, at DrawUI right after its clear (before world text picks): <paramref name="fillSelection"/> is
        /// what the (old) fill picked, <paramref name="finalSelection"/> what the game now sees. A frame the switch would skip must leave the game
        /// the same selection (a mismatch otherwise: the mouse moved over the world between the fill and DrawUI); the fill's
        /// own pick in such a frame is the draw highlight the switch drops (counted, not a mismatch).
        /// </summary>
        public static void Judge(BaseGameObject fillSelection, BaseGameObject finalSelection)
        {
            if (!_filled)
            {
                return;
            }

            _filled = false;
            _frames++;

            if (_wouldSkip)
            {
                _skipped++;

                // a skipped fill selects nothing, and DrawUI only ever clears: the game would see null
                if (finalSelection != null)
                {
                    _mismatches++;
                    string s = $"final selection old {Describe(finalSelection)}, skipped null (mouse over the world at DrawUI, not at the fill)";
                    _sample ??= s;
                    Log.Warn($"[pick-skip-check] mismatch: {s}");
                }

                if (fillSelection != null)
                {
                    _highlightOnly++;
                    _highlightSample ??= Describe(fillSelection);
                }
            }

            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[pick-skip-check] switch {(Enabled ? "on" : "off")}: {_frames} frames, {_skipped} would skip, {_mismatches} mismatches, " +
                         $"{_highlightOnly} draw-highlight-only" + (_highlightSample != null ? $" (first: {_highlightSample})" : "") +
                         (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                _frames = _skipped = _highlightOnly = 0;
                _highlightSample = null;
            }
        }

        private static string Describe(BaseGameObject o) => o switch
        {
            null => "null",
            Entity e => $"{e.GetType().Name} 0x{e.Serial:X8} graphic 0x{e.Graphic:X4} at {e.X},{e.Y},{e.Z}",
            GameObject g => $"{g.GetType().Name} graphic 0x{g.Graphic:X4} at {g.X},{g.Y},{g.Z}",
            _ => o.GetType().Name
        };
#endif
    }
}
