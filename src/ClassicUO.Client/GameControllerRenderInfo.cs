// SPDX-License-Identifier: BSD-2-Clause
using ClassicUO.Configuration;
using ClassicUO.Loop;
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
        // frame pacing: the present mode, refresh, frame limit, loop mode and present-on-change state (a jitter report)
        private PresentModeKind _riPresent;
        private double _riHz;
        private int _riFps;
        private LoopMode _riLoop;
        private bool _riCap, _riMotionFast;

        private void RenderInfoCheck()
        {
            // gpu_force_point: the Gpu world samples with the point sampler at every scale and skips the sharp-bilinear
            // branch (Uo.Render Renderer.ForcePoint); a diagnostic for tile grid lines, read every update
            bool forcePoint = Settings.GlobalSettings.GpuForcePoint;

            // the world pass is null between a drop and its recreation in the next Draw (DevBench reinit, a failed facet switch)
            if (_gpuWorld?.World != null)
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
            int fps = Settings.GlobalSettings.FPS;
            bool motionFast = Settings.GlobalSettings.MotionAboveDisplayRate;

            if (_renderInfoLogged && winW == _riWinW && winH == _riWinH && pixW == _riPixW && pixH == _riPixH && pp.BackBufferWidth == _riBackW &&
                pp.BackBufferHeight == _riBackH && targetW == _riTargetW && targetH == _riTargetH && displayScale == _riDisplayScale &&
                screenScale == _riScreenScale && zoom == _riZoom && gpuScale == _riGpuScale && forcePoint == _riForcePoint &&
                _presentMode == _riPresent && _displayHz == _riHz && fps == _riFps && _tickMode == _riLoop && _displayCapActive == _riCap &&
                motionFast == _riMotionFast)
            {
                return;
            }

            _renderInfoLogged = true;
            (_riWinW, _riWinH, _riPixW, _riPixH, _riBackW, _riBackH, _riTargetW, _riTargetH) = (winW, winH, pixW, pixH, pp.BackBufferWidth, pp.BackBufferHeight, targetW, targetH);
            (_riDisplayScale, _riScreenScale, _riZoom, _riGpuScale, _riForcePoint) = (displayScale, screenScale, zoom, gpuScale, forcePoint);
            (_riPresent, _riHz, _riFps, _riLoop, _riCap, _riMotionFast) = (_presentMode, _displayHz, fps, _tickMode, _displayCapActive, motionFast);

            // the Gpu world's pixel path (Uo.Render WorldRenderer/Renderer): scale < 0.999 trilinear (SampleTile for flat
            // tiles), else sharp-bilinear (texel-centre snap, point-sampled coverage for flat tiles); force_point: point sampler
            string path = _gpuWorld == null ? "n/a (legacy world)" : gpuScale < 0.999f ? "linear" : forcePoint ? "point (gpu_force_point)" : "sharp";

            Log.Info(
                $"[render-info] renderer={RendererStartup.Summary} world_renderer_setting={Settings.GlobalSettings.WorldRendererSetting} " +
                $"window={winW}x{winH} window_pixels={pixW}x{pixH} backbuffer={pp.BackBufferWidth}x{pp.BackBufferHeight} world_target={targetW}x{targetH} " +
                $"display_scale={displayScale:0.###} screen_scale={screenScale:0.###} high_dpi={CUOEnviroment.IsHighDPI} camera_zoom={zoom:0.###} " +
                $"gpu_world_scale={gpuScale:0.####} gpu_path={path} " +
                $"present={_presentMode} vsync={GraphicManager.SynchronizeWithVerticalRetrace} refresh_hz={_displayHz:0.###} fps_limit={fps} loop={_tickMode} " +
                $"present_at_display_rate={Settings.GlobalSettings.PresentAtDisplayRate} present_cap_active={_displayCapActive} " +
                $"vsync_present_on_change={Settings.GlobalSettings.VsyncPresentOnChange} motion_above_display_rate={motionFast}");
        }
    }
}
