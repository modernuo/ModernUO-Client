// SPDX-License-Identifier: BSD-2-Clause
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Graphics;
using static SDL3.SDL;

namespace ClassicUO
{
    // [render-info]: what the world is drawn with and at which scale, logged at startup and whenever a value changes, in every
    // build, so a rendering report (tile grid lines, blur) comes with the renderer, device, DPI, sizes and the Gpu world's
    // sampling path. Checked every RENDER_INFO_INTERVAL updates; no allocation unless a value changed.
    internal unsafe partial class GameController
    {
        private const int RENDER_INFO_INTERVAL = 30;

        private int _renderInfoCountdown;
        private bool _renderInfoLogged;
        private int _riWinW, _riWinH, _riPixW, _riPixH, _riBackW, _riBackH, _riTargetW, _riTargetH;
        private float _riDisplayScale, _riScreenScale, _riZoom, _riGpuScale;
        private bool _riForcePoint;

        private void RenderInfoCheck()
        {
            // gpu_force_point: the Gpu world samples with the point sampler at every scale and skips the sharp-bilinear
            // branch (Uo.Render Renderer.ForcePoint); a diagnostic for tile grid lines, read every update
            bool forcePoint = Settings.GlobalSettings.GpuForcePoint;

            if (_gpuWorld != null)
            {
                _gpuWorld.World.Renderer.ForcePoint = forcePoint;
            }

            if (--_renderInfoCountdown > 0)
            {
                return;
            }

            _renderInfoCountdown = RENDER_INFO_INTERVAL;

            SDL_GetWindowSize(Window.Handle, out int winW, out int winH);
            SDL_GetWindowSizeInPixels(Window.Handle, out int pixW, out int pixH);
            var pp = GraphicsDevice.PresentationParameters;
            RenderTarget2D target = _renderTargets.WorldRenderTarget;
            int targetW = target?.Width ?? 0, targetH = target?.Height ?? 0;
            float displayScale = SDL_GetWindowDisplayScale(Window.Handle);
            float screenScale = ScreenScale;
            float zoom = Scene?.Camera.Zoom ?? 0f;
            float gpuScale = _gpuWorld != null && zoom > 0f ? 1f / zoom : 0f;

            if (_renderInfoLogged && winW == _riWinW && winH == _riWinH && pixW == _riPixW && pixH == _riPixH && pp.BackBufferWidth == _riBackW &&
                pp.BackBufferHeight == _riBackH && targetW == _riTargetW && targetH == _riTargetH && displayScale == _riDisplayScale &&
                screenScale == _riScreenScale && zoom == _riZoom && gpuScale == _riGpuScale && forcePoint == _riForcePoint)
            {
                return;
            }

            _renderInfoLogged = true;
            (_riWinW, _riWinH, _riPixW, _riPixH, _riBackW, _riBackH, _riTargetW, _riTargetH) = (winW, winH, pixW, pixH, pp.BackBufferWidth, pp.BackBufferHeight, targetW, targetH);
            (_riDisplayScale, _riScreenScale, _riZoom, _riGpuScale, _riForcePoint) = (displayScale, screenScale, zoom, gpuScale, forcePoint);

            // the Gpu world's pixel path (Uo.Render WorldRenderer/Renderer): scale < 0.999 trilinear (SampleTile for flat
            // tiles), else sharp-bilinear (texel-centre snap, point-sampled coverage for flat tiles); force_point: point sampler
            string path = _gpuWorld == null ? "n/a (legacy world)" : gpuScale < 0.999f ? "linear" : forcePoint ? "point (gpu_force_point)" : "sharp";

            Log.Info(
                $"[render-info] renderer={RendererStartup.Summary} world_renderer_setting={Settings.GlobalSettings.WorldRendererSetting} " +
                $"window={winW}x{winH} window_pixels={pixW}x{pixH} backbuffer={pp.BackBufferWidth}x{pp.BackBufferHeight} world_target={targetW}x{targetH} " +
                $"display_scale={displayScale:0.###} screen_scale={screenScale:0.###} high_dpi={CUOEnviroment.IsHighDPI} camera_zoom={zoom:0.###} " +
                $"gpu_world_scale={gpuScale:0.####} gpu_path={path}");
        }
    }
}
