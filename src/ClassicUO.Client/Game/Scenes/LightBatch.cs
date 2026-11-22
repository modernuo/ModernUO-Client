// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_batch (light-edges round 6c, default off). With light_occlusion on the light pass drew light by light, and every
    /// occluded light switched the stencil (write, then test) and back, which flushes the batch, so the unoccluded lights between
    /// two occluded ones went out in small draws. With the switch on the pass collects every light's occluders first (kept in
    /// one list for the frame), draws all lights with none in one batch without the stencil, then the occluded ones with their
    /// masks as before. Additive blending with no negative term is order-free (a saturating sum), so the light target is the
    /// same. Both renderers (one light pass).
    /// CUO_LIGHT_BATCH_CHECK=1: once a second the pass renders the other path first, reads the light target back, renders the
    /// active path and compares the two pixel by pixel (expected identical); it also counts the non-overlapping sets the
    /// occluded lights would form (<see cref="CountSets"/>, count only). CUO_LIGHT_BATCH=1|0 overrides the setting in developer
    /// builds.
    /// </summary>
    internal static class LightBatch
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_BATCH") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_BATCH_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightBatch ?? false);

        /// <summary>The batched path runs: the switch on and light_occlusion on (without occlusion no light switches the stencil
        /// and the per-light loop is one batch already).</summary>
        public static bool Batched(bool on, bool occlusion) => on && occlusion;

        /// <summary>Greedy first-fit grouping of the occluded lights' quads into sets whose members do not overlap (one stencil
        /// pass and one draw per set, the batching design's step 2): the number of sets. <paramref name="setOf"/> receives each
        /// quad's set. Count only (the checker's report); the pass does not draw by sets.</summary>
        public static int CountSets(ReadOnlySpan<Rectangle> quads, Span<int> setOf)
        {
            int sets = 0;

            for (int i = 0; i < quads.Length; i++)
            {
                int s = 0;

                for (int j = 0; j < i; j++)
                {
                    if (setOf[j] == s && quads[j].Intersects(quads[i]))
                    {
                        // try the next set from the start
                        s++;
                        j = -1;
                    }
                }

                setOf[i] = s;
                sets = Math.Max(sets, s + 1);
            }

            return sets;
        }

        /// <summary>The checker's comparison: pixels that differ and the largest channel difference.</summary>
        public static int Differ(ReadOnlySpan<Color> a, ReadOnlySpan<Color> b, out int maxChannel)
        {
            int n = Math.Min(a.Length, b.Length), diff = 0;
            maxChannel = 0;

            for (int i = 0; i < n; i++)
            {
                if (a[i].PackedValue == b[i].PackedValue)
                {
                    continue;
                }

                diff++;
                Color x = a[i], y = b[i];
                maxChannel = Math.Max(maxChannel, Math.Max(Math.Max(Math.Abs(x.R - y.R), Math.Abs(x.G - y.G)), Math.Max(Math.Abs(x.B - y.B), Math.Abs(x.A - y.A))));
            }

            return diff + Math.Abs(a.Length - b.Length);
        }
    }
}
