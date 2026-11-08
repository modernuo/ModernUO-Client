// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;
using ClassicUO.Utility;
using System;
using System.Runtime.InteropServices;
using System.Buffers;

namespace ClassicUO.Game.Map
{
    internal sealed class Chunk
    {
        private readonly World _world;

        public Chunk(World world)
        {
            _world = world;
        }

        /// <summary>The World this chunk was built for; only that World's <see cref="ChunkPool"/> keeps or hands it out.</summary>
        public World Owner => _world;

        public GameObject[,] Tiles { get; } = new GameObject[8, 8];

        // tile-head: the first object of each cell's list (index x * 8 + y), kept by AddGameObject, ReleaseAnchor and the
        // teardowns, so the whole-tile walks start without following TPrevious from the anchor. CUO_TILE_ORDER_CHECK compares
        // it with the head found by TPrevious (TileOrder.CheckTile, HeadWrong).
        private readonly GameObject[] _heads = new GameObject[64];
        public bool IsDestroyed;

        /// <summary>Held by its World's <see cref="ChunkPool"/> (set and cleared by the pool): a second Destroy while pooled is a no-op,
        /// so the same chunk can never be handed out twice.</summary>
        internal bool InPool;
        public long LastAccessTime;
        public LinkedListNode<int> Node;
        public readonly ChunkMesh Mesh = new ChunkMesh();

        public int X;
        public int Y;

        /// <summary>
        /// Loaded, but some of its land or static art is still decoding on a worker (Map.LoadChunk): the draw walk skips
        /// it (Map.GetChunkForDraw) until Map.UpdateLoading finds the art uploaded and builds its mesh.
        /// </summary>
        public bool ArtPending;

        // Gpu world prepare (GameSceneGpuPrepare): which tiles (bit x * 8 + y) still need the per-frame walk. Rebuilt from
        // the tile lists when dirty (objects added or removed, mesh rebuilt, text added to a land tile or static).
        public bool GpuMaskDirty = true;
        public int GpuMaskGeneration;
        public ulong GpuWalkMask, GpuFullMask;
        // extents of the chunk's land and statics, for the targeted picking and screen-edge bands
        public int GpuMinZ, GpuMaxZ, GpuMaxHalfWidth, GpuMaxHeight;
        public uint GpuSeenFrame = uint.MaxValue - 1;
        public int GpuFreshSteps;
        // W4 (GameSceneGpuItemCache): the chunk's settled ground items' sprite instances; GpuItemsDirty = re-extract
        /// <summary>T6b: the items and mobiles per tile, sorted by key (null until the first one when maintained).</summary>
        public DynamicTileIndex Dynamic;
        /// <summary>T6b part 3: the legacy list order of every object per tile, kept only under CUO_TILE_ORDER_CHECK with the
        /// list dropped (<see cref="DynamicTileIndex.Shadow"/>); null otherwise.</summary>
        internal ShadowTileList Shadow;
        public Scenes.GpuItemCache GpuItems;
        public bool GpuItemsDirty = true;
        // tiles holding a mobile, and a mobile the walk's max Z stops at (rebuilt with the masks); a mobile on a cached
        // item tile is walked on its own
        public ulong GpuMobileMask, GpuMobileStopMask;
        // E6: tiles holding a graphic effect (rebuilt with the masks). A tile with a cached item is served and its effects
        // walked alone, in tile order.
        public ulong GpuEffectMask;

        /// <summary>E6 (gpu_prepare_item_cache_effects, set per frame by GameScene.GpuItemCacheBegin): effects joining or leaving a
        /// tile don't change which items are cached, like mobiles.</summary>
        public static bool GpuEffectsIgnored;
        // the mobiles of tile bit b, in tile-list order: GpuMobiles[GpuMobileStart[b] .. GpuMobileStart[b + 1])
        public GameObject[] GpuMobiles = Array.Empty<GameObject>();
        public readonly int[] GpuMobileStart = new int[65];

        /// <summary>An object joined or left one of the chunk's tile lists. Mobiles don't change which items are cached.</summary>
        /// <summary>W5: bumped whenever any object joins or leaves one of the chunk's tile lists, or the chunk is reused
        /// (MobileFrameCache keys a mobile's seat lookup on it).</summary>
        public uint ContentVersion;

