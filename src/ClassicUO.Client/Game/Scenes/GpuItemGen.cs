// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// pa-gpusig (gpu_prepare_item_cache_gen, perf audit 1111 #4): the Gpu item cache (GameSceneGpuItemCache) trusts a served
    /// tile without rebuilding its items' signatures while
    /// <list type="bullet">
    /// <item>the chunk's render generation (<see cref="Chunk.GpuItemRenderGen"/>) is the one the cache was built at:
    /// <see cref="Touch"/> bumps it from every in-place change the signature reads that the tile lists don't report (the item
    /// record writes of WorldEcs.CommitItem and SyncItem, ItemOffsets.Push, GameObject.AddMessage, the object-handle
    /// transitions outside the walk, Item.CheckGraphicChange); joins, leaves and destroys set GpuItemsDirty as before;</item>
    /// <item>the tile was served in every prepare pass since it was last checked: a tile left to the walk (under the mouse,
    /// the selected container, a check pass, culled) or a cache not prepared in a pass may have had its alpha or handles
    /// changed by the walk, so its signatures are compared once (the poll) the next time it is served;</item>
    /// <item>with NoColorObjectsOutOfRange, the player's range key is the one at the build; else each entry's range bit is
    /// recomputed from the position in its signature.</item>
    /// </list>
    /// CUO_GPU_SIG_CHECK=1 also rebuilds the signatures at every trusted use and logs each item whose signature changed
    /// (the field), with a summary every 5 s.
    /// </summary>
    internal static class GpuItemGen
    {
#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_GPU_ITEM_GEN=1|0)
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_GPU_ITEM_GEN") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        // CLEANUP(release): CUO_GPU_SIG_CHECK=1
        public static readonly bool CheckEnv = Environment.GetEnvironmentVariable("CUO_GPU_SIG_CHECK") == "1";

        /// <summary>Tests: the checker on (null: CUO_GPU_SIG_CHECK).</summary>
        internal static bool? CheckOverrideForTests;

        public static bool Check => CheckOverrideForTests ?? CheckEnv;
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? Settings.GlobalSettings?.GpuPrepareItemCacheGen ?? false;

        /// <summary>The signature's out-of-range bit (GameScene.GpuItemSignature, state bit 32 of its second half).</summary>
        public const ulong RANGE_BIT = 32UL << 56;

        /// <summary>
        /// The object changed in a way its chunk's cached item sprites may read: the chunk's render generation moves, so the
        /// cache is re-extracted (the prepare) or the tile walked (a change after the prepare). Nothing for an object off the
        /// map.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Touch(GameObject obj)
        {
            Chunk chunk = obj.TileChunk;

            if (chunk != null)
            {
                chunk.GpuItemRenderGen++;
#if CUO_DEVTOOLS
                _invalidations++;
#endif
            }
        }

        /// <summary>What <see cref="GameObject.Distance"/> compares with the view range: the range centre and the range, or
        /// ulong.MaxValue without a player (every item out of range).</summary>
        public static ulong RangeKey(World world)
        {
            if (world.Player == null)
            {
                return ulong.MaxValue;
            }

            return (ushort)world.RangeSize.X | (ulong)(ushort)world.RangeSize.Y << 16 | (ulong)(uint)world.ClientViewRange << 32;
        }

        /// <summary>The signature's out-of-range bit for the position it holds under <paramref name="rangeKey"/>
        /// (<see cref="GameObject.Distance"/> &gt; ClientViewRange).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool OutOfRange(ulong sig1, ulong rangeKey)
        {
            if (rangeKey == ulong.MaxValue)
            {
                return true;
            }

            int x = (ushort)sig1, y = (ushort)(sig1 >> 16);
            int fx = (ushort)rangeKey, fy = (ushort)(rangeKey >> 16), range = (int)(uint)(rangeKey >> 32);

            return Math.Max(Math.Abs(x - fx), Math.Abs(y - fy)) > range;
        }

        /// <summary>How a served tile's cached items are checked.</summary>
        internal enum TileCheck : byte
        {
            /// <summary>Unchanged: no signature is rebuilt.</summary>
            Trust,
            /// <summary>Rebuild and compare every entry's signature (the switch off, or the walk had the tile since).</summary>
            Signatures,
            /// <summary>Changed: the tile is walked and the chunk re-extracted.</summary>
            Fallback
        }

        /// <summary>
        /// The decision for served tile <paramref name="tile"/> of <paramref name="cache"/>: with the switch off always the
        /// signatures; on, a fallback when the chunk's render generation moved after the build (a change after the prepare),
        /// the signatures for a tile the walk may have had or a chunk marked for re-extraction since the prepare, a fallback
        /// when an entry's range bit flipped, else trust.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TileCheck Decide(bool on, Chunk chunk, GpuItemCache cache, int tile, bool rangeOn, ulong rangeKey)
        {
            if (!on)
            {
                return TileCheck.Signatures;
            }

            if (cache.RenderGen != chunk.GpuItemRenderGen)
            {
                return TileCheck.Fallback;
            }

            // a join, leave or another tile's fallback after the prepare: this tile's signatures, as with the switch off
            if (chunk.GpuItemsDirty || (cache.PollTiles & 1UL << tile) != 0)
            {
                return TileCheck.Signatures;
            }

            if (rangeOn && cache.RangeKey != rangeKey && !RangeBitsHold(cache, cache.TileStart[tile], cache.TileStart[tile + 1], rangeKey))
            {
                return TileCheck.Fallback;
            }

            return TileCheck.Trust;
        }

        /// <summary>The player moved or the view range changed since the build: whether entries [s, e) keep their range bits,
        /// from the positions their signatures hold (no item read).</summary>
        public static bool RangeBitsHold(GpuItemCache cache, int s, int e, ulong rangeKey)
        {
#if CUO_DEVTOOLS
            _rangePolled++;
#endif

            for (int i = s; i < e; i++)
            {
                ulong sig1 = cache.Entries[i].Sig1;

                if (OutOfRange(sig1, rangeKey) != ((sig1 & RANGE_BIT) != 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The fields two signatures differ in (GameScene.GpuItemSignature's layout).</summary>
        public static string DescribeDiff(ulong oldA, ulong oldB, ulong newA, ulong newB)
        {
            var sb = new System.Text.StringBuilder();

            void Field(string name, ulong o, ulong n)
            {
                if (o != n)
                {
                    sb.Append(sb.Length == 0 ? "" : ", ").Append(name).Append(" 0x").Append(o.ToString("X")).Append("->0x").Append(n.ToString("X"));
                }
            }

            Field("graphic", oldA & 0xFFFF, newA & 0xFFFF);
            Field("hue", oldA >> 16 & 0xFFFF, newA >> 16 & 0xFFFF);
            Field("amount", oldA >> 32 & 0xFFFF, newA >> 32 & 0xFFFF);
            Field("flags", oldA >> 48 & 0xFF, newA >> 48 & 0xFF);
            Field("alpha", oldA >> 56, newA >> 56);
            Field("x", oldB & 0xFFFF, newB & 0xFFFF);
            Field("y", oldB >> 16 & 0xFFFF, newB >> 16 & 0xFFFF);
            Field("z", oldB >> 32 & 0xFF, newB >> 32 & 0xFF);
            Field("priorityZ", oldB >> 40 & 0xFFFF, newB >> 40 & 0xFFFF);
            Field("allowedToDraw", oldB >> 56 & 1, newB >> 56 & 1);
            Field("destroyed", oldB >> 57 & 1, newB >> 57 & 1);
            Field("text", oldB >> 58 & 1, newB >> 58 & 1);
            Field("handles", oldB >> 59 & 1, newB >> 59 & 1);
            Field("offset", oldB >> 60 & 1, newB >> 60 & 1);
            Field("outOfRange", oldB >> 61 & 1, newB >> 61 & 1);

            return sb.ToString();
        }

#if CUO_DEVTOOLS
        // the checker's counts since the last summary (mismatches: since start too)
        private static long _invalidations, _trusted, _entriesTrusted, _polled, _rangePolled, _fallbacks, _genRebuilds, _spurious;
        private static long _mismatches, _mismatchesTotal, _logged, _at;

        /// <summary>Tests: mismatches since start.</summary>
        internal static long MismatchesTotal => _mismatchesTotal;

        internal static long Invalidations => _invalidations;

        /// <summary>Tests: back to the start state.</summary>
        internal static void ResetForTests()
        {
            OverrideForTests = null;
            CheckOverrideForTests = null;
            _invalidations = _trusted = _entriesTrusted = _polled = _rangePolled = _fallbacks = _genRebuilds = _spurious = 0;
            _mismatches = _mismatchesTotal = _logged = _at = 0;
        }

        public static void CountTrusted(int entries)
        {
            _trusted++;
            _entriesTrusted += entries;
        }

        public static void CountPolled() => _polled++;

        public static void CountFallback() => _fallbacks++;

        public static void CountGenRebuild(bool signaturesChanged)
        {
            _genRebuilds++;

            if (!signaturesChanged)
            {
                _spurious++;
            }
        }

        /// <summary>The generations trusted a tile whose item's signature changed (or that left the chunk).</summary>
        public static void Mismatch(Item item, Chunk chunk, int tile, ulong oldA, ulong oldB, ulong newA, ulong newB, bool otherChunk)
        {
            _mismatches++;
            _mismatchesTotal++;

            // every one is counted; the first 200 are logged in full
            if (_logged++ < 200)
            {
                string what = otherChunk ? "left the chunk" + (oldA != newA || oldB != newB ? "; " : "") : "";
                Log.Warn($"[gpu-sig-check] mismatch: item 0x{item.Serial:X8} (graphic 0x{oldA & 0xFFFF:X4}) chunk ({chunk.X},{chunk.Y}) tile {tile >> 3},{tile & 7}: " +
                         $"generations say unchanged, signature changed: {what}{DescribeDiff(oldA, oldB, newA, newB)}");
            }
        }

        /// <summary>The summary every 5 s (once per prepare pass while the checker is on).</summary>
        public static void Report(bool on)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_at == 0)
            {
                _at = now;

                return;
            }

            if (now - _at < System.Diagnostics.Stopwatch.Frequency * 5)
            {
                return;
            }

            Log.Info($"[gpu-sig-check] switch {(on ? "on" : "off")}: {_trusted} tiles trusted ({_entriesTrusted} item signatures not rebuilt), " +
                     $"{_polled} tiles polled after a walk, {_rangePolled} range-polled, {_fallbacks} fallbacks, {_invalidations} invalidations, " +
                     $"{_genRebuilds} generation rebuilds ({_spurious} with no signature changed), mismatches {_mismatches} (total {_mismatchesTotal})");

            _at = now;
            _invalidations = _trusted = _entriesTrusted = _polled = _rangePolled = _fallbacks = _genRebuilds = _spurious = 0;
            _mismatches = 0;
        }
#endif
    }
}
