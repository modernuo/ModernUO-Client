// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// pa-itemprep (perf audit #3): the record inputs of the item branch of the world fill (AddTileToRenderList), read once
    /// per item: the graphic and, for an item that is neither a corpse nor a multi, whether it is locked (Item.IsLocked: not
    /// movable and its tile weighs more than 90). The tiledata row, the corpse test and the multi test follow from these
    /// locals. item_prep_rows (default off) reads both from the item's packed ItemDrawRow (one record lookup); off, each is
    /// read once through its getter. Without a world record (the house placement preview, a spell entry, a destroyed or
    /// stale instance) the getters are read either way. CUO_ITEM_PREP_CHECK=1 compares the values the fill uses with the
    /// old getters (Item.ItemData, IsCorpse, IsLocked) at every use, and also runs the row read when the switch is off.
    /// </summary>
    internal static class ItemPrep
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_ITEM_PREP_ROWS=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ITEM_PREP_ROWS") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_ITEM_PREP_CHECK=1
        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_ITEM_PREP_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings.ItemPrepRows;

        /// <summary>
        /// The item's graphic (Item.Graphic) and whether the fill treats it as locked: false for a corpse or a multi (the fill
        /// never asks them), else Item.IsLocked. <paramref name="rows"/>: from the record's ItemDrawRow when the item has a
        /// world record.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Read(Item item, StaticTiles[] tiles, bool rows, out bool locked)
        {
            if (rows && item.TryScalarEntity(out Friflo.Engine.ECS.Entity e))
            {
                ref readonly ClassicUO.Ecs.ItemDrawRow r = ref ClassicUO.Ecs.EcsRead.Get<ClassicUO.Ecs.ItemDrawRow>(e);
                ushort g = r.Graphic;
                locked = g != Item.CORPSE_GRAPHIC && !item.IsMulti && ((Flags)r.Flags & Flags.Movable) == 0 && tiles[g].Weight > 90;

                return g;
            }

            return ReadGetters(item, tiles, out locked);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadGetters(Item item, StaticTiles[] tiles, out bool locked)
        {
            ushort graphic = item.Graphic;
            locked = graphic != Item.CORPSE_GRAPHIC && !item.IsMulti && (item.Flags & Flags.Movable) == 0 && tiles[graphic].Weight > 90;

            return graphic;
        }

#if CUO_DEVTOOLS
        private static long _items, _recordless, _mismatches, _at;
        private static string _sample;

        /// <summary>Tests: mismatches since start.</summary>
        internal static long Mismatches => _mismatches;

        /// <summary>Tests: the summary clock and the counts back to the start.</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            _items = _recordless = _mismatches = _at = 0;
            _sample = null;
        }

        /// <summary>
        /// CUO_ITEM_PREP_CHECK at every use: <paramref name="graphic"/> and <paramref name="locked"/> (what the fill uses) and
        /// the row read must give the old getters' tiledata row, corpse and locked answers. A mismatch logs the item and both
        /// values; a summary every 5 s.
        /// </summary>
        public static void Compare(Item item, StaticTiles[] tiles, ushort graphic, bool locked)
        {
            _items++;

            if (!item.TryScalarEntity(out _))
            {
                _recordless++;
            }

            // the old getters: IsCorpse; ItemData's row (IsMulti ? MultiGraphic : Graphic) of the tiledata the fill passes;
            // IsLocked, asked only for an item that is neither a corpse nor a multi
            bool oldCorpse = item.IsCorpse;
            bool oldMulti = item.IsMulti;
            ushort oldTile = oldMulti ? item.MultiGraphic : item.Graphic;
            bool oldLocked = !oldCorpse && !oldMulti && (item.Flags & Flags.Movable) == 0 && tiles[oldTile].Weight > 90;

            ushort rowGraphic = Read(item, tiles, true, out bool rowLocked);

            Judge(item, "used", graphic, locked, oldCorpse, oldMulti, oldTile, oldLocked);
            Judge(item, "row", rowGraphic, rowLocked, oldCorpse, oldMulti, oldTile, oldLocked);

            long now = Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;
            }
            else if (now - _at >= Stopwatch.Frequency * 5)
            {
                Log.Info($"[item-prep-check] switch {(Enabled ? "on" : "off")}: {_items} items, {_recordless} recordless, {_mismatches} mismatches" +
                         (_sample != null ? $"; first mismatch: {_sample}" : ""));
                _at = now;
                _items = _recordless = 0;
            }
        }

        private static void Judge(Item item, string what, ushort graphic, bool locked, bool oldCorpse, bool oldMulti, ushort oldTile, bool oldLocked)
        {
            bool corpse = graphic == Item.CORPSE_GRAPHIC;
            bool sameTile = (oldMulti ? item.MultiGraphic : graphic) == oldTile;

            if (corpse == oldCorpse && locked == oldLocked && sameTile)
            {
                return;
            }

            _mismatches++;
            string s = $"{what}: item 0x{item.Serial:X8} at {item.X},{item.Y},{item.Z} graphic 0x{graphic:X4} (old 0x{item.Graphic:X4}) multi {oldMulti} " +
                       $"corpse {corpse}/{oldCorpse} locked {locked}/{oldLocked} tile 0x{oldTile:X4} {(sameTile ? "same" : "differs")}";
            _sample ??= s;
            Log.Warn($"[item-prep-check] mismatch {s}");
        }
#endif
    }
}
