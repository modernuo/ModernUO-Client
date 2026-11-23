// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// fade_time_based (mount-fix): <see cref="GameScene.FadeSteps"/> catches up the fade steps (Constants.ALPHA_TIME each)
    /// that passed since the last frame in every pacing mode, not only when the draw is display-paced, so a full fade
    /// (11 steps) takes the same time at any frame rate. The steps stay on the 20 ms grid, so the visual-due wake
    /// (_alphaMoved -> VisualDue.At(_alphaTimer)) still names the next step. Off: unpaced frames take one step at most.
    /// Unit tests (DisplayRateTests) compare the fade duration across simulated frame rates in both states.
    /// </summary>
    internal static class FadeTiming
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_FADE_TIME_BASED") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
#else
        private static readonly bool? _env = null;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool TimeBased => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.FadeTimeBased ?? true);
    }
}
