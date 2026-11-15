// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// perf-17, static_pick_early_reject (default off): a static's pick test (Static.CheckMouseSelection) rejects a mouse
    /// that is far from the static's anchor before it reads the graphic, the tree filter or the art entry. The full test hits
    /// only inside the picker's mask, OrigW x OrigH of the art, placed at the anchor minus the art entry's stored draw offset
    /// ((OrigW &gt;&gt; 1) - 22, OrigH - 44; 0, 0 while unset). With W and H the largest OrigW and OrigH of the pack's static
    /// art, that is inside [-W, W + 22) x [-H, H + 44) of the anchor whatever entry and mask the graphic has. The full test
    /// has no side effects, so a reject only skips work. CUO_STATIC_PICK_CHECK=1 runs the full test at every pick and
    /// counts an early reject that it hit.
    /// </summary>
    internal static class StaticPick
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_STATIC_PICK_EARLY_REJECT=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_STATIC_PICK_EARLY_REJECT") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_STATIC_PICK_CHECK=1
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_STATIC_PICK_CHECK") == "1";

        /// <summary>Tests: the checker on (null: CUO_STATIC_PICK_CHECK).</summary>
        internal static bool? CheckOverrideForTests;
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.StaticPickEarlyReject;

        /// <summary>This fill's static picks go through <see cref="Rejects"/> (the switch or its checker on, and the pack
        /// sized). Taken by <see cref="BeginFill"/>.</summary>
        public static bool Active;

        /// <summary>This fill's switch: an early reject returns a miss (with only the checker on, the full test's answer).</summary>
        public static bool RejectOn;

#if CUO_DEVTOOLS
        /// <summary>This fill runs the checker: the full test runs after an early reject too.</summary>
        public static bool Checking;
#endif

        // the reject box around the anchor: [-_maxW, _maxW + 22) x [-_maxH, _maxH + 44), as unsigned spans
        private static int _maxW, _maxH;
        private static uint _spanW, _spanH;
        private static SpriteEntry[] _sizedFrom;

        /// <summary>Start of a world fill: takes the switch and its checker, and sizes the box from the pack once.</summary>
        public static void BeginFill()
        {
            bool on = RejectOn = Enabled;
#if CUO_DEVTOOLS
            Checking = CheckOverrideForTests ?? Check;
            on |= Checking;
#endif

            if (on)
            {
                SpriteEntry[] sprites = Client.Game.UO.FileManager.Arts.PackSpriteEntries;

                if (!ReferenceEquals(sprites, _sizedFrom))
                {
                    _sizedFrom = sprites;
                    MaxStaticSize(sprites, out int w, out int h);
                    SetBounds(w, h);
                }
            }

            Active = on && _spanW != 0;
        }

        /// <summary>The largest OrigW and OrigH of the static art (sprites 0x4000 on); 0, 0 without a table.</summary>
        internal static void MaxStaticSize(SpriteEntry[] sprites, out int width, out int height)
        {
            width = height = 0;

            if (sprites == null)
            {
                return;
            }

            for (int i = 0x4000; i < sprites.Length; i++)
            {
                width = Math.Max(width, (int)sprites[i].OrigW);
                height = Math.Max(height, (int)sprites[i].OrigH);
            }
        }

        /// <summary>The box for static art of at most <paramref name="maxW"/> x <paramref name="maxH"/> (none at 0).</summary>
        internal static void SetBounds(int maxW, int maxH)
        {
            if (maxW <= 0 || maxH <= 0)
            {
                _maxW = _maxH = 0;
                _spanW = _spanH = 0;

                return;
            }

            _maxW = maxW;
            _maxH = maxH;
            _spanW = (uint)(2 * maxW + 22);
            _spanH = (uint)(2 * maxH + 44);
        }

        /// <summary>The mouse at (<paramref name="dx"/>, <paramref name="dy"/>) from a static's anchor (its
        /// RealScreenPosition) misses any static art there.</summary>
        public static bool Rejects(int dx, int dy) => (uint)(dx + _maxW) >= _spanW || (uint)(dy + _maxH) >= _spanH;

#if CUO_DEVTOOLS
        private static long _picks, _rejects, _mismatches, _at;
        private static string _sample;

        /// <summary>Tests: mismatches since start, early rejects since the last summary.</summary>
        internal static long Mismatches => _mismatches;
        internal static long EarlyRejects => _rejects;

        /// <summary>Tests: back to the start state (the box kept).</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckOverrideForTests = null;
            Active = RejectOn = Checking = false;
            _picks = _rejects = _mismatches = _at = 0;
            _sample = null;
        }

        /// <summary>
        /// CUO_STATIC_PICK_CHECK at every static pick that reaches the full test: <paramref name="reject"/> (the early
        /// reject) must not drop a <paramref name="hit"/> of the full test. A summary every 5 s.
        /// </summary>
        public static void Judge(Static st, ushort graphic, bool reject, bool hit)
        {
            _picks++;

            if (reject)
            {
                _rejects++;

                if (hit)
                {
                    _mismatches++;
                    Point m = SelectedObject.TranslatedMousePositionByViewport;
                    string s = $"static graphic 0x{graphic:X4} at {st.X},{st.Y},{st.Z} screen {st.RealScreenPosition.X},{st.RealScreenPosition.Y} " +
                               $"mouse {m.X},{m.Y} box {_maxW},{_maxH}: early reject, full test hit";
                    _sample ??= s;
                    Log.Warn($"[static-pick-check] mismatch: {s}");
                }
            }

            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[static-pick-check] switch {(Enabled ? "on" : "off")}: {_picks} picks, {_rejects} early rejects " +
                         $"(box {_maxW} x {_maxH}), {_mismatches} mismatches" + (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                _picks = _rejects = 0;
            }
        }
#endif
    }
}
