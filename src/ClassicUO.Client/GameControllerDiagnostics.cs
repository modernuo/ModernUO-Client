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

        // driver `winfo <tag>`: the window, back buffer and UI target sizes and where three window points land in UI space
        // (Mouse.Update's mapping), for the borderless offset repro
        internal string WindowInfoLine()
        {
            SDL_GetWindowSize(Window.Handle, out int w, out int h);
            SDL_GetWindowSizeInPixels(Window.Handle, out int pw, out int ph);
            SDL_GetWindowPosition(Window.Handle, out int wx, out int wy);
            SDL_GetWindowBordersSize(Window.Handle, out int bt, out int bl, out int bb, out int br);
            uint display = SDL_GetDisplayForWindow(Window.Handle);
            var mode = (SDL_DisplayMode*)SDL_GetCurrentDisplayMode(display);
            SDL_GetDisplayUsableBounds(display, out SDL_Rect usable);
            SDL_GetDisplayBounds(display, out SDL_Rect bounds);
            var flags = (SDL_WindowFlags)SDL_GetWindowFlags(Window.Handle);
            var pp = GraphicsDevice.PresentationParameters;
            var cb = Window.ClientBounds;
            var ui = _renderTargets.UiRenderTarget;

            return $"window={w}x{h} at {wx},{wy} pixels={pw}x{ph} borders t{bt} l{bl} b{bb} r{br} borderless={(flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) != 0} maximized={(flags & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0} fullscreen={(flags & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0} focus={(flags & SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS) != 0} "
                + $"fna_client_bounds={cb.X},{cb.Y} {cb.Width}x{cb.Height} preferred_bb={GraphicManager.PreferredBackBufferWidth}x{GraphicManager.PreferredBackBufferHeight} pp_bb={pp.BackBufferWidth}x{pp.BackBufferHeight} "
                + $"ui_target={ui?.Width}x{ui?.Height} game_client_bounds={ClientBounds.Width}x{ClientBounds.Height} dpi_scale={DpiScale} display_scale={SDL_GetWindowDisplayScale(Window.Handle)} "
                + $"mode={(mode == null ? 0 : mode->w)}x{(mode == null ? 0 : mode->h)} display_bounds={bounds.x},{bounds.y} {bounds.w}x{bounds.h} usable={usable.x},{usable.y} {usable.w}x{usable.h} "
                + $"map(0,0)={Input.Mouse.MapToUi(0, 0)} map(mid)={Input.Mouse.MapToUi(w / 2, h / 2)} map(end)={Input.Mouse.MapToUi(w - 1, h - 1)}";
        }
    }
}
#endif
