// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using ClassicUO.Game.GameObjects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// Reuses the <see cref="Land"/> and <see cref="Static"/> objects of evicted chunks for later chunk loads, so a chunk load
    /// allocates (almost) nothing. Before this every load allocated about 25 KB of objects that all survived, and the gen0/gen1
    /// collections that promoted them paused 3-7 ms inside a chunk load (poc-log "Chunk loads without allocation").
    ///
    /// Lifetime invariants (a recycled object that something still holds would draw or pick the wrong tile):
    /// <list type="number">
    /// <item>Only <see cref="Chunk.Destroy"/> retires objects: an exact Land or Static it found in the chunk's tile lists, live
    /// (not destroyed by someone else first), after <see cref="GameObject.Destroy"/> and after it is unlinked (TPrevious, TNext,
    /// TileChunk null). Map statics and land are created only by <see cref="Chunk.Load"/> and live only in tile lists.</item>
    /// <item>A retired object stays destroyed (IsDestroyed, Graphic 0, <see cref="GameObject.InRecycler"/>) for at least
    /// <see cref="QuarantineMs"/> before <see cref="TakeLand"/>/<see cref="TakeStatic"/> hand it out (FIFO, oldest first).</item>
    /// <item>The cross-frame holders that can hold a Land/Static are scrubbed when it is retired (SelectedObject.Object and
    /// LastLeftDownObject, <see cref="Chunk.Destroy"/>). Everything else that sees land and statics finds them through the tile
    /// lists every frame, or keeps per-frame lists read only up to their count (see the holder audit in the poc-log).
    /// InspectorGump keeps one and compares <see cref="GameObject.Incarnation"/>.</item>
    /// <item>Reuse starts from the constructor's state: <see cref="GameObject.ResetForReuse"/> resets every instance field
    /// (a reflection unit test fails when a new field is not reset), then Land.Create/Static.Create run as for a new object.</item>
    /// </list>
    /// CUO_RECYCLE=0 turns reuse off. CUO_RECYCLE_CHECK=1 never reuses but keeps retired objects marked, and counts every
    /// later use of one (draw, picking, Gpu capture, selection) plus a tile-list integrity sweep; =reuse does the same checks
    /// with reuse on. Logs [recycle-check] every 5 s.
    /// </summary>
    internal sealed class MapObjectRecycler
    {
        public static readonly string CheckMode = Environment.GetEnvironmentVariable("CUO_RECYCLE_CHECK");
        public static readonly bool Check = CheckMode == "1" || CheckMode == "reuse";
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("CUO_RECYCLE") != "0" && CheckMode != "1";
        public static readonly uint QuarantineMs = uint.TryParse(Environment.GetEnvironmentVariable("CUO_RECYCLE_QUARANTINE_MS"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out uint q) ? q : 1000;

        // a full pool holds about a view's worth of chunks several times over (Constants.PREDICTABLE_CHUNKS = 300 chunks)
        public const int MaxLands = Constants.PREDICTABLE_TILE_COUNT;
        public const int MaxStatics = Constants.PREDICTABLE_STATICS;

        private struct Retired<T>
        {
            public T Obj;
            public uint Tick;
        }

        private readonly Queue<Retired<Land>> _lands = new Queue<Retired<Land>>();
        private readonly Queue<Retired<Static>> _statics = new Queue<Retired<Static>>();
        private readonly bool _enabled;
        private readonly uint _quarantineMs;

        /// <summary>Objects handed out again, retired, and dropped because the pool was full (FrameStats; reset by it).</summary>
        public static int FrameReused, FrameRetired;

        public MapObjectRecycler() : this(Enabled, QuarantineMs) { }

        public MapObjectRecycler(bool enabled, uint quarantineMs)
        {
            _enabled = enabled;
            _quarantineMs = quarantineMs;
        }

        public int PooledLands => _lands.Count;
        public int PooledStatics => _statics.Count;

        /// <summary>
        /// Takes a destroyed, unlinked Land or Static of an evicted chunk (Chunk.Destroy). Anything else is ignored.
        /// </summary>
        public void Retire(GameObject obj, uint now)
        {
            if (!obj.IsDestroyed || obj.TileChunk != null || obj.TNext != null || obj.TPrevious != null)
            {
                // not retired by Chunk.Destroy's rules: never reuse it
                return;
            }

            if (obj is Land land)
            {
                land.InRecycler = true;
                FrameRetired++;

                if (_enabled && _lands.Count < MaxLands)
                {
                    _lands.Enqueue(new Retired<Land> { Obj = land, Tick = now });
                }
            }
            else if (obj is Static st)
            {
                st.InRecycler = true;
                FrameRetired++;

                if (_enabled && _statics.Count < MaxStatics)
                {
                    _statics.Enqueue(new Retired<Static> { Obj = st, Tick = now });
                }
            }
        }

        /// <summary>The oldest retired Land if it has been retired for the quarantine time, else null (allocate one).</summary>
        public Land TakeLand(uint now)
        {
            if (_lands.Count != 0 && now - _lands.Peek().Tick >= _quarantineMs)
            {
                FrameReused++;

                return _lands.Dequeue().Obj;
            }

            return null;
        }

        /// <summary>The oldest retired Static if it has been retired for the quarantine time, else null (allocate one).</summary>
        public Static TakeStatic(uint now)
        {
            if (_statics.Count != 0 && now - _statics.Peek().Tick >= _quarantineMs)
            {
                FrameReused++;

                return _statics.Dequeue().Obj;
            }

            return null;
        }

        public void Clear()
        {
            _lands.Clear();
            _statics.Clear();
        }

        // ---- CUO_RECYCLE_CHECK ----

        private static readonly string[] _siteNames = { "land draw", "static draw", "land pick", "static pick", "gpu capture", "selected", "last left down", "mesh highlight", "inspector" };
        private static readonly long[] _touched = new long[_siteNames.Length];
        private static long _sweeps, _sweepObjects, _sweepBad, _lastBadLog;
        private static uint _nextLog, _frame;
        private static string _firstBad;

        public enum Site
        {
            LandDraw,
            StaticDraw,
            LandPick,
            StaticPick,
            GpuCapture,
            Selected,
            LastLeftDown,
            MeshHighlight,
            Inspector
        }

        /// <summary>CUO_RECYCLE_CHECK: a retired object was used (it would be a different tile once reused).</summary>
        public static void Touched(GameObject obj, Site site)
        {
            if (_touched[(int)site]++ == 0)
            {
                Log.Warn($"[recycle-check] retired {obj.GetType().Name} used at {_siteNames[(int)site]} (incarnation {obj.Incarnation})");
            }
        }

        /// <summary>CUO_RECYCLE_CHECK, once per frame from GameScene.Update before eviction: the selection holders, and every
        /// 64 frames the tile lists of every loaded chunk.</summary>
        public static void CheckFrame(World world, GameObject meshHighlight)
        {
            _frame++;

            if (SelectedObject.Object is GameObject s && s.InRecycler)
            {
                Touched(s, Site.Selected);
            }

            if (SelectedObject.LastLeftDownObject is GameObject l && l.InRecycler)
            {
                Touched(l, Site.LastLeftDown);
            }

            if (meshHighlight != null && meshHighlight.InRecycler)
            {
                Touched(meshHighlight, Site.MeshHighlight);
            }

            if ((_frame & 63) == 0 && world.Map != null)
            {
                Sweep(world.Map);
            }

            if (Time.Ticks >= _nextLog)
            {
                _nextLog = Time.Ticks + 5000;
                var sb = new System.Text.StringBuilder("[recycle-check] mode ").Append(CheckMode).Append(", touched after retire:");

                for (int i = 0; i < _siteNames.Length; i++)
                {
                    sb.Append(' ').Append(_siteNames[i]).Append(' ').Append(_touched[i]).Append(',');
                }

                sb.Append(" sweeps ").Append(_sweeps).Append(" (").Append(_sweepObjects).Append(" objects), bad ").Append(_sweepBad);

                if (_firstBad != null)
                {
                    sb.Append(", first bad: ").Append(_firstBad);
                }

                Log.Info(sb.ToString());
            }
        }

        private static void Sweep(Map map)
        {
            _sweeps++;

            foreach (Chunk chunk in map.GetUsedChunks())
            {
                if (chunk == null || chunk.IsDestroyed)
                {
                    continue;
                }

                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        // Tiles[x, y] is any node of the list (normally the land), not necessarily its head
                        GameObject head = chunk.GetHeadObject(x, y);
                        int guard = 0;

                        for (GameObject o = head; o != null && guard < 10000; o = o.TNext, guard++)
                        {
                            _sweepObjects++;

                            if (o.InRecycler)
                            {
                                Bad(chunk, x, y, o, "retired object in a tile list");
                            }
                            else if (o.TileChunk != chunk)
                            {
                                Bad(chunk, x, y, o, "TileChunk is another chunk");
                            }
                            else if (o.TNext != null && o.TNext.TPrevious != o)
                            {
                                Bad(chunk, x, y, o, "TNext.TPrevious mismatch");
                            }
                            else if ((o is Land || o is Static) && (o.X >> 3 != chunk.X || o.Y >> 3 != chunk.Y || (o.X & 7) != x || (o.Y & 7) != y || o.IsDestroyed))
                            {
                                Bad(chunk, x, y, o, "map object not at its tile or destroyed");
                            }
                        }
                    }
                }
            }
        }

        private static void Bad(Chunk chunk, int x, int y, GameObject o, string what)
        {
            _sweepBad++;

            if (_firstBad == null || Time.Ticks - _lastBadLog > 5000)
            {
                _lastBadLog = Time.Ticks;
                _firstBad ??= what;
                Log.Warn($"[recycle-check] chunk {chunk.X},{chunk.Y} tile {x},{y}: {what} ({o.GetType().Name} at {o.X},{o.Y} graphic 0x{o.Graphic:X4}, incarnation {o.Incarnation})");
            }
        }
    }
}
