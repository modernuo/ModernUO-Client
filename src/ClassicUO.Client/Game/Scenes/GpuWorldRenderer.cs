// SPDX-License-Identifier: BSD-2-Clause

extern alias sdl3core;

using System;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Renderer;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Uo.Pack;
using Uo.Render;
using GpuTextureFormat = sdl3core::SDL3.SDL.SDL_GPUTextureFormat;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// The world pass of Uo.Render (poc/Uo.Render) on FNA3D's SDL_GPU device, rendering into the world render target.
    /// Land and statics are baked from the pack's map regions; GameScene's list fill stays the authority for visibility,
    /// alpha and picking and reports the differences as per-sprite overrides. Items, mobiles and multis are dynamic
    /// sprites captured from their views' draw calls (<see cref="ISpriteCapture"/>), so the views' animation, hue and
    /// layering logic is reused unchanged. Mobiles are the exception (W3, setting <c>gpu_ecs_sprites</c>): their sprites
    /// come from the ECS (<see cref="MobileAnimationSystem"/> then <see cref="MobileSpriteExtract"/>), which also reproduces
    /// what the view does besides drawing (FrameInfo, the mirror bit, lights, the aura). Off, they are captured too.
    /// </summary>
    internal sealed class GpuWorldRenderer : ISpriteCapture, IDisposable
    {
        private const int RANK_ITEM = 6;
        private const int RANK_MOBILE = 7;

        private readonly IntPtr _device;
        private readonly PackSet _basePack;
        private PackSet _pack;
        private WorldRenderer _world;

        private SpriteInstance[] _dynamic = new SpriteInstance[4096];
        private int _dynamicCount;
        private GameObject _object;
        private int _objectRank;
        private bool _shadowOnly;
        private InstanceBlend _blend;
        private bool _wobble;
        private int _offsetX, _offsetY;
        private int _failedFacet = -1;
        private static readonly bool GrowOnDemand = Environment.GetEnvironmentVariable("CUO_GPU_GROW") != "0";
        private Season _landSeason = (Season)0xFF;
        private bool _ecsMobiles;
        private GameScene _scene;

        private GpuWorldRenderer(IntPtr device, PackSet pack)
        {
            _device = device;
            _basePack = pack;
        }

        public WorldRenderer World => _world;
        public int CaptureFallbacks { get; private set; }

        /// <summary>E12 (CUO_GPU_ITEM_CHECK): the probe pass runs the capture before the batcher begins (and suppressed:
        /// UltimaBatcher2D.BeginSuppress) and discards what it can't map (the batcher would draw it); <see cref="ProbeFallbacks"/>
        /// counts those. Each object it captures has its view state (FrameInfo, the mirror bit: picking reads them) journaled in
        /// <see cref="ProbeViews"/> first, so the probe can put it back.</summary>
        internal bool Probe;
        internal int ProbeFallbacks;
        internal System.Collections.Generic.List<(GameObject Obj, Rectangle FrameInfo, bool Flipped)> ProbeViews;

        /// <summary>E12: a sprite the capture couldn't map (FNA draws it, or the probe drops it), as the view asked for it:
        /// the whole request, including which draw asked (<see cref="FallbackKind"/>) and a seated draw's deformation.</summary>
        internal readonly record struct FallbackDraw(GameObject Obj, Texture2D Texture, Vector2 Position, Rectangle Source, Vector3 Hue, bool Flip,
                                                     float Depth, SpriteCaptureMode Mode, float Rotation, InstanceBlend Blend, FallbackKind Kind, Vector3 Mod);

        /// <summary>E12: the draw call behind a <see cref="FallbackDraw"/>.</summary>
        internal enum FallbackKind : byte
        {
            Normal,
            Scaled,
            Seated
        }

        // E12: set around Emit by the scaled and seated draws, so a fallback records which draw asked
        private FallbackKind _emitKind;
        private Vector3 _emitMod;

        /// <summary>E12: while set (both passes of a check frame), unmapped sprites are recorded in <see cref="Fallbacks"/>.</summary>
        internal bool RecordFallbacks;
        internal System.Collections.Generic.List<FallbackDraw> Fallbacks;

        /// <summary>E12: the dynamic instances captured so far this frame.</summary>
        internal ReadOnlySpan<SpriteInstance> DynamicInstances => new ReadOnlySpan<SpriteInstance>(_dynamic, 0, _dynamicCount);

        /// <summary>E12: forgets the dynamic instances captured so far (the probe pass's).</summary>
        internal void ResetDynamic()
        {
            _dynamicCount = 0;
            _object = null;
        }

        /// <summary>Dynamic instances not drawn because a frame had more than WorldOptions.MaxDynamic (cumulative).</summary>
        public long DynamicDropped { get; private set; }

        /// <summary>Null when FNA3D is not on the SDL_GPU driver or the pack can't be opened (<paramref name="error"/> says
        /// why). There is no Legacy fallback: the caller reports the error and exits (GpuDeviceLost.FailWorldSetup).</summary>
        public static GpuWorldRenderer TryCreate(IntPtr device, UOFileManager fileManager, out string error)
        {
            error = null;

            if (device == IntPtr.Zero)
            {
                error = "the GPU world renderer needs FNA3D on the SDL_GPU driver (no SDL_GPUDevice)";
                Log.Error(error);

                return null;
            }

            try
            {
                var tables = new PackTables
                {
                    Sprites = fileManager.Arts.PackSpriteEntries,
                    Texmaps = fileManager.Texmaps.PackSpriteEntries,
                    HueCount = fileManager.Hues.PackHueCount,
                    Hues = fileManager.Hues.PackHues,
                    AnimBodies = fileManager.Animations.PackBodies,
                    AnimDirs = fileManager.Animations.PackDirs,
                    AnimFrames = fileManager.Animations.PackFrames,
                    FramePalette = fileManager.Animations.PackFramePalette,
                    StaticNoCot = BuildNoCotTable(fileManager.TileData.StaticData.Length)
                };

                var pack = new PackSet(fileManager.Pack.Root, 0, null, tables);
                Log.Trace($"GPU world renderer: pack {pack.Root}, {PageFormats.Name(pack.Format)} {pack.PageSize}px pages, anims {(pack.AnimIndexed ? "Indexed8" : "16-bit")}");

                return new GpuWorldRenderer(device, pack);
            }
            catch (Exception ex)
            {
                error = $"the asset pack could not be opened for the GPU world renderer: {ex.Message}";
                Log.Error($"GPU world renderer unavailable: {ex}");

                return null;
            }
        }

        // StaticView: trees (and foliage, flagged in tiledata) stay opaque inside the circle of transparency
        private static bool[] BuildNoCotTable(int count)
        {
            var table = new bool[Math.Max(count, 0x10000)];

            for (int g = 0; g < table.Length && g <= 0xFFFF; g++)
            {
                table[g] = StaticFilters.IsTree((ushort)g, out _);
            }

            return table;
        }

        /// <summary>Streams the given map; a facet change recreates the world pass (its chunk store belongs to one facet).
        /// False only when the world pass can't be created; GpuDeviceLost.FailWorldSetup has then been called (the process
        /// exits at the end of the frame), and the caller draws no world.</summary>
        public bool EnsureFacet(int facet)
        {
            if (TryEnsureFacet(facet, out string error))
            {
                return true;
            }

            if (error != null)
            {
                GpuDeviceLost.FailWorldSetup(error);
            }

            return false;
        }

        /// <summary>EnsureFacet without the exit: false with <paramref name="error"/> (null if this facet failed before).
        /// RendererStartup's world probe uses it at startup, where a failure means Legacy (auto), not an exit.</summary>
        public bool TryEnsureFacet(int facet, out string error)
        {
            error = null;

            if (_world != null && _pack.Facet == facet)
            {
                return true;
            }

            if (facet == _failedFacet)
            {
                return false;
            }

            if (facet == 1 && !File.Exists(Path.Combine(_basePack.Root, "map", "1", "facet.json")))
            {
                facet = 0;

                if (_world != null && _pack.Facet == 0)
                {
                    return true;
                }
            }

            if (_world != null)
            {
                // Facet change (and the login after the startup probe built map 0): keep the device resources, the
                // resident pages and every CPU array, and re-stream the chunks of the new facet. Building a second world
                // left about 34 MiB of dead large objects and re-allocated the GPU arrays.
                try
                {
                    _pack = _basePack.WithFacet(facet);
                    _pack.LandRemap = null;
                    _landSeason = (Season)0xFF;
                    _world.ChangeFacet(_pack);
                    Log.Trace($"GPU world renderer: facet {facet} (switched, world pass kept)");
                    ArmLoadLog($"facet {facet}");

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error($"GPU world renderer: switching to facet {facet} failed, rebuilding: {ex}");
                }
            }

            try
            {
                DisposeWorld();
                _pack = _basePack.WithFacet(facet);

                var options = new WorldOptions
                {
                    OverrideSlots = 16384,
                    MaxTranslucent = 8192,
                    // the large-scale scene (2,000 mobiles, 20,000 items) needs about 33k dynamic instances and 82 anim
                    // pages zoomed out; at 32768 / 64 the last sprites were dropped and the anim pages thrashed
                    // (4 MiB of instances, 1 MiB per Indexed8 anim layer)
                    MaxDynamic = 131072,
                    // Grow on demand (memory per client, several clients at once): the anim pages and instance buffer
                    // start at 32 layers (32 MiB) and 256k instances (8 MiB) and double at 75% occupancy up to the old
                    // fixed sizes, 128 / 2M (128 + 64 MiB), which the large scene still reaches. The art pages start at
                    // their ceiling, 24 layers (256 MiB): every login filled 12 by frame 15 (GrowthPolicy.ArtLayersStart).
                    // CUO_GPU_GROW=0 starts at the ceilings (the old fixed sizes, for A/B).
                    Layers = GrowOnDemand ? GrowthPolicy.ArtLayersStart : GrowthPolicy.ArtLayersMax,
                    LayersMax = GrowthPolicy.ArtLayersMax,
                    AnimLayers = GrowOnDemand ? GrowthPolicy.AnimLayersStart : GrowthPolicy.AnimLayersMax,
                    AnimLayersMax = GrowthPolicy.AnimLayersMax,
                    InstanceCapacity = GrowOnDemand ? GrowthPolicy.InstancesStart : GrowthPolicy.InstancesMax,
                    InstanceCapacityMax = GrowthPolicy.InstancesMax
                };

                _pack.LandRemap = null;
                _landSeason = (Season)0xFF;
                _world = WorldRenderer.CreateExternal(_device, GpuTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, _pack, options);
                _world.ClearColor = new sdl3core::SDL3.SDL.SDL_FColor { r = 0, g = 0, b = 0, a = 1 };
                Log.Trace($"GPU world renderer: facet {facet}, shaders {_world.Renderer.ShaderFormat}");
                ArmLoadLog($"facet {facet}, new world");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"GPU world renderer: facet {facet} failed: {ex}");
                DisposeWorld();
                _failedFacet = facet;
                error = $"the GPU world pass for map {facet} could not be created: {ex.Message}";

                return false;
            }
        }

        public void BeginFrame()
        {
            _dynamicCount = 0;
            _object = null;
            CaptureFallbacks = 0;
            UpdateWaterWobble();
            EcsParity.BeginFrame();
#if CUO_DEVTOOLS
            GoldenCapture.BeginGpu();
#endif
            ItemSpriteInput.BeginFrame();
            SpritePickTable.BeginDraw();
            _ecsMobiles = Configuration.Settings.GlobalSettings.GpuEcsSprites && _pack != null && _pack.HasAnims;
            _scene = Client.Game.GetScene<GameScene>();
        }

        /// <summary>Mobiles' sprites come from the ECS this frame (gpu_ecs_sprites), not from MobileView.Draw.</summary>
        public bool EcsMobiles => _ecsMobiles;

        /// <summary>GameScene's draw offset: world pixel = screen position + offset.</summary>
        public void SetOffset(int offsetX, int offsetY)
        {
            _offsetX = offsetX;
            _offsetY = offsetY;
        }

        public void LandOverride(int x, int y, byte alpha, bool hidden, ushort highlightHue, bool forceShow = false)
        {
            _world.SetLandOverride(x, y, alpha, hidden, highlightHue, forceShow);
        }

        public void StaticOverride(int x, int y, int z, ushort graphic, byte alpha, bool hidden, ushort highlightHue, bool forceShow = false)
        {
            _world.SetStaticOverride(x, y, z, graphic, alpha, hidden, highlightHue, forceShow);
        }

        /// <summary>Season land graphics (SeasonManager): the baked land uses the season's tiles. Rebakes on change.</summary>
        public void SetLandSeason(Season season)
        {
            if (season == _landSeason || _world == null)
            {
                return;
            }

            _landSeason = season;
            ushort[] remap = null;

            for (int g = 0; g < 0x4000; g++)
            {
                ushort to = SeasonManager.GetLandSeasonGraphic(season, (ushort)g);

                if (to != g)
                {
                    remap ??= CreateIdentityRemap();
                    remap[g] = (ushort)(to & 0x3FFF);
                }
            }

            if (remap != null || _pack.LandRemap != null)
            {
                Log.Trace($"GPU world renderer: {season} land tiles, rebaking");
                _world.SetLandRemap(remap);
            }
        }

        private static ushort[] CreateIdentityRemap()
        {
            var remap = new ushort[0x4000];

            for (int i = 0; i < remap.Length; i++)
            {
                remap[i] = (ushort)i;
            }

            return remap;
        }

        /// <summary>GameScene's _maxZ / _maxGroundZ / _noDrawRoofs, applied to baked land and statics in the shader.</summary>
        public void SetZHiding(int maxZ, int maxGroundZ, bool hideRoofs)
        {
            _world.SetZHiding(maxZ, maxGroundZ, hideRoofs);
        }

        /// <summary>Whole-sprite hue for all baked land and statics this frame (the death screen's gray), 0 = none.</summary>
        public void SetTint(ushort hue)
        {
            _world.SetTint(hue);
        }

        /// <summary>Out-of-range hue (NoColorObjectsOutOfRange) for baked land and statics farther than range tiles from the
        /// player's tile this frame; 0 = none.</summary>
        public void SetRangeTint(ushort hue, int tileX, int tileY, int range)
        {
            _world.SetRangeTint(hue, tileX, tileY, range);
        }

        /// <summary>Shader-mode circle of transparency: centre and radius in target pixels, z = player z + 5.</summary>
        public void SetCircleOfTransparency(float centerX, float centerY, float radius, int z)
        {
            _world.SetCircleOfTransparency(centerX, centerY, radius, z);
        }

        /// <summary>
        /// ClassicUO's animated water (profile AnimatedWaterEffect, off by default): wet flat land gets a baked scaled copy
        /// (a change rebakes), and every <see cref="SpriteInstance.LightWobble"/> instance is scaled in the shader by
        /// View.DrawStatic's time-based factor, the same Time.Ticks the views read this frame.
        /// </summary>
        private void UpdateWaterWobble()
        {
            _wobble = ProfileManager.CurrentProfile?.AnimatedWaterEffect == true;

            if (_world == null)
            {
                return;
            }

            float t = Time.Ticks / 1000f;
            _world.SetWaterWobble(_wobble, 1.1f + MathF.Sin(t) * 0.1f, 1.1f + MathF.Cos(t) * 0.5f * 0.1f);
        }

        /// <summary>Every effect blend is reproduced by the world pass (Normal; Screen as additive; Multiply, ScreenLess,
        /// NormalHalfTransparent and ShadowBlue with their own translucent pipelines; ScreenRed draws normally, as in
        /// GameEffectView), so effects are depth-tested against the world. Sprites the capture can't map stay with FNA.</summary>
        public static bool CanCapture(GameEffect effect) => true;

        /// <summary>GameEffectView.Draw's blend state as the instance blend.</summary>
        internal static InstanceBlend BlendOf(GraphicEffectBlendMode blend) => blend switch
        {
            GraphicEffectBlendMode.Multiply => InstanceBlend.Multiply,
            GraphicEffectBlendMode.Screen or GraphicEffectBlendMode.ScreenMore => InstanceBlend.Screen,
            GraphicEffectBlendMode.ScreenLess => InstanceBlend.ScreenLess,
            GraphicEffectBlendMode.NormalHalfTransparent => InstanceBlend.NormalHalf,
            GraphicEffectBlendMode.ShadowBlue => InstanceBlend.ShadowBlue,
            // normal effects too go to the translucent pass (opaque output), so equal-depth effects keep painter order
            _ => InstanceBlend.NormalOrdered
        };

        /// <summary>
        /// T7b part 3 (ecs_effect_sprites): one effect of the effect list. While the switch is on an effect the extract handles
        /// is emitted from its state (<see cref="EffectSpriteExtract"/>, <see cref="GpuEffectResolve"/>) with the view's light;
        /// otherwise, and for lightning, effects without a state and art the pack lacks, its view is captured
        /// (<see cref="DrawObject"/>). CUO_EFFECT_SPRITE_CHECK compares both (not in the E12 probe pass, which the probe's own
        /// comparison covers).
        /// </summary>
        public bool DrawEffect(UltimaBatcher2D batcher, GameEffect effect, float depth)
        {
#if CUO_DEVTOOLS
            if (GoldenCapture.Recording && !Probe)
            {
                GoldenCapture.Mark(effect, _dynamicCount);
            }
#endif
            if (EffectSpriteExtract.Handles(effect))
            {
#if CUO_DEVTOOLS
                if (EffectSpriteCheck.Active && !Probe)
                {
                    return CompareEffect(batcher, effect, depth);
                }
#endif

                if (EffectSpriteSink.On && TryEmitEffect(effect, depth, out bool drawn))
                {
                    return drawn;
                }
            }

            return DrawObject(batcher, effect, depth);
        }

        // the extract's instances and light for one effect; false = left to its view (nothing emitted)
        private bool TryEmitEffect(GameEffect effect, float depth, out bool drawn)
        {
            drawn = false;

            if (!EffectSpriteInput.Gather(effect, out EffectSpriteInput input))
            {
                return false;
            }

            EffectSpriteExtract extract = EffectSpriteSink.Extract;
            drawn = extract.Select(in input, effect.RealScreenPosition.X, effect.RealScreenPosition.Y, depth);

            if (extract.MissingArt)
            {
                drawn = false;

                return false;
            }

            if (extract.HasSprite)
            {
                var tables = new GpuSpriteTables(_pack);
                GpuEffectResolve.Resolve(in tables, in extract.Sprite, _offsetX, _offsetY, ref _dynamic, ref _dynamicCount);
            }

            if (extract.Light)
            {
                _scene?.AddLight(input.Source, input.Source, extract.LightX, extract.LightY);
            }

            return true;
        }

