// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// clip-2: the world's depth model. Off (default): one depth key per sprite, (x + y) + (127 + PriorityZ) / 100, with the
    /// mobile strips' + 1 + 2i; the strip under the anchor (+ 3) beats walls up to two diagonals ahead (feet through walls,
    /// Felucca 1453 1668). On: per-pixel geometric depth by shape class (Uo.Render.GeoDepth): walls, boxes, land planes and the
    /// mobile composite (body, mount and equipment around one anchor). Gpu only so far; Legacy keeps the keys. Switch
    /// <c>depth_model</c>; <c>CUO_DEPTH_MODEL=1|0</c> overrides it in developer builds. The comparison so far is visual (the
    /// clip scenes' shots with the switch off and on); a per-pair checker (CUO_CLIP_ORDER_CHECK) is the next step.
    /// </summary>
    internal static class DepthModel
    {
        /// <summary>Tests: the switch (null: the setting).</summary>
        internal static bool? OverrideForTests;

#if CUO_DEVTOOLS
        private static readonly bool? s_env = Environment.GetEnvironmentVariable("CUO_DEPTH_MODEL") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
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
                return Settings.GlobalSettings != null && Settings.GlobalSettings.DepthModel;
            }
        }
    }
}
