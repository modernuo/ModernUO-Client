using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;


namespace ClassicUO.Renderer
{
    public class RenderTargets
    {
        private RenderTarget2D _uiRenderTarget;
        private RenderTarget2D _lightRenderTarget;
        private RenderTarget2D _worldRenderTarget;
        private RenderTarget2D _uiCacheRenderTarget;

        private RenderGeometry _geometry;

        private Func<Vector3> _lightsHue;
        private Func<BlendState> _lightsBlendState;

        private Texture2D _background;
        private SamplerState _defaultSamplerState;

        public RenderTarget2D UiRenderTarget { get => _uiRenderTarget; }
        public RenderTarget2D LightRenderTarget { get => _lightRenderTarget; }
        public RenderTarget2D WorldRenderTarget { get => _worldRenderTarget; }

        /// <summary>Where the world and light targets are drawn in the back buffer (window pixels).</summary>
        public Rectangle WorldSceneOnScreen => _geometry.WorldOnScreen;

        /// <summary>B8: the scene UI and gumps without the cursor, kept between frames (null until the first <see cref="EnsureSizes"/>).</summary>
        public RenderTarget2D UiCacheRenderTarget { get => _uiCacheRenderTarget; }

        /// <summary>Changes whenever the UI target or the UI cache target is (re)created: their old pixels are gone.</summary>
        public int UiTargetsVersion { get; private set; }

        // The GPU world pass draws into the world target outside FNA: FNA must load it instead of clearing it on bind.
        public bool PreserveWorldContents { get; set; }

        // The world area's opacity over black (1 = normal): 0 while there is no world frame to show yet (first entry),
        // then a short fade-in. Covers the world and its lights, not the UI.
        public float WorldFade { get; set; } = 1f;

        public void SetLightsConfiguration(Func<BlendState> lightsBlendState, Func<Vector3> lightsHue)
        {
            _lightsBlendState = lightsBlendState;
            _lightsHue = lightsHue;
        }

        /// <summary>The geometry the targets were sized for, and the composite draws with.</summary>
        public RenderGeometry Geometry => _geometry;

        public void EnsureSizes(GraphicsDevice graphicsDevice, in RenderGeometry geometry)
        {
            _geometry = geometry;

            RenderTarget2D ui = _uiRenderTarget, uiCache = _uiCacheRenderTarget;
            EnsureSize(graphicsDevice, ref _uiRenderTarget, geometry.UiWidth, geometry.UiHeight, RenderTargetUsage.PreserveContents);
            // B8, the UI render cache: the UI target keeps its contents between frames and a second UI-sized target holds the
            // UI without the cursor
            EnsureSize(graphicsDevice, ref _uiCacheRenderTarget, geometry.UiWidth, geometry.UiHeight, RenderTargetUsage.PreserveContents);

            if (ui != _uiRenderTarget || uiCache != _uiCacheRenderTarget)
            {
                UiTargetsVersion++;
            }

            EnsureSize(graphicsDevice, ref _lightRenderTarget, geometry.WorldWidth, geometry.WorldHeight);
            EnsureSize(graphicsDevice, ref _worldRenderTarget, geometry.WorldWidth, geometry.WorldHeight,
                PreserveWorldContents ? RenderTargetUsage.PreserveContents : (RenderTargetUsage?)null);

            // point sampling keeps an integer scale sharp; a fractional one is filtered
            _defaultSamplerState = geometry.IntegerScale ? SamplerState.PointClamp : SamplerState.LinearClamp;
        }

        private static void EnsureSize(GraphicsDevice graphicsDevice, ref RenderTarget2D renderTarget, int width, int height, RenderTargetUsage? usage = null)
        {
            if (width <= 0 || height <= 0)
                return;

            PresentationParameters pp = graphicsDevice.PresentationParameters;
            RenderTargetUsage targetUsage = usage ?? pp.RenderTargetUsage;

            if (renderTarget == null || renderTarget.IsDisposed || renderTarget.Width != width || renderTarget.Height != height
                || renderTarget.RenderTargetUsage != targetUsage)
            {
                renderTarget?.Dispose();

                renderTarget = new RenderTarget2D(
                    graphicsDevice,
                    width,
                    height,
                    false,
                    pp.BackBufferFormat,
                    pp.DepthStencilFormat,
                    pp.MultiSampleCount,
                    targetUsage
                    );
            }
        }

        public void InitializeBackground(Texture2D background)
        {
            _background = background ?? throw new ArgumentNullException(nameof(background));
        }

        public void Draw(UltimaBatcher2D batcher)
        {
            // draw world
            Vector3 fullAlphaNoColor = Vector3.UnitZ;

            batcher.Begin();
            batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            Rectangle rect = _geometry.BackBufferBounds;
            Rectangle world = _geometry.WorldOnScreen;
            batcher.DrawTiled(
                _background,
                rect,
                _background.Bounds,
                new Vector3(0, 0, 0.1f),
                0f
            );

            batcher.SetSampler(_defaultSamplerState);

            batcher.Draw(
                WorldRenderTarget,
                world,
                fullAlphaNoColor,
                0f
            );

            // draw lights
            batcher.SetBlendState(_lightsBlendState?.Invoke());

            batcher.Draw(
                LightRenderTarget,
                world,
                _lightsHue?.Invoke() ?? Vector3.Up,
                0f
            );

            batcher.SetBlendState(null);

            if (WorldFade < 1f)
            {
                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.Black),
                    world,
                    new Vector3(0, 0, 1f - Math.Max(0f, WorldFade)),
                    0f
                );
            }

            // the UI target covers the back buffer exactly (RenderGeometry's per-axis scales)
            batcher.Draw(
                UiRenderTarget,
                rect,
                fullAlphaNoColor,
                0f
            );
            
            // Reset sampler to default
            batcher.SetSampler(null);
            batcher.End();
        }
    }
}
