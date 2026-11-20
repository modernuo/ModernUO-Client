// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Animations;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// atlas_lru: page LRUs for the Art, Gump, Texmap, Light and font glyph atlases, with the anim atlas's policy
    /// (<see cref="AtlasLruPolicy"/>, anim_atlas_lru) and one budget per atlas (MiB: budget, floor, ceiling). Read once at
    /// startup; in developer builds CUO_ATLAS_LRU=1|0 overrides the setting, CUO_ATLAS_LRU_BUDGET_MB / _FLOOR_MB /
    /// _CEILING_MB / _MIN_AGE_S set every atlas (forced-small gates, with CUO_ATLAS_PAGE for small pages) and
    /// CUO_ATLAS_LRU_CHECK=1 checks reloads and draws.
    /// </summary>
    internal static class AtlasLruSetup
    {
        // art and gumps: 4096^2 pages (64 MiB); texmaps, lights and glyphs: 2048^2 (16 MiB)
        public static readonly (string Name, int BudgetMb, int FloorMb, int CeilingMb)[] Defaults =
        {
            ("art", 256, 128, 1024),
            ("gump", 192, 128, 768),
            ("texmap", 64, 32, 256),
            ("light", 32, 16, 64),
            ("font", 32, 16, 128),
        };

        /// <summary>Tests: the switch (null: the setting and the override).</summary>
        internal static bool? OnOverride;

        private static bool _enabled;
        private static long _checkLogAt;

        public static bool On
        {
            get
            {
                if (OnOverride.HasValue)
                {
                    return OnOverride.Value;
                }
#if CUO_DEVTOOLS
                switch (Environment.GetEnvironmentVariable("CUO_ATLAS_LRU"))
                {
                    case "1": return true;
                    case "0": return false;
                }
#endif
                return Configuration.Settings.GlobalSettings?.AtlasLru ?? false;
            }
        }

        /// <summary>The policy of atlas <paramref name="name"/> for pages of <paramref name="pageBytes"/>, or null when the
        /// switch is off.</summary>
        public static AtlasLruPolicy Create(bool on, string name, long pageBytes)
        {
            if (!on)
            {
                return null;
            }

            int i = Array.FindIndex(Defaults, d => d.Name == name);
            var (_, budgetMb, floorMb, ceilingMb) = Defaults[i];
            long minAgeS = 45, thrashS = 60, shrinkS = 180, shrinkUnfocusedS = 45;
#if CUO_DEVTOOLS
            budgetMb = Env("CUO_ATLAS_LRU_BUDGET_MB", budgetMb);
            floorMb = Env("CUO_ATLAS_LRU_FLOOR_MB", Math.Min(floorMb, budgetMb));
            ceilingMb = Env("CUO_ATLAS_LRU_CEILING_MB", Math.Max(ceilingMb, budgetMb));
            minAgeS = Env("CUO_ATLAS_LRU_MIN_AGE_S", (int)minAgeS);
#endif
            return new AtlasLruPolicy(
                AnimAtlasLruSetup.Pages(budgetMb, pageBytes),
                AnimAtlasLruSetup.Pages(floorMb, pageBytes),
                AnimAtlasLruSetup.Pages(ceilingMb, pageBytes))
            {
                MinAgeMs = minAgeS * 1000,
                ThrashWindowMs = thrashS * 1000,
                ShrinkQuietMs = shrinkS * 1000,
                ShrinkQuietUnfocusedMs = shrinkUnfocusedS * 1000,
                MemoryAllowsGrowth = AnimAtlasLruSetup.MemoryAllowsGrowth
            };
        }

        /// <summary>Called once at startup, after the atlases are created and before any sprite loads.</summary>
        public static void Enable(UltimaOnline uo)
        {
            bool on = On;
            _enabled = on;

            if (!on)
            {
                Log.Info("[atlas-lru] atlas_lru=off");

                return;
            }

            uo.Arts.EnableLru(Create(true, "art", uo.Arts.AtlasPageBytes));
            uo.Gumps.EnableLru(Create(true, "gump", uo.Gumps.AtlasPageBytes));
            uo.Texmaps.EnableLru(Create(true, "texmap", uo.Texmaps.AtlasPageBytes));
            uo.Lights.EnableLru(Create(true, "light", uo.Lights.AtlasPageBytes));
            uo.FontGlyphAtlas.EnableLru(Create(true, "font", uo.FontGlyphAtlas.AtlasPageBytes));

            Log.Info("[atlas-lru] atlas_lru=on: " + string.Join("; ", Array.ConvertAll(new[] { uo.Arts.Lru, uo.Gumps.Lru, uo.Texmaps.Lru, uo.Lights.Lru, uo.FontGlyphAtlas.Lru },
                l => $"{l.Name} budget {l.Policy.BudgetPages}, floor {l.Policy.FloorPages}, ceiling {l.Policy.CeilingPages}")) +
                $"; min age {uo.Arts.Lru.Policy.MinAgeMs / 1000} s");
        }

        /// <summary>Once per frame before anything is drawn (GameController.Update, after Animations.Tick).</summary>
        public static void Tick(UltimaOnline uo, long nowMs, bool unfocused)
        {
            if (!_enabled)
            {
                return;
            }

            uo.Arts?.TickLru(nowMs, unfocused);
            uo.Gumps?.TickLru(nowMs, unfocused);
            uo.Texmaps?.TickLru(nowMs, unfocused);
            uo.Lights?.TickLru(nowMs, unfocused);
            uo.FontGlyphAtlas?.TickLru(nowMs, unfocused);
#if CUO_DEVTOOLS
            if (AtlasPageLru.Check && nowMs >= _checkLogAt)
            {
                _checkLogAt = nowMs + 5000;
                Log.Info(AtlasPageLru.CheckSummary());
            }
#endif
        }

#if CUO_DEVTOOLS
        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;
#endif
    }
}