#if CUO_DEVTOOLS
        // CUO_EFFECT_SPRITE_CHECK: the extract's instances, then the view's capture after them; the switch's stay
        private bool CompareEffect(UltimaBatcher2D batcher, GameEffect effect, float depth)
        {
            int start = _dynamicCount;
            int lightsBefore = _scene?.LightCount ?? 0;

            if (!TryEmitEffect(effect, depth, out bool ecsDrawn))
            {
                EffectSpriteCheck.GpuViewPath();

                return DrawObject(batcher, effect, depth);
            }

            int ecsEnd = _dynamicCount;
            int lightsEcs = _scene?.LightCount ?? 0;
            (ushort, int, int) pickEcs = EffectSpriteCheck.PickEntry(EffectSpriteSink.Extract);
            int fallbacks = CaptureFallbacks;
            bool viewDrawn = DrawObject(batcher, effect, depth);
            (ushort, int, int) pickView = EffectSpriteCheck.PickEntry(EffectSpriteSink.Extract);
            int viewEnd = _dynamicCount;

            EffectSpriteCheck.CompareGpu(effect, new ReadOnlySpan<SpriteInstance>(_dynamic, start, ecsEnd - start),
                new ReadOnlySpan<SpriteInstance>(_dynamic, ecsEnd, viewEnd - ecsEnd), ecsDrawn, viewDrawn, CaptureFallbacks != fallbacks, pickEcs, pickView,
                _scene, lightsBefore, lightsEcs);

            if (EffectSpriteSink.On)
            {
                _dynamicCount = ecsEnd;

                return ecsDrawn;
            }

            Array.Copy(_dynamic, ecsEnd, _dynamic, start, viewEnd - ecsEnd);
            _dynamicCount = start + viewEnd - ecsEnd;

            return viewDrawn;
        }
