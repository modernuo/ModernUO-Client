// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Render;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene
    {
        private GpuWorldRenderer _gpuWorld;
        private uint _drawFrame;
        private readonly List<GameObject> _gpuMeshMultis = new List<GameObject>();
        private readonly List<Static> _gpuStatics = new List<Static>();
        private readonly List<bool> _gpuStaticsShadowOnly = new List<bool>();
        private readonly List<Static> _gpuFoliage = new List<Static>();
        private Rectangle _gpuView;
        private Uo.Pack.SpriteEntry[] _gpuArt;
        private static Uo.Pack.StaticTileData[] _gpuStaticData = Array.Empty<Uo.Pack.StaticTileData>();
        private bool _gpuStaticShadows, _gpuStumps, _gpuWaterWobble;
        private bool _gpuTintAll;
        // half the widest static art past its tile: tiles further beside the view can't reach into it
        private const int GPU_WIDE_ART = 320;

        public bool IsGpuWorldActive => _gpuWorld != null;

        // Holds the last presented world frame across discontinuities (WorldSettleGate). CUO_WORLD_HOLD=0: observe only.
        private readonly WorldSettleGate _settle = CreateSettleGate();
        private RenderTarget2D _settleTarget;

        internal WorldSettleGate Settle => _settle;

        // CUO_WORLD_SETTLE_LOG=1: log every presented frame whose view wasn't ready (what was missing)
        private static readonly bool SettleLog = Environment.GetEnvironmentVariable("CUO_WORLD_SETTLE_LOG") == "1";

        private void LogUnsettledFrame(GpuWorldRenderer gpu)
        {
            var w = gpu.World;
            Utility.Logging.Log.Trace(
                $"[settle] unsettled frame presented at {Time.Ticks}: chunks missing near view {w.Chunks.MissingNear} (in view {w.Chunks.MissingVisible}), " +
                $"pages missing {w.Pages.MissingVisible}, anim pages missing {w.AnimPages?.MissingVisible ?? 0}, client chunks queued in view {_world.Map.DrawPending}, " +
                $"camera {_world.Player.X},{_world.Player.Y}");
        }

        private static WorldSettleGate CreateSettleGate()
        {
            var gate = new WorldSettleGate { HoldEnabled = Environment.GetEnvironmentVariable("CUO_WORLD_HOLD") != "0" };

            if (double.TryParse(Environment.GetEnvironmentVariable("CUO_WORLD_HOLD_TIMEOUT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double timeout))
            {
                gate.TimeoutMs = timeout;
            }

            gate.EpisodeEnded += (kind, ms, held, unsettled, timedOut) =>
                Utility.Logging.Log.Trace($"[settle] {kind}: {(timedOut ? "timed out" : "ready")} after {ms:0} ms, {held} frames held, {unsettled} unsettled frames presented");

            return gate;
        }

        // The list fill decided to draw obj (called where it stamps DrawnFrame): report what differs from the baked sprite.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GpuOnDrawn(GameObject obj)
        {
            // the common case: an opaque meshed land tile the shader's max-Z test keeps (the out-of-range and death tints
            // are uniforms)
            if (obj.AlphaHue == 0xFF && obj.InChunkMesh && obj.AllowedToDraw && obj.Z <= _maxGroundZ && obj is Land)
            {
                return;
            }

            GpuOnDrawnSlow(obj);
        }

        private void GpuOnDrawnSlow(GameObject obj)
        {
            FrameStats.Inc(FrameCounter.OverrideCalls);
            FramePhase bucket = FrameStats.Enter(FramePhase.WalkOverride);
            GpuOnDrawnSlowCore(obj);
            FrameStats.Enter(bucket);
        }

        private void GpuOnDrawnSlowCore(GameObject obj)
        {
            if (Map.MapObjectRecycler.Check && obj.InRecycler)
            {
                Map.MapObjectRecycler.Touched(obj, Map.MapObjectRecycler.Site.GpuCapture);
            }

            switch (obj)
            {
                case Land land:
                    if (!land.AllowedToDraw)
                    {
                        _gpuWorld.LandOverride(land.X, land.Y, 255, true, 0);
                    }
                    else
                    {
                        // drawn although the shader's max-Z test hides it: fading out, or not meshed (Legacy draws those regardless)
                        bool force = land.Z > _maxGroundZ;

                        if (land.AlphaHue != 0xFF || force)
                        {
                            _gpuWorld.LandOverride(land.X, land.Y, land.AlphaHue, false, 0, force);
                        }
                    }

                    break;

                case Static st:
                    if (!st.AllowedToDraw)
                    {
                        _gpuWorld.StaticOverride(st.X, st.Y, st.Z, st.OriginalGraphic, 255, true, 0);
                    }
                    else if (st.InChunkMesh)
                    {
                        // meshed statics are never trees, foliage or animated (ChunkMesh) and draw without shadows
                        if (st.Graphic != GpuBakedGraphic(st) || GpuBakeOmits(st))
                        {
                            GpuReplaceStatic(st);
                        }
                        else
                        {
                            GpuStaticOverride(st);
                        }
                    }
                    else if (st.ItemData.IsFoliage)
                    {
                        // foliage alpha is updated after the walk (FillGameObjectList's foliage pass)
                        _gpuFoliage.Add(st);
                    }
                    else
                    {
                        CollectGpuStatic(st);
                    }

                    break;

                case Multi multi when multi.InChunkMesh:
                    _gpuMeshMultis.Add(multi);

                    break;
            }
        }

        // After the fill walked a tile: baked land and statics it skipped are hidden where the world pass would draw them.
        private void CollectGpuHidden(GameObject obj, bool skipBaked)
        {
            // tiles far beside the view: nothing of theirs reaches it (and the fill never touched them)
            int sx = (obj.X - obj.Y) * 22 - _offset.X - 22;

            if (sx < _gpuView.Left - GPU_WIDE_ART || sx > _gpuView.Right + GPU_WIDE_ART)
            {
                return;
            }

            FrameStats.Inc(FrameCounter.HiddenTiles);

            for (; obj != null; obj = obj.TNext)
            {
                // skipBaked: the walk left this tile's baked objects to the GPU (GameSceneGpuPrepare)
                if (obj.DrawnFrame == _drawFrame || skipBaked && IsGpuBaked(obj))
                {
                    continue;
                }

                // the shader already hides roofs and everything above the max Z: only the fill's other skips need overrides
                if (obj is Land land)
                {
                    if (land.Z <= _maxGroundZ && GpuLandVisible(land))
                    {
                        _gpuWorld.LandOverride(land.X, land.Y, 255, true, 0);
                    }
                }
                else if (obj is Static st && !GpuStaticZHidden(st) && GpuStaticVisible(st))
                {
                    _gpuWorld.StaticOverride(st.X, st.Y, st.Z, st.OriginalGraphic, 255, true, 0);
                }
            }
        }

        // Statics the list fill draws itself (not meshed): trees, foliage, animated and wet statics.
        private void CollectGpuStatic(Static st)
        {
            ushort graphic = st.Graphic;
            byte kind = GpuGraphicKind(graphic);

            // The baked sprite is the pack's original graphic: seasons, animated statics and stumps draw another one.
            if (
                graphic != st.OriginalGraphic
                // drawn but not baked (gargoyle-only statics such as 0x5683): every frame, including offset 0
                || GpuBakeOmits(st)
                || st.ItemData.IsAnimated && Client.Game.UO.FileManager.Arts.GetEntry(graphic + 0x4000).AnimOffset != 0
                || _gpuStumps && (kind & GPU_KIND_TREE) != 0
                // animated water: Legacy keeps animated statics out of its chunk mesh, so their view draws the scaled
                // water copy on every frame, including frames whose animation offset is 0 (the baked graphic)
                || _gpuWaterWobble && st.ItemData.IsAnimated && st.ItemData.IsWet
            )
            {
                GpuReplaceStatic(st);

                return;
            }

            if (_gpuStaticShadows && ((kind & (GPU_KIND_TREE | GPU_KIND_ROCK)) != 0 || st.ItemData.IsFoliage))
            {
                _gpuStatics.Add(st);
                _gpuStaticsShadowOnly.Add(true);
            }
            else if (st.ItemData.IsLight)
            {
                AddLightTimed(st, st.RealScreenPosition.X + 22, st.RealScreenPosition.Y + 22);
            }

            GpuStaticOverride(st);
        }

        private void GpuReplaceStatic(Static st)
        {
            _gpuWorld.StaticOverride(st.X, st.Y, st.Z, st.OriginalGraphic, 255, true, 0);
            _gpuStatics.Add(st);
            _gpuStaticsShadowOnly.Add(false);
        }

        private void GpuStaticOverride(Static st)
        {
            // drawn although the shader hides it: a roof or above-max-Z static still fading out
            bool force = GpuStaticZHidden(st);

            // the bake holds the settled alpha (tiledata Translucent: 178), so only a fade needs an override; its alpha
            // replaces the baked one
            if (st.AlphaHue != GpuBakedAlpha(st) || force)
            {
                _gpuWorld.StaticOverride(st.X, st.Y, st.Z, st.OriginalGraphic, st.AlphaHue, false, 0, force);
            }
        }

        // The alpha baked into a static's sprite (ChunkBaker: the pack's tiledata Translucent flag of the baked graphic).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GpuBakedAlpha(Static st)
        {
            int g = GpuBakedGraphic(st);

            return (uint)g < (uint)_gpuStaticData.Length && (_gpuStaticData[g].Flags & Uo.Render.TileFlags.Translucent) != 0
                ? Uo.Render.BakeJob.TranslucentAlpha
                : (byte)0xFF;
        }

        // The shader's max-Z / roof test (sprite.hlsl, FLAG_ZTEST) for the baked sprite of a static.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GpuStaticZHidden(Static st)
        {
            return st.Z >= _maxZ || _noDrawRoofs && Client.Game.UO.FileManager.TileData.StaticData[GpuBakedGraphic(st)].IsRoof;
        }

        private const byte GPU_KIND_KNOWN = 1, GPU_KIND_TREE = 2, GPU_KIND_ROCK = 4;
        private static readonly byte[] _gpuGraphicKinds = new byte[0x10000];

        private static byte GpuGraphicKind(ushort graphic)
        {
            byte kind = _gpuGraphicKinds[graphic];

            if (kind == 0)
            {
                kind = (byte)(GPU_KIND_KNOWN | (StaticFilters.IsTree(graphic, out _) ? GPU_KIND_TREE : 0) | (StaticFilters.IsRock(graphic) ? GPU_KIND_ROCK : 0));
                _gpuGraphicKinds[graphic] = kind;
            }

            return kind;
        }

        private void FinishGpuCollect()
        {
            for (int i = 0; i < _gpuFoliage.Count; i++)
            {
                if (_gpuFoliage[i].AlphaHue == 0)
                {
                    _gpuWorld.StaticOverride(_gpuFoliage[i].X, _gpuFoliage[i].Y, _gpuFoliage[i].Z, _gpuFoliage[i].OriginalGraphic, 255, true, 0);
                }
                else
                {
                    CollectGpuStatic(_gpuFoliage[i]);
                }
            }

            if (ProfileManager.CurrentProfile.HighlightGameObjects && SelectedObject.Object is GameObject sel && sel.DrawnFrame == _drawFrame)
            {
                if (sel is Land land)
                {
                    _gpuWorld.LandOverride(land.X, land.Y, land.AlphaHue, false, Constants.HIGHLIGHT_CURRENT_OBJECT_HUE, land.Z > _maxGroundZ);
                }
                else if (sel is Static st && (_gpuStatics.IndexOf(st) is int i && (i < 0 || _gpuStaticsShadowOnly[i])))
                {
                    _gpuWorld.StaticOverride(st.X, st.Y, st.Z, st.OriginalGraphic, st.AlphaHue, false, Constants.HIGHLIGHT_CURRENT_OBJECT_HUE, GpuStaticZHidden(st));
                }
            }
        }

        // Baked sprites the fill skipped need a hidden override only where the world pass would actually draw them.
        private bool GpuLandVisible(Land land)
        {
            // the 44x44 diamond's box; stretched corners can rise above the tile's z (up to 32 here) and drop to MinZ
            int sx = (land.X - land.Y) * 22 - _offset.X - 22;
            int sy = (land.X + land.Y) * 22 - (land.Z << 2) - _offset.Y - 22;
            int top = sy, bottom = sy + 44;

            if (land.IsStretched)
            {
                top -= 128;
                bottom += Math.Max(0, land.Z - land.MinZ) << 2;
            }

            return sx + 44 > _gpuView.Left && sx < _gpuView.Right && bottom > _gpuView.Top && top < _gpuView.Bottom;
        }

        private bool GpuStaticVisible(Static st)
        {
            int g = GpuBakedGraphic(st) + 0x4000;

            if ((uint)g >= (uint)_gpuArt.Length)
            {
                return false;
            }

            ref readonly var sp = ref _gpuArt[g];
            int x0 = (st.X - st.Y) * 22 - _offset.X - 22 - ((sp.OrigW >> 1) - 22);
            int y1 = (st.X + st.Y) * 22 - (st.Z << 2) - _offset.Y + 22;

            return x0 + sp.OrigW > _gpuView.Left && x0 < _gpuView.Right && y1 > _gpuView.Top && y1 - sp.OrigH < _gpuView.Bottom;
        }

        /// <summary><paramref name="hold"/>: the frame is prepared and streamed as usual, but nothing is drawn into the world
        /// target, which keeps the last presented frame (FNA's world-space draws go to the backbuffer, cleared before the
        /// composite).</summary>
        private void DrawWorldGpu(UltimaBatcher2D batcher, ref Matrix matrix, RenderTargets renderTargets, GpuWorldRenderer gpu, bool hold)
        {
            FrameStats.Begin(FramePhase.WorldPrepare);
            long stamp = FrameStats.Stamp();
            var device = batcher.GraphicsDevice;
            device.SetRenderTarget(hold ? null : renderTargets.WorldRenderTarget);
            // the world target is preserved (the GPU pass already cleared it); FNA's world-space draws get a fresh depth buffer
            device.Clear(ClearOptions.DepthBuffer | ClearOptions.Stencil, Vector4.Zero, 1f, 0);
            SelectedObject.Object = null;
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);

            _gpuWorld = gpu;
            _gpuMeshMultis.Clear();
            _gpuStatics.Clear();
            _gpuStaticsShadowOnly.Clear();
            _gpuFoliage.Clear();
            _gpuArt = Client.Game.UO.FileManager.Arts.PackSpriteEntries;
            _gpuStaticData = gpu.World?.Pack is { BakeTranslucentAlpha: true } pack ? pack.Statics : Array.Empty<Uo.Pack.StaticTileData>();
            var profile = ProfileManager.CurrentProfile;
            _gpuTintAll = _world.Player.IsDead && profile.EnableBlackWhiteEffect;
            _gpuStaticShadows = profile.ShadowsEnabled && profile.ShadowsStatics;
            _gpuStumps = profile.TreeToStumps;
            _gpuWaterWobble = profile.AnimatedWaterEffect;
            Point viewMin = Camera.ScreenToWorld(Point.Zero);
            Point viewMax = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            _gpuView = new Rectangle(viewMin.X, viewMin.Y, viewMax.X - viewMin.X, viewMax.Y - viewMin.Y);
            gpu.BeginFrame();
            gpu.SetLandSeason(_world.Season);
            GpuUpdateStaticSeason(gpu);
            FrameStats.AddSince(FramePhase.PrepSetup, stamp);

            FillGameObjectList();
            GpuItemCheckLists();
            stamp = FrameStats.Stamp();
            FinishGpuCollect();
            gpu.SetOffset(_offset.X, _offset.Y);
            gpu.SetZHiding(_maxZ, _maxGroundZ, _noDrawRoofs);

            if (_gpuTintAll)
            {
                gpu.SetTint(Constants.DEAD_RANGE_COLOR);
            }

            if (profile.NoColorObjectsOutOfRange)
            {
                // ApplyMeshHue / LandView / StaticView: GameObject.Distance = Chebyshev distance to World.RangeSize
                gpu.SetRangeTint(Constants.OUT_RANGE_COLOR, _world.RangeSize.X, _world.RangeSize.Y, _world.ClientViewRange);
            }

            var target = renderTargets.WorldRenderTarget;

            if (profile.UseCircleOfTransparency && profile.CircleOfTransparencyType != 1)
            {
                // IsometricWorld.fx: distance from the target's centre in target pixels, radius / zoom (gradient mode is CPU alpha)
                gpu.SetCircleOfTransparency(target.Width * 0.5f, target.Height * 0.5f, profile.CircleOfTransparencyRadius / Camera.Zoom, _world.Player.Z + 5);
            }

            batcher.SetSampler(SamplerState.PointClamp);
            batcher.Begin(null, matrix);
            batcher.SetBrightlight(ProfileManager.CurrentProfile.TerrainShadowsLevel * 0.1f);
            batcher.SetCircleOfTransparencyRadius(0f);
            batcher.SetStencil(DepthStencilState.Default);
            stamp = FrameStats.AddSince(FramePhase.PrepFoliageFinish, stamp);

            // dynamic sprites, in the legacy draw order: meshed multis, statics drawn by their view, then the lists
            int captured = 0;

            foreach (var multi in _gpuMeshMultis)
            {
                if (multi.Z <= _maxGroundZ && gpu.DrawObject(batcher, multi, multi.CalculateDepthZ()))
                {
                    captured++;
                }
            }

            stamp = FrameStats.AddSince(FramePhase.PrepCaptureMulti, stamp);

            for (int i = 0; i < _gpuStatics.Count; i++)
            {
                var st = _gpuStatics[i];

                if (st.Z <= _maxGroundZ && gpu.DrawObject(batcher, st, st.CalculateDepthZ(), _gpuStaticsShadowOnly[i]))
                {
                    captured++;
                }
            }

            FrameStats.AddSince(FramePhase.PrepCaptureStatic, stamp);

            FrameStats.Set(FrameCounter.CaptureMultis, _gpuMeshMultis.Count);
            FrameStats.Set(FrameCounter.CaptureStatics, _gpuStatics.Count);

            captured += _renderLists.CaptureRenderLists(batcher, _maxGroundZ, gpu, _gpuItemRunCount > 0 ? this : null);
            GpuItemCheckEnd();

            // the multi placement preview is depth-tested against the world, as in Legacy
            if (
                _multi != null
                && _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
                && gpu.DrawObject(batcher, _multi, _multi.CalculateDepthZ())
            )
            {
                captured++;
            }

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            FrameStats.End(FramePhase.WorldPrepare);
            FrameStats.Begin(FramePhase.WorldDraw);
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);

            // Effects go to the world pass (depth-tested like Legacy) when it can reproduce their blend; FNA draws the rest,
            // the object handles, weather and the selection over the world.
            int deferred = _renderLists.DrawDeferredRenderLists(batcher, _maxGroundZ, gpu);

            _world.Weather.Draw(batcher, 0, 0, MAX_LAYER_DEPTH - 1);

            DrawSelection(batcher, MAX_LAYER_DEPTH);

            batcher.SetSampler(null);
            batcher.SetStencil(null);
            batcher.End();

            gpu.Render(target, GetGpuCamera(target), hold);

            device.SetRenderTarget(null);
            _gpuWorld = null;

            RenderedObjectsCount = captured + deferred;
            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
            FrameStats.End(FramePhase.WorldDraw);
            FrameStats.Set(FrameCounter.RenderListEntries, RenderedObjectsCount);

            if (gpu.World != null)
            {
                FrameStats.Set(FrameCounter.GpuInstances, gpu.World.Instances + gpu.World.DynamicDrawn);
                FrameStats.Set(FrameCounter.GpuDynamic, gpu.World.DynamicDrawn);
                FrameStats.Set(FrameCounter.GpuOverrides, gpu.World.OverridesApplied);
                FrameStats.Set(FrameCounter.GpuDrawCalls, gpu.World.Renderer.DrawCalls);
            }
        }

        // The batcher's world matrix maps p = world - _offset to (p - c) / zoom + c - cameraOffset, c = the target's centre.
        private WorldCamera GetGpuCamera(RenderTarget2D target)
        {
            float scale = 1f / Camera.Zoom;
            Vector2 peek = Camera.Offset;

            return new WorldCamera
            {
                CenterX = _offset.X + Camera.Bounds.Width * 0.5 + peek.X / scale,
                CenterY = _offset.Y + Camera.Bounds.Height * 0.5 + peek.Y / scale,
                Zoom = scale,
                ViewportWidth = target.Width,
                ViewportHeight = target.Height
            };
        }
    }
}
