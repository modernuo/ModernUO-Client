// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// T6b (ecs-transition-plan.md, <c>world_tile_index</c>): a chunk's per-tile sorted arrays of the dynamic objects on it
    /// (items and mobiles; T8 adds effects through <see cref="Holds"/>), each entry the T6a key stored at insertion and the
    /// object (no entity id: a death re-key or a record replacement changes the serial/record under a placed object, and the
    /// object's validated ECS handle already reaches the record). <see cref="Chunk.AddGameObject"/> (every placement: AddToTile, SetInWorldTile, moves,
    /// Map.Park's deferred add) and <see cref="GameObject.RemoveFromTile"/> maintain it; Chunk.Destroy/ClearForReload clear
    /// it. The tile's linked list still holds everything (both are maintained): with the walk switch on, the walks read the
    /// list's other objects (land, statics, multi components, effects until T8) merged by key with this array
    /// (<see cref="TileWalk"/>), and CUO_TILE_ORDER_CHECK compares that merged order with the list.
    /// </summary>
    internal sealed class DynamicTileIndex
    {
        internal struct Entry
        {
            public TileOrderKey Key;
            public GameObject Obj;
        }

#if CUO_DEVTOOLS
        // CLEANUP(release): the gate's override of the setting (CUO_WORLD_TILE_INDEX=1|0); =0 keeps the index maintained with
        // the walk off (pickscan's world_tile_index mode toggles the walk at runtime, so it needs the index either way)
        private static readonly string s_env = Environment.GetEnvironmentVariable("CUO_WORLD_TILE_INDEX");

        /// <summary>The walks read the merged order (<see cref="TileWalk"/>). Dev builds: pickscan toggles it at runtime.</summary>
        public static bool WalkOn = s_env switch
        {
            "1" => true,
            "0" => false,
            _ => Configuration.Settings.GlobalSettings?.WorldTileIndex ?? false
        };

        /// <summary>Placements keep the index (the walk switch, an explicit CUO_WORLD_TILE_INDEX, or CUO_TILE_ORDER_CHECK).
        /// Read once: an index started late would miss the objects already placed. Tests set it.</summary>
        public static bool Maintain = WalkOn || s_env == "0" || TileOrder.Check;
#else
        public static readonly bool WalkOn = Configuration.Settings.GlobalSettings?.WorldTileIndex ?? false;
        public static bool Maintain = WalkOn;
#endif

        /// <summary>Kept in the index (and skipped in the list by <see cref="TileWalk"/>). The extension point for T8: add
        /// GameEffect here, and the walks, the checker and the item cache's merged loops follow.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Holds(GameObject obj) => obj is Item || obj is Mobile;

        private readonly Entry[][] _cells = new Entry[64][];
        private readonly int[] _counts = new int[64];

        /// <summary>Entries over all 64 tiles.</summary>
        public int Count { get; private set; }

        public int CountAt(int cell) => _counts[cell];

        public ReadOnlySpan<Entry> Cell(int cell) => _cells[cell] == null ? default : new ReadOnlySpan<Entry>(_cells[cell], 0, _counts[cell]);

        internal Entry[] RawCell(int cell, out int count)
        {
            count = _counts[cell];

            return _cells[cell];
        }

        /// <summary>Files <paramref name="obj"/> under <paramref name="cell"/> by the key AddGameObject just stamped: after every
        /// lower or equal key, so equal priorities keep insertion order (rank 2, oldest first).</summary>
        public void Add(GameObject obj, int cell)
        {
            var key = TileOrder.Key(obj);
            Entry[] arr = _cells[cell];
            int n = _counts[cell];

            if (arr == null)
            {
                arr = _cells[cell] = EntryPool.Rent(EntryPool.MinSize);
            }
            else if (n == arr.Length)
            {
                Entry[] bigger = EntryPool.Rent(n * 2);
                Array.Copy(arr, bigger, n);
                EntryPool.Return(arr, n);
                arr = _cells[cell] = bigger;
            }

            // upper bound: the stamp is the newest, so in practice the tail unless a higher priority is already there
            int pos = n;

            while (pos > 0 && arr[pos - 1].Key.CompareTo(key) > 0)
            {
                pos--;
            }

            if (pos < n)
            {
                Array.Copy(arr, pos, arr, pos + 1, n - pos);
            }

            arr[pos] = new Entry { Key = key, Obj = obj };
            _counts[cell] = n + 1;
            Count++;
        }

        /// <summary>Removes <paramref name="obj"/> from <paramref name="cell"/>; false when it was not there.</summary>
        public bool Remove(GameObject obj, int cell)
        {
            Entry[] arr = _cells[cell];
            int n = _counts[cell];

            if (arr == null)
            {
                return false;
            }

            // the key fields are still the insertion's (AddGameObject restamps only after RemoveFromTile)
            var key = TileOrder.Key(obj);
            int lo = 0, hi = n - 1, pos = -1;

            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = arr[mid].Key.CompareTo(key);

                if (c == 0)
                {
                    pos = mid;

                    break;
                }

                if (c < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (pos < 0 || !ReferenceEquals(arr[pos].Obj, obj))
            {
                // not found by key (a key field changed while placed): fall back to identity
                pos = -1;

                for (int i = 0; i < n; i++)
                {
                    if (ReferenceEquals(arr[i].Obj, obj))
                    {
                        pos = i;

                        break;
                    }
                }

                if (pos < 0)
                {
                    return false;
                }
            }

            n--;

            if (pos < n)
            {
                Array.Copy(arr, pos + 1, arr, pos, n - pos);
            }

            arr[n] = default;
            Count--;

            if (n == 0)
            {
                EntryPool.Return(arr, 0);
                _cells[cell] = null;
            }

            _counts[cell] = n;

            return true;
        }

        /// <summary>Chunk.Destroy and ClearForReload: every entry leaves, the arrays go back to the pool.</summary>
        public void Clear()
        {
            if (Count == 0)
            {
                return;
            }

            for (int i = 0; i < 64; i++)
            {
                if (_cells[i] != null)
                {
                    EntryPool.Return(_cells[i], _counts[i]);
                    _cells[i] = null;
                    _counts[i] = 0;
                }
            }

            Count = 0;
        }

        /// <summary>
        /// Per-tile entry arrays by power-of-two size class (main thread only). Chunks come and go as the player moves, so
        /// arrays are reused instead of collected; each class keeps at most <see cref="MaxPooled"/> arrays.
        /// </summary>
        internal static class EntryPool
        {
            public const int MinSize = 4;
            private const int MaxPooled = 4096;
            private static readonly Stack<Entry[]>[] s_pools = new Stack<Entry[]>[24];

            public static int Pooled
            {
                get
                {
                    int n = 0;

                    foreach (var s in s_pools)
                    {
                        n += s?.Count ?? 0;
                    }

                    return n;
                }
            }

            private static int Class(int size) => 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)size);

            public static Entry[] Rent(int size)
            {
                size = Math.Max(MinSize, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)size));
                Stack<Entry[]> s = s_pools[Class(size)];

                return s != null && s.Count > 0 ? s.Pop() : new Entry[size];
            }

            /// <summary>Clears the first <paramref name="used"/> entries (object references) and pools the array.</summary>
            public static void Return(Entry[] arr, int used)
            {
                if (used > 0)
                {
                    Array.Clear(arr, 0, used);
                }

                int c = Class(arr.Length);

                if (c >= s_pools.Length || (arr.Length & (arr.Length - 1)) != 0)
                {
                    return;
                }

                Stack<Entry[]> s = s_pools[c] ??= new Stack<Entry[]>();

                if (s.Count < MaxPooled)
                {
                    s.Push(arr);
                }
            }
        }
    }

    /// <summary>
    /// T6b: one tile's objects in the merged order, the list's non-dynamic objects (<see cref="DynamicTileIndex.Holds"/>
    /// false) merged by <see cref="TileOrderKey"/> with the tile's <see cref="DynamicTileIndex"/> array. While both are
    /// maintained this equals the list (CUO_TILE_ORDER_CHECK checks it); the walks use it when
    /// <see cref="DynamicTileIndex.WalkOn"/>.
    /// </summary>
    internal struct TileWalk
    {
        private GameObject _s;
        private TileOrderKey _sKey;
        private DynamicTileIndex.Entry[] _d;
        private int _di, _dn;

        /// <summary>Where a render-list walk starts. A whole-tile walk (<paramref name="single"/> false) with the walk on starts
        /// the merged order at the tile's head (<paramref name="merged"/> true: continue with <see cref="Next"/>); a
        /// single-object walk (<paramref name="single"/>: one object submitted, the Gpu item cache and mobile calls) starts at
        /// <paramref name="first"/> itself. Whole-tile vs single is explicit: the object's TNext is null for the tile's last
        /// object, so it cannot tell the two apart (Codex P2: a tail mobile's call restarted at the tile head).</summary>
        public static GameObject Start(GameObject first, Chunk chunk, bool single, bool walkOn, out TileWalk walk, out bool merged)
        {
            merged = walkOn && !single;

            if (!merged)
            {
                walk = default;

                return first;
            }

            return Begin(chunk, first.TileCell, out walk);
        }

        /// <summary>The first object of (<paramref name="chunk"/>, <paramref name="cell"/>) in merged order, or null.</summary>
        public static GameObject Begin(Chunk chunk, int cell, out TileWalk walk)
        {
            walk = default;
            walk._s = SkipDynamic(chunk.GetHeadObject(cell >> 3, cell & 7));

            if (chunk.Dynamic != null)
            {
                walk._d = chunk.Dynamic.RawCell(cell, out walk._dn);
            }

            if (walk._s != null && walk._dn > 0)
            {
                walk._sKey = TileOrder.Key(walk._s);
            }

            return walk.Next();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static GameObject SkipDynamic(GameObject o)
        {
            while (o != null && DynamicTileIndex.Holds(o))
            {
                o = o.TNext;
            }

            return o;
        }

        public GameObject Next()
        {
            if (_di < _dn && (_s == null || _d[_di].Key.CompareTo(_sKey) < 0))
            {
                return _d[_di++].Obj;
            }

            GameObject s = _s;

            if (s != null)
            {
                _s = SkipDynamic(s.TNext);

                if (_s != null && _di < _dn)
                {
                    _sKey = TileOrder.Key(_s);
                }
            }

            return s;
        }
    }
}
