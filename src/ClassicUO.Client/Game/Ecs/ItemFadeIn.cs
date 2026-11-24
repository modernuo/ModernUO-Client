// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game
{
    /// <summary>
    /// item_fade_in (mount-fix, owner 2026-11-23, second option beside <see cref="MobileFadeIn"/>). On (the default, today): an
    /// item created by a packet starts at AlphaHue 0 and fades in. Off: World.GetOrCreateItem marks the new item
    /// (<see cref="GameObjects.GameObject.AlphaSnap"/>) and its first walk sets its alpha to the target at once
    /// (GameScene.ProcessAlpha). Items found again (picked up and dropped, moved) are not new and behave as before. The
    /// snap is consumed by the first draw walk, so an item created inside a container snaps when it first reaches the
    /// ground. Both renderers read the same AlphaHue. Unit tests: WorldWriterItemTests.ItemFadeIn.
    /// </summary>
    internal static class ItemFadeIn
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_ITEM_FADE_IN") switch
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

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.ItemFadeIn ?? false);
    }
}
