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
            // the mode in force: SDL has no getter for it, so RefreshPresentMode repeats FNA3D's choice on the inputs FNA3D read
            // at the last device creation or reset (the hint and the window's supported modes); D3D11/OpenGL: Vsync or Immediate
            return $"display renderer={RendererStartup.Summary} world={Settings.GlobalSettings.WorldRenderer} FNA3D={GpuInterop.GetFna3dDriverName(GraphicsDevice)} SDL_GPU={GpuInterop.GetDriverName(_gpuDevice)} present={_presentMode} interval={pp.PresentationInterval} vsync={Settings.GlobalSettings.VSync} fps_limit={Settings.GlobalSettings.FPS} loop={_tickMode} present_at_display_rate={Settings.GlobalSettings.PresentAtDisplayRate} present_on_change={_presentOnChange} vsync_present_on_change={Settings.GlobalSettings.VsyncPresentOnChange} vsync_mailbox={Settings.GlobalSettings.VsyncMailbox} event_loop_idle_wait={Settings.GlobalSettings.EventLoopIdleWait} present_cap_active={_displayCapActive} focus={IsActive} window={w}x{h} window_pixels={pw}x{ph} backbuffer={pp.BackBufferWidth}x{pp.BackBufferHeight} world_target={_renderTargets.WorldRenderTarget?.Width}x{_renderTargets.WorldRenderTarget?.Height} display_scale={SDL_GetWindowDisplayScale(Window.Handle)} dpi_scale={DpiScale} refresh_hz={(mode == null ? 0 : mode->refresh_rate)}";
        }
    }
}
#endif
