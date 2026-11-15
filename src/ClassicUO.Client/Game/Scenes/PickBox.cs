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
    /// pick_early_reject (default off; perf-17 for statics as static_pick_early_reject, perf-19 for multi components and art
    /// items): the pick test rejects a mouse far from the object's anchor before it reads the graphic, the tile data or the
    /// art entry. With W x H the largest OrigW x OrigH of the pack's static art:
    /// <list type="bullet">
    /// <item>Static and Multi: the full test hits only inside the picker's OrigW x OrigH mask at the anchor (plus a multi's
    /// boat Offset) minus the stored draw offset ((OrigW &gt;&gt; 1) - 22, OrigH - 44; 0, 0 while a static's entry is unset),
    /// so inside [-W, W + 22) x [-H, H + 44) of it.</item>
    /// <item>Art item: the same masks at the anchor plus the boat offset, and a stack's copy 5 px further, so inside
    /// [-W - 5, W + 22) x [-H - 5, H + 44). Only with ArtWait: without it the full test may load an unsized entry's art
    /// synchronously, which a reject would not.</item>
    /// </list>
    /// The static and multi full tests have no side effects. An item's full test, outside its art, requests that art on the
    /// first-sight workers and sizes an unsized art entry from the pack; the draw of an item it draws looks the art up anyway
    /// (taking a decoded result or decoding it), and the full test sizes the entry when the mouse comes near, so a reject
    /// only moves that work. CUO_PICK_EARLY_REJECT_CHECK=1 runs the full test at every pick and counts an early reject that
    /// it hit (and, for items, one whose art was not resident).
    /// </summary>
    internal static class PickBox
    {
        internal enum Kind
        {
            Static,
            Multi,
            Item
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

        /// <summary>This fill's art item picks go through <see cref="RejectsItem(Item)"/> (<see cref="Active"/> and ArtWait).</summary>
        public static bool ItemActive;

        /// <summary>This fill's switch: an early reject returns a miss (with only the checker on, the full test's answer).</summary>
        public static bool RejectOn;

#if CUO_DEVTOOLS
        /// <summary>This fill runs the checker: the full test runs after an early reject too.</summary>
        public static bool Checking;
#endif

        // the static box around the anchor: [-_maxW, _maxW + 22) x [-_maxH, _maxH + 44), as unsigned spans; the item box
        // reaches 5 px further up and left (a stack's copy)
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
            ItemActive = Active && global::ClassicUO.Game.Map.Map.ArtWait;
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

        /// <summary>The mouse at (<paramref name="dx"/>, <paramref name="dy"/>) from an art item's anchor plus its boat offset
        /// misses any static art there, a stack's copy included.</summary>
        public static bool RejectsItem(int dx, int dy)
            => (uint)(dx + _maxW + 5) >= _spanW + 5 || (uint)(dy + _maxH + 5) >= _spanH + 5;

        /// <summary><see cref="RejectsItem(int, int)"/> for <paramref name="item"/> at the viewport mouse.</summary>
        public static bool RejectsItem(Item item)
        {
            Point anchor = item.RealScreenPosition;
            Vector3 offset = ItemOffsets.Read(item);
            Point mouse = SelectedObject.TranslatedMousePositionByViewport;

            return RejectsItem(mouse.X - (anchor.X + (int)offset.X), mouse.Y - (anchor.Y + (int)(offset.Y + offset.Z)));
        }

#if CUO_DEVTOOLS
        private static readonly long[] _picks = new long[3], _rejects = new long[3];
        private static long _mismatches, _notResident, _at;
        private static string _sample;

        /// <summary>Tests: mismatches since start; early rejects of a kind and item rejects of art not resident since the
        /// last summary.</summary>
        internal static long Mismatches => _mismatches;
        internal static long EarlyRejects(Kind kind) => _rejects[(int)kind];
        internal static long NotResident => _notResident;

        /// <summary>Tests: back to the start state (the box kept).</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckOverrideForTests = null;
            Active = ItemActive = RejectOn = Checking = false;
            Array.Clear(_picks);
            Array.Clear(_rejects);
            _mismatches = _notResident = _at = 0;
            _sample = null;
        }

        /// <summary>
        /// CUO_PICK_EARLY_REJECT_CHECK at every pick that reaches the full test: <paramref name="reject"/> (the early
        /// reject) must not drop a <paramref name="hit"/> of the full test. <paramref name="notResident"/>: an item's drawn
        /// art was not in the atlas after the full test (the request a reject skips was pending; counted, not a mismatch).
        /// A summary every 5 s.
        /// </summary>
        public static void Judge(Kind kind, GameObject obj, ushort graphic, bool reject, bool hit, bool notResident = false)
        {
            _picks[(int)kind]++;

            if (reject)
            {
                _rejects[(int)kind]++;

                if (notResident)
                {
                    _notResident++;
                }

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
                Log.Info($"[pick-box-check] switch {(Enabled ? "on" : "off")} (box {_maxW} x {_maxH}, items {(ItemActive ? "on" : "off")}): " +
                         $"static {_picks[0]} picks {_rejects[0]} early rejects, multi {_picks[1]} picks {_rejects[1]} early rejects, " +
                         $"item {_picks[2]} picks {_rejects[2]} early rejects ({_notResident} of art not resident), {_mismatches} mismatches" +
                         (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                Array.Clear(_picks);
                Array.Clear(_rejects);
                _notResident = 0;
            }
        }
#endif
    }
}
