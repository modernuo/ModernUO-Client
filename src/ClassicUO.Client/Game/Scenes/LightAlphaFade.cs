// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using Uo.Pack;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// light_alpha_fade (light-edges round 6b, default off). Owner edge case 3 (2026-11-22): under the circle of transparency
    /// the faded or hidden walls' lit windows kept their glow at full strength (a ring of floating arch-shaped glows over the
    /// floor), and light_occlusion's masks switched off at once where the fade crossed <see cref="LightOcclusion.MinOccluderAlpha"/>.
    /// With the switch on:
    /// (a) a surface glow (its light shape's class in the pack's lightclass.bin is <see cref="LightClass.Surface"/>; a shape the
    /// table does not list falls back to the emitter's tiledata Wall/Window flags) is drawn at its emitter's drawn alpha: the
    /// gradient circle and the roof / upper floor fade (AlphaHue, a multi piece's DrawnAlpha) times the shader circle's fade at
    /// the emitter's light point (<see cref="CotFade"/>). Radial glows (lamps, torches, sconces, braziers, candles) and
    /// mobiles' lights keep full light. Both light_occlusion states.
    /// (b) with light_occlusion on, an occluder's mask covers the fraction of its pixels its drawn alpha gives: the stencil write
    /// keeps a pixel where alpha beats a screen-space Bayer 4x4 threshold (IsometricWorld.fx DITHER_MASK, <see cref="DitherKeeps"/>),
    /// instead of the binary cut at 128.
    /// CUO_LIGHT_ALPHA_FADE_CHECK=1 runs the unfaded path next to the new one for every light and occluder and logs any
    /// difference the switch does not explain (a radial glow faded, a fade at full alpha, an occluder lost).
    /// </summary>
    internal static class LightAlphaFade
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_ALPHA_FADE") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_LIGHT_ALPHA_FADE_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightAlphaFade ?? false);

        /// <summary>IsometricWorld.fx: the hue mode that writes a mask pixel only where the alpha beats the Bayer threshold.</summary>
        public const float DitherMaskMode = 11f;

        /// <summary>A glow of class <paramref name="cls"/> fades with its emitter; an unlisted shape fades when its emitter is a
        /// wall or window by tiledata.</summary>
        public static bool IsSurface(LightClass cls, bool wallOrWindow) =>
            cls == LightClass.Surface || cls == LightClass.Unlisted && wallOrWindow;

        /// <summary>IsometricWorld.fx's circle of transparency at <paramref name="distance"/> target pixels from the centre:
        /// 0 inside 0.85 of the radius, t³ over the ring, 1 outside (or with no circle).</summary>
        public static float CotFade(float distance, float radius)
        {
            if (radius <= 0f)
            {
                return 1f;
            }

            float ratio = distance / radius;

            if (ratio < 0.85f)
            {
                return 0f;
            }

            if (ratio >= 1f)
            {
                return 1f;
            }

            float t = (ratio - 0.85f) / 0.15f;
            float a = t * t * t;

            // the shader discards below 0.02
            return a < 0.02f ? 0f : a;
        }

        /// <summary>The share of a surface glow left: the emitter's drawn alpha (0..255) times its circle fade.</summary>
        public static float Fade(byte alpha, float cot) => alpha / 255f * cot;

        /// <summary>The share of a glow the light pass draws: its fade with the switch on, all of it with the switch off.</summary>
        public static float Share(bool on, float fade) => on ? fade : 1f;

        /// <summary>The light's hue alpha for a fade: the additive blend (source alpha, one) takes the shader's output
        /// (colour x alpha, alpha x alpha), so the glow scales by the square of the hue alpha.</summary>
        public static float HueAlpha(float fade) => MathF.Sqrt(fade);

        /// <summary>The Bayer 4x4 rank (0..15) of the screen pixel (x, y), as IsometricWorld.fx computes it.</summary>
        public static int Bayer(int x, int y)
        {
            int lx = x & 1, ly = y & 1, hx = (x >> 1) & 1, hy = (y >> 1) & 1;

            return 4 * (2 * (lx ^ ly) + ly) + 2 * (hx ^ hy) + hy;
        }

        /// <summary>A mask pixel at (x, y) of an occluder drawn at <paramref name="alpha"/> (0..1) is written.</summary>
        public static bool DitherKeeps(float alpha, int x, int y) => alpha * 16f > Bayer(x, y) + 0.5f;

        /// <summary>The least drawn alpha at which an object masks a glow: any with the switch on (the mask is dithered),
        /// else the binary cut.</summary>
        public static byte MinMaskAlpha(bool on) => on ? (byte)1 : LightOcclusion.MinOccluderAlpha;
    }
}
