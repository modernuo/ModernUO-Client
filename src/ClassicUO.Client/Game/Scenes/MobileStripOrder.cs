// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// mobile_strip_order: a mobile's strip below the anchor (DepthOf kind 1, add + 2) out-keys the upper strip (+ 0) of a
    /// mobile one diagonal ahead, and ties with one two diagonals ahead, so a horse's hooves drew over a player standing one
    /// tile south of it (owner, 2026-11-23). With the switch on, the rows of that strip above <see cref="Band"/> rows under
    /// the composite anchor (the standing tile's bottom vertex: they cannot overlap anything two diagonals ahead) take
    /// <see cref="NearAdd"/>: over land and statics one diagonal ahead (keys up to + 0.5 + 0.03), under a mobile one diagonal
    /// ahead (+ 1). The rows further down keep + 2 (they reach the tiles two diagonals ahead, whose land needs it). Mid-step the
    /// depth tile is the later one, so the band stays above its bottom vertex too. No scan and no new pass: one row split in the
    /// extract, an extra instance only for a strip that crosses the band. mobile_wall_order caps and raises the near part as
    /// it does the strip. Switch <c>mobile_strip_order</c>; <c>CUO_MOBILE_STRIP_ORDER=1|0</c> overrides it and
    /// <c>CUO_MOBILE_STRIP_CHECK=1</c> compares every split with the old add (developer builds).
    /// </summary>
    internal static class MobileStripOrder
    {
        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

        /// <summary>The near rows' add: above land/statics one diagonal ahead (0.5 + up to 25 z), below a mobile one diagonal
        /// ahead (1, up to 25 z lower).</summary>
        public const float NearAdd = 0.75f;

        /// <summary>Rows under the composite anchor that stay on the standing tile's diamond: the anchor is 3 px above the tile
        /// centre (MobileSpriteExtract.Emit: posY - 3 + 22), the bottom vertex 22 px below it.</summary>
        public const int Band = 25;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_MOBILE_STRIP_ORDER") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        /// <summary>CUO_MOBILE_STRIP_CHECK=1: every split (with the switch off too) is compared with the old add.</summary>
        internal static readonly bool Check = Environment.GetEnvironmentVariable("CUO_MOBILE_STRIP_CHECK") == "1";
#else
        internal const bool Check = false;
#endif

        public static bool On
        {
            get
            {
                if (OverrideForTests.HasValue)
                {
                    return OverrideForTests.Value;
                }

#if CUO_DEVTOOLS
                if (s_env.HasValue)
                {
                    return s_env.Value;
                }
#endif
                return Settings.GlobalSettings != null && Settings.GlobalSettings.MobileStripOrder;
            }
        }

        /// <summary>The rows of a strip at screen rows [<paramref name="top"/>, top + <paramref name="h"/>) above the band of a
        /// composite anchored at <paramref name="anchorY"/> (0: none, <paramref name="h"/>: all).</summary>
        public static int NearRows(int top, int h, int anchorY) => Math.Clamp(anchorY + Band - top, 0, h);

#if CUO_DEVTOOLS
        internal static long Splits, NearRowsTotal, FarRowsTotal, Mismatches;
        private static long s_nextReport;

        /// <summary>CUO_MOBILE_STRIP_CHECK: the old add of the strip (<paramref name="oldAdd"/>, + 2 after mobile_wall_order)
        /// against the near rows' new one. The near rows must never draw later than before, and without a wall rule moving
        /// them they must sit between the land/statics one diagonal ahead and a mobile one diagonal ahead.</summary>
        internal static void Note(int x, int y, int z, ushort body, int nearRows, int farRows, float oldAdd, float nearAdd, bool wallMoved)
        {
            Splits++;
            NearRowsTotal += nearRows;
            FarRowsTotal += farRows;

            bool bad = nearAdd > oldAdd || !wallMoved && (nearAdd <= 0.5f || nearAdd >= 1f);

            if (bad)
            {
                Mismatches++;
                Log.Warn($"[mobile-strip-check] MISMATCH mobile body 0x{body:X4} at {x},{y},{z}: near rows {nearRows} add {nearAdd:0.00} (old {oldAdd:0.00}, wall moved {wallMoved}), far rows {farRows}");
            }

            long now = Environment.TickCount64;

            if (now >= s_nextReport)
            {
                s_nextReport = now + 5000;
                Log.Info($"[mobile-strip-check] summary switch={(On ? "on" : "off")} splits {Splits} near rows {NearRowsTotal} far rows {FarRowsTotal} mismatches {Mismatches}");
            }
        }
#endif
    }
}
