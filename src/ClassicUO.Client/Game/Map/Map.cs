// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Assets;

namespace ClassicUO.Game.Map
{
    internal sealed class Map
    {
        private static Chunk[] _terrainChunks;
        private static readonly bool[] _blockAccessList = new bool[0x1000];
        private readonly LinkedList<int> _usedIndices = new LinkedList<int>();
        private readonly World _world;

        // ---- incremental chunk loading (UpdateLoading, GetChunkForDraw, Park) ----
        //
        // Rule: callers that need a chunk's data now (game logic: GetChunk/GetChunk2/GetTile with load = true, GetMapZ,
        // GetTileZ) load it synchronously, as before; if its region is still being decoded by a prefetch they wait for the
        // rest of that decode. Two callers do not: the draw walk (GetChunkForDraw) and objects placed on a tile
        // (GameObject.AddToTile, which parks the object until its chunk is loaded). Their chunks are queued and loaded by
        // UpdateLoading, once per frame, nearest the player first, never waiting for a decode: the player's own chunk as
        // soon as its regions are decoded, the rest (the 3x3 around the player first) within a time budget that covers
        // the load and the mesh build (CUO_CHUNK_BUDGET_MS, default 2 ms; at least one chunk per frame). Pathfinding loads
        // the chunks within the view range on demand, so walking right after a teleport sees the same tiles as before.
        // CUO_CHUNK_SYNC=1 restores the old behaviour (every chunk loads when first asked for, no prefetch).
        //
        // First-sight art: a queued chunk is not loaded until the land, texmap and static art its block names is in the
        // atlases. The missing art is requested from the block data (decoded on worker threads, Art/Texmap
        // FirstSightDecoder) and uploaded by UpdateLoading inside the same budget, so the mesh build only reads art that
        // is already there. A chunk that game logic loads before its art is ready is marked ArtPending: the draw walk
        // skips it (and counts it in DrawPending, so the world-settle hold waits for it) until its art is there and
        // UpdateLoading builds its mesh. CUO_ART_SYNC=1 turns this off (art decodes in the mesh build, as before).
        public static readonly bool Incremental = Environment.GetEnvironmentVariable("CUO_CHUNK_SYNC") != "1";
        public static readonly bool ArtWait = Incremental && ClassicUO.Renderer.Arts.Art.AsyncFirstSight;
        public static readonly double BudgetMs = double.TryParse(Environment.GetEnvironmentVariable("CUO_CHUNK_BUDGET_MS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) ? ms : 2.0;
        // regions within this many tiles of the player are decoded ahead (the screen at 1920x1080 and zoom 1 reaches about
        // 40 tiles from the player; at 25 tiles/s, 64 tiles of margin is more than two seconds of travel)
        public const int PrefetchRadius = 104;

        private readonly ChunkLoadQueue _queue = new ChunkLoadQueue();
        private readonly Func<int, int, ChunkLoadResult> _tryLoad;
        private static readonly Func<long> _clock = Stopwatch.GetTimestamp;
        private uint _frame;

        // objects placed on a tile of a chunk that is not loaded yet, by block; added to the tile when it loads
        private struct Parked
        {
            public GameObject Obj;
            public byte X, Y;
        }

        private readonly Dictionary<int, List<Parked>> _parked = new Dictionary<int, List<Parked>>();
        private readonly Stack<List<Parked>> _parkedPool = new Stack<List<Parked>>();

        /// <summary>Chunks loaded this frame by UpdateLoading and by synchronous callers (FrameStats).</summary>
        public static int FrameChunkLoads;
        public static long FrameChunkLoadTicks;
        /// <summary>Bytes the main thread allocated inside chunk loads this frame, garbage collections that started inside one
        /// and their pause time (FrameStats chunk_alloc, chunk_gc, chunk_gc_us).</summary>
        public static long FrameChunkLoadBytes;
        public static int FrameChunkLoadGcs;
        public static long FrameChunkLoadGcTicks;
        /// <summary>Time UpdateLoading spent building the meshes of the chunks it loaded this frame (FrameStats).</summary>
        public static long FrameMeshTicks;


        public Map(World world, int index)
        {
            _world = world;
            Index = index;
            BlocksCount = Client.Game.UO.FileManager.Maps.MapBlocksSize[Index, 0] * Client.Game.UO.FileManager.Maps.MapBlocksSize[Index, 1];
            _tryLoad = TryLoadQueued;

            if (_terrainChunks == null || BlocksCount > _terrainChunks.Length)
                _terrainChunks = new Chunk[BlocksCount];

            ClearBockAccess();
        }

        public readonly int BlocksCount;
        public readonly int Index;


        public Chunk GetChunk(int block)
        {
            if (block >= 0 && block < BlocksCount)
            {
                return _terrainChunks[block];
            }

            return null;
        }

        public Chunk GetChunk(int x, int y, bool load = true)
        {
            if (x < 0 || y < 0)
            {
                return null;
            }

            int cellX = x >> 3;
            int cellY = y >> 3;

            return GetChunk2(cellX, cellY, load);
        }

        public Chunk GetChunk2(int chunkX, int chunkY, bool load = true)
        {
            int block = GetBlock(chunkX, chunkY);

            if (block >= BlocksCount || block >= _terrainChunks.Length)
            {
                return null;
            }

            ref Chunk chunk = ref _terrainChunks[block];

            if (chunk == null)
            {
                if (!load)
                {
                    return null;
                }

                LinkedListNode<int> node = _usedIndices.AddLast(block);
                chunk = Chunk.Create(_world, chunkX, chunkY);
                LoadChunk(chunk, block);
                chunk.Node = node;
            }
            else if (chunk.IsDestroyed)
            {
                // make sure node is clear
                if (chunk.Node != null && (chunk.Node.Previous != null || chunk.Node.Next != null))
                {
                    chunk.Node.List?.Remove(chunk.Node);
                }

                LinkedListNode<int> node = _usedIndices.AddLast(block);
                chunk.X = chunkX;
                chunk.Y = chunkY;
                LoadChunk(chunk, block);
                chunk.Node = node;
            }

            chunk.LastAccessTime = Time.Ticks;

            return chunk;
        }


        // the one place chunks get their land and statics; objects parked on the chunk follow
        private void LoadChunk(Chunk chunk, int block)
        {
            long t0 = Stopwatch.GetTimestamp();
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            int gcs0 = GC.CollectionCount(0);
            TimeSpan pause0 = GC.GetTotalPauseDuration();
            chunk.Load(Index);
            _blockArtWait.Forget(ChunkKey(chunk.X, chunk.Y));
            _chunkArtWait.Forget(ChunkKey(chunk.X, chunk.Y));
            chunk.ArtPending = ArtWait && RequestChunkArt(chunk) != ArtCheck.Ready;

            if (_parked.Remove(block, out var list))
            {
                // newest first: an object parked twice on this block (a mobile walking) goes to its last tile only
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var p = list[i];

                    if (p.Obj.PendingBlock == block && !p.Obj.IsDestroyed)
                    {
                        p.Obj.AddToTile(chunk, p.X, p.Y); // clears PendingBlock
                    }
                }

                list.Clear();
                _parkedPool.Push(list);
            }

            _queue.Remove(chunk.X, chunk.Y);
            FrameChunkLoads++;
            FrameChunkLoadTicks += Stopwatch.GetTimestamp() - t0;
            FrameChunkLoadBytes += GC.GetAllocatedBytesForCurrentThread() - bytes0;
            FrameChunkLoadGcs += GC.CollectionCount(0) - gcs0;
            FrameChunkLoadGcTicks += (GC.GetTotalPauseDuration() - pause0).Ticks;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsInMap(int chunkX, int chunkY)
        {
            var size = Client.Game.UO.FileManager.Maps.MapBlocksSize;

            return (uint)chunkX < (uint)size[Index, 0] && (uint)chunkY < (uint)size[Index, 1];
        }

        /// <summary>
        /// The draw walk's lookup: the chunk if it is loaded; otherwise null, and the chunk is queued for UpdateLoading.
        /// </summary>
        public Chunk GetChunkForDraw(int chunkX, int chunkY)
        {
            if (!Incremental)
            {
                return GetChunk2(chunkX, chunkY, true);
            }

            if (chunkX < 0 || chunkY < 0 || !IsInMap(chunkX, chunkY))
            {
                return null;
            }

            Chunk chunk = _terrainChunks[GetBlock(chunkX, chunkY)];

            if (chunk != null && !chunk.IsDestroyed)
            {
                chunk.LastAccessTime = Time.Ticks;

                if (!chunk.ArtPending)
                {
                    return chunk;
                }
            }

            _queue.Request(chunkX, chunkY, _frame);
            DrawPending++;

            return null;
        }

        /// <summary>
        /// Chunks the draw walk asked for since the caller last reset it that are not loaded yet (queued for UpdateLoading).
        /// GameScene resets it before its walk; while it is not 0 the visible area is not ready (WorldSettleGate).
        /// </summary>
        public int DrawPending;

        /// <summary>
        /// Called by GameObject.AddToTile: when the chunk of tile (x, y) is not loaded, removes <paramref name="obj"/> from
        /// its tile, keeps it until the chunk loads (then adds it to (x, y)) and returns true. False when the chunk is loaded
        /// (or the map has no such tile), so the caller adds the object as usual.
        /// </summary>
        public bool Park(GameObject obj, int x, int y)
        {
            if (!Incremental || x < 0 || y < 0)
            {
                return false;
            }

            int chunkX = x >> 3, chunkY = y >> 3;

            if (!IsInMap(chunkX, chunkY))
            {
                return false;
            }

            int block = GetBlock(chunkX, chunkY);
            Chunk chunk = _terrainChunks[block];

            if (chunk != null && !chunk.IsDestroyed)
            {
                return false;
            }

            obj.RemoveFromTile();
            obj.PendingBlock = block;

            if (!_parked.TryGetValue(block, out var list))
            {
                list = _parkedPool.Count > 0 ? _parkedPool.Pop() : new List<Parked>();
                _parked[block] = list;
            }

            list.Add(new Parked { Obj = obj, X = (byte)(x & 7), Y = (byte)(y & 7) });
            _queue.Request(chunkX, chunkY, _frame, sticky: true);

            return true;
        }

        public int QueuedChunks => _queue.Count;

        /// <summary>Starts decoding the regions around tile (x, y) on the thread pool (a teleport or position packet).</summary>
        public void Prefetch(int x, int y)
        {
            if (Incremental)
            {
                Client.Game.UO.FileManager.Maps.PrefetchAround(Index, x, y, PrefetchRadius);
            }
        }

        /// <summary>
        /// Once per frame, before the draw: prefetches the regions around the player and loads queued chunks (see the rule
        /// at the top of this class).
        /// </summary>
        public void UpdateLoading(int playerX, int playerY)
        {
            _frame++;

            if (!Incremental)
            {
                return;
            }

            var maps = Client.Game.UO.FileManager.Maps;
            maps.PrefetchAround(Index, playerX, playerY, PrefetchRadius);

            int pcx = playerX >> 3, pcy = playerY >> 3;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int cx = pcx + dx, cy = pcy + dy;

                    if (cx >= 0 && cy >= 0 && IsInMap(cx, cy))
                    {
                        Chunk c = _terrainChunks[GetBlock(cx, cy)];

                        if (c == null || c.IsDestroyed || c.ArtPending)
                        {
                            _queue.Request(cx, cy, _frame);
                        }
                    }
                }
            }

            long deadline = Stopwatch.GetTimestamp() + (long)(BudgetMs * Stopwatch.Frequency / 1000.0);

            if (ArtWait)
            {
                // atlas uploads of art the workers finished, inside the chunk budget (at least one each)
                Client.Game.UO.Arts.DrainReady(deadline);
                Client.Game.UO.Texmaps.DrainReady(deadline);
            }

            _queue.Drain(pcx, pcy, _frame, deadline, _clock, _tryLoad);

            // keys of chunks that left the queue unloaded; a forgotten key only costs one full scan
            _blockArtWait.Trim(4096);
            _chunkArtWait.Trim(4096);
        }

