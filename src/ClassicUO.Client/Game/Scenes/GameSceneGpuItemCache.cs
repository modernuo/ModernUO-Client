// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// W4 of world-prepare-ecs-plan.md: one chunk's settled ground items, extracted once (<see cref="ItemSpriteExtract"/>)
    /// and replayed every frame while nothing about them changes. Entries are in walk order (tile bit x * 8 + y ascending,
    /// then the tile list), instances in world pixels, so the cache doesn't depend on the camera.
    /// </summary>
    internal sealed class GpuItemCache
    {
        public struct Entry
        {
            public Item Item;
            // what the extract read from the item (GpuItemSignature): a different value means re-extract
            public ulong Sig0, Sig1;
            public int InstStart, InstCount;
            // the tile anchor (RealScreenPosition + the draw offset: the walk's culling) and the picking box, world pixels
            public int Sx, Sy;
            public int Left, Top, Right, Bottom;
            public int LightX, LightY;
            public bool Light;
            public byte Tile;
        }

        public Entry[] Entries = new Entry[16];
        public int Count;
        public SpriteInstance[] Instances = new SpriteInstance[32];
        public int InstanceCount;
        // entries of tile bit b: [TileStart[b], TileStart[b + 1])
        public readonly int[] TileStart = new int[65];
        // tiles whose only unbaked objects are cached items
        public ulong Tiles;
        public bool Valid;
        public int Generation;
        // a tile was left out for a passing reason (text, fading in, object handles): rebuild from this frame on (0 = never)
        public uint RetryFrame;
        public int LitCount;
        public int MinSx, MaxSx, MinSy, MaxSy;
        public int Left, Top, Right, Bottom;
        // this frame: no entry's tile is culled by the walk (no per-entry culling needed)
        public bool Interior;
        // CUO_GPU_ITEM_CHECK: the frame and tiles the cache would have served
        public uint CheckFrame;
        public ulong CheckTiles;

        public void Reset()
        {
            Array.Clear(Entries, 0, Count);
            Count = 0;
            InstanceCount = 0;
            Tiles = 0;
            LitCount = 0;
            Valid = false;
            MinSx = MinSy = Left = Top = int.MaxValue;
            MaxSx = MaxSy = Right = Bottom = int.MinValue;
        }

        public ref Entry Add(ReadOnlySpan<SpriteInstance> instances)
        {
            if (Count == Entries.Length)
            {
                Array.Resize(ref Entries, Count * 2);
            }

            if (InstanceCount + instances.Length > Instances.Length)
            {
                Array.Resize(ref Instances, Math.Max(Instances.Length * 2, InstanceCount + instances.Length));
            }

            ref Entry e = ref Entries[Count++];
            e.InstStart = InstanceCount;
            e.InstCount = instances.Length;
            instances.CopyTo(new Span<SpriteInstance>(Instances, InstanceCount, instances.Length));
            InstanceCount += instances.Length;

            return ref e;
        }
    }

    // W4: ground-item chunk caching in the Gpu world prepare (setting gpu_prepare_item_cache). W1's skip machinery
    // (GameSceneGpuPrepare) applied to settled ground items:
    //   - a chunk's tiles whose only unbaked objects are plain ground items (not corpses, multis, animated art, foliage,
    //     internal, translucent or roof items; no text, no object handles; settled at full alpha below the max Z and the
    //     ground Z) are extracted once into Chunk.GpuItems and left out of the walk;
    //   - every frame each served item's signature (graphic, hue, amount, flags, position, alpha, text, handles, offset,
    //     tile) is compared with the extracted one; a changed tile is walked that frame and the chunk re-extracted on the
    //     next. Objects joining or leaving a tile (Chunk.OnTileContentChanged), a full walk of the chunk (fades, new or
    //     re-entered chunks, max Z / roof / season changes), a new GPU world pass, the setting and the dead-gray tint
    //     re-extract too;
    //   - still walked: the tiles under the mouse (picking, by each item's art box), the selected container's tile, every
    //     tile of a full-walk step, all items while object handles show;
    //   - the cached instances go into the capture where the walk would have queued the items (the statics render list's
    //     length when the tile is reached), so the dynamic buffer's order, and with it equal-depth ties, is unchanged; their
    //     lights are added at the same point. DrawnFrame is stamped as the walk does.
    // CUO_GPU_ITEM_CHECK=1 (every 30th frame) or =every: the frame walks every item as usual and compares what the cache
    // would have served, item by item and instance by instance, with what the walk and the extract produced.
    internal partial class GameScene
    {
        private static readonly string _gpuItemCheckEnv = Environment.GetEnvironmentVariable("CUO_GPU_ITEM_CHECK");
        private static readonly bool _gpuItemCheck = _gpuItemCheckEnv == "1" || _gpuItemCheckEnv == "every";
        private static readonly bool _gpuItemCheckEvery = _gpuItemCheckEnv == "every";

        /// <summary>A CUO_GPU_ITEM_CHECK frame's capture is running: GpuWorldRenderer reports each extracted item.</summary>
        internal static bool GpuItemCheckActive;

        private bool _gpuItemCacheOn, _gpuItemCheckNow;
        private int _gpuItemGeneration;
        private bool _gpuItemKeyOn, _gpuItemKeyGray;
        private Uo.Render.WorldRenderer _gpuItemKeyWorld;
        private Item _gpuSelContainer;
        // this chunk's tiles served from its cache, or (check frames) that would be
        private ulong _gpuServeTiles, _gpuCheckTiles;

        private struct GpuItemRun
        {
            public GpuItemCache Cache;
            public int EntryStart, EntryEnd;
            public int InsertAt;
        }

        private GpuItemRun[] _gpuItemRuns = new GpuItemRun[64];
        private int _gpuItemRunCount, _gpuItemRunNext;

        // the capture's insertion point of the next cached run (RenderLists.CaptureRenderList)
        internal int GpuItemNextInsert => _gpuItemRunNext < _gpuItemRunCount ? _gpuItemRuns[_gpuItemRunNext].InsertAt : int.MaxValue;

        private void GpuItemCacheBegin(bool useHandles)
        {
            _gpuItemRunCount = 0;
            _gpuItemRunNext = 0;
            _gpuItemCacheOn = false;
            _gpuItemCheckNow = false;
            _gpuServeTiles = 0;
            _gpuCheckTiles = 0;

            if (!_gpuSkipEnabled)
            {
                return;
            }

            var profile = ProfileManager.CurrentProfile;
            bool want = Settings.GlobalSettings.GpuPrepareItemCache && _gpuWorld.EcsMobiles;
            bool gray = _world.Player.IsDead && profile.EnableBlackWhiteEffect;
            var world = _gpuWorld.World;

            if (want != _gpuItemKeyOn || gray != _gpuItemKeyGray || !ReferenceEquals(world, _gpuItemKeyWorld))
            {
                _gpuItemGeneration++;
                _gpuItemKeyOn = want;
                _gpuItemKeyGray = gray;
                _gpuItemKeyWorld = world;
            }

            if (!want || useHandles || !_gpuSkipActive || world == null)
            {
                return;
            }

            _gpuItemCacheOn = true;
            _gpuSelContainer = SelectedObject.SelectedContainer;

            if (_gpuItemCheck && (_gpuItemCheckEvery || ++_gpuItemCheckCounter % 30 == 0))
            {
                _gpuItemCheckNow = true;
                _gpuCheckExpectCount = 0;
                _gpuCheckPos = 0;
                _gpuCheckBad = 0;
                GpuItemCheckActive = true;
            }
        }

        // Why an unbaked object keeps its tile in the walk: 0 = cacheable, 1 = until the tile or the item changes,
        // 2 = for now (try again later).
        private int GpuItemCacheReason(GameObject obj)
        {
            if (obj is not Item item || item.IsCorpse || item.IsMulti || item.IsDestroyed || !item.AllowedToDraw)
            {
                return 1;
            }

            ref StaticTiles data = ref item.ItemData;

            if (data.IsInternal || data.IsFoliage || data.IsAnimated || data.IsTranslucent)
            {
                return 1;
            }

            if (item.Z >= _maxZ || item.Z > _maxGroundZ || _noDrawRoofs && data.IsRoof)
            {
                return 1;
            }

            int maxObjectZ = item.PriorityZ;
            CalculateObjectHeight(ref maxObjectZ, ref data);

            if (maxObjectZ > GPU_WALK_MAX_Z)
            {
                return 1;
            }

            if (item.AlphaHue != 0xFF || item.Offset != Vector3.Zero || item.ObjectHandlesStatus != ObjectHandlesStatus.NONE ||
                item.TextContainer != null && !item.TextContainer.IsEmpty)
            {
                return 2;
            }

            return 0;
        }

        // Everything about the item that the walk's rules and the extract read and that can change while it lies there.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GpuItemSignature(Item item, out ulong a, out ulong b)
        {
            a = item.Graphic | (ulong)item.Hue << 16 | (ulong)item.Amount << 32 | (ulong)(byte)item.Flags << 48 | (ulong)item.AlphaHue << 56;
            ulong state = (item.AllowedToDraw ? 1UL : 0) | (item.IsDestroyed ? 2UL : 0) | (item.TextContainer != null && !item.TextContainer.IsEmpty ? 4UL : 0) |
                          (item.ObjectHandlesStatus != ObjectHandlesStatus.NONE ? 8UL : 0) | (item.Offset != Vector3.Zero ? 16UL : 0);
            b = item.X | (ulong)item.Y << 16 | (ulong)(byte)item.Z << 32 | (ulong)(ushort)item.PriorityZ << 40 | state << 56;
        }

        private void BuildGpuItemCache(Chunk chunk, GpuItemCache cache)
        {
            FrameStats.Inc(FrameCounter.ItemCacheBuilds);
            cache.Reset();
            GpuWorldRenderer gpu = _gpuWorld;
            ItemSpriteExtract extract = gpu.ItemExtract;
            Uo.Pack.SpriteEntry[] art = _gpuArt;
            int offX = _offset.X, offY = _offset.Y;
            bool transient = false;
            ulong walk = chunk.GpuWalkMask;

            for (int b = 0; b < 64; b++)
            {
                cache.TileStart[b] = cache.Count;

                if ((walk & 1UL << b) == 0)
                {
                    continue;
                }

                GameObject head = chunk.GetHeadObject(b >> 3, b & 7);
                int reason = 0;
                bool any = false;

                for (GameObject obj = head; obj != null; obj = obj.TNext)
                {
                    // mobiles are walked on their own (GpuServeCachedTile's callers)
                    if (IsGpuBaked(obj) || obj is Mobile)
                    {
                        continue;
                    }

                    reason = GpuItemCacheReason(obj);

                    if (reason != 0)
                    {
                        break;
                    }

                    any = true;
                }

                if (reason == 2)
                {
                    transient = true;
                }

                if (reason != 0 || !any)
                {
                    continue;
                }

                for (GameObject obj = head; obj != null; obj = obj.TNext)
                {
                    if (IsGpuBaked(obj) || obj is Mobile)
                    {
                        continue;
                    }

                    Item item = Unsafe.As<Item>(obj);
                    // what the walk would do first: the screen position for the current camera
                    item.UpdateRealScreenPosition(offX, offY);
                    item.ScreenPositionGen = _screenPositionGen;
                    int posX = item.RealScreenPosition.X, posY = item.RealScreenPosition.Y;
                    gpu.ExtractItem(item, offX, offY, posX, posY, item.CalculateDepthZ());

                    ref GpuItemCache.Entry e = ref cache.Add(extract.Instances);
                    e.Item = item;
                    e.Tile = (byte)b;
                    GpuItemSignature(item, out e.Sig0, out e.Sig1);
                    e.Sx = posX + offX;
                    e.Sy = posY + offY;
                    e.Light = extract.Light;
                    e.LightX = extract.LightX + offX;
                    e.LightY = extract.LightY + offY;

                    if (e.Light)
                    {
                        cache.LitCount++;
                    }

                    // the art the view's picking tests (ItemView.CheckMouseSelection: the art at the anchor minus the
                    // entry's draw offset, a stack's copy 5 px up and left), with a margin
                    int g = item.DisplayedGraphic + 0x4000;
                    int ow = 44, oh = 44;

                    if ((uint)g < (uint)art.Length)
                    {
                        ow = Math.Max(ow, (int)art[g].OrigW);
                        oh = Math.Max(oh, (int)art[g].OrigH);
                    }

                    e.Left = e.Sx + 22 - (ow >> 1) - 8;
                    e.Right = e.Sx + 22 + (ow >> 1) + 8;
                    e.Top = e.Sy + 44 - oh - 8;
                    e.Bottom = e.Sy + 44 + 8;

                    cache.MinSx = Math.Min(cache.MinSx, e.Sx);
                    cache.MaxSx = Math.Max(cache.MaxSx, e.Sx);
                    cache.MinSy = Math.Min(cache.MinSy, e.Sy);
                    cache.MaxSy = Math.Max(cache.MaxSy, e.Sy);
                    cache.Left = Math.Min(cache.Left, e.Left);
                    cache.Right = Math.Max(cache.Right, e.Right);
                    cache.Top = Math.Min(cache.Top, e.Top);
                    cache.Bottom = Math.Max(cache.Bottom, e.Bottom);
                }

                cache.Tiles |= 1UL << b;
            }

            cache.TileStart[64] = cache.Count;
            cache.Valid = true;
            cache.Generation = _gpuItemGeneration;
            cache.RetryFrame = transient ? _drawFrame + 30 : 0;
            chunk.GpuItemsDirty = false;
        }

        // Before the chunk's tile walk: which of its walk tiles the item cache serves this frame.
        private void GpuItemCachePrepare(Chunk chunk, ref ulong walkTiles)
        {
            GpuItemCache cache = chunk.GpuItems;

            if (cache == null)
            {
                chunk.GpuItems = cache = new GpuItemCache();
            }

            if (chunk.GpuItemsDirty || !cache.Valid || cache.Generation != _gpuItemGeneration || cache.RetryFrame != 0 && _drawFrame >= cache.RetryFrame)
            {
                BuildGpuItemCache(chunk, cache);
            }

            // a mobile the walk stops at (the walk's max Z) would end the tile before the items behind it
            ulong serve = cache.Tiles & walkTiles & ~chunk.GpuMobileStopMask;

            if (serve == 0)
            {
                return;
            }

            int offX = _offset.X, offY = _offset.Y;
            int mx = _gpuMouse.X + offX, my = _gpuMouse.Y + offY;

            // picking: a tile with an item art box under the mouse is walked
            if (mx >= cache.Left && mx <= cache.Right && my >= cache.Top && my <= cache.Bottom)
            {
                for (int i = 0; i < cache.Count; i++)
                {
                    ref GpuItemCache.Entry e = ref cache.Entries[i];

                    if (mx >= e.Left && mx <= e.Right && my >= e.Top && my <= e.Bottom)
                    {
                        serve &= ~(1UL << e.Tile);
                    }
                }
            }

            // the container a gump drag hovers is drawn with the highlight hue
            if (_gpuSelContainer != null && ReferenceEquals(_gpuSelContainer.TileChunk, chunk))
            {
                serve &= ~(1UL << ((_gpuSelContainer.X & 7) << 3 | (_gpuSelContainer.Y & 7)));
            }

            cache.Interior = cache.MinSx - offX >= _minPixel.X && cache.MaxSx - offX <= _maxPixel.X && cache.MinSy - offY >= _minPixel.Y &&
                             cache.MaxSy - offY <= _maxPixel.Y;

            if (_gpuItemCheckNow)
            {
                cache.CheckFrame = _drawFrame;
                cache.CheckTiles = serve;
                _gpuCheckTiles = serve;

                return;
            }

            // tiles with mobiles stay in the walk, which then skips their items
            walkTiles &= ~(serve & ~chunk.GpuMobileMask);
            _gpuServeTiles = serve;
        }

        // The served entries' signatures still match (else the tile is walked and the chunk re-extracted next frame).
        private bool GpuItemTileValid(Chunk chunk, GpuItemCache cache, int s, int e)
        {
            for (int i = s; i < e; i++)
            {
                ref GpuItemCache.Entry en = ref cache.Entries[i];
                Item item = en.Item;
                GpuItemSignature(item, out ulong a, out ulong b);

                if (a != en.Sig0 || b != en.Sig1 || !ReferenceEquals(item.TileChunk, chunk))
                {
                    chunk.GpuItemsDirty = true;
                    FrameStats.Inc(FrameCounter.ItemCacheFallbacks);

                    return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GpuItemCulled(GpuItemCache cache, ref GpuItemCache.Entry en)
        {
            if (cache.Interior)
            {
                return false;
            }

            // the walk's culling by the item's anchor (the tile's x, the item's y)
            int sx = en.Sx - _offset.X, sy = en.Sy - _offset.Y;

            return sx < _minPixel.X || sx > _maxPixel.X || sy < _minPixel.Y || sy > _maxPixel.Y;
        }

        // The tile loop reached served tile b: its items join the capture here. False = an item changed: walk the tile.
        private bool GpuServeCachedTile(Chunk chunk, int b)
        {
            GpuItemCache cache = chunk.GpuItems;
            int s = cache.TileStart[b], e = cache.TileStart[b + 1];

            if (!GpuItemTileValid(chunk, cache, s, e))
            {
                return false;
            }

            int insertAt = _renderLists.StaticsCount;
            uint frame = _drawFrame;
            FrameStats.Inc(FrameCounter.ItemCacheTiles);

            for (int i = s; i < e; i++)
            {
                ref GpuItemCache.Entry en = ref cache.Entries[i];

                if (GpuItemCulled(cache, ref en))
                {
                    continue;
                }

                en.Item.DrawnFrame = frame;

                if (_gpuItemRunCount > 0)
                {
                    ref GpuItemRun last = ref _gpuItemRuns[_gpuItemRunCount - 1];

                    if (ReferenceEquals(last.Cache, cache) && last.EntryEnd == i && last.InsertAt == insertAt)
                    {
                        last.EntryEnd++;

                        continue;
                    }
                }

                if (_gpuItemRunCount == _gpuItemRuns.Length)
                {
                    Array.Resize(ref _gpuItemRuns, _gpuItemRunCount * 2);
                }

                _gpuItemRuns[_gpuItemRunCount++] = new GpuItemRun { Cache = cache, EntryStart = i, EntryEnd = i + 1, InsertAt = insertAt };
            }

            return true;
        }

        /// <summary>The capture reached statics-list index <paramref name="index"/>: append the cached runs queued before it.</summary>
        internal int EmitGpuItemRuns(GpuWorldRenderer gpu, int index)
        {
            int done = 0;

            while (_gpuItemRunNext < _gpuItemRunCount && _gpuItemRuns[_gpuItemRunNext].InsertAt <= index)
            {
                ref GpuItemRun run = ref _gpuItemRuns[_gpuItemRunNext++];
                GpuItemCache cache = run.Cache;
                ref GpuItemCache.Entry first = ref cache.Entries[run.EntryStart];
                ref GpuItemCache.Entry last = ref cache.Entries[run.EntryEnd - 1];
                int start = first.InstStart;
                gpu.AppendDynamic(new ReadOnlySpan<SpriteInstance>(cache.Instances, start, last.InstStart + last.InstCount - start));

                if (cache.LitCount > 0)
                {
                    for (int i = run.EntryStart; i < run.EntryEnd; i++)
                    {
                        ref GpuItemCache.Entry en = ref cache.Entries[i];

                        if (en.Light)
                        {
                            AddLight(en.Item, en.Item, en.LightX - _offset.X, en.LightY - _offset.Y);
                        }
                    }
                }

                done += run.EntryEnd - run.EntryStart;
            }

            FrameStats.Add(FrameCounter.ItemCacheItems, done);

            return done;
        }

        // ---- CUO_GPU_ITEM_CHECK ----

        private struct GpuItemExpect
        {
            public GpuItemCache Cache;
            public int Index;
            // where the served run would join the statics list: its length at the tile, less the cached items queued before
            public int InsertAt;
        }

        private GpuItemExpect[] _gpuCheckExpect = new GpuItemExpect[1024];
        private int _gpuCheckExpectCount, _gpuCheckPos, _gpuCheckBad, _gpuItemCheckCounter;
        private int _gpuItemChecks, _gpuItemCheckStale, _gpuItemCheckCompared, _gpuItemCheckMissing, _gpuItemCheckExtra, _gpuItemCheckDiff,
            _gpuItemCheckFallbacks, _gpuItemCheckPlace;
        private long _gpuItemCheckAt;
        private string _gpuItemCheckSample;

        // Everything else the walk queued (mobiles walked alone on served tiles, other objects, the selection) must match
        // between a served frame and the check frame after it, when nothing moves: a hash of the render lists without the
        // check frame's cacheable items, compared with the frame before.
        private Func<GameObject, bool> _gpuCheckSkip;
        private ulong _gpuListHashPrev;
        private uint _gpuListHashFrame;
        private bool _gpuListHashPrevServed;
        private int _gpuListSame, _gpuListDiff;

        private void GpuItemCheckLists()
        {
            if (!_gpuItemCheck || !_gpuSkipEnabled)
            {
                return;
            }

            _gpuCheckSkip ??= obj => obj is Item it && it.TileChunk?.GpuItems is { } c && c.CheckFrame == _drawFrame &&
                                     (c.CheckTiles & 1UL << ((it.X & 7) << 3 | (it.Y & 7))) != 0;
            ulong h = _renderLists.DebugHash(_gpuCheckSkip) ^ (ulong)RuntimeHelpers.GetHashCode(SelectedObject.Object ?? (object)_renderLists) << 7;

            if (_gpuItemCheckNow && _gpuListHashPrevServed && _gpuListHashFrame == _drawFrame - 1)
            {
                if (h == _gpuListHashPrev)
                {
                    _gpuListSame++;
                }
                else
                {
                    _gpuListDiff++;
                }
            }

            _gpuListHashPrev = h;
            _gpuListHashFrame = _drawFrame;
            _gpuListHashPrevServed = _gpuItemCacheOn && !_gpuItemCheckNow;
        }

        // A check frame reached tile b, which the cache would have served: what it would have queued, in order.
        private void GpuExpectCachedTile(Chunk chunk, int b)
        {
            GpuItemCache cache = chunk.GpuItems;
            int s = cache.TileStart[b], e = cache.TileStart[b + 1];

            if (!GpuItemTileValid(chunk, cache, s, e))
            {
                // served, this tile would have been walked: nothing to compare
                cache.CheckTiles &= ~(1UL << b);
                _gpuItemCheckFallbacks++;

                return;
            }

            int insertAt = _renderLists.StaticsCount - _gpuCheckExpectCount;

            for (int i = s; i < e; i++)
            {
                if (GpuItemCulled(cache, ref cache.Entries[i]))
                {
                    continue;
                }

                if (_gpuCheckExpectCount == _gpuCheckExpect.Length)
                {
                    Array.Resize(ref _gpuCheckExpect, _gpuCheckExpectCount * 2);
                }

                _gpuCheckExpect[_gpuCheckExpectCount++] = new GpuItemExpect { Cache = cache, Index = i, InsertAt = insertAt };
            }
        }

        /// <summary>A check frame's capture extracted <paramref name="item"/> (GpuWorldRenderer.DrawItemEcs).</summary>
        internal void GpuItemCheckDrawn(Item item, ReadOnlySpan<SpriteInstance> got, bool light, int lightX, int lightY)
        {
            GpuItemCache cache = item.TileChunk?.GpuItems;

            if (cache == null || cache.CheckFrame != _drawFrame || (cache.CheckTiles & 1UL << ((item.X & 7) << 3 | (item.Y & 7))) == 0)
            {
                return;
            }

            if (_gpuCheckPos >= _gpuCheckExpectCount)
            {
                _gpuItemCheckExtra++;
                _gpuCheckBad++;
                _gpuItemCheckSample ??= $"extra: item 0x{item.Serial:X8} 0x{item.Graphic:X4} at {item.X},{item.Y},{item.Z} drawn by the walk, not in the cache";

                return;
            }

            ref GpuItemExpect ex = ref _gpuCheckExpect[_gpuCheckPos++];
            ref GpuItemCache.Entry en = ref ex.Cache.Entries[ex.Index];
            _gpuItemCheckCompared++;

            if (!ReferenceEquals(en.Item, item))
            {
                _gpuItemCheckMissing++;
                _gpuCheckBad++;
                _gpuItemCheckSample ??= $"order: walk drew 0x{item.Serial:X8} at {item.X},{item.Y},{item.Z}, cache has 0x{en.Item.Serial:X8} at {en.Item.X},{en.Item.Y},{en.Item.Z}";

                return;
            }

            // the walk queued it at statics index CaptureStaticsIndex after _gpuCheckPos - 1 other cached items: served, the
            // capture has it after the same uncached objects
            if (RenderLists.CaptureStaticsIndex - (_gpuCheckPos - 1) != ex.InsertAt)
            {
                _gpuItemCheckPlace++;
                _gpuCheckBad++;
                _gpuItemCheckSample ??= $"place: item 0x{item.Serial:X8} at statics index {RenderLists.CaptureStaticsIndex} after {_gpuCheckPos - 1} cached, run inserted at {ex.InsertAt}";
            }

            ReadOnlySpan<SpriteInstance> mine = new ReadOnlySpan<SpriteInstance>(ex.Cache.Instances, en.InstStart, en.InstCount);
            bool same = mine.Length == got.Length && MemoryMarshal.AsBytes(mine).SequenceEqual(MemoryMarshal.AsBytes(got)) && en.Light == light &&
                        (!light || en.LightX - _offset.X == lightX && en.LightY - _offset.Y == lightY);

            if (!same)
            {
                _gpuItemCheckDiff++;
                _gpuCheckBad++;
                _gpuItemCheckSample ??= $"different: item 0x{item.Serial:X8} 0x{item.Graphic:X4} hue 0x{item.Hue:X4} at {item.X},{item.Y},{item.Z}: " +
                                        $"{mine.Length} cached vs {got.Length} extracted instances" +
                                        (mine.Length > 0 && got.Length > 0 ? $", first x {mine[0].X}/{got[0].X} y {mine[0].Y}/{got[0].Y} hue {mine[0].PageHue >> 16}/{got[0].PageHue >> 16} depth {mine[0].Depth}/{got[0].Depth}" : "") +
                                        $", light {en.Light}/{light}";
            }
        }

        // After the capture of a check frame: entries the walk didn't draw, and the totals every 5 s.
        private void GpuItemCheckEnd()
        {
            if (!_gpuItemCheckNow)
            {
                return;
            }

            GpuItemCheckActive = false;
            _gpuItemCheckNow = false;
            _gpuItemChecks++;

            if (_gpuCheckPos < _gpuCheckExpectCount)
            {
                int left = _gpuCheckExpectCount - _gpuCheckPos;
                _gpuItemCheckMissing += left;
                _gpuCheckBad += left;
                ref GpuItemExpect ex = ref _gpuCheckExpect[_gpuCheckPos];
                Item it = ex.Cache.Entries[ex.Index].Item;
                _gpuItemCheckSample ??= $"missing: cached item 0x{it.Serial:X8} 0x{it.Graphic:X4} at {it.X},{it.Y},{it.Z} not drawn by the walk";
            }

            if (_gpuCheckBad > 0)
            {
                _gpuItemCheckStale++;
            }

            Array.Clear(_gpuCheckExpect, 0, _gpuCheckExpectCount);
            _gpuCheckExpectCount = 0;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_gpuItemCheckAt == 0)
            {
                _gpuItemCheckAt = now;
            }
            else if (now - _gpuItemCheckAt >= System.Diagnostics.Stopwatch.Frequency * 5)
            {
                Log.Info($"[gpu-item-check] {_gpuItemChecks} checks, {_gpuItemCheckStale} with differences; {_gpuItemCheckCompared} cached items compared: " +
                         $"{_gpuItemCheckMissing} missing or out of order, {_gpuItemCheckExtra} extra, {_gpuItemCheckDiff} different, {_gpuItemCheckPlace} misplaced; " +
                         $"{_gpuItemCheckFallbacks} tiles caught by the signature; other queued objects vs the served frame before: " +
                         $"{_gpuListSame} same, {_gpuListDiff} different" + (_gpuItemCheckSample != null ? $"; first: {_gpuItemCheckSample}" : ""));
                _gpuItemCheckAt = now;
                _gpuItemChecks = _gpuItemCheckStale = _gpuItemCheckCompared = _gpuItemCheckMissing = _gpuItemCheckExtra = _gpuItemCheckDiff = _gpuItemCheckFallbacks = _gpuItemCheckPlace = 0;
                _gpuListSame = _gpuListDiff = 0;
                _gpuItemCheckSample = null;
            }
        }
    }
}
