// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// T6a's tile-list order key: the stored PriorityZ, then the tie rank, then the insertion stamp (newest first for
    /// ranks 0 and 1, oldest first for rank 2). A struct comparison, because a 64-bit stamp does not fit a packed key with
    /// the priority and rank.
    /// </summary>
    internal readonly struct TileOrderKey : IComparable<TileOrderKey>
    {
        public readonly short Priority;
        public readonly byte Rank;
        public readonly ulong Seq;

        public TileOrderKey(short priority, byte rank, ulong seq)
        {
            Priority = priority;
            Rank = rank;
            Seq = seq;
        }

        public int CompareTo(TileOrderKey other)
        {
            if (Priority != other.Priority)
            {
                return Priority < other.Priority ? -1 : 1;
            }

            if (Rank != other.Rank)
            {
                return Rank < other.Rank ? -1 : 1;
            }

            if (Seq == other.Seq)
            {
                return 0;
            }

            // rank 2 oldest first; land and state-1 multis newest first
            bool less = Seq < other.Seq;

            return (Rank == 2) == less ? -1 : 1;
        }

        public override string ToString() => $"({Priority}, {Rank}, {Seq})";
    }

    /// <summary>
    /// T6a (ecs-transition-plan.md): one sort key that predicts every tile list's order, so a tile index built outside the
    /// GameObject links (T6b) can reproduce it.
    ///
    /// <see cref="Chunk.AddGameObject"/> keeps each tile's TNext list sorted by the PriorityZ it computes at insertion
    /// (stored in <see cref="GameObject.PriorityZ"/>, never changed while the object is in a list) and inserts relative to
    /// equal priorities by its local <c>state</c>:
    /// <list type="bullet">
    /// <item>0 (<see cref="Land"/>): before every equal, so the newest land comes first;</item>
    /// <item>1 (a <see cref="Multi"/> that is generic-internal, or not a preview): after the equal lands, before every other
    /// equal, so the newest such multi comes first among them;</item>
    /// <item>-1 or 2 (everything else, preview multis included): after every equal, so the oldest comes first.</item>
    /// </list>
    /// Every insertion restamps <see cref="GameObject.TileSeq"/> from <see cref="NextSeq"/> (64-bit, so it never wraps);
    /// removal (RemoveFromTile, Destroy) only unlinks. So by induction the list equals its sort by <see cref="Key"/>, as
    /// long as the rank an object had at insertion is the rank its current fields give (see <see cref="Rank"/>).
    ///
    /// CUO_TILE_ORDER_CHECK=1 (every 30 frames), =every (every frame) or =N (every N frames), at the start of the world
    /// prepare (the state the walk then reads), over every tile of every loaded chunk (a superset of the tiles the walk
    /// visits): each list is traversed in full with cycle detection and must have consistent links, hold only objects whose
    /// stored chunk and cell are that tile, and equal its stable sort by <see cref="Key"/>; and every item, mobile, effect and
    /// house component that says it is on a tile must be reachable from that tile's anchor. Logs [tile-order] every 5 s and
    /// the first 25 failing tiles.
    /// </summary>
    internal static class TileOrder
    {
        /// <summary>The global insertion counter (Chunk.AddGameObject pre-increments it, so 0 is never a stamp).</summary>
        public static ulong NextSeq;

        public static readonly string CheckMode = Environment.GetEnvironmentVariable("CUO_TILE_ORDER_CHECK");
        public static readonly bool Check = !string.IsNullOrEmpty(CheckMode) && CheckMode != "0";
        public static readonly int CheckInterval = CheckMode == "every" ? 1 : CheckMode != "1" && int.TryParse(CheckMode, out int n) && n > 0 ? n : 30;

        /// <summary>
        /// AddGameObject's tie rank from the object's current fields: 0 land, 1 a multi it inserts with state 1 (generic-internal
        /// is tested first, so generic-internal wins over preview), 2 everything else.
        /// </summary>
        public static int Rank(GameObject obj)
        {
            if (obj is Land)
            {
                return 0;
            }

            if (obj is Multi m && ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0 || (m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) == 0))
            {
                return 1;
            }

            return 2;
        }

        public static TileOrderKey Key(GameObject obj) => new TileOrderKey(obj.PriorityZ, (byte)Rank(obj), obj.TileSeq);

        /// <summary>The result of one tile's check (<see cref="CheckTile"/>).</summary>
        public struct TileCheck
        {
            public int Objects;
            /// <summary>Objects whose key is below their predecessor's. 0 = the list equals its stable sort by the key.</summary>
            public int OutOfOrder;
            /// <summary>Index of the first such object, or -1.</summary>
            public int FirstDescent;
            /// <summary>A TPrevious or TNext cycle: the traversal stopped there, so the tile counts as failed.</summary>
            public bool Cycle;
            /// <summary>Objects whose TNext does not point back (TNext.TPrevious != it).</summary>
            public int BrokenLinks;
            /// <summary>Objects in the list whose stored TileChunk/TileCell is not this tile.</summary>
            public int Foreign;
            /// <summary>The forward walk from the head never met the anchor (Tiles[x, y]): the list the walk reads is not the
            /// one the anchor belongs to (a TPrevious link without a matching TNext, found during head discovery or here).</summary>
            public bool AnchorMissed;
            /// <summary>Objects (not effects) whose X/Y is not this tile: moved without being re-added.</summary>
            public int Misplaced;

            public readonly bool Failed => OutOfOrder != 0 || Cycle || BrokenLinks != 0 || Foreign != 0 || AnchorMissed || Misplaced != 0;
        }

        /// <summary>The head of a tile list from any node, by TPrevious, with Floyd cycle detection; null on a cycle.</summary>
        public static GameObject FindHead(GameObject anchor, out bool cycle) => FindHead(anchor, out cycle, out _);

        /// <summary><see cref="FindHead(GameObject, out bool)"/>, also counting TPrevious links whose target does not point back
        /// (p.TNext != the node it was reached from).</summary>
        public static GameObject FindHead(GameObject anchor, out bool cycle, out int brokenBack)
        {
            cycle = false;
            brokenBack = 0;

            if (anchor == null)
            {
                return null;
            }

            GameObject slow = anchor, fast = anchor;

            while (slow.TPrevious != null)
            {
                if (!ReferenceEquals(slow.TPrevious.TNext, slow))
                {
                    brokenBack++;
                }

                slow = slow.TPrevious;
                fast = fast?.TPrevious?.TPrevious;

                if (fast != null && ReferenceEquals(fast, slow))
                {
                    cycle = true;

                    return null;
                }
            }

            return slow;
        }

        /// <summary>
        /// One full traversal of tile (x, y) of <paramref name="chunk"/> from its anchor: head by TPrevious, then every node by
        /// TNext with Floyd cycle detection (no length cap), checking links, membership and key order.
        /// </summary>
        public static TileCheck CheckTile(Chunk chunk, int x, int y)
        {
            var r = new TileCheck { FirstDescent = -1 };
            GameObject anchor = chunk.Tiles[x, y];
            GameObject head = FindHead(anchor, out bool cycle, out int brokenBack);
            r.BrokenLinks = brokenBack;

            if (cycle)
            {
                r.Cycle = true;

                return r;
            }

            byte cell = (byte)((x << 3) | y);
            int tileX = chunk.X * 8 + x, tileY = chunk.Y * 8 + y;
            bool anchorSeen = false;
            GameObject fast = head;
            TileOrderKey prev = default;

            for (GameObject o = head; o != null; o = o.TNext)
            {
                TileOrderKey k = Key(o);

                if (r.Objects != 0 && k.CompareTo(prev) < 0)
                {
                    if (r.OutOfOrder++ == 0)
                    {
                        r.FirstDescent = r.Objects;
                    }
                }

                if (o.TNext != null && !ReferenceEquals(o.TNext.TPrevious, o))
                {
                    r.BrokenLinks++;
                }

                if (!ReferenceEquals(o.TileChunk, chunk) || o.TileCell != cell)
                {
                    r.Foreign++;
                }

                // effects place themselves at their own positions (moving effects); everything else lives at its X/Y
                if (o is not GameEffect && (o.X != tileX || o.Y != tileY))
                {
                    r.Misplaced++;
                }

                anchorSeen |= ReferenceEquals(o, anchor);

                prev = k;
                r.Objects++;
                fast = fast?.TNext?.TNext;

                if (fast != null && ReferenceEquals(fast, o.TNext))
                {
                    r.Cycle = true;

                    break;
                }
            }

            r.AnchorMissed = !r.Cycle && anchor != null && !anchorSeen;

            return r;
        }

        /// <summary>True when <paramref name="obj"/> is in the list of its stored tile (TileChunk, TileCell).</summary>
        public static bool IsReachable(GameObject obj)
        {
            Chunk chunk = obj.TileChunk;

            // T6b part 3: with the list dropped a dynamic is reachable through its tile's index entry (and the shadow list's)
            if (DynamicTileIndex.ListDropped && DynamicTileIndex.Holds(obj))
            {
                return chunk.Dynamic != null && chunk.Dynamic.Contains(obj, obj.TileCell) &&
                       (chunk.Shadow == null || chunk.Shadow.Contains(obj, obj.TileCell));
            }
            GameObject head = FindHead(chunk.Tiles[obj.TileCell >> 3, obj.TileCell & 7], out bool cycle);
            GameObject fast = head;

            for (GameObject o = head; o != null && !cycle; o = o.TNext)
            {
                if (ReferenceEquals(o, obj))
                {
                    return true;
                }

                fast = fast?.TNext?.TNext;
                cycle = fast != null && ReferenceEquals(fast, o.TNext);
            }

            return false;
        }

        // ---- CUO_TILE_ORDER_CHECK ----

        private const int MaxReports = 25;
        private static long _checks, _tiles, _objects, _outOfOrder, _badTiles, _cycles, _brokenLinks, _foreign, _ownedChecked, _unreachable, _anchorMissed, _misplaced;
        // objects seen per rank, and preview multis (rank 2): shows which insertion rules the runs exercised
        private static readonly long[] _ranks = new long[3];
        private static long _previews;
        private static int _reports;
        private static uint _frame, _nextLog;

        /// <summary>Once per frame from GameScene.FillGameObjectList (both renderers) when <see cref="Check"/>.</summary>
        public static void CheckFrame(World world)
        {
            if (++_frame % (uint)CheckInterval == 0 && world.Map != null)
            {
                _checks++;

                foreach (Chunk chunk in world.Map.GetUsedChunks())
                {
                    if (chunk == null || chunk.IsDestroyed)
                    {
                        continue;
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            if (DynamicTileIndex.Maintain || DynamicTileIndex.ListDropped)
                            {
                                CheckIndexCell(chunk, x, y);
                            }

                            if (chunk.Tiles[x, y] == null)
                            {
                                continue;
                            }

                            _tiles++;
                            TileCheck r = CheckTile(chunk, x, y);
                            _objects += r.Objects;
                            _outOfOrder += r.OutOfOrder;
                            _brokenLinks += r.BrokenLinks;
                            _foreign += r.Foreign;
                            _misplaced += r.Misplaced;

                            if (r.AnchorMissed)
                            {
                                _anchorMissed++;
                            }

                            if (r.Cycle)
                            {
                                _cycles++;
                            }
                            else
                            {
                                CountRanks(chunk, x, y);
                            }

                            if (r.Failed)
                            {
                                _badTiles++;
                                Report(chunk, x, y, r);
                            }
                        }
                    }
                }

                // the other direction: everything that says it is on a tile is in that tile's list
                foreach (Item item in world.Items.Values)
                {
                    CheckOwned(item);
                }

                foreach (Mobile mobile in world.Mobiles.Values)
                {
                    CheckOwned(mobile);
                }

#if CUO_DEVTOOLS
                // World.Effects exists only in developer builds
                for (GameEffect fx = (GameEffect)world.Effects.Items; fx != null; fx = (GameEffect)fx.Next)
                {
                    CheckOwned(fx);
                }
#endif

                foreach (House house in world.HouseManager.Houses)
                {
                    foreach (Multi m in house.Components)
                    {
                        CheckOwned(m);
                    }
                }
            }

            if (Time.Ticks >= _nextLog && (DynamicTileIndex.Maintain || DynamicTileIndex.ListDropped))
            {
                Log.Info(
                    $"[tile-order] index: tiles {_indexTiles}, entries {_indexEntries}, merged objects {_indexMerged}, mismatched tiles {_indexBadTiles} " +
                    $"(order {_indexOrder}, bad entries {_indexBadEntries}, count {_indexCount}); double submissions {_doubleSubmissions}; reader checks {_readerChecks}, mismatches {_readerMismatches}; world_tile_index walk {(DynamicTileIndex.IndexWalkOn ? "on" : "off")}, " +
                    $"list dropped {(DynamicTileIndex.ListDropped ? "yes" : "no")}, shadow list {(DynamicTileIndex.Shadow ? "on" : "off")}, list dynamics {_indexListDynamics}, " +
                    $"pooled arrays {DynamicTileIndex.EntryPool.Pooled}"
                );
            }

            if (Time.Ticks >= _nextLog)
            {
                _nextLog = Time.Ticks + 5000;
                Log.Info(
                    $"[tile-order] tiles {_tiles}, objects {_objects}, out of order {_outOfOrder}, cycles {_cycles}, broken links {_brokenLinks}, foreign {_foreign}, anchor missed {_anchorMissed}, misplaced {_misplaced}, " +
                    $"unreachable {_unreachable} of {_ownedChecked} owned; failed tiles {_badTiles}; checks {_checks}; land {_ranks[0]}, multi {_ranks[1]}, " +
                    $"other {_ranks[2]} (preview multis {_previews}), every {CheckInterval} frames, seq {NextSeq}"
                );
            }
        }

        // ---- T6b: the merged order (list without dynamics + DynamicTileIndex) must equal the list while both are maintained ----

        private static long _doubleSubmissions;
        private static long _readerChecks, _readerMismatches;

        /// <summary>T6b part 2: a converted reader ran with the walk on and off (<see cref="Check"/> only) and got the same
        /// or different results; logs the first reports.</summary>
        /// <summary>True when both lists hold the same objects (by reference) the same number of times, whatever the order.</summary>
        public static bool SameObjects(List<GameObject> a, List<GameObject> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            var counts = new Dictionary<GameObject, int>(ReferenceEqualityComparer.Instance);

            foreach (GameObject o in a)
            {
                counts[o] = counts.GetValueOrDefault(o) + 1;
            }

            foreach (GameObject o in b)
            {
                if (!counts.TryGetValue(o, out int n) || n == 0)
                {
                    return false;
                }

                counts[o] = n - 1;
            }

            return true;
        }

        public static void ReaderCheck(bool same, string reader, Func<string> detail)
        {
            _readerChecks++;

            if (same)
            {
                return;
            }

            _readerMismatches++;

            if (_reports++ < MaxReports)
            {
                Log.Warn($"[tile-order] reader mismatch: {reader} differs between the list walk and the merged walk: {detail()}");
            }
        }

        /// <summary>The render-list walk reached <paramref name="obj"/> after it was already drawn (or served by the Gpu item
        /// cache) this frame: a submitted object that the list walk would not submit twice.</summary>
        public static void NoteDoubleSubmission(GameObject obj)
        {
            _doubleSubmissions++;

            if (_reports++ < MaxReports)
            {
                Log.Warn($"[tile-order] double submission: {Describe(obj)} at {obj.X},{obj.Y} drawn twice in one frame");
            }
        }

        private static long _indexTiles, _indexEntries, _indexMerged, _indexBadTiles, _indexOrder, _indexBadEntries, _indexCount, _indexListDynamics;

        /// <summary>The result of one cell's index check (<see cref="CheckIndex"/>).</summary>
        public struct IndexCheck
        {
            public int Entries;
            public int Merged;
            /// <summary>Positions where the merged order and the list differ (a missing, extra or misordered object).</summary>
            public int OrderMismatches;
            /// <summary>Entries out of key order, with a stored key or id other than the object's, on another cell, or of an
            /// object the index must not hold.</summary>
            public int BadEntries;
            /// <summary>The list's dynamic objects and the entries differ in number.</summary>
            public bool CountMismatch;
            public int FirstMismatch;
            /// <summary>T6b part 3, list dropped: dynamic objects still linked into the tile list (it must hold none).</summary>
            public int ListDynamics;

            public readonly bool Failed => OrderMismatches != 0 || BadEntries != 0 || CountMismatch || ListDynamics != 0;
        }

        /// <summary>T6b: cell (x, y) of <paramref name="chunk"/>: the merged order (<see cref="TileWalk"/>) equals the list, and
        /// every entry is the object's own (key, id, cell). A list with a cycle is the list check's failure, not this one's.</summary>
        public static IndexCheck CheckIndex(Chunk chunk, int x, int y)
        {
            var r = new IndexCheck { FirstMismatch = -1 };
            int cell = x << 3 | y;
            DynamicTileIndex.Entry[] entries = null;
            int n = 0;

            if (chunk.Dynamic != null)
            {
                entries = chunk.Dynamic.RawCell(cell, out n);
            }

            r.Entries = n;

            for (int i = 0; i < n; i++)
            {
                ref DynamicTileIndex.Entry e = ref entries[i];
                GameObject o = e.Obj;
                bool bad = o == null || !DynamicTileIndex.Holds(o) || !ReferenceEquals(o.TileChunk, chunk) || o.TileCell != cell ||
                           e.Key.CompareTo(Key(o)) != 0 ||
                           i > 0 && entries[i - 1].Key.CompareTo(e.Key) >= 0;

                if (bad)
                {
                    r.BadEntries++;
                }
            }

            GameObject head = FindHead(chunk.Tiles[x, y], out bool cycle);

            if (cycle)
            {
                return r;
            }

            // T6b part 3: with the list dropped the legacy order is the shadow list's (every object, by the old insertion
            // rule), and the tile list itself must hold no dynamics. Without a shadow (not under the checker) only that is checked.
            List<GameObject> shadow = null;

            if (DynamicTileIndex.ListDropped)
            {
                for (GameObject o = head; o != null; o = o.TNext)
                {
                    if (DynamicTileIndex.Holds(o))
                    {
                        r.ListDynamics++;
                    }
                }

                if (chunk.Shadow == null)
                {
                    return r;
                }

                shadow = chunk.Shadow.Cell(cell) ?? new List<GameObject>();
            }

            int dynamicInList = 0, limit = 1 << 20, pos = 0;
            GameObject merged = TileWalk.BeginIndex(chunk, cell, out TileWalk walk);

            for (GameObject o = shadow != null ? (shadow.Count > 0 ? shadow[0] : null) : head; (o != null || merged != null) && pos < limit; pos++)
            {
                if (merged != null)
                {
                    r.Merged++;
                }

                if (!ReferenceEquals(o, merged))
                {
                    r.OrderMismatches++;

                    if (r.FirstMismatch < 0)
                    {
                        r.FirstMismatch = pos;
                    }
                }

                if (o != null)
                {
                    if (DynamicTileIndex.Holds(o))
                    {
                        dynamicInList++;
                    }

                    o = shadow != null ? (pos + 1 < shadow.Count ? shadow[pos + 1] : null) : o.TNext;
                }

                if (merged != null)
                {
                    merged = walk.Next();
                }
            }

            r.CountMismatch = dynamicInList != n;

            return r;
        }

        private static void CheckIndexCell(Chunk chunk, int x, int y)
        {
            if (chunk.Tiles[x, y] == null && (chunk.Dynamic == null || chunk.Dynamic.CountAt(x << 3 | y) == 0))
            {
                return;
            }

            _indexTiles++;
            IndexCheck r = CheckIndex(chunk, x, y);
            _indexEntries += r.Entries;
            _indexMerged += r.Merged;
            _indexOrder += r.OrderMismatches;
            _indexBadEntries += r.BadEntries;
            _indexListDynamics += r.ListDynamics;

            if (r.CountMismatch)
            {
                _indexCount++;
            }

            if (!r.Failed)
            {
                return;
            }

            _indexBadTiles++;

            if (_reports++ >= MaxReports)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[tile-order] index mismatch at tile ").Append(chunk.X * 8 + x).Append(',').Append(chunk.Y * 8 + y).Append(": entries ")
              .Append(r.Entries).Append(", order mismatches ").Append(r.OrderMismatches).Append(" (first at ").Append(r.FirstMismatch)
              .Append("), bad entries ").Append(r.BadEntries).Append(", count mismatch ").Append(r.CountMismatch)
              .Append(", list dynamics ").Append(r.ListDynamics).Append(" | list:");
            int i = 0;

            for (GameObject o = chunk.Tiles[x, y] == null ? null : FindHead(chunk.Tiles[x, y], out _); o != null && i < 16; o = o.TNext, i++)
            {
                sb.Append(" #").Append(i).Append(' ').Append(Describe(o));
            }

            if (chunk.Shadow?.Cell(x << 3 | y) is { } shadowCell)
            {
                sb.Append(" | shadow (legacy order):");
                i = 0;

                foreach (GameObject o in shadowCell)
                {
                    if (i >= 16)
                    {
                        break;
                    }

                    sb.Append(" #").Append(i++).Append(' ').Append(Describe(o));
                }
            }

            sb.Append(" | index:");

            foreach (var e in chunk.Dynamic != null ? chunk.Dynamic.Cell(x << 3 | y) : default)
            {
                sb.Append(' ').Append(e.Key).Append(' ').Append(e.Obj == null ? "null" : Describe(e.Obj));
            }

            Log.Warn(sb.ToString());
        }

        private static void CountRanks(Chunk chunk, int x, int y)
        {
            for (GameObject o = FindHead(chunk.Tiles[x, y], out _); o != null; o = o.TNext)
            {
                int rank = Rank(o);
                _ranks[rank]++;

                if (rank == 2 && o is Multi)
                {
                    _previews++;
                }
            }
        }

        private static void CheckOwned(GameObject obj)
        {
            Chunk chunk = obj.TileChunk;

            if (chunk == null || chunk.IsDestroyed)
            {
                return;
            }

            _ownedChecked++;

            if (!IsReachable(obj))
            {
                _unreachable++;

                if (_reports++ < MaxReports)
                {
                    Log.Warn($"[tile-order] unreachable: {Describe(obj)} at {obj.X},{obj.Y} says chunk {chunk.X},{chunk.Y} cell {obj.TileCell >> 3},{obj.TileCell & 7}");
                }
            }
        }

        private static void Report(Chunk chunk, int x, int y, TileCheck r)
        {
            if (_reports++ >= MaxReports)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[tile-order] tile ").Append(chunk.X * 8 + x).Append(',').Append(chunk.Y * 8 + y).Append(": ").Append(r.Objects)
              .Append(" objects, out of order ").Append(r.OutOfOrder).Append(", cycle ").Append(r.Cycle).Append(", broken links ")
              .Append(r.BrokenLinks).Append(", foreign ").Append(r.Foreign).Append(", anchor missed ").Append(r.AnchorMissed)
              .Append(", misplaced ").Append(r.Misplaced);

            if (!r.Cycle)
            {
                // a window around the first descent (or the start), at most 16 objects
                int from = Math.Max(0, (r.FirstDescent < 0 ? 0 : r.FirstDescent) - 8);
                int i = 0;
                GameObject prev = null;

                for (GameObject o = FindHead(chunk.Tiles[x, y], out _); o != null && i < from + 16; o = o.TNext, i++)
                {
                    if (i >= from)
                    {
                        bool descent = prev != null && Key(o).CompareTo(Key(prev)) < 0;
                        sb.Append(descent ? " | >>#" : " | #").Append(i).Append(' ').Append(Describe(o));
                    }

                    prev = o;
                }
            }

            Log.Warn(sb.ToString());
        }

        public static string Describe(GameObject o)
        {
            string kind = o switch
            {
                Multi m => $"Multi(state 0x{(int)m.State:X}{(m.IsCustom ? " custom" : "")}{(m.IsHousePreview ? " housepreview" : "")})",
                Item it => it.IsCorpse ? "Item(corpse)" : it.IsMulti ? "Item(multi)" : "Item",
                _ => o.GetType().Name
            };

            return $"{kind} rank {Rank(o)} pz {o.PriorityZ} z {o.Z} seq {o.TileSeq} graphic 0x{o.Graphic:X4} cell {o.TileCell >> 3},{o.TileCell & 7}{(o.IsDestroyed ? " DESTROYED" : "")}";
        }
    }
}