        // queued blocks waiting for art: the first sprite still missing (positions: land cells 0..63, then statics)
        private readonly ArtWaitTracker _blockArtWait = new ArtWaitTracker();
        // loaded ArtPending chunks: the art progress (uploads so far) at the last check
        private readonly ArtWaitTracker _chunkArtWait = new ArtWaitTracker();

        private static long ChunkKey(int chunkX, int chunkY) => ((long)chunkX << 32) | (uint)chunkY;

        // what a remembered "ready" depends on besides the uploads: the atlas contents (Art.Reload) and the season graphics
        private int ArtGeneration => Client.Game.UO.Arts.Generation * 16 + (int)_world.Season;

        private enum ArtCheck
        {
            Ready,
            /// <summary>Not ready, after a cheap check (counts nothing against the budget).</summary>
            Waiting,
            /// <summary>Not ready, after a full scan that requested the missing art (counts against the budget).</summary>
            Scanned
        }

        /// <summary>
        /// Whether the art that chunk (chunkX, chunkY)'s map block names (land art, texmaps, static art, with the current
        /// season's graphics) is in the atlases. The first check (and the first after a season change or atlas reload)
        /// requests everything missing at once; later checks resume at the first sprite still missing.
        /// </summary>
        private ArtCheck RequestBlockArt(int chunkX, int chunkY)
        {
            if (!Client.Game.UO.FileManager.Maps.TryGetBlock(Index, chunkX, chunkY, out var block))
            {
                return ArtCheck.Ready;
            }

            var arts = Client.Game.UO.Arts;
            var texmaps = Client.Game.UO.Texmaps;
            var landData = Client.Game.UO.FileManager.TileData.LandData;
            var season = _world.Season;
            long key = ChunkKey(chunkX, chunkY);
            int generation = ArtGeneration;
            int pos = _blockArtWait.Begin(key, generation, out bool full);
            int firstMissing = -1;
            int count = block.Land.Length + block.Statics.Length;

            for (; pos < count; pos++)
            {
                bool ready;

                if (pos < block.Land.Length)
                {
                    ushort graphic = SeasonManager.GetLandSeasonGraphic(season, block.Land[pos].TileId);

                    if (graphic <= 2)
                    {
                        continue; // not drawn (Land.AllowedToDraw)
                    }

                    // the texmap is used when the tile is stretched (it depends on the neighbours' z): ask for both. A
                    // failed speculative decode counts as ready, so this can't wait for (or throw on) art the old path
                    // never decoded
                    ushort tex = graphic < landData.Length ? landData[graphic].TexID : (ushort)0;
                    ready = arts.RequestLand(graphic) & (tex == 0 || texmaps.Request(tex));
                }
                else
                {
                    ready = arts.RequestArt(SeasonManager.GetSeasonGraphic(season, block.Statics[pos - block.Land.Length].Graphic));
                }

                if (!ready && firstMissing < 0)
                {
                    firstMissing = pos;

                    if (!full)
                    {
                        break;
                    }
                }
            }

            _blockArtWait.End(key, generation, firstMissing);

            return firstMissing < 0 ? ArtCheck.Ready : full ? ArtCheck.Scanned : ArtCheck.Waiting;
        }

