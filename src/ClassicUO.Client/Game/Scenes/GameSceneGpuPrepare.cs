// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;
using ClassicUO.Game.Managers;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    // Gpu world prepare, W1 of world-prepare-ecs-plan.md: under the Gpu world renderer, baked land and statics (the
    // chunk-meshed ones Uo.Render bakes) are drawn by the GPU with the shader's max-Z, roof and circle-of-transparency
    // tests. Once their alpha has settled they need nothing from the per-frame walk, so the walk only visits:
    //   - tiles holding anything else (items, mobiles, effects, multis, unmeshed statics: Chunk.GpuWalkMask), skipping
    //     their baked objects;
    //   - tiles with baked objects that still need per-frame work (light sources, season-replaced graphics,
    //     hidden land, too high for the walk's max Z, or carrying overhead text: Chunk.GpuFullMask), in full;
    //   - the tiles under the mouse (picking) and the screen-edge tiles where the walk culls objects the GPU would still
    //     show, in full.
    // Everything is walked in full while alphas move: for GPU_FADE_STEPS alpha steps after the max Z, max ground Z, roofs,
    // season or fading setting change (all visible chunks), and after a chunk is built or comes into view (that chunk).
    // Afterwards each baked object's alpha is snapped to its settled value, so skipping it is what the walk would do.
    internal partial class GameScene
    {
        // alpha steps a fade can take (255 / 25 rounded up, plus one)
        private const int GPU_FADE_STEPS = 12;
        // the walk's maxZ argument (FillGameObjectList): higher objects are never drawn by it
        private const int GPU_WALK_MAX_Z = 150;

        private static readonly bool _gpuSkipCheck = Environment.GetEnvironmentVariable("CUO_GPU_SKIP_CHECK") == "1";

        private bool _gpuSkipEnabled, _gpuSkipActive, _gpuSkipWasEnabled;
        private int _gpuFullSteps;
        private int _gpuPrevMaxZ = int.MinValue, _gpuPrevMaxGroundZ;
        private bool _gpuPrevNoDrawRoofs, _gpuPrevFading;
        private Managers.Season _gpuPrevSeason;
        private Point _gpuMouse;
        private Rectangle _gpuCullView;
        private int _gpuCheckFrame;
        private long _gpuCheckAt;
        private int _gpuCheckObjects, _gpuCheckAlpha, _gpuCheckDraw, _gpuCheckRuns;
        private string _gpuCheckSample;

        public bool IsGpuPrepareSkipping => _gpuSkipActive;

        // Season statics baked by the world pass (WorldRenderer.SetStaticRemap) while the skipping is on: a meshed static
        // whose season graphic is baked needs no per-frame capture, so the walk can skip it like any other.
        private static ushort[] _gpuStaticRemap;
        private Uo.Render.WorldRenderer _gpuRemapWorld;
        private Uo.Render.PackSet _gpuRemapPack; // a facet change keeps the world pass but swaps its pack
        private Managers.Season _gpuRemapSeason;
        private bool _gpuRemapOn;

        private void GpuUpdateStaticSeason(GpuWorldRenderer gpu)
        {
            var world = gpu.World;

            if (world == null)
            {
                return;
            }

            bool want = Settings.GlobalSettings.GpuPrepareSkipBaked;

            if (world == _gpuRemapWorld && world.Pack == _gpuRemapPack && want == _gpuRemapOn && (!want || _world.Season == _gpuRemapSeason))
            {
                return;
            }

            _gpuRemapWorld = world;
            _gpuRemapPack = world.Pack;
            _gpuRemapOn = want;
            _gpuRemapSeason = _world.Season;
            ushort[] remap = null;

            if (want)
            {
                for (int g = 0; g < 0x10000; g++)
                {
                    ushort to = SeasonManager.GetSeasonGraphic(_world.Season, (ushort)g);

                    if (to != g)
                    {
                        if (remap == null)
                        {
                            remap = new ushort[0x10000];

                            for (int i = 0; i < remap.Length; i++)
                            {
                                remap[i] = (ushort)i;
                            }
                        }

                        remap[g] = to;
                    }
                }
            }

            _gpuStaticRemap = remap;
            _gpuMaskGeneration++;

            if (remap != null || world.Pack.StaticRemap != null)
            {
                Log.Trace($"GPU world renderer: {(remap != null ? _world.Season.ToString() : "no")} static season graphics in the bake, rebaking");
                world.SetStaticRemap(remap);
            }
        }

        // The graphic of a static's baked sprite (its season graphic when the bake has it).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GpuBakedGraphic(Static st)
        {
            return _gpuStaticRemap != null ? _gpuStaticRemap[st.OriginalGraphic] : st.OriginalGraphic;
        }

        // ChunkBaker leaves out statics whose draw flags hide them from a non-gargoyle player; when the client draws one
        // anyway (a gargoyle player and NoDrawUnlessGargoyle), there is no baked sprite to show, so the list fill draws it
        // like Legacy's StaticView does, on every animation frame.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool GpuBakeOmits(Static st)
        {
            return BakeOmits(Client.Game.UO.FileManager.TileData.StaticData, GpuBakedGraphic(st));
        }

        /// <summary>Whether ChunkBaker leaves the baked graphic <paramref name="g"/> out (its predicate: the pack's draw flags
        /// for a non-gargoyle player).</summary>
        internal static bool BakeOmits(ClassicUO.Assets.StaticTiles[] staticData, int g)
        {
            return (uint)g >= (uint)staticData.Length || !Uo.Pack.TileDraw.Static(staticData[g].PackFlags, playerIsGargoyle: false);
        }

        /// <summary>Recomputes an object's screen position for the current camera (the Gpu prepare skips baked objects).</summary>
        public void RefreshScreenPosition(GameObject obj)
        {
            obj.UpdateRealScreenPosition(_offset.X, _offset.Y);
        }

        // After GetViewPort (max Z and roofs are known), before the chunk loop.
        private void GpuPrepareBegin()
        {
            var profile = ProfileManager.CurrentProfile;

            // the gradient circle of transparency is per-object CPU work on baked statics (the out-of-range tint is a uniform)
            _gpuSkipEnabled = _gpuWorld != null && Settings.GlobalSettings.GpuPrepareSkipBaked && profile != null &&
                              !(profile.UseCircleOfTransparency && profile.CircleOfTransparencyType == 1);

            if (!_gpuSkipEnabled)
            {
                if (_gpuSkipWasEnabled)
                {
                    // back to the full walk (setting off, Legacy renderer): skipped objects may have missed camera moves
                    UpdateDrawPosition = true;
                }

                _gpuSkipActive = false;
                _gpuSkipWasEnabled = false;

                return;
            }

            bool fading = profile.UseObjectsFading;

            if (!_gpuSkipWasEnabled || _maxZ != _gpuPrevMaxZ || _maxGroundZ != _gpuPrevMaxGroundZ || _noDrawRoofs != _gpuPrevNoDrawRoofs ||
                _world.Season != _gpuPrevSeason || fading != _gpuPrevFading)
            {
                if (_world.Season != _gpuPrevSeason)
                {
                    // season graphics change which statics are replaced (GpuFullMask): rebuild every chunk's mask
                    _gpuMaskGeneration++;
                }

                _gpuFullSteps = GPU_FADE_STEPS;
                _gpuPrevMaxZ = _maxZ;
                _gpuPrevMaxGroundZ = _maxGroundZ;
                _gpuPrevNoDrawRoofs = _noDrawRoofs;
                _gpuPrevSeason = _world.Season;
                _gpuPrevFading = fading;
            }

            _gpuSkipWasEnabled = true;
            _gpuSkipActive = _gpuFullSteps == 0;
            _gpuMouse = SelectedObject.TranslatedMousePositionByViewport;
            // the walk's culling rectangle, and the target's view in the same (RealScreenPosition) coordinates
            Point viewMin = Camera.ScreenToWorld(Point.Zero);
            Point viewMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            _gpuCullView = new Rectangle(viewMin.X, viewMin.Y, viewMax.X - viewMin.X, viewMax.Y - viewMin.Y);
        }

        // Which of the chunk's tiles the walk visits this frame: fullTiles in full, walkTiles skipping baked objects;
        // edgeTiles get GpuHideCulledBaked (the walk's culling of baked objects, without the rest of the walk).
        private void GpuPrepareChunk(Chunk chunk, out ulong fullTiles, out ulong walkTiles, out ulong edgeTiles)
        {
            if (chunk.GpuSeenFrame != _drawFrame - 1)
            {
                // newly built or back in view: its alphas may still move
                chunk.GpuFreshSteps = GPU_FADE_STEPS;
            }

            chunk.GpuSeenFrame = _drawFrame;

            if (chunk.GpuMaskDirty || chunk.GpuMaskGeneration != _gpuMaskGeneration)
            {
                RebuildGpuMask(chunk);
                FrameStats.Inc(FrameCounter.GpuMaskRebuilds);
            }

            _gpuServeTiles = 0;
            _gpuCheckTiles = 0;

            if (!_gpuSkipActive || chunk.GpuFreshSteps > 0)
            {
                // alphas may move, and the max Z, ground Z or roofs that decide which items are cacheable may have changed
                chunk.GpuItemsDirty = true;

                if (_gpuItemCheckSame && _alphaChanged && chunk.GpuFreshSteps > 0)
                {
                    // E12: a fade step (the probe pass can't be repeated on the same state)
                    _gpuAlphaSteps++;
                }

                if (_alphaChanged && chunk.GpuFreshSteps > 0 && --chunk.GpuFreshSteps == 0 && _gpuSkipActive)
                {
                    // this frame's walk still runs in full; the alphas it leaves are settled
                    _gpuSettleChunks.Add(chunk);
                }

                fullTiles = ulong.MaxValue;
                walkTiles = 0;
                edgeTiles = 0;

                return;
            }

            ulong pickTiles = GpuBandMask(chunk, out edgeTiles);
            fullTiles = chunk.GpuFullMask | pickTiles;
            walkTiles = chunk.GpuWalkMask & ~fullTiles;
            edgeTiles &= ~fullTiles;

            if (FrameStats.On)
            {
                FrameStats.Add(FrameCounter.GpuFullTiles, System.Numerics.BitOperations.PopCount(fullTiles));
                FrameStats.Add(FrameCounter.GpuWalkTiles, System.Numerics.BitOperations.PopCount(walkTiles));
                FrameStats.Add(FrameCounter.GpuEdgeTiles, System.Numerics.BitOperations.PopCount(edgeTiles));
                FrameStats.Add(FrameCounter.GpuPickTiles, System.Numerics.BitOperations.PopCount(pickTiles & ~chunk.GpuFullMask));
            }
        }

        private readonly System.Collections.Generic.List<Chunk> _gpuSettleChunks = new System.Collections.Generic.List<Chunk>();

        // After the chunk loop.
        private void GpuPrepareEnd()
        {
            if (!_gpuSkipEnabled)
            {
                return;
            }

            if (_gpuItemCheckSame && _alphaChanged && _gpuFullSteps > 0)
            {
                _gpuAlphaSteps++;
            }

            if (_alphaChanged && _gpuFullSteps > 0 && --_gpuFullSteps == 0)
            {
                // the last full walk ran this frame: settle every visible chunk
                _gpuSettleChunks.Clear();
                _gpuSettleChunks.AddRange(_visibleChunks);
            }

            for (int i = 0; i < _gpuSettleChunks.Count; i++)
            {
                SettleGpuChunk(_gpuSettleChunks[i]);
            }

            _gpuSettleChunks.Clear();

            if (_gpuSkipCheck && _gpuSkipActive && ++_gpuCheckFrame % 30 == 0)
            {
                CheckGpuSkip();
            }

            FrameStats.Set(FrameCounter.GpuSkipActive, _gpuSkipActive ? 1 : 0);
        }

        private int _gpuMaskGeneration;

        // Baked land and statics: drawn by the GPU from its bake; the walk only adjusts them through overrides.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsGpuBaked(GameObject obj)
        {
            return obj.InChunkMesh && (obj is Land || obj is Static);
        }

        // A baked object that needs the walk every frame even when its alpha has settled.
        private static bool NeedsGpuWalk(GameObject obj)
        {
            int reason = GpuWalkReason(obj);

            if (_gpuSkipCheck && reason != 0)
            {
                _gpuWalkReasons[reason]++;
            }

            return reason != 0;
        }

        // CUO_GPU_SKIP_CHECK: how many baked objects needed the walk, by reason (logged with the check)
        private static readonly int[] _gpuWalkReasons = new int[8];
        private static readonly string[] _gpuWalkReasonNames = { "", "not drawable", "text", "high land", "season or unbaked graphic", "translucent mismatch", "light", "high static" };

        private static int GpuWalkReason(GameObject obj)
        {
            if (!obj.AllowedToDraw)
            {
                return 1;
            }

            if (obj.TextContainer != null && !obj.TextContainer.IsEmpty)
            {
                return 2;
            }

            if (obj is Land)
            {
                return obj.PriorityZ > GPU_WALK_MAX_Z ? 3 : 0;
            }

            var st = (Static)obj;
            ref var data = ref st.ItemData;

            if (st.Graphic != GpuBakedGraphic(st) || GpuBakeOmits(st))
            {
                return 4;
            }

            // translucent statics bake their settled alpha (178) and need no per-frame override; only a static whose client
            // tiledata disagrees with the pack's (never expected) keeps one
            if (data.IsTranslucent != (GpuBakedAlpha(st) != 0xFF))
            {
                return 5;
            }

            if (data.IsLight)
            {
                return 6;
            }

            int maxObjectZ = obj.PriorityZ;
            CalculateObjectHeight(ref maxObjectZ, ref data);

            return maxObjectZ > GPU_WALK_MAX_Z ? 7 : 0;
        }

        private void RebuildGpuMask(Chunk chunk)
        {
            ulong walk = 0, full = 0, mobiles = 0, mobileStops = 0, effects = 0;
            int minZ = int.MaxValue, maxZ = int.MinValue, halfW = 22, height = 44;
            int mobileCount = 0;
            // T6b: GpuMobiles is in the walk's order, so with world_tile_index a tile with dynamics reads the merged order
            ulong mergeCells = TileWalk.MergeCells(chunk, DynamicTileIndex.Merged);

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    ulong bit = 1UL << (x << 3 | y);
                    chunk.GpuMobileStart[x << 3 | y] = mobileCount;

                    for (GameObject obj = TileWalk.Open(chunk, x << 3 | y, mergeCells, out TileWalk tileWalk, out bool mergedWalk);
                         obj != null;
                         obj = mergedWalk ? tileWalk.Next() : obj.TNext)
                    {
                        if (!IsGpuBaked(obj))
                        {
                            walk |= bit;

                            if (obj is Mobile)
                            {
                                mobiles |= bit;

                                if (mobileCount == chunk.GpuMobiles.Length)
                                {
                                    Array.Resize(ref chunk.GpuMobiles, Math.Max(8, mobileCount * 2));
                                }

                                chunk.GpuMobiles[mobileCount++] = obj;

                                // AddTileToRenderList stops the tile's walk at this mobile (W4 must not serve the rest)
                                if (obj.PriorityZ + Constants.DEFAULT_CHARACTER_HEIGHT > GPU_WALK_MAX_Z)
                                {
                                    mobileStops |= bit;
                                }
                            }

                            if (obj is GameEffect)
                            {
                                effects |= bit;
                            }

                            continue;
                        }

                        if (NeedsGpuWalk(obj))
                        {
                            full |= bit;
                        }

                        if (obj is Land land)
                        {
                            int lo = land.Z, hi = land.Z;

                            if (land.IsStretched)
                            {
                                lo = Math.Min(lo, land.MinZ);
                                hi = Math.Max(hi, Math.Max(Math.Max(land.YOffsets.Top, land.YOffsets.Bottom), Math.Max(land.YOffsets.Left, land.YOffsets.Right)) >> 2);
                            }

                            minZ = Math.Min(minZ, lo);
                            maxZ = Math.Max(maxZ, hi);
                        }
                        else
                        {
                            var uv = Client.Game.UO.Arts.GetArt(obj.Graphic).UV;
                            minZ = Math.Min(minZ, obj.Z);
                            maxZ = Math.Max(maxZ, obj.Z);
                            halfW = Math.Max(halfW, (uv.Width >> 1) + 1);
                            height = Math.Max(height, uv.Height + 1);
                        }
                    }
                }
            }

            chunk.GpuWalkMask = walk;
            chunk.GpuFullMask = full;
            chunk.GpuMobileMask = mobiles;
            chunk.GpuMobileStopMask = mobileStops;
            chunk.GpuEffectMask = effects;
            chunk.GpuMobileStart[64] = mobileCount;

            if (mobileCount < chunk.GpuMobiles.Length)
            {
                Array.Clear(chunk.GpuMobiles, mobileCount, chunk.GpuMobiles.Length - mobileCount);
            }
            chunk.GpuMinZ = minZ == int.MaxValue ? 0 : minZ;
            chunk.GpuMaxZ = maxZ == int.MinValue ? 0 : maxZ;
            chunk.GpuMaxHalfWidth = halfW;
            chunk.GpuMaxHeight = height;
            chunk.GpuMaskDirty = false;
            chunk.GpuMaskGeneration = _gpuMaskGeneration;
        }

        // Tiles to walk in full this frame for picking (any baked sprite of the tile could be under the mouse), and the
        // screen-edge tiles (edge): the walk culls by the tile's anchor, and a culled sprite that still reaches into the view
        // gets a hidden override from CollectGpuHidden, which GpuHideCulledBaked reproduces.
        private ulong GpuBandMask(Chunk chunk, out ulong edgeMask)
        {
            int halfW = chunk.GpuMaxHalfWidth, height = chunk.GpuMaxHeight;
            int zLo = chunk.GpuMinZ << 2, zHi = chunk.GpuMaxZ << 2;
            int baseX = chunk.X << 3, baseY = chunk.Y << 3;

            // the chunk's anchors: RealScreenPosition.X = D * 22 - offX - 22, RealScreenPosition.Y + z * 4 = S * 22 - offY - 22
            int dMin = baseX - baseY - 7, dMax = baseX - baseY + 7, sMin = baseX + baseY, sMax = sMin + 14;
            int sxMin = dMin * 22 - _offset.X - 22, sxMax = dMax * 22 - _offset.X - 22;
            int syMin = sMin * 22 - _offset.Y - 22, syMax = sMax * 22 - _offset.Y - 22;

            // a sprite of a tile spans x in [sx + 22 - halfW, sx + 22 + halfW], y in [sy - zHi + 44 - height, sy - zLo + 44]
            bool mouse = sxMin + 22 - halfW <= _gpuMouse.X && _gpuMouse.X <= sxMax + 22 + halfW &&
                         syMin - zHi + 44 - height <= _gpuMouse.Y && _gpuMouse.Y <= syMax - zLo + 44;

            Rectangle view = _gpuCullView;
            // some anchor beside or below the culling rectangle, and some sprite of the chunk reaching the view
            bool edge = (sxMin < _minPixel.X || sxMax > _maxPixel.X || syMax - zLo > _maxPixel.Y) &&
                        sxMax + 22 + halfW > view.Left && sxMin + 22 - halfW < view.Right &&
                        syMax - zLo + 44 > view.Top && syMin - zHi + 44 - height < view.Bottom;

            edgeMask = 0;

            if (!mouse && !edge)
            {
                return 0;
            }

            ulong mask = 0;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    int d = baseX + x - (baseY + y), s = baseX + x + baseY + y;
                    int sx = d * 22 - _offset.X - 22, sy = s * 22 - _offset.Y - 22;
                    int left = sx + 22 - halfW, right = sx + 22 + halfW, top = sy - zHi + 44 - height, bottom = sy - zLo + 44;

                    if (mouse && left <= _gpuMouse.X && _gpuMouse.X <= right && top <= _gpuMouse.Y && _gpuMouse.Y <= bottom)
                    {
                        mask |= 1UL << (x << 3 | y);

                        continue;
                    }

                    // culled by the walk (whole tile by x; at the bottom by the lowest object's anchor), yet reaching the view
                    if (
                        edge && (sx < _minPixel.X || sx > _maxPixel.X || sy - zLo > _maxPixel.Y) &&
                        right > view.Left && left < view.Right && bottom > view.Top && top < view.Bottom
                    )
                    {
                        edgeMask |= 1UL << (x << 3 | y);
                    }
                }
            }

            return mask;
        }

        // A screen-edge tile's settled baked objects (GpuBandMask): the walk would cull some of them (the whole tile beside
        // the culling rectangle, or an object anchored below it) and CollectGpuHidden would then hide those it finds on
        // screen. The others it would leave to the GPU unchanged.
        private void GpuHideCulledBaked(GameObject head)
        {
            int sx = (head.X - head.Y) * 22 - _offset.X - 22;

            if (sx < _gpuView.Left - GPU_WIDE_ART || sx > _gpuView.Right + GPU_WIDE_ART)
            {
                return;
            }

            bool tileCulled = sx < _minPixel.X || sx > _maxPixel.X;

            for (GameObject obj = head; obj != null; obj = obj.TNext)
            {
                if (!IsGpuBaked(obj))
                {
                    continue;
                }

                int sy = (obj.X + obj.Y) * 22 - (obj.Z << 2) - _offset.Y - 22;

                if (obj is Land land)
                {
                    int adjustedY = land.IsStretched ? sy + (land.Z << 2) - (land.MinZ << 2) : sy;

                    if ((tileCulled || adjustedY < _minPixel.Y || sy > _maxPixel.Y) && land.Z <= _maxGroundZ && GpuLandVisible(land))
                    {
                        _gpuWorld.LandOverride(land.X, land.Y, 255, true, 0);
                    }
                }
                else
                {
                    var st = Unsafe.As<Static>(obj);

                    if ((tileCulled || sy < _minPixel.Y || sy > _maxPixel.Y) && !GpuStaticZHidden(st) && GpuStaticVisible(st))
                    {
                        _gpuWorld.StaticOverride(st.X, st.Y, st.Z, st.OriginalGraphic, 255, true, 0);
                    }
                }
            }
        }

        // The alpha the walk converges to for a baked object under the current max Z and roofs (ProcessAlpha's meshed path).
        private byte GpuSettledAlpha(GameObject obj)
        {
            if (obj is Land)
            {
                return obj.Z > _maxGroundZ ? (byte)0 : (byte)0xFF;
            }

            ref var data = ref Unsafe.As<Static>(obj).ItemData;

            if (obj.Z >= _maxZ || _noDrawRoofs && data.IsRoof)
            {
                return 0;
            }

            return data.IsTranslucent ? (byte)178 : (byte)0xFF;
        }

        private void SettleGpuChunk(Chunk chunk)
        {
            if (chunk.IsDestroyed)
            {
                return;
            }

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (GameObject obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                    {
                        if (IsGpuBaked(obj))
                        {
                            obj.AlphaHue = GpuSettledAlpha(obj);
                        }
                    }
                }
            }
        }

        // CUO_GPU_SKIP_CHECK=1: every 30th skipping frame, checks that every skipped baked object is settled and that the
        // GPU draws it exactly when the full walk would (no override, no hidden override). Logs the totals every 5 s.
        private void CheckGpuSkip()
        {
            _gpuCheckRuns++;

            foreach (var chunk in _visibleChunks)
            {
                if (chunk.GpuFreshSteps > 0)
                {
                    continue;
                }

                ulong full = chunk.GpuFullMask | GpuBandMask(chunk, out ulong edge);

                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        if ((full & 1UL << (x << 3 | y)) != 0)
                        {
                            continue;
                        }

                        GameObject head = chunk.GetHeadObject(x, y);

                        if (head == null)
                        {
                            continue;
                        }

                        // the walk breaks out of a tile whose anchor is beside the culling rectangle
                        int sx = (head.X - head.Y) * 22 - _offset.X - 22;
                        bool tileCulled = sx < _minPixel.X || sx > _maxPixel.X;

                        for (GameObject obj = head; obj != null; obj = obj.TNext)
                        {
                            if (!IsGpuBaked(obj) || NeedsGpuWalk(obj))
                            {
                                continue;
                            }

                            _gpuCheckObjects++;
                            byte settled = GpuSettledAlpha(obj);

                            if (obj.AlphaHue != settled)
                            {
                                _gpuCheckAlpha++;
                                _gpuCheckSample ??= $"alpha {obj.AlphaHue} != {settled}: {obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z}";

                                continue;
                            }

                            bool walkDraws, gpuShows;
                            int sy = (obj.X + obj.Y) * 22 - (obj.Z << 2) - _offset.Y - 22;

                            if (obj is Land land)
                            {
                                int adjustedY = land.IsStretched ? sy + (land.Z << 2) - (land.MinZ << 2) : sy;
                                walkDraws = !tileCulled && adjustedY >= _minPixel.Y && sy <= _maxPixel.Y && obj.AlphaHue != 0;
                                // the drawn diamond (stretched: its corners' z), not GpuLandVisible's slack
                                int top = sy, bottom = sy + 44;

                                if (land.IsStretched)
                                {
                                    int hi = Math.Max(Math.Max(land.YOffsets.Top, land.YOffsets.Bottom), Math.Max(land.YOffsets.Left, land.YOffsets.Right));
                                    top = sy + (land.Z << 2) - hi;
                                    bottom = adjustedY + 44;
                                }

                                gpuShows = land.Z <= _maxGroundZ && sx + 44 > _gpuView.Left && sx < _gpuView.Right && bottom > _gpuView.Top && top < _gpuView.Bottom;
                            }
                            else
                            {
                                var st = (Static)obj;
                                walkDraws = !tileCulled && sy >= _minPixel.Y && sy <= _maxPixel.Y && obj.AlphaHue != 0;
                                gpuShows = !GpuStaticZHidden(st) && GpuStaticVisible(st);
                            }

                            // edge tiles: GpuHideCulledBaked hides an undrawn object the GPU would show (same test as
                            // CollectGpuHidden, which the full walk runs)
                            if ((edge & 1UL << (x << 3 | y)) != 0 && !walkDraws &&
                                (obj is Land l2 ? l2.Z <= _maxGroundZ && GpuLandVisible(l2) : !GpuStaticZHidden((Static)obj) && GpuStaticVisible((Static)obj)))
                            {
                                gpuShows = false;
                            }

                            // The full walk leaves a drawn object to the GPU (settled: alpha 255, no override) and hides an
                            // undrawn one the GPU would show. Skipping both, only an undrawn but visible object differs.
                            if (!walkDraws && gpuShows)
                            {
                                _gpuCheckDraw++;
                                _gpuCheckSample ??= $"walk draws {walkDraws}, gpu shows {gpuShows}: {obj.GetType().Name} 0x{obj.Graphic:X4} at {obj.X},{obj.Y},{obj.Z} anchor {sx},{sy}";
                            }
                        }
                    }
                }
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_gpuCheckAt == 0)
            {
                _gpuCheckAt = now;
            }
            else if (now - _gpuCheckAt >= System.Diagnostics.Stopwatch.Frequency * 5)
            {
                Log.Info($"[gpu-skip-check] {_gpuCheckRuns} checks, {_gpuCheckObjects} skipped objects: {_gpuCheckAlpha} unsettled, {_gpuCheckDraw} drawn differently" +
                         (_gpuCheckSample != null ? $"; first: {_gpuCheckSample}" : ""));
                var reasons = new System.Text.StringBuilder();

                for (int r = 1; r < _gpuWalkReasons.Length; r++)
                {
                    reasons.Append($" {_gpuWalkReasonNames[r]} {_gpuWalkReasons[r]}");
                    _gpuWalkReasons[r] = 0;
                }

                Log.Info($"[gpu-skip-check] baked objects walked every frame, by reason (mask rebuilds and checks):{reasons}");
                _gpuCheckAt = now;
                _gpuCheckRuns = _gpuCheckObjects = _gpuCheckAlpha = _gpuCheckDraw = 0;
                _gpuCheckSample = null;
            }
        }
    }
}
