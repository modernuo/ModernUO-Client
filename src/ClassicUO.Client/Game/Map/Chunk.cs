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
        private static readonly Queue<Chunk> _pool = new Queue<Chunk>();

        private readonly World _world;

        public Chunk(World world)
        {
            _world = world;
        }

        public GameObject[,] Tiles { get; } = new GameObject[8, 8];
        public bool IsDestroyed;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnTileContentChanged(GameObject obj)
        {
            ContentVersion++;
            GpuMaskDirty = true;

            if (obj is not Mobile && !(GpuEffectsIgnored && obj is GameEffect))
            {
                GpuItemsDirty = true;
            }
        }


        /// <summary>Chunks allocated because the pool was empty (FrameStats chunk_new; reset by it).</summary>
        public static int Created;
        /// <summary>Chunks allocated over the session ([gcs] pool lines).</summary>
        public static long TotalCreated;
        public static int PooledCount => _pool.Count;

        /// <summary>[gcs] pool lines: how many pooled and used chunks have each statics mesh capacity.</summary>
        public static string DescribeMeshCapacities(Map map)
        {
            var hist = new System.Collections.Generic.SortedDictionary<int, int>();

            void Add(Chunk c)
            {
                int cap = c.Mesh.Statics.Vertices.Length;
                hist[cap] = hist.TryGetValue(cap, out int n) ? n + 1 : 1;
            }

            foreach (Chunk c in _pool)
            {
                Add(c);
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
            Chunk c;
            if (_pool.Count > 0)
            {
                c = _pool.Dequeue();
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

        public static void ClearPool()
        {
            while (_pool.Count > 0)
            {
                var chunk = _pool.Dequeue();
                chunk.Mesh.Clear();
            }
        }


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

            foreach (ref readonly var sb in block.Statics)
            {
                int pos = (sb.Y << 3) + sb.X;

                Static staticObject = Static.Create(_world, sb.Graphic, sb.Hue, pos, recycler.TakeStatic(now));
                staticObject.X = (ushort)(bx + sb.X);
                staticObject.Y = (ushort)(by + sb.Y);
                staticObject.Z = sb.Z;
                staticObject.UpdateScreenPosition();

                AddGameObject(staticObject, sb.X, sb.Y);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public GameObject GetHeadObject(int x, int y)
        {
            GameObject obj = Tiles[x, y];

            while (obj?.TPrevious != null)
            {
                obj = obj.TPrevious;
            }

            return obj;
        }

        public void AddGameObject(GameObject obj, int x, int y)
        {
            Mesh.MarkDirtyIfNeeded(obj);
            obj.RemoveFromTile();
            OnTileContentChanged(obj);
            obj.TileChunk = this;

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
                    ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];

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

            if (Tiles[x, y] == null)
            {
                Tiles[x, y] = obj;
                obj.TPrevious = null;
                obj.TNext = null;

                return;
            }


            GameObject o = Tiles[x, y];

            if (o == obj)
            {
                if (o.Previous != null)
                {
                    o = (GameObject) o.Previous;
                }
                else if (o.Next != null)
                {
                    o = (GameObject) o.Next;
                }
                else
                {
                    return;
                }
            }

            while (o?.TPrevious != null)
            {
                o = o.TPrevious;
            }

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
            }
        }

        public void RemoveGameObject(GameObject obj, int x, int y)
        {
            Mesh.MarkDirtyIfNeeded(obj);
            OnTileContentChanged(obj);

            if (obj != null && obj.TileChunk == this)
            {
                obj.TileChunk = null;
            }
            ref GameObject firstNode = ref Tiles[x, y];

            if (firstNode == null || obj == null)
            {
                return;
            }

            if (firstNode == obj)
            {
                firstNode = obj.TNext;
            }

            if (obj.TNext != null)
            {
                obj.TNext.TPrevious = obj.TPrevious;
            }

            if (obj.TPrevious != null)
            {
                obj.TPrevious.TNext = obj.TNext;
            }

            obj.TPrevious = null;
            obj.TNext = null;
        }


        public void Destroy()
        {
            MapObjectRecycler recycler = _world.MapRecycler;
            uint now = Time.Ticks;
            bool retired = false;

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

            if (Node != null)
            {
                if (Node.Next != null || Node.Previous != null)
                {
                    Node.List?.Remove(Node);
                }

                Node = null;
            }

            IsDestroyed = true;
            GpuItems?.Reset();
            Array.Clear(GpuMobiles);

            if (_pool.Count < Constants.PREDICTABLE_CHUNKS)
            {
                Mesh.SoftClear();
                _pool.Enqueue(this);
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
                }
            }

            Mesh.SoftClear();
            GpuItems?.Reset();
            Array.Clear(GpuMobiles);
        }

        public bool HasNoExternalData()
        {
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