        /// <summary>
        /// As <see cref="RequestBlockArt"/>, from a loaded chunk's objects: the art its mesh build and draw walk will look
        /// up (land art, texmaps of stretched land, static art). Walks the tiles again only when art was uploaded since the
        /// last check (or the generation changed).
        /// </summary>
        private ArtCheck RequestChunkArt(Chunk chunk)
        {
            var arts = Client.Game.UO.Arts;
            var texmaps = Client.Game.UO.Texmaps;
            long key = ChunkKey(chunk.X, chunk.Y);
            int generation = ArtGeneration;
            int progress = (arts.CompletedDecodes + texmaps.CompletedDecodes) & int.MaxValue;

            if (_chunkArtWait.Begin(key, generation, out bool full) == progress && !full)
            {
                return ArtCheck.Waiting;
            }

            bool ready = RequestChunkArtWalk(chunk);
            _chunkArtWait.End(key, generation, ready ? -1 : progress);

            return ready ? ArtCheck.Ready : ArtCheck.Scanned;
        }

        private bool RequestChunkArtWalk(Chunk chunk)
        {
            var arts = Client.Game.UO.Arts;
            var texmaps = Client.Game.UO.Texmaps;
            var landData = Client.Game.UO.FileManager.TileData.LandData;
            bool ready = true;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (var obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                    {
                        switch (obj)
                        {
                            case Land land when land.AllowedToDraw:
                                ready &= arts.RequestLand(land.Graphic);

                                if (land.IsStretched && land.Graphic < landData.Length)
                                {
                                    ready &= texmaps.Request(landData[land.Graphic].TexID);
                                }

                                break;

                            case Static st:
                                ready &= arts.RequestArt(st.Graphic);

                                break;
                        }
                    }
                }
            }

