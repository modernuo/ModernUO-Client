// SPDX-License-Identifier: BSD-2-Clause

namespace ClassicUO.Game
{
    /// <summary>
    /// anim_freeze (developer builds only): idle and action animations of mobiles and animated statics hold their current
    /// frame, so screenshot comparisons can tell a sprite that was not drawn from one that animated (shotcmp masks pixels
    /// that change inside a shot set). Mobiles still step and walk. Only frame selection stops: no packets, no world state,
    /// no ECS writes. CUO_ANIM_FREEZE=1 at startup, DevBench set:anim_freeze=1|0 live. Correctness and shot runs only: never
    /// in timing runs (FrameStats windows record "anim_freeze" so such a run can be rejected).
    /// </summary>
    internal static class AnimFreeze
    {
#if CUO_DEVTOOLS
        public static bool On = System.Environment.GetEnvironmentVariable("CUO_ANIM_FREEZE") == "1";
#else
        public const bool On = false;
#endif
    }
}
