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

        private Rectangle _gameWindowOnScreen;
        private Rectangle _gameWindowAfterDPI;
        private Rectangle _gameWorldSceneOnScreen;
        private Rectangle _gameWorldSceneAfterDPI;

        private Func<Vector3> _lightsHue;
        private Func<BlendState> _lightsBlendState;

        private Texture2D _background;
        private SamplerState _defaultSamplerState;

        public RenderTarget2D UiRenderTarget { get => _uiRenderTarget; }
        public RenderTarget2D LightRenderTarget { get => _lightRenderTarget; }
        public RenderTarget2D WorldRenderTarget { get => _worldRenderTarget; }

        /// <summary>B8: the scene UI and gumps without the cursor, kept between frames (null while <see cref="UiCache"/> is off).</summary>
        public RenderTarget2D UiCacheRenderTarget { get => _uiCacheRenderTarget; }

        /// <summary>B8, the UI render cache: the UI target keeps its contents between frames and a second UI-sized target holds
        /// the UI without the cursor. Set before <see cref="EnsureSizes"/>.</summary>
        public bool UiCache { get; set; }

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

        public void EnsureSizes(GraphicsDevice graphicsDevice, Rectangle gameWindowOnScreen, Rectangle gameWorldSceneAfterDPI, float dpiScale)
        {
            _gameWindowOnScreen = gameWindowOnScreen;
            _gameWindowAfterDPI = ScaleRectangle(gameWindowOnScreen, dpiScale);
            _gameWorldSceneOnScreen = ScaleRectangle(gameWorldSceneAfterDPI, 1/dpiScale);
            _gameWorldSceneAfterDPI = gameWorldSceneAfterDPI;

            RenderTarget2D ui = _uiRenderTarget, uiCache = _uiCacheRenderTarget;
            EnsureSize(graphicsDevice, ref _uiRenderTarget, _gameWindowAfterDPI.Width, _gameWindowAfterDPI.Height,
                UiCache ? RenderTargetUsage.PreserveContents : (RenderTargetUsage?)null);

            if (UiCache)
            {
                EnsureSize(graphicsDevice, ref _uiCacheRenderTarget, _gameWindowAfterDPI.Width, _gameWindowAfterDPI.Height,
                    RenderTargetUsage.PreserveContents);
            }
            else if (_uiCacheRenderTarget != null)
            {
                _uiCacheRenderTarget.Dispose();
                _uiCacheRenderTarget = null;
            }

            if (ui != _uiRenderTarget || uiCache != _uiCacheRenderTarget)
            {
                UiTargetsVersion++;
            }
            EnsureSize(graphicsDevice, ref _lightRenderTarget, _gameWorldSceneAfterDPI.Width, _gameWorldSceneAfterDPI.Height);
            EnsureSize(graphicsDevice, ref _worldRenderTarget, _gameWorldSceneAfterDPI.Width, _gameWorldSceneAfterDPI.Height,
                PreserveWorldContents ? RenderTargetUsage.PreserveContents : (RenderTargetUsage?)null);

            if (dpiScale == Math.Floor(dpiScale))
            {
                // Use PointClamp for integer DPI scaling to avoid blurriness
                _defaultSamplerState = SamplerState.PointClamp;
            }
            else
            {
                // Use LinearClamp for non-integer DPI scaling for smoother results
                _defaultSamplerState = SamplerState.LinearClamp;
            }
        }

        private static Rectangle ScaleRectangle(Rectangle gameWindowOnScreen, float dpiScale) => new(
                (int)(gameWindowOnScreen.X / dpiScale),
                (int)(gameWindowOnScreen.Y / dpiScale),
                (int)(gameWindowOnScreen.Width / dpiScale),
                (int)(gameWindowOnScreen.Height / dpiScale)
            );

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

            var rect = new Rectangle(
                0,
                0,
                _gameWindowOnScreen.Width,
                _gameWindowOnScreen.Height
            );
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
                _gameWorldSceneOnScreen,
                fullAlphaNoColor,
                0f
            );

            // draw lights
            batcher.SetBlendState(_lightsBlendState?.Invoke());

            batcher.Draw(
                LightRenderTarget,
                _gameWorldSceneOnScreen,
                _lightsHue?.Invoke() ?? Vector3.Up,
                0f
            );

            batcher.SetBlendState(null);

            if (WorldFade < 1f)
            {
                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.Black),
                    _gameWorldSceneOnScreen,
                    new Vector3(0, 0, 1f - Math.Max(0f, WorldFade)),
                    0f
                );
            }

            // Draw UI at original window size (render target is DPI-scaled but destination is not)
            batcher.Draw(
                UiRenderTarget,
                _gameWindowOnScreen,
                fullAlphaNoColor,
                0f
            );
            
            // Reset sampler to default
            batcher.SetSampler(null);
            batcher.End();
        }
    }
}
