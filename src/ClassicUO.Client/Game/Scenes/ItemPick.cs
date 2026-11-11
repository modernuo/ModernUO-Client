// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// pa-itemprep (perf audit #7), item_pick_early_reject (default off): an art item's pick test (ItemView.PickArt) rejects
    /// the mouse before the stored draw offset is looked up (SpritePickTable.TryItemArt, the art entry) when the mouse is
    /// outside both boxes the test can use for the drawn graphic's pack sprite (OrigW x OrigH): the box of a sized entry
    /// (every writer stores PickEntryFromSize of that size; Item.OutsidePickBounds, the stack's 5 px included) and the
    /// mask itself at the unshifted position (an entry not sized yet: the pixel test at 0, 0 and the stack's at 5, 5).
    /// Rejected keeps the old reject's side effects. A graphic the pack lacks keeps the full test. CUO_ITEM_PICK_CHECK=1
    /// runs the full test at every pick, returns its answer, and counts an early reject that it hit.
    /// </summary>
    internal static class ItemPick
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ITEM_PICK_EARLY_REJECT=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ITEM_PICK_EARLY_REJECT") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_ITEM_PICK_CHECK=1
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_ITEM_PICK_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.ItemPickEarlyReject;

        /// <summary>
        /// The mouse misses the art <paramref name="graphic"/> drawn for <paramref name="item"/> whatever draw offset the
        /// full test finds. False when the pack has no size for the graphic.
        /// </summary>
        public static bool Outside(Item item, ushort graphic)
        {
            SpriteEntry[] sprites = Client.Game.UO.FileManager.Arts.PackSpriteEntries;
            uint idx = graphic + 0x4000u;

            if (sprites == null || idx >= (uint)sprites.Length)
            {
                return false;
            }

            ref readonly SpriteEntry s = ref sprites[idx];

            if (s.OrigW == 0 || s.OrigH == 0)
            {
                return false;
            }

            // the full test's position before the entry is subtracted (ItemView.PickArt)
            Point position = item.RealScreenPosition;
            Vector3 offset = ItemOffsets.Read(item);
            int dx = SelectedObject.TranslatedMousePositionByViewport.X - (position.X + (int)offset.X);
            int dy = SelectedObject.TranslatedMousePositionByViewport.Y - (position.Y + (int)(offset.Y + offset.Z));

            return OutsideArt(s.OrigW, s.OrigH, dx, dy);
        }

        /// <summary>
        /// The mouse at (<paramref name="dx"/>, <paramref name="dy"/>) from the item's anchor misses art of
        /// <paramref name="width"/> x <paramref name="height"/>: outside the sized entry's reject box and outside the mask
        /// at the unshifted position (with the stack's copy 5 px further).
        /// </summary>
        internal static bool OutsideArt(int width, int height, int dx, int dy)
        {
            Item.PickEntryFromSize(width, height, out int entryW, out int entryH);

            return Item.OutsidePickBounds(entryW, entryH, dx + entryW, dy + entryH)
                   && (dx < -5 || dy < -5 || dx >= width || dy >= height);
        }

        /// <summary>
        /// An early reject's side effects, the full test's for a mouse outside the art: the art requested on the first-sight
        /// workers, and an art entry without a draw offset given the pack size's (the value every writer stores).
        /// </summary>
        public static void Rejected(ushort graphic)
        {
            global::ClassicUO.Game.Map.FirstSightArt.RequestStatic(graphic, frames: false);

            if (!global::ClassicUO.Game.Map.Map.ArtWait)
            {
                return;
            }

            ref ArtEntry index = ref Client.Game.UO.FileManager.Arts.GetEntry(graphic + 0x4000);

            if ((index.Width | index.Height) == 0)
            {
                ref readonly SpriteEntry s = ref Client.Game.UO.FileManager.Arts.PackSpriteEntries[graphic + 0x4000];
                Item.PickEntryFromSize(s.OrigW, s.OrigH, out int entryW, out int entryH);
                index.Width = entryW;
                index.Height = entryH;
            }
        }

#if CUO_DEVTOOLS
        private static long _picks, _rejects, _mismatches, _at;
        private static string _sample;

        /// <summary>Tests: mismatches since start, early rejects since the last summary.</summary>
        internal static long Mismatches => _mismatches;
        internal static long Rejects => _rejects;

        /// <summary>Tests: back to the start state.</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            _picks = _rejects = _mismatches = _at = 0;
            _sample = null;
        }

        /// <summary>
        /// CUO_ITEM_PICK_CHECK at every art item pick: <paramref name="reject"/> (the early reject) must not drop a
        /// <paramref name="hit"/> of the full test on the same input. A summary every 5 s.
        /// </summary>
        public static void Judge(Item item, ushort graphic, bool reject, bool hit)
        {
            _picks++;

            if (reject)
            {
                _rejects++;

                if (hit)
                {
                    _mismatches++;
                    string s = $"item 0x{item.Serial:X8} graphic 0x{graphic:X4} at {item.X},{item.Y},{item.Z} screen {item.RealScreenPosition.X},{item.RealScreenPosition.Y} " +
                               $"mouse {SelectedObject.TranslatedMousePositionByViewport.X},{SelectedObject.TranslatedMousePositionByViewport.Y}: early reject, full test hit";
                    _sample ??= s;
                    Log.Warn($"[item-pick-check] mismatch: {s}");
                }
            }

            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[item-pick-check] switch {(Enabled ? "on" : "off")}: {_picks} picks, {_rejects} early rejects, {_mismatches} mismatches" +
                         (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                _picks = _rejects = 0;
            }
        }
#endif
    }
}
