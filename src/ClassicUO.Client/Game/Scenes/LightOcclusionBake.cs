// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using Microsoft.Xna.Framework;

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

        /// <summary>The bake key's light term: the light (id, colour, hue flag, occluder count), its hue, the scale, the phase
        /// term (<see cref="PhaseKey"/>), the light art's rectangle and texture.</summary>
        public static ulong KeyLight(ulong light, Vector3 hue, float scale, ulong phase, Rectangle uv, int texture)
        {
            ulong h = 0xCBF29CE484222325UL;
            h = Mix(h, light);
            h = Mix(h, BitConverter.SingleToUInt32Bits(hue.X) | (ulong)BitConverter.SingleToUInt32Bits(hue.Y) << 32);
            h = Mix(h, BitConverter.SingleToUInt32Bits(hue.Z) | (ulong)BitConverter.SingleToUInt32Bits(scale) << 32);
            h = Mix(h, phase);
            h = Mix(h, PackRect(uv));

            return Mix(h, (uint)texture);
        }

        /// <summary>The bake key's term for one occluder: its art, texture, source rectangle and offset from the light (world
        /// pixels).</summary>
        public static ulong KeyOccluder(ulong h, uint art, int texture, Rectangle uv, int dx, int dy)
        {
            h = Mix(h, art | (ulong)(uint)texture << 32);
            h = Mix(h, PackRect(uv));

            return Mix(h, (uint)dx | (ulong)(uint)dy << 32);
        }

        private static ulong PackRect(Rectangle r) => (ulong)(ushort)r.X | (ulong)(ushort)r.Y << 16 | (ulong)(ushort)r.Width << 32 | (ulong)(ushort)r.Height << 48;

        /// <summary>light_occlusion_bake_world_phase's steps per pixel: the phase in the key is rounded to 1/256 pixel.</summary>
        public const int PhaseSteps = 256;

        /// <summary>
        /// light_occlusion_bake_world_phase: the sub-pixel phase of a light quad's corner at world pixel <paramref name="worldX"/>
        /// (its draw position plus the world draw offset), in 1/<see cref="PhaseSteps"/> pixel. The camera matrix puts the world's
        /// origin on a whole target pixel (Camera.SetWorldOffset), so a world pixel's phase depends only on the pixel and the
        /// scale: walking, peeking and resizing move the light by whole pixels. Computed in double from integers, it carries
        /// none of the float rounding the frame's matrix adds (the frame phase's low bits change with every draw offset at a
        /// scale like 0.4, which re-baked every glow each frame while walking).
        /// </summary>
        public static int WorldPhase(double worldX, float scale)
        {
            long q = (long)Math.Floor(worldX * scale * PhaseSteps + 0.5);

            return (int)(q & (PhaseSteps - 1));
        }

        /// <summary>light_occlusion_bake_world_phase: the whole target pixel left of (or above) a quad corner drawn at
        /// <paramref name="target"/> (the frame's matrix) whose world phase is <paramref name="phase"/>. On a snapped camera
        /// target - phase is a whole pixel up to float rounding; off the grid the slot goes to the nearest pixel (at most half a
        /// pixel away; CUO_LIGHT_OCC_BAKE_CHECK counts those).</summary>
        public static int PhasePixel(float target, int phase) => (int)Math.Floor(target - phase / (double)PhaseSteps + 0.5);

        /// <summary>How far, in pixels, the frame's quad corner is from the world phase placement (0 up to float rounding on a
        /// snapped camera).</summary>
        public static double PhaseResidual(float target, int pixel, int phase) => Math.Abs(target - pixel - phase / (double)PhaseSteps);

        /// <summary>The bake key's phase term: the world phase (rounded steps), or the frame phase's float bits.</summary>
        public static ulong PhaseKey(bool world, int phaseX, int phaseY, float frameX, float frameY) =>
            world
                ? (uint)phaseX | (ulong)(uint)phaseY << 32 | 1UL << 63
                : BitConverter.SingleToUInt32Bits(frameX) | (ulong)BitConverter.SingleToUInt32Bits(frameY) << 32;
    }

    /// <summary>
    /// light_occlusion_bake_world_phase (with light_occlusion_bake): a masked glow's bake key holds the sub-pixel phase taken
    /// from the light's world pixel (<see cref="LightOcclusionBake.WorldPhase"/>) instead of the frame matrix's float phase,
    /// so camera moves (walking at a fractional zoom) no longer re-bake it; the bake is still drawn at the light's own phase.
    /// CUO_LIGHT_OCC_BAKE_CHECK counts the re-bakes the frame phase key would have made, and slots placed off the camera's
    /// pixel grid.
    /// </summary>
    internal static class LightBakeWorldPhase
    {
#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_LIGHT_OCC_BAKE_WORLD_PHASE") switch
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

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.LightOcclusionBakeWorldPhase ?? false);
    }

    /// <summary>Events per second over the last whole window of at least a second (FrameStats lo_rebakes_s): no allocation,
    /// reported on every frame.</summary>
    internal struct LightBakeRate
    {
        private uint _start;
        private int _count;
        private bool _started;

        public int PerSecond { get; private set; }

        public void Add(int n, uint nowMs)
        {
            if (!_started)
            {
                _started = true;
                _start = nowMs;
            }

            uint dt = nowMs - _start;

            if (dt >= 1000)
            {
                PerSecond = (int)(_count * 1000L / dt);
                _count = 0;
                _start = nowMs;
            }

            _count += n;
        }
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