        /// <summary>The Map that loaded this chunk (Map.LoadChunk): told about content changes, which can invalidate its
        /// max-Z decision (Map.NoteContentChanged). Cleared by Destroy.</summary>
        internal Map OwnerMap;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnTileContentChanged(GameObject obj)
        {
            ContentVersion++;
            GpuMaskDirty = true;

            if (obj is not Mobile)
            {
                // the max-Z decision skips mobiles; everything else on its tiles can change it
                OwnerMap?.NoteContentChanged(X, Y);

                if (!(GpuEffectsIgnored && obj is GameEffect))
                {
                    GpuItemsDirty = true;
                }
            }
        }


        /// <summary>Chunks allocated because the pool was empty (FrameStats chunk_new; reset by it).</summary>
        public static int Created;
        /// <summary>Chunks allocated over the session ([gcs] pool lines).</summary>
        public static long TotalCreated;

        /// <summary>[gcs] pool lines: how many pooled and used chunks have each statics mesh capacity.</summary>
        public static string DescribeMeshCapacities(World world, Map map)
        {
            var hist = new System.Collections.Generic.SortedDictionary<int, int>();

            void Add(Chunk c)
            {
                int cap = c.Mesh.Statics.Vertices.Length;
                hist[cap] = hist.TryGetValue(cap, out int n) ? n + 1 : 1;
            }

            if (world != null)
            {
                foreach (Chunk c in world.ChunkPool.Chunks)
                {
                    Add(c);
                }
            }

            if (map != null)
            {
                foreach (Chunk c in map.GetUsedChunks())
                {
                    if (c != null)
                    {
                        Add(c);
                    }
                }
            }

            var sb = new System.Text.StringBuilder();

            foreach (var kv in hist)
            {
                sb.Append(' ').Append(kv.Key).Append(':').Append(kv.Value);
            }

            return sb.ToString();
        }

        public static Chunk Create(World world, int x, int y)
        {
            Chunk c = world.ChunkPool.Take();
            if (c != null)
            {
                c.IsDestroyed = false;
            }
            else
            {
                c = new Chunk(world);
                Created++;
                TotalCreated++;
            }

            c.LastAccessTime = Time.Ticks + Constants.CLEAR_TEXTURES_DELAY;
            c.X = x;
            c.Y = y;
            c.GpuMaskDirty = true;
            c.GpuItemsDirty = true;
            c.GpuSeenFrame = uint.MaxValue - 1;
            c.ContentVersion++;

            return c;
        }

        /// <summary>
        /// Login warm-up (<see cref="MapWarmup"/>): fills the pool up to <paramref name="count"/> chunks (at most
        /// <see cref="Constants.PREDICTABLE_CHUNKS"/>) with statics mesh layers sized for <paramref name="staticsCapacity"/>
        /// sprites, so the first teleports take pooled chunks instead of allocating them and their mesh arrays while playing.
        /// A new chunk is in the same state as a pooled one (Destroy: no tiles, no node, cleared mesh). Returns the chunks added.
        /// </summary>
        public static int Prefill(World world, int count, int staticsCapacity)
            => world.ChunkPool.Prefill(count, staticsCapacity);


