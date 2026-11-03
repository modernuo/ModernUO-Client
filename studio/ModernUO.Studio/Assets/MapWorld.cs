extern alias sdl3core;

using ClassicUO.Renderer;
using Microsoft.Xna.Framework.Graphics;
using Uo.Render;
using GpuTextureFormat = sdl3core::SDL3.SDL.SDL_GPUTextureFormat;
using SdlColor = sdl3core::SDL3.SDL.SDL_FColor;

namespace ModernUO.Studio.Assets
{
    /// <summary>
    /// The client's GPU world renderer (poc/Uo.Render) on FNA3D's SDL_GPU device, drawing one facet's land and statics
    /// into a render target the map panel shows. Studio has no game objects, so this uses Uo.Render directly rather than
    /// the client's GpuWorldRenderer, whose job is capturing items, mobiles and effects.
    /// </summary>
    internal sealed class MapWorld : IDisposable
    {
        private const GpuTextureFormat TargetFormat = GpuTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;

        private readonly GraphicsDevice _device;
        private readonly PackSet _basePack;
        private PackSet _pack;
        private WorldRenderer _world;
        private RenderTarget2D _target;

        private MapWorld(GraphicsDevice device, IntPtr gpu, PackSet pack)
        {
            _device = device;
            _basePack = pack;
            _pack = pack;

            var options = new WorldOptions
            {
                // Studio draws no mobiles or items: a small dynamic range; the pages and instances grow on demand
                MaxDynamic = 1024,
                OverrideSlots = 4096,
                Layers = GrowthPolicy.ArtLayersStart,
                LayersMax = GrowthPolicy.ArtLayersMax,
                AnimLayers = GrowthPolicy.AnimLayersStart,
                AnimLayersMax = GrowthPolicy.AnimLayersMax,
                InstanceCapacity = GrowthPolicy.InstancesStart,
                InstanceCapacityMax = GrowthPolicy.InstancesMax
            };

            _world = WorldRenderer.CreateExternal(gpu, TargetFormat, _pack, options);
            _world.ClearColor = new SdlColor { r = 0.06f, g = 0.06f, b = 0.07f, a = 1f };
        }

        public int Facet => _pack.Facet;
        public RenderTarget2D Target => _target;
        public bool VisibleReady => _world.VisibleReady;
        public int Chunks => _world.ChunkCount;

        /// <summary>Chunks in view that are not drawable this frame: what a re-bake must never raise (smoke tests).</summary>
        public int MissingVisible => _world.Chunks.MissingVisible;
        public long Instances => _world.Instances;

        /// <summary>Null with <paramref name="error"/> when FNA3D is not on the SDL_GPU driver or the pack has no maps.</summary>
        public static MapWorld TryCreate(GraphicsDevice device, StudioAssets assets, out string error)
        {
            error = null;
            IntPtr gpu = GpuInterop.GetDevice(device);

            if (gpu == IntPtr.Zero)
            {
                error = $"the map needs FNA3D on the SDL_GPU driver (this run uses {GpuInterop.GetFna3dDriverName(device)})";

                return null;
            }

            if (assets.Manifest.Facets.Count == 0)
            {
                error = "the pack has no maps";

                return null;
            }

            var files = assets.Files;

            try
            {
                // the loaders' tables, shared with the renderer as the client does (GpuWorldRenderer.TryCreate); no circle of
                // transparency in Studio, so no static is exempt from it
                var tables = new PackTables
                {
                    Sprites = files.Arts.PackSpriteEntries,
                    Texmaps = files.Texmaps.PackSpriteEntries,
                    HueCount = files.Hues.PackHueCount,
                    Hues = files.Hues.PackHues,
                    AnimBodies = files.Animations.PackBodies,
                    AnimDirs = files.Animations.PackDirs,
                    AnimFrames = files.Animations.PackFrames,
                    FramePalette = files.Animations.PackFramePalette,
                    StaticNoCot = new bool[0x10000]
                };

                int facet = assets.Manifest.Facets[0].Index;
                var pack = new PackSet(assets.Root, facet, null, tables);

                return new MapWorld(device, gpu, pack);
            }
            catch (NotSupportedException ex) when (ex.Message.Contains("shader formats"))
            {
                // Uo.Render compiles SPIR-V only when the official DXC release is found (DxcSpirv in Uo.Render.csproj);
                // without it a Vulkan device has no shaders
                error = "Uo.Render was built without SPIR-V (Vulkan) shaders. Install the official DXC release from "
                    + "github.com/microsoft/DirectXShaderCompiler and build with -p:DxcSpirv=<path to its bin/x64/dxc.exe>";
                StudioLog.Error($"Map renderer: {ex.Message}");

                return null;
            }
            catch (Exception ex)
            {
                error = $"the map renderer could not start: {ex.Message}";
                StudioLog.Error($"Map renderer: {ex}");

                return null;
            }
        }