            return ready;
        }

        private ChunkLoadResult TryLoadQueued(int chunkX, int chunkY)
        {
            if (!IsInMap(chunkX, chunkY))
            {
                return ChunkLoadResult.Done;
            }

            int block = GetBlock(chunkX, chunkY);
            Chunk chunk = _terrainChunks[block];

            if (chunk != null && !chunk.IsDestroyed)
            {
                if (!chunk.ArtPending)
                {
                    return ChunkLoadResult.Done;
                }

                // loaded by game logic before its art was there: draw it once the art is uploaded
                var check = RequestChunkArt(chunk);

                if (check != ArtCheck.Ready)
                {
                    return check == ArtCheck.Scanned ? ChunkLoadResult.Scanned : ChunkLoadResult.NotReady;
                }

                chunk.ArtPending = false;
                BuildMeshQueued(chunk);

                return ChunkLoadResult.Loaded;
            }

            // queued for parked objects that have all moved on or gone: nothing to load (if the chunk is on screen, the
            // draw walk asks for it again)
            if (_parked.TryGetValue(block, out var list) && !HasLiveParked(list, block))
            {
                _parked.Remove(block);
                list.Clear();
                _parkedPool.Push(list);

                return ChunkLoadResult.Done;
            }

            if (!Client.Game.UO.FileManager.Maps.IsBlockReady(Index, chunkX, chunkY))
            {
                return ChunkLoadResult.NotReady;
            }

            // the art first (free while it decodes on the workers), so the mesh build below reads no art for the first time
            if (ArtWait)
            {
                var check = RequestBlockArt(chunkX, chunkY);

                if (check != ArtCheck.Ready)
                {
                    return check == ArtCheck.Scanned ? ChunkLoadResult.Scanned : ChunkLoadResult.NotReady;
                }
            }

            chunk = GetChunk2(chunkX, chunkY, true);

            if (chunk != null && !chunk.ArtPending)
            {
                BuildMeshQueued(chunk);
            }

            return ChunkLoadResult.Loaded;
        }