        public void Load(int index)
        {
            IsDestroyed = false;

            Map map = _world.Map;

            if (!Client.Game.UO.FileManager.Maps.TryGetBlock(index, X, Y, out var block))
            {
                return;
            }

            int bx = X << 3;
            int by = Y << 3;
            MapObjectRecycler recycler = _world.MapRecycler;
            uint now = Time.Ticks;

            for (int y = 0; y < 8; ++y)
            {
                int pos = y << 3;
                ushort tileY = (ushort)(by + y);

                for (int x = 0; x < 8; ++x, ++pos)
                {
                    ushort tileID = block.Land[pos].TileId;
                    sbyte z = block.Land[pos].Z;

                    Land land = Land.Create(_world, tileID, recycler.TakeLand(now));

                    ushort tileX = (ushort)(bx + x);

                    land.ApplyStretch(map, tileX, tileY, z);
                    land.X = tileX;
                    land.Y = tileY;
                    land.Z = z;
                    land.UpdateScreenPosition();

                    AddGameObject(land, x, y);
                }
            }

            bool gargoyle = GameObject.PlayerIsGargoyle(_world);

            foreach (ref readonly var sb in block.Statics)
            {
                int pos = (sb.Y << 3) + sb.X;

                Static staticObject = Static.Create(_world, sb.Graphic, sb.Hue, pos, gargoyle, recycler.TakeStatic(now));
                staticObject.X = (ushort)(bx + sb.X);
                staticObject.Y = (ushort)(by + sb.Y);
                staticObject.Z = sb.Z;
                staticObject.UpdateScreenPosition();

                AddGameObject(staticObject, sb.X, sb.Y);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject GetHeadObject(int x, int y) => _heads[(x << 3) | y];

        /// <summary><see cref="GetHeadObject"/> by cell (x * 8 + y).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject HeadAt(int cell) => _heads[cell];

        /// <summary>The static tiledata AddGameObject's priority reads: the loaded array (Client's FileManager setup assigns
        /// it once after TileDataLoader.Load, which is the only place that array is replaced); unit tests set their own.</summary>
        internal static StaticTiles[] StaticTileData = Array.Empty<StaticTiles>();

        public void AddGameObject(GameObject obj, int x, int y)
        {
            Mesh.MarkDirtyIfNeeded(obj);
            obj.RemoveFromTile();
            OnTileContentChanged(obj);
            obj.TileChunk = this;
            obj.TileCell = (byte)((x << 3) | y);

            short priorityZ = obj.Z;
            sbyte state = -1;

            ushort graphic = obj.Graphic;

            switch (obj)
            {
                case Land tile:

                    if (tile.IsStretched)
                    {
                        priorityZ = (short) (tile.AverageZ - 1);
                    }
                    else
                    {
                        priorityZ--;
                    }

                    priorityZ -= 1;

                    state = 0;

                    break;

                case Mobile _:
                    priorityZ++;

                    break;

                case Item item:

                    if (item.IsCorpse)
                    {
                        priorityZ++;

                        break;
                    }
                    else if (item.IsMulti)
                    {
                        graphic = item.MultiGraphic;
                    }

                    goto default;

                case GameEffect _:
                    priorityZ += 2;

                    break;

                case Multi m:

                    state = 1;

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL) != 0)
                    {
                        priorityZ--;

                        break;
                    }

                    if ((m.State & CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_PREVIEW) != 0)
                    {
                        state = 2;
                        priorityZ++;
                    }
                    //else if ((m.ItemData.Flags & TileFlag.StairRight) != 0)
                    //{
                    //    priorityZ++;
                    //}

                    //if (m.IsMovable)
                    //{
                    //    priorityZ += 1;
                    //}

                    goto default;

                default:
                    ref StaticTiles data = ref StaticTileData[graphic];

                    if (data.IsBackground)
                    {
                        priorityZ--;
                    }

                    //if (data.IsSurface)
                    //{
                    //    priorityZ--;
                    //}

                    if (data.Height != 0)
                    {
                        priorityZ++;
                    }

                    if (data.IsMultiMovable)
                    {
                        priorityZ++;
                    }

                    break;
            }

            obj.PriorityZ = priorityZ;
            // T6a: insertion stamp; TileOrder.Key(obj) predicts this list's order from it (CUO_TILE_ORDER_CHECK)
            obj.TileSeq = ++TileOrder.NextSeq;

            // T6b: the dynamic index files the object by the key just stamped (at the same point in update order)
            if (DynamicTileIndex.Shadow)
            {
                // CUO_TILE_ORDER_CHECK with the list dropped: the legacy list order, every object (ShadowTileList)
                (Shadow ??= new ShadowTileList()).Add(obj, obj.TileCell, priorityZ, state);
            }

            if ((DynamicTileIndex.Maintain || DynamicTileIndex.ListDropped) && DynamicTileIndex.Holds(obj))
            {
                (Dynamic ??= new DynamicTileIndex()).Add(obj, obj.TileCell);

                if (DynamicTileIndex.ListDropped)
                {
                    // T6b part 3: the dynamics live only in the index; the list (and its anchor) keeps land, statics, multis
                    obj.TPrevious = null;
                    obj.TNext = null;

                    return;
                }
            }

            int cell = obj.TileCell;

            if (Tiles[x, y] == null)
            {
                Tiles[x, y] = obj;
                _heads[cell] = obj;
                obj.TPrevious = null;
                obj.TNext = null;

                return;
            }


            // Tiles[x, y] is never obj here: RemoveFromTile above moved the anchor off it (ReleaseAnchor). The old code had an
            // "o == obj" branch for a stale anchor that followed the LinkedObject container links (Previous/Next), which on a
            // landless tile could put obj into another tile's list (T6a audit).
            GameObject o = _heads[cell];
            GameObject found = null;
            GameObject start = o;

            while (o != null)
            {
                int testPriorityZ = o.PriorityZ;

                if (testPriorityZ > priorityZ || testPriorityZ == priorityZ && (state == 0 || state == 1 && !(o is Land)))
                {
                    break;
                }

                found = o;
                o = o.TNext;
            }

            if (found != null)
            {
                obj.TPrevious = found;
                GameObject next = found.TNext;
                obj.TNext = next;
                found.TNext = obj;

                if (next != null)
                {
                    next.TPrevious = obj;
                }
            }
            else if (start != null)
            {
                obj.TNext = start;
                start.TPrevious = obj;
                obj.TPrevious = null;
                _heads[cell] = obj;
            }
        }

