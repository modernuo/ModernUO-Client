// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using ClassicUO.Configuration;
using ClassicUO.Renderer;
using static SDL3.SDL;

namespace ClassicUO
{
    internal unsafe partial class GameController
    {
        private bool _diagStarted;
        private void DiagDisplay()
        {
            if (!_diagStarted)
            {
                Diagnostics.Start(Diagnostics.RuntimeHeader() + DiagDisplayLine());
                _diagStarted = true;
            }
            if (Diagnostics.NeedsDisplay) Diagnostics.Display(DiagDisplayLine());
        }
        private string DiagDisplayLine()
        {
            SDL_GetWindowSize(Window.Handle, out int w, out int h);
            SDL_GetWindowSizeInPixels(Window.Handle, out int pw, out int ph);
            var mode = (SDL_DisplayMode*)SDL_GetCurrentDisplayMode(SDL_GetDisplayForWindow(Window.Handle));
            var pp = GraphicsDevice.PresentationParameters;
            string present = "unavailable (non SDL_GPU backend)";
            if (_gpuDevice != IntPtr.Zero)
            {
                // SDL has no public effective-mode getter. Infer using the committed FNA3D interval conversion and
                // runtime capability query, explicitly marked inferred rather than pretending to inspect native state.
                bool immediate = pp.PresentationInterval == Microsoft.Xna.Framework.Graphics.PresentInterval.Immediate;
                var requested = immediate ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE :
                    SDL_GetHintBoolean("FNA3D_VULKAN_FORCE_MAILBOX_VSYNC", false) ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX : SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
                var effective = SDL_WindowSupportsGPUPresentMode(_gpuDevice, Window.Handle, requested) ? requested : SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
                present = $"requested={requested} effective_inferred={effective}";
            }
            return $"display renderer={RendererStartup.Summary} world={Settings.GlobalSettings.WorldRenderer} FNA3D={GpuInterop.GetFna3dDriverName(GraphicsDevice)} SDL_GPU={GpuInterop.GetDriverName(_gpuDevice)} present={present} interval={pp.PresentationInterval} vsync={Settings.GlobalSettings.VSync} fps_limit={Settings.GlobalSettings.FPS} event_loop={Settings.GlobalSettings.EventLoop} focus={IsActive} window={w}x{h} window_pixels={pw}x{ph} backbuffer={pp.BackBufferWidth}x{pp.BackBufferHeight} world_target={_renderTargets.WorldRenderTarget?.Width}x{_renderTargets.WorldRenderTarget?.Height} display_scale={SDL_GetWindowDisplayScale(Window.Handle)} dpi_scale={DpiScale} refresh_hz={(mode == null ? 0 : mode->refresh_rate)}";
        }
    }
}
#endif
