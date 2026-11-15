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
    /// pick_early_reject (default off; perf-17 for statics as static_pick_early_reject, perf-19 for multi components): the
    /// pick test rejects a mouse far from the object's anchor before it reads the graphic or the art entry. The full test
    /// hits only inside the picker's OrigW x OrigH mask at the anchor (plus a multi's boat Offset) minus the stored draw
    /// offset ((OrigW &gt;&gt; 1) - 22, OrigH - 44; 0, 0 while a static's entry is unset). With W x H the largest OrigW x
    /// OrigH of the pack's static art, that is inside [-W, W + 22) x [-H, H + 44) of it whatever the graphic. The full tests
    /// have no side effects, so a reject only skips work. CUO_PICK_EARLY_REJECT_CHECK=1 runs the full test at every pick and
    /// counts an early reject that it hit.
    /// </summary>
    internal static class PickBox
    {
        internal enum Kind
        {
            Static,
            Multi
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_PICK_EARLY_REJECT=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_PICK_EARLY_REJECT") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_PICK_EARLY_REJECT_CHECK=1
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_PICK_EARLY_REJECT_CHECK") == "1";

        /// <summary>Tests: the checker on (null: CUO_PICK_EARLY_REJECT_CHECK).</summary>
        internal static bool? CheckOverrideForTests;
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.PickEarlyReject;

        /// <summary>This fill's static and multi picks go through <see cref="Rejects"/> (the switch or its checker on, and
        /// the pack sized). Taken by <see cref="BeginFill"/>.</summary>
        public static bool Active;

        /// <summary>This fill's switch: an early reject returns a miss (with only the checker on, the full test's answer).</summary>
        public static bool RejectOn;

#if CUO_DEVTOOLS
        /// <summary>This fill runs the checker: the full test runs after an early reject too.</summary>
        public static bool Checking;
#endif

        // the box around the anchor: [-_maxW, _maxW + 22) x [-_maxH, _maxH + 44), as unsigned spans
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

        /// <summary>The mouse at (<paramref name="dx"/>, <paramref name="dy"/>) from a static's or multi component's anchor
        /// (its RealScreenPosition, plus a multi's boat Offset) misses any static art there.</summary>
        public static bool Rejects(int dx, int dy) => (uint)(dx + _maxW) >= _spanW || (uint)(dy + _maxH) >= _spanH;

#if CUO_DEVTOOLS
        private static readonly long[] _picks = new long[2], _rejects = new long[2];
        private static long _mismatches, _at;
        private static string _sample;

        /// <summary>Tests: mismatches since start; early rejects of a kind since the last summary.</summary>
        internal static long Mismatches => _mismatches;
        internal static long EarlyRejects(Kind kind) => _rejects[(int)kind];

        /// <summary>Tests: back to the start state (the box kept).</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckOverrideForTests = null;
            Active = RejectOn = Checking = false;
            Array.Clear(_picks);
            Array.Clear(_rejects);
            _mismatches = _at = 0;
            _sample = null;
        }

        /// <summary>
        /// CUO_PICK_EARLY_REJECT_CHECK at every pick that reaches the full test: <paramref name="reject"/> (the early
        /// reject) must not drop a <paramref name="hit"/> of the full test. A summary every 5 s.
        /// </summary>
        public static void Judge(Kind kind, GameObject obj, ushort graphic, bool reject, bool hit)
        {
            _picks[(int)kind]++;

            if (reject)
            {
                _rejects[(int)kind]++;

                if (hit)
                {
                    _mismatches++;
                    Point m = SelectedObject.TranslatedMousePositionByViewport;
                    string s = $"{kind} graphic 0x{graphic:X4} at {obj.X},{obj.Y},{obj.Z} screen {obj.RealScreenPosition.X},{obj.RealScreenPosition.Y} " +
                               $"mouse {m.X},{m.Y} box {_maxW},{_maxH}: early reject, full test hit";
                    _sample ??= s;
                    Log.Warn($"[pick-box-check] mismatch: {s}");
                }
            }

            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[pick-box-check] switch {(Enabled ? "on" : "off")} (box {_maxW} x {_maxH}): " +
                         $"static {_picks[0]} picks {_rejects[0]} early rejects, multi {_picks[1]} picks {_rejects[1]} early rejects, " +
                         $"{_mismatches} mismatches" +
                         (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                Array.Clear(_picks);
                Array.Clear(_rejects);
            }
        }
#endif
    }
}
