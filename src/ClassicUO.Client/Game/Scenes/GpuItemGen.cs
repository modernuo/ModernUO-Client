// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// pa-gpusig (perf audit 1111 #4; always on since quiet-1123): the Gpu item cache (GameSceneGpuItemCache) trusts a served
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
    /// </summary>
    internal static class GpuItemGen
    {
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
            /// <summary>Rebuild and compare every entry's signature (the walk had the tile since, or the chunk was marked).</summary>
            Signatures,
            /// <summary>Changed: the tile is walked and the chunk re-extracted.</summary>
            Fallback
        }

        /// <summary>
        /// The decision for served tile <paramref name="tile"/> of <paramref name="cache"/>: a fallback when the chunk's render generation moved after the build (a change after the prepare),
        /// the signatures for a tile the walk may have had or a chunk marked for re-extraction since the prepare, a fallback
        /// when an entry's range bit flipped, else trust.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TileCheck Decide(Chunk chunk, GpuItemCache cache, int tile, bool rangeOn, ulong rangeKey)
        {
            if (cache.RenderGen != chunk.GpuItemRenderGen)
            {
                return TileCheck.Fallback;
            }

            // a join, leave or another tile's fallback after the prepare: this tile's signatures
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
    }
}