        // Builds the chunk's mesh here, inside the budget: the draw walk would otherwise build every chunk that loaded this
        // frame at once (about 230 us per chunk on first sight, most of it art lookups, which ArtWait moves to workers). The
        // draw walk still builds meshes that go dirty later (objects added, season change). A chunk drawn for the first
        // time is marked fresh by the Gpu prepare whether or not it built the mesh itself.
        private void BuildMeshQueued(Chunk chunk)
        {
            if (chunk.Mesh.IsDirty && Client.Game.GraphicsDevice != null)
            {
                long t0 = Stopwatch.GetTimestamp();
                chunk.Mesh.Build(chunk, _world, Client.Game.GraphicsDevice);
                chunk.GpuMaskDirty = true;
                FrameMeshTicks += Stopwatch.GetTimestamp() - t0;
            }
        }

        private static bool HasLiveParked(List<Parked> list, int block)
        {
            foreach (var p in list)
            {
                if (p.Obj.PendingBlock == block && !p.Obj.IsDestroyed)
                {
                    return true;
                }
            }

            return false;
        }

        public GameObject GetTile(int x, int y, bool load = true)
        {
            return GetChunk(x, y, load)?.GetHeadObject(x % 8, y % 8);
        }

        public sbyte GetTileZ(int x, int y)
        {
            // MapLoader caches the last block, so ApplyStretch's 13 neighbour lookups per cell skip the lock and region lookup
            return Client.Game.UO.FileManager.Maps.GetLandZ(Index, x, y);
        }

