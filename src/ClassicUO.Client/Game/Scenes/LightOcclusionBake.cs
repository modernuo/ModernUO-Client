// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_occlusion_bake (with light_occlusion, light_occlusion_cache and light_batch): a masked surface glow is drawn once,
    /// with its occluders cut out by the stencil as the light pass draws it, into a slot of an atlas render target, at the
    /// light target's scale and sub-pixel phase; later frames draw the slot as one plain quad (blend One/One, point sampled, all
    /// baked lights in one batch), so a static scene switches no stencil or blend state per light. The bake stores the light's
    /// additive contribution (colour times alpha) as the light target's 8-bit blend would add it, so the sum is the same up to
    /// rounding ties. A slot is used while its key (light id, colour, hue alpha, scale, phase, and every occluder's art,
    /// texture, source rectangle and offset from the light) is unchanged. Not baked: masks dithered by light_alpha_fade (screen
    /// pattern) and occluders the circle of transparency reaches (screen circle); those lights take the stencil path. At most
    /// <see cref="BakesPerPass"/> bakes per light pass (the rest take the stencil path that pass); a full atlas starts over.
    /// CUO_LIGHT_OCC_BAKE_CHECK=1 draws the light pass without bakes once a second, reads both targets back and logs every
    /// differing pixel count.
    /// </summary>
    internal static class LightOcclusionBake
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_OCCLUSION_BAKE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_OCC_BAKE_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightOcclusionBake ?? false);

        /// <summary>The bake runs: its switch with light_occlusion, light_occlusion_cache (the slot lives in the light's cache
        /// entry) and light_batch (the batched pass draws the slots in one batch), and without dithered masks.</summary>
        public static bool Active(bool on, bool occlusion, bool cache, bool batch, bool ditherMasks) =>
            on && occlusion && cache && batch && !ditherMasks;

        /// <summary>The atlas: 1024 x 1024 Color with a 24/8 depth-stencil buffer, 8 MB of GPU memory.</summary>
        public const int AtlasSize = 1024;

        public const int BakesPerPass = 24;

        /// <summary>A slot's size for a light quad of w x h world pixels at scale: the scaled size rounded up, plus one pixel
        /// for the sub-pixel phase and one of margin on each side.</summary>
        public static int SlotSize(int size, float scale) => (int)MathF.Ceiling(size * scale) + 3;

        public static ulong Mix(ulong h, ulong v) => (h ^ v) * 0x100000001B3UL + (h >> 29);
    }

    /// <summary>Shelf allocation of bake slots in the atlas; <see cref="Reset"/> frees every slot (a new generation).</summary>
    internal sealed class LightBakeShelf
    {
        private readonly int _w, _h;
        private int _x, _y, _rowH;

        public LightBakeShelf(int w, int h)
        {
            _w = w;
            _h = h;
        }

        public int Generation { get; private set; }

        public bool TryAlloc(int w, int h, out int x, out int y)
        {
            x = y = 0;

            if (w > _w || h > _h)
            {
                return false;
            }

            if (_x + w > _w)
            {
                _y += _rowH;
                _x = 0;
                _rowH = 0;
            }

            if (_y + h > _h)
            {
                return false;
            }

            x = _x;
            y = _y;
            _x += w;
            _rowH = Math.Max(_rowH, h);

            return true;
        }

        public void Reset()
        {
            _x = _y = _rowH = 0;
            Generation++;
        }
    }
}
