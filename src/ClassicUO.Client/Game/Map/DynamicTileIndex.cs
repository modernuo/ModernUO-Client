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
    /// it. With the walk switch off (and the index maintained for a checker or effects) the tile's linked list still holds
    /// everything. With it on (T6b part 3, <see cref="ListDropped"/>) the list keeps only land, statics and multi components and
    /// the dynamics live only here: the walks read the list merged by key with this array (<see cref="TileWalk"/>), and
    /// CUO_TILE_ORDER_CHECK compares that merged order with the legacy order kept in a dev-only <see cref="ShadowTileList"/>.
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
        public static bool Maintain = ShouldMaintain(WalkOn, s_env == "0", TileOrder.Check, EffectSystem.GlobalOn);

        /// <summary>T6b part 3, dev builds: the walks read the shadow list (<see cref="ShadowTileList"/>, the legacy order) instead of
        /// the merged order: what "walk off" means once the list no longer holds the dynamics (<see cref="SetWalk"/>; pickscan's
        /// world_tile_index mode and the plans' tileindex:off). Needs <see cref="Shadow"/>.</summary>
        public static bool ShadowWalk;

        /// <summary>DevBench: the walk on (the merged order) or off. With the list kept, off is the list walk; with the list dropped,
        /// off is the shadow list (the legacy order, CUO_TILE_ORDER_CHECK only): false when that is not available.</summary>
        public static bool SetWalk(bool on)
        {
            if (!ListDropped)
            {
                WalkOn = on;
                ShadowWalk = false;

                return true;
            }

            if (!on && !Shadow)
            {
                return false;
            }

            WalkOn = true;
            ShadowWalk = !on;

            return true;
        }

        /// <summary>The walks read the index (the merged order), not the list or the shadow list.</summary>
        public static bool IndexWalkOn => (WalkOn || ListDropped) && !ShadowWalk;
#else
        public static readonly bool WalkOn = Configuration.Settings.GlobalSettings?.WorldTileIndex ?? false;
        public static bool Maintain = ShouldMaintain(WalkOn, false, false, EffectSystem.GlobalOn);
        public static bool IndexWalkOn => WalkOn || ListDropped;
