// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace ClassicUO.Loop
{
    /// <summary>
    /// The frames/s the player-facing counter shows. Present on change draws only when something changes, so the drawn rate
    /// falls to a few frames/s on an idle screen although the screen refreshes at the display rate and the next change shows
    /// at the next refresh (owner, 2026-11-19). While the display-rate cap is in force the counter shows the display rate (an
    /// fps cap below it: the cap); otherwise the drawn rate. The debug overlay keeps the drawn rate (CurrentRefreshRate) and
    /// adds the possible rate from the last frame build time.
    /// </summary>
    internal static class FpsDisplay
    {
        public static uint Shown(uint drawn, bool displayCapActive, double displayHz, int fpsCap)
        {
            if (!displayCapActive || !double.IsFinite(displayHz) || displayHz <= 0)
            {
                return drawn;
            }

            uint hz = (uint)Math.Round(displayHz);

            return fpsCap > 0 && fpsCap < hz ? (uint)fpsCap : hz;
        }

        /// <summary>The frames/s the last frame's build time would allow (0 when unknown).</summary>
        public static uint Possible(double frameBuildMs)
            => double.IsFinite(frameBuildMs) && frameBuildMs > 0 ? (uint)Math.Min(uint.MaxValue, Math.Round(1000.0 / frameBuildMs)) : 0;
    }
}
