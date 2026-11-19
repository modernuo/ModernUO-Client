// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Loop
{
    internal enum PresentModeKind
    {
        /// <summary>Vsync off: no wait for the vblank (tearing possible).</summary>
        Immediate,
        /// <summary>Normal vsync (FIFO): Present queues the frame and blocks once the swapchain is full.</summary>
        Vsync,
        /// <summary>Vsync without the queue: Present does not block, the newest frame replaces a waiting one at each vblank.</summary>
        Mailbox
    }

    /// <summary>
    /// The present mode in force (vsync_mailbox). SDL has no getter for a swapchain's mode, so <see cref="Choose"/> repeats
    /// FNA3D's choice (FNA3D_Driver_SDL.c XNAToSDL_PresentMode) on the same inputs: FNA3D reads <see cref="MailboxHint"/> and
    /// SDL_WindowSupportsGPUPresentMode at device creation and at every backbuffer reset (ApplyChanges, a vsync toggle).
    /// The hint applies to every SDL_GPU backend despite its name. FNA3D's D3D11 and OpenGL drivers have no mailbox.
    /// </summary>
    internal static class PresentModeRule
    {
        public const string MailboxHint = "FNA3D_VULKAN_FORCE_MAILBOX_VSYNC";

        /// <param name="vsync">The presentation interval is One (SynchronizeWithVerticalRetrace).</param>
        /// <param name="sdlGpu">FNA3D runs on its SDL_GPU driver.</param>
        /// <param name="mailboxHint">The hint FNA3D reads (set from vsync_mailbox; an environment variable of the same name wins).</param>
        /// <param name="mailboxSupported">SDL_WindowSupportsGPUPresentMode(MAILBOX) for the window.</param>
        /// <param name="immediateSupported">SDL_WindowSupportsGPUPresentMode(IMMEDIATE) for the window.</param>
        public static PresentModeKind Choose(bool vsync, bool sdlGpu, bool mailboxHint, bool mailboxSupported, bool immediateSupported)
        {
            if (!sdlGpu)
            {
                return vsync ? PresentModeKind.Vsync : PresentModeKind.Immediate;
            }

            if (vsync)
            {
                return mailboxHint && mailboxSupported ? PresentModeKind.Mailbox : PresentModeKind.Vsync;
            }

            return immediateSupported ? PresentModeKind.Immediate : PresentModeKind.Vsync;
        }

        /// <summary>
        /// The Options menu's suffix to the VSync checkbox: players don't know the word "mailbox" (it stays in logs and
        /// diagnostics), so mailbox reads "VSync (True Triple Buffering)", to show that vsync on costs no raw performance; the
        /// normal vsync fallback and vsync off read plain "VSync".
        /// </summary>
        /// <summary>
        /// The fps cap's frame interval (ms) under the present mode: mailbox discards frames the display never shows, so a cap
        /// above the refresh rate only spends CPU and GPU; it is held to the display rate (owner, 2026-11-19). 0 (no cap) and
        /// normal vsync/immediate keep the interval.
        /// </summary>
        public static double CapInterval(double intervalMs, PresentModeKind mode, double displayHz)
            => mode == PresentModeKind.Mailbox && intervalMs > 0 && displayHz > 0 ? Math.Max(intervalMs, 1000.0 / displayHz) : intervalMs;

        public static string OptionsSuffix(PresentModeKind mode) => mode == PresentModeKind.Mailbox ? "(True Triple Buffering)" : string.Empty;
    }
}
