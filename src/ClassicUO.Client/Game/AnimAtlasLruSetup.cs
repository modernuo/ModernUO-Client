// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Renderer.Animations;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game
{
    /// <summary>
    /// anim_atlas_lru: builds the Legacy anim atlas LRU policy from the setting (and, in developer builds, the CUO_ANIM_LRU_*
    /// overrides) right after the atlas is created. Budget in MiB: starts at 256, floor 128, ceiling 1024 (4, 2 and 16
    /// 64 MiB pages).
    /// </summary>
    internal static class AnimAtlasLruSetup
    {
        public const int DefaultBudgetMb = 256, DefaultFloorMb = 128, DefaultCeilingMb = 1024;

        /// <summary>Tests: the switch (null: the setting and the override).</summary>
        internal static bool? OnOverride;

        public static bool On
        {
            get
            {
                if (OnOverride.HasValue)
                {
                    return OnOverride.Value;
                }
#if CUO_DEVTOOLS
                // CLEANUP(release): the gate's override of the setting
                switch (Environment.GetEnvironmentVariable("CUO_ANIM_LRU"))
                {
                    case "1": return true;
                    case "0": return false;
                }
#endif
                return Configuration.Settings.GlobalSettings?.AnimAtlasLru ?? false;
            }
        }

        /// <summary>The policy for pages of <paramref name="pageBytes"/>, or null when the switch is off.</summary>
        public static AnimAtlasLruPolicy Create(bool on, long pageBytes)
        {
            if (!on)
            {
                return null;
            }

            int budgetMb = DefaultBudgetMb, floorMb = DefaultFloorMb, ceilingMb = DefaultCeilingMb;
            long minAgeS = 45, thrashS = 60, shrinkS = 180, shrinkUnfocusedS = 45;
#if CUO_DEVTOOLS
            // CLEANUP(release): sizes and times for the gates (a small budget so eviction happens in a short scene)
            budgetMb = Env("CUO_ANIM_LRU_BUDGET_MB", budgetMb);
            floorMb = Env("CUO_ANIM_LRU_FLOOR_MB", Math.Min(floorMb, budgetMb));
            ceilingMb = Env("CUO_ANIM_LRU_CEILING_MB", Math.Max(ceilingMb, budgetMb));
            minAgeS = Env("CUO_ANIM_LRU_MIN_AGE_S", (int)minAgeS);
            thrashS = Env("CUO_ANIM_LRU_THRASH_S", (int)thrashS);
            shrinkS = Env("CUO_ANIM_LRU_SHRINK_S", (int)shrinkS);
            shrinkUnfocusedS = Env("CUO_ANIM_LRU_SHRINK_UNFOCUSED_S", (int)shrinkUnfocusedS);
#endif
            var policy = new AnimAtlasLruPolicy(Pages(budgetMb, pageBytes), Pages(floorMb, pageBytes), Pages(ceilingMb, pageBytes))
            {
                MinAgeMs = minAgeS * 1000,
                ThrashWindowMs = thrashS * 1000,
                ShrinkQuietMs = shrinkS * 1000,
                ShrinkQuietUnfocusedMs = shrinkUnfocusedS * 1000,
                MemoryAllowsGrowth = MemoryAllowsGrowth
            };

            return policy;
        }

        public static int Pages(int mb, long pageBytes) => (int)Math.Max(1, ((long)mb << 20) / pageBytes);

        /// <summary>Called once at startup, after the atlas is created.</summary>
        public static void Enable(Animations animations)
        {
            bool on = On;
            var policy = Create(on, animations.AtlasPageBytes);

            if (policy != null)
            {
                animations.EnableLru(policy);
            }

            Log.Info(policy == null
                ? "[anim-lru] anim_atlas_lru=off"
                : $"[anim-lru] anim_atlas_lru=on: budget {policy.BudgetPages} pages, floor {policy.FloorPages}, ceiling {policy.CeilingPages}, " +
                  $"page {animations.AtlasPageBytes >> 20} MiB, min age {policy.MinAgeMs / 1000} s, thrash window {policy.ThrashWindowMs / 1000} s");
        }

        // a thrash reload grows the budget only while the GC sees memory headroom: the load below its high-memory threshold
        // and at least 1 GiB available (allocates a GCMemoryInfo: only asked on a thrash reload that could grow)
        private static bool MemoryAllowsGrowth()
        {
            var info = GC.GetGCMemoryInfo();

            return info.MemoryLoadBytes < info.HighMemoryLoadThresholdBytes && info.TotalAvailableMemoryBytes - info.MemoryLoadBytes > 1L << 30;
        }

#if CUO_DEVTOOLS
        private static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;
#endif
    }
}
