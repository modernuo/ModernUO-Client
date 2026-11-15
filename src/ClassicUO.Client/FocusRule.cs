// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO
{
    /// <summary>
    /// One rule for what the inactive and minimized throttles see: an automated session (DevBench, a -ecsdriver script, or
    /// CUO_IGNORE_FOCUS=1 in a dev build) behaves as focused and not minimized, because bench and test clients are never focused
    /// (several run at once). The simulated states (<c>inactive on</c>, <c>minimized on</c>) still force the throttled paths so the
    /// throttles stay testable. Used by ReduceFPSWhenInactive, throttle_update_when_inactive and skip_draw_when_minimized.
    /// In a release build no driver exists, so <see cref="Automation"/> is false and nothing changes.
    /// </summary>
    internal static class FocusRule
    {
#if CUO_DEVTOOLS
        // set by Main for -ecsdriver
        internal static bool DriverSession;

        // `inactive on|off` (driver and DevBench plan): the window counts as unfocused whatever its real state
        internal static bool ForceInactive;

        private static readonly bool s_env = Environment.GetEnvironmentVariable("CUO_IGNORE_FOCUS") == "1";

        public static bool Automation => DevBench.Enabled || DriverSession || s_env;
#else
        internal static bool ForceInactive => false;

        public static bool Automation => false;
#endif

        /// <summary>The inactive throttles apply: the forced state, or a really unfocused window outside automation.</summary>
        public static bool IsInactive(bool windowActive, bool automation, bool forceInactive) => forceInactive || (!windowActive && !automation);

        /// <summary>The window counts as minimized: the simulated state, or a really minimized window outside automation.</summary>
        public static bool IsMinimized(bool realMinimized, bool automation, bool forceMinimized) => forceMinimized || (realMinimized && !automation);
    }
}