        public void SetFacet(int facet)
        {
            if (facet == _pack.Facet)
            {
                return;
            }

            // a facet change keeps the device resources and the resident pages and re-streams the chunks (as the client)
            _pack = _basePack.WithFacet(facet);
            _world.ChangeFacet(_pack);
        }

        /// <summary>Makes the target <paramref name="width"/> x <paramref name="height"/>; false while either is zero.</summary>
        public bool EnsureTarget(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            if (_target == null || _target.IsDisposed || _target.Width != width || _target.Height != height)
            {
                _target?.Dispose();
                _target = new RenderTarget2D(_device, width, height, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }

            return true;
        }

        /// <summary>
        /// Draws <paramref name="edits"/> live: the renderer reads edited regions from memory (ChunkStore.RegionOverride)
        /// and re-bakes the blocks each edit, undo or redo touches.
        /// </summary>
        public void AttachEdits(MapEdits edits)
        {
            _world.Chunks.RegionOverride = (rx, ry) => edits.RegionBytes(_pack.Facet, rx, ry);
            edits.BlocksChanged += (facet, bx0, by0, bx1, by1) =>
            {
                if (facet == _pack.Facet)
                {
                    _world.Chunks.InvalidateBlocks(bx0, by0, bx1, by1);
                }
            };
        }

        public void HighlightLand(int x, int y, ushort hue) => _world.SetLandOverride(x, y, -1, false, hue);

        /// <summary>Tints a static this frame; <paramref name="alpha"/> below 255 also makes it translucent (a preview).</summary>
        public void HighlightStatic(int x, int y, int z, ushort graphic, ushort hue, int alpha = -1) => _world.SetStaticOverride(x, y, z, graphic, alpha, false, hue);

        /// <summary>
        /// Streams around the camera and draws into the target. Call after ImGui built its frame and before FNA submits the
        /// draws that sample the target: Uo.Render submits its own command buffer first, as the client's world pass does.
        /// </summary>
        public bool Render(in WorldCamera camera, int maxZ, bool hideRoofs)
        {
            IntPtr texture = _target == null || _target.IsDisposed ? IntPtr.Zero : GpuInterop.GetTexture(_target);

            if (texture == IntPtr.Zero)
            {
                return false;
            }

            _world.Prepare(in camera);

            if (maxZ < 127 || hideRoofs)
            {
                _world.SetZHiding(maxZ, maxZ, hideRoofs);
            }

            _world.DynamicCount = 0;
            bool ok = _world.Render(texture, (uint)_target.Width, (uint)_target.Height, TargetFormat, false);

            if (_world.Renderer.SubmitFailures > 0)
            {
                StudioLog.Error($"Map renderer: GPU submit failed: {_world.Renderer.LastSubmitError}");
            }

            return ok;
        }

        public void Dispose()
        {
            _world?.Dispose();
            _world = null;
            _target?.Dispose();
            _target = null;
        }
    }
}
