// SPDX-License-Identifier: BSD-2-Clause

extern alias sdl3core;

using System;
using System.IO;
using ClassicUO.Assets;
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
    /// layering logic is reused unchanged. The ECS mirror's DynamicSpriteExtract is not the source yet; swapping it in
    /// means filling <see cref="WorldRenderer.DynamicBuffer"/> from it instead of <see cref="Draw"/>.
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
        private bool _additive;
        private int _offsetX, _offsetY;
        private int _failedFacet = -1;
        private Season _landSeason = (Season)0xFF;

        private GpuWorldRenderer(IntPtr device, PackSet pack)
        {
            _device = device;
            _basePack = pack;
        }

        public WorldRenderer World => _world;
        public int CaptureFallbacks { get; private set; }

        /// <summary>Null when FNA3D is not on the SDL_GPU driver or the pack can't be opened; the caller stays on Legacy.</summary>
        public static GpuWorldRenderer TryCreate(IntPtr device, UOFileManager fileManager)
        {
            if (device == IntPtr.Zero)
            {
                Log.Warn("WorldRenderer=Gpu needs FNA3D on the SDL_GPU driver (no SDL_GPUDevice); falling back to Legacy");

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
                    StaticNoCot = BuildNoCotTable(fileManager.TileData.StaticData.Length)
                };

                var pack = new PackSet(fileManager.Pack.Root, 0, null, tables);
                Log.Trace($"GPU world renderer: pack {pack.Root}, {PageFormats.Name(pack.Format)} {pack.PageSize}px pages, anims {(pack.AnimIndexed ? "Indexed8" : "16-bit")}");

                return new GpuWorldRenderer(device, pack);
            }
            catch (Exception ex)
            {
                Log.Error($"GPU world renderer unavailable, falling back to Legacy: {ex.Message}");

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

        /// <summary>Streams the given map; a facet change recreates the world pass (its chunk store belongs to one facet).</summary>
        public bool EnsureFacet(int facet)
        {
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

            try
            {
                _world?.Dispose();
                _world = null;
                _pack = _basePack.WithFacet(facet);

                var options = new WorldOptions
                {
                    OverrideSlots = 16384,
                    MaxTranslucent = 8192
                };

                _pack.LandRemap = null;
                _landSeason = (Season)0xFF;
                _world = WorldRenderer.CreateExternal(_device, GpuTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, _pack, options);
                _world.ClearColor = new sdl3core::SDL3.SDL.SDL_FColor { r = 0, g = 0, b = 0, a = 1 };
                Log.Trace($"GPU world renderer: facet {facet}, shaders {_world.Renderer.ShaderFormat}");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"GPU world renderer: facet {facet} failed, drawing it with Legacy: {ex}");
                _world?.Dispose();
                _world = null;
                _failedFacet = facet;

                return false;
            }
        }

        public void BeginFrame()
        {
            _dynamicCount = 0;
            _object = null;
            CaptureFallbacks = 0;
        }

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

        /// <summary>Shader-mode circle of transparency: centre and radius in target pixels, z = player z + 5.</summary>
        public void SetCircleOfTransparency(float centerX, float centerY, float radius, int z)
        {
            _world.SetCircleOfTransparency(centerX, centerY, radius, z);
        }

        /// <summary>Effects whose blend the world pass reproduces (normal, and Screen as additive); the rest stay with FNA.</summary>
        public static bool CanCapture(GameEffect effect)
        {
            return effect.Blend is GraphicEffectBlendMode.Normal or GraphicEffectBlendMode.Screen or GraphicEffectBlendMode.ScreenMore;
        }

        /// <summary>Draws a world object's view into the dynamic sprites. Sprites the capture can't map stay with the batcher.</summary>
        public bool DrawObject(UltimaBatcher2D batcher, GameObject obj, float depth, bool shadowOnly = false)
        {
            _object = obj;
            _shadowOnly = shadowOnly;
            _objectRank = obj is Mobile ? RANK_MOBILE : RANK_ITEM;
            _additive = obj is GameEffect effect && effect.Blend != GraphicEffectBlendMode.Normal;
            batcher.Capture = this;

            try
            {
                return obj.Draw(batcher, obj.RealScreenPosition.X, obj.RealScreenPosition.Y, depth);
            }
            finally
            {
                batcher.Capture = null;
                _object = null;
            }
        }

        public bool Draw(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, bool scaled, float depth, SpriteCaptureMode mode)
        {
            if (_object == null)
            {
                return false;
            }

            if (scaled || _shadowOnly && mode != SpriteCaptureMode.Shadow)
            {
                // the animated-water wobble copy and scaled sprites: not supported by the world pass
                return true;
            }

            return Emit(texture, position, source, hue, flip, depth, mode, 0f);
        }

        public bool DrawRotated(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, float rotation, float depth)
        {
            if (_object == null || _shadowOnly)
            {
                return _object != null;
            }

            return Emit(texture, position, source, hue, false, depth, SpriteCaptureMode.Normal, rotation);
        }

        private bool Emit(Texture2D texture, Vector2 position, Rectangle source, Vector3 hue, bool flip, float depth, SpriteCaptureMode mode, float rotation)
        {
            if (!PackSpriteMap.TryFind(texture, source, out uint packRef, out Point origin))
            {
                CaptureFallbacks++;

                return false;
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
                    CaptureFallbacks++;

                    return false;
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

            if (cot || _additive)
            {
                inst.SetLightBits((cot ? SpriteInstance.LightCot : 0) | (_additive ? SpriteInstance.LightAdditive : 0));
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

        /// <summary>
        /// Streams around the camera and renders into <paramref name="target"/> (cleared first). Call before FNA submits
        /// the frame that samples the target; FNA draws recorded into it this frame execute after the world on the GPU.
        /// </summary>
        public bool Render(RenderTarget2D target, in WorldCamera camera)
        {
            IntPtr texture = GpuInterop.GetTexture(target);

            if (_world == null || texture == IntPtr.Zero)
            {
                return false;
            }

            _world.Prepare(in camera);

            // Ties at equal depth go to the first instance drawn; ClassicUO's painter order gives them to the last one.
            int n = Math.Min(_dynamicCount, _world.DynamicBuffer.Length);
            var dst = _world.DynamicBuffer;

            for (int i = 0; i < n; i++)
            {
                dst[i] = _dynamic[_dynamicCount - 1 - i];
            }

            _world.DynamicCount = n;

            bool ok = _world.Render(texture, (uint)target.Width, (uint)target.Height, GpuTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM, false);

            if (DevBench.Enabled && Time.Ticks >= _nextStatsLog)
            {
                _nextStatsLog = Time.Ticks + 5000;
                var w = _world;
                Log.Trace(
                    $"GPU world: {w.ChunkCount} chunks, {w.Instances} baked + {w.DynamicDrawn} dynamic instances, {w.Renderer.DrawCalls} draws; overrides {w.OverridesApplied} applied, " +
                    $"{w.OverridesMissed} missed, {w.OverridesDropped} dropped; translucent {w.TranslucentCount} (cot ring {w.CotRingCount}); capture fallbacks {CaptureFallbacks}; " +
                    $"pages {w.Pages.ResidentCount}/{w.Pages.Layers} resident, {w.Pages.MissingVisible} missing, {w.Pages.TotalUploads} uploads, {w.Pages.Evictions} evictions" +
                    (w.AnimPages != null ? $"; anim pages {w.AnimPages.ResidentCount}/{w.AnimPages.Layers}, {w.AnimPages.MissingVisible} missing, {w.AnimPages.TotalUploads} uploads, {w.AnimPages.Evictions} evictions" : "") +
                    $"; chunks ready {w.Chunks.ReadyChunks} pending {w.Chunks.PendingChunks}; settled {w.Settled}");
            }

            return ok;
        }

        private uint _nextStatsLog;

        public void Dispose()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
