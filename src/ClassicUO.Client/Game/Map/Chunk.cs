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
            ResetCaps();
        }

        /// <summary>The World this chunk was built for; only that World's <see cref="ChunkPool"/> keeps or hands it out.</summary>
        public World Owner => _world;

        public TileListObject[,] Tiles { get; } = new TileListObject[8, 8];

        // tile-head: the first object of each cell's list (index x * 8 + y), kept by AddGameObject, ReleaseAnchor and the
        // teardowns, so the whole-tile walks start without following TPrevious from the anchor. CUO_TILE_ORDER_CHECK compares
        // it with the head found by TPrevious (TileOrder.CheckTile, HeadWrong).
        private readonly TileListObject[] _heads = new TileListObject[64];
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
        /// <summary>T6b: the items, mobiles and effects per tile, sorted by key (null until the first one).</summary>
        public DynamicTileIndex Dynamic;
        public Scenes.GpuItemCache GpuItems;
        public bool GpuItemsDirty = true;
        /// <summary>pa-gpusig (GpuItemGen): bumped when one of the chunk's items changes in place
        /// in a way its cached sprites read (GpuItemGen.Touch); the cache keeps the value it was built at.</summary>
        public uint GpuItemRenderGen;
        // tiles holding a mobile, and a mobile the walk's max Z stops at (rebuilt with the masks); a mobile on a cached
        // item tile is walked on its own
        public ulong GpuMobileMask, GpuMobileStopMask;
        // E6: tiles holding a graphic effect (rebuilt with the masks). A tile with a cached item is served and its effects
        // walked alone, in tile order.
        public ulong GpuEffectMask;

        /// <summary>E6 (set by GameScene.GpuItemCacheBegin): effects joining or leaving a
        /// tile don't change which items are cached, like mobiles.</summary>
        public static bool GpuEffectsIgnored;
        // the mobiles of tile bit b, in tile-list order: GpuMobiles[GpuMobileStart[b] .. GpuMobileStart[b + 1])
        public GameObject[] GpuMobiles = Array.Empty<GameObject>();
        public readonly int[] GpuMobileStart = new int[65];

        /// <summary>An object joined or left one of the chunk's tile lists. Mobiles don't change which items are cached.</summary>
        /// <summary>W5: bumped whenever any object joins or leaves one of the chunk's tile lists, or the chunk is reused
        /// (MobileFrameCache keys a mobile's seat lookup on it).</summary>
        public uint ContentVersion;

        /// <summary>light_occlusion_cache (GameSceneLightOcclusion): bumped when a static, multi piece or item joins or leaves
        /// one of the chunk's tile lists, an item changes in place (GpuItemGen.Touch), or the chunk is reused; mobiles and
        /// effects don't bump it. A light's cached occluder candidates are trusted while every chunk its search reached keeps
        /// the value it was built at.</summary>
        public uint OccluderVersion;

        // mobile_wall_order (Scenes.MobileWallOrder): per tile, what a mobile's wall scan could find there, so a mobile with
        // nothing around it skips the scan and an idle one keeps its last result. Pieces are statics, multi pieces and
        // non-multi items. CapTop: the highest top (z + tiledata height) of the pieces with height; CapZ: the highest z of
        // any piece; CapFlatZ: the highest z of the items without height (they count only as pieces overhead). CapVersion
        // changes whenever a piece with height, a static or a multi piece joins or leaves the chunk (and on load, reuse,
        // teardown and season change); CapFlatVersion when an item without height does (a dropped coin), so it alone does
        // not throw away the scans of the mobiles around. Versions are unique across chunks (s_capSeq), so a pooled chunk
        // reused elsewhere never matches an old value.
        internal readonly short[] CapTop = new short[64];
        internal readonly sbyte[] CapZ = new sbyte[64];
        internal readonly sbyte[] CapFlatZ = new sbyte[64];
        internal uint CapVersion, CapFlatVersion;
        private static uint s_capSeq;

        /// <summary>The last version handed to any chunk: unchanged means no chunk's cap summary changed and no chunk was
        /// loaded, reused or torn down since (MobileWallOrder's one-compare cache test).</summary>
        internal static uint CapSeq => s_capSeq;

        internal const short CapNone = short.MinValue;

        /// <summary>Forgets the cap summary (no pieces) under new versions: load, reuse, teardown.</summary>
        internal void ResetCaps()
        {
            Array.Fill(CapTop, CapNone);
            Array.Fill(CapZ, sbyte.MinValue);
            Array.Fill(CapFlatZ, sbyte.MinValue);
            CapVersion = ++s_capSeq;
            CapFlatVersion = ++s_capSeq;
        }

        // the scans' pieces (MobileWallOrder: Static, Multi, Item that is not a multi); mobiles, land and effects are not
        private static bool CapPiece(GameObject obj) => obj is Static || obj is Multi || obj is Item { IsMulti: false };

        private static int CapHeight(ushort graphic) => graphic < StaticTileData.Length ? StaticTileData[graphic].Height : 0;

        /// <summary>A piece joined tile <paramref name="cell"/> (x * 8 + y): the summary only grows.</summary>
        internal void CapAdded(GameObject obj, int cell)
        {
            if (!CapPiece(obj))
            {
                return;
            }

            int h = CapHeight(obj.Graphic);
            sbyte z = obj.Z;

            if (z > CapZ[cell])
            {
                CapZ[cell] = z;
            }

            if (h > 0)
            {
                CapTop[cell] = (short)Math.Max(CapTop[cell], z + h);
            }
            else if (obj is Item)
            {
                if (z > CapFlatZ[cell])
                {
                    CapFlatZ[cell] = z;
                }

                CapFlatVersion = ++s_capSeq;

                return;
            }

            CapVersion = ++s_capSeq;
        }

        /// <summary>A piece left tile <paramref name="cell"/>: the tile's summary is taken again from what is left on it.</summary>
        internal void CapRemoved(GameObject obj, int cell)
        {
            if (!CapPiece(obj))
            {
                return;
            }

            CapRebuildTile(cell);

            if (CapHeight(obj.Graphic) == 0 && obj is Item)
            {
                CapFlatVersion = ++s_capSeq;
            }
            else
            {
                CapVersion = ++s_capSeq;
            }
        }

        private void CapRebuildTile(int cell)
        {
            short top = CapNone;
            sbyte maxZ = sbyte.MinValue, flatZ = sbyte.MinValue;

            for (GameObject o = TileWalk.Begin(this, cell, out TileWalk walk); o != null; o = walk.Next())
            {
                if (!CapPiece(o))
                {
                    continue;
                }

                int h = CapHeight(o.Graphic);
                maxZ = Math.Max(maxZ, o.Z);

                if (h > 0)
                {
                    top = (short)Math.Max(top, o.Z + h);
                }
                else if (o is Item)
                {
                    flatZ = Math.Max(flatZ, o.Z);
                }
            }

            CapTop[cell] = top;
            CapZ[cell] = maxZ;
            CapFlatZ[cell] = flatZ;
        }

        /// <summary>Every tile's summary again under new versions (World.ChangeSeason swaps statics' graphics in place).</summary>
        internal void RebuildCaps()
        {
            for (int cell = 0; cell < 64; cell++)
            {
                CapRebuildTile(cell);
            }

            CapVersion = ++s_capSeq;
            CapFlatVersion = ++s_capSeq;
        }

        /// <summary>
        /// view_margin (GameScene.AddChunkLights): the chunk's light-emitting statics and multi components (LightEmitters:
        /// tiledata Light with a light shape), in no order, kept by AddGameObject and RemoveFromTile. With the switch on the
        /// light pass takes every static light from these lists (the walk adds none). CUO_VIEW_MARGIN_CHECK compares them with
        /// a walk of the tile lists.
        /// </summary>
        internal TileListObject[] LightSources = Array.Empty<TileListObject>();
        internal int LightSourceCount;
        internal Scenes.ChunkLightCache LightCache;

        // Graphic/season changes invalidate cached row data only. The reference list's membership and
        // offer order are owned exclusively by tile insertion/removal, even while chunk_light_cache is off.
        internal void LightGraphicChanged(TileListObject obj) => OccluderVersion++;

        /// <summary>
        /// view_margin (sprite margin): how far the art of the chunk's statics, multi components and items reaches past its
        /// anchor's 44 x 44 box, in world pixels: left and right of it, above it. Grows only (map statics at Load, multis and
        /// items as they join; reset when the chunk is loaded again), so the walk's per-chunk tile mask (GameScene
        /// FillGameObjectList) covers every sprite of the chunk that can reach the view. A growth after Load sets
        /// ViewMargin.OverhangGrew: tiles joining the walk get their positions recomputed.
        /// </summary>
        internal int StaticOverLeft, StaticOverRight, StaticOverUp;

        /// <summary>
        /// view_margin_walk: the tiles (bit x &lt;&lt; 3 | y) holding a static or multi piece whose art reaches past its tile
        /// (ViewMargin.ArtReachesPastTile) or a stretched land tile rising past the walk's slack (LandReachesPastTile): the only
        /// margin tiles without dynamics the walk visits. Set as objects join (land and statics at Load, multis later), cleared
        /// only when the chunk loads again: a tile whose tall object left stays walked, which is harmless.
        /// </summary>
        internal ulong ReachCells;

        /// <summary>light_occlusion (GameScene.CollectOccluders): the lowest and highest Z of the chunk's statics, multi
        /// components and items, grow-only like the overhang (empty: low above high), so a light's occluder search skips the
        /// chunks whose sprites cannot reach it whatever their art size or elevation.</summary>
        internal int OccZLo = int.MaxValue, OccZHi = int.MinValue;

        /// <summary>the fill's per-chunk band (GameScene, GameSceneWalkChunkZ): the lowest and highest Z any object of the chunk's tiles is
        /// anchored at (land: its Z, MinZ and stretched corners), empty while low is above high. Set from the tiles at load and
        /// at a mesh rebuild (<see cref="RecomputeWalkZ"/>), grown by every object joining a tile (AddGameObject) and every Z
        /// change of an object on one (GameObject.Z), never shrunk in between: no object of the chunk is outside it.</summary>
        internal int WalkZLo = int.MaxValue, WalkZHi = int.MinValue;
        /// <summary>The range the fill last banded this chunk with: a range that grew since brings tiles into the walk
        /// whose positions missed camera moves.</summary>
        internal int FillZLo, FillZHi;
        /// <summary>The fill (GameScene._fillSeq) that last banded this chunk; a chunk not banded at the previous fill may
        /// hold positions that missed camera moves.</summary>
        internal uint FillSeq = uint.MaxValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void NoteWalkZ(int z)
        {
            if (z < WalkZLo)
            {
                WalkZLo = z;
            }

            if (z > WalkZHi)
            {
                WalkZHi = z;
            }
        }

        /// <summary>The Z extent an object's anchor (and, for land, its stretched corners) can take on its tile.</summary>
        internal static void WalkZOf(GameObject obj, out int lo, out int hi)
        {
            lo = hi = obj.Z;

            // the walk and GpuLandVisible read MinZ only for stretched land
            if (obj is Land { IsStretched: true } land)
            {
                lo = Math.Min(lo, land.MinZ);
                hi = Math.Max(hi, Math.Max(Math.Max(land.YOffsets.Top, land.YOffsets.Bottom), Math.Max(land.YOffsets.Left, land.YOffsets.Right)) >> 2);
            }
        }

        private void NoteWalkZ(GameObject obj)
        {
            WalkZOf(obj, out int lo, out int hi);
            NoteWalkZ(lo);
            NoteWalkZ(hi);
        }

        /// <summary>The exact range of the objects on the chunk's tiles now (the list's and the dynamic index's).</summary>
        internal void RecomputeWalkZ()
        {
            WalkZLo = int.MaxValue;
            WalkZHi = int.MinValue;

            for (int cell = 0; cell < 64; cell++)
            {
                for (GameObject o = TileWalk.Begin(this, cell, out TileWalk walk); o != null; o = walk.Next())
                {
                    if (!o.IsDestroyed)
                    {
                        NoteWalkZ(o);
                    }
                }
            }
        }

        private bool _loading;

        private void RecordOverhang(ushort graphic, int z, int cell = -1)
        {
            if (z < OccZLo)
            {
                OccZLo = z;
            }

            if (z > OccZHi)
            {
                OccZHi = z;
            }

            ArtLoader arts = Client.Game?.UO?.FileManager?.Arts;

            if (arts == null || !arts.TrySize(graphic + 0x4000, out int w, out int h))
            {
                return;
            }

            if (cell >= 0 && Scenes.ViewMargin.ArtReachesPastTile(w, h))
            {
                ReachCells |= 1UL << cell;
            }

            int left = (w >> 1) - 22, right = w - (w >> 1) - 22, up = h - 44;

            if (left > StaticOverLeft || right > StaticOverRight || up > StaticOverUp)
            {
                StaticOverLeft = Math.Max(StaticOverLeft, left);
                StaticOverRight = Math.Max(StaticOverRight, right);
                StaticOverUp = Math.Max(StaticOverUp, up);

                if (!_loading)
                {
                    Scenes.ViewMargin.OverhangGrew = true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsLightSource(GameObject obj) => (obj is Static || obj is Multi) && Scenes.LightEmitters.Static(obj.Graphic);

        private void NoteLightSource(TileListObject node)
        {
            if (LightSourceCount == LightSources.Length)
            {
                Array.Resize(ref LightSources, Math.Max(4, LightSourceCount * 2));
            }

            LightSources[LightSourceCount++] = node;
        }

        /// <summary>RemoveFromTile: <paramref name="node"/> leaves the chunk (a no-op when it is not a light source).</summary>
        internal void ForgetLightSource(TileListObject node)
        {
            for (int i = LightSourceCount - 1; i >= 0; i--)
            {
                if (ReferenceEquals(LightSources[i], node))
                {
                    LightSources[i] = LightSources[--LightSourceCount];
                    LightSources[LightSourceCount] = null;

                    return;
                }
            }
        }

        private void ClearLightSources()
        {
            LightCache?.Dispose();
            Array.Clear(LightSources, 0, LightSourceCount);
            LightSourceCount = 0;
        }

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

                if (obj is not GameEffect)
                {
                    OccluderVersion++;
                }

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

        /// <summary>[gcs] pool lines: how many pooled and used chunks have each statics mesh capacity, and the bytes their mesh
        /// layers hold (managed arrays and GPU vertex buffers, pooled and used chunks apart).</summary>
        public static string DescribeMeshCapacities(World world, Map map)
        {
            var hist = new System.Collections.Generic.SortedDictionary<int, int>();
            long pooledHeld = 0, pooledGpu = 0, usedHeld = 0, usedGpu = 0;
            int used = 0;

            void Add(Chunk c, ref long held, ref long gpu)
            {
                int cap = c.Mesh.Statics.Vertices.Length;
                hist[cap] = hist.TryGetValue(cap, out int n) ? n + 1 : 1;
                held += c.Mesh.Land.HeldBytes + c.Mesh.Statics.HeldBytes;
                gpu += c.Mesh.Land.GpuBytes + c.Mesh.Statics.GpuBytes;
            }

            if (world != null)
            {
                foreach (Chunk c in world.ChunkPool.Chunks)
                {
                    Add(c, ref pooledHeld, ref pooledGpu);
                }
            }

            if (map != null)
            {
                foreach (Chunk c in map.GetUsedChunks())
                {
                    if (c != null)
                    {
                        Add(c, ref usedHeld, ref usedGpu);
                        used++;
                    }
                }
            }

            var sb = new System.Text.StringBuilder();

            foreach (var kv in hist)
            {
                sb.Append(' ').Append(kv.Key).Append(':').Append(kv.Value);
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"; mesh MB used {used} chunks {usedHeld / 1048576.0:0.00} (gpu {usedGpu / 1048576.0:0.00}), pooled {pooledHeld / 1048576.0:0.00} (gpu {pooledGpu / 1048576.0:0.00})");

            if (world != null)
            {
                var mp = world.MeshPool;
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $", spare sets {mp.SpareSets} {mp.SpareBytes / 1048576.0:0.00} (reused {mp.Reused}, created {mp.Created}, dropped {mp.Dropped})");
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
            c.OccluderVersion++;
            c.ResetCaps();

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
            WalkZLo = int.MaxValue;
            WalkZHi = int.MinValue;
            FillSeq = uint.MaxValue;

            Map map = _world.Map;

            if (!Client.Game.UO.FileManager.Maps.TryGetBlock(index, X, Y, out var block))
            {
                return;
            }

            int bx = X << 3;
            int by = Y << 3;
            MapObjectRecycler recycler = _world.MapRecycler;
            uint now = Time.Ticks;
            ReachCells = 0;

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
            StaticOverLeft = StaticOverRight = StaticOverUp = 0;
            OccZLo = int.MaxValue;
            OccZHi = int.MinValue;
            _loading = true;

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

            _loading = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TileListObject GetHeadObject(int x, int y) => _heads[(x << 3) | y];

        /// <summary><see cref="GetHeadObject"/> by cell (x * 8 + y).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TileListObject HeadAt(int cell) => _heads[cell];

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
            CapAdded(obj, obj.TileCell);
            NoteWalkZ(obj);

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
            // T6a: insertion stamp; TileOrder.Key(obj) orders the list and the index by it (CUO_TILE_ORDER_CHECK)
            obj.TileSeq = ++TileOrder.NextSeq;

            // T6b part 3: the dynamics live only in the index, filed by the key just stamped; the list (and its anchor) keeps
            // land, statics and multis
            if (obj is not TileListObject node)
            {
                (Dynamic ??= new DynamicTileIndex()).Add(obj, obj.TileCell);

                if (obj is Item { IsMulti: false } overItem)
                {
                    RecordOverhang(overItem.Graphic, overItem.Z);
                }

                return;
            }

            if (IsLightSource(node))
            {
                NoteLightSource(node);
            }

            if (node is Static || node is Multi)
            {
                RecordOverhang(node.Graphic, node.Z, obj.TileCell);
            }
            else if (node is Land { IsStretched: true } land &&
                     Scenes.ViewMargin.LandReachesPastTile(Scenes.ViewMargin.LandUp(land.Z, land.YOffsets.Top, land.YOffsets.Right, land.YOffsets.Bottom, land.YOffsets.Left)))
            {
                ReachCells |= 1UL << obj.TileCell;
            }

            int cell = obj.TileCell;

            if (Tiles[x, y] == null)
            {
                Tiles[x, y] = node;
                _heads[cell] = node;
                node.TPrevious = null;
                node.TNext = null;

                return;
            }


            // Tiles[x, y] is never obj here: RemoveFromTile above moved the anchor off it (ReleaseAnchor). The old code had an
            // "o == obj" branch for a stale anchor that followed the LinkedObject container links (Previous/Next), which on a
            // landless tile could put obj into another tile's list (T6a audit).
            TileListObject o = _heads[cell];
            TileListObject found = null;
            TileListObject start = o;

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
                node.TPrevious = found;
                TileListObject next = found.TNext;
                node.TNext = next;
                found.TNext = node;

                if (next != null)
                {
                    next.TPrevious = node;
                }
            }
            else if (start != null)
            {
                node.TNext = start;
                start.TPrevious = node;
                node.TPrevious = null;
                _heads[cell] = node;
            }
        }

        /// <summary>
        /// RemoveFromTile only (the one place objects leave a tile list): when <paramref name="obj"/> is the anchor of its
        /// cell (Tiles[x, y], the node the walk starts from), a surviving neighbour takes its place, or null when it was
        /// alone. Normally the anchor is the land, which never leaves; on a landless tile (a block without map data) the
        /// first object placed used to stay the anchor after leaving, which hid the rest of the list.
        /// </summary>
        public void ReleaseAnchor(TileListObject obj)
        {
            ref TileListObject anchor = ref Tiles[obj.TileCell >> 3, obj.TileCell & 7];

            if (ReferenceEquals(anchor, obj))
            {
                anchor = obj.TPrevious ?? obj.TNext;
            }

            // the head leaves: its successor becomes the head (null when it was alone); RemoveFromTile unlinks it next
            ref TileListObject head = ref _heads[obj.TileCell];

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

            if (Dynamic != null && Dynamic.Count > 0)
            {
                // T6b part 3: the list does not reach the items, mobiles and effects; tear down in the merged order
                retired = TearDownMerged(recycler, now);
            }

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    TileListObject obj = Tiles[i, j];

                    if (obj == null)
                    {
                        continue;
                    }

                    TileListObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        TileListObject next = first.TNext;
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
            ClearLightSources();
            Dynamic?.Clear();
            ResetCaps();
            GpuItems?.Reset();
            Array.Clear(GpuMobiles);

            if (_world.ChunkPool.CanAdd(this))
            {
                Mesh.SoftClear(_world.MeshPool);
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
        /// T6b part 3: Destroy's and ClearForReload's per-object teardown when the index holds objects, tile by tile in the merged
        /// order (the list's land, statics and multis with the index's items, mobiles and effects), each object as the
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

                    for (GameObject o = TileWalk.Begin(this, cell, out TileWalk walk); o != null; o = walk.Next())
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

                        if (o is TileListObject node)
                        {
                            node.TPrevious = null;
                            node.TNext = null;
                        }

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
            if (Dynamic != null && Dynamic.Count > 0)
            {
                // T6b part 3: the list does not reach the dynamics (no recycling here, as below)
                TearDownMerged(null, 0);
            }

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    TileListObject first = GetHeadObject(i, j);

                    while (first != null)
                    {
                        TileListObject next = first.TNext;

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

            WalkZLo = int.MaxValue;
            WalkZHi = int.MinValue;
            FillSeq = uint.MaxValue;
            Mesh.SoftClear(null); // in-place reload: the chunk stays in use and rebuilds at once, so it keeps its arrays
            ClearLightSources();
            Dynamic?.Clear();
            ResetCaps();
            GpuItems?.Reset();
            Array.Clear(GpuMobiles);
        }

        public bool HasNoExternalData()
        {
            // T6b part 3: dynamics (items, mobiles, effects) are external data, and only the index holds them
            if (Dynamic != null && Dynamic.Count > 0)
            {
                return false;
            }

            for (int i = 0; i < 8; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    for (TileListObject obj = GetHeadObject(i, j); obj != null; obj = obj.TNext)
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