#endif

        /// <summary>
        /// T6b part 3: with the walk switch on, the tile lists stop holding the dynamics (<see cref="Holds"/>): items, mobiles and
        /// (with ecs_effects) effects live only in the index; the list keeps land, statics and multi components. Read once at
        /// startup like <see cref="Maintain"/> (an object linked under one value and unlinked under the other would corrupt the
        /// list); tests set it before they build a world. Implies <see cref="Maintain"/>. Off (the default): nothing changes.
        /// </summary>
        public static bool ListDropped = WalkOn;

        /// <summary>
        /// T6b part 3: CUO_TILE_ORDER_CHECK with the list dropped keeps <see cref="ShadowTileList"/> per chunk (every object,
        /// dynamics included, inserted by the legacy list rule), so the checker and the readers' checks can still compare the
        /// merged order with the legacy list. Read once; tests set it.
        /// </summary>
        public static bool Shadow = ListDropped && TileOrder.Check;

        /// <summary>The walks read the merged order: the walk switch, or a dropped list (whose dynamics only the index holds).</summary>
        public static bool Merged
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => WalkOn || ListDropped;
        }

        /// <summary>Whether placements keep the index: the walk, an explicit CUO_WORLD_TILE_INDEX=0, the order checker, or held
        /// effects (ecs_effects holds them in the index even with the walk off).</summary>
        internal static bool ShouldMaintain(bool walkOn, bool envOff, bool orderCheck, bool effectsOn) => walkOn || envOff || orderCheck || effectsOn;

        /// <summary>Kept in the index (and skipped in the list by <see cref="TileWalk"/>). The extension point for T8: add
        /// GameEffect here, and the walks, the checker and the item cache's merged loops follow.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Holds(GameObject obj) => obj is Item || obj is Mobile || EffectsHeld && obj is GameEffect;

        /// <summary>T8 (<c>ecs_effects</c>): effects are kept in the index too (priority +2, T6a's key). Read once like
        /// <see cref="Maintain"/>: an effect placed under one value and removed under the other would leave its entry behind;
        /// tests set it before they build a world.</summary>
        public static bool EffectsHeld = EffectSystem.GlobalOn;

        private readonly Entry[][] _cells = new Entry[64][];
        private readonly int[] _counts = new int[64];

        /// <summary>Entries over all 64 tiles.</summary>
        public int Count { get; private set; }

        public int CountAt(int cell) => _counts[cell];

        /// <summary>Bit c set: tile c holds at least one entry. The whole-tile walks test it once per tile (a register test, see
        /// <see cref="TileWalk.MergeCells"/>) instead of reading the cell's count, so a tile without dynamics walks exactly as
        /// with the switch off.</summary>
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
    /// T6b: one tile's objects in the merged order, the list's non-dynamic objects (<see cref="DynamicTileIndex.Holds"/>
    /// false) merged by <see cref="TileOrderKey"/> with the tile's <see cref="DynamicTileIndex"/> array. While both are
    /// maintained this equals the list (CUO_TILE_ORDER_CHECK checks it); the walks use it when
    /// <see cref="DynamicTileIndex.WalkOn"/>.
    /// </summary>
    internal struct TileWalk
    {
        private GameObject _s;
        private DynamicTileIndex.Entry[] _d;
        private int _di, _dn;
#if CUO_DEVTOOLS
        // DynamicTileIndex.ShadowWalk: the shadow list's cell (the legacy order) instead of the merge
        private List<GameObject> _sh;
#endif

        /// <summary>
        /// The tiles of <paramref name="chunk"/> whose whole-tile walk takes the merged order (bit = cell): none with
        /// <paramref name="walkOn"/> false, the index's occupied cells (<see cref="DynamicTileIndex.Cells"/>) with it on. A tile
        /// without dynamics merges to its list, so it walks TNext exactly as with the switch off. The whole-tile walks (the
        /// render walk's chunk loop, the Gpu mask build, the item cache) read it once per chunk, so the per-tile decision is a
        /// test of a register (tile-index-2: fix A's per-tile Start call and count lookup were the switch's remaining cost per
        /// walked tile). Dev builds with <see cref="DynamicTileIndex.ShadowWalk"/>: every cell (the shadow list's order).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong MergeCells(Chunk chunk, bool walkOn)
        {
            if (!walkOn)
            {
                return 0;
            }

#if CUO_DEVTOOLS
            if (DynamicTileIndex.ShadowWalk)
            {
                return ulong.MaxValue;
            }
#endif
            DynamicTileIndex dyn = chunk.Dynamic;

            return dyn == null ? 0 : dyn.Cells;
        }

        /// <summary>Where a render-list walk starts. A whole-tile walk (<paramref name="single"/> false) with the walk on and
        /// dynamics on the tile starts the merged order at the tile's head (<paramref name="merged"/> true: continue with
        /// <see cref="Next"/>); otherwise the walk starts at <paramref name="first"/> and follows TNext. A single-object walk
        /// (one object submitted, the Gpu item cache and mobile calls) never merges: the object's TNext is null for the tile's
        /// last object, so it cannot tell the two apart (Codex P2: a tail mobile's call restarted at the tile head). The render
        /// walk takes the same decision from its caller's <see cref="MergeCells"/> and starts with <see cref="StartTile"/>.</summary>
        public static GameObject Start(GameObject first, Chunk chunk, bool single, bool walkOn, out TileWalk walk, out bool merged)
        {
            merged = !single && (MergeCells(chunk, walkOn) >> first.TileCell & 1) != 0;

            return StartTile(first, chunk, merged, out walk);
        }

        /// <summary>A walk from <paramref name="first"/>: the merged order of its tile from the tile's head when
        /// <paramref name="merged"/>, else <paramref name="first"/> itself (continue with TNext).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GameObject StartTile(GameObject first, Chunk chunk, bool merged, out TileWalk walk)
        {
            if (!merged)
            {
                walk = default;

                return first;
            }

            return Begin(chunk, first.TileCell, out walk);
        }

        /// <summary>A whole-tile walk of (<paramref name="chunk"/>, <paramref name="cell"/>) by the chunk's
        /// <see cref="MergeCells"/>: the merged order (<paramref name="merged"/> true, continue with <see cref="Next"/>) or the
        /// list from its head (continue with TNext).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GameObject Open(Chunk chunk, int cell, ulong mergeCells, out TileWalk walk, out bool merged)
        {
            merged = (mergeCells >> cell & 1) != 0;

            if (merged)
            {
                return Begin(chunk, cell, out walk);
            }

            walk = default;

            return chunk.HeadAt(cell);
        }

        /// <summary>The first object of (<paramref name="chunk"/>, <paramref name="cell"/>) in merged order, or null. Dev builds
        /// with <see cref="DynamicTileIndex.ShadowWalk"/>: the shadow list's order (the legacy list) instead.</summary>
        public static GameObject Begin(Chunk chunk, int cell, out TileWalk walk)
        {
#if CUO_DEVTOOLS
            if (DynamicTileIndex.ShadowWalk && chunk.Shadow != null)
            {
                walk = default;
                walk._sh = chunk.Shadow.Cell(cell) ?? s_emptyShadow;
                walk._dn = walk._sh.Count;

                return walk.Next();
            }
#endif

            return BeginIndex(chunk, cell, out walk);
        }

#if CUO_DEVTOOLS
        private static readonly List<GameObject> s_emptyShadow = new List<GameObject>();
#endif

        /// <summary>
        /// The tile's first object for a whole-tile walk: the list's head, else (a tile holding only dynamics once the list
        /// drops them: no land, no anchor) the index's first entry, else null. A starting point for <see cref="Start"/>, which
        /// reads only its cell.
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

        /// <summary><see cref="Begin"/> without the dev shadow walk: always the merged order (the checker's side).</summary>
        public static GameObject BeginIndex(Chunk chunk, int cell, out TileWalk walk)
        {
            walk = default;
            walk._s = SkipDynamic(chunk.HeadAt(cell));

            if (chunk.Dynamic != null)
            {
                walk._d = chunk.Dynamic.RawCell(cell, out walk._dn);
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
#if CUO_DEVTOOLS
            if (_sh != null)
            {
                return _di < _dn ? _sh[_di++] : null;
            }
#endif

            GameObject s = _s;

            if (_di < _dn && (s == null || Before(in _d[_di].Key, s)))
            {
                return _d[_di++].Obj;
            }

            if (s != null)
            {
                _s = SkipDynamic(s.TNext);
            }

            return s;
        }
    }

    /// <summary>
    /// T6b part 2: one tile's objects for the readers that are not in the render hot path (GetMapZ, the pathfinder, the
    /// foliage, lights, targeting, the max-Z decision, sitting). <paramref name="merged"/> false walks the tile list from its head
    /// (or from <c>from</c>); true walks <see cref="TileWalk"/>'s merged order (the list's other objects plus the index's
    /// array), starting at the same object. While both are maintained the two give the same sequence; the callers take the
    /// switch from <see cref="DynamicTileIndex.WalkOn"/> and CUO_TILE_ORDER_CHECK runs both and compares the results.
    /// </summary>
    internal struct TileReader
    {
        private TileWalk _walk;
        private GameObject _cur;
        private bool _merged;
        // T6b part 3: with the list dropped the list walk is the shadow list's (CUO_TILE_ORDER_CHECK)
        private List<GameObject> _shadow;
        private int _si;

        /// <summary>The first object at or after <paramref name="from"/> (the tile's head when null), or null; continue with
        /// <see cref="Next"/>.</summary>
        public static GameObject Open(Chunk chunk, int cell, bool merged, GameObject from, out TileReader reader)
        {
            reader = default;

            if (chunk == null)
            {
                return null;
            }

            // the list without its dynamics is not "the list walk": with the list dropped, not merged means the shadow list (the
            // legacy order, kept only under the checker), and without one the merged order
            if (!merged && DynamicTileIndex.ListDropped && chunk.Shadow != null)
            {
                List<GameObject> cellList = chunk.Shadow.Cell(cell);
                int i = 0;

                if (cellList != null && from != null)
                {
                    while (i < cellList.Count && !ReferenceEquals(cellList[i], from))
                    {
                        i++;
                    }
                }

                if (cellList == null || i >= cellList.Count)
                {
                    return null;
                }

                reader._shadow = cellList;
                reader._si = i + 1;

                return cellList[i];
            }

            reader._merged = (merged || DynamicTileIndex.ListDropped) && chunk.Dynamic != null;

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
            if (_shadow != null)
            {
                return _si < _shadow.Count ? _shadow[_si++] : null;
            }

            if (_merged)
            {
                return _walk.Next();
            }

            return _cur = _cur?.TNext;
        }
    }

    /// <summary>
    /// T6b part 3, CUO_TILE_ORDER_CHECK with the list dropped (<see cref="DynamicTileIndex.Shadow"/>): per tile, every object
    /// (land, statics, multis and the dynamics the list no longer holds) in the legacy list order. Kept by
    /// <see cref="Chunk.AddGameObject"/> with the list's insertion rule applied verbatim (the scan from the head stops at the first
    /// object with a higher stored priority, or an equal one when the new object is land, or a state-1 multi and the other is
    /// not land; the new object goes there) and emptied by identity in <see cref="GameObject.RemoveFromTile"/>. It is an
    /// independent copy of the old list, derived from neither the index nor the T6a key, so the checker still proves the
    /// merged order equals the legacy order. Dev tooling only: one list per occupied tile.
    /// </summary>
    internal sealed class ShadowTileList
    {
        private readonly List<GameObject>[] _cells = new List<GameObject>[64];

        public List<GameObject> Cell(int cell) => _cells[cell];

        public void Add(GameObject obj, int cell, short priorityZ, sbyte state)
        {
            List<GameObject> list = _cells[cell] ??= new List<GameObject>(4);
            int i = 0;

            for (; i < list.Count; i++)
            {
                GameObject o = list[i];
                int testPriorityZ = o.PriorityZ;

                if (testPriorityZ > priorityZ || testPriorityZ == priorityZ && (state == 0 || state == 1 && !(o is Land)))
                {
                    break;
                }
            }

            list.Insert(i, obj);
        }

        public bool Remove(GameObject obj, int cell)
        {
            List<GameObject> list = _cells[cell];

            if (list == null)
            {
                return false;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], obj))
                {
                    list.RemoveAt(i);

                    return true;
                }
            }

            return false;
        }

        public bool Contains(GameObject obj, int cell)
        {
            List<GameObject> list = _cells[cell];

            if (list != null)
            {
                foreach (GameObject o in list)
                {
                    if (ReferenceEquals(o, obj))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void Clear()
        {
            foreach (List<GameObject> list in _cells)
            {
                list?.Clear();
            }
        }
    }
}
