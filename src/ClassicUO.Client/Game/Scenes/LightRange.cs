// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_range (light-edges round 6c, default off; needs chunk_lights). Owner 2026-11-22: zoomed out with chunk_lights and
    /// light_occlusion on, every light in range was drawn (the ranked list grows to FrameLights.SafetyBound) and the light pass
    /// lagged. With the switch on:
    /// a light farther from the player than the range (tiles, Chebyshev, from its screen offset: <see cref="TileDistance"/>) takes
    /// no slot, and over the last <see cref="FadeTiles"/> tiles inside it fades out (<see cref="RangeShare"/>). The range is the
    /// setting light_range_tiles, or with 0 just past the zoom-1 view plus the largest halo's reach (<see cref="AutoRange"/>);
    /// the ranked list keeps at most light_budget lights (its best: in view first, then nearest the player), and when the budget
    /// binds the <see cref="FadeRanks"/> lowest-ranked kept lights fade by rank (<see cref="BudgetShare"/>), so a light crossing
    /// the budget's edge dims instead of popping. No per-light state: both shares are computed every frame from the frame's
    /// positions and ranks.
    /// CUO_LIGHT_RANGE_CHECK=1 checks every light pass against the rule (kept lights inside the range, shares as the rule gives
    /// them, the budget kept the best ranks, the faded ones are the lowest) and, with the switch off, counts what it would drop;
    /// CUO_LIGHT_RANGE=1|0 overrides the setting in developer builds.
    /// </summary>
    internal static class LightRange
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_RANGE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_RANGE_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightRange ?? false);

        /// <summary>The lights kept with no light_budget setting. bench-001041 (light 05, 1920x1080): the Moonglow bank holds
        /// about 90 per pass with chunk_lights, the classic walk offered 132 there at zoom 2.5, Britain's 1495 1629 about 10; the
        /// budget binds only in denser spots than those, well above the classic 100.</summary>
        public const int DefaultBudget = 256;

        /// <summary>The tiles over which a light fades out before the range.</summary>
        public const int FadeTiles = 2;

        /// <summary>The lowest-ranked kept lights that fade when the budget binds.</summary>
        public const int FadeRanks = 16;

        /// <summary>A tile is 44 screen pixels across (dx + dy) for any step: |sx| + |sy| = 44 x the Chebyshev tile distance.</summary>
        public const float TilePixels = 44f;

        /// <summary>The Chebyshev tile distance of a light at (x, y) from the player's light position (px, py), both in the light
        /// list's screen coordinates: exact for tile offsets at equal Z, fractional in between (smooth while walking).</summary>
        public static float TileDistance(int x, int y, int px, int py) => (Math.Abs(x - px) + Math.Abs(y - py)) / TilePixels;

        /// <summary>The automatic range: the corner of the zoom-1 view (<paramref name="viewWidth"/> x <paramref name="viewHeight"/>
        /// screen pixels, the player at its centre) plus the largest halo's reach (<paramref name="lightMargin"/> pixels,
        /// ViewMargin.LightMargin), in whole tiles, plus the fade.</summary>
        public static int AutoRange(int viewWidth, int viewHeight, int lightMargin) =>
            (int)MathF.Ceiling((Math.Max(viewWidth, 0) * 0.5f + Math.Max(viewHeight, 0) * 0.5f) / TilePixels)
            + (int)MathF.Ceiling(Math.Max(lightMargin, 0) / TilePixels) + FadeTiles;

        /// <summary>The range in tiles: the setting when positive, else <paramref name="auto"/>.</summary>
        public static int Range(int setting, int auto) => setting > 0 ? setting : auto;

        /// <summary>The lights kept: the setting when positive (at most FrameLights.SafetyBound), else <see cref="DefaultBudget"/>.</summary>
        public static int Budget(int setting) => setting > 0 ? Math.Min(setting, FrameLights.SafetyBound) : DefaultBudget;

        /// <summary>A light at <paramref name="distance"/> tiles is dropped at or past the range.</summary>
        public static bool InRange(float distance, int range) => distance < range;

        /// <summary>The share of a light left at <paramref name="distance"/> tiles: 1 up to <see cref="FadeTiles"/> before the
        /// range, then linear to 0 at the range.</summary>
        public static float RangeShare(float distance, int range) => Math.Clamp((range - distance) / FadeTiles, 0f, 1f);

        /// <summary>The share of the kept light <paramref name="rankFromWorst"/> places above the lowest (0 = the lowest) when the
        /// budget binds and <paramref name="ranks"/> fade: (rank + 1) / ranks, 1 above them.</summary>
        public static float BudgetShare(int rankFromWorst, int ranks) =>
            rankFromWorst < 0 || rankFromWorst >= ranks ? 1f : (rankFromWorst + 1) / (float)ranks;
    }
}
