// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using ClassicUO.Renderer;
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
        // E12: the tiles the probe pass (stamp ProbeStamp) served
        public uint ProbeStamp;
        public ulong ProbeServed;

        // E7: items of served tiles the cache can't hold (animated art, corpses, fading, text...), walked alone where they lie
        // in the tile list: before entry Entry. Those of tile bit b: [WalkStart[b], WalkStart[b + 1]); WalkTiles = tiles with any.
        public struct Walked
        {
            public Item Item;
            public int Entry;
        }

        public Walked[] WalkedItems = new Walked[4];
        public int WalkedCount;
        public readonly int[] WalkStart = new int[65];
        public ulong WalkTiles;

        public void AddWalked(Item item)
        {
            if (WalkedCount == WalkedItems.Length)
            {
                Array.Resize(ref WalkedItems, WalkedCount * 2);
            }

            WalkedItems[WalkedCount++] = new Walked { Item = item, Entry = Count };
        }

        /// <summary>The item is one of tile <paramref name="b"/>'s walked-alone items.</summary>
        public bool IsWalked(int b, GameObject obj)
        {
            for (int i = WalkStart[b], end = WalkStart[b + 1]; i < end; i++)
            {
                if (ReferenceEquals(WalkedItems[i].Item, obj))
                {
                    return true;
                }
            }

            return false;
        }

        public void Reset()
        {
            Array.Clear(Entries, 0, Count);
            Count = 0;
            Array.Clear(WalkedItems, 0, WalkedCount);
            WalkedCount = 0;
            WalkTiles = 0;
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
    //     lights are added at the same point. DrawnFrame is stamped as the walk does;
    //   - E7 (gpu_prepare_item_cache_mixed): a tile whose other unbaked objects are items the cache can't hold (corpses,
    //     multis, animated / foliage / translucent / roof items, text, fading, ...) that never end the tile's walk is served
    //     too: those items are walked alone where they lie in the tile list, between the cached runs before and after them.
    //     A tile that also holds mobiles walks them in the same pass, in tile order with those items.
    //   - E6 (gpu_prepare_item_cache_effects): graphic effects (GameEffect) don't count as unbaked objects of a tile for the cache,
    //     and don't re-extract a chunk when they join or leave a tile (Chunk.GpuEffectsIgnored). A served tile holding effects
    //     (Chunk.GpuEffectMask, rebuilt with the masks) walks them alone, in tile order with its mobiles and walked-alone items:
    //     an effect never ends a tile's walk and goes to the effects list (or the transparent one when its alpha isn't full),
    //     so only its order among the other transparent objects of the tile matters, and the single pass keeps it.
    // CUO_GPU_ITEM_CHECK=1 (every 30th frame) or =every (every frame), E12: a check frame runs the prepare and the capture twice
    // on the same frame state (no Update between; DrawWorldGpu): first serving from the cache as a normal frame would (the
    // probe pass, drawn to nothing: its instances, lights, render lists and selection are kept), then as a full walk (the
    // reference pass, which is the frame drawn). The two must produce the same dynamic instances (items, mobiles and effects,
    // with position, depth, blend, hue and alpha), lights, render lists and selection. The reference pass still compares the
    // cache with the walk item by item, as before. A frame whose alphas step (the walk changes them) is not checked.
    // CUO_GPU_ITEM_CHECK=legacy (every 30th frame) or =legacy-every: the older check: the frame walks every item and compares
    // the cache item by item, and hashes of the render lists and the capture order are compared with the served frame before.
    internal partial class GameScene
    {
        private static readonly string _gpuItemCheckEnv = Environment.GetEnvironmentVariable("CUO_GPU_ITEM_CHECK");
        private static readonly bool _gpuItemCheckLegacy = _gpuItemCheckEnv == "legacy" || _gpuItemCheckEnv == "legacy-every";
        private static readonly bool _gpuItemCheck = _gpuItemCheckEnv == "1" || _gpuItemCheckEnv == "every" || _gpuItemCheckLegacy;
        private static readonly bool _gpuItemCheckEvery = _gpuItemCheckEnv == "every" || _gpuItemCheckEnv == "legacy-every";
        // E12: check frames run the served and the full prepare on the same frame state
        private static readonly bool _gpuItemCheckSame = _gpuItemCheck && !_gpuItemCheckLegacy;

        /// <summary>A CUO_GPU_ITEM_CHECK frame's capture is running: GpuWorldRenderer reports each extracted item.</summary>
        internal static bool GpuItemCheckActive;

        private bool _gpuItemCacheOn, _gpuItemCheckNow;
        private int _gpuItemGeneration;
        private bool _gpuItemKeyOn, _gpuItemKeyGray, _gpuItemKeyRange, _gpuItemKeyMixed, _gpuItemKeyEffects;
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
            bool range = profile.NoColorObjectsOutOfRange;
            bool mixed = Settings.GlobalSettings.GpuPrepareItemCacheMixed;
            bool effects = want && Settings.GlobalSettings.GpuPrepareItemCacheEffects;
            var world = _gpuWorld.World;

            if (want != _gpuItemKeyOn || gray != _gpuItemKeyGray || range != _gpuItemKeyRange || mixed != _gpuItemKeyMixed ||
                effects != _gpuItemKeyEffects || !ReferenceEquals(world, _gpuItemKeyWorld))
            {
                _gpuItemGeneration++;
                _gpuItemKeyOn = want;
                _gpuItemKeyGray = gray;
                _gpuItemKeyRange = range;
                _gpuItemKeyMixed = mixed;
                _gpuItemKeyEffects = effects;
                Chunk.GpuEffectsIgnored = effects;
                _gpuItemKeyWorld = world;
            }

            if (!want || useHandles || !_gpuSkipActive || world == null)
            {
                return;
            }

            _gpuItemCacheOn = true;
            _gpuSelContainer = SelectedObject.SelectedContainer;

            // =every: every other frame, so each check frame follows a served one (the capture-order comparison needs it)
            if (_gpuItemCheckSame && _gpuItemPass == 2 || _gpuItemCheckLegacy && (_gpuItemCheckEvery ? (++_gpuItemCheckCounter & 1) == 0 : ++_gpuItemCheckCounter % 30 == 0))
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

            // the container a gump drag hovers is extracted with the highlight hue: never bake it, try again later
            if (item.AlphaHue != 0xFF || item.Offset != Vector3.Zero || item.ObjectHandlesStatus != ObjectHandlesStatus.NONE ||
                item.TextContainer != null && !item.TextContainer.IsEmpty || ReferenceEquals(item, _gpuSelContainer))
            {
                return 2;
            }

            return 0;
        }

        // E7: an uncacheable object the walk can visit alone without changing the rest of its tile: an item (its list,
        // order and picking are its own) that never ends the tile's walk (AddTileToRenderList returns at the max Z).
        private static bool GpuItemWalksAlone(GameObject obj)
        {
            if (obj is not Item item || item.IsDestroyed)
            {
                return false;
            }

            ref StaticTiles data = ref item.IsMulti ? ref Client.Game.UO.FileManager.TileData.StaticData[item.MultiGraphic] : ref item.ItemData;
            int maxObjectZ = item.PriorityZ;
            CalculateObjectHeight(ref maxObjectZ, ref data);

            return maxObjectZ <= GPU_WALK_MAX_Z;
        }

        // Everything about the item that the walk's rules and the extract read and that can change while it lies there.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // The out-of-range hue (NoColorObjectsOutOfRange) is part of it: the extract bakes it into the instances, and it flips
        // when the player moves or the server changes the view range (0xC8).
        private void GpuItemSignature(Item item, out ulong a, out ulong b)
        {
            a = item.Graphic | (ulong)item.Hue << 16 | (ulong)item.Amount << 32 | (ulong)(byte)item.Flags << 48 | (ulong)item.AlphaHue << 56;
            ulong state = (item.AllowedToDraw ? 1UL : 0) | (item.IsDestroyed ? 2UL : 0) | (item.TextContainer != null && !item.TextContainer.IsEmpty ? 4UL : 0) |
                          (item.ObjectHandlesStatus != ObjectHandlesStatus.NONE ? 8UL : 0) | (item.Offset != Vector3.Zero ? 16UL : 0) |
                          (_gpuItemKeyRange && item.Distance > _world.ClientViewRange ? 32UL : 0);
            b = item.X | (ulong)item.Y << 16 | (ulong)(byte)item.Z << 32 | (ulong)(ushort)item.PriorityZ << 40 | state << 56;
        }

        private void BuildGpuItemCache(Chunk chunk, GpuItemCache cache)
        {
            FrameStats.Inc(FrameCounter.ItemCacheBuilds);

            if (_gpuItemCheckSame)
            {
                _gpuItemBuildTotal++;
            }
            cache.Reset();
            GpuWorldRenderer gpu = _gpuWorld;
            ItemSpriteExtract extract = gpu.ItemExtract;
            Uo.Pack.SpriteEntry[] art = _gpuArt;
            int offX = _offset.X, offY = _offset.Y;
            bool transient = false;
            bool mixed = _gpuItemKeyMixed;
            bool fx = _gpuItemKeyEffects;
            ulong walk = chunk.GpuWalkMask;

            for (int b = 0; b < 64; b++)
            {
                cache.TileStart[b] = cache.Count;
                cache.WalkStart[b] = cache.WalkedCount;

                if ((walk & 1UL << b) == 0)
                {
                    continue;
                }

                GameObject head = chunk.GetHeadObject(b >> 3, b & 7);
                int reason = 0;
                bool any = false, alone = false;

                for (GameObject obj = head; obj != null; obj = obj.TNext)
                {
                    // mobiles are walked on their own (GpuServeCachedTile's callers), E6: effects too
                    if (IsGpuBaked(obj) || obj is Mobile || fx && obj is GameEffect)
                    {
                        continue;
                    }

                    int r = GpuItemCacheReason(obj);

                    if (r == 0)
                    {
                        any = true;

                        continue;
                    }

                    if (r == 2)
                    {
                        transient = true;
                    }

                    // E7: the tile is still served, this item walked alone
                    if (mixed && GpuItemWalksAlone(obj))
                    {
                        alone = true;

                        continue;
                    }

                    reason = r;

                    break;
                }

                if (reason != 0 || !any)
                {
                    continue;
                }

                for (GameObject obj = head; obj != null; obj = obj.TNext)
                {
                    if (IsGpuBaked(obj) || obj is Mobile || fx && obj is GameEffect)
                    {
                        continue;
                    }

                    if (alone && GpuItemCacheReason(obj) != 0)
                    {
                        cache.AddWalked(Unsafe.As<Item>(obj));

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

                if (alone)
                {
                    cache.WalkTiles |= 1UL << b;
                }
            }

            cache.TileStart[64] = cache.Count;
            cache.WalkStart[64] = cache.WalkedCount;
            cache.Valid = true;
            cache.Generation = _gpuItemGeneration;
            cache.RetryFrame = transient ? GpuRetryClock + 30 : 0;
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

            if (chunk.GpuItemsDirty || !cache.Valid || cache.Generation != _gpuItemGeneration || cache.RetryFrame != 0 && GpuRetryClock >= cache.RetryFrame)
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

            // tiles with mobiles stay in the walk, which then skips their items; E7: a tile with walked-alone items leaves it
            // even with mobiles (GpuServeMixedTile walks them, in tile order with those items)
            // E6: so does a tile with effects (walked alone too, in the same pass)
            ulong fxTiles = _gpuItemKeyEffects ? chunk.GpuEffectMask : 0;
            walkTiles &= ~(serve & ~(chunk.GpuMobileMask & ~(cache.WalkTiles | fxTiles)));
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

                    if (_gpuItemCheckSame)
                    {
                        _gpuItemFallbackTotal++;
                    }

                    return false;
                }
            }

            return true;
        }

        // E7: the tile's walked-alone items are still where the cache found them (else the tile is walked and re-extracted).
        private static bool GpuItemWalkedValid(Chunk chunk, GpuItemCache cache, int b)
        {
            for (int i = cache.WalkStart[b], end = cache.WalkStart[b + 1]; i < end; i++)
            {
                Item item = cache.WalkedItems[i].Item;

                if (item.IsDestroyed || !ReferenceEquals(item.TileChunk, chunk) || ((item.X & 7) << 3 | (item.Y & 7)) != b)
                {
                    chunk.GpuItemsDirty = true;
                    FrameStats.Inc(FrameCounter.ItemCacheFallbacks);

                    if (_gpuItemCheckSame)
                    {
                        _gpuItemFallbackTotal++;
                    }

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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GpuServeCachedTile(Chunk chunk, int b)
        {
            if (!GpuServeCachedTileCore(chunk, b))
            {
                return false;
            }

            if (_gpuItemCheckSame && _gpuItemPass == 1)
            {
                GpuProbeServed(chunk.GpuItems, b);
            }

            return true;
        }

        private bool GpuServeCachedTileCore(Chunk chunk, int b)
        {
            GpuItemCache cache = chunk.GpuItems;
            int s = cache.TileStart[b], e = cache.TileStart[b + 1];

            if (!GpuItemTileValid(chunk, cache, s, e))
            {
                return false;
            }

            FrameStats.Inc(FrameCounter.ItemCacheTiles);

            if (((cache.WalkTiles | (_gpuItemKeyEffects ? chunk.GpuEffectMask : 0)) & 1UL << b) == 0)
            {
                GpuQueueCachedEntries(cache, s, e);

                return true;
            }

            return GpuServeMixedTile(chunk, cache, b, s, e);
        }

        // E7: a served tile with walked-alone items: in tile order, the cached entries before each such item join the capture
        // at the statics list's length there, then the walk visits the item alone, as the full walk would have reached it.
        // Out of line, so AddTileToRenderList's callers in the chunk loop are unchanged.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool GpuServeMixedTile(Chunk chunk, GpuItemCache cache, int b, int s, int e)
        {
            if (!GpuItemWalkedValid(chunk, cache, b))
            {
                return false;
            }

            int w = cache.WalkStart[b], wEnd = cache.WalkStart[b + 1];
            FrameStats.Add(FrameCounter.ItemCacheWalked, wEnd - w);
            bool effects = _gpuItemKeyEffects && (chunk.GpuEffectMask & 1UL << b) != 0;

            if ((chunk.GpuMobileMask & 1UL << b) != 0 || effects)
            {
                // mobiles or effects too (the tile left walkTiles): one pass over the tile list, walking the mobiles, effects
                // and walked-alone items in their order (a corpse and a mobile share the animations list, a fading item, a
                // fading mobile and a fading effect the transparent one)
                for (GameObject obj = chunk.GetHeadObject(b >> 3, b & 7); obj != null; obj = obj.TNext)
                {
                    if (obj is Mobile)
                    {
                        if (!obj.IsDestroyed)
                        {
                            AddTileToRenderList(obj, false, GPU_WALK_MAX_Z, chunk, true, true, obj.TNext);
                        }

                        continue;
                    }

                    if (effects && obj is GameEffect)
                    {
                        if (!obj.IsDestroyed)
                        {
                            FrameStats.Inc(FrameCounter.ItemCacheFx);
                            AddTileToRenderList(obj, false, GPU_WALK_MAX_Z, chunk, true, true, obj.TNext);
                        }

                        continue;
                    }

                    if (w < wEnd && ReferenceEquals(cache.WalkedItems[w].Item, obj))
                    {
                        int entry = cache.WalkedItems[w++].Entry;

                        if (entry > s)
                        {
                            GpuQueueCachedEntries(cache, s, entry);
                            s = entry;
                        }

                        AddTileToRenderList(obj, false, GPU_WALK_MAX_Z, chunk, true, true, obj.TNext);
                    }
                }

                GpuQueueCachedEntries(cache, s, e);

                return true;
            }

            for (; w < wEnd; w++)
            {
                ref GpuItemCache.Walked walked = ref cache.WalkedItems[w];

                if (walked.Entry > s)
                {
                    GpuQueueCachedEntries(cache, s, walked.Entry);
                    s = walked.Entry;
                }

                Item item = walked.Item;
                // object handles never show while the cache serves (GpuItemCacheBegin)
                AddTileToRenderList(item, false, GPU_WALK_MAX_Z, chunk, true, true, item.TNext);
            }

            GpuQueueCachedEntries(cache, s, e);

            return true;
        }

        // Cached entries [s, e) join the capture where the walk would have queued them: the statics list's current length.
        private void GpuQueueCachedEntries(GpuItemCache cache, int s, int e)
        {
            int insertAt = _renderLists.StaticsCount;
            uint frame = _drawFrame;

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

                if (RenderLists.CaptureOrderOn)
                {
                    for (int i = run.EntryStart; i < run.EntryEnd; i++)
                    {
                        RenderLists.CaptureOrderHash = RenderLists.MixCaptureOrder(RenderLists.CaptureOrderHash, cache.Entries[i].Item);
                    }
                }

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
        private ulong _gpuOrderPrev;
        private uint _gpuOrderFrame;
        private bool _gpuOrderPrevServed;
        private int _gpuOrderSame, _gpuOrderDiff;

        private void GpuItemCheckLists()
        {
            if (!_gpuItemCheck || !_gpuSkipEnabled)
            {
                return;
            }

            _gpuCheckSkip ??= obj => obj is Item it && it.TileChunk?.GpuItems is { } c && c.CheckFrame == _drawFrame &&
                                     (c.CheckTiles & 1UL << ((it.X & 7) << 3 | (it.Y & 7))) != 0 && !c.IsWalked((it.X & 7) << 3 | (it.Y & 7), it);

            if (_gpuItemCheckSame)
            {
                // E12: the probe pass hashed its lists without a skip; the reference pass leaves out the cached items of the
                // tiles the probe served (the probe's lists never held them; a tile that fell back to the walk was not served)
                if (_gpuItemPass == 2)
                {
                    _gpuProbeSkip ??= obj => obj is Item it && it.TileChunk?.GpuItems is { } c && c.ProbeStamp == _gpuProbeServeStamp &&
                                             (c.ProbeServed & 1UL << ((it.X & 7) << 3 | (it.Y & 7))) != 0 && !c.IsWalked((it.X & 7) << 3 | (it.Y & 7), it);
                    _gpuProbeRefHash = _renderLists.DebugHash(_gpuProbeSkip);
                }

                return;
            }

            // E7: the capture order of everything, cached items included (RenderLists.CaptureOrderHash), compared in GpuItemCheckEnd
            RenderLists.CaptureOrderOn = true;
            RenderLists.CaptureOrderHash = 1469598103934665603UL;
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

            if (!GpuItemTileValid(chunk, cache, s, e) || (cache.WalkTiles & 1UL << b) != 0 && !GpuItemWalkedValid(chunk, cache, b))
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

            int tile = (item.X & 7) << 3 | (item.Y & 7);

            if (cache == null || cache.CheckFrame != _drawFrame || (cache.CheckTiles & 1UL << tile) == 0)
            {
                return;
            }

            // E7: served, this item would have been walked alone (the walk drew it as usual)
            bool mixedTile = (cache.WalkTiles & 1UL << tile) != 0;

            if (mixedTile && cache.IsWalked(tile, item))
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
            // E7: served, the tile's walked-alone items queued before this one in the statics list move its run back by as
            // many (the walk queues a tile's objects together: count them back from this item)
            int walkedBefore = 0;

            if (mixedTile)
            {
                for (int j = RenderLists.CaptureStaticsIndex - 1; j >= 0; j--)
                {
                    GameObject o = _renderLists.StaticAt(j);

                    if (o is not Item || o.X != item.X || o.Y != item.Y)
                    {
                        break;
                    }

                    if (cache.IsWalked(tile, o))
                    {
                        walkedBefore++;
                    }
                }
            }

            if (RenderLists.CaptureStaticsIndex - (_gpuCheckPos - 1) != ex.InsertAt + walkedBefore)
            {
                _gpuItemCheckPlace++;
                _gpuCheckBad++;
                _gpuItemCheckSample ??= $"place: item 0x{item.Serial:X8} at statics index {RenderLists.CaptureStaticsIndex} after {_gpuCheckPos - 1} cached and {walkedBefore} walked alone, run inserted at {ex.InsertAt}";
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
            if (RenderLists.CaptureOrderOn)
            {
                // E7: a served frame's capture order (cached runs where they were injected, walked-alone items, everything
                // else) must equal the reference walk's on the check frame after it, while nothing moves
                RenderLists.CaptureOrderOn = false;
                ulong order = RenderLists.CaptureOrderHash;

                if (_gpuItemCheckNow && _gpuOrderPrevServed && _gpuOrderFrame == _drawFrame - 1)
                {
                    if (order == _gpuOrderPrev)
                    {
                        _gpuOrderSame++;
                    }
                    else
                    {
                        _gpuOrderDiff++;
                    }
                }

                _gpuOrderPrev = order;
                _gpuOrderFrame = _drawFrame;
                _gpuOrderPrevServed = _gpuItemCacheOn && !_gpuItemCheckNow;
            }

            if (_gpuItemCheckSame && _gpuItemPass == 2)
            {
                _gpuProbeRefSelected = SelectedObject.Object;
            }

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

            if (!_gpuItemCheckSame)
            {
                GpuItemCheckReport();
            }
        }

        // The totals every 5 s (E12: called once per frame from DrawWorldGpu, whether or not the frame was compared).
        private void GpuItemCheckReport()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_gpuItemCheckAt == 0)
            {
                _gpuItemCheckAt = now;
            }
            else if (now - _gpuItemCheckAt >= System.Diagnostics.Stopwatch.Frequency * 5)
            {
                string items = $"{_gpuItemCheckCompared} cached items compared: " +
                               $"{_gpuItemCheckMissing} missing or out of order, {_gpuItemCheckExtra} extra, {_gpuItemCheckDiff} different, {_gpuItemCheckPlace} misplaced; " +
                               $"{_gpuItemCheckFallbacks} tiles caught by the signature";

                if (_gpuItemCheckSame)
                {
                    Log.Info($"[gpu-item-check] same frame: {_gpuProbeAttempts} check frames, {_gpuProbeChecks} compared ({_gpuProbeFull} fully, {_gpuProbePartial} partially: " +
                             $"{_gpuProbePartialUploads} textures uploaded during the check, {_gpuProbePartialFna} with {_gpuProbeSuppressed} sprites FNA would draw dropped in the probe), " +
                             $"{_gpuProbeFellBack} of them with served tiles that fell back to the walk; not compared: {_gpuProbeSkipAlpha} alphas stepping, {_gpuProbeSkipNoCache} no item cache; " +
                             $"{_gpuProbeStale} fully compared with differences, {_gpuProbeStalePartial} partially compared with differences, {_gpuProbeStaleFellBack} with fallen-back tiles with differences: " +
                             $"{_gpuProbeInstDiff} in item/mobile instances, {_gpuProbeFxDiff} in effect instances, {_gpuProbeListDiff} in render lists, {_gpuProbeLightDiff} in lights, " +
                             $"{_gpuProbeSelDiff} in the selection, {_gpuProbeCountDiff} in collected lists, {_gpuProbeOverrideDiff} in world overrides, {_gpuProbeFallbackDiff} in unmapped sprites, " +
                             $"{_gpuProbeRollDiff} in animation rolls; {_gpuProbeInstCompared} instances compared ({_gpuProbeFxCompared} effect), {_gpuProbeOverridesCompared} overrides, " +
                             $"{_gpuProbeFallbacks} unmapped sprites compared, {_gpuProbeRefBuilds} caches rebuilt in the reference pass; " +
                             $"isolation: {_gpuProbeLeaks} probe passes left sprites in the batcher; " +
                             $"item check on the reference pass: {_gpuItemChecks} frames, {_gpuItemCheckStale} with differences, {items}" +
                             (_gpuItemCheckSample != null ? $"; first: {_gpuItemCheckSample}" : ""));
                }
                else
                {
                    Log.Info($"[gpu-item-check] {_gpuItemChecks} checks, {_gpuItemCheckStale} with differences; {items}; other queued objects vs the served frame before: " +
                             $"{_gpuListSame} same, {_gpuListDiff} different; capture order vs the served frame before: {_gpuOrderSame} same, {_gpuOrderDiff} different" +
                             (_gpuItemCheckSample != null ? $"; first: {_gpuItemCheckSample}" : ""));
                }

                _gpuItemCheckAt = now;
                _gpuItemChecks = _gpuItemCheckStale = _gpuItemCheckCompared = _gpuItemCheckMissing = _gpuItemCheckExtra = _gpuItemCheckDiff = _gpuItemCheckFallbacks = _gpuItemCheckPlace = 0;
                _gpuListSame = _gpuListDiff = _gpuOrderSame = _gpuOrderDiff = 0;
                _gpuProbeAttempts = _gpuProbeChecks = _gpuProbeFull = _gpuProbePartial = _gpuProbePartialUploads = _gpuProbePartialFna = _gpuProbeSuppressed = 0;
                _gpuProbeFellBack = _gpuProbeSkipAlpha = _gpuProbeSkipNoCache = _gpuProbeStale = _gpuProbeStalePartial = _gpuProbeStaleFellBack = 0;
                _gpuProbeInstDiff = _gpuProbeFxDiff = _gpuProbeListDiff = _gpuProbeLightDiff = _gpuProbeSelDiff = _gpuProbeCountDiff = 0;
                _gpuProbeOverrideDiff = _gpuProbeFallbackDiff = _gpuProbeRollDiff = 0;
                _gpuProbeInstCompared = _gpuProbeFxCompared = _gpuProbeOverridesCompared = _gpuProbeFallbacks = _gpuProbeRefBuilds = _gpuProbeLeaks = 0;
                _gpuItemCheckSample = null;
            }
        }

        // ---- E12: the same-frame check ----

        // 0 = a normal frame, 1 = the probe pass (serving from the cache, nothing drawn), 2 = the reference pass (a full walk, drawn)
        private int _gpuItemPass;
        // served tiles walked because an item changed since the cache was built (counted only with the check on)
        private static int _gpuItemFallbackTotal;
        // chunk caches built (counted only with the check on)
        private static int _gpuItemBuildTotal;
        // fade steps taken: CalculateAlpha moved an alpha, or a chunk's or the scene's fade-step count went down (check on only)
        private static int _gpuAlphaSteps;
        // the probe passes so far: each adds a _drawFrame step that isn't a frame of the retry clock (GpuRetryClock)
        private uint _gpuProbeFrames;
        // stamps GpuItemCache.ProbeServed with the probe pass that set it
        private uint _gpuProbeServeStamp;
        private Func<GameObject, bool> _gpuProbeSkip;

        // The clock of GpuItemCache.RetryFrame: _drawFrame without the probe passes' steps (a check frame is one frame).
        private uint GpuRetryClock => _drawFrame - _gpuProbeFrames;

        // What the probe pass keeps and what it must put back; allocated on the first check frame.
        private sealed class GpuProbeState
        {
            public SpriteInstance[] Inst = new SpriteInstance[4096];
            public readonly System.Collections.Generic.List<(int List, GameObject Obj, byte Alpha, bool Allowed)> List = new(), RefList = new();
            public (byte Id, ushort Color, bool IsHue, int X, int Y)[] Lights = new (byte, ushort, bool, int, int)[64];
            public readonly System.Collections.Generic.List<GpuWorldRenderer.FallbackDraw> Fallbacks = new(), RefFallbacks = new();
            public readonly System.Collections.Generic.List<PendingOverrideQueue.Entry> Overrides = new();
            public readonly System.Collections.Generic.List<(GameObject Obj, Rectangle FrameInfo, bool Flipped)> Views = new();
            public readonly System.Collections.Generic.List<(GameObject Obj, sbyte Index)> Foliage = new();
            public readonly System.Collections.Generic.List<int> Rolls = new();
        }

        private GpuProbeState _gpuProbe;
        private bool _gpuProbeValid, _gpuProbeFellBackNow;
        private ulong _gpuProbeHash, _gpuProbeRefHash;
        private int _gpuProbeInstCount, _gpuProbeEffectsAt, _gpuProbeRefEffectsAt;
        private int _gpuProbeLightCount, _gpuProbeLightBase;
        private object _gpuProbeSelected, _gpuProbeRefSelected;
        private int _gpuProbeStatics, _gpuProbeMultis, _gpuProbeFoliage;
        private int _gpuProbeUploads, _gpuProbeSuppressedNow, _gpuProbeBuilds;
        private PendingOverrideQueue.Mark _gpuProbeOverrideMark;
        private int _gpuProbeAttempts, _gpuProbeChecks, _gpuProbeFull, _gpuProbePartial, _gpuProbePartialUploads, _gpuProbePartialFna, _gpuProbeSuppressed,
            _gpuProbeFellBack, _gpuProbeSkipAlpha, _gpuProbeSkipNoCache, _gpuProbeStale, _gpuProbeStalePartial, _gpuProbeStaleFellBack,
            _gpuProbeInstDiff, _gpuProbeFxDiff, _gpuProbeListDiff, _gpuProbeLightDiff, _gpuProbeSelDiff, _gpuProbeCountDiff, _gpuProbeOverrideDiff,
            _gpuProbeFallbackDiff, _gpuProbeRollDiff, _gpuProbeInstCompared, _gpuProbeFxCompared, _gpuProbeOverridesCompared, _gpuProbeFallbacks,
            _gpuProbeRefBuilds, _gpuProbeLeaks;

        // The probe pass served tile b of the chunk (the reference pass's list hash leaves out its cached items).
        private void GpuProbeServed(GpuItemCache cache, int b)
        {
            if (cache.ProbeStamp != _gpuProbeServeStamp)
            {
                cache.ProbeStamp = _gpuProbeServeStamp;
                cache.ProbeServed = 0;
            }

            cache.ProbeServed |= 1UL << b;
        }

        // The probe pass's fill is about to change obj's foliage stamp: journal it (put back after the probe).
        private void GpuProbeFoliageStamp(GameObject obj) => _gpuProbe.Foliage.Add((obj, obj.FoliageIndex));

        /// <summary>
        /// A check frame (DrawWorldGpu, before the frame's own list fill): the probe pass. It fills the lists and captures the
        /// frame as a normal frame would (cached tiles served), keeps what that produced, and puts back what the fill and the
        /// capture changed, so the frame's real fill that follows (a full walk, with the old item check) starts from the same
        /// state. Nothing it does reaches the batcher (suppressed) or the world pass's overrides (restored to a checkpoint).
        /// Returns 0: no probe, fill as usual; 1: the probe ran, fill (the reference pass); 2: the frame can't be checked and
        /// this fill is its normal one (don't fill again).
        /// </summary>
        private int GpuItemProbeRun(UltimaBatcher2D batcher, GpuWorldRenderer gpu)
        {
            _gpuItemPass = 0;
            _gpuProbeValid = false;
            WorldRenderer world = gpu.World;

            if (!_world.InGame || world == null || !(_gpuItemCheckEvery || ++_gpuItemCheckCounter % 30 == 0))
            {
                return 0;
            }

            GpuProbeState st = _gpuProbe ??= new GpuProbeState();
            _gpuProbeAttempts++;
            bool updatePosition = UpdateDrawPosition;
            var foliage = FoliageIndex;
            long alphaTimer = _alphaTimer;
            int lightBase = _lightCount;
            int alphaSteps = _gpuAlphaSteps;
            int fallbacks = _gpuItemFallbackTotal;
            int uploads = ClassicUO.Renderer.TextureUploads.Epoch;
            PendingOverrideQueue.Mark overrides = world.MarkPendingOverrides();
            st.Foliage.Clear();
            st.Rolls.Clear();
            AnimationGroupRules.RollTape = st.Rolls;
            AnimationGroupRules.RollMode = 1;
            _gpuProbeServeStamp++;
            _gpuItemPass = 1;
            FillGameObjectList();

            // a fade step (an alpha moved, a fade-step count went down) can't be taken twice: the probe's fill is the frame's
            if (!_gpuItemCacheOn || _gpuAlphaSteps != alphaSteps)
            {
                if (_gpuItemCacheOn)
                {
                    _gpuProbeSkipAlpha++;
                }
                else
                {
                    _gpuProbeSkipNoCache++;
                }

                _gpuItemPass = 0;
                AnimationGroupRules.RollMode = 0;
                AnimationGroupRules.RollTape = null;
                st.Foliage.Clear();

                return 2;
            }

            _gpuProbeFellBackNow = fallbacks != _gpuItemFallbackTotal;
            _gpuProbeHash = _renderLists.DebugHash(static _ => false);
            _renderLists.DebugCollect(static _ => false, st.List);
            FinishGpuCollect();
            gpu.SetOffset(_offset.X, _offset.Y);
            st.Views.Clear();
            st.Fallbacks.Clear();
            gpu.ProbeViews = st.Views;
            gpu.Fallbacks = st.Fallbacks;
            gpu.Probe = true;
            gpu.RecordFallbacks = true;
            gpu.ProbeFallbacks = 0;
            int pending = batcher.PendingSprites, flushes = batcher.FlushesDone;
            batcher.BeginSuppress();

            try
            {
                foreach (var multi in _gpuMeshMultis)
                {
                    if (multi.Z <= _maxGroundZ)
                    {
                        gpu.DrawObject(batcher, multi, multi.CalculateDepthZ());
                    }
                }

                for (int i = 0; i < _gpuStatics.Count; i++)
                {
                    var stat = _gpuStatics[i];

                    if (stat.Z <= _maxGroundZ)
                    {
                        gpu.DrawObject(batcher, stat, stat.CalculateDepthZ(), _gpuStaticsShadowOnly[i]);
                    }
                }

                _renderLists.CaptureRenderLists(batcher, _maxGroundZ, gpu, _gpuItemRunCount > 0 ? this : null);

                if (_multi != null && _world.TargetManager.IsTargeting && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement)
                {
                    gpu.DrawObject(batcher, _multi, _multi.CalculateDepthZ());
                }

                _gpuProbeSelected = SelectedObject.Object;
                _gpuProbeEffectsAt = gpu.DynamicInstances.Length;
                _renderLists.CaptureEffectsOnly(batcher, _maxGroundZ, gpu);
            }
            finally
            {
                gpu.Probe = false;
                gpu.RecordFallbacks = false;
                _gpuProbeSuppressedNow = batcher.EndSuppress();
            }

            // isolation self-check: the probe left nothing to draw and drew nothing
            if (batcher.PendingSprites != pending || batcher.FlushesDone != flushes)
            {
                _gpuProbeLeaks++;
                _gpuItemCheckSample ??= $"isolation: the probe left {batcher.PendingSprites - pending} sprites in the batcher and flushed {batcher.FlushesDone - flushes} times";
            }

            _gpuProbeFallbacks += gpu.ProbeFallbacks;

            ReadOnlySpan<SpriteInstance> got = gpu.DynamicInstances;

            if (st.Inst.Length < got.Length)
            {
                st.Inst = new SpriteInstance[Math.Max(got.Length, st.Inst.Length * 2)];
            }

            got.CopyTo(st.Inst);
            _gpuProbeInstCount = got.Length;
            _gpuProbeLightCount = _lightCount - lightBase;
            _gpuProbeLightBase = lightBase;

            if (st.Lights.Length < _gpuProbeLightCount)
            {
                st.Lights = new (byte, ushort, bool, int, int)[Math.Max(_gpuProbeLightCount, st.Lights.Length * 2)];
            }

            for (int i = 0; i < _gpuProbeLightCount; i++)
            {
                st.Lights[i] = LightAt(lightBase + i);
            }

            _gpuProbeStatics = _gpuStatics.Count;
            _gpuProbeMultis = _gpuMeshMultis.Count;
            _gpuProbeFoliage = _gpuFoliage.Count;
            st.Overrides.Clear();
            ref readonly PendingOverrideQueue queue = ref world.PendingOverrides;

            for (int i = overrides.Count; i < queue.Count; i++)
            {
                st.Overrides.Add(queue[i]);
            }

            // back to where the frame began
            gpu.ResetDynamic();
            world.RestorePendingOverrides(overrides);
            TruncateLights(lightBase);
            SelectedObject.Object = null;
            _gpuMeshMultis.Clear();
            _gpuStatics.Clear();
            _gpuStaticsShadowOnly.Clear();
            _gpuFoliage.Clear();
            UpdateDrawPosition = updatePosition;
            FoliageIndex = foliage;
            // no alpha moved (checked above): the reference pass's fill sees the alpha timer as the probe's did
            _alphaTimer = alphaTimer;

            for (int i = st.Foliage.Count - 1; i >= 0; i--)
            {
                st.Foliage[i].Obj.FoliageIndex = st.Foliage[i].Index;
            }

            for (int i = st.Views.Count - 1; i >= 0; i--)
            {
                (GameObject obj, Rectangle frameInfo, bool flipped) = st.Views[i];
                obj.FrameInfo = frameInfo;
                obj.ViewFlipped = flipped;
            }

            st.Foliage.Clear();
            st.Views.Clear();
            gpu.ProbeViews = null;
            _gpuProbeFrames++;
            AnimationGroupRules.RollMode = 2;
            AnimationGroupRules.RollTapeAt = 0;
            AnimationGroupRules.RollTapeShort = 0;
            st.RefFallbacks.Clear();
            gpu.Fallbacks = st.RefFallbacks;
            gpu.RecordFallbacks = true;
            _gpuProbeOverrideMark = overrides;
            _gpuProbeUploads = uploads;
            _gpuProbeBuilds = _gpuItemBuildTotal;
            _gpuItemPass = 2;
            _gpuProbeValid = true;

            return 1;
        }

        // After the reference pass captured its effects: its instances, lights, lists, selection, overrides, unmapped sprites
        // and animation rolls against the probe's.
        private void GpuItemProbeCompare(GpuWorldRenderer gpu)
        {
            GpuProbeState st = _gpuProbe;
            _gpuProbeValid = false;
            _gpuItemPass = 0;
            gpu.RecordFallbacks = false;
            gpu.Fallbacks = null;
            AnimationGroupRules.RollMode = 0;
            AnimationGroupRules.RollTape = null;

            _gpuProbeChecks++;
            _gpuProbeRefBuilds += _gpuItemBuildTotal - _gpuProbeBuilds;
            // partially checked: resources became ready during the check (the passes may not have seen the same textures,
            // picking masks or animation frames), or the probe's suppressed batcher dropped sprites FNA draws (not compared)
            bool uploads = ClassicUO.Renderer.TextureUploads.Epoch != _gpuProbeUploads;
            bool fna = _gpuProbeSuppressedNow > 0;
            bool partial = uploads || fna;

            if (partial)
            {
                _gpuProbePartial++;
                _gpuProbePartialUploads += uploads ? 1 : 0;
                _gpuProbePartialFna += fna ? 1 : 0;
                _gpuProbeSuppressed += _gpuProbeSuppressedNow;
            }
            else
            {
                _gpuProbeFull++;
            }

            if (_gpuProbeFellBackNow)
            {
                _gpuProbeFellBack++;
            }

            ReadOnlySpan<SpriteInstance> reference = gpu.DynamicInstances;
            ReadOnlySpan<SpriteInstance> probe = new ReadOnlySpan<SpriteInstance>(st.Inst, 0, _gpuProbeInstCount);
            int refAt = Math.Min(_gpuProbeRefEffectsAt, reference.Length);
            int probeAt = Math.Min(_gpuProbeEffectsAt, probe.Length);
            bool bad = false;

            bool world = CompareInstances("items and mobiles", probe.Slice(0, probeAt), reference.Slice(0, refAt));
            bool effects = CompareInstances("effects", probe.Slice(probeAt), reference.Slice(refAt));
            _gpuProbeInstCompared += reference.Length;
            _gpuProbeFxCompared += reference.Length - refAt;

            if (world)
            {
                _gpuProbeInstDiff++;
                bad = true;
            }

            if (effects)
            {
                _gpuProbeFxDiff++;
                bad = true;
            }

            if (_gpuProbeHash != _gpuProbeRefHash)
            {
                _gpuProbeListDiff++;
                bad = true;
                _renderLists.DebugCollect(_gpuProbeSkip, st.RefList);
                int n = Math.Min(st.List.Count, st.RefList.Count), k = 0;

                while (k < n && st.List[k] == st.RefList[k])
                {
                    k++;
                }

                string D(System.Collections.Generic.List<(int List, GameObject Obj, byte Alpha, bool Allowed)> l, int i) =>
                    i < l.Count ? $"list {l[i].List} {l[i].Obj.GetType().Name} 0x{l[i].Obj.Graphic:X4} at {l[i].Obj.X},{l[i].Obj.Y},{l[i].Obj.Z} alpha {l[i].Alpha} allowed {l[i].Allowed}" : "end";

                _gpuItemCheckSample ??= $"lists: {st.List.Count} served vs {st.RefList.Count} full, first difference at {k}: served {D(st.List, k)}, full {D(st.RefList, k)}";
            }

            if (!ReferenceEquals(_gpuProbeSelected, _gpuProbeRefSelected))
            {
                _gpuProbeSelDiff++;
                bad = true;
                _gpuItemCheckSample ??= $"selection: served {_gpuProbeSelected}, full {_gpuProbeRefSelected}";
            }

            if (_gpuProbeStatics != _gpuStatics.Count || _gpuProbeMultis != _gpuMeshMultis.Count || _gpuProbeFoliage != _gpuFoliage.Count)
            {
                _gpuProbeCountDiff++;
                bad = true;
                _gpuItemCheckSample ??= $"collected: statics {_gpuProbeStatics}/{_gpuStatics.Count}, multis {_gpuProbeMultis}/{_gpuMeshMultis.Count}, foliage {_gpuProbeFoliage}/{_gpuFoliage.Count} (served/full)";
            }

            int refLights = _lightCount - _gpuProbeLightBase;
            bool lightsDiffer = refLights != _gpuProbeLightCount;

            for (int i = 0; !lightsDiffer && i < _gpuProbeLightCount; i++)
            {
                lightsDiffer = st.Lights[i] != LightAt(_gpuProbeLightBase + i);
            }

            if (lightsDiffer)
            {
                _gpuProbeLightDiff++;
                bad = true;
                _gpuItemCheckSample ??= $"lights: {_gpuProbeLightCount} served, {refLights} in the full pass";
            }

            // the world pass's overrides: the probe's (taken off the queue) against what the reference pass queued since the mark
            if (gpu.World is { } wr)
            {
                ref readonly PendingOverrideQueue queue = ref wr.PendingOverrides;
                int from = _gpuProbeOverrideMark.Count;
                int refCount = queue.Count - from;
                bool overridesDiffer = refCount != st.Overrides.Count || queue.Dropped != _gpuProbeOverrideMark.Dropped;

                for (int i = 0; !overridesDiffer && i < refCount; i++)
                {
                    PendingOverrideQueue.Entry a = st.Overrides[i];
                    ref readonly PendingOverrideQueue.Entry b = ref queue[from + i];
                    overridesDiffer = a.X != b.X || a.Y != b.Y || a.Z != b.Z || a.Graphic != b.Graphic || a.IsStatic != b.IsStatic || a.Value != b.Value;
                }

                _gpuProbeOverridesCompared += refCount;

                if (overridesDiffer)
                {
                    _gpuProbeOverrideDiff++;
                    bad = true;
                    _gpuItemCheckSample ??= $"overrides: {st.Overrides.Count} served, {refCount} in the full pass ({queue.Dropped - _gpuProbeOverrideMark.Dropped} dropped)";
                }
            }

            // unmapped sprites (FNA draws them in the reference, the probe dropped them): the view's request, as asked
            bool fallbacksDiffer = st.Fallbacks.Count != st.RefFallbacks.Count;

            for (int i = 0; !fallbacksDiffer && i < st.Fallbacks.Count; i++)
            {
                fallbacksDiffer = !st.Fallbacks[i].Equals(st.RefFallbacks[i]);
            }

            if (fallbacksDiffer)
            {
                _gpuProbeFallbackDiff++;
                bad = true;
                _gpuItemCheckSample ??= $"unmapped sprites: {st.Fallbacks.Count} served, {st.RefFallbacks.Count} in the full pass";
            }

            st.Fallbacks.Clear();
            st.RefFallbacks.Clear();

            // the reference pass replays the probe's animation rolls: it must use exactly as many
            if (AnimationGroupRules.RollTapeShort != 0 || AnimationGroupRules.RollTapeAt != st.Rolls.Count)
            {
                _gpuProbeRollDiff++;
                bad = true;
                _gpuItemCheckSample ??= $"animation rolls: {st.Rolls.Count} served, {AnimationGroupRules.RollTapeAt + AnimationGroupRules.RollTapeShort} in the full pass";
            }

            if (bad)
            {
                if (_gpuProbeFellBackNow)
                {
                    _gpuProbeStaleFellBack++;
                }

                if (partial)
                {
                    _gpuProbeStalePartial++;
                }
                else
                {
                    _gpuProbeStale++;
                }
            }
        }

        private bool CompareInstances(string what, ReadOnlySpan<SpriteInstance> probe, ReadOnlySpan<SpriteInstance> reference)
        {
            if (probe.Length == reference.Length && MemoryMarshal.AsBytes(probe).SequenceEqual(MemoryMarshal.AsBytes(reference)))
            {
                return false;
            }

            int n = Math.Min(probe.Length, reference.Length);
            int first = n;

            for (int i = 0; i < n; i++)
            {
                if (!MemoryMarshal.AsBytes(probe.Slice(i, 1)).SequenceEqual(MemoryMarshal.AsBytes(reference.Slice(i, 1))))
                {
                    first = i;

                    break;
                }
            }

            string at = first < n
                ? $"first at {first}: served x {probe[first].X} y {probe[first].Y} depth {probe[first].Depth} blend {probe[first].Blend} hue {probe[first].PageHue >> 16}, " +
                  $"full x {reference[first].X} y {reference[first].Y} depth {reference[first].Depth} blend {reference[first].Blend} hue {reference[first].PageHue >> 16}"
                : "one is a prefix of the other";
            _gpuItemCheckSample ??= $"{what}: {probe.Length} served vs {reference.Length} full instances; {at}";

            return true;
        }
    }
}