        public void GetMapZ(int x, int y, out sbyte groundZ, out sbyte staticZ)
        {
            Chunk chunk = GetChunk(x, y);
            //var obj = GetTile(x, y);
            groundZ = staticZ = 0;

            if (chunk == null)
            {
                return;
            }

            GameObject obj = chunk.Tiles[x % 8, y % 8];

            while (obj != null)
            {
                if (obj is Land)
                {
                    groundZ = obj.Z;
                }
                else if (staticZ < obj.Z)
                {
                    staticZ = obj.Z;
                }

                obj = obj.TNext;
            }
        }

        public void ClearBockAccess()
        {
            _blockAccessList.AsSpan().Fill(false);
        }

        public sbyte CalculateNearZ(sbyte defaultZ, int x, int y, int z)
        {
            ref bool access = ref _blockAccessList[(x & 0x3F) + ((y & 0x3F) << 6)];

            if (access)
            {
                return defaultZ;
            }

            access = true;
            Chunk chunk = GetChunk(x, y, false);

            if (chunk != null)
            {
                GameObject obj = chunk.Tiles[x % 8, y % 8];

                for (; obj != null; obj = obj.TNext)
                {
                    if (!(obj is Static) && !(obj is Multi))
                    {
                        continue;
                    }

                    if (obj.Graphic >= Client.Game.UO.FileManager.TileData.StaticData.Length)
                    {
                        continue;
                    }

                    if (!Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic].IsRoof || Math.Abs(z - obj.Z) > 6)
                    {
                        continue;
                    }

                    break;
                }

                if (obj == null)
                {
                    return defaultZ;
                }

                sbyte tileZ = obj.Z;

                if (tileZ < defaultZ)
                {
                    defaultZ = tileZ;
                }

                defaultZ = CalculateNearZ(defaultZ, x - 1, y, tileZ);
                defaultZ = CalculateNearZ(defaultZ, x + 1, y, tileZ);
                defaultZ = CalculateNearZ(defaultZ, x, y - 1, tileZ);
                defaultZ = CalculateNearZ(defaultZ, x, y + 1, tileZ);
            }

            return defaultZ;
        }


        public bool TryGetBlock(int blockX, int blockY, out MapBlockData block)
        {
            return Client.Game.UO.FileManager.Maps.TryGetBlock(Index, blockX, blockY, out block);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetBlock(int blockX, int blockY)
        {
            return blockX * Client.Game.UO.FileManager.Maps.MapBlocksSize[Index, 1] + blockY;
        }

        public IEnumerable<Chunk> GetUsedChunks()
        {
            foreach (int i in _usedIndices)
            {
                yield return GetChunk(i);
            }
        }


        public void ClearUnusedBlocks()
        {
            int count = 0;
            long ticks = Time.Ticks - Constants.CLEAR_TEXTURES_DELAY;

            LinkedListNode<int> first = _usedIndices.First;

            while (first != null)
            {
                LinkedListNode<int> next = first.Next;

                ref Chunk block = ref _terrainChunks[first.Value];

                if (block != null && block.LastAccessTime < ticks && block.HasNoExternalData())
                {
                    block.Destroy();
                    block = null;

                    if (++count >= Constants.MAX_MAP_OBJECT_REMOVED_BY_GARBAGE_COLLECTOR)
                    {
                        break;
                    }
                }

                first = next;
            }
        }

        public void Destroy()
        {
            LinkedListNode<int> first = _usedIndices.First;

            while (first != null)
            {
                LinkedListNode<int> next = first.Next;
                ref Chunk c = ref _terrainChunks[first.Value];
                c?.Destroy();
                c = null;
                first = next;
            }

            _usedIndices.Clear();

            // parked objects go the way of the objects in the loaded chunks (Chunk.Destroy): destroyed, except the player
            foreach (var kv in _parked)
            {
                foreach (var p in kv.Value)
                {
                    if (p.Obj.PendingBlock == kv.Key)
                    {
                        p.Obj.PendingBlock = -1;

                        if (!ReferenceEquals(p.Obj, _world.Player) && !p.Obj.IsDestroyed)
                        {
                            p.Obj.Destroy();
                        }
                    }
                }
            }

            _parked.Clear();
            _queue.Clear();
        }
    }
}