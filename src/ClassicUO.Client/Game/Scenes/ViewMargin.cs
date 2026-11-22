// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;

namespace ClassicUO.Game.Scenes
{
    /// <summary>
    /// view_margin (light-edges, default off): the world beyond the walked view that can still reach it. The walk culls each
    /// object by its anchor against the view padded by one tile (GetViewPort: _minPixel/_maxPixel), but a light's halo reaches
    /// up to half the largest light texture (175 px in the classic lights) past its source, so a wall torch just outside the
    /// view lit up only when it entered it. With the switch on, GameScene.AddChunkLights adds the light sources whose anchor is
    /// in the band between that rectangle and the rectangle grown by <see cref="LightMargin"/>: the chunks' recorded light
    /// statics and multi components (Chunk.LightSources) and their ground items, without walking those tiles.
    /// CUO_VIEW_MARGIN_CHECK=1 compares that set with a walk of every tile list and dynamic of the same chunks, every frame,
    /// in both states (off: what the margin would add). The pure geometry lives here for the unit tests.
    /// </summary>
    internal static class ViewMargin
    {
        // anchor y = S * 22 - 4 * Z - offY - 22 for Z in sbyte: -508..+512 px
        private const int Z_UP = 127 * 4, Z_DOWN = 128 * 4;

#if CUO_DEVTOOLS
        private static readonly bool? _env = Environment.GetEnvironmentVariable("CUO_VIEW_MARGIN") switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

        public static readonly bool Check = Environment.GetEnvironmentVariable("CUO_VIEW_MARGIN_CHECK") == "1";
#else
        private static readonly bool? _env = null;
        public const bool Check = false;
#endif

        /// <summary>Tests: the switch (null: the setting).</summary>
        [ThreadStatic] internal static bool? OverrideForTests;

        public static bool Enabled => OverrideForTests ?? _env ?? (Settings.GlobalSettings?.ViewMargin ?? false);

        /// <summary>The light margin for lights whose largest texture side is <paramref name="maxExtent"/> px: the halo's
        /// radius (the light is drawn centred on anchor + 22, and the walked rectangle already has 44 px of slack).</summary>
        public static int LightMargin(int maxExtent) => (maxExtent + 1) >> 1;

        /// <summary>The tile bands (D = x - y, S = x + y, inclusive, one tile of slack) whose anchors can fall in the
        /// rectangle (left, top, right, bottom) for some Z, at draw offset (offX, offY).</summary>
        public static void Bands(int left, int top, int right, int bottom, int offX, int offY, out int dLo, out int dHi, out int sLo, out int sHi)
        {
            dLo = FloorDiv22(left + offX + 22) - 1;
            dHi = FloorDiv22(right + offX + 22) + 1;
            sLo = FloorDiv22(top + offY + 22 - Z_DOWN) - 1;
            sHi = FloorDiv22(bottom + offY + 22 + Z_UP) + 1;
        }

        /// <summary>The chunk rectangle holding every tile of the bands (clamped at 0).</summary>
        public static void ChunkRange(int dLo, int dHi, int sLo, int sHi, out int cx0, out int cy0, out int cx1, out int cy1)
        {
            // x = (S + D) / 2, y = (S - D) / 2
            cx0 = Math.Max(0, (sLo + dLo) >> 1) >> 3;
            cx1 = Math.Max(0, (sHi + dHi + 1) >> 1) >> 3;
            cy0 = Math.Max(0, (sLo - dHi) >> 1) >> 3;
            cy1 = Math.Max(0, (sHi - dLo + 1) >> 1) >> 3;
        }

        /// <summary>Whether chunk (cx, cy) has a tile in the bands.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ChunkTouches(int cx, int cy, int dLo, int dHi, int sLo, int sHi)
        {
            int d0 = (cx - cy) * 8 - 7, s0 = (cx + cy) * 8;

            return d0 + 14 >= dLo && d0 <= dHi && s0 + 14 >= sLo && s0 <= sHi;
        }

        /// <summary>The anchor (ax, ay) is outside the walked rectangle (the walk's own) and inside it grown by
        /// <paramref name="margin"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InBand(int ax, int ay, int left, int top, int right, int bottom, int margin)
        {
            if (ax >= left && ax <= right && ay >= top && ay <= bottom)
            {
                return false;
            }

            return ax >= left - margin && ax <= right + margin && ay >= top - margin && ay <= bottom + margin;
        }

        /// <summary>A light drawn centred on (x, y) with a <paramref name="width"/> x <paramref name="height"/> texture touches
        /// the view (left, top, right, bottom), edges included.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool LightReaches(int x, int y, int width, int height, int left, int top, int right, int bottom)
        {
            int hw = (width + 1) >> 1, hh = (height + 1) >> 1;

            return width > 0 && height > 0 && x + hw >= left && x - hw <= right && y + hh >= top && y - hh <= bottom;
        }

        /// <summary>An object's anchor (GameObject.RealScreenPosition) at draw offset (offX, offY).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Anchor(int x, int y, int z, int offX, int offY, out int ax, out int ay)
        {
            ax = (x - y) * 22 - offX - 22;
            ay = (x + y) * 22 - (z << 2) - offY - 22;
        }

        // floor(v / 22) for any sign
        private static int FloorDiv22(int v) => v >= 0 ? v / 22 : -((-v + 21) / 22);

        /// <summary>CUO_VIEW_MARGIN_CHECK: an order-independent hash of one candidate.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Mix(int x, int y, int z, ushort graphic, bool item)
        {
            uint h = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)(z + 128) * 83492791u ^ (uint)graphic * 2654435761u ^ (item ? 0x9E3779B9u : 0u);
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;

            return h;
        }
    }
}
