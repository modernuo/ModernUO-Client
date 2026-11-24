// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game
{
    /// <summary>
    /// idle_on_spawn (mount-fix, owner 2026-11-23). On (the default, upstream behaviour): a mobile record the mirror creates
    /// starts with AnimationTimer.IdleDue 0, so the idle plan (MobileAnimationAdvanceSystem.PlanIdle) plays its client-side
    /// idle on its first update: a dismounted horse fidgets at once, and a crowd entering view fidgets in sync. Off: the
    /// record starts with <see cref="GameObjects.Mobile.FirstIdleDue"/> (the steady-state residual of the 30-60 s idle
    /// delay: 0-60 s ahead, mean 23.3 s, one draw per mobile, so staggered at the settled rate), set in WorldEcs.InitCreatedMobile. Idles after the first are unchanged, so the steady idle rate is the same.
    /// Unit tests (WorldWriterMobileTests.IdleOnSpawn) cover both states.
    /// </summary>
    internal static class IdleOnSpawn
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_IDLE_ON_SPAWN") switch
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

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.IdleOnSpawn ?? false);
    }
}