#endif

        /// <summary>Draws a world object's view into the dynamic sprites. Sprites the capture can't map stay with the batcher.</summary>
        public bool DrawObject(UltimaBatcher2D batcher, GameObject obj, float depth, bool shadowOnly = false)
        {
            if (Probe)
            {
                ProbeViews.Add((obj, obj.FrameInfo, obj.ViewFlipped));
            }
#if CUO_DEVTOOLS
            else if (GoldenCapture.Recording)
            {
                GoldenCapture.Mark(obj, _dynamicCount);
            }
#endif

            if (_ecsMobiles && obj is Mobile m && !shadowOnly && TryDrawMobileEcs(batcher, m, depth, out bool ecsDrawn))
            {
                return ecsDrawn;
            }

            if (_ecsMobiles && obj is Item it && !shadowOnly && ItemSpriteExtract.Handles(it))
            {
                return DrawItemEcs(batcher, it, depth);
            }

            _object = obj;
            _shadowOnly = shadowOnly;
            _objectRank = obj is Mobile ? RANK_MOBILE : RANK_ITEM;
            _blend = obj is GameEffect effect ? BlendOf(effect.Blend) : InstanceBlend.Normal;
            batcher.Capture = this;

            try
            {
                // W3 parity harness (CUO_ECS_PARITY): the ECS extract for the same mobile, diffed against this capture
                if (EcsParity.Active && obj is Mobile mobile && !shadowOnly)
                {
                    int start = _dynamicCount;
                    int lightsBefore = _scene?.LightCount ?? 0;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool drawn = obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
                    long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    EcsParity.OnCaptured(mobile, depth, _dynamic, start, _dynamicCount, _pack, _offsetX, _offsetY, ticks);
                    EcsParity.CompareViewEffects(mobile, mobile.World.Ecs.MobileExtract, _scene, lightsBefore);

                    return drawn;
                }

                return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
            }
            finally
            {
                batcher.Capture = null;
                _object = null;
            }
        }

        /// <summary>
        /// W3: a mobile's dynamic sprites from the ECS, plus what MobileView.Draw does besides drawing (aura, FrameInfo,
        /// IsFlipped, lights). On EcsParity frames the view is drawn under capture first as the reference (its side effects
        /// stand) and its instances are replaced by the ECS ones after the diff. False = not mirrored: capture it instead.
        /// </summary>
        private readonly ItemSpriteExtract _itemExtract = new ItemSpriteExtract();

        /// <summary>
        /// W3: an art item's dynamic sprites from <see cref="ItemSpriteExtract"/> instead of ItemView.Draw, plus its light.
        /// On EcsParity frames the view is drawn under capture first as the reference, as for mobiles.
        /// </summary>
        private bool DrawItemEcs(UltimaBatcher2D batcher, Item item, float depth)
        {
            int posX = item.RealScreenPosition.X, posY = item.RealScreenPosition.Y;
            int start = _dynamicCount;
            bool drawn;

            if (EcsParity.Active)
            {
                int lightsBefore = _scene?.LightCount ?? 0;
                _object = item;
                _shadowOnly = false;
                _objectRank = RANK_ITEM;
                _blend = InstanceBlend.Normal;
                batcher.Capture = this;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool viewDrawn;

                try
                {
                    viewDrawn = item.Draw(batcher, posX, posY, depth);
                }
                finally
                {
                    batcher.Capture = null;
                    _object = null;
                }

                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                drawn = EmitItem(item, _offsetX, _offsetY, posX, posY, depth);
                long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                EcsParity.OnItemCaptured(item, _dynamic, start, _dynamicCount, _itemExtract, viewDrawn, drawn, _scene, lightsBefore, t1 - t0, t2 - t1);
                _dynamicCount = start;
            }
            else
            {
                drawn = EmitItem(item, _offsetX, _offsetY, posX, posY, depth);

                if (_itemExtract.Light)
                {
                    _scene?.AddLight(item, item, _itemExtract.LightX, _itemExtract.LightY);
                }
            }

            if (!Probe)
            {
                SpritePickTable.RecordItem(item, _itemExtract);
            }

            ReadOnlySpan<SpriteInstance> got = _itemExtract.Instances;

            if (GameScene.GpuItemCheckActive)
            {
                _scene?.GpuItemCheckDrawn(item, got, _itemExtract.Light, _itemExtract.LightX, _itemExtract.LightY);
            }

            AppendDynamic(got);

            return drawn;
        }

        /// <summary>W4 (GameSceneGpuItemCache): <see cref="ItemSpriteExtract.Emit"/> for a chunk's item cache, with the
        /// scene's draw offset (the frame's <see cref="SetOffset"/> comes after the walk). The result is in <see cref="ItemExtract"/>.</summary>
        public bool ExtractItem(Item item, int offsetX, int offsetY, int posX, int posY, float depth) => EmitItem(item, offsetX, offsetY, posX, posY, depth);

        // T7b: the extract reads the item's input (its mirror record, plus what is still only on the GameObject); a
        // corpse's mirror bit goes back to the GameObject, as the view's DrawCorpse stores it
        private bool EmitItem(Item item, int offsetX, int offsetY, int posX, int posY, float depth)
        {
            ItemSpriteInput.Gather(item, out ItemSpriteInput input);
            bool drawn = _itemExtract.Emit(in input, _pack, offsetX, offsetY, posX, posY, depth);

            if (_itemExtract.SetsFlipped)
            {
                item.ViewFlipped = _itemExtract.Flipped;
            }

            return drawn;
        }

        public ItemSpriteExtract ItemExtract => _itemExtract;

        /// <summary>Appends ready dynamic instances (the item cache's), in capture order.</summary>
        public void AppendDynamic(ReadOnlySpan<SpriteInstance> got)
        {
            if (_dynamicCount + got.Length > _dynamic.Length)
            {
                Array.Resize(ref _dynamic, Math.Max(_dynamic.Length * 2, _dynamicCount + got.Length));
            }

            got.CopyTo(new Span<SpriteInstance>(_dynamic, _dynamicCount, got.Length));
            _dynamicCount += got.Length;
        }

        private bool TryDrawMobileEcs(UltimaBatcher2D batcher, Mobile mobile, float depth, out bool drawn)
        {
            drawn = false;

            if (mobile.IsDestroyed || !mobile.AllowedToDraw)
            {
                return true;
            }

            World world = mobile.World;
            WorldEcs ecs = world.Ecs;

            // E5 (MobileEntityLookup, shared with the Legacy sink)
            if (!MobileEntityLookup.TryGet(mobile, out Friflo.Engine.ECS.Entity e))
            {
                return false;
            }

            MobileSpriteExtract extract = ecs.MobileExtract;
            int posX = mobile.RealScreenPosition.X, posY = mobile.RealScreenPosition.Y;
            int start = _dynamicCount;

            if (EcsParity.Active)
            {
                int lightsBefore = _scene?.LightCount ?? 0;
                _object = mobile;
                _shadowOnly = false;
                _objectRank = RANK_MOBILE;
                _blend = InstanceBlend.Normal;
                batcher.Capture = this;

                try
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    drawn = mobile.Draw(batcher, posX, posY, depth);
                    long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    EcsParity.OnCaptured(mobile, depth, _dynamic, start, _dynamicCount, _pack, _offsetX, _offsetY, ticks);
                }
                finally
                {
                    batcher.Capture = null;
                    _object = null;
                }

                EcsParity.CompareViewEffects(mobile, extract, _scene, lightsBefore);
                _dynamicCount = start;
            }
            else
            {
                // e was looked up above (mirrored, with AnimationState) and the mobile is not destroyed: Update(world, mobile)'s checks.
                // E5: the update works on the entity's own state, which the extract then reads without more lookups.
                // TakeDirty comes first: removing the tag moves the entity, which would invalidate the refs.
                bool dirty = MobileAnimationSystem.TakeDirty(e);
                // one record read for both components (as ItemSpriteInput.ReadRecord)
                ClassicUO.Ecs.EcsRow data = ClassicUO.Ecs.EcsRead.Row(e);
                ref ClassicUO.Ecs.AnimationState state = ref data.Get<ClassicUO.Ecs.AnimationState>();
                ref ClassicUO.Ecs.MobileRenderState render = ref data.Get<ClassicUO.Ecs.MobileRenderState>();
                ecs.Animation.UpdateInPlace(world, mobile, e, data, dirty, ref state, ref render);

                mobile.DrawAura(batcher, posX, posY, depth);
                extract.Begin(_pack, _offsetX, _offsetY);
                extract.Emit(e, data, in state, in render, posX, posY, depth, mobile.FrameCache);
                drawn = true;

                // the view's side effects: picking (unless recorded below), text and health bars read FrameInfo and IsFlipped
                bool mirror = (state.Flags & ClassicUO.Ecs.AnimStateFlags.Mirror) != 0;
                mobile.FrameInfo = extract.FrameInfo;
                mobile.ViewFlipped = mirror;

                if (!Probe)
                {
                    // T7b part 2b: picking reads the same state from the records (the side effects above stay: other
                    // readers, the mirror history). EcsParity frames keep the view's and record nothing.
                    SpritePickTable.RecordMobile(mobile, extract.FrameInfo, mirror);
                }

                if (_scene != null)
                {
                    foreach (ref readonly MobileSpriteExtract.LightRequest light in extract.Lights)
                    {
                        Item item = world.Items.Get(light.Item);

                        if (item != null)
                        {
                            _scene.AddLight(mobile, item, light.X, light.Y);
                        }
                    }
                }
            }

            ReadOnlySpan<SpriteInstance> got = extract.Instances;

            if (_dynamicCount + got.Length > _dynamic.Length)
            {
                Array.Resize(ref _dynamic, Math.Max(_dynamic.Length * 2, _dynamicCount + got.Length));
            }

            got.CopyTo(new Span<SpriteInstance>(_dynamic, _dynamicCount, got.Length));
            _dynamicCount += got.Length;

            return true;
        }

        public bool Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode)
        {
            if (_object == null)
            {
                return false;
            }

            if (_shadowOnly && mode != SpriteCaptureMode.Shadow)
            {
                return true;
            }

            if (scaled)
            {
                // The only scaled world draw is View.DrawStatic's animated-water copy (wet statics the view draws, e.g.
                // animated ones; baked land gets its copy in the bake): scaled in the shader by the frame's water scale.
                // Anything else scaled isn't supported by the world pass and is dropped.
                if (!_wobble || _object is not (Static or Land))
                {
                    return true;
                }

                int before = _dynamicCount;
                _emitKind = FallbackKind.Scaled;
                bool emitted = Emit(texture, position, source, hue, flip, depth, mode, 0f);
                _emitKind = FallbackKind.Normal;

                if (!emitted)
                {
                    return false;
                }

                if (_dynamicCount > before)
                {
                    ref SpriteInstance inst = ref _dynamic[_dynamicCount - 1];
                    int px = (int)MathF.Round(position.X) + _offsetX, py = (int)MathF.Round(position.Y) + _offsetY;
                    inst.Light |= SpriteInstance.WobbleBits(inst.X - px, inst.Y - py);
                }

                return true;
            }

            return Emit(texture, position, source, hue, flip, depth, mode, 0f);
        }

        public bool DrawSitted(Texture2D texture, Vector2 position, Rectangle source, Vector3 mod, Vector3 hue, bool flip, float depth)
        {
            if (_object == null || _shadowOnly)
            {
                return _object != null;
            }

            int before = _dynamicCount;
            _emitKind = FallbackKind.Seated;
            _emitMod = mod;
            bool emitted = Emit(texture, position, source, hue, flip, depth, SpriteCaptureMode.Normal, 0f);
            _emitKind = FallbackKind.Normal;
            _emitMod = default;

            if (!emitted)
            {
                return false;
            }

            if (_dynamicCount > before)
            {
                SitDeform.Split(ref _dynamic, ref _dynamicCount, before, (int)MathF.Round(position.Y) + _offsetY, source.Height, mod, flip);
            }

            return true;
        }

        public bool DrawRotated(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, float rotation, float depth)
        {
            if (_object == null || _shadowOnly)
            {
                return _object != null;
            }

            return Emit(texture, position, source, hue, false, depth, SpriteCaptureMode.Normal, rotation);
        }

        // A sprite the world pass can't draw: false = the batcher draws it (the probe pass drops it instead).
        private bool Fallback(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, float depth, SpriteCaptureMode mode, float rotation)
        {
            CaptureFallbacks++;

            if (RecordFallbacks)
            {
                Fallbacks.Add(new FallbackDraw(_object, texture, position, source, hue, flip, depth, mode, rotation, _blend, _emitKind, _emitMod));
            }

            if (Probe)
            {
                ProbeFallbacks++;

                return true;
            }

            return false;
        }

        private bool Emit(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, float depth, SpriteCaptureMode mode, float rotation)
        {
            if (!PackSpriteMap.TryFind(texture, source, out uint packRef, out Point origin))
            {
                return Fallback(texture, position, source, hue, flip, depth, mode, rotation);
            }

            // alpha > 1 asks IsometricWorld.fx for the circle of transparency
            bool cot = hue.Z > 1f;
            float alpha = cot ? hue.Z - 1f : hue.Z;

            if (alpha <= 0f)
            {
                return true;
            }

            uint flags = 0;
            uint hueRow = 0;
            var hueMode = HueMode.Normal;
            int type = (int)hue.Y;
            int row = (int)hue.X;

            switch (type)
            {
                case ShaderHueTranslator.SHADER_NONE:
                    break;

                case ShaderHueTranslator.SHADER_HUED:
                case ShaderHueTranslator.SHADER_PARTIAL_HUED:
                case ShaderHueTranslator.SHADER_EFFECT_HUED:
                    if (row >= 0 && row < _pack.HueCount)
                    {
                        hueRow = (uint)row;
                        flags = SpriteInstance.FlagHued | (type == ShaderHueTranslator.SHADER_PARTIAL_HUED ? SpriteInstance.FlagPartial : 0);
                    }

                    if (type == ShaderHueTranslator.SHADER_EFFECT_HUED)
                    {
                        hueMode = HueMode.EffectHued;
                    }

                    break;

                case ShaderHueTranslator.SHADER_SPECTRAL:
                    hueMode = HueMode.Spectral;

                    break;

                case ShaderHueTranslator.SHADER_SHADOW:
                    hueMode = HueMode.Shadow;

                    break;

                default:
                    return Fallback(texture, position, source, hue, flip, depth, mode, rotation);
            }

            SpriteEntry sp;
            int page;
            uint palette = 0;

            if ((packRef & PackSpriteMap.AnimFrame) != 0)
            {
                uint index = packRef & ~PackSpriteMap.AnimFrame;
                sp = _pack.AnimFrames[index].Sprite;
                page = _pack.AnimPageBase + sp.Page;

                if (_pack.AnimIndexed)
                {
                    flags |= SpriteInstance.FlagIndexed;
                    palette = _pack.FramePalette[index];
                }
            }
            else
            {
                sp = _pack.Sprites[packRef];
                page = sp.Page;
            }

            if (!sp.Present)
            {
                return true;
            }

            // the drawn rectangle in untrimmed sprite pixels, clipped to the pack's trimmed rectangle
            int rx0 = source.X - origin.X, ry0 = source.Y - origin.Y;
            int rx1 = rx0 + source.Width, ry1 = ry0 + source.Height;
            int ix0 = Math.Max(rx0, sp.TrimX), iy0 = Math.Max(ry0, sp.TrimY);
            int ix1 = Math.Min(rx1, sp.TrimX + sp.W), iy1 = Math.Min(ry1, sp.TrimY + sp.H);

            if (ix1 <= ix0 || iy1 <= iy0)
            {
                return true;
            }

            int w = ix1 - ix0, h = iy1 - iy0;
            int px = (int)MathF.Round(position.X) + _offsetX;
            int py = (int)MathF.Round(position.Y) + _offsetY;
            int dx = flip ? rx1 - ix1 : ix0 - rx0;
            int dy = iy0 - ry0;
            int x, y;

            if (hueMode == HueMode.Shadow)
            {
                // Batcher2D.DrawShadow shears the whole source rect; the same affine map on the trimmed sub-rect
                int fullH = ry1 - ry0;
                x = px + dx + (fullH - dy - h) / 2;
                y = py + (fullH + dy - h) / 2;
            }
            else if (rotation != 0f)
            {
                // rotate about the untrimmed rect's top-left (Batcher2D origin 0): the trimmed quad's corner moves to R * d
                float sn = MathF.Sin(rotation), cs = MathF.Cos(rotation);
                x = px + (int)MathF.Round(cs * dx - sn * dy);
                y = py + (int)MathF.Round(sn * dx + cs * dy);
            }
            else
            {
                x = px + dx;
                y = py + dy;
            }

            if (_dynamicCount >= _dynamic.Length)
            {
                Array.Resize(ref _dynamic, _dynamic.Length * 2);
            }

            ref var inst = ref _dynamic[_dynamicCount++];
            inst.X = x;
            inst.Y = y;
            inst.Whf = SpriteInstance.PackWhf(w, h, flags | (flip ? SpriteInstance.FlagMirror : 0));
            inst.Uv = (uint)(sp.X + (ix0 - sp.TrimX)) | ((uint)(sp.Y + (iy0 - sp.TrimY)) << 16);
            inst.PageHue = (uint)page | (hueRow << 16);
            inst.Depth = BakeJob.DepthKeyD((int)MathF.Round(depth * 100f) - 50, _object.X, _objectRank);
            inst.Light = 0;
            inst.Corners = palette;
            inst.Mode = hueMode;

            if (cot)
            {
                inst.SetLightBits(SpriteInstance.LightCot);
            }

            if (_blend != InstanceBlend.Normal)
            {
                inst.Blend = _blend;
            }

            if (rotation != 0f)
            {
                inst.SetRotation(rotation);
            }

            if (alpha < 1f)
            {
                inst.Alpha = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 1, 254);
            }

            return true;
        }

        /// <summary>The last Render drew everything its view needs (WorldRenderer.VisibleReady).</summary>
        public bool VisibleReady => _world != null && _world.VisibleReady;

        /// <summary>
        /// Streams around the camera and renders into <paramref name="target"/> (cleared first). Call before FNA submits
        /// the frame that samples the target; FNA draws recorded into it this frame execute after the world on the GPU.
        /// <paramref name="streamOnly"/>: stream and upload only, leaving the target (the held frame) untouched.
        /// </summary>
        public bool Render(RenderTarget2D target, in WorldCamera camera, bool streamOnly = false)
        {
            IntPtr texture = target == null || target.IsDisposed ? IntPtr.Zero : GpuInterop.GetTexture(target);

            if (_world == null || texture == IntPtr.Zero && !streamOnly)
            {
                return false;
            }

            _world.Prepare(in camera);
#if CUO_DEVTOOLS
            GoldenCapture.EndGpu(DynamicInstances);
#endif

            // Ties at equal depth go to the first instance drawn; ClassicUO's painter order gives them to the last one.
            int n = Math.Min(_dynamicCount, _world.DynamicBuffer.Length);
            var dst = _world.DynamicBuffer;

            if (n < _dynamicCount)
            {
                if (DynamicDropped == 0)
                {
                    Log.Warn($"GPU world: {_dynamicCount} dynamic instances, capacity {n}; the rest are not drawn");
                }

                DynamicDropped += _dynamicCount - n;
            }

            for (int i = 0; i < n; i++)
            {
                dst[i] = _dynamic[_dynamicCount - 1 - i];
            }

            _world.DynamicCount = n;

            bool ok = streamOnly
                ? _world.Stream()
                : _world.Render(texture, (uint)target.Width, (uint)target.Height, GpuTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, false);

            if (_world.Renderer.SubmitFailures > 0)
            {
                // e.g. VK_ERROR_DEVICE_LOST; GameController exits at the end of the frame
                GpuDeviceLost.Report("GPU world submit", _world.Renderer.LastSubmitError);
            }

            LogLoad();

            if (_world.Renderer.TranslucentGrowths != _loggedGrowths)
            {
                _loggedGrowths = _world.Renderer.TranslucentGrowths;
                Log.Info($"GPU world: translucent list grown to {_world.Renderer.MaxTranslucent} entries");
            }

            if (_world.GrowthEvents != _loggedArrayGrowths)
            {
                // grow on demand (CUO_GPU_GROW): one line per step, with the step's main-thread time
                _loggedArrayGrowths = _world.GrowthEvents;
                Log.Info($"GPU world: grown at frame {_world.Frame}: {_world.GrowthSummary()}");
            }

            if (BakeJob.TileMismatches != _loggedTileMismatches)
            {
                // the out-of-range tint's tile reconstruction disagreed with the bake (never expected)
                _loggedTileMismatches = BakeJob.TileMismatches;
                Log.Warn($"GPU world: {_loggedTileMismatches} baked instances with a wrong reconstructed tile");
            }

            if (_world.OverridesDropped > 0 && Time.Ticks >= _nextDropLog)
            {
                // the override hash or pending list, or the translucent list at its limit, was full: sprites may be missing
                _nextDropLog = Time.Ticks + 10000;
                Log.Warn($"GPU world: {_world.OverridesDropped} overrides or translucent entries dropped this frame");
            }

            if (DevBench.Enabled && Time.Ticks >= _nextStatsLog)
            {
                _nextStatsLog = Time.Ticks + 5000;
                var w = _world;
                Log.Trace(
                    $"GPU world: {w.ChunkCount} chunks, {w.Instances} baked + {w.DynamicDrawn} dynamic instances, {w.Renderer.DrawCalls} draws; overrides {w.OverridesApplied} applied, " +
                    $"{w.OverridesMissed} missed, {w.OverridesDropped} dropped; dynamic dropped {DynamicDropped}; translucent {w.TranslucentCount} (baked {w.BakedTranslucentCount}, cot ring {w.CotRingCount}); capture fallbacks {CaptureFallbacks}; " +
                    $"pages {w.Pages.ResidentCount}/{w.Pages.Layers} resident, {w.Pages.MissingVisible} missing, {w.Pages.TotalUploads} uploads, {w.Pages.Evictions} evictions" +
                    (w.AnimPages != null ? $"; anim pages {w.AnimPages.ResidentCount}/{w.AnimPages.Layers}, {w.AnimPages.MissingVisible} missing, {w.AnimPages.TotalUploads} uploads, {w.AnimPages.Evictions} evictions" : "") +
                    $"; chunks ready {w.Chunks.ReadyChunks} pending {w.Chunks.PendingChunks} missing near view {w.Chunks.MissingNear}; settled {w.Settled}, visible ready {w.VisibleReady}" +
                    $"; instances used {w.Chunks.Alloc.Used}/{w.Chunks.Alloc.Capacity}" + StagingDetail(w));
            }

            return ok;
        }

        // Scene load time (memory round 3): from the first world frame after a world is built or its facet changes (the
        // login is a facet change) until the world first settles (no chunk pending, no page queued, loading or missing).
        private string _loadWhat;
        private long _loadStartTicks, _loadLastUploadTicks;
        private int _loadFrames, _loadUploads;

        private void ArmLoadLog(string what)
        {
            _loadWhat = what;
            _loadStartTicks = 0;
            _loadFrames = 0;
        }

        private void LogLoad()
        {
            if (_loadWhat == null)
            {
                return;
            }

            var w = _world;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            int uploads = w.Pages.TotalUploads + (w.AnimPages?.TotalUploads ?? 0);

            if (_loadFrames++ == 0)
            {
                _loadStartTicks = now;
                _loadLastUploadTicks = now;
                _loadUploads = uploads;

                return;
            }

            if (uploads != _loadUploads)
            {
                _loadUploads = uploads;
                _loadLastUploadTicks = now;
            }

            if (!w.Settled)
            {
                return;
            }

            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log.Info(
                $"[load] GPU world settled ({_loadWhat}) after {(now - _loadStartTicks) * f:F0} ms, {_loadFrames} frames; last page upload at {(_loadLastUploadTicks - _loadStartTicks) * f:F0} ms; " +
                $"art {w.Pages.ResidentCount}/{w.Pages.Layers} resident, {w.Pages.TotalUploads} uploads" +
                (w.AnimPages != null ? $"; anim {w.AnimPages.ResidentCount}/{w.AnimPages.Layers} resident, {w.AnimPages.TotalUploads} uploads" : "") +
                LoadDetail());
            _loadWhat = null;
        }

        // Staging slot waits (memory round 3: a slot is reused only after its last copy retired): cumulative per world.
        private string LoadDetail() => StagingDetail(_world);

        private static string StagingDetail(Uo.Render.WorldRenderer w) =>
            $"; staging art {w.Pages.Slots.Summary()}" + (w.AnimPages != null ? $", anim {w.AnimPages.Slots.Summary()}" : "") +
            $"; copy fences {w.Renderer.Copies.FencesTaken} taken, {w.Renderer.Copies.PendingFences} pending";

        private uint _nextStatsLog, _nextDropLog;
        private int _loggedGrowths, _loggedTileMismatches, _loggedArrayGrowths;

        public void Dispose()
        {
            DisposeWorld();
        }

        /// <summary>Detaches the world before disposing it: if its teardown throws (a failed drain after device loss), the
        /// caller's catch must not reach the same world again. WorldRenderer.Dispose is one-shot as well.</summary>
        private void DisposeWorld()
        {
            var world = _world;
            _world = null;
            world?.Dispose();
        }
    }
}
