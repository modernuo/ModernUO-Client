// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Game.GameObjects;
using Microsoft.Xna.Framework;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// pick_early_reject (always on; perf-17 for statics, perf-19 for multi components and art items): the pick test rejects a mouse far from the object's anchor before it reads the graphic, the tile data or the
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
    /// only moves that work. Baked in after quiet-1115.
    /// </summary>
    internal static class PickBox
    {
        /// <summary>This fill's static and multi picks go through <see cref="Rejects"/> (the pack sized). Taken by <see cref="BeginFill"/>.</summary>
        public static bool Active;

        /// <summary>This fill's art item picks go through <see cref="RejectsItem(Item)"/> (<see cref="Active"/> and ArtWait).</summary>
        public static bool ItemActive;

        // the static box around the anchor: [-_maxW, _maxW + 22) x [-_maxH, _maxH + 44), as unsigned spans; the item box
        // reaches 5 px further up and left (a stack's copy)
        private static int _maxW, _maxH;
        private static uint _spanW, _spanH;
        private static SpriteEntry[] _sizedFrom;

        /// <summary>Start of a world fill: sizes the box from the pack once.</summary>
        public static void BeginFill()
        {
            SpriteEntry[] sprites = Client.Game.UO.FileManager.Arts.PackSpriteEntries;

            if (!ReferenceEquals(sprites, _sizedFrom))
            {
                _sizedFrom = sprites;
                MaxStaticSize(sprites, out int w, out int h);
                SetBounds(w, h);
            }

            Active = _spanW != 0;
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
    }
}