        /// <summary>
        /// RemoveFromTile only (the one place objects leave a tile list): when <paramref name="obj"/> is the anchor of its
        /// cell (Tiles[x, y], the node the walk starts from), a surviving neighbour takes its place, or null when it was
        /// alone. Normally the anchor is the land, which never leaves; on a landless tile (a block without map data) the
        /// first object placed used to stay the anchor after leaving, which hid the rest of the list.
        /// </summary>
        public void ReleaseAnchor(GameObject obj)
        {
            ref GameObject anchor = ref Tiles[obj.TileCell >> 3, obj.TileCell & 7];

            if (ReferenceEquals(anchor, obj))
            {
                anchor = obj.TPrevious ?? obj.TNext;
            }

            // the head leaves: its successor becomes the head (null when it was alone); RemoveFromTile unlinks it next
            ref GameObject head = ref _heads[obj.TileCell];

            if (ReferenceEquals(head, obj))
            {
                head = obj.TNext;
            }
        }


        public void Destroy()
        {
            if (InPool)
            {
                return; // already destroyed and pooled
            }

            MapObjectRecycler recycler = _world.MapRecycler;
            uint now = Time.Ticks;
            bool retired = false;

            if (DynamicTileIndex.ListDropped && Dynamic != null && Dynamic.Count > 0)
            {
                // T6b part 3: the list no longer reaches the items, mobiles and effects; tear down in the merged order
                retired = TearDownMerged(recycler, now);
            }

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;
                        // map land and statics live only in tile lists (Chunk.Load): once destroyed and unlinked here they go
                        // to the recycler, which hands them out again after its quarantine (MapObjectRecycler invariants)
                        bool recycle = (first is Land || first is Static) && !first.IsDestroyed;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first.TileChunk = null;

                        if (recycle)
                        {
                            recycler.Retire(first, now);
                            retired = true;
                        }

                        first = next;
                    }

