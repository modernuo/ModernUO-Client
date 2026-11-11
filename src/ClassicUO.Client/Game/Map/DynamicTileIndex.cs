// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// T6b (ecs-transition-plan.md, <c>world_tile_index</c>): a chunk's per-tile sorted arrays of the dynamic objects on it
    /// (items, mobiles and effects, see <see cref="Holds"/>), each entry the T6a key stored at insertion and the
    /// object (no entity id: a death re-key or a record replacement changes the serial/record under a placed object, and the
    /// object's validated ECS handle already reaches the record). <see cref="Chunk.AddGameObject"/> (every placement: AddToTile, SetInWorldTile, moves,
    /// Map.Park's deferred add) and <see cref="GameObject.RemoveFromTile"/> maintain it; Chunk.Destroy/ClearForReload clear
    /// it. The tile's linked list keeps only land, statics and multi components (T6b part 3; T9 deleted the
    /// <c>world_tile_index</c> off path, in which the list held everything): the dynamics live only here, and every whole-tile
    /// walk reads the list merged by key with this array (<see cref="TileWalk"/>).
    /// </summary>
    internal sealed class DynamicTileIndex
    {
        internal struct Entry
        {
            public TileOrderKey Key;
            public GameObject Obj;
        }

        /// <summary>Kept in the index and never in the tile list: items, mobiles and, since T8, effects (priority +2, T6a's
        /// key).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Holds(GameObject obj) => obj is Item || obj is Mobile || obj is GameEffect;

        private readonly Entry[][] _cells = new Entry[64][];
        private readonly int[] _counts = new int[64];

        /// <summary>Entries over all 64 tiles.</summary>
        public int Count { get; private set; }

        public int CountAt(int cell) => _counts[cell];

        /// <summary>Bit c set: tile c holds at least one entry. The whole-tile walks test it once per tile (a register test, see
        /// <see cref="TileWalk.MergeCells"/>) instead of reading the cell's count, so a tile without dynamics walks its list
        /// alone.</summary>
        public ulong Cells { get; private set; }

        public ReadOnlySpan<Entry> Cell(int cell) => _cells[cell] == null ? default : new ReadOnlySpan<Entry>(_cells[cell], 0, _counts[cell]);

        internal Entry[] RawCell(int cell, out int count)
        {
            count = _counts[cell];

            return _cells[cell];
        }

        /// <summary><see cref="TileOrder.Key"/> of a held object (<see cref="Holds"/>): never Land or a Multi, so its rank is 2 and
        /// the rank's type tests are skipped on every placement and removal (CUO_TILE_ORDER_CHECK compares every stored key
        /// with TileOrder.Key).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static TileOrderKey KeyOf(GameObject obj) => new TileOrderKey(obj.PriorityZ, 2, obj.TileSeq);

        /// <summary>Files <paramref name="obj"/> under <paramref name="cell"/> by the key AddGameObject just stamped: after every
        /// lower or equal key, so equal priorities keep insertion order (rank 2, oldest first).</summary>
        public void Add(GameObject obj, int cell)
        {
            var key = KeyOf(obj);
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
            Cells |= 1UL << cell;
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
            var key = KeyOf(obj);
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
                Cells &= ~(1UL << cell);
            }

            _counts[cell] = n;

            return true;
        }

        /// <summary>Whether <paramref name="cell"/> holds <paramref name="obj"/> (by identity; the checker's reachability).</summary>
        public bool Contains(GameObject obj, int cell)
        {
            Entry[] arr = _cells[cell];

            for (int i = 0, n = _counts[cell]; i < n; i++)
            {
                if (ReferenceEquals(arr[i].Obj, obj))
                {
                    return true;
                }
            }

            return false;
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
            Cells = 0;
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
    /// T6b: one tile's objects in the merged order, the list's objects (land, statics, multis) merged by
    /// <see cref="TileOrderKey"/> with the tile's <see cref="DynamicTileIndex"/> array.
    /// </summary>
    internal struct TileWalk
    {
        private TileListObject _s;
        private DynamicTileIndex.Entry[] _d;
        private int _di, _dn;

        /// <summary>
        /// The tiles of <paramref name="chunk"/> whose whole-tile walk takes the merged order (bit = cell): the index's occupied
        /// cells (<see cref="DynamicTileIndex.Cells"/>). A tile without dynamics walks its list alone. The whole-tile
        /// walks (the render walk's chunk loop, the Gpu mask build, the item cache) read it once per chunk, so the per-tile
        /// decision is a test of a register (tile-index-2: fix A's per-tile Start call and count lookup were the merge's
        /// remaining cost per walked tile).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MergeCells(Chunk chunk)
        {
            DynamicTileIndex dyn = chunk.Dynamic;

            return dyn == null ? 0 : dyn.Cells;
        }

        /// <summary>Where a render-list walk starts. A whole-tile walk (<paramref name="single"/> false) with dynamics on the tile
        /// starts the merged order at the tile's head (<paramref name="merged"/> true: continue with <see cref="Next"/>);
        /// otherwise it walks the tile's list from its head by TNext from <paramref name="list"/>. A single-object walk (one
        /// object submitted, the Gpu item cache and mobile calls) never merges and ends after <paramref name="first"/>
        /// (<paramref name="list"/> null; Codex P2: a tail mobile's call restarted at the tile head). The render walk takes the
        /// same decision from its caller's <see cref="MergeCells"/> and starts with <see cref="StartTile"/>.</summary>
        public static GameObject Start(GameObject first, Chunk chunk, bool single, out TileWalk walk, out bool merged, out TileListObject list)
        {
            merged = !single && (MergeCells(chunk) >> first.TileCell & 1) != 0;

            return StartTile(first, chunk, merged, single, out walk, out list);
        }

        /// <summary>A walk of <paramref name="first"/>'s tile: the merged order from the tile's head when
        /// <paramref name="merged"/> (continue with <see cref="Next"/>), <paramref name="first"/> alone when
        /// <paramref name="single"/> (<paramref name="list"/> null: continue with list?.TNext), else the list from the tile's
        /// head (<paramref name="list"/>, the walk's current object typed as a list object, kept in a local by the caller:
        /// continue with list.TNext). A whole-tile walk starts at the tile's first object (<see cref="First"/>), which is the
        /// list's head unless the tile holds only dynamics, and then it merges.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GameObject StartTile(GameObject first, Chunk chunk, bool merged, bool single, out TileWalk walk, out TileListObject list)
        {
            if (merged)
            {
                list = null;

                return Begin(chunk, first.TileCell, out walk);
            }

            walk = default;

            if (single)
            {
                list = null;

                return first;
            }

            return list = chunk.HeadAt(first.TileCell);
        }

        /// <summary>A whole-tile walk of (<paramref name="chunk"/>, <paramref name="cell"/>) by the chunk's
        /// <see cref="MergeCells"/>: the merged order (<paramref name="merged"/> true, continue with <see cref="Next"/>) or the
        /// list from its head (<paramref name="list"/>, the walk's current object typed as a list object: continue with
        /// list.TNext).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GameObject Open(Chunk chunk, int cell, ulong mergeCells, out TileWalk walk, out bool merged, out TileListObject list)
        {
            merged = (mergeCells >> cell & 1) != 0;

            if (merged)
            {
                list = null;

                return Begin(chunk, cell, out walk);
            }

            walk = default;

            return list = chunk.HeadAt(cell);
        }

        /// <summary>
        /// The tile's first object for a whole-tile walk: the list's head, else (a tile holding only dynamics: no land, no
        /// anchor) the index's first entry, else null. A starting point for <see cref="Start"/>, which reads only its cell.
        /// </summary>
        public static GameObject First(Chunk chunk, int x, int y)
        {
            GameObject head = chunk.GetHeadObject(x, y);

            if (head == null && chunk.Dynamic != null && chunk.Dynamic.CountAt(x << 3 | y) > 0)
            {
                head = chunk.Dynamic.RawCell(x << 3 | y, out _)[0].Obj;
            }

            return head;
        }

        /// <summary>The first object of (<paramref name="chunk"/>, <paramref name="cell"/>) in merged order, or null.</summary>
        public static GameObject Begin(Chunk chunk, int cell, out TileWalk walk)
        {
            walk = default;
            walk._s = chunk.HeadAt(cell);

            if (chunk.Dynamic != null)
            {
                walk._d = chunk.Dynamic.RawCell(cell, out walk._dn);
            }

            return walk.Next();
        }

        /// <summary>
        /// Whether the index entry <paramref name="key"/> comes before the list object <paramref name="s"/>. A list object's key
        /// priority is its stored PriorityZ (<see cref="TileOrder.Key"/>), so only an equal priority needs the rank (type tests)
        /// and the stamp: the merge no longer builds a full key for every list object it passes (tile-index-2).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Before(in TileOrderKey key, GameObject s)
        {
            short p = s.PriorityZ;

            if (key.Priority != p)
            {
                return key.Priority < p;
            }

            return key.CompareTo(TileOrder.Key(s)) < 0;
        }

        public GameObject Next()
        {
            TileListObject s = _s;

            if (_di < _dn && (s == null || Before(in _d[_di].Key, s)))
            {
                return _d[_di++].Obj;
            }

            if (s != null)
            {
                _s = s.TNext;
            }

            return s;
        }
    }

    /// <summary>
    /// T6b part 2: one tile's objects for the readers that are not in the render hot path (GetMapZ, the pathfinder, the
    /// foliage, lights, targeting, the max-Z decision, sitting): <see cref="TileWalk"/>'s merged order, from the tile's head or
    /// from a given object. A chunk without an index walks its list alone.
    /// </summary>
    internal struct TileReader
    {
        private TileWalk _walk;
        private GameObject _cur;
        private bool _merged;

        /// <summary>The first object at or after <paramref name="from"/> (the tile's head when null), or null; continue with
        /// <see cref="Next"/>.</summary>
        public static GameObject Open(Chunk chunk, int cell, GameObject from, out TileReader reader)
        {
            reader = default;

            if (chunk == null)
            {
                return null;
            }

            reader._merged = chunk.Dynamic != null;

            if (!reader._merged)
            {
                reader._cur = from ?? chunk.HeadAt(cell);

                return reader._cur;
            }

            GameObject obj = TileWalk.Begin(chunk, cell, out reader._walk);

            while (from != null && obj != null && !ReferenceEquals(obj, from))
            {
                obj = reader._walk.Next();
            }

            return obj;
        }

        public GameObject Next()
        {
            if (_merged)
            {
                return _walk.Next();
            }

            // a chunk without an index holds no dynamics; a dynamic passed as the start has no list links
            return _cur = (_cur as TileListObject)?.TNext;
        }
    }
}
