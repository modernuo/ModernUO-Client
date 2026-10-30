// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Text;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Map
{
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
    /// Every insertion restamps <see cref="GameObject.TileSeq"/> from <see cref="NextSeq"/>; removal (RemoveFromTile, Destroy)
    /// only unlinks. So by induction the list equals its sort by (PriorityZ, rank, rank 2 ? seq : -seq), as long as the rank
    /// an object had at insertion is the rank its current fields give (see <see cref="Rank"/>).
    ///
    /// CUO_TILE_ORDER_CHECK=1 (every 30 frames), =every (every frame) or =N (every N frames): every tile of every loaded chunk
    /// (a superset of the tiles the walk visits) must equal its stable sort by <see cref="Key"/>; logs [tile-order] every 5 s
    /// and the first 25 offenders.
    /// </summary>
    internal static class TileOrder
    {
        /// <summary>The global insertion counter (Chunk.AddGameObject pre-increments it, so 0 is never a stamp).</summary>
        public static uint NextSeq;

        public static readonly string CheckMode = Environment.GetEnvironmentVariable("CUO_TILE_ORDER_CHECK");
        public static readonly bool Check = !string.IsNullOrEmpty(CheckMode) && CheckMode != "0";
        public static readonly int CheckInterval = CheckMode == "every" ? 1 : CheckMode != "1" && int.TryParse(CheckMode, out int n) && n > 0 ? n : 30;

        private const CUSTOM_HOUSE_MULTI_OBJECT_FLAGS FrontMultiMask = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL;

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

            if (obj is Multi m && ((m.State & FrontMultiMask) != 0 || (m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) == 0))
            {
                return 1;
            }

            return 2;
        }

        /// <summary>The predicted order key: PriorityZ as stored, then rank, then newest first (ranks 0, 1) or oldest first (2).</summary>
        public static ulong Key(GameObject obj)
        {
            int rank = Rank(obj);
            uint seq = rank == 2 ? obj.TileSeq : uint.MaxValue - obj.TileSeq;

            return ((ulong)(uint)(obj.PriorityZ + 32768) << 34) | ((ulong)(uint)rank << 32) | seq;
        }

        /// <summary>
        /// The number of objects whose key is below their predecessor's, from the head of a tile list. 0 means the list equals
        /// its stable sort by <see cref="Key"/> (a stable sort leaves a non-decreasing sequence as it is, and moves any descent).
        /// </summary>
        public static int CountOutOfOrder(GameObject head, out int objects)
        {
            int bad = 0;
            objects = 0;
            ulong prev = 0;

            for (GameObject o = head; o != null && objects < 10000; o = o.TNext)
            {
                ulong k = Key(o);

                if (objects++ != 0 && k < prev)
                {
                    bad++;
                }

                prev = k;
            }

            return bad;
        }

        // ---- CUO_TILE_ORDER_CHECK ----

        private const int MaxOffenders = 25;
        private static long _checks, _tiles, _objects, _outOfOrder, _badTiles;
        // objects seen per rank, and preview multis (rank 2): shows which insertion rules the runs exercised
        private static readonly long[] _ranks = new long[3];
        private static long _previews;
        private static int _offenders;
        private static uint _frame, _nextLog;

        /// <summary>Once per frame from GameScene.Update when <see cref="Check"/>.</summary>
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
                            GameObject head = chunk.GetHeadObject(x, y);

                            if (head == null)
                            {
                                continue;
                            }

                            _tiles++;
                            int bad = CountOutOfOrder(head, out int objects);

                            for (GameObject o = head; o != null; o = o.TNext)
                            {
                                int rank = Rank(o);
                                _ranks[rank]++;

                                if (rank == 2 && o is Multi)
                                {
                                    _previews++;
                                }
                            }

                            _objects += objects;

                            if (bad != 0)
                            {
                                _outOfOrder += bad;
                                _badTiles++;
                                Report(chunk, x, y, head);
                            }
                        }
                    }
                }
            }

            if (Time.Ticks >= _nextLog)
            {
                _nextLog = Time.Ticks + 5000;
                Log.Info($"[tile-order] tiles {_tiles}, objects {_objects}, out of order {_outOfOrder} (tiles {_badTiles}), checks {_checks}; land {_ranks[0]}, multi {_ranks[1]}, other {_ranks[2]} (preview multis {_previews}), every {CheckInterval} frames, seq {NextSeq}");
            }
        }

        private static void Report(Chunk chunk, int x, int y, GameObject head)
        {
            if (_offenders >= MaxOffenders)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[tile-order] out of order at ").Append(chunk.X * 8 + x).Append(',').Append(chunk.Y * 8 + y).Append(':');
            GameObject prev = null;
            int n = 0;

            for (GameObject o = head; o != null && n < 64; o = o.TNext, n++)
            {
                bool offender = prev != null && Key(o) < Key(prev);

                if (offender)
                {
                    _offenders++;
                }

                sb.Append(offender ? " | >>" : " | ").Append(Describe(o));
                prev = o;
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

            return $"{kind} rank {Rank(o)} pz {o.PriorityZ} z {o.Z} seq {o.TileSeq} graphic 0x{o.Graphic:X4}{(o.IsDestroyed ? " DESTROYED" : "")}";
        }
    }
}