                    Tiles[i, j] = null;
                    _heads[i << 3 | j] = null;
                }
            }

            if (retired)
            {
                // the selection holders keep an object across a frame boundary (set in the draw walk, read in the next
                // Update and input handling); a destroyed map object there would be a different tile once reused
                if (SelectedObject.Object is GameObject sel && sel.IsDestroyed)
                {
                    SelectedObject.Object = null;
                }

                if (SelectedObject.LastLeftDownObject is GameObject down && down.IsDestroyed)
                {
                    SelectedObject.LastLeftDownObject = null;
                }
            }

            // an evicted chunk the max-Z decision read: recompute (it requests the chunk again and recomputes on its load)
            OwnerMap?.NoteContentChanged(X, Y);
            OwnerMap = null;

            if (Node != null)
            {
                if (Node.Next != null || Node.Previous != null)
                {
                    Node.List?.Remove(Node);
                }

                Node = null;
            }

            IsDestroyed = true;
            Dynamic?.Clear();
            Shadow?.Clear();
            GpuItems?.Reset();
            Array.Clear(GpuMobiles);

            if (_world.ChunkPool.CanAdd(this))
            {
                Mesh.SoftClear();
                _world.ChunkPool.Add(this);
            }
            else
            {
                Mesh.Clear();
            }
        }

        public void Clear()
        {
            Destroy();
        }

        private static List<GameObject> s_teardown = new List<GameObject>();

        /// <summary>
        /// T6b part 3 (list dropped): Destroy's and ClearForReload's per-object teardown, tile by tile in the merged order (the
        /// legacy list order: the list's land, statics and multis with the index's items, mobiles and effects), each object as the
        /// list loop below treats it: destroyed (the player excepted, which only leaves), unlinked, off the chunk, and land and
        /// statics retired to <paramref name="recycler"/> (null: no recycling, ClearForReload). Leaves every Tiles[x, y] null.
        /// </summary>
        private bool TearDownMerged(MapObjectRecycler recycler, uint now)
        {
            bool retired = false;
            List<GameObject> objects = s_teardown ?? new List<GameObject>();
            s_teardown = null; // re-entered (an object's Destroy tearing down another chunk): that call gets its own list

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    int cell = i << 3 | j;

                    if (Tiles[i, j] == null && Dynamic.CountAt(cell) == 0)
                    {
                        continue;
                    }

                    objects.Clear();

                    for (GameObject o = TileWalk.BeginIndex(this, cell, out TileWalk walk); o != null; o = walk.Next())
                    {
                        objects.Add(o);
                    }

                    foreach (GameObject o in objects)
                    {
                        bool recycle = recycler != null && (o is Land || o is Static) && !o.IsDestroyed;

                        if (!ReferenceEquals(o, _world.Player))
                        {
                            o.Destroy();
                        }

                        o.TPrevious = null;
                        o.TNext = null;
                        o.TileChunk = null;

                        if (recycle)
                        {
                            recycler.Retire(o, now);
                            retired = true;
                        }
                    }

                    Tiles[i, j] = null;
                    _heads[cell] = null;
                }
            }

            objects.Clear();
            s_teardown = objects;

            return retired;
        }

        /// <summary>
        /// Clears the chunk's tile objects for an in-place reload (UltimaLive block
        /// update) and marks the mesh dirty so it rebuilds — WITHOUT destroying or
        /// pooling the chunk itself. The chunk stays owned by the map: its
        /// <see cref="_terrainChunks"/> slot and <see cref="Node"/> are left intact.
        /// Using <see cref="Destroy"/>/<see cref="Clear"/> here would enqueue this
        /// chunk to the shared pool while it is still referenced by the map, so it
        /// would later be handed out by <see cref="Create"/> for a different block
        /// (double ownership) — the original block then renders a stale/empty mesh
        /// (black, world-locked, permanent).
        /// </summary>
        public void ClearForReload()
        {
            if (DynamicTileIndex.ListDropped && Dynamic != null && Dynamic.Count > 0)
            {
                // T6b part 3: the list no longer reaches the dynamics (no recycling here, as below)
                TearDownMerged(null, 0);
            }

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    GameObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        GameObject next = first.TNext;

                        if (!ReferenceEquals(first, _world.Player))
                        {
                            first.Destroy();
                        }

                        first.TPrevious = null;
                        first.TNext = null;
                        first.TileChunk = null;
                        first = next;
                    }

                    Tiles[i, j] = null;
                    _heads[i << 3 | j] = null;
                }
            }

            Mesh.SoftClear();
            Dynamic?.Clear();
            Shadow?.Clear();
            GpuItems?.Reset();
            Array.Clear(GpuMobiles);
        }

        public bool HasNoExternalData()
        {
            // T6b part 2: dynamics (items, mobiles, effects) are external data. With the list kept the list walk below finds them
            // too; with it dropped (part 3) this is the only place that sees them.
            if (DynamicTileIndex.Merged && Dynamic != null && Dynamic.Count > 0)
            {
                return false;
            }

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    for (GameObject obj = GetHeadObject(i, j); obj != null; obj = obj.TNext)
                    {
                        if (!(obj is Land) && !(obj is Static) /*&& !(obj is Multi)*/)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}