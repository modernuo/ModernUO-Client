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

        // ui_border_bake (G11a; always on since quiet-1123): the composite copies a baked border texture instead of clearing
        // the back buffer, drawing the background pattern over the whole window and blending the whole UI target. The border
        // holds black, the pattern and, outside the world viewport, the UI target over them: those pixels change only with
        // the UI target or the geometry, so they are baked on such a change (BorderBakeState) and copied opaque each frame.
        // Then the world, lights and fade go into the viewport, and the UI target is blended only inside the viewport (a
        // scissor, so each pixel gets the same blend as the full composite). FNA still clears the back buffer when it is bound
        // (its RenderTargetUsage is DiscardContents). Costs one back-buffer-sized target (8 MB at 1080p, 33 MB at 4K).

        /// <summary>ui_border_bake: where the border texture is stale. The caller reports UI target changes here.</summary>
        public UiBorderBake BorderBakeState { get; } = new();

        /// <summary>ui_border_bake: the baked border (back-buffer sized; null until the first baked composite).</summary>
        public RenderTarget2D BorderRenderTarget => _borderRenderTarget;

        private RenderTarget2D _borderRenderTarget;

        private static readonly Vector3 BackgroundHue = new Vector3(0, 0, 0.1f);

        /// <summary>The composite into the back buffer, which must be bound.</summary>
        public void Draw(UltimaBatcher2D batcher)
        {
            // the full composite when there is nothing to bake into (a zero-sized back buffer, no UI target)
            if (EnsureBorder(batcher, null))
            {
                DrawBaked(batcher);
            }
            else
            {
                DrawFull(batcher);
            }
        }

        private void DrawFull(UltimaBatcher2D batcher)
        {
            batcher.Begin();
            batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            Rectangle rect = _geometry.BackBufferBounds;
            batcher.DrawTiled(
                _background,
                rect,
                _background.Bounds,
                BackgroundHue,
                0f
            );

            batcher.SetSampler(_defaultSamplerState);

            DrawWorldLayers(batcher);

            // the UI target covers the back buffer exactly (RenderGeometry's per-axis scales)
            batcher.Draw(
                UiRenderTarget,
                rect,
                Vector3.UnitZ,
                0f
            );

            // Reset sampler to default
            batcher.SetSampler(null);
            batcher.End();
        }

        private void DrawBaked(UltimaBatcher2D batcher)
        {
            Rectangle rect = _geometry.BackBufferBounds;

            batcher.Begin();
            batcher.SetBlendState(BlendState.Opaque);
            batcher.SetSampler(SamplerState.PointClamp);
            batcher.Draw(_borderRenderTarget, new Rectangle(0, 0, _borderRenderTarget.Width, _borderRenderTarget.Height), Vector3.UnitZ, 0f);
            batcher.SetBlendState(null);
            batcher.SetSampler(_defaultSamplerState);

            DrawWorldLayers(batcher);

            Rectangle world = UiBorderBake.Intersect(_geometry.WorldOnScreen, rect);

            if (world.Width > 0 && world.Height > 0 && batcher.ClipBegin(world.X, world.Y, world.Width, world.Height))
            {
                batcher.Draw(UiRenderTarget, rect, Vector3.UnitZ, 0f);
                batcher.ClipEnd();
            }

            batcher.SetSampler(null);
            batcher.End();
        }

        private void DrawWorldLayers(UltimaBatcher2D batcher)
        {
            Rectangle world = _geometry.WorldOnScreen;

            batcher.Draw(
                WorldRenderTarget,
                world,
                Vector3.UnitZ,
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
        }

        // ui_border_bake: brings the border texture up to date and binds target again. False when there is nothing to bake
        // into (a zero-sized back buffer, no UI target): the full composite is drawn instead.
        private bool EnsureBorder(UltimaBatcher2D batcher, RenderTarget2D target)
        {
            RenderTarget2D ui = _uiRenderTarget;
            GraphicsDevice device = batcher.GraphicsDevice;

            if (ui == null || ui.IsDisposed || _background == null || _geometry.BackBufferWidth <= 0 || _geometry.BackBufferHeight <= 0)
            {
                return false;
            }

            EnsureSize(device, ref _borderRenderTarget, _geometry.BackBufferWidth, _geometry.BackBufferHeight, RenderTargetUsage.PreserveContents);

            if (_borderRenderTarget == null || _borderRenderTarget.IsDisposed)
            {
                return false;
            }

            BorderBakeKind kind = BorderBakeState.Next(_geometry, UiTargetsVersion, _borderRenderTarget, _background, out Rectangle region);

            if (kind != BorderBakeKind.None)
            {
                device.SetRenderTarget(_borderRenderTarget);
                Bake(batcher, region);
                device.SetRenderTarget(target);
            }

            return true;
        }

        // Bakes region (back-buffer pixels) of the border with the draws the full composite makes there, each clipped to
        // region: black, the background tiles, then the UI target clipped to the border rects outside the viewport.
        private void Bake(UltimaBatcher2D batcher, Rectangle region)
        {
            Rectangle rect = _geometry.BackBufferBounds;

            batcher.Begin();

            if (batcher.ClipBegin(region.X, region.Y, region.Width, region.Height))
            {
                batcher.SetBlendState(BlendState.Opaque);
                batcher.Draw(SolidColorTextureCache.GetTexture(Color.Black), region, Vector3.UnitZ, 0f);
                batcher.SetBlendState(null);
                DrawBackgroundTiles(batcher, rect, region);
                batcher.ClipEnd();
            }

            batcher.SetSampler(_defaultSamplerState);

            Span<Rectangle> border = stackalloc Rectangle[4];
            int n = UiBorderBake.BorderRects(rect, _geometry.WorldOnScreen, border);

            for (int i = 0; i < n; i++)
            {
                Rectangle r = UiBorderBake.Intersect(border[i], region);

                if (r.Width > 0 && r.Height > 0 && batcher.ClipBegin(r.X, r.Y, r.Width, r.Height))
                {
                    batcher.Draw(UiRenderTarget, rect, Vector3.UnitZ, 0f);
                    batcher.ClipEnd();
                }
            }

            batcher.SetSampler(null);
            batcher.End();
        }

        // UltimaBatcher2D.DrawTiled over rect, only the tiles that reach region: the same quads, so the same pixels
        private void DrawBackgroundTiles(UltimaBatcher2D batcher, Rectangle rect, Rectangle region)
        {
            Rectangle source = _background.Bounds;
            int tw = source.Width, th = source.Height;

            if (tw <= 0 || th <= 0)
            {
                return;
            }

            int i0 = Math.Max(0, (region.X - rect.X) / tw), i1 = (region.X + region.Width - 1 - rect.X) / tw;
            int j0 = Math.Max(0, (region.Y - rect.Y) / th), j1 = (region.Y + region.Height - 1 - rect.Y) / th;

            for (int j = j0; j <= j1; j++)
            {
                int h = rect.Height - j * th;

                if (h <= 0)
                {
                    break;
                }

                for (int i = i0; i <= i1; i++)
                {
                    int w = rect.Width - i * tw;

                    if (w <= 0)
                    {
                        break;
                    }

                    batcher.Draw(
                        _background,
                        new Vector2(rect.X + i * tw, rect.Y + j * th),
                        new Rectangle(source.X, source.Y, Math.Min(w, tw), Math.Min(h, th)),
                        BackgroundHue,
                        0f
                    );
                }
            }
        }
    }
}
